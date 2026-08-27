using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media.Imaging;

namespace Lampa.Desktop;

internal static class AppIconFactory
{
    public enum StatusKind
    {
        Idle,
        Connected,
        Error
    }

    private static Bitmap? _sourceArt;
    private static Bitmap? SourceArt => _sourceArt ??= LoadSourceArt();

    public static System.Drawing.Icon CreateTrayIcon(StatusKind status)
    {
        using var bitmap = CreateBitmap(32, status, forTray: true);
        return System.Drawing.Icon.FromHandle(bitmap.GetHicon());
    }

    public static BitmapSource CreateWindowIcon()
    {
        using var bitmap = CreateBitmap(128, StatusKind.Idle, forTray: false);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        stream.Position = 0;

        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        frame.Freeze();
        return frame;
    }

    private static Bitmap? LoadSourceArt()
    {
        foreach (var uri in new[]
                 {
                     new Uri("pack://application:,,,/Assets/hottabych-genie-v2.png", UriKind.Absolute),
                     new Uri("pack://application:,,,/Lampa;component/Assets/hottabych-genie-v2.png", UriKind.Absolute)
                 })
        {
            try
            {
                var resource = System.Windows.Application.GetResourceStream(uri);
                if (resource is null) continue;
                using var stream = resource.Stream;
                using var original = new Bitmap(stream);
                return WithTransparentBackground(original);
            }
            catch
            {
                // try next
            }
        }

        return TryLoadFromDisk();
    }

    private static Bitmap? TryLoadFromDisk()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "hottabych-genie-v2.png");
            return File.Exists(path) ? WithTransparentBackground(new Bitmap(path)) : null;
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap WithTransparentBackground(Bitmap source)
    {
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var pixel = source.GetPixel(x, y);
                result.SetPixel(x, y, pixel.R + pixel.G + pixel.B < 28
                    ? Color.Transparent
                    : pixel);
            }
        }

        return result;
    }

    private static Bitmap CreateBitmap(int size, StatusKind status, bool forTray)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.Clear(Color.Transparent);

        var background = Color.FromArgb(255, 3, 8, 14);
        var statusColor = status switch
        {
            StatusKind.Connected => Color.FromArgb(255, 66, 245, 138),
            StatusKind.Error => Color.FromArgb(255, 255, 69, 58),
            _ => Color.FromArgb(255, 88, 184, 255)
        };

        using var statusBrush = new SolidBrush(statusColor);

        var pad = size * 0.04f;
        var body = new RectangleF(pad, pad, size - pad * 2, size - pad * 2);
        if (SourceArt is null)
        {
            using var fallbackBrush = new SolidBrush(background);
            FillRoundRect(graphics, fallbackBrush, body, size * 0.22f);
        }

        if (SourceArt is not null)
        {
            var artPad = size * (forTray ? 0.10f : 0.08f);
            graphics.DrawImage(SourceArt, new RectangleF(artPad, artPad, size - artPad * 2, size - artPad * 2));
        }

        if (forTray)
        {
            using var ring = new Pen(statusColor, Math.Max(3f, size * 0.12f));
            graphics.DrawEllipse(ring, pad + 1, pad + 1, size - pad * 2 - 2, size - pad * 2 - 2);

            var badge = size * 0.46f;
            var badgeX = size - badge - size * 0.02f;
            var badgeY = size - badge - size * 0.02f;
            graphics.FillEllipse(statusBrush, badgeX, badgeY, badge, badge);
            using var outline = new Pen(background, Math.Max(2f, size * 0.06f));
            graphics.DrawEllipse(outline, badgeX, badgeY, badge, badge);
        }

        return bitmap;
    }

    private static void FillRoundRect(Graphics graphics, Brush brush, RectangleF rect, float radius)
    {
        using var path = BuildRoundedPath(rect, radius);
        graphics.FillPath(brush, path);
    }

    private static GraphicsPath BuildRoundedPath(RectangleF rect, float radius)
    {
        var diameter = Math.Max(2, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
