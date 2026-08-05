using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using EasyChat.Contracts.Ocr;

namespace EasyChat.Presentation.Features.ScreenshotOcr.Controls;

public sealed class OcrImageViewport : Control
{
    private const double MinimumZoom = 0.1;
    private const double MaximumZoom = 8;
    private static readonly IBrush SurfaceBrush = new SolidColorBrush(Color.Parse("#17191C"));
    private Bitmap? _bitmap;
    private OcrRegionSpatialIndex _index = OcrRegionSpatialIndex.Empty;
    private readonly HashSet<int> _selected = [];
    private Point _pan;
    private Point _pointerStart;
    private Point _panStart;
    private Point? _selectionStart;
    private Point? _selectionCurrent;
    private int? _hovered;
    private IPointer? _capturedPointer;
    private double _zoom = 1;
    private bool _isPanning;
    private bool _spacePressed;

    public OcrImageViewport()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    public event Action<IReadOnlyList<int>>? SelectionChanged;
    public event Action<double>? ZoomChanged;

    public void SetBitmap(Bitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        _bitmap = bitmap;
        ResetView();
    }

    public void SetRegions(IReadOnlyList<OcrTextRegion> regions)
    {
        _index = new OcrRegionSpatialIndex(regions);
        _selected.Clear();
        _hovered = null;
        SelectionChanged?.Invoke([]);
        InvalidateVisual();
    }

    public void ZoomIn() => SetZoomAt(_zoom * 1.2, Bounds.Center);
    public void ZoomOut() => SetZoomAt(_zoom / 1.2, Bounds.Center);

    public void ResetView()
    {
        _zoom = 1;
        _pan = default;
        _selected.Clear();
        _hovered = null;
        ZoomChanged?.Invoke(100);
        SelectionChanged?.Invoke([]);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(SurfaceBrush, new Rect(Bounds.Size));
        if (_bitmap is null || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        var transform = GetTransform();
        var destination = new Rect(
            transform.Origin.X,
            transform.Origin.Y,
            _bitmap.PixelSize.Width * transform.Scale,
            _bitmap.PixelSize.Height * transform.Scale);
        context.DrawImage(_bitmap, destination);

        var visibleImage = transform.ToImage(new Rect(Bounds.Size));
        foreach (var regionIndex in _index.Query(visibleImage))
        {
            var selected = _selected.Contains(regionIndex);
            var hovered = _hovered == regionIndex;
            if (!selected && !hovered)
                continue;

            var geometry = CreateGeometry(_index.Regions[regionIndex].Polygon, transform);
            var fill = selected
                ? new SolidColorBrush(Color.FromArgb(82, 22, 163, 155))
                : new SolidColorBrush(Color.FromArgb(62, 33, 150, 243));
            var pen = new Pen(
                selected ? Brushes.Teal : Brushes.DeepSkyBlue,
                selected ? 2 : 1.25);
            context.DrawGeometry(fill, pen, geometry);
        }

        if (_selectionStart is { } start && _selectionCurrent is { } current)
        {
            var selection = Normalize(start, current);
            context.DrawRectangle(
                new SolidColorBrush(Color.FromArgb(40, 33, 150, 243)),
                new Pen(Brushes.DeepSkyBlue, 1),
                transform.ToView(selection));
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_bitmap is null || e.Delta.Y == 0)
            return;
        SetZoomAt(_zoom * Math.Pow(1.15, e.Delta.Y), e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_bitmap is null)
            return;
        Focus();
        var point = e.GetCurrentPoint(this);
        var position = e.GetPosition(this);
        if (point.Properties.IsMiddleButtonPressed
            || (_spacePressed && point.Properties.IsLeftButtonPressed))
        {
            _isPanning = true;
            _pointerStart = position;
            _panStart = _pan;
            Capture(e.Pointer);
            Cursor = new Cursor(StandardCursorType.SizeAll);
            e.Handled = true;
            return;
        }

        if (point.Properties.IsLeftButtonPressed)
        {
            var imagePoint = GetTransform().ToImage(position);
            _selectionStart = imagePoint;
            _selectionCurrent = imagePoint;
            Capture(e.Pointer);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_bitmap is null)
            return;
        var position = e.GetPosition(this);
        if (_isPanning)
        {
            var delta = position - _pointerStart;
            _pan = new Point(_panStart.X + delta.X, _panStart.Y + delta.Y);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_selectionStart is not null)
        {
            _selectionCurrent = GetTransform().ToImage(position);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        var imagePoint = GetTransform().ToImage(position);
        var hovered = _index.HitTest(imagePoint);
        if (_hovered != hovered)
        {
            _hovered = hovered;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_isPanning)
        {
            _isPanning = false;
            Cursor = Cursor.Default;
            ReleaseCapture();
            e.Handled = true;
            return;
        }

        if (_selectionStart is not { } start)
            return;
        var end = GetTransform().ToImage(e.GetPosition(this));
        _selectionStart = null;
        _selectionCurrent = null;
        _selected.Clear();
        if (Distance(start, end) <= 4 / Math.Max(GetTransform().Scale, 0.001))
        {
            var hit = _index.HitTest(end);
            if (hit is not null)
                _selected.Add(hit.Value);
        }
        else
        {
            foreach (var index in _index.Query(Normalize(start, end)))
                _selected.Add(index);
        }
        ReleaseCapture();
        SelectionChanged?.Invoke(_selected.Order().ToArray());
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_selectionStart is null && !_isPanning && _hovered is not null)
        {
            _hovered = null;
            InvalidateVisual();
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _capturedPointer = null;
        _isPanning = false;
        _selectionStart = null;
        _selectionCurrent = null;
        Cursor = Cursor.Default;
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Space)
        {
            _spacePressed = true;
            e.Handled = true;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key == Key.Space)
        {
            _spacePressed = false;
            e.Handled = true;
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        InvalidateVisual();
    }

    private void SetZoomAt(double value, Point anchor)
    {
        if (_bitmap is null)
            return;
        var oldTransform = GetTransform();
        var imageAnchor = oldTransform.ToImage(anchor);
        _zoom = Math.Clamp(value, MinimumZoom, MaximumZoom);
        var fitScale = GetFitScale();
        var newScale = fitScale * _zoom;
        var centeredOrigin = GetCenteredOrigin(newScale);
        _pan = new Point(
            anchor.X - imageAnchor.X * newScale - centeredOrigin.X,
            anchor.Y - imageAnchor.Y * newScale - centeredOrigin.Y);
        ZoomChanged?.Invoke(_zoom * 100);
        InvalidateVisual();
    }

    private ViewportTransform GetTransform()
    {
        var scale = GetFitScale() * _zoom;
        var centered = GetCenteredOrigin(scale);
        return new ViewportTransform(
            scale,
            new Point(centered.X + _pan.X, centered.Y + _pan.Y));
    }

    private double GetFitScale()
    {
        if (_bitmap is null)
            return 1;
        return Math.Max(
            0.0001,
            Math.Min(
                Bounds.Width / Math.Max(1, _bitmap.PixelSize.Width),
                Bounds.Height / Math.Max(1, _bitmap.PixelSize.Height)));
    }

    private Point GetCenteredOrigin(double scale)
    {
        if (_bitmap is null)
            return default;
        return new Point(
            (Bounds.Width - _bitmap.PixelSize.Width * scale) / 2,
            (Bounds.Height - _bitmap.PixelSize.Height * scale) / 2);
    }

    private void Capture(IPointer pointer)
    {
        _capturedPointer = pointer;
        pointer.Capture(this);
    }

    private void ReleaseCapture()
    {
        var pointer = _capturedPointer;
        _capturedPointer = null;
        pointer?.Capture(null);
    }

    private static StreamGeometry CreateGeometry(
        IReadOnlyList<ImagePoint> polygon,
        ViewportTransform transform)
    {
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        context.BeginFigure(transform.ToView(polygon[0]), isFilled: true);
        for (var index = 1; index < polygon.Count; index++)
            context.LineTo(transform.ToView(polygon[index]));
        context.EndFigure(isClosed: true);
        return geometry;
    }

    private static Rect Normalize(Point first, Point second) => new(
        Math.Min(first.X, second.X),
        Math.Min(first.Y, second.Y),
        Math.Abs(first.X - second.X),
        Math.Abs(first.Y - second.Y));

    private static double Distance(Point first, Point second)
    {
        var x = first.X - second.X;
        var y = first.Y - second.Y;
        return Math.Sqrt(x * x + y * y);
    }

    internal readonly record struct ViewportTransform(double Scale, Point Origin)
    {
        public Point ToImage(Point point) => new(
            (point.X - Origin.X) / Scale,
            (point.Y - Origin.Y) / Scale);

        public Rect ToImage(Rect rect)
        {
            var topLeft = ToImage(rect.TopLeft);
            var bottomRight = ToImage(rect.BottomRight);
            return new Rect(topLeft, bottomRight);
        }

        public Point ToView(ImagePoint point) => new(
            Origin.X + point.X * Scale,
            Origin.Y + point.Y * Scale);

        public Rect ToView(Rect rect) => new(
            Origin.X + rect.X * Scale,
            Origin.Y + rect.Y * Scale,
            rect.Width * Scale,
            rect.Height * Scale);
    }
}

internal sealed class OcrRegionSpatialIndex
{
    private const int CellSize = 256;
    private readonly Dictionary<long, List<int>> _cells = [];
    private readonly Rect[] _bounds;

    internal static OcrRegionSpatialIndex Empty { get; } = new([]);

    internal OcrRegionSpatialIndex(IReadOnlyList<OcrTextRegion> regions)
    {
        Regions = regions;
        _bounds = new Rect[regions.Count];
        for (var index = 0; index < regions.Count; index++)
        {
            var bounds = GetBounds(regions[index].Polygon);
            _bounds[index] = bounds;
            foreach (var cell in GetCells(bounds))
            {
                if (!_cells.TryGetValue(cell, out var values))
                {
                    values = [];
                    _cells.Add(cell, values);
                }
                values.Add(index);
            }
        }
    }

    internal IReadOnlyList<OcrTextRegion> Regions { get; }

    internal int? HitTest(Point point)
    {
        if (!_cells.TryGetValue(CellKey(FloorCell(point.X), FloorCell(point.Y)), out var candidates))
            return null;
        for (var candidate = candidates.Count - 1; candidate >= 0; candidate--)
        {
            var index = candidates[candidate];
            if (_bounds[index].Contains(point) && Contains(Regions[index].Polygon, point))
                return index;
        }
        return null;
    }

    internal IReadOnlyList<int> Query(Rect area)
    {
        var found = new HashSet<int>();
        foreach (var cell in GetCells(area))
        {
            if (!_cells.TryGetValue(cell, out var candidates))
                continue;
            foreach (var index in candidates)
            {
                if (_bounds[index].Intersects(area))
                    found.Add(index);
            }
        }
        return found.ToArray();
    }

    private static Rect GetBounds(IReadOnlyList<ImagePoint> polygon)
    {
        var left = polygon.Min(point => point.X);
        var top = polygon.Min(point => point.Y);
        var right = polygon.Max(point => point.X);
        var bottom = polygon.Max(point => point.Y);
        return new Rect(left, top, Math.Max(0.01, right - left), Math.Max(0.01, bottom - top));
    }

    private static bool Contains(IReadOnlyList<ImagePoint> polygon, Point point)
    {
        var inside = false;
        for (int current = 0, previous = polygon.Count - 1;
             current < polygon.Count;
             previous = current++)
        {
            var a = polygon[current];
            var b = polygon[previous];
            if ((a.Y > point.Y) != (b.Y > point.Y)
                && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
            {
                inside = !inside;
            }
        }
        return inside;
    }

    private static IEnumerable<long> GetCells(Rect area)
    {
        var left = FloorCell(area.Left);
        var top = FloorCell(area.Top);
        var right = FloorCell(area.Right);
        var bottom = FloorCell(area.Bottom);
        for (var y = top; y <= bottom; y++)
        for (var x = left; x <= right; x++)
            yield return CellKey(x, y);
    }

    private static int FloorCell(double value) => (int)Math.Floor(value / CellSize);
    private static long CellKey(int x, int y) => ((long)x << 32) ^ (uint)y;
}
