using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using QRCoder;

namespace ZapretRemote.UI;

/// <summary>
/// Generates crisp state-specific tray/window icons and QR code bitmaps.
/// </summary>
public static class IconFactory
{
    public static Icon CreateStatusIcon(bool active)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var bg = active ? Theme.AccentGreen : Color.FromArgb(52, 59, 71);
            var border = active ? Theme.AccentGreenHover : Color.FromArgb(84, 93, 110);
            var fg = active ? Color.White : Color.FromArgb(190, 198, 212);

            using var bgBrush = new SolidBrush(bg);
            using var borderPen = new Pen(border, 1.5f);
            using var path = RoundedRect(new RectangleF(1.5f, 1.5f, 29f, 29f), 7f);
            g.FillPath(bgBrush, path);
            g.DrawPath(borderPen, path);

            // Bridge arc + two endpoint nodes
            using var arcPen = new Pen(fg, 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(arcPen, 7f, 8f, 18f, 16f, 185f, 170f);

            using var nodeBrush = new SolidBrush(fg);
            g.FillEllipse(nodeBrush, 5.5f, 16.5f, 5.5f, 5.5f);
            g.FillEllipse(nodeBrush, 21f, 16.5f, 5.5f, 5.5f);
            g.FillEllipse(nodeBrush, 13.5f, 5.5f, 5f, 5f);
        }

        var hIcon = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    public static Bitmap CreateQrBitmap(string payload, int targetSizePx)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        using var qrCode = new QRCode(data);
        using var raw = qrCode.GetGraphic(8, Color.Black, Color.White, drawQuietZones: true);

        var resized = new Bitmap(targetSizePx, targetSizePx);
        using var g = Graphics.FromImage(resized);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.Clear(Color.White);
        g.DrawImage(raw, 0, 0, targetSizePx, targetSizePx);
        return resized;
    }

    private static GraphicsPath RoundedRect(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
