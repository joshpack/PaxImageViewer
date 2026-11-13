using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Media.Imaging;

namespace pxdImageViewer.App;

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
        // Never read or move the real settings folder from a test.
        var testData = Path.Combine(Path.GetTempPath(), "pxdImageViewer-selftest");
        try { if (Directory.Exists(testData)) Directory.Delete(testData, true); } catch { }
        Environment.SetEnvironmentVariable("PXDVIEW_DATA", testData);
        try
        {
            var sw = Stopwatch.StartNew();
            using var img = ImageLoader.Load(image);
            Check(img.Width > 0, $"decoded {Path.GetFileName(image)}: {img.Width}×{img.Height} {img.Format} in {sw.ElapsedMilliseconds} ms");

            var (files, index) = ImageFolder.Around(image);
            Check(index >= 0 && files.Count > 0, $"folder has {files.Count} images, this is #{index + 1}");

            var dir = Path.Combine(Path.GetTempPath(), "pxdImageViewer-selftest");
            ViewerSettings.FilePath = Path.Combine(dir, "settings.json");
            ViewerSettings.Load();
            ViewerSettings.Current.SlideSeconds = 7;
            ViewerSettings.Current.KeepZoom = true;
            ViewerSettings.SaveNow();
            ViewerSettings.Load();
            Check(ViewerSettings.Current.SlideSeconds == 7 && ViewerSettings.Current.KeepZoom, "settings save and load");

            // Settings folder from before the pxd rename: moved once, then left alone.
            var oldDir = Path.Combine(dir, ViewerMigration.FormerName(ViewerMigration.AppId));
            var newDir = Path.Combine(dir, ViewerMigration.AppId);
            Directory.CreateDirectory(oldDir);
            File.WriteAllText(Path.Combine(oldDir, "settings.json"), "{\"SlideSeconds\": 9}");
            var moved = ViewerMigration.Folder(oldDir, newDir);
            Check(moved && !Directory.Exists(oldDir) && File.Exists(Path.Combine(newDir, "settings.json")), "migration: old settings folder moved to the new name");
            Directory.CreateDirectory(oldDir);
            var again = ViewerMigration.Folder(oldDir, newDir);
            Check(!again && Directory.Exists(oldDir) && File.ReadAllText(Path.Combine(newDir, "settings.json")).Contains('9'), "migration: second run is a no-op");
            Check(!ViewerMigration.Folder(Path.Combine(dir, "missing"), Path.Combine(dir, "x")), "migration: nothing to move is a no-op");

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

            // Off the UI thread: pxdUpdate awaits without ConfigureAwait, and the Avalonia context here is never pumped.
            Task.Run(() => UpdateChecks(Path.Combine(dir, "update"), Check)).Wait();
            Directory.Delete(dir, true);
        }
        catch (Exception ex)
        {
            Check(false, "crashed: " + ex);
        }
        sb.AppendLine(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        return sb.ToString();
    }

    /// <summary>
    /// Update before start (pxdUpdate): never for --self-test, and a fake release on a local HttpListener: found,
    /// a bad checksum changes nothing, the files in a temp app folder are swapped. Nothing real is updated or started.
    /// </summary>
    private static void UpdateChecks(string root, Action<bool, string> check)
    {
        check(!pxdStandard.pxdUpdate.BeforeStart(Program.Repo, Program.AssetPattern, ["--self-test"]), "update before start: never during --self-test");
        check(Regex.IsMatch("pxdImageViewer-1.1.1-win-x64.zip", Program.AssetPattern) && !Regex.IsMatch("pxdImageViewer-1.1.1-win-x64.zip.sha256", Program.AssetPattern),
            "update asset pattern matches the release zip only");

        var app = Path.Combine(root, "app");
        var pkg = Path.Combine(root, "pkg");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(pkg);
        File.WriteAllText(Path.Combine(app, "pxdImageViewer.exe"), "old exe");
        File.WriteAllText(Path.Combine(app, "libSkiaSharp.dll"), "old lib");
        File.WriteAllText(Path.Combine(pkg, "pxdImageViewer.exe"), "new exe");
        File.WriteAllText(Path.Combine(pkg, "libSkiaSharp.dll"), "new lib");
        var zip = Path.Combine(root, "pxdImageViewer-9.9.9-win-x64.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(pkg, zip);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(zip))).ToLowerInvariant();

        Environment.SetEnvironmentVariable("PXD_UPDATE_API", null);
        check(pxdStandard.pxdUpdate.ApiBases().SequenceEqual(["https://pxdtek.com/gh", "https://api.github.com"]), "update: release info from pxdtek.com first, GitHub as the fallback");
        check(pxdStandard.pxdUpdate.GitHubCopy("https://pxdtek.com/dl/pxdImageViewer/v1.1.2/pxdImageViewer-1.1.2-win-x64.zip") ==
              "https://github.com/pxdtek/pxdImageViewer/releases/download/v1.1.2/pxdImageViewer-1.1.2-win-x64.zip", "update: a pxdtek.com download falls back to the same file on GitHub");

        var badSum = false;
        var blocked = false;
        var prefix = $"http://127.0.0.1:{new Random().Next(41000, 49000)}/";
        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        var serving = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                System.Net.HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch { return; }
                if (blocked && ctx.Request.Url!.AbsolutePath is "/repos/pxdtek/pxdImageViewer/releases/latest" or "/zip")
                {
                    // What a web filter answers instead of the real thing.
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    try { await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("<!DOCTYPE html><html><body>Blocked by your network.</body></html>")); } catch { }
                    ctx.Response.Close();
                    continue;
                }
                byte[] body = ctx.Request.Url!.AbsolutePath switch
                {
                    "/repos/pxdtek/pxdImageViewer/releases/latest" => Encoding.UTF8.GetBytes(
                        $"{{\"tag_name\":\"v9.9.9\",\"assets\":[{{\"name\":\"pxdImageViewer-9.9.9-win-x64.zip\",\"browser_download_url\":\"{prefix}zip\"}}," +
                        $"{{\"name\":\"pxdImageViewer-9.9.9-win-x64.zip.sha256\",\"browser_download_url\":\"{prefix}sum\"}}]}}"),
                    "/zip" => File.ReadAllBytes(zip),
                    "/sum" => Encoding.ASCII.GetBytes((badSum ? new string('0', 64) : hash) + "  pxdImageViewer-9.9.9-win-x64.zip"),
                    _ => [],
                };
                ctx.Response.StatusCode = body.Length == 0 ? 404 : 200;
                try { await ctx.Response.OutputStream.WriteAsync(body); } catch { }
                ctx.Response.Close();
            }
        });

        Environment.SetEnvironmentVariable("PXD_UPDATE_API", prefix.TrimEnd('/'));
        try
        {
            var plan = pxdStandard.pxdUpdate.FindUpdateAsync(Program.Repo, Program.AssetPattern, new Version(1, 1, 1)).GetAwaiter().GetResult();
            check(plan is { Sum: not null }, "update: newer release found (with checksum)");
            check(pxdStandard.pxdUpdate.FindUpdateAsync(Program.Repo, Program.AssetPattern, new Version(9, 9, 9)).GetAwaiter().GetResult() is null, "update: current copy left alone");
            var exe = Path.Combine(app, "pxdImageViewer.exe");
            blocked = true;
            check(pxdStandard.pxdUpdate.FindUpdateAsync(Program.Repo, Program.AssetPattern, new Version(1, 1, 1)).GetAwaiter().GetResult() is null,
                "update: a block page is never read as release info");
            var notApplied = !pxdStandard.pxdUpdate.ApplyAsync(plan!.Value.Zip, plan.Value.Sum, app, exe, [], start: false).GetAwaiter().GetResult();
            check(notApplied && File.ReadAllText(exe) == "old exe" && Directory.GetFiles(app, "*.pxdold").Length == 0, "update: a block page instead of the zip changes nothing");
            blocked = false;
            badSum = true;
            var refused = !pxdStandard.pxdUpdate.ApplyAsync(plan!.Value.Zip, plan.Value.Sum, app, exe, [], start: false).GetAwaiter().GetResult();
            check(refused && File.ReadAllText(exe) == "old exe", "update: a bad checksum changes nothing");
            badSum = false;
            var ok = pxdStandard.pxdUpdate.ApplyAsync(plan.Value.Zip, plan.Value.Sum, app, exe, [], start: false).GetAwaiter().GetResult();
            check(ok && File.ReadAllText(exe) == "new exe" && File.ReadAllText(Path.Combine(app, "libSkiaSharp.dll")) == "new lib", "update: exe and native libs swapped");
            pxdStandard.pxdUpdate.CleanUp(app);
            check(Directory.GetFiles(app, "*.pxdold").Length == 0, "update: set-aside files removed at next start");
            check(pxdStandard.pxdUpdate.ThisVersion() >= new Version(1, 1, 1), $"update: this copy reads its own version ({pxdStandard.pxdUpdate.ThisVersion()})");
        }
        catch (Exception ex)
        {
            check(false, "update checks crashed: " + ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PXD_UPDATE_API", null);
            listener.Stop();
            serving.Wait(2000);
        }
    }
}
