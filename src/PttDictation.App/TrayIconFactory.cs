namespace PttDictation.App;

internal static class TrayIconFactory
{
    public static Icon Create()
    {
        using var bitmap = new Bitmap(16, 16);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        using var microphoneBrush = new SolidBrush(Color.FromArgb(238, 242, 247));
        using var accentBrush = new SolidBrush(Color.FromArgb(45, 212, 191));
        using var shadowPen = new Pen(Color.FromArgb(28, 32, 38), 1.5f);
        using var microphonePen = new Pen(Color.FromArgb(238, 242, 247), 1.7f)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round
        };

        using var microphoneBody = CreateRoundedRectanglePath(new RectangleF(5, 1, 6, 9), 3);
        graphics.FillPath(microphoneBrush, microphoneBody);
        graphics.DrawPath(shadowPen, microphoneBody);
        graphics.DrawArc(microphonePen, new RectangleF(3, 5, 10, 7), 0, 180);
        graphics.DrawLine(microphonePen, 8, 11, 8, 14);
        graphics.DrawLine(microphonePen, 5.5f, 14, 10.5f, 14);
        graphics.FillEllipse(accentBrush, 7, 3, 2, 2);

        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static System.Drawing.Drawing2D.GraphicsPath CreateRoundedRectanglePath(RectangleF bounds, float radius)
    {
        var diameter = radius * 2;
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
