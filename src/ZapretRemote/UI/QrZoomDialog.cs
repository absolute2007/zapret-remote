using System.Drawing;
using System.Runtime.InteropServices;

namespace ZapretRemote.UI;

/// <summary>
/// Modal dialog showing an enlarged 520x520 px QR code for effortless camera scanning.
/// </summary>
public sealed class QrZoomDialog : Form
{
    private readonly PictureBox _qrBox;

    public QrZoomDialog(string title, string payload)
    {
        Text = title;
        BackColor = Theme.Background;
        ForeColor = Theme.TextPrimary;
        Font = Theme.BodyFont;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(590, 640);

        EnableDarkTitleBar(Handle);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Theme.Background
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        var header = new Label
        {
            Text = title,
            Font = Theme.TitleFont,
            ForeColor = Theme.TextPrimary,
            AutoSize = true,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 0, 0, 12)
        };

        var frame = new Panel
        {
            Size = new Size(544, 544),
            BackColor = Color.White,
            Padding = new Padding(12),
            Anchor = AnchorStyles.None,
            Cursor = Cursors.Hand
        };

        _qrBox = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.White,
            Image = IconFactory.CreateQrBitmap(payload, 520),
            Cursor = Cursors.Hand
        };
        _qrBox.Click += (_, _) => Close();
        frame.Click += (_, _) => Close();
        frame.Controls.Add(_qrBox);

        var hint = new Label
        {
            Text = "Нажмите Esc или кликните по QR-коду, чтобы закрыть",
            Font = Theme.SmallFont,
            ForeColor = Theme.TextSecondary,
            AutoSize = true,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 10, 0, 0)
        };

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(frame, 0, 1);
        root.Controls.Add(hint, 0, 2);

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
                Close();
        };
        Click += (_, _) => Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _qrBox.Image?.Dispose();
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
