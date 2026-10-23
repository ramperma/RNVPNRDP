using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace WgRdp.Tray;

/// <summary>Iconos de bandeja generados en memoria (gris / azul / verde / rojo).</summary>
internal static class TrayIcons
{
    public static Icon Idle { get; } = Create(Color.FromArgb(139, 155, 176));
    public static Icon Busy { get; } = Create(Color.FromArgb(61, 156, 240));
    public static Icon Active { get; } = Create(Color.FromArgb(61, 220, 151));
    public static Icon Failed { get; } = Create(Color.FromArgb(224, 108, 117));

    private static Icon Create(Color accent)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using (var bg = new SolidBrush(Color.FromArgb(22, 29, 39)))
                g.FillEllipse(bg, 1, 1, size - 3, size - 3);
            using (var ring = new Pen(Color.FromArgb(42, 53, 68), 2f))
                g.DrawEllipse(ring, 2, 2, size - 5, size - 5);
            using (var fill = new SolidBrush(accent))
                g.FillEllipse(fill, 8, 8, size - 17, size - 17);
        }

        var handle = bmp.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
