using System.Text.Json;
using System.Text.Json.Serialization;

namespace pxdImageViewer;

/// <summary>
/// The viewer's preferences, shared by the standalone app and every app that hosts the viewer (pxd Commander), so the
/// slideshow speed and zoom choices follow you. Saved as JSON in %APPDATA%\pxdImageViewer\settings.json.
/// </summary>
public sealed class ViewerSettings
{
    public static ViewerSettings Current { get; private set; } = new();

    /// <summary>Seconds each image stays up in a slideshow.</summary>
    public double SlideSeconds { get; set; } = 4;
    /// <summary>Moving to another image keeps the zoom level and the spot you were looking at, instead of fitting.</summary>
    public bool KeepZoom { get; set; }
    public bool Shuffle { get; set; }
    /// <summary>Slideshows start again at the first image after the last one.</summary>
    public bool Loop { get; set; } = true;
    /// <summary>Cross-fade between images in a slideshow.</summary>
    public bool Fade { get; set; } = true;
    /// <summary>Fit also enlarges images smaller than the window.</summary>
    public bool EnlargeSmall { get; set; }
    /// <summary>Zoom (image pixels per screen pixel) from which pixels are drawn as sharp squares, not smoothed.</summary>
    public double PixelZoom { get; set; } = 4;
    /// <summary>Draw a grid between pixels when zoomed far in (from 12×).</summary>
    public bool PixelGrid { get; set; } = true;
    /// <summary>A checkerboard behind transparent images.</summary>
    public bool Checkerboard { get; set; } = true;
    public Placement? Window { get; set; }

    public sealed class Placement
    {
        public int X { get; set; }
        public int Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool Maximized { get; set; }
    }

    /// <summary>Where the settings live; a host can point it elsewhere (tests use a throwaway folder).</summary>
    public static string FilePath { get; set; } =
        Environment.GetEnvironmentVariable("PXDVIEW_DATA") is { Length: > 0 } custom
            ? Path.Combine(custom, "settings.json")
            : Path.Combine(ViewerMigration.DataFolder(), "settings.json");

    private static bool _loaded;
    private static Timer? _timer;
    private static readonly object Gate = new();

    public static void Load()
    {
        _loaded = true;
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize(File.ReadAllText(FilePath), SettingsJson.Default.ViewerSettings) ?? new();
        }
        catch
        {
            Current = new();
        }
        Current.SlideSeconds = Math.Clamp(Current.SlideSeconds, 0.5, 600);
        Current.PixelZoom = Math.Clamp(Current.PixelZoom, 1, 64);
    }

    /// <summary>Loads once, the first time any viewer opens.</summary>
    public static void EnsureLoaded()
    {
        if (!_loaded) Load();
    }

    /// <summary>Coalesces bursts of changes (window drags, speed clicks) into one write.</summary>
    public static void SaveSoon()
    {
        _timer ??= new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
        _timer.Change(700, Timeout.Infinite);
    }

    public static void SaveNow()
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Current, SettingsJson.Default.ViewerSettings));
                File.Move(tmp, FilePath, true);
            }
            catch
            {
                // Preferences are a convenience; never let a locked file break the viewer.
            }
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ViewerSettings))]
internal sealed partial class SettingsJson : JsonSerializerContext;
