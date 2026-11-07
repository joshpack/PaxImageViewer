using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace PaxImageViewer;

/// <summary>A decoded image: one bitmap, or every frame of an animation with its delay.</summary>
public sealed class ViewerImage : IDisposable
{
    public required string Path { get; init; }
    public required IReadOnlyList<WriteableBitmap> Frames { get; set; }
    /// <summary>Delay after each frame (animations only).</summary>
    public IReadOnlyList<TimeSpan> Durations { get; init; } = [];
    public required string Format { get; init; }
    public bool HasAlpha { get; init; }
    public long FileSize { get; init; }
    public DateTime Modified { get; init; }

    public int Width => Frames[0].PixelSize.Width;
    public int Height => Frames[0].PixelSize.Height;
    public bool IsAnimated => Frames.Count > 1;

    /// <summary>Turns every frame a quarter turn, losslessly (pixels are moved, not resampled).</summary>
    public void Rotate(bool clockwise)
    {
        var origin = clockwise ? SKEncodedOrigin.RightTop : SKEncodedOrigin.LeftBottom;
        var turned = new List<WriteableBitmap>(Frames.Count);
        foreach (var f in Frames)
        {
            using var fb = f.Lock();
            var info = new SKImageInfo(fb.Size.Width, fb.Size.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var src = new SKBitmap();
            src.InstallPixels(info, fb.Address, fb.RowBytes);
            turned.Add(ImageLoader.Orient(src, origin));
        }
        foreach (var f in Frames) f.Dispose();
        Frames = turned;
    }

    public void Dispose()
    {
        foreach (var f in Frames) f.Dispose();
    }

    internal static WriteableBitmap NewBitmap(int w, int h) =>
        new(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
}
