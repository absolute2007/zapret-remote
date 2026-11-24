using System.Drawing;
using System.Runtime.InteropServices;
using ZapretRemote.Models;
using ZapretRemote.Services;

namespace ZapretRemote.UI;

/// <summary>
/// Interactive YouTube/zapret diagnostics and safe backup-first auto-tuning dialog.
/// </summary>
public sealed class ZapretDoctorDialog : Form
{
    private readonly TextBox _reportBox;
    private readonly Button _recheckButton;
    private readonly Button _autoTuneButton;
    private readonly Button _restoreButton;
    private readonly Button _closeButton;
    private readonly CancellationTokenSource _cts = new();

    private bool _busy;

    public event Action? StatusChanged;

    public ZapretDoctorDialog()
    {
        Text = "Диагностика и настройка zapret";
        BackColor = Theme.Background;
        ForeColor = Theme.TextPrimary;
        Font = Theme.BodyFont;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(700, 420);
        ShowInTaskbar = false;

        EnableDarkTitleBar(Handle);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Theme.Background
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        _reportBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = true,
            BackColor = Theme.InputBackground,
            ForeColor = Theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.BodyFont,
            Text = "Запуск проверки YouTube и параметров службы zapret..."
        };

        var bottomRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Margin = new Padding(0, 10, 0, 0)
        };

        _closeButton = MakeButton("Закрыть", 96, Theme.ButtonNeutral);
        _closeButton.Click += (_, _) => Close();

        _restoreButton = MakeButton("Откатить zapret (UAC)", 164, Theme.ButtonNeutral);
        _restoreButton.Margin = new Padding(0, 0, 8, 0);
        _restoreButton.Click += async (_, _) => await RunRestoreAsync();

        _autoTuneButton = MakeButton("Автоподбор zapret (UAC)", 184, Theme.AccentBlue);
        _autoTuneButton.Margin = new Padding(0, 0, 8, 0);
        _autoTuneButton.Click += async (_, _) => await RunAutoTuneAsync();

        _recheckButton = MakeButton("Проверить снова", 136, Theme.ButtonNeutral);
        _recheckButton.Margin = new Padding(0, 0, 8, 0);
        _recheckButton.Click += async (_, _) => await RunDiagnosticsAsync();

        bottomRow.Controls.Add(_closeButton);
        bottomRow.Controls.Add(_restoreButton);
        bottomRow.Controls.Add(_autoTuneButton);
        bottomRow.Controls.Add(_recheckButton);

        root.Controls.Add(_reportBox, 0, 0);
        root.Controls.Add(bottomRow, 0, 1);

        Shown += async (_, _) => await RunDiagnosticsAsync();
        FormClosing += (_, _) => _cts.Cancel();
    }

    private async Task RunDiagnosticsAsync()
    {
        if (_busy)
            return;

        SetBusy(true, "Выполняется проверка www.youtube.com, локального и внешнего CDN (около 3-8 сек)...");
        try
        {
            var report = await ZapretAutoFixService.DiagnoseYouTubeAsync(AppSettings.XrayExePath, _cts.Token);
            if (!IsDisposed)
            {
                _reportBox.Text = report.SummaryText;
                UpdateButtonsState(report.ServiceInfo);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
                _reportBox.Text = $"Ошибка диагностики: {ex.Message}";
        }
        finally
        {
            if (!IsDisposed)
                SetBusy(false, null);
        }
    }

    private async Task RunAutoTuneAsync()
    {
        if (_busy)
            return;

        var info = ZapretAutoFixService.InspectService();
        if (!info.CanPatchGoogleRule)
        {
            MessageBox.Show(
                this,
                "Автоподбор поддерживается для установленной службы Windows 'zapret' с правилом list-google.txt.",
                "Диагностика zapret",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var confirm = MessageBox.Show(
            this,
            "Программа сохранит оригинал службы zapret в zapret-backup.json, протестирует несколько стратегий для list-google.txt и применит новую ТОЛЬКО если она реально лучше текущей (Discord и остальные правила не трогаются). Продолжить?",
            "Автоподбор zapret",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (confirm != DialogResult.Yes)
            return;

        SetBusy(true, "Ожидается подтверждение UAC и тестирование стратегий list-google.txt (до 30-45 сек)...");
        try
        {
            var (ok, tuneReport, err) = await Task.Run(() =>
            {
                var success = ZapretAutoFixService.AutoTuneAndApply(AppSettings.XrayExePath, out var rep, out var error);
                return (success, rep, error);
            });

            if (IsDisposed)
                return;

            if (!ok)
            {
                MessageBox.Show(this, err ?? "Не удалось выполнить автоподбор.", "Автоподбор zapret", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                await RunDiagnosticsAsync();
                return;
            }

            _reportBox.Text = "=== Результат автоподбора list-google.txt ===\r\n" + tuneReport;
            StatusChanged?.Invoke();
            UpdateButtonsState(ZapretAutoFixService.InspectService());
        }
        finally
        {
            if (!IsDisposed)
                SetBusy(false, null);
        }
    }

    private async Task RunRestoreAsync()
    {
        if (_busy)
            return;

        SetBusy(true, "Откат службы zapret к сохранённому оригиналу из zapret-backup.json...");
        try
        {
            var (ok, err) = await Task.Run(() =>
            {
                var success = ZapretAutoFixService.RestoreOriginal(out var error);
                return (success, error);
            });

            if (IsDisposed)
                return;

            if (!ok)
            {
                MessageBox.Show(this, err ?? "Не удалось восстановить zapret.", "Откат zapret", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else
            {
                MessageBox.Show(this, "Оригинальные настройки службы zapret восстановлены и перезапущены.", "Откат zapret", MessageBoxButtons.OK, MessageBoxIcon.Information);
                StatusChanged?.Invoke();
            }

            SetBusy(false, null);
            await RunDiagnosticsAsync();
        }
        finally
        {
            if (!IsDisposed)
                SetBusy(false, null);
        }
    }

    private void SetBusy(bool busy, string? statusText)
    {
        _busy = busy;
        if (!string.IsNullOrEmpty(statusText))
            _reportBox.Text = statusText;

        if (busy)
        {
            _recheckButton.Enabled = false;
            _autoTuneButton.Enabled = false;
            _restoreButton.Enabled = false;
        }
        else
        {
            _recheckButton.Enabled = true;
            UpdateButtonsState(ZapretAutoFixService.InspectService());
        }
    }

    private void UpdateButtonsState(ZapretServiceInfo info)
    {
        _autoTuneButton.Enabled = !_busy && info.CanPatchGoogleRule;
        _restoreButton.Enabled = !_busy && info.HasBackup;
    }

    private static Button MakeButton(string text, int width, Color backColor)
    {
        var btn = new Button
        {
            Text = text,
            Width = width,
            Height = 30,
            FlatStyle = FlatStyle.Flat,
            BackColor = backColor,
            ForeColor = Color.White,
            Cursor = Cursors.Hand
        };
        btn.FlatAppearance.BorderSize = 0;
        return btn;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private static void EnableDarkTitleBar(IntPtr handle)
    {
        var dark = 1;
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
    }
}
