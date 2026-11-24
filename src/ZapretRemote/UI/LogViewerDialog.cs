using System.Drawing;
using System.Runtime.InteropServices;
using ZapretRemote.Services;

namespace ZapretRemote.UI;

/// <summary>
/// Live diagnostic log viewer for xray.exe stdout/stderr output.
/// </summary>
public sealed class LogViewerDialog : Form
{
    private readonly XrayProcessManager _manager;
    private readonly TextBox _logBox;

    public LogViewerDialog(XrayProcessManager manager)
    {
        _manager = manager;

        Text = "Лог Xray";
        BackColor = Theme.Background;
        ForeColor = Theme.TextPrimary;
        Font = Theme.BodyFont;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(760, 440);
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

        _logBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            BackColor = Theme.InputBackground,
            ForeColor = Theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.MonoFont,
            Text = _manager.GetRecentLogTail()
        };

        var bottomRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Margin = new Padding(0, 8, 0, 0)
        };

        var closeBtn = new Button
        {
            Text = "Закрыть",
            Width = 100,
            Height = 30,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.ButtonNeutral,
            ForeColor = Color.White,
            Cursor = Cursors.Hand
        };
        closeBtn.FlatAppearance.BorderSize = 0;
        closeBtn.Click += (_, _) => Close();

        var copyBtn = new Button
        {
            Text = "Скопировать лог",
            Width = 140,
            Height = 30,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.AccentBlue,
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, 8, 0)
        };
        copyBtn.FlatAppearance.BorderSize = 0;
        copyBtn.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_logBox.Text))
                Clipboard.SetText(_logBox.Text);
        };

        bottomRow.Controls.Add(closeBtn);
        bottomRow.Controls.Add(copyBtn);

        root.Controls.Add(_logBox, 0, 0);
        root.Controls.Add(bottomRow, 0, 1);

        _manager.LogLineReceived += OnLogLineReceived;
        Shown += (_, _) => ScrollToEnd();
    }

    private void OnLogLineReceived(object? sender, string line)
    {
        if (IsDisposed)
            return;

        try
        {
            BeginInvoke(() =>
            {
                if (IsDisposed)
                    return;

                if (_logBox.TextLength > 0)
                    _logBox.AppendText(Environment.NewLine);
                _logBox.AppendText(line);
                ScrollToEnd();
            });
        }
        catch
        {
            // Ignore shutdown races.
        }
    }

    private void ScrollToEnd()
    {
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.ScrollToCaret();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _manager.LogLineReceived -= OnLogLineReceived;
        }
        base.Dispose(disposing);
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
}
