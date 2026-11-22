/*
ImageGlass - A Fast, Seamless Photo Viewer
Copyright (C) 2010 - 2026 DUONG DIEU PHAP
Project homepage: https://imageglass.org

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ImageGlass.Common;
using ImageGlass.Common.Extensions;
using ImageGlass.Common.Localization;
using ImageGlass.Common.Types;
using ImageGlass.UI;
using ImageGlass.UI.Viewer;
using ImageGlass.UI.Windowing;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;

namespace ImageGlass.Tools;

public partial class ColorPickerToolControl : PhControl, IToolControl
{
    public static string TOOL_ID => "Tool_ColorPicker";
    public string ToolId => TOOL_ID;
    public string ToolName => Core.Lang[LangId.Menu_MnuColorPicker];
    public ResxIconId? ToolIcon => ResxIconId.IconToolColorPicker;
    public bool HasSettingsUI => true;
    public object? Settings { get; private set; } = new ColorPickerConfig();
    public ColorPickerConfig Options => (ColorPickerConfig)Settings!;
    public ViewerControl Viewer { get; set; } = null!;

    private const int MAX_RECORDED_ITEMS = 50;
    private readonly List<RecordedColorItem> _recordedItems = [];
    private readonly ColorPickerLoupeControl _loupe = new() { ZIndex = 120 };
    private Point? _pointerPressViewerPos;
    private Cursor? _previousViewerCursor;
    private static Cursor? _colorPickerCursor;


    #region Public Properties

    /// <summary>
    /// Gets the tooltip text displayed for the copy button.
    /// </summary>
    public string CopyButtonTooltipText
    {
        get => GetValue(CopyButtonTooltipTextProperty);
        private set => SetValue(CopyButtonTooltipTextProperty, value);
    }
    public static readonly StyledProperty<string> CopyButtonTooltipTextProperty =
        AvaloniaProperty.Register<ColorPickerToolControl, string>(nameof(CopyButtonTooltipText), string.Empty);


    /// <summary>
    /// Gets the title of the live color panel.
    /// </summary>
    public string LivePanelTitle
    {
        get => GetValue(LivePanelTitleProperty);
        private set => SetValue(LivePanelTitleProperty, value);
    }
    public static readonly StyledProperty<string> LivePanelTitleProperty =
        AvaloniaProperty.Register<ColorPickerToolControl, string>(nameof(LivePanelTitle), "实时取色");


    /// <summary>
    /// Gets the hint text shown in the live status pill.
    /// </summary>
    public string LiveFollowHintText
    {
        get => GetValue(LiveFollowHintTextProperty);
        private set => SetValue(LiveFollowHintTextProperty, value);
    }
    public static readonly StyledProperty<string> LiveFollowHintTextProperty =
        AvaloniaProperty.Register<ColorPickerToolControl, string>(nameof(LiveFollowHintText), "实时动态跟随");


    /// <summary>
    /// Gets the title of the recorded color panel.
    /// </summary>
    public string RecordedPanelTitle
    {
        get => GetValue(RecordedPanelTitleProperty);
        private set => SetValue(RecordedPanelTitleProperty, value);
    }
    public static readonly StyledProperty<string> RecordedPanelTitleProperty =
        AvaloniaProperty.Register<ColorPickerToolControl, string>(nameof(RecordedPanelTitle), "点击采样记录");


    /// <summary>
    /// Gets the empty state hint for the recorded color panel.
    /// </summary>
    public string EmptyRecordHintText
    {
        get => GetValue(EmptyRecordHintTextProperty);
        private set => SetValue(EmptyRecordHintTextProperty, value);
    }
    public static readonly StyledProperty<string> EmptyRecordHintTextProperty =
        AvaloniaProperty.Register<ColorPickerToolControl, string>(nameof(EmptyRecordHintText), "在图片上点击即可记录当前色彩项");


    /// <summary>
    /// Gets the secondary empty state hint for the recorded color panel.
    /// </summary>
    public string EmptyRecordSubHintText
    {
        get => GetValue(EmptyRecordSubHintTextProperty);
        private set => SetValue(EmptyRecordSubHintTextProperty, value);
    }
    public static readonly StyledProperty<string> EmptyRecordSubHintTextProperty =
        AvaloniaProperty.Register<ColorPickerToolControl, string>(nameof(EmptyRecordSubHintText), "自动记录点击位置的 6 种色彩空间参数，点击任意色值可直接复制");


    /// <summary>
    /// Gets the text for clearing recorded items.
    /// </summary>
    public string ClearRecordsTooltipText
    {
        get => GetValue(ClearRecordsTooltipTextProperty);
        private set => SetValue(ClearRecordsTooltipTextProperty, value);
    }
    public static readonly StyledProperty<string> ClearRecordsTooltipTextProperty =
        AvaloniaProperty.Register<ColorPickerToolControl, string>(nameof(ClearRecordsTooltipText), "清空");


    /// <summary>
    /// Gets whether there are any recorded color items.
    /// </summary>
    public bool HasRecordedItems
    {
        get => GetValue(HasRecordedItemsProperty);
        private set => SetValue(HasRecordedItemsProperty, value);
    }
    public static readonly StyledProperty<bool> HasRecordedItemsProperty =
        AvaloniaProperty.Register<ColorPickerToolControl, bool>(nameof(HasRecordedItems));


    /// <summary>
    /// Gets the count text of recorded items.
    /// </summary>
    public string RecordedCountText
    {
        get => GetValue(RecordedCountTextProperty);
        private set => SetValue(RecordedCountTextProperty, value);
    }
    public static readonly StyledProperty<string> RecordedCountTextProperty =
        AvaloniaProperty.Register<ColorPickerToolControl, string>(nameof(RecordedCountText), "0");


    /// <summary>
    /// Gets, sets the current point.
    /// </summary>
    public Point? CurrentPoint
    {
        get => GetValue(CurrentPointProperty);
        set => SetValue(CurrentPointProperty, value);
    }
    public static readonly StyledProperty<Point?> CurrentPointProperty =
        AvaloniaProperty.Register<ColorPickerToolControl, Point?>(nameof(CurrentPoint));


    /// <summary>
    /// Gets the formatted current coordinate text.
    /// </summary>
    public string? CurrentPointText => CurrentPoint is { } pt
        ? $"{(int)pt.X}, {(int)pt.Y}"
        : (SelectedPoint is { } spt ? $"{(int)spt.X}, {(int)spt.Y}" : "--, --");
    public static readonly DirectProperty<ColorPickerToolControl, string?> CurrentPointTextProperty =
        AvaloniaProperty.RegisterDirect<ColorPickerToolControl, string?>(nameof(CurrentPointText), i => i.CurrentPointText);


    /// <summary>
    /// Gets whether there is an active live or selected color to preview.
    /// </summary>
    public bool HasActiveColor => CurrentPoint is not null || SelectedPoint is not null;
    public static readonly DirectProperty<ColorPickerToolControl, bool> HasActiveColorProperty =
        AvaloniaProperty.RegisterDirect<ColorPickerToolControl, bool>(nameof(HasActiveColor), i => i.HasActiveColor);


    /// <summary>
    /// Gets the active color currently previewed in the swatch card.
    /// </summary>
    public Color ActiveColor => ActiveDisplayColor;
    public static readonly DirectProperty<ColorPickerToolControl, Color> ActiveColorProperty =
        AvaloniaProperty.RegisterDirect<ColorPickerToolControl, Color>(nameof(ActiveColor), i => i.ActiveColor);


    /// <summary>
    /// Gets the formatted alpha percentage badge text.
    /// </summary>
    public string? LiveAlphaPercentText => HasActiveColor
        ? $"A {Math.Round(ActiveDisplayColor.A / 255.0 * 100)}%"
        : "A --%";
    public static readonly DirectProperty<ColorPickerToolControl, string?> LiveAlphaPercentTextProperty =
        AvaloniaProperty.RegisterDirect<ColorPickerToolControl, string?>(nameof(LiveAlphaPercentText), i => i.LiveAlphaPercentText);


    /// <summary>
    /// Gets the tooltip for the last selected comparison dot inside the live swatch.
    /// </summary>
    public string? LastSelectedColorTooltip => SelectedPoint is { } spt
        ? $"{SelectedColor.ToHex(skipAlpha: true)} ({(int)spt.X}, {(int)spt.Y})"
        : null;
    public static readonly DirectProperty<ColorPickerToolControl, string?> LastSelectedColorTooltipProperty =
        AvaloniaProperty.RegisterDirect<ColorPickerToolControl, string?>(nameof(LastSelectedColorTooltip), i => i.LastSelectedColorTooltip);


    /// <summary>
    /// Gets, sets the selected point.
    /// </summary>
    public Point? SelectedPoint
    {
        get => GetValue(SelectedPointProperty);
        set => SetValue(SelectedPointProperty, value);
    }
    public static readonly StyledProperty<Point?> SelectedPointProperty =
        AvaloniaProperty.Register<ColorPickerToolControl, Point?>(nameof(SelectedPoint));


    /// <summary>
    /// Gets, sets the current text color.
    /// </summary>
    public Color CurrentTextColor => GetPositionTextColor(CurrentColor);
    public static readonly DirectProperty<ColorPickerToolControl, Color> CurrentTextColorProperty =
        AvaloniaProperty.RegisterDirect<ColorPickerToolControl, Color>(nameof(CurrentTextColor), i => i.CurrentTextColor);


    /// <summary>
    /// Gets, sets the selected text color.
    /// </summary>
    public Color SelectedTextColor => GetPositionTextColor(SelectedColor);
    public static readonly DirectProperty<ColorPickerToolControl, Color> SelectedTextColorProperty =
        AvaloniaProperty.RegisterDirect<ColorPickerToolControl, Color>(nameof(SelectedTextColor), i => i.SelectedTextColor);


    /// <summary>
    /// Gets, sets the current color (updated dynamically on pointer move).
    /// </summary>
    public Color CurrentColor
    {
        get => GetValue(CurrentColorProperty);
        set => SetValue(CurrentColorProperty, value);
    }
    public static readonly StyledProperty<Color> CurrentColorProperty =
        AvaloniaProperty.Register<ColorPickerToolControl, Color>(nameof(CurrentColor), Const.COLOR_EMPTY);


    /// <summary>
    /// Gets, sets the selected color (updated on click).
    /// </summary>
    public Color SelectedColor
    {
        get => GetValue(SelectedColorProperty);
        set => SetValue(SelectedColorProperty, value);
    }
    public static readonly StyledProperty<Color> SelectedColorProperty =
        AvaloniaProperty.Register<ColorPickerToolControl, Color>(nameof(SelectedColor));


    /// <summary>
    /// Gets the active color displayed by all live color format items:
    /// uses <see cref="CurrentColor"/> while moving on the image, or falls back to <see cref="SelectedColor"/>.
    /// </summary>
    private Color ActiveDisplayColor => CurrentPoint is not null
        ? CurrentColor
        : (SelectedPoint is not null ? SelectedColor : CurrentColor);


    /// <summary>
    /// Gets the short HEX label displayed inside the live swatch bottom bar.
    /// </summary>
    public string? LiveColorHEX => HasActiveColor
        ? ActiveDisplayColor.ToHex(skipAlpha: true)
        : (Core.Lang.Metadata.Code.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "待取色" : "Ready");
    public static readonly DirectProperty<ColorPickerToolControl, string?> LiveColorHEXProperty =
        AvaloniaProperty.RegisterDirect<ColorPickerToolControl, string?>(nameof(LiveColorHEX), i => i.LiveColorHEX);


    /// <summary>
    /// Gets the dynamic color in RGB format.
    /// </summary>
    public string? ColorRGB => FormatColor(ActiveDisplayColor, ColorFormat.RGB, Options.ShowRgbWithAlpha);
    public static readonly DirectProperty<ColorPickerToolControl, string?> ColorRGBProperty =
        AvaloniaProperty.RegisterDirect<ColorPickerToolControl, string?>(nameof(ColorRGB), i => i.ColorRGB);


    /// <summary>
    /// Gets the dynamic color in HEX format.
    /// </summary>
    public string? ColorHEX => FormatColor(ActiveDisplayColor, ColorFormat.HEX, Options.ShowHexWithAlpha);
    public static readonly DirectProperty<ColorPickerToolControl, string?> ColorHEXProperty =
        AvaloniaProperty.RegisterDirect<ColorPickerToolControl, string?>(nameof(ColorHEX), i => i.ColorHEX);


    /// <summary>
    /// Gets the dynamic color in HSL format.
    /// </summary>
    public string? ColorHSL => FormatColor(ActiveDisplayColor, ColorFormat.HSL, Options.ShowHslWithAlpha);
    public static readonly DirectProperty<ColorPickerToolControl, string?> ColorHSLProperty =
        AvaloniaProperty.RegisterDirect<ColorPickerToolControl, string?>(nameof(ColorHSL), i => i.ColorHSL);


    /// <summary>
    /// Gets the dynamic color in HSV format.
    /// </summary>
    public string? ColorHSV => FormatColor(ActiveDisplayColor, ColorFormat.HSV, Options.ShowHsvWithAlpha);
    public static readonly DirectProperty<ColorPickerToolControl, string?> ColorHSVProperty =
        AvaloniaProperty.RegisterDirect<ColorPickerToolControl, string?>(nameof(ColorHSV), i => i.ColorHSV);


    /// <summary>
    /// Gets the dynamic color in CMYK format.
    /// </summary>
    public string? ColorCMYK => FormatColor(ActiveDisplayColor, ColorFormat.CMYK, Options.ShowCmykWithAlpha);
    public static readonly DirectProperty<ColorPickerToolControl, string?> ColorCMYKProperty =
        AvaloniaProperty.RegisterDirect<ColorPickerToolControl, string?>(nameof(ColorCMYK), i => i.ColorCMYK);


    /// <summary>
    /// Gets the dynamic color in CIELAB format.
    /// </summary>
    public string? ColorCIELAB => FormatColor(ActiveDisplayColor, ColorFormat.CIELAB, Options.ShowCIELabWithAlpha);
    public static readonly DirectProperty<ColorPickerToolControl, string?> ColorCIELABProperty =
        AvaloniaProperty.RegisterDirect<ColorPickerToolControl, string?>(nameof(ColorCIELAB), i => i.ColorCIELAB);


    #endregion // Public Properties



    /// <summary>
    /// Gets the code font family for color values.
    /// </summary>
    public FontFamily CodeFontFamily { get; } = new(Const.FONT_CODE);


    public ColorPickerToolControl()
    {
        InitializeComponent();
        PART_LiveHexText.FontFamily = CodeFontFamily;
        PART_LivePointText.FontFamily = CodeFontFamily;
        PART_LiveAlphaText.FontFamily = CodeFontFamily;
    }



    #region Control Events

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        UpdateLocalizedLabels();
        RaiseColorFormatPropertiesChanged();
        RebuildRecordedListUI();

        // Attach color picker crosshair/eyedropper cursor and floating magnifier loupe
        _previousViewerCursor = Viewer.Cursor;
        _colorPickerCursor ??= CreateColorPickerCursor();
        Viewer.Cursor = _colorPickerCursor;

        if (Viewer.Content is Panel viewerPanel && !viewerPanel.Children.Contains(_loupe))
        {
            viewerPanel.Children.Add(_loupe);
        }

        Viewer.ViewerPointerMoved += Viewer_ViewerPointerMoved;
        Viewer.ViewerPointerPressed += Viewer_ViewerPointerPressed;
        Viewer.PointerReleased += Viewer_PointerReleased;
        Viewer.PointerExited += Viewer_PointerExited;
        Viewer.PhotoLoading += Viewer_PhotoLoading;
    }


    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        Viewer.ViewerPointerMoved -= Viewer_ViewerPointerMoved;
        Viewer.ViewerPointerPressed -= Viewer_ViewerPointerPressed;
        Viewer.PointerReleased -= Viewer_PointerReleased;
        Viewer.PointerExited -= Viewer_PointerExited;
        Viewer.PhotoLoading -= Viewer_PhotoLoading;

        _loupe.HideLoupe();
        if (Viewer.Content is Panel viewerPanel)
        {
            viewerPanel.Children.Remove(_loupe);
        }

        Viewer.Cursor = _previousViewerCursor ?? new Cursor(StandardCursorType.Hand);
    }


    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.Property == CurrentColorProperty || e.Property == CurrentPointProperty ||
            e.Property == SelectedColorProperty || e.Property == SelectedPointProperty)
        {
            RaisePropertyChanged(CurrentTextColorProperty, default, CurrentTextColor);
            RaisePropertyChanged(SelectedTextColorProperty, default, SelectedTextColor);
            RaisePropertyChanged(CurrentPointTextProperty, default, CurrentPointText);
            RaisePropertyChanged(HasActiveColorProperty, default, HasActiveColor);
            RaisePropertyChanged(ActiveColorProperty, default, ActiveColor);
            RaisePropertyChanged(LiveAlphaPercentTextProperty, default, LiveAlphaPercentText);
            RaisePropertyChanged(LastSelectedColorTooltipProperty, default, LastSelectedColorTooltip);
            RaisePropertyChanged(LiveColorHEXProperty, default, LiveColorHEX);
            RaiseColorFormatPropertiesChanged();
        }
    }


    protected override void OnIgLanguageChanged()
    {
        base.OnIgLanguageChanged();
        UpdateLocalizedLabels();
        RaisePropertyChanged(LiveColorHEXProperty, default, LiveColorHEX);
        RebuildRecordedListUI();
    }


    private void UpdateLocalizedLabels()
    {
        CopyButtonTooltipText = Core.Lang[LangId._Copy];

        var isZh = Core.Lang.Metadata.Code.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        LivePanelTitle = isZh ? "实时取色" : "Live Picker";
        LiveFollowHintText = isZh ? "实时动态跟随" : "Live Cursor";
        RecordedPanelTitle = isZh ? "采样记录" : "Recorded Samples";
        EmptyRecordHintText = isZh ? "在图片上点击即可记录当前色彩项" : "Click on the image to record color samples";
        EmptyRecordSubHintText = isZh ? "自动记录点击位置的 6 种色彩空间参数，点击任意色值可直接复制" : "Records all 6 color formats on click; click any chip to copy";
        ClearRecordsTooltipText = isZh ? "清空" : Core.Lang[LangId._Reset];
    }


    private void ButtonCopy_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control ctl) return;
        var colorCode = ctl.Tag as string ?? (ctl as ContentControl)?.Content as string;
        if (string.IsNullOrEmpty(colorCode)) return;

        CopyTextToClipboard(colorCode);
    }


    private void ButtonClearRecords_Click(object? sender, RoutedEventArgs e)
    {
        _recordedItems.Clear();
        RebuildRecordedListUI();
    }


    private void Viewer_ViewerPointerMoved(ViewerControl sender, ViewerPointerEventArgs e)
    {
        var isValidSrcPoint = new Rect(sender.BitmapSize).Contains(e.SourcePoint.ToPoint(1));
        if (!isValidSrcPoint)
        {
            CurrentPoint = null;
            CurrentColor = Const.COLOR_EMPTY;
            _loupe.HideLoupe();
            return;
        }

        var color = sender.GetColorAt(e.SourcePoint.X, e.SourcePoint.Y);
        CurrentPoint = e.SourcePoint.ToPoint(1);
        CurrentColor = color;
        _loupe.UpdatePosition(sender, e.Point.Position, e.SourcePoint, color);
    }


    private void Viewer_PointerExited(object? sender, PointerEventArgs e)
    {
        _loupe.HideLoupe();
    }


    private void Viewer_PhotoLoading(ViewerControl sender, ImageGlass.Common.Photoing.PhotoLoadingEventArgs e)
    {
        _loupe.HideLoupe();
    }


    /// <summary>
    /// Creates a high-contrast precision crosshair + center ring cursor for color picking.
    /// </summary>
    private static Cursor CreateColorPickerCursor()
    {
        const int size = 25;
        const int center = size / 2; // 12

        using var bitmap = new WriteableBitmap(
            new PixelSize(size, size),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using (var frame = bitmap.Lock())
        {
            var pixels = new byte[frame.RowBytes * size];

            void SetPixel(int x, int y, byte r, byte g, byte b, byte a = 255)
            {
                if ((uint)x >= size || (uint)y >= size) return;
                var idx = y * frame.RowBytes + x * 4;
                pixels[idx + 0] = (byte)(b * a / 255);
                pixels[idx + 1] = (byte)(g * a / 255);
                pixels[idx + 2] = (byte)(r * a / 255);
                pixels[idx + 3] = a;
            }

            // 1. Draw outer black shadow/outline for cross arms (arms from dist 4..10)
            for (var d = 3; d <= 11; d++)
            {
                for (var t = -1; t <= 1; t++)
                {
                    SetPixel(center + d, center + t, 0, 0, 0, 210);
                    SetPixel(center - d, center + t, 0, 0, 0, 210);
                    SetPixel(center + t, center + d, 0, 0, 0, 210);
                    SetPixel(center + t, center - d, 0, 0, 0, 210);
                }
            }

            // 2. Draw crisp white inner cross arms (dist 4..10)
            for (var d = 4; d <= 10; d++)
            {
                SetPixel(center + d, center, 255, 255, 255);
                SetPixel(center - d, center, 255, 255, 255);
                SetPixel(center, center + d, 255, 255, 255);
                SetPixel(center, center - d, 255, 255, 255);
            }

            // 3. Center 3x3 pixel target box outline (black outer + cyan/white inner) so the exact sampled pixel in the middle stays clear
            for (var dy = -2; dy <= 2; dy++)
            {
                for (var dx = -2; dx <= 2; dx++)
                {
                    var maxDist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    if (maxDist == 2)
                    {
                        SetPixel(center + dx, center + dy, 0, 0, 0, 230);
                    }
                    else if (maxDist == 1)
                    {
                        SetPixel(center + dx, center + dy, 56, 189, 248, 255);
                    }
                }
            }

            Marshal.Copy(pixels, 0, frame.Address, pixels.Length);
        }

        return new Cursor(bitmap, new PixelPoint(center, center));
    }


    private void Viewer_ViewerPointerPressed(ViewerControl sender, ViewerPointerEventArgs e)
    {
        // Only record on left mouse button click (or touch/pen primary press)
        if (!e.Point.Properties.IsLeftButtonPressed)
        {
            _pointerPressViewerPos = null;
            return;
        }

        var isValidSrcPoint = new Rect(sender.BitmapSize).Contains(e.SourcePoint.ToPoint(1));
        if (!isValidSrcPoint)
        {
            _pointerPressViewerPos = null;
            return;
        }

        _pointerPressViewerPos = e.Point.Position;
    }


    private void Viewer_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left || _pointerPressViewerPos is null)
        {
            _pointerPressViewerPos = null;
            return;
        }

        var releasePos = e.GetPosition(Viewer);
        var pressPos = _pointerPressViewerPos.Value;
        _pointerPressViewerPos = null;

        // Ignore pan drag gestures (threshold 5px)
        var dragDist = Math.Sqrt(Math.Pow(releasePos.X - pressPos.X, 2) + Math.Pow(releasePos.Y - pressPos.Y, 2));
        if (dragDist > 5) return;

        var srcPoint = Viewer.PointClientToSource(releasePos).ToPixelPoint();
        var isValidSrcPoint = new Rect(Viewer.BitmapSize).Contains(srcPoint.ToPoint(1));
        if (!isValidSrcPoint) return;

        var pt = srcPoint.ToPoint(1);
        var color = Viewer.GetColorAt(srcPoint.X, srcPoint.Y);

        CurrentPoint = pt;
        CurrentColor = color;
        SelectedPoint = pt;
        SelectedColor = color;

        RecordClickedColor(pt, color);
    }

    #endregion // Control Events



    #region Recorded Color Panel

    private void RecordClickedColor(Point point, Color color)
    {
        var item = new RecordedColorItem(
            point,
            color,
            FormatColor(color, ColorFormat.HEX, Options.ShowHexWithAlpha),
            FormatColor(color, ColorFormat.RGB, Options.ShowRgbWithAlpha),
            FormatColor(color, ColorFormat.HSL, Options.ShowHslWithAlpha),
            FormatColor(color, ColorFormat.HSV, Options.ShowHsvWithAlpha),
            FormatColor(color, ColorFormat.CMYK, Options.ShowCmykWithAlpha),
            FormatColor(color, ColorFormat.CIELAB, Options.ShowCIELabWithAlpha));

        // Insert newest at top
        _recordedItems.Insert(0, item);
        if (_recordedItems.Count > MAX_RECORDED_ITEMS)
        {
            _recordedItems.RemoveAt(_recordedItems.Count - 1);
        }

        RebuildRecordedListUI();
    }


    private void RefreshRecordedItemsFormatting()
    {
        for (int i = 0; i < _recordedItems.Count; i++)
        {
            var old = _recordedItems[i];
            _recordedItems[i] = new RecordedColorItem(
                old.Point,
                old.Color,
                FormatColor(old.Color, ColorFormat.HEX, Options.ShowHexWithAlpha),
                FormatColor(old.Color, ColorFormat.RGB, Options.ShowRgbWithAlpha),
                FormatColor(old.Color, ColorFormat.HSL, Options.ShowHslWithAlpha),
                FormatColor(old.Color, ColorFormat.HSV, Options.ShowHsvWithAlpha),
                FormatColor(old.Color, ColorFormat.CMYK, Options.ShowCmykWithAlpha),
                FormatColor(old.Color, ColorFormat.CIELAB, Options.ShowCIELabWithAlpha));
        }

        RebuildRecordedListUI();
    }


    private void RebuildRecordedListUI()
    {
        if (PART_RecordedList is null) return;

        PART_RecordedList.Children.Clear();
        PART_QuickSwatches?.Children.Clear();

        HasRecordedItems = _recordedItems.Count > 0;
        RecordedCountText = _recordedItems.Count.ToString();

        // Populate rich recorded item cards first so quick swatches can scroll them into view
        var cards = new List<Control>(_recordedItems.Count);
        for (int i = 0; i < _recordedItems.Count; i++)
        {
            var item = _recordedItems[i];
            var sampleNumber = _recordedItems.Count - i;
            var card = CreateRecordedItemCard(item, sampleNumber, i == 0);
            cards.Add(card);
            PART_RecordedList.Children.Add(card);
        }

        // Populate quick swatch palette strip in header (up to 10 recent colors)
        if (PART_QuickSwatches is not null)
        {
            var maxSwatches = Math.Min(_recordedItems.Count, 10);
            for (int i = 0; i < maxSwatches; i++)
            {
                var swItem = _recordedItems[i];
                PART_QuickSwatches.Children.Add(CreateQuickSwatchPill(swItem, i == 0, cards[i]));
            }
        }
    }


    private Control CreateQuickSwatchPill(RecordedColorItem item, bool isLatest, Control targetCard)
    {
        var checker = new Border();
        if (Resources.TryGetResource("CheckerboardBrush", ActualThemeVariant, out var checkerObj) && checkerObj is IBrush checkerBrush)
        {
            checker.Background = checkerBrush;
        }

        var fill = new Border
        {
            Background = new SolidColorBrush(item.Color),
        };

        var swatchBtn = new PhToolButton
        {
            Width = 16,
            Height = 16,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(isLatest ? 1.5 : 1),
            ClipToBounds = true,
            Cursor = new Cursor(StandardCursorType.Hand),
            Content = new Panel
            {
                Children = { checker, fill },
            },
        };
        swatchBtn[!TemplatedControl.BorderBrushProperty] = new DynamicResourceExtension(
            isLatest ? "IgIconBlueBrush" : "IgDarkBrush2EFFFFFF");
        ToolTip.SetTip(swatchBtn, $"{item.Hex} ({(int)item.Point.X}, {(int)item.Point.Y})");

        swatchBtn.Click += (_, _) =>
        {
            SelectedPoint = item.Point;
            SelectedColor = item.Color;
            CurrentPoint = item.Point;
            CurrentColor = item.Color;
            CopyTextToClipboard(item.Hex);
            targetCard.BringIntoView();
        };

        return swatchBtn;
    }


    private Control CreateRecordedItemCard(RecordedColorItem item, int sampleNumber, bool isLatest)
    {
        // 1. Left column: Color swatch button + index & coordinate pill
        var swatchChecker = new Border();
        if (Resources.TryGetResource("CheckerboardBrush", ActualThemeVariant, out var checkerObj) && checkerObj is IBrush checkerBrush)
        {
            swatchChecker.Background = checkerBrush;
        }
        var swatchFill = new Border
        {
            Background = new SolidColorBrush(item.Color),
        };

        var indexBadge = new Border
        {
            Padding = new Thickness(3.5, 0.5),
            CornerRadius = new CornerRadius(3, 0, 3, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(165, 15, 17, 21)),
            Child = new TextBlock
            {
                Text = $"#{sampleNumber}",
                FontFamily = CodeFontFamily,
                FontSize = 8.5,
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.White,
            },
        };

        var swatchBtn = new PhToolButton
        {
            Width = 38,
            Height = 32,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Center,
            Content = new Panel
            {
                Children = { swatchChecker, swatchFill, indexBadge },
            },
        };
        swatchBtn[!TemplatedControl.BorderBrushProperty] = new DynamicResourceExtension("IgDarkBrush2EFFFFFF");
        ToolTip.SetTip(swatchBtn, $"{Core.Lang[LangId._Copy]} HEX: {item.Hex}");
        swatchBtn.Click += (_, _) =>
        {
            SelectedPoint = item.Point;
            SelectedColor = item.Color;
            CurrentPoint = item.Point;
            CurrentColor = item.Color;
            CopyTextToClipboard(item.Hex);
        };

        var coordBlock = new TextBlock
        {
            Text = $"{(int)item.Point.X},{(int)item.Point.Y}",
            FontFamily = CodeFontFamily,
            FontSize = 9.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        coordBlock[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("IgDarkBrush9CA3AF");

        var leftInfoStack = new StackPanel
        {
            Width = 48,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 2.5,
            Children = { swatchBtn, coordBlock },
        };

        // 2. Center area: 2 columns × 3 rows grid (2 items per row) showing all 6 color formats as clickable chips
        var chipsGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,5,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
            RowSpacing = 3,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Control CreateFormatChip(string label, string value, string labelColorKey)
        {
            var lbl = new TextBlock
            {
                Text = label,
                FontSize = 9.5,
                FontWeight = FontWeight.Bold,
                Width = 32,
                VerticalAlignment = VerticalAlignment.Center,
            };
            lbl[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(labelColorKey);

            var val = new TextBlock
            {
                Text = value,
                FontFamily = CodeFontFamily,
                FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            val[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("IgDarkBrushF3F4F6");

            var chipGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,4,*"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(lbl, 0);
            Grid.SetColumn(val, 2);
            chipGrid.Children.AddRange([lbl, val]);

            var chipBtn = new PhToolButton
            {
                Content = chipGrid,
            };
            chipBtn.Classes.Add("record-chip-btn");
            ToolTip.SetTip(chipBtn, $"{Core.Lang[LangId._Copy]} {label}: {value}");
            chipBtn.Click += (_, e) =>
            {
                e.Handled = true;
                SelectedPoint = item.Point;
                SelectedColor = item.Color;
                CopyTextToClipboard(value);
            };

            return chipBtn;
        }

        var chipHex = CreateFormatChip("HEX", item.Hex, "IgIconCyanBrush");
        var chipRgb = CreateFormatChip("RGBA", item.Rgb, "IgIconBlueBrush");
        var chipHsl = CreateFormatChip("HSL", item.Hsl, "IgIconPurpleBrush");
        var chipHsv = CreateFormatChip("HSV", item.Hsv, "IgIconAmberBrush");
        var chipCmyk = CreateFormatChip("CMYK", item.Cmyk, "IgIconEmeraldBrush");
        var chipLab = CreateFormatChip("LAB", item.Cielab, "IgIconRoseBrush");

        Grid.SetRow(chipHex, 0); Grid.SetColumn(chipHex, 0);
        Grid.SetRow(chipRgb, 0); Grid.SetColumn(chipRgb, 2);
        Grid.SetRow(chipHsl, 1); Grid.SetColumn(chipHsl, 0);
        Grid.SetRow(chipHsv, 1); Grid.SetColumn(chipHsv, 2);
        Grid.SetRow(chipCmyk, 2); Grid.SetColumn(chipCmyk, 0);
        Grid.SetRow(chipLab, 2); Grid.SetColumn(chipLab, 2);
        chipsGrid.Children.AddRange([chipHex, chipRgb, chipHsl, chipHsv, chipCmyk, chipLab]);

        // 3. Right column: Delete single item button
        var removeBtn = new PhToolButton
        {
            Padding = new Thickness(5),
            CornerRadius = new CornerRadius(5),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Content = new Avalonia.Controls.Shapes.Path
            {
                Width = 9,
                Height = 9,
                Stretch = Stretch.Uniform,
                Data = Resx.GetIcon(ResxIconId.IconClose),
                [!Avalonia.Controls.Shapes.Shape.FillProperty] = new DynamicResourceExtension("IgDarkBrush8E95A2"),
            },
        };
        ToolTip.SetTip(removeBtn, Core.Lang[LangId._Delete]);
        removeBtn.Click += (_, e) =>
        {
            e.Handled = true;
            _recordedItems.Remove(item);
            RebuildRecordedListUI();
        };

        var rowGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,8,*,6,Auto"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(leftInfoStack, 0);
        Grid.SetColumn(chipsGrid, 2);
        Grid.SetColumn(removeBtn, 4);
        rowGrid.Children.AddRange([leftInfoStack, chipsGrid, removeBtn]);

        // Context menu to copy any specific format from this recorded item
        var ctxMenu = BuildRecordItemContextMenu(item);

        var cardBorder = new Border
        {
            Padding = new Thickness(7, 5),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Child = rowGrid,
            ContextMenu = ctxMenu,
        };

        cardBorder[!Border.BackgroundProperty] = new DynamicResourceExtension(
            isLatest ? "IgDarkBrush143B82F6" : "IgDarkBrush0AFFFFFF");
        cardBorder[!Border.BorderBrushProperty] = new DynamicResourceExtension(
            isLatest ? "IgDarkBrush363B82F6" : "IgDarkBrush14FFFFFF");

        return cardBorder;
    }


    private ContextMenu BuildRecordItemContextMenu(RecordedColorItem item)
    {
        var menu = new ContextMenu();

        void AddCopyItem(string label, string value)
        {
            var mi = new MenuItem
            {
                Header = $"{Core.Lang[LangId._Copy]} {label}:  {value}",
            };
            mi.Click += (_, _) => CopyTextToClipboard(value);
            menu.Items.Add(mi);
        }

        AddCopyItem("HEX", item.Hex);
        AddCopyItem("RGBA", item.Rgb);
        AddCopyItem("HSL", item.Hsl);
        AddCopyItem("HSV", item.Hsv);
        AddCopyItem("CMYK", item.Cmyk);
        AddCopyItem("CIELAB", item.Cielab);

        return menu;
    }


    private void CopyTextToClipboard(string text)
    {
        var top = TopLevel.GetTopLevel(this)?.Clipboard;
        _ = top?.SetTextAsync(text);
    }

    #endregion // Recorded Color Panel



    #region Control Methods

    /// <summary>
    /// Determines the appropriate text color to use based on the specified background color.
    /// </summary>
    private static Color GetPositionTextColor(Color c)
    {
        if (c.IsEmpty) return Core.Theme.InvertedBaseColor;
        if (c.A < 130) return Core.Theme.BaseColor.InvertBlackOrWhite();
        return c.InvertBlackOrWhite();
    }


    /// <summary>
    /// Formats the specified color as a string in the specified color format.
    /// </summary>
    private static string FormatColor(Color color, ColorFormat format, bool includeAlpha = true)
    {
        var skipAlpha = !includeAlpha;
        var code = format switch
        {
            ColorFormat.RGB => color.ToRgbaString(skipAlpha),
            ColorFormat.HEX => color.ToHex(skipAlpha),
            ColorFormat.HSL => color.ToHslString(skipAlpha),
            ColorFormat.HSV => color.ToHsvString(skipAlpha),
            ColorFormat.CMYK => color.ToCmykString(skipAlpha),
            ColorFormat.CIELAB => color.ToCIELABString(skipAlpha),
            _ => string.Empty,
        };

        return code;
    }


    /// <summary>
    /// Raises property changed notifications for all color format properties.
    /// </summary>
    private void RaiseColorFormatPropertiesChanged()
    {
        RaisePropertyChanged(ColorRGBProperty, default, ColorRGB);
        RaisePropertyChanged(ColorHEXProperty, default, ColorHEX);
        RaisePropertyChanged(ColorHSLProperty, default, ColorHSL);
        RaisePropertyChanged(ColorHSVProperty, default, ColorHSV);
        RaisePropertyChanged(ColorCMYKProperty, default, ColorCMYK);
        RaisePropertyChanged(ColorCIELABProperty, default, ColorCIELAB);
    }


    /// <summary>
    /// <inheritdoc/>
    /// </summary>
    public void LoadSettings(JsonElement? jsonEl)
    {
        var settings = jsonEl?.Deserialize(ColorPickerConfigJsonContext.Default.ColorPickerConfig);
        if (settings is not null)
        {
            Settings = settings;
        }
    }


    /// <summary>
    /// <inheritdoc/>
    /// </summary>
    public JsonElement? SaveSettings()
    {
        var jsonEl = JsonSerializer.SerializeToElement(Options, ColorPickerConfigJsonContext.Default.ColorPickerConfig);

        return jsonEl;
    }


    /// <summary>
    /// <inheritdoc/>
    /// </summary>
    public async Task ShowSettingsWindowAsync()
    {
        var window = new ColorPickerSettingsWindow(Options);
        var owner = TopLevel.GetTopLevel(this) as PhWindow;
        var result = await window.ShowAsync(owner);

        if (result == DialogExitCode.OK)
        {
            Settings = window.ResultConfig;
            RaiseColorFormatPropertiesChanged();
            RefreshRecordedItemsFormatting();
        }
    }

    #endregion // Control Methods

}


internal sealed record RecordedColorItem(
    Point Point,
    Color Color,
    string Hex,
    string Rgb,
    string Hsl,
    string Hsv,
    string Cmyk,
    string Cielab);


public enum ColorFormat
{
    RGB,
    HEX,
    HSL,
    HSV,
    CMYK,
    CIELAB,
}
