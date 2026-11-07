using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace PaxImageViewer.App;

/// <summary>
/// The app mark, drawn in code: a framed landscape with a magnifying lens over its corner. Rendered to the .ico at build
/// time (<c>--write-icon</c>) and to the window icon at start-up.
/// </summary>
internal static class AppIcon
{
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

    private static void Draw(DrawingContext ctx, double size)
    {
        using var _ = ctx.PushTransform(Matrix.CreateScale(size / 100, size / 100));

        var tile = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse("#1F2838"), 0), new GradientStop(Color.Parse("#0C1017"), 1) },
        };
        ctx.DrawRectangle(tile, new Pen(new SolidColorBrush(Color.Parse("#34425A")), 1.6), new RoundedRect(new Rect(5, 5, 90, 90), 22));

        // The photo.
        var frame = new Rect(16, 22, 64, 52);
        ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#0A0D13")), null, new RoundedRect(frame, 7));
        using (ctx.PushClip(new RoundedRect(frame, 7)))
        {
            var sky = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#16243A"), 0), new GradientStop(Color.Parse("#0E1522"), 1) },
            };
            ctx.FillRectangle(sky, frame);
            ctx.DrawEllipse(new SolidColorBrush(Color.Parse("#F5C15C")), null, new Point(63, 36), 5.5, 5.5);
            var hills = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#89CFF0"), 0), new GradientStop(Color.Parse("#8C8CF2"), 1) },
            };
            ctx.DrawGeometry(hills, null, Geometry.Parse("M14 76 L36 46 L49 62 L58 52 L82 76 Z"));
        }
        ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#89CFF0")), 3), new RoundedRect(frame, 7));

        // The lens.
        var lensPen = new Pen(new SolidColorBrush(Color.Parse("#EEF2F8")), 5, lineCap: PenLineCap.Round);
        ctx.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#EEF2F8")), 8.5, lineCap: PenLineCap.Round), new Point(80, 80), new Point(88, 88));
        ctx.DrawEllipse(new SolidColorBrush(Color.Parse("#D90C1017")), lensPen, new Point(69, 69), 12.5, 12.5);
        ctx.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#89CFF0")), 2.6, lineCap: PenLineCap.Round), new Point(64, 69), new Point(74, 69));
        ctx.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#89CFF0")), 2.6, lineCap: PenLineCap.Round), new Point(69, 64), new Point(69, 74));
    }

    private static RenderTargetBitmap Render(int px)
    {
        var bmp = new RenderTargetBitmap(new PixelSize(px, px), new Vector(96, 96));
        using (var ctx = bmp.CreateDrawingContext()) Draw(ctx, px);
        return bmp;
    }

    public static WindowIcon CreateWindowIcon()
    {
        using var bmp = Render(64);
        var ms = new MemoryStream();
        bmp.Save(ms);
        ms.Position = 0;
        return new WindowIcon(ms);
    }

    /// <summary>A multi-size .ico (PNG entries, which every supported Windows reads).</summary>
    public static void WriteIco(string path)
    {
        var images = Sizes.Select(s =>
        {
            using var bmp = Render(s);
            var ms = new MemoryStream();
            bmp.Save(ms);
            return (Size: s, Png: ms.ToArray());
        }).ToList();

        using var f = File.Create(path);
        using var w = new BinaryWriter(f);
        w.Write((ushort)0);
        w.Write((ushort)1);
        w.Write((ushort)images.Count);
        var offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((ushort)1);
            w.Write((ushort)32);
            w.Write(png.Length);
            w.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in images) w.Write(png);
    }
}
