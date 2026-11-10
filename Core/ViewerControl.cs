using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace pxdImageViewer;

/// <summary>
/// The whole viewer: the canvas, a title row, a floating toolbar, and the folder it steps through (with a slideshow).
/// Decoding happens off the UI thread and the neighbours of the current image are decoded ahead, so stepping and
/// slideshows don't wait.
/// </summary>
public sealed class ViewerControl : Grid
{
    private static readonly double[] Speeds = [0.5, 1, 1.5, 2, 3, 4, 5, 7, 10, 15, 20, 30, 45, 60, 120, 300];

    private readonly ImageCanvas _canvas = new();
    private readonly Border _top;
    private readonly TextBlock _title = new();
    private readonly TextBlock _counter = new();
    private readonly TextBlock _details = new();
    private readonly StackPanel _captionSlot = new() { Orientation = Orientation.Horizontal };
    private readonly Border _pill;
    private readonly Border _message;
    private readonly TextBlock _messageText = new();
    private readonly Border _info;
    private readonly TextBlock _infoText = new();
    private readonly Border _empty;
    private readonly Border _emptyOpen;
    private readonly TextBlock _zoomText = new();
    private readonly TextBlock _speedText = new();
    private readonly GlyphButton _play, _keepZoom, _shuffle, _grid, _infoButton, _full, _open;

    private List<string> _files = [];
    private List<int> _order = [];
    private int _pos = -1;
    private int _direction = 1;
    private int _token;
    private readonly Dictionary<string, Task<ViewerImage>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _slideTimer = new();
    private readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private bool _playing;
    private bool _fullScreen;
    private bool _overPill;

    /// <summary>Close the viewer (Esc when there's nothing else to back out of).</summary>
    public event Action? CloseRequested;
    /// <summary>Asks the host window to enter (true) or leave (false) full screen.</summary>
    public event Action<bool>? FullScreenRequested;
    /// <summary>The image on screen changed (path, or null): the host can follow along, e.g. select it in a list.</summary>
    public event Action<string?>? CurrentChanged;

    /// <summary>Lets the user pick a file (the standalone app shows the Open button only when this is set).</summary>
    public Func<Task<string?>>? PickFile
    {
        get => _pickFile;
        set
        {
            _pickFile = value;
            _open.IsVisible = value is not null;
            _emptyOpen.IsVisible = value is not null;
            UpdateEmpty();
        }
    }
    private Func<Task<string?>>? _pickFile;

    /// <summary>The title row: the host window drags itself by it.</summary>
    public Control TitleBar => _top;
    /// <summary>Where the host window puts its minimize / maximize / close buttons.</summary>
    public Panel CaptionSlot => _captionSlot;
    public ImageCanvas Canvas => _canvas;
    public string? CurrentPath => _pos >= 0 && _pos < _order.Count ? _files[_order[_pos]] : null;
    public int Count => _files.Count;
    public bool IsPlaying => _playing;

    public ViewerControl()
    {
        ViewerSettings.EnsureLoaded();
        Background = ViewerTheme.Back;
        RowDefinitions = new RowDefinitions("Auto,*");
        Focusable = true;

        // ---- Title row ----
        _title.Foreground = ViewerTheme.Text;
        _title.FontSize = 13;
        _title.FontWeight = FontWeight.SemiBold;
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        _title.VerticalAlignment = VerticalAlignment.Center;
        foreach (var t in new[] { _counter, _details })
        {
            t.Foreground = ViewerTheme.Muted;
            t.FontSize = 12;
            t.VerticalAlignment = VerticalAlignment.Center;
            t.Margin = new Thickness(12, 0, 0, 0);
        }
        var titles = new DockPanel { Margin = new Thickness(14, 0, 8, 0), LastChildFill = true };
        DockPanel.SetDock(_counter, Dock.Right);
        DockPanel.SetDock(_details, Dock.Right);
        titles.Children.Add(_details);
        titles.Children.Add(_counter);
        titles.Children.Add(_title);
        var topGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Height = 36 };
        topGrid.Children.Add(titles);
        Grid.SetColumn(_captionSlot, 1);
        topGrid.Children.Add(_captionSlot);
        _top = new Border { Background = ViewerTheme.Bar, Child = topGrid };
        Children.Add(_top);

        // ---- Image area ----
        var area = new Panel();
        Grid.SetRow(area, 1);
        Children.Add(area);
        area.Children.Add(_canvas);
        _canvas.ViewChanged += UpdateZoomText;

        _messageText.Foreground = ViewerTheme.Text;
        _messageText.TextWrapping = TextWrapping.Wrap;
        _messageText.TextAlignment = TextAlignment.Center;
        _messageText.MaxWidth = 460;
        _message = Card(_messageText);
        _message.HorizontalAlignment = HorizontalAlignment.Center;
        _message.VerticalAlignment = VerticalAlignment.Center;
        _message.IsVisible = false;
        _message.IsHitTestVisible = false;
        area.Children.Add(_message);

        _infoText.Foreground = ViewerTheme.Text;
        _infoText.FontSize = 12;
        _infoText.LineHeight = 19;
        _info = Card(_infoText);
        _info.HorizontalAlignment = HorizontalAlignment.Left;
        _info.VerticalAlignment = VerticalAlignment.Top;
        _info.Margin = new Thickness(14);
        _info.IsVisible = false;
        _info.IsHitTestVisible = false;
        area.Children.Add(_info);

        // ---- Empty state ----
        var emptyStack = new StackPanel { Spacing = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        emptyStack.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M3 5 H21 V19 H3 Z M3 16 L8.5 10.5 L13 15 L16 12 L21 17 M15.5 8.5 A1.5 1.5 0 1 1 15.49 8.5 Z"),
            Stroke = ViewerTheme.Accent,
            StrokeThickness = 1.4,
            StrokeJoin = PenLineJoin.Round,
            Width = 64,
            Height = 64,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        emptyStack.Children.Add(new TextBlock
        {
            Text = "Drop an image here",
            Foreground = ViewerTheme.Text,
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        emptyStack.Children.Add(new TextBlock
        {
            Text = "or press Ctrl+O to open one. Every image in its folder comes along for the slideshow.",
            Foreground = ViewerTheme.Muted,
            FontSize = 12.5,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        _emptyOpen = new Border
        {
            Child = new TextBlock { Text = "Open image…", Foreground = ViewerTheme.Back, FontWeight = FontWeight.SemiBold, FontSize = 13 },
            Background = ViewerTheme.Accent,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 8),
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            IsVisible = false,
        };
        _emptyOpen.PointerReleased += (_, e) =>
        {
            e.Handled = true;
            _ = PickAndOpenAsync();
        };
        emptyStack.Children.Add(_emptyOpen);
        _empty = new Border { Child = emptyStack, Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        area.Children.Add(_empty);

        // ---- Toolbar ----
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        bar.Children.Add(new GlyphButton(Glyphs.Prev, "Previous (Left)", () => Step(-1)));
        _play = new GlyphButton(Glyphs.Play, "Slideshow (Space)", ToggleSlideshow);
        _play.SetFilled(true);
        bar.Children.Add(_play);
        bar.Children.Add(new GlyphButton(Glyphs.Next, "Next (Right)", () => Step(1)));
        bar.Children.Add(Separator());

        bar.Children.Add(new GlyphButton(Glyphs.Minus, "Slower slideshow ( [ )", () => ChangeSpeed(+1), 28));
        _speedText.Foreground = ViewerTheme.Text;
        _speedText.FontSize = 12.5;
        _speedText.FontWeight = FontWeight.SemiBold;
        _speedText.VerticalAlignment = VerticalAlignment.Center;
        _speedText.HorizontalAlignment = HorizontalAlignment.Center;
        var speed = new Border { Child = _speedText, MinWidth = 42, Background = Brushes.Transparent };
        ToolTip.SetTip(speed, "Seconds per image (scroll to change)");
        speed.PointerWheelChanged += (_, e) =>
        {
            ChangeSpeed(e.Delta.Y > 0 ? -1 : 1);
            e.Handled = true;
        };
        bar.Children.Add(speed);
        bar.Children.Add(new GlyphButton(Glyphs.Plus, "Faster slideshow ( ] )", () => ChangeSpeed(-1), 28));
        _keepZoom = new GlyphButton(Glyphs.Lock, "Keep zoom between images (K)", ToggleKeepZoom);
        bar.Children.Add(_keepZoom);
        _shuffle = new GlyphButton(Glyphs.Shuffle, "Shuffle (S)", ToggleShuffle);
        bar.Children.Add(_shuffle);
        bar.Children.Add(Separator());

        bar.Children.Add(new GlyphButton(Glyphs.ZoomOut, "Zoom out (−)", () => _canvas.ZoomBy(1 / 1.5)));
        _zoomText.Foreground = ViewerTheme.Text;
        _zoomText.FontSize = 12.5;
        _zoomText.FontWeight = FontWeight.SemiBold;
        _zoomText.VerticalAlignment = VerticalAlignment.Center;
        _zoomText.HorizontalAlignment = HorizontalAlignment.Center;
        var zoomBox = new Border { Child = _zoomText, MinWidth = 58, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand) };
        ToolTip.SetTip(zoomBox, "Click: fit ↔ 100%");
        zoomBox.PointerReleased += (_, e) =>
        {
            ToggleFitActual();
            e.Handled = true;
        };
        bar.Children.Add(zoomBox);
        bar.Children.Add(new GlyphButton(Glyphs.ZoomIn, "Zoom in (+)", () => _canvas.ZoomBy(1.5)));
        bar.Children.Add(new GlyphButton(Glyphs.Fit, "Fit to window (0)", _canvas.Fit));
        bar.Children.Add(new TextButton("1:1", "Actual size (1)", () => _canvas.ActualSize()));
        bar.Children.Add(Separator());

        bar.Children.Add(new GlyphButton(Glyphs.RotateLeft, "Rotate left (Shift+R)", () => Rotate(false)));
        bar.Children.Add(new GlyphButton(Glyphs.RotateRight, "Rotate right (R)", () => Rotate(true)));
        _grid = new GlyphButton(Glyphs.Grid, "Pixel grid when zoomed far in (G)", TogglePixelGrid);
        bar.Children.Add(_grid);
        _infoButton = new GlyphButton(Glyphs.Info, "Details (I)", ToggleInfo);
        bar.Children.Add(_infoButton);
        _full = new GlyphButton(Glyphs.FullScreen, "Full screen (F)", () => FullScreenRequested?.Invoke(!_fullScreen));
        bar.Children.Add(_full);
        _open = new GlyphButton(Glyphs.Open, "Open… (Ctrl+O)", () => _ = PickAndOpenAsync()) { IsVisible = false };
        bar.Children.Add(_open);

        _pill = new Border
        {
            Child = bar,
            Background = ViewerTheme.Pill,
            BorderBrush = ViewerTheme.PillBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(6, 4),
            Margin = new Thickness(12, 0, 12, 18),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(180) }],
        };
        _pill.PointerEntered += (_, _) => _overPill = true;
        _pill.PointerExited += (_, _) => _overPill = false;
        area.Children.Add(_pill);

        _slideTimer.Tick += (_, _) =>
        {
            _slideTimer.Stop();
            if (_playing) _ = AdvanceSlideshowAsync();
        };
        _idleTimer.Tick += (_, _) =>
        {
            _idleTimer.Stop();
            if (!_overPill && _files.Count > 0) SetChromeVisible(false);
        };

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = DroppedPath(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        });
        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (DroppedPath(e) is { } p) Open(p);
            e.Handled = true;
        });

        RefreshToggles();
        UpdateEmpty();
    }

    // ---- Opening -----------------------------------------------------------------------------------------------------

    /// <summary>Opens an image (with its folder's other images to step through), or a folder's first image.</summary>
    public void Open(string path, bool slideshow = false)
    {
        if (Directory.Exists(path))
        {
            var files = ImageFolder.List(path);
            if (files.Count == 0)
            {
                ShowMessage("There are no images in " + Path.GetFileName(path.TrimEnd('\\', '/')) + ".");
                return;
            }
            Open(files, 0, slideshow);
            return;
        }
        var (list, index) = ImageFolder.Around(path);
        Open(list, index, slideshow);
    }

    /// <summary>Opens <paramref name="files"/> (already in the order to show them) at <paramref name="index"/>.</summary>
    public void Open(IReadOnlyList<string> files, int index, bool slideshow = false)
    {
        foreach (var t in _cache.Values) DisposeLater(t);
        _cache.Clear();
        _files = files.ToList();
        if (_files.Count == 0) return;
        index = Math.Clamp(index, 0, _files.Count - 1);
        BuildOrder(index);
        _pos = 0;
        _direction = 1;
        _ = ShowCurrentAsync(fade: false, keep: null);
        if (slideshow) StartSlideshow();
        UpdateEmpty();
    }

    private async Task PickAndOpenAsync()
    {
        if (PickFile is null) return;
        var picked = await PickFile();
        if (picked is not null) Open(picked);
    }

    private static string? DroppedPath(DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        if (files is null) return null;
        foreach (var f in files)
            if (f.TryGetLocalPath() is { } p && (Directory.Exists(p) || ImageLoader.IsImage(p)))
                return p;
        return null;
    }

    private void BuildOrder(int startIndex)
    {
        _order = Enumerable.Range(0, _files.Count).ToList();
        if (!ViewerSettings.Current.Shuffle)
        {
            _pos = startIndex;
            return;
        }
        // Shuffle everything, but begin with what's on screen.
        var rng = Random.Shared;
        for (var i = _order.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (_order[i], _order[j]) = (_order[j], _order[i]);
        }
        var at = _order.IndexOf(startIndex);
        (_order[0], _order[at]) = (_order[at], _order[0]);
        _pos = 0;
    }

    // ---- Stepping ----------------------------------------------------------------------------------------------------

    public void Step(int delta)
    {
        if (_order.Count == 0) return;
        var next = _pos + delta;
        if (next < 0 || next >= _order.Count)
        {
            if (_order.Count == 1) return;
            next = (next % _order.Count + _order.Count) % _order.Count; // wrap around
        }
        GoTo(next, delta < 0 ? -1 : 1);
    }

    public void First() => GoTo(0, 1);
    public void Last() => GoTo(_order.Count - 1, -1);

    private void GoTo(int pos, int direction, bool fade = false)
    {
        if (pos < 0 || pos >= _order.Count) return;
        var keep = ViewerSettings.Current.KeepZoom && !_canvas.IsFit && _canvas.Image is not null ? _canvas.Capture() : (ViewState?)null;
        _pos = pos;
        _direction = direction;
        _ = ShowCurrentAsync(fade, keep);
        if (_playing) _slideTimer.Stop(); // restarts once this image is up
    }

    private Task<ViewerImage> Load(string path)
    {
        if (_cache.TryGetValue(path, out var t)) return t;
        t = Task.Run(() => ImageLoader.Load(path));
        _cache[path] = t;
        return t;
    }

    private async Task ShowCurrentAsync(bool fade, ViewState? keep)
    {
        var token = ++_token;
        var path = CurrentPath;
        if (path is null) return;
        _title.Text = Path.GetFileName(path);
        _counter.Text = $"{_order[_pos] + 1} / {_files.Count}";
        CurrentChanged?.Invoke(path);

        var task = Load(path);
        if (!task.IsCompleted)
        {
            // Only say "Loading" when it's slow enough to notice.
            _ = Task.Delay(180).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
            {
                if (token == _token && !task.IsCompleted) ShowMessage("Loading " + Path.GetFileName(path) + "…");
            }));
        }

        ViewerImage? image = null;
        string? error = null;
        try
        {
            image = await task;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _cache.Remove(path); // try again next time (the file may have been mid-copy)
        }
        if (token != _token) return;

        if (image is null)
        {
            _canvas.Show(null);
            _details.Text = "";
            ShowMessage($"Couldn't open {Path.GetFileName(path)}\n{error}");
            if (_playing) RestartSlideTimer(TimeSpan.FromSeconds(Math.Min(1.5, ViewerSettings.Current.SlideSeconds)));
        }
        else
        {
            HideMessage();
            _canvas.Show(image, keep, fade && ViewerSettings.Current.Fade);
            _details.Text = $"{image.Width:N0} × {image.Height:N0}   {image.Format}   {FormatSize(image.FileSize)}";
            if (_playing) RestartSlideTimer(TimeSpan.FromSeconds(ViewerSettings.Current.SlideSeconds));
        }
        UpdateInfo();
        Prefetch();
    }

    /// <summary>Decodes the images either side of the current one, and lets go of the rest.</summary>
    private void Prefetch()
    {
        if (_order.Count == 0) return;
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { CurrentPath! };
        foreach (var d in new[] { _direction, -_direction })
        {
            var p = _pos + d;
            if (p < 0 || p >= _order.Count) p = (p % _order.Count + _order.Count) % _order.Count;
            wanted.Add(_files[_order[p]]);
        }
        foreach (var key in _cache.Keys.Where(k => !wanted.Contains(k)).ToList())
        {
            DisposeLater(_cache[key]);
            _cache.Remove(key);
        }
        foreach (var w in wanted) Load(w);
    }

    /// <summary>Frees an image a moment later, after any cross-fade from it has finished drawing.</summary>
    private void DisposeLater(Task<ViewerImage> task)
    {
        task.ContinueWith(t =>
        {
            if (t.Status != TaskStatus.RanToCompletion) return;
            DispatcherTimer.RunOnce(() =>
            {
                if (!ReferenceEquals(_canvas.Image, t.Result)) t.Result.Dispose();
            }, TimeSpan.FromSeconds(1));
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ---- Slideshow ---------------------------------------------------------------------------------------------------

    public void ToggleSlideshow()
    {
        if (_playing) StopSlideshow();
        else StartSlideshow();
    }

    public void StartSlideshow()
    {
        if (_files.Count == 0) return;
        _playing = true;
        RestartSlideTimer(TimeSpan.FromSeconds(ViewerSettings.Current.SlideSeconds));
        RefreshToggles();
        PokeChrome();
    }

    public void StopSlideshow()
    {
        _playing = false;
        _slideTimer.Stop();
        RefreshToggles();
        SetChromeVisible(true);
    }

    private void RestartSlideTimer(TimeSpan after)
    {
        _slideTimer.Stop();
        _slideTimer.Interval = after;
        _slideTimer.Start();
    }

    private async Task AdvanceSlideshowAsync()
    {
        if (_order.Count == 0) return;
        var next = _pos + 1;
        if (next >= _order.Count)
        {
            if (!ViewerSettings.Current.Loop)
            {
                StopSlideshow();
                return;
            }
            if (ViewerSettings.Current.Shuffle) BuildOrder(_order[_pos]);
            next = ViewerSettings.Current.Shuffle ? 1 % _order.Count : 0;
        }
        // Wait for the next image to finish decoding, so every slide gets its full time on screen.
        var path = _files[_order[next]];
        try { await Load(path); } catch { }
        if (!_playing) return;
        GoTo(next, 1, fade: true);
    }

    private void ChangeSpeed(int dir)
    {
        var s = ViewerSettings.Current;
        var i = Array.FindIndex(Speeds, v => v >= s.SlideSeconds - 1e-9);
        if (i < 0) i = Speeds.Length - 1;
        s.SlideSeconds = Speeds[Math.Clamp(i + dir, 0, Speeds.Length - 1)];
        ViewerSettings.SaveSoon();
        RefreshToggles();
        if (_playing) RestartSlideTimer(TimeSpan.FromSeconds(s.SlideSeconds));
    }

    // ---- Toggles -----------------------------------------------------------------------------------------------------

    private void ToggleKeepZoom()
    {
        ViewerSettings.Current.KeepZoom = !ViewerSettings.Current.KeepZoom;
        ViewerSettings.SaveSoon();
        RefreshToggles();
        Toast(ViewerSettings.Current.KeepZoom ? "Zoom is kept between images" : "Each image fits the window");
    }

    private void ToggleShuffle()
    {
        ViewerSettings.Current.Shuffle = !ViewerSettings.Current.Shuffle;
        ViewerSettings.SaveSoon();
        if (_order.Count > 0) BuildOrder(_order[_pos]);
        if (_order.Count > 0) _counter.Text = $"{_order[_pos] + 1} / {_files.Count}";
        RefreshToggles();
        Prefetch();
        Toast(ViewerSettings.Current.Shuffle ? "Shuffle on" : "Shuffle off");
    }

    private void TogglePixelGrid()
    {
        ViewerSettings.Current.PixelGrid = !ViewerSettings.Current.PixelGrid;
        ViewerSettings.SaveSoon();
        RefreshToggles();
        _canvas.InvalidateVisual();
    }

    private void ToggleInfo()
    {
        _info.IsVisible = !_info.IsVisible;
        UpdateInfo();
        RefreshToggles();
    }

    private void ToggleFitActual()
    {
        if (_canvas.IsFit) _canvas.ActualSize();
        else _canvas.Fit();
    }

    private void Rotate(bool clockwise)
    {
        if (_canvas.Image is not { } img) return;
        img.Rotate(clockwise);
        _canvas.Reset();
        _details.Text = $"{img.Width:N0} × {img.Height:N0}   {img.Format}   {FormatSize(img.FileSize)}";
        UpdateInfo();
    }

    public void SetFullScreen(bool on)
    {
        _fullScreen = on;
        _top.IsVisible = !on;
        _full.SetGlyph(on ? Glyphs.ExitFullScreen : Glyphs.FullScreen);
        _full.SetTip(on ? "Leave full screen (F / Esc)" : "Full screen (F)");
        PokeChrome();
    }

    private void RefreshToggles()
    {
        var s = ViewerSettings.Current;
        _play.SetGlyph(_playing ? Glyphs.Pause : Glyphs.Play);
        _play.SetFilled(!_playing);
        _play.IsOn = _playing;
        _play.SetTip(_playing ? "Pause slideshow (Space)" : "Slideshow (Space)");
        _keepZoom.IsOn = s.KeepZoom;
        _shuffle.IsOn = s.Shuffle;
        _grid.IsOn = s.PixelGrid;
        _infoButton.IsOn = _info.IsVisible;
        _speedText.Text = s.SlideSeconds < 1 ? $"{s.SlideSeconds:0.#} s" : s.SlideSeconds >= 60 ? $"{s.SlideSeconds / 60:0.#} min" : $"{s.SlideSeconds:0.#} s";
    }

    // ---- Chrome ------------------------------------------------------------------------------------------------------

    private void UpdateZoomText()
    {
        if (_canvas.Image is null)
        {
            _zoomText.Text = "–";
            return;
        }
        var pct = _canvas.Zoom * 100;
        _zoomText.Text = pct < 10 ? $"{pct:0.0}%" : $"{pct:N0}%";
    }

    private void UpdateEmpty()
    {
        _empty.IsVisible = _files.Count == 0 && !_message.IsVisible;
        _pill.IsVisible = _files.Count > 0;
    }

    private void UpdateInfo()
    {
        if (!_info.IsVisible) return;
        if (_canvas.Image is not { } img)
        {
            _infoText.Text = CurrentPath is { } p ? Path.GetFileName(p) : "No image";
            return;
        }
        var mp = img.Width * (double)img.Height / 1_000_000;
        var lines = new List<string>
        {
            Path.GetFileName(img.Path),
            Path.GetDirectoryName(img.Path) ?? "",
            $"{img.Width:N0} × {img.Height:N0} px  ({mp:0.#} MP)",
            $"{img.Format}{(img.HasAlpha ? ", transparency" : "")}{(img.IsAnimated ? $", {img.Frames.Count} frames" : "")}",
            $"{FormatSize(img.FileSize)}  ·  modified {img.Modified:yyyy-MM-dd HH:mm}",
        };
        _infoText.Text = string.Join("\n", lines);
    }

    private void ShowMessage(string text)
    {
        _messageText.Text = text;
        _message.IsVisible = true;
        _empty.IsVisible = false;
    }

    private void HideMessage()
    {
        _message.IsVisible = false;
        UpdateEmpty();
    }

    private IDisposable? _toastTimer;

    /// <summary>A short note in the middle of the screen (it doesn't replace an error).</summary>
    private void Toast(string text)
    {
        if (_message.IsVisible && _toastTimer is null) return;
        ShowMessage(text);
        _toastTimer?.Dispose();
        _toastTimer = DispatcherTimer.RunOnce(() =>
        {
            _toastTimer = null;
            if (_messageText.Text == text) HideMessage();
        }, TimeSpan.FromSeconds(1.2));
    }

    /// <summary>The mouse moved: show the toolbar (and cursor), then hide them again after a quiet spell.</summary>
    private void PokeChrome()
    {
        SetChromeVisible(true);
        _idleTimer.Stop();
        _idleTimer.Start();
    }

    private void SetChromeVisible(bool on)
    {
        _pill.Opacity = on ? 1 : 0;
        _pill.IsHitTestVisible = on;
        _canvas.Cursor = !on && _fullScreen ? new Cursor(StandardCursorType.None) : new Cursor(StandardCursorType.Arrow);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        PokeChrome();
    }

    // ---- Keyboard ----------------------------------------------------------------------------------------------------

    private TopLevel? _host;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _host = TopLevel.GetTopLevel(this);
        _host?.AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        Dispatcher.UIThread.Post(() => _canvas.Focus());
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _host?.RemoveHandler(KeyDownEvent, OnKey);
        _host = null;
        _slideTimer.Stop();
        _idleTimer.Stop();
        _canvas.Show(null);
        foreach (var t in _cache.Values) DisposeLater(t);
        _cache.Clear();
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox) return;
        if (HandleKey(e.Key, e.KeyModifiers)) e.Handled = true;
    }

    /// <summary>The viewer's shortcuts; returns false for keys it leaves alone.</summary>
    public bool HandleKey(Key key, KeyModifiers mods)
    {
        var ctrl = (mods & KeyModifiers.Control) != 0;
        var shift = (mods & KeyModifiers.Shift) != 0;
        const double pan = 120;
        switch (key)
        {
            case Key.Left when shift: _canvas.PanBy(-pan, 0); return true;
            case Key.Right when shift: _canvas.PanBy(pan, 0); return true;
            case Key.Up when shift: _canvas.PanBy(0, -pan); return true;
            case Key.Down when shift: _canvas.PanBy(0, pan); return true;
            case Key.Left or Key.PageUp or Key.Back or Key.Up: Step(-1); return true;
            case Key.Right or Key.PageDown or Key.Down: Step(1); return true;
            case Key.Home: First(); return true;
            case Key.End: Last(); return true;
            case Key.Space or Key.F5 or Key.MediaPlayPause: ToggleSlideshow(); return true;
            case Key.OemPlus or Key.Add: _canvas.ZoomBy(1.5); return true;
            case Key.OemMinus or Key.Subtract: _canvas.ZoomBy(1 / 1.5); return true;
            case Key.D0 or Key.NumPad0 or Key.Multiply: _canvas.Fit(); return true;
            case Key.D1 or Key.NumPad1: _canvas.ActualSize(); return true;
            case Key.R: Rotate(!shift); return true;
            case Key.L when !ctrl: Rotate(false); return true;
            case Key.K: ToggleKeepZoom(); return true;
            case Key.S when !ctrl: ToggleShuffle(); return true;
            case Key.G: TogglePixelGrid(); return true;
            case Key.I: ToggleInfo(); return true;
            case Key.OemOpenBrackets: ChangeSpeed(+1); return true;
            case Key.OemCloseBrackets: ChangeSpeed(-1); return true;
            case Key.O when ctrl && PickFile is not null: _ = PickAndOpenAsync(); return true;
            case Key.F or Key.F11: FullScreenRequested?.Invoke(!_fullScreen); return true;
            case Key.Enter when (mods & KeyModifiers.Alt) != 0: FullScreenRequested?.Invoke(!_fullScreen); return true;
            case Key.Escape:
                if (_fullScreen) FullScreenRequested?.Invoke(false);
                else if (_playing) StopSlideshow();
                else CloseRequested?.Invoke();
                return true;
        }
        return false;
    }

    // ---- Helpers -----------------------------------------------------------------------------------------------------

    private static Border Card(Control child) => new()
    {
        Child = child,
        Background = ViewerTheme.Pill,
        BorderBrush = ViewerTheme.PillBorder,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(16, 12),
    };

    private static Control Separator() => new Border
    {
        Width = 1,
        Height = 20,
        Margin = new Thickness(6, 0),
        Background = ViewerTheme.PillBorder,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };
}
