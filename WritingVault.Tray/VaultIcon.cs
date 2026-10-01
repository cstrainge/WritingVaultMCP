using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace WritingVault.Tray;

internal enum VaultBadge { Healthy, Partial, Stopped, Busy }

internal static class VaultIcon
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon Create(string baseIconPath, VaultBadge badge)
    {
        using var source = new Icon(baseIconPath, new Size(32, 32));
        if (badge == VaultBadge.Healthy)
            return (Icon)source.Clone();

        using var bitmap = new Bitmap(32, 32);
        using (var canvas = Graphics.FromImage(bitmap))
        {
            canvas.Clear(Color.Transparent);
            canvas.SmoothingMode = SmoothingMode.AntiAlias;
            canvas.DrawIcon(source, new Rectangle(0, 0, 32, 32));
            var color = badge switch
            {
                VaultBadge.Partial => Color.FromArgb(235, 168, 54),
                VaultBadge.Stopped => Color.FromArgb(105, 108, 105),
                _ => Color.FromArgb(83, 141, 210)
            };
            using var outline = new SolidBrush(Color.FromArgb(24, 26, 23));
            using var fill = new SolidBrush(color);
            using var white = new Pen(Color.White, 2.2f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            canvas.FillEllipse(outline, 18, 18, 14, 14);
            canvas.FillEllipse(fill, 20, 20, 10, 10);
            switch (badge)
            {
                case VaultBadge.Partial:
                    canvas.DrawLine(white, 25, 22, 25, 25);
                    canvas.FillEllipse(Brushes.White, 24, 27, 2, 2);
                    break;
                case VaultBadge.Stopped:
                    canvas.DrawLine(white, 22, 28, 28, 22);
                    break;
                case VaultBadge.Busy:
                    canvas.DrawArc(white, 22, 22, 6, 6, 15, 290);
                    break;
            }
        }

        var handle = bitmap.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }
}
