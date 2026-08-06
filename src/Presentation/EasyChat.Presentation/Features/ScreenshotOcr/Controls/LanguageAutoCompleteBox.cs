using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Material.Icons;

namespace EasyChat.Presentation.Features.ScreenshotOcr.Controls;

/// <summary>
/// Keeps the autocomplete popup compact while allowing visible long items to be
/// read without introducing a horizontal scrollbar.
/// </summary>
public sealed class LanguageAutoCompleteBox : AutoCompleteBox
{
    private const string PopupPartName = "PART_Popup";
    private const double ArrowButtonWidth = 32;
    private const double PopupEdgeInset = 8;
    private const double FallbackPopupHorizontalChrome = 94;
    private const double TextWidthSafetyMargin = 4;
    private const double MaximumPopupWidth = 540;

    private Popup? _dropDownPopup;
    private ScrollViewer? _dropDownScrollViewer;
    private Control? _popupClipTarget;
    private CornerRadius _popupCornerRadius;
    private double _basePopupWidth;
    private double _popupWidth;
    private string _allItemsSearchText = string.Empty;
    private string? _textBeforeOpening;
    private bool _showAllItems;
    private bool _restoreTextWhenClosing;
    private bool _widthUpdateQueued;
    private bool _isUpdatingPopupWidth;

    public static readonly StyledProperty<MaterialIconKind> DropDownIconProperty =
        AvaloniaProperty.Register<LanguageAutoCompleteBox, MaterialIconKind>(
            nameof(DropDownIcon),
            MaterialIconKind.ChevronDown);

    protected override Type StyleKeyOverride => typeof(AutoCompleteBox);

    public MaterialIconKind DropDownIcon
    {
        get => GetValue(DropDownIconProperty);
        private set => SetValue(DropDownIconProperty, value);
    }

    public LanguageAutoCompleteBox()
    {
        DropDownOpened += OnDropDownOpened;
        DropDownClosed += OnDropDownClosed;
        Populated += OnPopulated;
        GotFocus += OnSelectorGotFocus;
        LostFocus += OnSelectorLostFocus;
        PointerPressed += OnSelectorPointerPressed;
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        DetachPopupClip();
        DetachDropDownScrollViewer();
        base.OnApplyTemplate(e);
        TextFilter = FilterText;
        _dropDownPopup = e.NameScope.Find<Popup>(PopupPartName);
        if (_dropDownPopup is not null)
            _dropDownPopup.Placement = PlacementMode.BottomEdgeAlignedRight;
        _basePopupWidth = Bounds.Width;
        _popupWidth = _basePopupWidth;
        ApplyPopupWidth();

        if (IsDropDownOpen)
            QueuePopupWidthUpdate();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var arrangedSize = base.ArrangeOverride(finalSize);

        if (arrangedSize.Width > 0)
        {
            _basePopupWidth = arrangedSize.Width;
            if (!IsDropDownOpen || _popupWidth < _basePopupWidth)
                _popupWidth = _basePopupWidth;
            ApplyPopupWidth();
        }

        return arrangedSize;
    }

    private void OnSelectorGotFocus(object? sender, RoutedEventArgs e)
    {
        PrepareToShowAllItems();
        QueueSelectAllText();
    }

    private void OnSelectorPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (IsDropDownOpen && e.GetPosition(this).X >= Bounds.Width - ArrowButtonWidth)
        {
            CloseDropDownAndRestoreText();
            e.Handled = true;
            return;
        }

        PrepareToShowAllItems();
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsDropDownOpen)
                OpenDropDownWithAllItems();
            SelectAllText();
        }, DispatcherPriority.Input);
    }

    private void OnSelectorLostFocus(object? sender, RoutedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsKeyboardFocusWithin)
                CloseDropDownAndRestoreText();
        }, DispatcherPriority.Background);
    }

    private void OpenDropDownWithAllItems()
    {
        PrepareToShowAllItems();
        IsDropDownOpen = true;
    }

    public void ToggleDropDown()
    {
        Focus();

        if (IsDropDownOpen)
        {
            CloseDropDownAndRestoreText();
            return;
        }

        OpenDropDownWithAllItems();
        QueueSelectAllText();
    }

    private void PrepareToShowAllItems()
    {
        if (!IsDropDownOpen)
            _textBeforeOpening = Text;

        _allItemsSearchText = Text ?? string.Empty;
        _showAllItems = true;
    }

    private void CloseDropDownAndRestoreText()
    {
        if (IsDropDownOpen)
        {
            _restoreTextWhenClosing = true;
            IsDropDownOpen = false;
            return;
        }

        RestoreTextBeforeOpening();
    }

    private void RestoreTextBeforeOpening()
    {
        if (_textBeforeOpening is not null)
            Text = _textBeforeOpening;
    }

    private void QueueSelectAllText() =>
        Dispatcher.UIThread.Post(SelectAllText, DispatcherPriority.Input);

    private void SelectAllText() =>
        this.GetVisualDescendants().OfType<TextBox>().FirstOrDefault()?.SelectAll();

    private void OnDropDownOpened(object? sender, EventArgs e)
    {
        _textBeforeOpening ??= Text;
        DropDownIcon = MaterialIconKind.ChevronUp;
        QueuePopupWidthUpdate();
    }

    private void OnDropDownClosed(object? sender, EventArgs e)
    {
        DetachDropDownScrollViewer();
        _showAllItems = false;
        if (_restoreTextWhenClosing)
            RestoreTextBeforeOpening();
        _restoreTextWhenClosing = false;
        _textBeforeOpening = Text;
        DropDownIcon = MaterialIconKind.ChevronDown;
        _popupWidth = _basePopupWidth;
        ApplyPopupWidth();
    }

    private void OnPopulated(object? sender, PopulatedEventArgs e)
    {
        if (IsDropDownOpen)
            QueuePopupWidthUpdate();
    }

    private void OnDropDownScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        QueuePopupWidthUpdate();
    }

    private bool FilterText(string? searchText, string? itemText)
    {
        searchText ??= string.Empty;

        // Keep the selected value visible while the arrow opens the complete
        // list. A different search string means the user has started typing.
        if (_showAllItems)
        {
            if (string.Equals(searchText, _allItemsSearchText, StringComparison.Ordinal))
                return true;

            _showAllItems = false;
        }

        return itemText?.Contains(searchText, StringComparison.OrdinalIgnoreCase) == true;
    }

    private void QueuePopupWidthUpdate()
    {
        if (_widthUpdateQueued)
            return;

        _widthUpdateQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _widthUpdateQueued = false;
            if (_isUpdatingPopupWidth || !IsDropDownOpen)
                return;

            AttachDropDownScrollViewer();
            UpdatePopupWidthFromVisibleItems();
        }, DispatcherPriority.Render);
    }

    private void AttachDropDownScrollViewer()
    {
        var scrollViewer = _dropDownPopup?.Child as ScrollViewer
            ?? _dropDownPopup?.Child?
                .GetVisualDescendants()
                .OfType<ScrollViewer>()
                .FirstOrDefault();

        if (scrollViewer is null)
        {
            DetachDropDownScrollViewer();
            return;
        }

        if (ReferenceEquals(scrollViewer, _dropDownScrollViewer))
        {
            ConfigurePopupItems(scrollViewer);
            ConfigurePopupScrollBars(scrollViewer);
            return;
        }

        DetachDropDownScrollViewer();
        _dropDownScrollViewer = scrollViewer;
        _dropDownScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
        _dropDownScrollViewer.HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        _dropDownScrollViewer.ScrollChanged += OnDropDownScrollChanged;

        if (_dropDownPopup?.Child is { } child)
        {
            child.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            child.ClipToBounds = true;
            ConfigurePopupChrome(child);
        }

        ConfigurePopupItems(_dropDownScrollViewer);
        ConfigurePopupScrollBars(_dropDownScrollViewer);
    }

    private void DetachDropDownScrollViewer()
    {
        if (_dropDownScrollViewer is null)
            return;

        _dropDownScrollViewer.ScrollChanged -= OnDropDownScrollChanged;
        _dropDownScrollViewer = null;
    }

    private void UpdatePopupWidthFromVisibleItems()
    {
        if (_dropDownScrollViewer is null || _dropDownScrollViewer.Bounds.Height <= 0)
            return;

        _isUpdatingPopupWidth = true;
        try
        {
            var maxTextWidth = 0d;
            var requiredPopupWidth = 0d;
            var hasMeasuredTextLayout = false;
            var viewportHeight = _dropDownScrollViewer.Bounds.Height;
            var popupChild = _dropDownPopup?.Child;
            var currentPopupWidth = popupChild?.Bounds.Width > 0
                ? popupChild.Bounds.Width
                : _popupWidth;

            foreach (var textBlock in _dropDownScrollViewer.GetVisualDescendants().OfType<TextBlock>())
            {
                if (string.IsNullOrWhiteSpace(textBlock.Text))
                    continue;

                var point = textBlock.TranslatePoint(new Point(0, 0), _dropDownScrollViewer);
                if (point is null
                    || point.Value.Y + textBlock.Bounds.Height < 0
                    || point.Value.Y > viewportHeight)
                {
                    continue;
                }

                var measureBlock = new TextBlock
                {
                    Text = textBlock.Text,
                    FontFamily = textBlock.FontFamily,
                    FontSize = textBlock.FontSize,
                    FontWeight = textBlock.FontWeight,
                    FontStyle = textBlock.FontStyle,
                    FontStretch = textBlock.FontStretch,
                    LetterSpacing = textBlock.LetterSpacing,
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.None
                };
                measureBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var desiredTextWidth = measureBlock.DesiredSize.Width;
                maxTextWidth = Math.Max(maxTextWidth, desiredTextWidth);

                if (currentPopupWidth <= 0 || textBlock.Bounds.Width <= 0)
                    continue;

                // Derive the actual popup chrome from the realized item rather than
                // guessing at theme padding and the vertical scrollbar width.
                hasMeasuredTextLayout = true;
                requiredPopupWidth = Math.Max(
                    requiredPopupWidth,
                    currentPopupWidth + desiredTextWidth - textBlock.Bounds.Width + TextWidthSafetyMargin);
            }

            var minimumPopupWidth = _basePopupWidth + PopupEdgeInset * 2;
            var maximumPopupWidth = Math.Max(MaximumPopupWidth, minimumPopupWidth);
            var desiredWidth = hasMeasuredTextLayout
                ? requiredPopupWidth
                : maxTextWidth + FallbackPopupHorizontalChrome;
            desiredWidth = Math.Clamp(desiredWidth, minimumPopupWidth, maximumPopupWidth);

            if (Math.Abs(_popupWidth - desiredWidth) < 0.5)
                return;

            _popupWidth = desiredWidth;
            ApplyPopupWidth();
        }
        finally
        {
            _isUpdatingPopupWidth = false;
        }
    }

    private void ApplyPopupWidth()
    {
        if (_dropDownPopup is null || double.IsNaN(_popupWidth) || _popupWidth <= 0)
            return;

        _dropDownPopup.Width = _popupWidth;

        if (_dropDownPopup.Child is { } child)
        {
            child.Width = _popupWidth;
            child.MaxWidth = _popupWidth;
            child.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            child.ClipToBounds = true;
            ConfigurePopupChrome(child);

            if (_dropDownScrollViewer is not null)
            {
                ConfigurePopupItems(_dropDownScrollViewer);
                ConfigurePopupScrollBars(_dropDownScrollViewer);
            }

        }
    }

    private void ConfigurePopupChrome(Control popupChild)
    {
        var roundedBorder = popupChild as Border;
        if (roundedBorder is not null && !HasCornerRadius(roundedBorder))
        {
            roundedBorder = popupChild.GetVisualDescendants()
                .OfType<Border>()
                .FirstOrDefault(HasCornerRadius);
        }

        if (roundedBorder is not null)
        {
            ConfigurePopupBorder(roundedBorder);
            EnsurePopupEdgeInset(roundedBorder);
        }

        foreach (var border in popupChild.GetVisualDescendants().OfType<Border>())
            ConfigurePopupBorder(border);

        AttachPopupClip(
            popupChild,
            roundedBorder is not null ? roundedBorder.CornerRadius : new CornerRadius(8));
    }

    private static void EnsurePopupEdgeInset(Border popupBorder)
    {
        // Reserve a small gutter so the scrollbar cannot cover the popup's rounded edge.
        var padding = popupBorder.Padding;
        popupBorder.Padding = new Thickness(
            Math.Max(padding.Left, PopupEdgeInset),
            Math.Max(padding.Top, PopupEdgeInset),
            Math.Max(padding.Right, PopupEdgeInset),
            Math.Max(padding.Bottom, PopupEdgeInset));
    }

    private static void ConfigurePopupScrollBars(ScrollViewer scrollViewer)
    {
        foreach (var scrollBar in scrollViewer.GetVisualDescendants().OfType<ScrollBar>())
        {
            if (scrollBar.Orientation != Avalonia.Layout.Orientation.Vertical)
                continue;

            var margin = scrollBar.Margin;
            scrollBar.Margin = new Thickness(
                margin.Left,
                Math.Max(margin.Top, PopupEdgeInset),
                Math.Max(margin.Right, PopupEdgeInset),
                Math.Max(margin.Bottom, PopupEdgeInset));
        }
    }

    private void AttachPopupClip(Control target, CornerRadius radius)
    {
        if (!ReferenceEquals(target, _popupClipTarget))
        {
            DetachPopupClip();
            _popupClipTarget = target;
            _popupClipTarget.SizeChanged += OnPopupClipTargetSizeChanged;
        }

        _popupCornerRadius = radius;
        UpdatePopupClip();
    }

    private void DetachPopupClip()
    {
        if (_popupClipTarget is null)
            return;

        _popupClipTarget.SizeChanged -= OnPopupClipTargetSizeChanged;
        _popupClipTarget.Clip = null;
        _popupClipTarget = null;
    }

    private void OnPopupClipTargetSizeChanged(object? sender, SizeChangedEventArgs e) =>
        UpdatePopupClip();

    private void UpdatePopupClip()
    {
        if (_popupClipTarget is null
            || _popupClipTarget.Bounds.Width <= 0
            || _popupClipTarget.Bounds.Height <= 0)
        {
            return;
        }

        if (!HasCornerRadius(_popupCornerRadius))
        {
            _popupClipTarget.Clip = null;
            return;
        }

        _popupClipTarget.Clip = CreateRoundedClip(
            new Size(_popupClipTarget.Bounds.Width, _popupClipTarget.Bounds.Height),
            _popupCornerRadius);
    }

    private static StreamGeometry CreateRoundedClip(Size size, CornerRadius corners)
    {
        var maxRadius = Math.Min(size.Width, size.Height) / 2;
        var topLeft = Math.Clamp(corners.TopLeft, 0, maxRadius);
        var topRight = Math.Clamp(corners.TopRight, 0, maxRadius);
        var bottomRight = Math.Clamp(corners.BottomRight, 0, maxRadius);
        var bottomLeft = Math.Clamp(corners.BottomLeft, 0, maxRadius);
        var geometry = new StreamGeometry();

        using var context = geometry.Open();
        context.BeginFigure(new Point(topLeft, 0), isFilled: true);
        context.LineTo(new Point(size.Width - topRight, 0));
        if (topRight > 0)
            context.ArcTo(
                new Point(size.Width, topRight),
                new Size(topRight, topRight),
                rotationAngle: 0,
                isLargeArc: false,
                SweepDirection.Clockwise);
        context.LineTo(new Point(size.Width, size.Height - bottomRight));
        if (bottomRight > 0)
            context.ArcTo(
                new Point(size.Width - bottomRight, size.Height),
                new Size(bottomRight, bottomRight),
                rotationAngle: 0,
                isLargeArc: false,
                SweepDirection.Clockwise);
        context.LineTo(new Point(bottomLeft, size.Height));
        if (bottomLeft > 0)
            context.ArcTo(
                new Point(0, size.Height - bottomLeft),
                new Size(bottomLeft, bottomLeft),
                rotationAngle: 0,
                isLargeArc: false,
                SweepDirection.Clockwise);
        context.LineTo(new Point(0, topLeft));
        if (topLeft > 0)
            context.ArcTo(
                new Point(topLeft, 0),
                new Size(topLeft, topLeft),
                rotationAngle: 0,
                isLargeArc: false,
                SweepDirection.Clockwise);
        context.EndFigure(isClosed: true);
        return geometry;
    }

    private static bool HasCornerRadius(CornerRadius corners) =>
        corners.TopLeft > 0
        || corners.TopRight > 0
        || corners.BottomRight > 0
        || corners.BottomLeft > 0;

    private static bool HasCornerRadius(Border border) => HasCornerRadius(border.CornerRadius);

    private static void ConfigurePopupBorder(Border border)
    {
        var radius = border.CornerRadius;
        if (radius.TopLeft <= 0
            && radius.TopRight <= 0
            && radius.BottomRight <= 0
            && radius.BottomLeft <= 0)
        {
            return;
        }

        border.ClipToBounds = true;
        if (border.Child is Panel panel)
            panel.Background = null;
    }

    private static void ConfigurePopupItems(Control popupRoot)
    {
        var scrollViewer = popupRoot as ScrollViewer
            ?? popupRoot.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        var viewportWidth = scrollViewer?.Viewport.Width > 0
            ? scrollViewer.Viewport.Width
            : scrollViewer?.Bounds.Width ?? 0;
        var itemMaxWidth = viewportWidth > 0
            ? Math.Max(0, viewportWidth - PopupEdgeInset)
            : double.PositiveInfinity;

        foreach (var listItem in popupRoot.GetVisualDescendants().OfType<ListBoxItem>())
            ConfigurePopupItem(listItem, itemMaxWidth);
    }

    private static void ConfigurePopupItem(ListBoxItem listItem, double itemMaxWidth)
    {
        listItem.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        listItem.HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        listItem.Margin = new Thickness(0, 0, PopupEdgeInset, 0);
        listItem.Width = double.NaN;
        listItem.MaxWidth = itemMaxWidth;
        listItem.Padding = new Thickness(0);
        listItem.CornerRadius = new CornerRadius(4);
        listItem.ClipToBounds = true;
    }
}
