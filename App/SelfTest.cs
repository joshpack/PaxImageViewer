using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Media.Imaging;

namespace PaxImageViewer.App;

/// <summary>
/// Exercises a build (especially the native AOT one, where trimming can break things the normal build never shows)
/// without opening a window: decode, folder listing, settings round-trip and an off-screen render of the viewer.
/// </summary>
internal static class SelfTest
{
    public static string Run(string image)
    {
        var sb = new StringBuilder();
        var fails = 0;
        void Check(bool ok, string what)
        {
            sb.AppendLine((ok ? "PASS " : "FAIL ") + what);
            if (!ok) fails++;
        }
        sb.AppendLine($"started {(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds:0} ms after launch");
        try
        {
            var sw = Stopwatch.StartNew();
            using var img = ImageLoader.Load(image);
            Check(img.Width > 0, $"decoded {Path.GetFileName(image)}: {img.Width}×{img.Height} {img.Format} in {sw.ElapsedMilliseconds} ms");

            var (files, index) = ImageFolder.Around(image);
            Check(index >= 0 && files.Count > 0, $"folder has {files.Count} images, this is #{index + 1}");

            var dir = Path.Combine(Path.GetTempPath(), "PaxImageViewer-selftest");
            ViewerSettings.FilePath = Path.Combine(dir, "settings.json");
            ViewerSettings.Load();
            ViewerSettings.Current.SlideSeconds = 7;
            ViewerSettings.Current.KeepZoom = true;
            ViewerSettings.SaveNow();
            ViewerSettings.Load();
            Check(ViewerSettings.Current.SlideSeconds == 7 && ViewerSettings.Current.KeepZoom, "settings save and load");

            var viewer = new ViewerControl();
            viewer.Measure(new Size(1000, 700));
            viewer.Arrange(new Rect(0, 0, 1000, 700));
            viewer.Canvas.Show(img);
            viewer.Canvas.SetZoom(8);
            viewer.Measure(new Size(1000, 700));
            viewer.Arrange(new Rect(0, 0, 1000, 700));
            using var rtb = new RenderTargetBitmap(new PixelSize(1000, 700));
            rtb.Render(viewer);
            var png = Path.Combine(dir, "render.png");
            rtb.Save(png);
            Check(new FileInfo(png).Length > 1000, $"viewer renders off-screen at 800% ({png})");
            viewer.Canvas.Show(null);
            Directory.Delete(dir, true);
        }
        catch (Exception ex)
        {
            Check(false, "crashed: " + ex);
        }
        sb.AppendLine(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        return sb.ToString();
    }
}
