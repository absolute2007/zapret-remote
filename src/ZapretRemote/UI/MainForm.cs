using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using ZapretRemote.Models;
using ZapretRemote.Services;

namespace ZapretRemote.UI;

public sealed class MainForm : Form
{
    private enum ShareTab
    {
        HappVless,
        HappRouting,
        Telegram
    }

    private readonly AppSettings _settings;
    private readonly XrayProcessManager _xrayManager;

    private readonly Icon _inactiveIcon;
    private readonly Icon _activeIcon;

    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _trayToggleItem;
    private readonly ToolStripMenuItem _trayCopyHappItem;
    private readonly ToolStripMenuItem _trayCopyTgItem;

    // Left column: Controls & Diagnostics
    private readonly Button _toggleButton;
    private readonly Panel _proxyStatusDot;
    private readonly Label _proxyStatusLabel;
    private readonly Button _openLogButton;

    private readonly Panel _zapretDot;
    private readonly Label _zapretLabel;

    private readonly Panel _tgDot;
    private readonly Label _tgLabel;

    private readonly Panel _firewallDot;
    private readonly Label _firewallLabel;
    private readonly Button _firewallActionButton;
    private readonly Button _zapretDoctorButton;

    private readonly ComboBox _adapterCombo;
    private readonly Button _refreshAdaptersButton;
    private readonly NumericUpDown _happPortInput;
    private readonly NumericUpDown _tgPortInput;
    private readonly CheckBox _blockQuicCheck;
    private readonly CheckBox _enableTgBridgeCheck;
    private readonly CheckBox _verboseLogCheck;

    // Right column: Large QR & Share
    private readonly Button _happTabButton;
    private readonly Button _happRoutingTabButton;
    private readonly Button _tgTabButton;
    private readonly Panel _qrFramePanel;
    private readonly PictureBox _qrPictureBox;
    private readonly Label _qrPlaceholderLabel;
    private readonly Label _qrZoomHintLabel;
    private readonly Label _shareHintLabel;
    private readonly TextBox _uriTextBox;
    private readonly Button _copyPrimaryButton;
    private readonly Button _secondaryActionButton;

    private readonly System.Windows.Forms.Timer _statusTimer;
    private readonly System.Windows.Forms.Timer _copyResetTimer;
    private readonly System.Windows.Forms.Timer _portDebounceTimer;

    private CancellationTokenSource? _startCts;
    private LogViewerDialog? _logDialog;
    private ZapretDoctorDialog? _doctorDialog;
    private ShareTab _activeTab = ShareTab.HappVless;
    private TgWsProxyStatus _lastTgStatus;
    private FirewallRuleStatus _lastFirewallStatus;
    private string? _currentQrPayload;
    private string? _currentQrTitle;

    private bool _isTransitioning;
    private bool _isExiting;
    private bool _trayHintShown;
    private bool _suppressSettingEvents;

    private Color _proxyDotColor = Theme.TextSecondary;
    private Color _zapretDotColor = Theme.TextSecondary;
    private Color _tgDotColor = Theme.TextSecondary;
    private Color _firewallDotColor = Theme.TextSecondary;

    public MainForm(AppSettings settings, XrayProcessManager xrayManager)
    {
        _settings = settings;
        _xrayManager = xrayManager;
        _lastTgStatus = TgWsProxyService.GetStatus();

        _inactiveIcon = IconFactory.CreateStatusIcon(active: false);
        _activeIcon = IconFactory.CreateStatusIcon(active: true);

        Text = "Zapret Remote";
        Icon = _inactiveIcon;
        BackColor = Theme.Background;
        ForeColor = Theme.TextPrimary;
        Font = Theme.BodyFont;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(920, 650);

        EnableDarkTitleBar(Handle);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Background
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44f));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56f));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        Controls.Add(root);

        // ==================== LEFT COLUMN ====================
        var leftColumn = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = new Padding(0, 0, 8, 0),
            BackColor = Theme.Background
        };
        leftColumn.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        leftColumn.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        leftColumn.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        leftColumn.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.Controls.Add(leftColumn, 0, 0);

        // Card 1: Control
        var controlCard = CreateCard(new Padding(14));
        var controlLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 4
        };
        controlLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        var titleLabel = new Label
        {
            Text = "Zapret Remote",
            Font = Theme.TitleFont,
            ForeColor = Theme.TextPrimary,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 2)
        };
        var subtitleLabel = new Label
        {
            Text = "LAN-мост для Happ (VLESS) и Telegram (TgWsProxy)",
            Font = Theme.SmallFont,
            ForeColor = Theme.TextSecondary,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 12)
        };

        _toggleButton = CreateButton("Включить прокси-мост", Theme.AccentGreen, Theme.AccentGreenHover, height: 48);
        _toggleButton.Font = Theme.HeroButtonFont;
        _toggleButton.Margin = new Padding(0, 0, 0, 10);
        _toggleButton.Click += async (_, _) => await ToggleProxyAsync();

        var proxyFooterRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Margin = new Padding(0)
        };
        proxyFooterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        proxyFooterRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var proxyStatusInfo = CreateDotRow(out _proxyStatusDot, out _proxyStatusLabel, "Остановлен", () => _proxyDotColor);
        proxyStatusInfo.Anchor = AnchorStyles.Left;
        proxyStatusInfo.Margin = new Padding(0, 3, 6, 0);

        _openLogButton = CreateButton("Лог Xray", Theme.ButtonNeutral, Theme.ButtonNeutralHover, height: 24);
        _openLogButton.Dock = DockStyle.None;
        _openLogButton.Width = 78;
        _openLogButton.Font = Theme.SmallFont;
        _openLogButton.Margin = new Padding(0);
        _openLogButton.Click += (_, _) => OpenLogViewer();

        proxyFooterRow.Controls.Add(proxyStatusInfo, 0, 0);
        proxyFooterRow.Controls.Add(_openLogButton, 1, 0);

        controlLayout.Controls.Add(titleLabel);
        controlLayout.Controls.Add(subtitleLabel);
        controlLayout.Controls.Add(_toggleButton);
        controlLayout.Controls.Add(proxyFooterRow);
        controlCard.Controls.Add(controlLayout);
        leftColumn.Controls.Add(controlCard, 0, 0);

        // Card 2: Diagnostics
        var diagCard = CreateCard(new Padding(14, 12, 14, 12));
        diagCard.Margin = new Padding(0, 10, 0, 0);
        var diagLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 5
        };
        diagLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        var diagHeader = new Label
        {
            Text = "Состояние на ПК",
            Font = Theme.SectionFont,
            ForeColor = Theme.TextPrimary,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8)
        };

        var zapretRow = CreateDotRow(out _zapretDot, out _zapretLabel, "zapret (winws.exe): проверка...", () => _zapretDotColor);
        zapretRow.Margin = new Padding(0, 0, 0, 6);

        var tgRow = CreateDotRow(out _tgDot, out _tgLabel, "TgWsProxy: проверка...", () => _tgDotColor);
        tgRow.Margin = new Padding(0, 0, 0, 6);

        var fwRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Margin = new Padding(0, 0, 0, 6)
        };
        fwRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        fwRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var fwInfo = CreateDotRow(out _firewallDot, out _firewallLabel, "Брандмауэр: проверка...", () => _firewallDotColor);
        fwInfo.Anchor = AnchorStyles.Left;
        fwInfo.Margin = new Padding(0, 3, 6, 0);

        _firewallActionButton = CreateButton("Разрешить", Theme.AccentBlue, Color.FromArgb(78, 155, 255), height: 26);
        _firewallActionButton.Dock = DockStyle.None;
        _firewallActionButton.Width = 92;
        _firewallActionButton.Font = Theme.SmallFont;
        _firewallActionButton.Margin = new Padding(0);
        _firewallActionButton.Click += (_, _) => OnFirewallActionClick();

        fwRow.Controls.Add(fwInfo, 0, 0);
        fwRow.Controls.Add(_firewallActionButton, 1, 0);

        var doctorRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Margin = new Padding(0)
        };
        doctorRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        doctorRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var doctorLabel = new Label
        {
            Text = "YouTube и автоподбор zapret (с бэкапом):",
            Font = Theme.SmallFont,
            ForeColor = Theme.TextSecondary,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 5, 6, 0)
        };

        _zapretDoctorButton = CreateButton("Диагностика", Theme.ButtonNeutral, Theme.ButtonNeutralHover, height: 26);
        _zapretDoctorButton.Dock = DockStyle.None;
        _zapretDoctorButton.Width = 92;
        _zapretDoctorButton.Font = Theme.SmallFont;
        _zapretDoctorButton.Margin = new Padding(0);
        _zapretDoctorButton.Click += (_, _) => OpenZapretDoctor();

        doctorRow.Controls.Add(doctorLabel, 0, 0);
        doctorRow.Controls.Add(_zapretDoctorButton, 1, 0);

        diagLayout.Controls.Add(diagHeader);
        diagLayout.Controls.Add(zapretRow);
        diagLayout.Controls.Add(tgRow);
        diagLayout.Controls.Add(fwRow);
        diagLayout.Controls.Add(doctorRow);
        diagCard.Controls.Add(diagLayout);
        leftColumn.Controls.Add(diagCard, 0, 1);

        // Card 3: Network & Bypass Settings
        var netCard = CreateCard(new Padding(14, 12, 14, 12));
        netCard.Margin = new Padding(0, 10, 0, 0);
        netCard.Dock = DockStyle.Fill;

        var netLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6
        };
        netLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        var netHeader = new Label
        {
            Text = "Сеть и параметры",
            Font = Theme.SectionFont,
            ForeColor = Theme.TextPrimary,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8)
        };

        var adapterRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Margin = new Padding(0, 0, 0, 10)
        };
        adapterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 74f));
        adapterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26f));

        _adapterCombo = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Theme.InputBackground,
            ForeColor = Theme.TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.BodyFont,
            Margin = new Padding(0, 0, 6, 0)
        };
        _adapterCombo.SelectedIndexChanged += (_, _) => OnAdapterChanged();

        _refreshAdaptersButton = CreateButton("Обновить", Theme.ButtonNeutral, Theme.ButtonNeutralHover, height: 26);
        _refreshAdaptersButton.Font = Theme.SmallFont;
        _refreshAdaptersButton.Margin = new Padding(0);
        _refreshAdaptersButton.Click += (_, _) => RefreshNetworkAdapters();

        adapterRow.Controls.Add(_adapterCombo, 0, 0);
        adapterRow.Controls.Add(_refreshAdaptersButton, 1, 0);

        var portsGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 4,
            Margin = new Padding(0, 0, 0, 12)
        };
        portsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        portsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        portsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        portsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));

        var happPortLabel = CreateSmallLabel("Порт Happ:");
        _happPortInput = CreatePortInput(_settings.Port);
        _happPortInput.Margin = new Padding(0, 0, 10, 0);
        _happPortInput.ValueChanged += (_, _) => OnPortEdited();

        var tgPortLabel = CreateSmallLabel("Порт TG:");
        _tgPortInput = CreatePortInput(_settings.TgBridgePort);
        _tgPortInput.Margin = new Padding(0);
        _tgPortInput.ValueChanged += (_, _) => OnPortEdited();

        portsGrid.Controls.Add(happPortLabel, 0, 0);
        portsGrid.Controls.Add(_happPortInput, 1, 0);
        portsGrid.Controls.Add(tgPortLabel, 2, 0);
        portsGrid.Controls.Add(_tgPortInput, 3, 0);

        _blockQuicCheck = CreateCheckBox("Блокировать QUIC (UDP 443) для YouTube", _settings.BlockQuic);
        _blockQuicCheck.CheckedChanged += async (_, _) => await OnToggleOptionChangedAsync();

        _enableTgBridgeCheck = CreateCheckBox("Проксировать TgWsProxy в локальную сеть", _settings.EnableTgBridge);
        _enableTgBridgeCheck.CheckedChanged += async (_, _) => await OnToggleOptionChangedAsync();

        _verboseLogCheck = CreateCheckBox("Подробный лог Xray (info)", _settings.VerboseLogging);
        _verboseLogCheck.Margin = new Padding(0);
        _verboseLogCheck.CheckedChanged += async (_, _) => await OnToggleOptionChangedAsync();

        netLayout.Controls.Add(netHeader);
        netLayout.Controls.Add(adapterRow);
        netLayout.Controls.Add(portsGrid);
        netLayout.Controls.Add(_blockQuicCheck);
        netLayout.Controls.Add(_enableTgBridgeCheck);
        netLayout.Controls.Add(_verboseLogCheck);
        netCard.Controls.Add(netLayout);
        leftColumn.Controls.Add(netCard, 0, 2);

        // ==================== RIGHT COLUMN ====================
        var rightCard = CreateCard(new Padding(14));
        rightCard.Dock = DockStyle.Fill;
        rightCard.Margin = new Padding(8, 0, 0, 0);
        root.Controls.Add(rightCard, 1, 0);

        var rightLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6
        };
        rightLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        rightLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rightLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        rightLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rightLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rightLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rightLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rightCard.Controls.Add(rightLayout);

        // 3 Valid-URL Tabs
        var tabRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 3,
            Margin = new Padding(0, 0, 0, 10)
        };
        tabRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34f));
        tabRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33f));
        tabRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33f));

        _happTabButton = CreateTabButton("1. Happ (VLESS)", () => SwitchTab(ShareTab.HappVless));
        _happTabButton.Margin = new Padding(0, 0, 4, 0);

        _happRoutingTabButton = CreateTabButton("2. Маршрут Happ", () => SwitchTab(ShareTab.HappRouting));
        _happRoutingTabButton.Margin = new Padding(2, 0, 2, 0);

        _tgTabButton = CreateTabButton("Telegram (TgWsProxy)", () => SwitchTab(ShareTab.Telegram));
        _tgTabButton.Margin = new Padding(4, 0, 0, 0);

        tabRow.Controls.Add(_happTabButton, 0, 0);
        tabRow.Controls.Add(_happRoutingTabButton, 1, 0);
        tabRow.Controls.Add(_tgTabButton, 2, 0);

        // Large 364x364 QR Frame
        _qrFramePanel = new Panel
        {
            Size = new Size(370, 370),
            BackColor = Color.White,
            Padding = new Padding(8),
            Anchor = AnchorStyles.None,
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 2, 0, 4)
        };
        _qrPictureBox = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.White,
            Cursor = Cursors.Hand
        };
        _qrPlaceholderLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Theme.InputBackground,
            ForeColor = Theme.TextSecondary,
            Font = Theme.BodyFont,
            Visible = false,
            Padding = new Padding(20)
        };
        _qrPictureBox.Click += (_, _) => OpenZoomedQr();
        _qrFramePanel.Click += (_, _) => OpenZoomedQr();
        _qrFramePanel.Controls.Add(_qrPictureBox);
        _qrFramePanel.Controls.Add(_qrPlaceholderLabel);

        _qrZoomHintLabel = new Label
        {
            Text = "Нажмите на QR-код для увеличения (520x520)",
            Font = Theme.SmallFont,
            ForeColor = Theme.TextSecondary,
            AutoSize = true,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 0, 0, 8)
        };

        _shareHintLabel = new Label
        {
            Text = string.Empty,
            Font = Theme.SmallFont,
            ForeColor = Theme.TextSecondary,
            AutoSize = true,
            MaximumSize = new Size(465, 0),
            Margin = new Padding(0, 0, 0, 8)
        };

        _uriTextBox = new TextBox
        {
            ReadOnly = true,
            BackColor = Theme.InputBackground,
            ForeColor = Theme.TextSecondary,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.MonoFont,
            Dock = DockStyle.Top,
            Margin = new Padding(0, 0, 0, 8)
        };

        var shareButtons = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Margin = new Padding(0)
        };
        shareButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68f));
        shareButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32f));

        _copyPrimaryButton = CreateButton("Скопировать", Theme.ButtonNeutral, Theme.ButtonNeutralHover, height: 34);
        _copyPrimaryButton.Margin = new Padding(0, 0, 6, 0);
        _copyPrimaryButton.Click += (_, _) => CopyCurrentUri();

        _secondaryActionButton = CreateButton("Новый UUID", Theme.ButtonNeutral, Theme.ButtonNeutralHover, height: 34);
        _secondaryActionButton.Margin = new Padding(0);
        _secondaryActionButton.Click += async (_, _) => await OnSecondaryShareActionAsync();

        shareButtons.Controls.Add(_copyPrimaryButton, 0, 0);
        shareButtons.Controls.Add(_secondaryActionButton, 1, 0);

        rightLayout.Controls.Add(tabRow, 0, 0);
        rightLayout.Controls.Add(_qrFramePanel, 0, 1);
        rightLayout.Controls.Add(_qrZoomHintLabel, 0, 2);
        rightLayout.Controls.Add(_shareHintLabel, 0, 3);
        rightLayout.Controls.Add(_uriTextBox, 0, 4);
        rightLayout.Controls.Add(shareButtons, 0, 5);

        // ==================== SYSTEM TRAY ====================
        var trayMenu = new ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            ShowImageMargin = false
        };
        _trayToggleItem = new ToolStripMenuItem("Включить прокси-мост", null, async (_, _) => await ToggleProxyAsync());
        _trayCopyHappItem = new ToolStripMenuItem("Скопировать ключ Happ (vless://)", null, (_, _) => CopyHappUri());
        _trayCopyTgItem = new ToolStripMenuItem("Скопировать прокси Telegram (t.me)", null, (_, _) => CopyTelegramUri());
        var trayOpenItem = new ToolStripMenuItem("Открыть окно", null, (_, _) => RestoreFromTray());
        var trayExitItem = new ToolStripMenuItem("Выход", null, (_, _) => ExitApplication());

        trayMenu.Items.Add(_trayToggleItem);
        trayMenu.Items.Add(_trayCopyHappItem);
        trayMenu.Items.Add(_trayCopyTgItem);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(trayOpenItem);
        trayMenu.Items.Add(trayExitItem);

        _trayIcon = new NotifyIcon
        {
            Icon = _inactiveIcon,
            Text = "Zapret Remote — Выключен",
            ContextMenuStrip = trayMenu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();

        // ==================== TIMERS ====================
        _statusTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _statusTimer.Tick += (_, _) => RefreshSystemStatus();
        _statusTimer.Start();

        _copyResetTimer = new System.Windows.Forms.Timer { Interval = 1600 };
        _copyResetTimer.Tick += (_, _) =>
        {
            _copyResetTimer.Stop();
            UpdateCopyButtonLabel();
        };

        _portDebounceTimer = new System.Windows.Forms.Timer { Interval = 600 };
        _portDebounceTimer.Tick += async (_, _) =>
        {
            _portDebounceTimer.Stop();
            await CommitPortChangesAsync();
        };

        _xrayManager.UnexpectedExit += (_, details) =>
        {
            if (IsDisposed)
                return;

            BeginInvoke(() =>
            {
                UpdateProxyVisualState();
                var msg = string.IsNullOrWhiteSpace(details)
                    ? "Процесс xray.exe неожиданно завершился."
                    : $"Процесс xray.exe неожиданно завершился:\n{details}";
                MessageBox.Show(this, msg, "Zapret Remote", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            });
        };

        RefreshNetworkAdapters();
        RefreshSystemStatus();
        SwitchTab(ShareTab.HappVless);
        UpdateProxyVisualState();
    }

    public void RestoreFromTray()
    {
        Show();
        ShowInTaskbar = true;
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        Activate();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_isExiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            ShowInTaskbar = false;

            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _trayIcon.ShowBalloonTip(
                    2500,
                    "Zapret Remote",
                    "Приложение свёрнуто в системный трей и продолжает работу.",
                    ToolTipIcon.Info);
            }
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _startCts?.Cancel();
            _startCts?.Dispose();
            _logDialog?.Dispose();
            _statusTimer.Dispose();
            _copyResetTimer.Dispose();
            _portDebounceTimer.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _qrPictureBox.Image?.Dispose();
            _inactiveIcon.Dispose();
            _activeIcon.Dispose();
        }
        base.Dispose(disposing);
    }

    private void ExitApplication()
    {
        _isExiting = true;
        _startCts?.Cancel();
        _xrayManager.Stop();
        _trayIcon.Visible = false;
        Close();
        Application.Exit();
    }

    private void OpenLogViewer()
    {
        if (_logDialog is { IsDisposed: false })
        {
            _logDialog.BringToFront();
            return;
        }

        _logDialog = new LogViewerDialog(_xrayManager);
        _logDialog.Show(this);
    }

    private void OpenZapretDoctor()
    {
        if (_doctorDialog is { IsDisposed: false })
        {
            _doctorDialog.BringToFront();
            return;
        }

        _doctorDialog = new ZapretDoctorDialog();
        _doctorDialog.StatusChanged += () =>
        {
            if (!IsDisposed)
                RefreshSystemStatus();
        };
        _doctorDialog.Show(this);
    }

    private async Task ToggleProxyAsync()
    {
        if (_isTransitioning)
            return;

        if (_xrayManager.IsRunning)
        {
            _xrayManager.Stop();
            UpdateProxyVisualState();
            return;
        }

        await StartProxyAsync();
    }

    private async Task StartProxyAsync()
    {
        _isTransitioning = true;
        SetControlsEnabled(false);

        _startCts?.Cancel();
        _startCts?.Dispose();
        _startCts = new CancellationTokenSource();

        try
        {
            if (!XrayDownloader.IsXrayInstalled())
            {
                SetProxyStatus("Скачивание Xray-core...", Theme.AccentAmber);
                var progress = new Progress<string>(msg => SetProxyStatus(msg, Theme.AccentAmber));
                await XrayDownloader.EnsureXrayAsync(progress, _startCts.Token);
            }

            SetProxyStatus("Запуск xray.exe...", Theme.AccentAmber);
            _lastTgStatus = TgWsProxyService.GetStatus();
            var lanIp = GetSelectedLanIp();
            await _xrayManager.StartAsync(_settings, lanIp, _lastTgStatus, _startCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Cancelled during shutdown.
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Ошибка запуска прокси", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _isTransitioning = false;
            SetControlsEnabled(true);
            RefreshSystemStatus();
            UpdateProxyVisualState();
        }
    }

    private void SetControlsEnabled(bool enabled)
    {
        _toggleButton.Enabled = enabled;
        _trayToggleItem.Enabled = enabled;
        _happPortInput.Enabled = enabled;
        _tgPortInput.Enabled = enabled;
        _blockQuicCheck.Enabled = enabled;
        _enableTgBridgeCheck.Enabled = enabled;
        _verboseLogCheck.Enabled = enabled;
    }

    private void OnPortEdited()
    {
        if (_suppressSettingEvents)
            return;

        _portDebounceTimer.Stop();
        _portDebounceTimer.Start();
    }

    private async Task CommitPortChangesAsync()
    {
        var newHappPort = (int)_happPortInput.Value;
        var newTgPort = (int)_tgPortInput.Value;

        if (newHappPort == newTgPort)
        {
            _suppressSettingEvents = true;
            newTgPort = newHappPort == AppSettings.DefaultTgBridgePort
                ? AppSettings.DefaultTgBridgePort + 1
                : AppSettings.DefaultTgBridgePort;
            _tgPortInput.Value = newTgPort;
            _suppressSettingEvents = false;
        }

        if (newHappPort == _settings.Port && newTgPort == _settings.TgBridgePort)
            return;

        _settings.Port = newHappPort;
        _settings.TgBridgePort = newTgPort;
        _settings.Save();

        RefreshShareView();
        RefreshSystemStatus();

        if (_xrayManager.IsRunning)
        {
            await StartProxyAsync();
        }
    }

    private async Task OnToggleOptionChangedAsync()
    {
        if (_suppressSettingEvents)
            return;

        _settings.BlockQuic = _blockQuicCheck.Checked;
        _settings.EnableTgBridge = _enableTgBridgeCheck.Checked;
        _settings.VerboseLogging = _verboseLogCheck.Checked;
        _settings.Save();

        RefreshShareView();
        RefreshSystemStatus();

        if (_xrayManager.IsRunning)
        {
            await StartProxyAsync();
        }
    }

    private void OnAdapterChanged()
    {
        if (_suppressSettingEvents)
            return;

        if (_adapterCombo.SelectedItem is LanAdapterInfo selected)
        {
            _settings.PreferredIp = selected.IpAddress;
            _settings.Save();
            RefreshShareView();
            UpdateProxyVisualState();
        }
    }

    private void RefreshNetworkAdapters()
    {
        _suppressSettingEvents = true;
        try
        {
            var adapters = NetworkDetector.GetLanAdapters();
            _adapterCombo.Items.Clear();

            foreach (var adapter in adapters)
            {
                _adapterCombo.Items.Add(adapter);
            }

            var best = NetworkDetector.SelectBestAdapter(adapters, _settings.PreferredIp);
            if (best is not null)
            {
                _adapterCombo.SelectedItem = best;
                if (!string.Equals(_settings.PreferredIp, best.IpAddress, StringComparison.OrdinalIgnoreCase))
                {
                    _settings.PreferredIp = best.IpAddress;
                    _settings.Save();
                }
            }
        }
        finally
        {
            _suppressSettingEvents = false;
        }

        RefreshShareView();
    }

    private void RefreshSystemStatus()
    {
        var zapret = ZapretMonitor.GetStatus();
        if (zapret.IsRunning)
        {
            _zapretDotColor = Theme.AccentGreenHover;
            _zapretLabel.Text = $"zapret (winws.exe): активен (PID {string.Join(", ", zapret.Pids)})";
            _zapretLabel.ForeColor = Theme.TextPrimary;
        }
        else
        {
            _zapretDotColor = Theme.AccentAmber;
            _zapretLabel.Text = "zapret (winws.exe): не запущен";
            _zapretLabel.ForeColor = Theme.AccentAmber;
        }
        _zapretDot.Invalidate();

        var prevReady = _lastTgStatus.IsReadyForBridge;
        _lastTgStatus = TgWsProxyService.GetStatus();
        if (_lastTgStatus.IsProcessRunning && _lastTgStatus.IsReadyForBridge)
        {
            _tgDotColor = Theme.AccentGreenHover;
            _tgLabel.Text = $"TgWsProxy: активен ({_lastTgStatus.LocalHost}:{_lastTgStatus.LocalPort})";
            _tgLabel.ForeColor = Theme.TextPrimary;
        }
        else if (_lastTgStatus.IsReadyForBridge)
        {
            _tgDotColor = Theme.AccentAmber;
            _tgLabel.Text = $"TgWsProxy: не запущен (порт {_lastTgStatus.LocalPort})";
            _tgLabel.ForeColor = Theme.AccentAmber;
        }
        else
        {
            _tgDotColor = Theme.TextSecondary;
            _tgLabel.Text = "TgWsProxy: не найден на ПК";
            _tgLabel.ForeColor = Theme.TextSecondary;
        }
        _tgDot.Invalidate();

        if (prevReady != _lastTgStatus.IsReadyForBridge && _activeTab == ShareTab.Telegram)
        {
            RefreshShareView();
        }

        var requiredPorts = _settings.GetRequiredFirewallPorts(_lastTgStatus.IsReadyForBridge);
        _lastFirewallStatus = FirewallHelper.CheckRuleStatus(AppSettings.XrayExePath, requiredPorts);
        var portsText = string.Join(", ", requiredPorts);

        switch (_lastFirewallStatus)
        {
            case FirewallRuleStatus.Allowed:
                _firewallDotColor = Theme.AccentGreenHover;
                _firewallLabel.Text = $"Брандмауэр: {portsText} открыты";
                _firewallLabel.ForeColor = Theme.TextPrimary;
                _firewallActionButton.Text = "Удалить";
                _firewallActionButton.BackColor = Theme.ButtonNeutral;
                _firewallActionButton.FlatAppearance.MouseOverBackColor = Theme.ButtonNeutralHover;
                break;

            case FirewallRuleStatus.BlockedByRule:
                _firewallDotColor = Theme.AccentRedHover;
                _firewallLabel.Text = "Брандмауэр: найден блок";
                _firewallLabel.ForeColor = Theme.AccentRedHover;
                _firewallActionButton.Text = "Разрешить";
                _firewallActionButton.BackColor = Theme.AccentRed;
                _firewallActionButton.FlatAppearance.MouseOverBackColor = Theme.AccentRedHover;
                break;

            default:
                _firewallDotColor = Theme.AccentAmber;
                _firewallLabel.Text = $"Брандмауэр: открыть {portsText}";
                _firewallLabel.ForeColor = Theme.AccentAmber;
                _firewallActionButton.Text = "Разрешить";
                _firewallActionButton.BackColor = Theme.AccentBlue;
                _firewallActionButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(78, 155, 255);
                break;
        }
        _firewallDot.Invalidate();
    }

    private void SwitchTab(ShareTab tab)
    {
        _activeTab = tab;

        StyleTabButton(_happTabButton, tab == ShareTab.HappVless);
        StyleTabButton(_happRoutingTabButton, tab == ShareTab.HappRouting);
        StyleTabButton(_tgTabButton, tab == ShareTab.Telegram);

        RefreshShareView();
    }

    private static void StyleTabButton(Button btn, bool active)
    {
        btn.BackColor = active ? Theme.AccentBlue : Theme.ButtonNeutral;
        btn.FlatAppearance.MouseOverBackColor = active ? Theme.AccentBlue : Theme.ButtonNeutralHover;
    }

    private void RefreshShareView()
    {
        var lanIp = GetSelectedLanIp() ?? "127.0.0.1";
        _copyResetTimer.Stop();

        switch (_activeTab)
        {
            case ShareTab.HappVless:
            {
                var uri = XrayConfigBuilder.BuildVlessUri(_settings.Uuid, lanIp, _settings.Port);
                _currentQrPayload = uri;
                _currentQrTitle = "1. Happ — Профиль VLESS";
                _uriTextBox.Text = uri;
                _shareHintLabel.Text = "В Happ нажмите «+» -> Сканировать QR-код. Поддерживает Discord Voice (UDP) и все сайты через zapret.";
                _secondaryActionButton.Visible = true;
                _secondaryActionButton.Text = "Новый UUID";
                ShowQrBitmap(uri);
                break;
            }
            case ShareTab.HappRouting:
            {
                var deeplink = XrayConfigBuilder.BuildHappRoutingDeeplink();
                _currentQrPayload = deeplink;
                _currentQrTitle = "2. Маршрут Happ (FakeDNS + DoU)";
                _uriTextBox.Text = deeplink;
                _shareHintLabel.Text = "В Happ: Настройки -> Маршрутизация -> Импорт (или вставьте скопированную ссылку happ://). Отключает IPv6 и ускоряет локальную сеть.";
                _secondaryActionButton.Visible = false;
                ShowQrBitmap(deeplink);
                break;
            }
            case ShareTab.Telegram:
            {
                _secondaryActionButton.Visible = true;
                _secondaryActionButton.Text = "Перечитать";

                if (!_settings.EnableTgBridge)
                {
                    ShowQrPlaceholder("Мост TgWsProxy отключён в настройках слева.\nВключите опцию «Проксировать TgWsProxy в локальную сеть».");
                    _uriTextBox.Text = string.Empty;
                    _shareHintLabel.Text = "Локальный мост для Telegram отключён.";
                }
                else if (!_lastTgStatus.IsReadyForBridge || string.IsNullOrEmpty(_lastTgStatus.FullSecret))
                {
                    ShowQrPlaceholder("Конфиг TgWsProxy не найден (%APPDATA%\\TgWsProxy\\config.json).\nЗапустите TgWsProxy на ПК и нажмите «Перечитать».");
                    _uriTextBox.Text = string.Empty;
                    _shareHintLabel.Text = "TgWsProxy должен быть установлен и запущен на этом ПК.";
                }
                else
                {
                    var tgUrl = TgWsProxyService.BuildTelegramProxyHttpsUri(lanIp, _settings.TgBridgePort, _lastTgStatus.FullSecret);
                    _currentQrPayload = tgUrl;
                    _currentQrTitle = "Telegram — TgWsProxy";
                    _uriTextBox.Text = tgUrl;
                    _shareHintLabel.Text = _lastTgStatus.IsProcessRunning
                        ? "Отсканируйте камерой телефона или вставьте ссылку в Telegram (работает и с Happ, и без него)."
                        : "Внимание: TgWsProxy сейчас не запущен на ПК — запустите его для работы Telegram.";
                    ShowQrBitmap(tgUrl);
                }
                break;
            }
        }

        UpdateCopyButtonLabel();
    }

    private void ShowQrBitmap(string payload)
    {
        _qrPlaceholderLabel.Visible = false;
        _qrPictureBox.Visible = true;
        _qrZoomHintLabel.Visible = true;
        _copyPrimaryButton.Enabled = true;

        var oldImg = _qrPictureBox.Image;
        _qrPictureBox.Image = IconFactory.CreateQrBitmap(payload, 354);
        oldImg?.Dispose();
    }

    private void ShowQrPlaceholder(string message)
    {
        _currentQrPayload = null;
        _currentQrTitle = null;
        _qrPictureBox.Visible = false;
        _qrZoomHintLabel.Visible = false;
        _qrPlaceholderLabel.Text = message;
        _qrPlaceholderLabel.Visible = true;
        _copyPrimaryButton.Enabled = false;
    }

    private void OpenZoomedQr()
    {
        if (string.IsNullOrEmpty(_currentQrPayload) || string.IsNullOrEmpty(_currentQrTitle))
            return;

        using var zoom = new QrZoomDialog(_currentQrTitle, _currentQrPayload);
        zoom.ShowDialog(this);
    }

    private void UpdateCopyButtonLabel()
    {
        _copyPrimaryButton.Text = _activeTab switch
        {
            ShareTab.HappVless => "Скопировать ключ vless://",
            ShareTab.HappRouting => "Скопировать маршрут happ://",
            _ => "Скопировать ссылку t.me/proxy"
        };
    }

    private void CopyCurrentUri()
    {
        if (string.IsNullOrWhiteSpace(_uriTextBox.Text))
            return;

        Clipboard.SetText(_uriTextBox.Text);
        _copyPrimaryButton.Text = "Скопировано в буфер";
        _copyResetTimer.Stop();
        _copyResetTimer.Start();
    }

    private void CopyHappUri()
    {
        var lanIp = GetSelectedLanIp() ?? "127.0.0.1";
        Clipboard.SetText(XrayConfigBuilder.BuildVlessUri(_settings.Uuid, lanIp, _settings.Port));
    }

    private void CopyTelegramUri()
    {
        if (!_lastTgStatus.IsReadyForBridge || string.IsNullOrEmpty(_lastTgStatus.FullSecret))
            return;

        var lanIp = GetSelectedLanIp() ?? "127.0.0.1";
        Clipboard.SetText(TgWsProxyService.BuildTelegramProxyHttpsUri(lanIp, _settings.TgBridgePort, _lastTgStatus.FullSecret));
    }

    private async Task OnSecondaryShareActionAsync()
    {
        if (_activeTab == ShareTab.HappVless)
        {
            var confirm = MessageBox.Show(
                this,
                "Создать новый UUID? Старый QR-код и ключ в Happ перестанут работать, профиль нужно будет добавить заново.",
                "Новый UUID",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (confirm != DialogResult.Yes)
                return;

            _settings.RegenerateUuid();
            RefreshShareView();

            if (_xrayManager.IsRunning)
            {
                await StartProxyAsync();
            }
        }
        else if (_activeTab == ShareTab.Telegram)
        {
            RefreshSystemStatus();
            RefreshShareView();
        }
    }

    private void OnFirewallActionClick()
    {
        if (_lastFirewallStatus == FirewallRuleStatus.Allowed)
        {
            if (!FirewallHelper.RemoveFromFirewall(out var error) && !string.IsNullOrWhiteSpace(error))
            {
                MessageBox.Show(this, error, "Брандмауэр Windows", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        else
        {
            var ports = _settings.GetRequiredFirewallPorts(_lastTgStatus.IsReadyForBridge);
            if (!FirewallHelper.AllowInFirewall(AppSettings.XrayExePath, ports, out var error) && !string.IsNullOrWhiteSpace(error))
            {
                MessageBox.Show(this, error, "Брандмауэр Windows", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        RefreshSystemStatus();
    }

    private void UpdateProxyVisualState()
    {
        var running = _xrayManager.IsRunning;
        var lanIp = GetSelectedLanIp() ?? "0.0.0.0";
        var activePorts = _xrayManager.ActivePorts.Count > 0
            ? string.Join(", ", _xrayManager.ActivePorts)
            : _settings.Port.ToString();

        if (running)
        {
            _toggleButton.Text = "Остановить прокси-мост";
            _toggleButton.BackColor = Theme.AccentRed;
            _toggleButton.FlatAppearance.MouseOverBackColor = Theme.AccentRedHover;
            SetProxyStatus($"Работает: {lanIp} ({activePorts})", Theme.AccentGreenHover);

            Icon = _activeIcon;
            _trayIcon.Icon = _activeIcon;
            _trayIcon.Text = $"Zapret Remote — Активен ({activePorts})";
            _trayToggleItem.Text = "Остановить прокси-мост";
        }
        else
        {
            _toggleButton.Text = "Включить прокси-мост";
            _toggleButton.BackColor = Theme.AccentGreen;
            _toggleButton.FlatAppearance.MouseOverBackColor = Theme.AccentGreenHover;
            if (!_isTransitioning)
            {
                SetProxyStatus("Остановлен", Theme.TextSecondary);
            }

            Icon = _inactiveIcon;
            _trayIcon.Icon = _inactiveIcon;
            _trayIcon.Text = "Zapret Remote — Выключен";
            _trayToggleItem.Text = "Включить прокси-мост";
        }

        _trayCopyTgItem.Enabled = _settings.EnableTgBridge && _lastTgStatus.IsReadyForBridge;
    }

    private void SetProxyStatus(string text, Color dotColor)
    {
        _proxyDotColor = dotColor;
        _proxyStatusDot.Invalidate();
        _proxyStatusLabel.Text = text;
        _proxyStatusLabel.ForeColor = dotColor == Theme.TextSecondary ? Theme.TextSecondary : Theme.TextPrimary;
    }

    private string? GetSelectedLanIp()
    {
        return (_adapterCombo.SelectedItem as LanAdapterInfo)?.IpAddress ?? _settings.PreferredIp;
    }

    private Button CreateTabButton(string text, Action onClick)
    {
        var btn = CreateButton(text, Theme.ButtonNeutral, Theme.ButtonNeutralHover, height: 34);
        btn.Font = Theme.SmallFont;
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private static Label CreateSmallLabel(string text)
    {
        return new Label
        {
            Text = text,
            Font = Theme.SmallFont,
            ForeColor = Theme.TextSecondary,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 4, 4, 0)
        };
    }

    private static CheckBox CreateCheckBox(string text, bool initial)
    {
        return new CheckBox
        {
            Text = text,
            Checked = initial,
            AutoSize = true,
            ForeColor = Theme.TextPrimary,
            Font = Theme.BodyFont,
            Margin = new Padding(0, 0, 0, 7)
        };
    }

    private static NumericUpDown CreatePortInput(int value)
    {
        return new NumericUpDown
        {
            Minimum = AppSettings.MinPort,
            Maximum = AppSettings.MaxPort,
            Value = value,
            BackColor = Theme.InputBackground,
            ForeColor = Theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.BodyFont,
            Dock = DockStyle.Fill
        };
    }

    private static FlowLayoutPanel CreateDotRow(out Panel dot, out Label label, string initialText, Func<Color> colorProvider)
    {
        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        dot = CreateStatusDot(colorProvider);
        label = new Label
        {
            Text = initialText,
            Font = Theme.BodyFont,
            ForeColor = Theme.TextPrimary,
            AutoSize = true,
            Margin = new Padding(6, 0, 0, 0)
        };
        row.Controls.Add(dot);
        row.Controls.Add(label);
        return row;
    }

    private static Panel CreateCard(Padding padding)
    {
        var panel = new Panel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            BackColor = Theme.Surface,
            Padding = padding
        };
        panel.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.SurfaceBorder, 1f);
            e.Graphics.DrawRectangle(pen, 0, 0, panel.Width - 1, panel.Height - 1);
        };
        return panel;
    }

    private static Button CreateButton(string text, Color back, Color hover, int height)
    {
        var btn = new Button
        {
            Text = text,
            Dock = DockStyle.Top,
            Height = height,
            FlatStyle = FlatStyle.Flat,
            BackColor = back,
            ForeColor = Color.White,
            Font = Theme.BodyFont,
            Cursor = Cursors.Hand
        };
        btn.FlatAppearance.BorderSize = 0;
        btn.FlatAppearance.MouseOverBackColor = hover;
        return btn;
    }

    private static Panel CreateStatusDot(Func<Color> colorProvider)
    {
        var dot = new Panel
        {
            Size = new Size(10, 10),
            Margin = new Padding(0, 4, 0, 0),
            BackColor = Color.Transparent
        };
        dot.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(colorProvider());
            e.Graphics.FillEllipse(brush, 1, 1, 8, 8);
        };
        return dot;
    }

    private static void EnableDarkTitleBar(IntPtr hwnd)
    {
        try
        {
            const int DwmwaUseImmersiveDarkMode = 20;
            int enabled = 1;
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
        }
        catch
        {
            // Ignore on older Windows builds.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColorTable()) { }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = Theme.TextPrimary;
            base.OnRenderItemText(e);
        }
    }

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Surface;
        public override Color MenuItemSelected => Theme.ButtonNeutralHover;
        public override Color MenuItemBorder => Theme.SurfaceBorder;
        public override Color MenuBorder => Theme.SurfaceBorder;
        public override Color SeparatorDark => Theme.SurfaceBorder;
        public override Color SeparatorLight => Theme.SurfaceBorder;
    }
}
