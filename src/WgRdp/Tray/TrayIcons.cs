using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;

namespace WgRdp.Tray;

/// <summary>Iconos de bandeja a partir del logo de la app, con indicador de estado.</summary>
internal static class TrayIcons
{
    public static Icon Idle { get; } = Create(Color.FromArgb(139, 155, 176));
    public static Icon Busy { get; } = Create(Color.FromArgb(61, 156, 240));
    public static Icon Active { get; } = Create(Color.FromArgb(61, 220, 151));
    public static Icon Failed { get; } = Create(Color.FromArgb(224, 108, 117));

    private static Bitmap? _brand;

    private static Bitmap Brand
    {
        get
        {
            if (_brand is not null)
                return _brand;

            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "app-icon-48.png");
            if (File.Exists(path))
            {
                using var loaded = new Bitmap(path);
                _brand = new Bitmap(loaded);
                return _brand;
            }

            _brand = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(_brand))
            {
                g.Clear(Color.Transparent);
                using var fill = new SolidBrush(Color.FromArgb(31, 138, 112));
                g.FillEllipse(fill, 4, 4, 24, 24);
            }

            return _brand;
        }
    }

    private static Icon Create(Color accent)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);

            var brand = Brand;
            var inset = 2;
            g.DrawImage(brand, inset, inset, size - inset * 2, size - inset * 2);

            const int dot = 8;
            using (var ring = new SolidBrush(Color.FromArgb(22, 29, 39)))
                g.FillEllipse(ring, size - dot - 2, size - dot - 2, dot + 2, dot + 2);
            using (var fill = new SolidBrush(accent))
                g.FillEllipse(fill, size - dot - 1, size - dot - 1, dot, dot);
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
