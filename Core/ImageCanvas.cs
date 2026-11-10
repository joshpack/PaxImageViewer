using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace pxdImageViewer;

/// <summary>Zoom and position of a view, kept relative so it carries over to an image of another size.</summary>
public readonly record struct ViewState(bool Fit, double Zoom, double CenterX, double CenterY);

/// <summary>
/// Draws one image at any zoom, from a sliver up to 512 image pixels per screen pixel. Only the visible part of the
/// image is sampled, so deep zoom stays fast and precise. Shrunk images use high-quality (mipmapped) filtering, gentle
/// enlargements are smoothed, and from <see cref="ViewerSettings.PixelZoom"/> up pixels are shown as crisp squares,
/// with a grid between them when they're big enough to inspect one by one.
/// </summary>
public sealed class ImageCanvas : Control
{
    public const double MaxZoom = 512;

    private ViewerImage? _image;
    private int _frame;
    private DispatcherTimer? _animation;

    // Zoom is in device pixels per image pixel (1 = 100%: one image pixel per screen pixel, whatever the DPI).
    private double _zoom = 1;
    private Point _center; // image coordinates at the middle of the view
    private bool _fit = true;

    // Smooth zoom: log-zoom eases from → to while one image point stays under the pointer.
    private double _zoomFrom, _zoomTo;
    private Point _anchorScreen, _anchorImage;
    private DateTime _zoomStart;
    private bool _zooming;
    private const double ZoomMs = 140;

    // Cross-fade from the previous image.
    private (WriteableBitmap Bitmap, Rect Dest, Rect Src)? _fadeFrom;
    private DateTime _fadeStart;
    private const double FadeMs = 280;
    private bool _frameQueued;

    // Panning.
    private Point? _dragFrom;
    private Point _dragCenter;

    public ImageCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Arrow);
    }

    public ViewerImage? Image => _image;
    public bool IsFit => _fit;
    /// <summary>Device pixels per image pixel (1 = 100%).</summary>
    public double Zoom => _zoom;

    /// <summary>The zoom or position changed (for the % readout).</summary>
    public event Action? ViewChanged;

    private double Scaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
    /// <summary>DIPs per image pixel.</summary>
    private double K => _zoom / Scaling;

    public ViewState Capture() =>
        _image is null ? new ViewState(true, 1, 0.5, 0.5) : new ViewState(_fit, _zoom, _center.X / _image.Width, _center.Y / _image.Height);

    /// <summary>
    /// Shows <paramref name="image"/>. With <paramref name="keep"/> the zoom and the relative spot carry over;
    /// otherwise it fits. <paramref name="fade"/> cross-fades from what was there.
    /// </summary>
    public void Show(ViewerImage? image, ViewState? keep = null, bool fade = false)
    {
        if (fade && _image is not null && Bounds.Width > 0 && CurrentRects() is { } r)
        {
            _fadeFrom = (_image.Frames[_frame], r.Dest, r.Src);
            _fadeStart = DateTime.UtcNow;
        }
        else _fadeFrom = null;

        StopAnimation();
        _image = image;
        _frame = 0;
        _zooming = false;
        if (image is null)
        {
            InvalidateVisual();
            ViewChanged?.Invoke();
            return;
        }

        if (keep is { Fit: false } k)
        {
            _fit = false;
            _zoom = Math.Clamp(k.Zoom, MinZoom, MaxZoom);
            _center = new Point(k.CenterX * image.Width, k.CenterY * image.Height);
            Clamp();
        }
        else
        {
            _fit = true;
            ApplyFit();
        }
        if (image.IsAnimated) StartAnimation();
        if (_fadeFrom is not null) QueueFrame();
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    /// <summary>The image's frames were replaced (rotated): fit the new shape.</summary>
    public void Reset()
    {
        _fadeFrom = null;
        _frame = 0;
        _fit = true;
        ApplyFit();
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    // ---- Zoom ------------------------------------------------------------------------------------------------------

    public double FitZoom
    {
        get
        {
            if (_image is null || Bounds.Width <= 0 || Bounds.Height <= 0) return 1;
            var z = Math.Min(Bounds.Width / _image.Width, Bounds.Height / _image.Height) * Scaling;
            return ViewerSettings.Current.EnlargeSmall ? z : Math.Min(z, 1);
        }
    }

    private double MinZoom => Math.Max(0.001, Math.Min(FitZoom, 1) / 8);

    public void Fit()
    {
        if (_image is null) return;
        if (_fit) return;
        AnimateTo(FitZoom, null, fit: true);
    }

    /// <summary>100%: one image pixel per screen pixel, around <paramref name="at"/> (view coordinates) or the middle.</summary>
    public void ActualSize(Point? at = null) => AnimateTo(1, at);

    public void ZoomBy(double factor, Point? at = null)
    {
        if (_image is null) return;
        var from = _zooming ? _zoomTo : _zoom;
        var target = from * factor;
        // Snap through the interesting stops (fit and 100%) instead of jumping past them.
        foreach (var stop in new[] { FitZoom, 1.0 })
            if ((from < stop && target > stop) || (from > stop && target < stop))
            {
                target = stop;
                break;
            }
        AnimateTo(target, at);
    }

    /// <summary>Zooms to <paramref name="zoom"/> keeping the image point under <paramref name="at"/> in place.</summary>
    public void AnimateTo(double zoom, Point? at, bool fit = false)
    {
        if (_image is null) return;
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        var screen = at ?? new Point(Bounds.Width / 2, Bounds.Height / 2);
        _anchorScreen = screen;
        _anchorImage = fit ? new Point(_image.Width / 2.0, _image.Height / 2.0) : ToImage(screen);
        if (fit) _anchorScreen = new Point(Bounds.Width / 2, Bounds.Height / 2);
        _zoomFrom = _zoom;
        _zoomTo = zoom;
        _fitAfterZoom = fit;
        _zoomStart = DateTime.UtcNow;
        _zooming = true;
        _fit = false;
        QueueFrame();
    }

    private bool _fitAfterZoom;

    /// <summary>Sets the zoom straight away (no easing); used by tests and keyboard repeat.</summary>
    public void SetZoom(double zoom, Point? at = null)
    {
        AnimateTo(zoom, at);
        StepZoom(1);
    }

    private void StepZoom(double t)
    {
        var e = 1 - Math.Pow(1 - t, 3); // ease-out
        _zoom = Math.Exp(Math.Log(_zoomFrom) + (Math.Log(_zoomTo) - Math.Log(_zoomFrom)) * e);
        var k = K;
        _center = new Point(
            _anchorImage.X - (_anchorScreen.X - Bounds.Width / 2) / k,
            _anchorImage.Y - (_anchorScreen.Y - Bounds.Height / 2) / k);
        Clamp();
        if (t >= 1)
        {
            _zooming = false;
            if (_fitAfterZoom) _fit = true;
        }
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    private void ApplyFit()
    {
        if (_image is null) return;
        _zoom = FitZoom;
        _center = new Point(_image.Width / 2.0, _image.Height / 2.0);
    }

    /// <summary>Keeps the image on screen: centred on an axis where it fits, edges no further in than the view's.</summary>
    private void Clamp()
    {
        if (_image is null) return;
        var k = K;
        double Axis(double c, double size, double view)
        {
            var half = view / 2 / k;
            return size * k <= view ? size / 2 : Math.Clamp(c, half, size - half);
        }
        _center = new Point(Axis(_center.X, _image.Width, Bounds.Width), Axis(_center.Y, _image.Height, Bounds.Height));
    }

    public Point ToImage(Point view)
    {
        var k = K;
        return new Point((view.X - Bounds.Width / 2) / k + _center.X, (view.Y - Bounds.Height / 2) / k + _center.Y);
    }

    /// <summary>Pans by a distance in view coordinates (keyboard).</summary>
    public void PanBy(double dx, double dy)
    {
        if (_image is null) return;
        _fit = false;
        _center = new Point(_center.X + dx / K, _center.Y + dy / K);
        Clamp();
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    public bool CanPan => _image is not null && (_image.Width * K > Bounds.Width + 0.5 || _image.Height * K > Bounds.Height + 0.5);

    // ---- Frames ----------------------------------------------------------------------------------------------------

    private void QueueFrame()
    {
        if (_frameQueued) return;
        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            // Not on screen yet: jump to the end state.
            if (_zooming) StepZoom(1);
            _fadeFrom = null;
            return;
        }
        _frameQueued = true;
        top.RequestAnimationFrame(_ =>
        {
            _frameQueued = false;
            var now = DateTime.UtcNow;
            if (_zooming) StepZoom(Math.Min(1, (now - _zoomStart).TotalMilliseconds / ZoomMs));
            if (_fadeFrom is not null && (now - _fadeStart).TotalMilliseconds >= FadeMs) _fadeFrom = null;
            InvalidateVisual();
            if (_zooming || _fadeFrom is not null) QueueFrame();
        });
    }

    private void StartAnimation()
    {
        if (_image is null) return;
        _animation = new DispatcherTimer { Interval = _image.Durations[0] };
        _animation.Tick += (_, _) =>
        {
            if (_image is not { IsAnimated: true } img) return;
            _frame = (_frame + 1) % img.Frames.Count;
            _animation!.Interval = img.Durations[_frame];
            InvalidateVisual();
        };
        _animation.Start();
    }

    private void StopAnimation()
    {
        _animation?.Stop();
        _animation = null;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_fit) ApplyFit();
        else Clamp();
        ViewChanged?.Invoke();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        StopAnimation();
    }

    // ---- Drawing ---------------------------------------------------------------------------------------------------

    /// <summary>Where the image lands (view coordinates, clipped to the view) and the image area that fills it.</summary>
    private (Rect Dest, Rect Src)? CurrentRects()
    {
        if (_image is null) return null;
        var s = Scaling;
        var k = K;
        // Snap the image's corner to a device pixel so 100% and whole-number zooms are pixel exact.
        var left = Math.Round((Bounds.Width / 2 - _center.X * k) * s) / s;
        var top = Math.Round((Bounds.Height / 2 - _center.Y * k) * s) / s;
        var full = new Rect(left, top, _image.Width * k, _image.Height * k);
        var visible = full.Intersect(new Rect(Bounds.Size));
        if (visible.Width <= 0 || visible.Height <= 0) return null;
        var src = new Rect((visible.X - left) / k, (visible.Y - top) / k, visible.Width / k, visible.Height / k);
        return (visible, src);
    }

    public override void Render(DrawingContext ctx)
    {
        var bounds = new Rect(Bounds.Size);
        ctx.FillRectangle(ViewerTheme.Back, bounds);
        if (_image is null) return;

        var fade = 1.0;
        if (_fadeFrom is { } from)
        {
            fade = Math.Clamp((DateTime.UtcNow - _fadeStart).TotalMilliseconds / FadeMs, 0, 1);
            using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
            using (ctx.PushOpacity(1 - fade))
                ctx.DrawImage(from.Bitmap, from.Src, from.Dest);
        }

        if (CurrentRects() is not { } r) return;
        using (ctx.PushOpacity(fade))
        {
            if (_image.HasAlpha && ViewerSettings.Current.Checkerboard) DrawChecker(ctx, r.Dest);

            var settings = ViewerSettings.Current;
            // 100% is drawn 1:1 (nothing to filter); far in, pixels become sharp squares instead of a blur.
            var crisp = _zoom >= settings.PixelZoom - 1e-9 || Math.Abs(_zoom - 1) < 1e-6;
            var mode = crisp ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality;
            using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = mode }))
                ctx.DrawImage(_image.Frames[_frame], r.Src, r.Dest);

            if (settings.PixelGrid && _zoom >= 12) DrawPixelGrid(ctx, r.Dest, r.Src);
        }
    }

    private static void DrawChecker(DrawingContext ctx, Rect area)
    {
        const double cell = 10;
        ctx.FillRectangle(ViewerTheme.CheckA, area);
        using (ctx.PushClip(area))
        {
            var x0 = Math.Floor(area.X / cell) * cell;
            var y0 = Math.Floor(area.Y / cell) * cell;
            for (var y = y0; y < area.Bottom; y += cell)
                for (var x = x0 + ((int)Math.Round(y / cell) % 2 == 0 ? 0 : cell); x < area.Right; x += cell * 2)
                    ctx.FillRectangle(ViewerTheme.CheckB, new Rect(x, y, cell, cell));
        }
    }

    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), 1);
    private static readonly IPen GridPenLight = new Pen(new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)), 1);

    private void DrawPixelGrid(DrawingContext ctx, Rect dest, Rect src)
    {
        var k = K;
        var s = Scaling;
        // Pixel edges fall on whole image coordinates; snap each line to the device grid so it stays one pixel thin.
        double Snap(double v) => (Math.Floor(v * s) + 0.5) / s;
        var fx = Math.Ceiling(src.X);
        var fy = Math.Ceiling(src.Y);
        using (ctx.PushClip(dest))
        {
            for (var x = fx; x <= src.Right; x++)
            {
                var vx = Snap(dest.X + (x - src.X) * k);
                ctx.DrawLine(GridPen, new Point(vx, dest.Top), new Point(vx, dest.Bottom));
                ctx.DrawLine(GridPenLight, new Point(vx + 1 / s, dest.Top), new Point(vx + 1 / s, dest.Bottom));
            }
            for (var y = fy; y <= src.Bottom; y++)
            {
                var vy = Snap(dest.Y + (y - src.Y) * k);
                ctx.DrawLine(GridPen, new Point(dest.Left, vy), new Point(dest.Right, vy));
                ctx.DrawLine(GridPenLight, new Point(dest.Left, vy + 1 / s), new Point(dest.Right, vy + 1 / s));
            }
        }
    }

    // ---- Mouse -----------------------------------------------------------------------------------------------------

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_image is null) return;
        var d = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
        if (d == 0) return;
        ZoomBy(Math.Pow(1.25, d), e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var p = e.GetCurrentPoint(this);
        if (_image is null) return;
        if (p.Properties.IsLeftButtonPressed && e.ClickCount == 2)
        {
            // Double-click: fit ↔ 100% at the clicked spot (small images go to 4× instead).
            if (_fit || _zooming && _fitAfterZoom)
                AnimateTo(Math.Abs(FitZoom - 1) < 0.01 ? 4 : 1, p.Position);
            else Fit();
            e.Handled = true;
            return;
        }
        if ((p.Properties.IsLeftButtonPressed || p.Properties.IsMiddleButtonPressed) && CanPan)
        {
            _dragFrom = p.Position;
            _dragCenter = _center;
            _zooming = false;
            Cursor = new Cursor(StandardCursorType.SizeAll);
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragFrom is not { } from) return;
        var now = e.GetPosition(this);
        var k = K;
        _fit = false;
        _center = new Point(_dragCenter.X - (now.X - from.X) / k, _dragCenter.Y - (now.Y - from.Y) / k);
        Clamp();
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragFrom is null) return;
        _dragFrom = null;
        Cursor = new Cursor(StandardCursorType.Arrow);
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragFrom = null;
        Cursor = new Cursor(StandardCursorType.Arrow);
    }
}
