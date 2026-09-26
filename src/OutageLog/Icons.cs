using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;

namespace OutageLog;

/// <summary>Tray icons drawn at runtime: the app's pulse line on a square colored by the current state.</summary>
internal static class Icons
{
    public static readonly Color Online = Color.FromArgb(0x0c, 0xa3, 0x0c);
    public static readonly Color Slow = Color.FromArgb(0xfa, 0xb2, 0x19);
    public static readonly Color Down = Color.FromArgb(0xd0, 0x3b, 0x3b);
    public static readonly Color Idle = Color.FromArgb(0x89, 0x87, 0x81);

    private static readonly PointF[] Pulse =
    {
        new(0.12f, 0.56f), new(0.33f, 0.56f), new(0.43f, 0.26f), new(0.56f, 0.80f), new(0.66f, 0.46f), new(0.73f, 0.56f), new(0.88f, 0.56f),
    };

    public static Color ColorFor(Status? status) => status switch
    {
        null => Idle,
        Status.Up => Online,
        Status.Degraded => Slow,
        _ => Down,
    };

    public static Icon Make(Color background, int size)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float r = size * 0.22f;
            using (var path = RoundedRect(new RectangleF(0.5f, 0.5f, size - 1, size - 1), r))
            using (var brush = new SolidBrush(background))
                g.FillPath(brush, path);
            var points = Array.ConvertAll(Pulse, p => new PointF(p.X * size, p.Y * size));
            using var pen = new Pen(Color.White, Math.Max(1.6f, size * 0.11f)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(pen, points);
        }
        IntPtr handle = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(handle).Clone();
        Native.DestroyIcon(handle);
        return icon;
    }

    public static Icon AppIcon(Size size)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("OutageLog.app.ico");
        return stream != null ? new Icon(stream, size) : SystemIcons.Application;
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
