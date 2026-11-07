using Avalonia.Media.Imaging;
using SkiaSharp;

namespace PaxImageViewer;

/// <summary>
/// Decodes image files at full resolution, straight into the bitmaps that get drawn (no extra copies). Skia handles the
/// common formats and honours the camera's rotation; on Windows, WIC covers the rest (TIFF, HEIC, AVIF, JPEG XL, camera
/// RAW) as far as the installed codecs allow.
/// </summary>
public static class ImageLoader
{
    /// <summary>Extensions the viewer offers to open and includes in a folder's slideshow.</summary>
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".apng", ".gif", ".bmp", ".dib", ".webp", ".ico", ".cur", ".wbmp",
        ".tif", ".tiff", ".heic", ".heif", ".hif", ".avif", ".jxl", ".jxr", ".wdp", ".hdp",
        ".dng", ".cr2", ".cr3", ".nef", ".nrw", ".arw", ".sr2", ".orf", ".rw2", ".raf", ".pef", ".srw",
    };

    public static bool IsImage(string path) => Extensions.Contains(System.IO.Path.GetExtension(path));

    /// <summary>All frames of an animation are kept only while they fit in this much memory; past it, frame one only.</summary>
    private const long AnimationBudget = 768L * 1024 * 1024;

    public static ViewerImage Load(string path, CancellationToken ct = default)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new FileNotFoundException("The file isn't there any more.", path);
        var bytes = File.ReadAllBytes(path);
        ct.ThrowIfCancellationRequested();

        Exception? skiaError = null;
        try
        {
            if (LoadWithSkia(path, bytes, fi, ct) is { } img) return img;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            skiaError = ex;
        }

        if (OperatingSystem.IsWindows())
        {
            var (bmp, format) = Wic.Decode(path);
            if (bmp is not null)
                return new ViewerImage
                {
                    Path = path, Frames = [bmp], Format = format, HasAlpha = true, FileSize = fi.Length, Modified = fi.LastWriteTime,
                };
        }
        throw skiaError ?? new NotSupportedException("This image format isn't supported" +
            (OperatingSystem.IsWindows() ? " (Windows may need its codec from the Microsoft Store)." : "."));
    }

    private static ViewerImage? LoadWithSkia(string path, byte[] bytes, FileInfo fi, CancellationToken ct)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null) return null;

        var w = codec.Info.Width;
        var h = codec.Info.Height;
        if (w <= 0 || h <= 0) return null;
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        var format = codec.EncodedFormat.ToString().ToUpperInvariant();
        var hasAlpha = codec.Info.AlphaType != SKAlphaType.Opaque;
        var origin = codec.EncodedOrigin;

        var frameCount = codec.FrameCount;
        var frameBytes = (long)w * h * 4;
        if (frameCount > 1 && frameCount * frameBytes <= AnimationBudget)
        {
            var frames = new List<WriteableBitmap>(frameCount);
            var durations = new List<TimeSpan>(frameCount);
            var frameInfo = codec.FrameInfo;
            var buffers = new SKBitmap[frameCount];
            try
            {
                for (var i = 0; i < frameCount; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var canvas = new SKBitmap(info);
                    var required = frameInfo[i].RequiredFrame;
                    if (required >= 0 && buffers[required] is { } prior) prior.CopyTo(canvas);
                    else canvas.Erase(SKColors.Transparent);
                    var r = codec.GetPixels(info, canvas.GetPixels(), new SKCodecOptions(i, required));
                    if (r is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) { canvas.Dispose(); break; }
                    buffers[i] = canvas;
                    frames.Add(Orient(canvas, origin));
                    var ms = frameInfo[i].Duration;
                    durations.Add(TimeSpan.FromMilliseconds(ms < 20 ? 100 : ms)); // browsers treat 0–10 ms as 100 ms
                }
            }
            finally
            {
                foreach (var b in buffers) b?.Dispose();
            }
            if (frames.Count > 0)
                return new ViewerImage
                {
                    Path = path, Frames = frames, Durations = durations, Format = format, HasAlpha = hasAlpha,
                    FileSize = fi.Length, Modified = fi.LastWriteTime,
                };
        }

        WriteableBitmap bmp;
        if (origin == SKEncodedOrigin.TopLeft)
        {
            // The common case: decode straight into the bitmap that gets drawn.
            bmp = ViewerImage.NewBitmap(w, h);
            using (var fb = bmp.Lock())
            {
                var r = codec.GetPixels(info, fb.Address, fb.RowBytes, SKCodecOptions.Default);
                if (r is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                {
                    bmp.Dispose();
                    throw new InvalidDataException("The image data is damaged (" + r + ").");
                }
            }
        }
        else
        {
            using var raw = new SKBitmap(info);
            var r = codec.GetPixels(info, raw.GetPixels());
            if (r is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                throw new InvalidDataException("The image data is damaged (" + r + ").");
            ct.ThrowIfCancellationRequested();
            bmp = Orient(raw, origin);
        }
        return new ViewerImage { Path = path, Frames = [bmp], Format = format, HasAlpha = hasAlpha, FileSize = fi.Length, Modified = fi.LastWriteTime };
    }

    /// <summary>Copies <paramref name="src"/> into a new bitmap turned/flipped the way an EXIF orientation says.</summary>
    internal static WriteableBitmap Orient(SKBitmap src, SKEncodedOrigin origin)
    {
        int w = src.Width, h = src.Height;
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var dw = swap ? h : w;
        var dh = swap ? w : h;
        // x' = ScaleX·x + SkewX·y + TransX ; y' = SkewY·x + ScaleY·y + TransY
        var m = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
            _ => SKMatrix.Identity,
        };
        var bmp = ViewerImage.NewBitmap(dw, dh);
        using var fb = bmp.Lock();
        var dinfo = new SKImageInfo(dw, dh, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(dinfo, fb.Address, fb.RowBytes);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.SetMatrix(m);
        using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
        canvas.DrawBitmap(src, 0, 0, paint); // whole-pixel moves: no resampling, nothing lost
        canvas.Flush();
        return bmp;
    }
}
