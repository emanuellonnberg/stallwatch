using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace StallWatch.Tray;

/// <summary>Tray icons drawn at startup (no image files): a filled dot whose colour is the state.</summary>
static class TrayIcons
{
    public static readonly Icon Watching = Dot(Color.FromArgb(34, 160, 90));
    public static readonly Icon Limited = Dot(Color.FromArgb(214, 158, 26));
    public static readonly Icon Paused = Dot(Color.FromArgb(140, 140, 140));
    public static readonly Icon Alert = Dot(Color.FromArgb(214, 50, 50));

    static Icon Dot(Color color)
    {
        var size = SystemInformation.SmallIconSize.Width;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(size * 0.12f, size * 0.12f, size * 0.76f, size * 0.76f);
            using var fill = new SolidBrush(color);
            using var edge = new Pen(Color.FromArgb(200, 255, 255, 255), Math.Max(1, size / 16f));
            g.FillEllipse(fill, r);
            g.DrawEllipse(edge, r);
        }
        var h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone(); // own copy, so the handle can be freed
        DestroyIcon(h);
        return icon;
    }

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
}
