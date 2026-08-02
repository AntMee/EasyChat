using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EasyChat.Contracts.Platform;
using Key = Avalonia.Input.Key;

namespace EasyChat.Views.Overlay;

public enum CaptureOverlayAction
{
    Translation,
    CopyOriginal,
    CopyTranslated,
    CopyBilingual,
    CopyImageTranslated
}

internal enum OverlayMode
{
    Idle,
    Selecting,
    Resizing,
    Moving,
    Done
}

internal enum ResizeHandle
{
    None,
    TopLeft,
    TopCenter,
    TopRight,
    RightCenter,
    BottomRight,
    BottomCenter,
    BottomLeft,
    LeftCenter
}

public partial class OverlayWindowView : Window
{
    private readonly Bitmap? _capturedImage;
    private readonly ScreenRegion _bounds;
    private readonly bool _precise;
    private readonly bool _regionOnly;
    private readonly Rectangle? _selectionRectangle;
    private readonly Border? _hintBorder;
    private readonly TextBlock? _hintTextBlock;
    private readonly Control? _toolbarBorder;
    private readonly Control? _copyMenuBorder;
    private readonly Control? _copyButton;
    private readonly Border[] _handles = new Border[8];
    private DispatcherTimer? _menuCloseTimer;
    private Point _startPoint;
    private Rect _initialSelection;
    private OverlayMode _mode;
    private ResizeHandle _activeHandle;
    private bool _completed;

    public OverlayWindowView() => InitializeComponent();

    public OverlayWindowView(
        ScreenRegion bounds,
        Bitmap capturedImage,
        bool precise,
        bool regionOnly = false)
    {
        InitializeComponent();
        _bounds = bounds;
        _capturedImage = capturedImage;
        _precise = precise;
        _regionOnly = regionOnly;
        ShowInTaskbar = false;
        WindowState = WindowState.Normal;
        WindowDecorations = WindowDecorations.None;
        ExtendClientAreaToDecorationsHint = true;
        Topmost = true;
        Position = new PixelPoint(bounds.X, bounds.Y);
        Width = bounds.Width;
        Height = bounds.Height;
        Background = new ImageBrush(capturedImage);

        _selectionRectangle = Require<Rectangle>("SelectionRectangle");
        _hintBorder = Require<Border>("HintBorder");
        _hintTextBlock = Require<TextBlock>("HintTextBlock");
        _toolbarBorder = Require<Control>("ToolbarBorder");
        _copyMenuBorder = Require<Control>("CopyMenuBorder");
        _copyButton = Require<Control>("CopyButton");
        _handles =
        [
            Require<Border>("HandleTopLeft"),
            Require<Border>("HandleTopCenter"),
            Require<Border>("HandleTopRight"),
            Require<Border>("HandleRightCenter"),
            Require<Border>("HandleBottomRight"),
            Require<Border>("HandleBottomCenter"),
            Require<Border>("HandleBottomLeft"),
            Require<Border>("HandleLeftCenter")
        ];
        _hintTextBlock.Text = regionOnly ? Lang.Resources.FixedArea_Hint : Lang.Resources.Screenshot_Hint;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
    }

    public event Action<Bitmap, CaptureOverlayAction>? SelectionCompleted;
    public event Action<ScreenRegion>? RegionSelected;
    public event Action? SelectionCanceled;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (_hintBorder is not null)
        {
            Canvas.SetLeft(_hintBorder, 30);
            Canvas.SetTop(_hintBorder, 30);
            _hintBorder.IsVisible = true;
        }
    }

    private T Require<T>(string name) where T : Control =>
        this.FindControl<T>(name) ?? throw new InvalidOperationException($"{name} not found.");

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_selectionRectangle is null || _toolbarBorder is null || _hintBorder is null)
            return;
        var position = e.GetPosition(this);
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            Cancel();
            return;
        }

        if (_mode == OverlayMode.Done)
        {
            var handle = GetHitHandle(position);
            if (handle != ResizeHandle.None)
            {
                _mode = OverlayMode.Resizing;
                _activeHandle = handle;
                _startPoint = position;
                _initialSelection = CurrentSelection();
                _toolbarBorder.IsVisible = false;
                return;
            }

            var selection = CurrentSelection();
            if (selection.Contains(position))
            {
                _mode = OverlayMode.Moving;
                _startPoint = position;
                _initialSelection = selection;
                _toolbarBorder.IsVisible = false;
                HideHandles();
                Cursor = new Cursor(StandardCursorType.SizeAll);
                return;
            }
        }

        _mode = OverlayMode.Selecting;
        _activeHandle = ResizeHandle.None;
        _hintBorder.IsVisible = false;
        _toolbarBorder.IsVisible = false;
        HideHandles();
        _startPoint = position;
        _selectionRectangle.IsVisible = true;
        SetSelection(new Rect(position, new Size(0, 0)));
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_selectionRectangle is null)
            return;
        var position = e.GetPosition(this);
        switch (_mode)
        {
            case OverlayMode.Idle:
                MoveHint(position);
                break;
            case OverlayMode.Selecting:
                SetSelection(new Rect(
                    Math.Min(position.X, _startPoint.X),
                    Math.Min(position.Y, _startPoint.Y),
                    Math.Abs(position.X - _startPoint.X),
                    Math.Abs(position.Y - _startPoint.Y)));
                break;
            case OverlayMode.Resizing:
                ResizeSelection(position);
                break;
            case OverlayMode.Moving:
                MoveSelection(position);
                break;
            case OverlayMode.Done:
                UpdateCursor(position);
                break;
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_selectionRectangle is null ||
            _mode is not (OverlayMode.Selecting or OverlayMode.Resizing or OverlayMode.Moving))
            return;
        if (_selectionRectangle.Width <= 0 || _selectionRectangle.Height <= 0)
        {
            ResetSelection();
            return;
        }
        if (!_precise)
        {
            ProcessSelection(CaptureOverlayAction.Translation);
            return;
        }
        _mode = OverlayMode.Done;
        ShowHandles();
        UpdateToolbarPosition();
        Cursor = Cursor.Default;
    }

    public void ConfirmButton_OnClick(object? sender, RoutedEventArgs e) =>
        ProcessSelection(CaptureOverlayAction.Translation);
    public void CopyOriginal_OnClick(object? sender, RoutedEventArgs e) =>
        ProcessSelection(CaptureOverlayAction.CopyOriginal);
    public void CopyTranslated_OnClick(object? sender, RoutedEventArgs e) =>
        ProcessSelection(CaptureOverlayAction.CopyTranslated);
    public void CopyBilingual_OnClick(object? sender, RoutedEventArgs e) =>
        ProcessSelection(CaptureOverlayAction.CopyBilingual);
    public void CopyImageTranslated_OnClick(object? sender, RoutedEventArgs e) =>
        ProcessSelection(CaptureOverlayAction.CopyImageTranslated);
    public void ResetButton_OnClick(object? sender, RoutedEventArgs e) => ResetSelection();
    public void CancelButton_OnClick(object? sender, RoutedEventArgs e) => Cancel();

    private Rect CurrentSelection() => new(
        Canvas.GetLeft(_selectionRectangle!),
        Canvas.GetTop(_selectionRectangle!),
        _selectionRectangle!.Width,
        _selectionRectangle.Height);

    private void SetSelection(Rect selection)
    {
        if (_selectionRectangle is null)
            return;
        Canvas.SetLeft(_selectionRectangle, selection.X);
        Canvas.SetTop(_selectionRectangle, selection.Y);
        _selectionRectangle.Width = selection.Width;
        _selectionRectangle.Height = selection.Height;
        UpdateHandles(selection);
    }

    private void MoveSelection(Point position)
    {
        var moved = _initialSelection.Translate(new Vector(
            position.X - _startPoint.X,
            position.Y - _startPoint.Y));
        SetSelection(moved);
    }

    private void ResizeSelection(Point position)
    {
        var x = _initialSelection.X;
        var y = _initialSelection.Y;
        var width = _initialSelection.Width;
        var height = _initialSelection.Height;
        var deltaX = position.X - _startPoint.X;
        var deltaY = position.Y - _startPoint.Y;
        switch (_activeHandle)
        {
            case ResizeHandle.TopLeft: x += deltaX; y += deltaY; width -= deltaX; height -= deltaY; break;
            case ResizeHandle.TopCenter: y += deltaY; height -= deltaY; break;
            case ResizeHandle.TopRight: y += deltaY; width += deltaX; height -= deltaY; break;
            case ResizeHandle.RightCenter: width += deltaX; break;
            case ResizeHandle.BottomRight: width += deltaX; height += deltaY; break;
            case ResizeHandle.BottomCenter: height += deltaY; break;
            case ResizeHandle.BottomLeft: x += deltaX; width -= deltaX; height += deltaY; break;
            case ResizeHandle.LeftCenter: x += deltaX; width -= deltaX; break;
        }
        SetSelection(new Rect(x, y, Math.Max(1, width), Math.Max(1, height)));
    }

    private void UpdateHandles(Rect selection)
    {
        if (_selectionRectangle?.IsVisible != true)
            return;
        var points = new[]
        {
            new Point(selection.X - 5, selection.Y - 5),
            new Point(selection.Center.X - 5, selection.Y - 5),
            new Point(selection.Right - 5, selection.Y - 5),
            new Point(selection.Right - 5, selection.Center.Y - 5),
            new Point(selection.Right - 5, selection.Bottom - 5),
            new Point(selection.Center.X - 5, selection.Bottom - 5),
            new Point(selection.X - 5, selection.Bottom - 5),
            new Point(selection.X - 5, selection.Center.Y - 5)
        };
        for (var index = 0; index < _handles.Length; index++)
        {
            Canvas.SetLeft(_handles[index], points[index].X);
            Canvas.SetTop(_handles[index], points[index].Y);
        }
    }

    private void ShowHandles()
    {
        foreach (var handle in _handles)
            handle.IsVisible = true;
    }

    private void HideHandles()
    {
        foreach (var handle in _handles)
            handle.IsVisible = false;
    }

    private ResizeHandle GetHitHandle(Point point)
    {
        for (var index = 0; index < _handles.Length; index++)
        {
            var handle = _handles[index];
            if (!handle.IsVisible)
                continue;
            var hit = new Rect(Canvas.GetLeft(handle), Canvas.GetTop(handle), handle.Width, handle.Height)
                .Inflate(5);
            if (hit.Contains(point))
                return (ResizeHandle)(index + 1);
        }
        return ResizeHandle.None;
    }

    private void UpdateCursor(Point point)
    {
        Cursor = GetHitHandle(point) switch
        {
            ResizeHandle.TopLeft => new Cursor(StandardCursorType.TopLeftCorner),
            ResizeHandle.TopCenter or ResizeHandle.BottomCenter => new Cursor(StandardCursorType.SizeNorthSouth),
            ResizeHandle.TopRight => new Cursor(StandardCursorType.TopRightCorner),
            ResizeHandle.RightCenter or ResizeHandle.LeftCenter => new Cursor(StandardCursorType.SizeWestEast),
            ResizeHandle.BottomRight => new Cursor(StandardCursorType.BottomRightCorner),
            ResizeHandle.BottomLeft => new Cursor(StandardCursorType.BottomLeftCorner),
            _ when CurrentSelection().Contains(point) => new Cursor(StandardCursorType.SizeAll),
            _ => Cursor.Default
        };
    }

    private void MoveHint(Point pointer)
    {
        if (_hintBorder is null)
            return;
        var target = new Point(30, 30);
        Canvas.SetLeft(_hintBorder, target.X);
        Canvas.SetTop(_hintBorder, target.Y);
        _hintBorder.IsVisible = !new Rect(target, _hintBorder.Bounds.Size).Contains(pointer);
    }

    private void UpdateToolbarPosition()
    {
        if (_toolbarBorder is null || _copyButton is null)
            return;
        var selection = CurrentSelection();
        _toolbarBorder.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var toolbarSize = _toolbarBorder.DesiredSize;
        var width = toolbarSize.Width > 0 ? toolbarSize.Width : 200;
        var height = toolbarSize.Height > 0 ? toolbarSize.Height : 60;
        _copyButton.IsVisible = !_regionOnly;
        var x = Math.Clamp(selection.Right - width, 10, Math.Max(10, Bounds.Width - width - 10));
        var y = selection.Bottom + 10;
        if (y + height > Bounds.Height)
            y = Math.Max(0, selection.Y - height - 10);
        Canvas.SetLeft(_toolbarBorder, x);
        Canvas.SetTop(_toolbarBorder, y);
        _toolbarBorder.IsVisible = true;
    }

    private void ProcessSelection(CaptureOverlayAction action)
    {
        if (_selectionRectangle is null || _capturedImage is null)
            return;
        _copyMenuBorder!.IsVisible = false;
        var selection = CurrentSelection();
        var scaling = RenderScaling;
        var local = new ScreenRegion(
            (int)(selection.X * scaling),
            (int)(selection.Y * scaling),
            (int)(selection.Width * scaling),
            (int)(selection.Height * scaling));
        if (local.IsEmpty)
        {
            Cancel();
            return;
        }

        _completed = true;
        if (_regionOnly)
        {
            RegionSelected?.Invoke(new ScreenRegion(
                _bounds.X + local.X,
                _bounds.Y + local.Y,
                local.Width,
                local.Height));
        }
        else
        {
            var crop = new PixelRect(local.X, local.Y, local.Width, local.Height)
                .Intersect(new PixelRect(0, 0, _capturedImage.PixelSize.Width, _capturedImage.PixelSize.Height));
            if (crop.Width <= 0 || crop.Height <= 0)
            {
                Cancel();
                return;
            }
            var source = new CroppedBitmap(_capturedImage, crop);
            var bitmap = new RenderTargetBitmap(crop.Size, new Vector(96, 96));
            using var context = bitmap.CreateDrawingContext();
            context.DrawImage(source, new Rect(source.Size));
            SelectionCompleted?.Invoke(bitmap, action);
        }
        Close();
    }

    private void ResetSelection()
    {
        _mode = OverlayMode.Idle;
        if (_selectionRectangle is not null)
            _selectionRectangle.IsVisible = false;
        HideHandles();
        if (_toolbarBorder is not null)
            _toolbarBorder.IsVisible = false;
        if (_copyMenuBorder is not null)
            _copyMenuBorder.IsVisible = false;
        _activeHandle = ResizeHandle.None;
        Cursor = Cursor.Default;
        if (_hintBorder is not null)
            _hintBorder.IsVisible = true;
    }

    private void CopyButton_OnPointerEntered(object? sender, PointerEventArgs e)
    {
        _menuCloseTimer?.Stop();
        if (_toolbarBorder?.IsVisible != true || _copyButton is null || _copyMenuBorder is null)
            return;
        var canvas = this.FindControl<Canvas>("MainCanvas");
        var position = canvas is null ? null : _copyButton.TranslatePoint(default, canvas);
        if (position is null)
            return;
        Canvas.SetLeft(_copyMenuBorder, position.Value.X);
        var top = Canvas.GetTop(_toolbarBorder) + _toolbarBorder.Bounds.Height + 2;
        if (top + 150 > Height)
            top = Canvas.GetTop(_toolbarBorder) - 152;
        Canvas.SetTop(_copyMenuBorder, top);
        _copyMenuBorder.IsVisible = true;
    }

    private void CopyButton_OnPointerExited(object? sender, PointerEventArgs e) => StartMenuCloseTimer();
    private void CopyMenu_OnPointerEntered(object? sender, PointerEventArgs e) => _menuCloseTimer?.Stop();
    private void CopyMenu_OnPointerExited(object? sender, PointerEventArgs e) => StartMenuCloseTimer();

    private void StartMenuCloseTimer()
    {
        _menuCloseTimer?.Stop();
        _menuCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _menuCloseTimer.Tick += (_, _) =>
        {
            if (_copyMenuBorder is not null)
                _copyMenuBorder.IsVisible = false;
            _menuCloseTimer.Stop();
        };
        _menuCloseTimer.Start();
    }

    private void InputElement_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Cancel();
        else if (e.Key == Key.Enter && _toolbarBorder?.IsVisible == true)
            ProcessSelection(CaptureOverlayAction.Translation);
    }

    private void Cancel()
    {
        if (_completed)
            return;
        _completed = true;
        SelectionCanceled?.Invoke();
        Close();
    }

    private void TopLevel_OnClosed(object? sender, EventArgs e)
    {
        if (!_completed)
        {
            _completed = true;
            SelectionCanceled?.Invoke();
        }
        _menuCloseTimer?.Stop();
    }
}
