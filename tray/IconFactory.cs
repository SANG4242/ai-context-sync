using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;

namespace AIContextSyncTray;

internal static class IconFactory
{
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Bitmap LoadBaseBitmap()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.png")
            ?? throw new InvalidOperationException("缺少嵌入图标资源 app.png。");
        using var source = new Bitmap(stream);
        return new Bitmap(source);
    }

    public static Icon CreateTrayIcon(Bitmap source, ServiceState state)
    {
        using var canvas = new Bitmap(64, 64);
        using var graphics = Graphics.FromImage(canvas);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.Clear(Color.Transparent);
        graphics.DrawImage(source, new Rectangle(2, 2, 60, 60));

        var badgeColor = state switch
        {
            ServiceState.Connected => Color.FromArgb(34, 197, 94),
            ServiceState.Degraded => Color.FromArgb(245, 158, 11),
            ServiceState.Stopped => Color.FromArgb(239, 68, 68),
            _ => Color.FromArgb(148, 163, 184)
        };

        using var badgeBrush = new SolidBrush(badgeColor);
        using var borderPen = new Pen(Color.White, 3);
        var badge = new Rectangle(39, 39, 23, 23);

        if (state == ServiceState.Degraded)
        {
            var points = new[]
            {
                new Point(51, 38),
                new Point(63, 61),
                new Point(39, 61)
            };
            graphics.FillPolygon(badgeBrush, points);
            graphics.DrawPolygon(borderPen, points);
            using var markPen = new Pen(Color.White, 3) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            graphics.DrawLine(markPen, 51, 46, 51, 54);
            graphics.FillEllipse(Brushes.White, 49.5f, 56, 3, 3);
        }
        else
        {
            graphics.FillEllipse(badgeBrush, badge);
            graphics.DrawEllipse(borderPen, badge);
            using var markPen = new Pen(Color.White, 3.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            if (state == ServiceState.Connected)
            {
                graphics.DrawLines(markPen, new[] { new Point(45, 51), new Point(49, 56), new Point(58, 46) });
            }
            else if (state == ServiceState.Stopped)
            {
                graphics.DrawLine(markPen, 46, 46, 56, 56);
                graphics.DrawLine(markPen, 56, 46, 46, 56);
            }
            else
            {
                graphics.FillEllipse(Brushes.White, 45, 50, 3, 3);
                graphics.FillEllipse(Brushes.White, 50, 50, 3, 3);
                graphics.FillEllipse(Brushes.White, 55, 50, 3, 3);
            }
        }

        var handle = canvas.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }
}
