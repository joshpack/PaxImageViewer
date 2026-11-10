using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using SkiaSharp;

namespace pxdImageViewer.Screenshots;

/// <summary>
/// pxdImageViewer.Screenshots.exe &lt;output folder&gt;
/// Paints eight made-up landscapes (no text in them) into a temp folder, opens the first in a viewer window with
/// Avalonia.Headless and real Skia drawing (nothing on screen), saves viewer.png and removes the temp folder.
/// Viewer settings go to a throwaway folder (PXDVIEW_DATA).
/// </summary>
internal static class Program
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<ShotApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont();

    [STAThread]
    public static void Main(string[] args)
    {
        var outDir = Path.GetFullPath(args.Length > 0 ? args[0] : "screenshots");
        Directory.CreateDirectory(outDir);
        var work = Path.Combine(Path.GetTempPath(), "pxdImageViewer-shots-" + Environment.ProcessId);
        var images = Path.Combine(work, "images");
        Environment.SetEnvironmentVariable("PXDVIEW_DATA", Path.Combine(work, "data"));
        var log = new List<string>();
        var code = 0;
        try
        {
            Directory.CreateDirectory(images);
            Paint(Path.Combine(images, "Alpine lake.jpg"), 6000, 4000, 0);
            var names = new[] { "Coastline", "Desert road", "Forest trail", "Harbour", "Meadow", "Mountain pass", "Snowfield" };
            for (var i = 0; i < names.Length; i++) Paint(Path.Combine(images, names[i] + ".png"), 1600, 1060, i + 1);

            using var session = HeadlessUnitTestSession.StartNew(typeof(Program));
            session.Dispatch(async () =>
            {
                var w = new ViewerWindow(canOpenFiles: true) { Width = 1200, Height = 800 };
                w.Show();
                w.Viewer.Open(Path.Combine(images, "Alpine lake.jpg"));
                await Settle(3000);
                // The toolbar hides after a moment without the mouse: move it over the picture, like a user would.
                w.MouseMove(new Point(600, 420));
                await Settle(400);
                var frame = w.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame.");
                frame.Save(Path.Combine(outDir, "viewer.png"));
                log.Add($"viewer.png: {frame.PixelSize.Width}×{frame.PixelSize.Height}");
                w.Close();
                await Settle(200);
                return 0;
            }, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            log.Add("FAILED " + ex);
            code = 1;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (Exception ex) { log.Add("cleanup: " + ex.Message); }
            File.WriteAllLines(Path.Combine(outDir, "screenshots.log"), log);
        }
        Environment.Exit(code);
    }

    private static async Task Settle(int ms)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(25);
        }
    }

    /// <summary>A painted landscape: sky gradient, sun, two ridges, a lake with a reflection line, light grain.</summary>
    private static void Paint(string path, int w, int h, int seed)
    {
        var palettes = new (string Top, string Bottom, string Far, string Near, string Water)[]
        {
            ("#23305E", "#E7A86B", "#4A5378", "#232A42", "#5B6E96"),
            ("#7FB4D8", "#D9ECF4", "#6C8797", "#3C5260", "#93C2D2"),
            ("#E9B36A", "#F6DEB0", "#B9824E", "#7E5130", "#D9A877"),
            ("#86B48D", "#DCEBD2", "#4F7458", "#2D4834", "#7FA58C"),
            ("#5D6E91", "#E6B78E", "#535C78", "#2C3247", "#7889A6"),
            ("#A9D1B5", "#EEF4DE", "#6E9473", "#3F6046", "#9EC6B4"),
            ("#6F8FB3", "#D7E1EC", "#5D6E86", "#323C4C", "#8EA7C0"),
            ("#B9CCE0", "#F3F6FA", "#8B9DB2", "#5A6A7E", "#C8D7E6"),
        };
        var p = palettes[seed % palettes.Length];
        var rnd = new Random(seed + 11);
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var c = new SKCanvas(bmp))
        {
            var horizon = h * 0.66f;
            using (var sky = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, horizon), [SKColor.Parse(p.Top), SKColor.Parse(p.Bottom)], SKShaderTileMode.Clamp) })
                c.DrawRect(0, 0, w, horizon + 2, sky);
            using (var sun = new SKPaint { Color = new SKColor(255, 240, 210, 230), IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, h * 0.01f) })
                c.DrawCircle(w * (0.3f + 0.4f * (float)rnd.NextDouble()), horizon * 0.55f, h * 0.05f, sun);
            Ridge(c, rnd, w, horizon, h * 0.3f, SKColor.Parse(p.Far), 8);
            Ridge(c, rnd, w, horizon, h * 0.18f, SKColor.Parse(p.Near), 13);
            using (var water = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, horizon), new SKPoint(0, h), [SKColor.Parse(p.Water), SKColor.Parse(p.Near)], SKShaderTileMode.Clamp) })
                c.DrawRect(0, horizon, w, h - horizon, water);
            using (var shine = new SKPaint { Color = new SKColor(255, 255, 255, 70) })
                c.DrawRect(0, horizon, w, Math.Max(2, h / 300f), shine);
        }

        var bytes = new byte[bmp.ByteCount];
        System.Runtime.InteropServices.Marshal.Copy(bmp.GetPixels(), bytes, 0, bytes.Length);
        var noise = new Random(seed * 7919 + 1);
        for (var i = 0; i < bytes.Length; i += 4)
        {
            var n = noise.Next(-2, 3);
            for (var k = 0; k < 3; k++) bytes[i + k] = (byte)Math.Clamp(bytes[i + k] + n, 0, 255);
        }
        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, bmp.GetPixels(), bytes.Length);

        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, 88);
        using var f = File.Create(path);
        data.SaveTo(f);
    }

    private static void Ridge(SKCanvas c, Random rnd, int w, float baseY, float height, SKColor color, int peaks)
    {
        using var path = new SKPath();
        path.MoveTo(0, baseY);
        var step = (float)w / peaks;
        for (var i = 0; i <= peaks; i++)
        {
            var x = i * step;
            path.LineTo(x + step * 0.5f * (float)rnd.NextDouble(), baseY - height * (0.45f + 0.55f * (float)rnd.NextDouble()));
            path.LineTo(x + step * (0.6f + 0.4f * (float)rnd.NextDouble()), baseY - height * 0.25f * (float)rnd.NextDouble());
        }
        path.LineTo(w, baseY);
        path.Close();
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        c.DrawPath(path, paint);
    }
}

/// <summary>The standalone app's look: dark Fluent theme (the viewer draws its own chrome).</summary>
internal sealed class ShotApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }
}
