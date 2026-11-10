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
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using ImageGlass.Common.Localization;
using ImageGlass.Common.Types;
using ImageGlass.UI;
using ImageGlass.UI.Windowing;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ImageGlass.Common.Windows;

public partial class SettingsWindow : DialogWindow
{
    private readonly SettingsViewModel _vm = new();
    private readonly SettingsWindowView _viewEl;
    private readonly string? _targetConfigId;
    private readonly string? _editToolId;
    private bool _editToolShown;

    private Border? _windowSurface;
    private Border? _windowOutline;
    private Grid? _resizeHandles;
    private Avalonia.Controls.Shapes.Path? _winPathMaximize;
    private Avalonia.Controls.Shapes.Path? _winPathRestore;

    private WindowEdge? _resizeEdge;
    private PixelPoint _resizeStartPointer;
    private PixelPoint _resizeStartPosition;
    private Size _resizeStartSize;
    private Cursor? _diagonalResizeCursor;
    private Cursor? _oppositeDiagonalResizeCursor;


    protected override int MIN_WIDTH => 0;
    protected override int MAX_WIDTH => int.MaxValue;
    protected override Thickness ContentPadding => new(0);


    private const double DEFAULT_WIDTH = 900;
    private const double DEFAULT_HEIGHT = 580;
    private const double MIN_RESTORE_WIDTH = 200;
    private const double MIN_RESTORE_HEIGHT = 100;


    protected override void UpdateBackground(bool isActive)
    {
        Background = Brushes.Transparent;
    }

    protected override void OnIgBackdropStyleChanged(BackdropStyle style)
    {
        UpdateBackground(IsActive);
    }

    protected override void OnIgFramelessModeChanged(bool enable)
    {
        ExtendClientAreaToDecorationsHint = false;
        WindowDecorations = WindowDecorations.None;
    }


    public SettingsWindow(string? targetConfigId = null, string? editToolId = null)
    {
        _targetConfigId = targetConfigId;
        _editToolId = editToolId;

        UseCustomBackdrop = true;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        ExtendClientAreaToDecorationsHint = false;
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;

        // 强制启用设置窗口暗黑主题分支，使内页所有标准控件与主工作台风格高度统一
        RequestedThemeVariant = ThemeVariant.Dark;
        ApplySettingsWorkbenchResources();

        IsButton1Visible = true;
        IsButton2Visible = true;
        IsButton3Visible = true;

        DefaultButton = DialogButton.Button1;
        DefaultFocus = DialogFocus.Default;
        ShowInTaskbar = true;
        PressEnterToSubmit = false;

        // resizable window
        CanResize = true;
        CanMinimize = true;
        CanMaximize = true;
        SizeToContent = SizeToContent.Manual;
        // no min size: let the window be resized as small as the OS allows

        // restore window size & position
        RestoreWindowBounds(Core.Config.SettingsWindowBounds,
            new(DEFAULT_WIDTH, DEFAULT_HEIGHT),
            new(MIN_RESTORE_WIDTH, MIN_RESTORE_HEIGHT));

        // restore window maximized state
        if (Core.Config.EnableSettingsWindowMaximized) WindowState = WindowState.Maximized;

        _viewEl = new SettingsWindowView(_vm);
        DialogContent = _viewEl;

        WrapWithWorkbenchChrome();
        StyleFooterButtons();
        ApplyTheme();

        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty || e.Property == CanResizeProperty)
            {
                UpdateWindowChromeState();
            }
        };
        UpdateWindowChromeState();
    }


    /// <summary>
    /// 注入高质感深空黑曜石工作室主题资源，确保不受全局浅色系统词典干扰。
    /// </summary>
    private void ApplySettingsWorkbenchResources()
    {
        var glassBorder = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            [
                new GradientStop(Color.Parse("#32FFFFFF"), 0.0),
                new GradientStop(Color.Parse("#14FFFFFF"), 0.45),
                new GradientStop(Color.Parse("#0BFFFFFF"), 1.0),
            ],
        };

        Resources["ModalGlassBorderBrush"] = glassBorder;
        Resources["IG_ThemeBackgroundBrush"] = Brush.Parse("#23252B");
        Resources["IG_ThemeForegroundBrush"] = Brush.Parse("#E5E7EB");
        Resources["TextControlForeground"] = Brush.Parse("#E5E7EB");
        Resources["TextControlForegroundPointerOver"] = Brush.Parse("#FFFFFF");
        Resources["TextControlForegroundFocused"] = Brush.Parse("#FFFFFF");
        Resources["SystemControlForegroundBaseHighBrush"] = Brush.Parse("#E5E7EB");

        Resources["IG_BackgroundNeutralBrush"] = Brush.Parse("#191B20");
        Resources["IG_BackgroundNeutralHoverBrush"] = Brush.Parse("#262932");
        Resources["IG_BackgroundNeutralPressedBrush"] = Brush.Parse("#303440");
        Resources["IG_BorderNeutralBrush"] = Brush.Parse("#14FFFFFF");
        Resources["IG_BorderControlBrush"] = Brush.Parse("#1EFFFFFF");

        Resources["IG_ToolButtonBackgroundHover"] = Brush.Parse("#1AFFFFFF");
        Resources["IG_ToolButtonBackgroundPressed"] = Brush.Parse("#28FFFFFF");
        Resources["IG_TextAccentColor"] = Brush.Parse("#60A5FA");

        Resources["PhButtonBackground"] = Brush.Parse("#282B33");
        Resources["PhButtonBackgroundPointerOver"] = Brush.Parse("#323640");
        Resources["PhButtonBackgroundPressed"] = Brush.Parse("#22252C");
        Resources["PhButtonBorderBrush"] = Brush.Parse("#18FFFFFF");
        Resources["PhButtonBorderBrushPressed"] = Brush.Parse("#10FFFFFF");

        Resources["PhAccentFill"] = Brush.Parse("#356BBF");
        Resources["PhAccentFillPointerOver"] = Brush.Parse("#407AD2");
        Resources["PhAccentFillPressed"] = Brush.Parse("#2B59A1");
        Resources["PhAccentButtonBorderBrush"] = Brush.Parse("#28FFFFFF");
        Resources["AccentButtonForeground"] = Brush.Parse("#F9FAFB");

        Resources["IG_BackgroundWarningBrush"] = Brush.Parse("#1DF59E0B");
        Resources["IG_TextWarningBrush"] = Brush.Parse("#FBBF24");
        Resources["IG_TextDangerBrush"] = Brush.Parse("#F87171");
        Resources["MenuFlyoutPresenterBackground"] = Brush.Parse("#F424262C");
        Resources["MenuFlyoutPresenterBorderBrush"] = glassBorder;
        Resources["OverlayCornerRadius"] = new CornerRadius(10);
    }


    private void WrapWithWorkbenchChrome()
    {
        if (Content is not Control dialogBody) return;
        Content = null;

        var surfaceGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
        };
        var titleBar = BuildCustomTitleBar();
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(dialogBody, 1);
        surfaceGrid.Children.AddRange([titleBar, dialogBody]);

        _windowSurface = new Border
        {
            CornerRadius = new CornerRadius(12),
            ClipToBounds = true,
            Background = Brush.Parse("#23252B"),
            Child = surfaceGrid,
        };

        _windowOutline = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderBrush = Brush.Parse("#28FFFFFF"),
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
            ZIndex = 1999,
        };

        _resizeHandles = BuildResizeHandles();

        var outerHost = new Grid();
        outerHost.Children.AddRange([_windowSurface, _windowOutline, _resizeHandles]);
        Content = outerHost;
    }


    private Border BuildCustomTitleBar()
    {
        var isMac = BHelper.OS == OSType.Mac;

        var titleBarBg = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            [
                new GradientStop(Color.Parse("#2C2E34"), 0.0),
                new GradientStop(Color.Parse("#23252A"), 1.0),
            ],
        };

        var barGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
        };
        barGrid.PointerPressed += TitleBar_PointerPressed;

        // 左侧组：macOS 三色交通灯 + 设置小图标 + 标题文字
        var leftPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (isMac)
        {
            leftPanel.Children.Add(BuildMacTrafficLights());
        }

        var gearPath = new Avalonia.Controls.Shapes.Path
        {
            Width = 14,
            Height = 14,
            Margin = new Thickness(isMac ? 2 : 14, 0, 8, 0),
            Stretch = Stretch.Uniform,
            Fill = Brush.Parse("#60A5FA"),
            VerticalAlignment = VerticalAlignment.Center,
            Data = Geometry.Parse("M12,15.5 A3.5,3.5 0 1 1 12,8.5 A3.5,3.5 0 1 1 12,15.5 M19.43,12.98 C19.47,12.66 19.5,12.34 19.5,12 C19.5,11.66 19.47,11.34 19.43,11.02 L21.54,9.37 C21.73,9.22 21.78,8.95 21.66,8.73 L19.66,5.27 C19.54,5.05 19.27,4.97 19.05,5.05 L16.56,6.05 C16.04,5.65 15.48,5.32 14.87,5.07 L14.49,2.42 C14.46,2.18 14.25,2 14,2 L10,2 C9.75,2 9.54,2.18 9.51,2.42 L9.13,5.07 C8.52,5.32 7.96,5.65 7.44,6.05 L4.95,5.05 C4.73,4.97 4.46,5.05 4.34,5.27 L2.34,8.73 C2.21,8.95 2.27,9.22 2.46,9.37 L4.57,11.02 C4.53,11.34 4.5,11.66 4.5,12 C4.5,12.34 4.53,12.66 4.57,12.98 L2.46,14.63 C2.27,14.78 2.21,15.05 2.34,15.27 L4.34,18.73 C4.46,18.95 4.73,19.03 4.95,18.95 L7.44,17.95 C7.96,18.35 8.52,18.68 9.13,18.93 L9.51,21.58 C9.54,21.82 9.75,22 10,22 L14,22 C14.25,22 14.46,21.82 14.49,21.58 L14.87,18.93 C15.48,18.68 16.04,18.35 16.56,17.95 L19.05,18.95 C19.27,19.03 19.54,18.95 19.66,18.73 L21.66,15.27 C21.78,15.05 21.73,14.78 21.54,14.63 L19.43,12.98 Z"),
        };

        var titleText = new TextBlock
        {
            FontSize = 12.5,
            FontWeight = FontWeight.Medium,
            Foreground = Brush.Parse("#E5E7EB"),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            [!TextBlock.TextProperty] = this[!TitleProperty],
        };

        leftPanel.Children.AddRange([gearPath, titleText]);
        Grid.SetColumn(leftPanel, 0);
        barGrid.Children.Add(leftPanel);

        // 中间空白拖拽区，确保鼠标点击可拖动整个窗口
        var dragSpacer = new Border { Background = Brushes.Transparent };
        Grid.SetColumn(dragSpacer, 1);
        barGrid.Children.Add(dragSpacer);

        // Windows / Linux 右侧窗口操作按钮
        if (!isMac)
        {
            var winButtons = BuildWindowsCaptionButtons();
            Grid.SetColumn(winButtons, 2);
            barGrid.Children.Add(winButtons);
        }

        return new Border
        {
            Height = 38,
            Background = titleBarBg,
            BorderBrush = Brush.Parse("#12FFFFFF"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = barGrid,
        };
    }


    private StackPanel BuildMacTrafficLights()
    {
        var macPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 8,
            Margin = new Thickness(14, 0, 12, 0),
            Background = Brushes.Transparent,
        };

        var btnClose = CreateMacCircleButton("#FF5F56", "#FF736A", "M 1,1 L 5,5 M 5,1 L 1,5", "#4C0000", false, () => OnDialogAborted());
        var btnMin = CreateMacCircleButton("#FFBD2E", "#FFCC4D", "M 0.5,3 L 5.5,3", "#563D00", false, () => WindowState = WindowState.Minimized);
        var btnMax = CreateMacCircleButton("#27C93F", "#3BD852", "M 0,2.5 L 3.5,0 L 3.5,2.5 Z M 6,3.5 L 2.5,6 L 2.5,3.5 Z", "#004D1A", true, ToggleMaximize);

        macPanel.Children.AddRange([btnClose, btnMin, btnMax]);

        // 平时纯净圆点，悬停红绿灯组时淡入向量图标
        var glyphList = new[]
        {
            (Avalonia.Controls.Shapes.Path)((Border)btnClose.Content!).Child!,
            (Avalonia.Controls.Shapes.Path)((Border)btnMin.Content!).Child!,
            (Avalonia.Controls.Shapes.Path)((Border)btnMax.Content!).Child!,
        };

        macPanel.PointerEntered += (_, _) =>
        {
            foreach (var g in glyphList) g.Opacity = 0.85;
        };
        macPanel.PointerExited += (_, _) =>
        {
            foreach (var g in glyphList) g.Opacity = 0;
        };

        return macPanel;
    }


    private static Button CreateMacCircleButton(string normalHex, string hoverHex, string pathData, string glyphHex, bool useFill, Action onClick)
    {
        var glyph = new Avalonia.Controls.Shapes.Path
        {
            Width = 6,
            Height = 6,
            Data = Geometry.Parse(pathData),
            Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(110) }],
        };
        if (useFill)
        {
            glyph.Fill = Brush.Parse(glyphHex);
        }
        else
        {
            glyph.Stroke = Brush.Parse(glyphHex);
            glyph.StrokeThickness = 1.2;
            glyph.StrokeLineCap = PenLineCap.Round;
        }

        var circle = new Border
        {
            Width = 12,
            Height = 12,
            CornerRadius = new CornerRadius(6),
            Background = Brush.Parse(normalHex),
            Child = glyph,
        };

        var btn = new Button
        {
            Width = 12,
            Height = 12,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Content = circle,
        };
        btn.Classes.Add("link");
        btn.PointerEntered += (_, _) =>
        {
            circle.Background = Brush.Parse(hoverHex);
            glyph.Opacity = 1.0;
        };
        btn.PointerExited += (_, _) => circle.Background = Brush.Parse(normalHex);
        btn.Click += (_, _) => onClick();

        return btn;
    }


    private StackPanel BuildWindowsCaptionButtons()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        var minBtn = CreateWindowsCaptionButton(
            new Line { StartPoint = new Point(0, 5), EndPoint = new Point(10, 5), Stroke = Brush.Parse("#E5E7EB"), StrokeThickness = 1 },
            false,
            () => WindowState = WindowState.Minimized);

        _winPathMaximize = new Avalonia.Controls.Shapes.Path
        {
            Width = 10,
            Height = 10,
            Data = Geometry.Parse("M 0,0 H 10 V 10 H 0 Z"),
            Stroke = Brush.Parse("#E5E7EB"),
            StrokeThickness = 1,
        };
        _winPathRestore = new Avalonia.Controls.Shapes.Path
        {
            Width = 10,
            Height = 10,
            Data = Geometry.Parse("M 2,0 H 10 V 8 H 8 M 0,2 H 8 V 10 H 0 Z"),
            Stroke = Brush.Parse("#E5E7EB"),
            StrokeThickness = 1,
            IsVisible = false,
        };
        var maxPanel = new Panel { Width = 10, Height = 10 };
        maxPanel.Children.AddRange([_winPathMaximize, _winPathRestore]);
        var maxBtn = CreateWindowsCaptionButton(maxPanel, false, ToggleMaximize);

        var closeBtn = CreateWindowsCaptionButton(
            new Avalonia.Controls.Shapes.Path
            {
                Width = 10,
                Height = 10,
                Data = Geometry.Parse("M 0,0 L 10,10 M 10,0 L 0,10"),
                Stroke = Brush.Parse("#E5E7EB"),
                StrokeThickness = 1.1,
            },
            true,
            () => OnDialogAborted());

        panel.Children.AddRange([minBtn, maxBtn, closeBtn]);
        return panel;
    }


    private static Button CreateWindowsCaptionButton(Control iconContent, bool isClose, Action onClick)
    {
        var box = new Panel { Width = 10, Height = 10 };
        box.Children.Add(iconContent);

        var btnBg = new Border
        {
            Background = Brushes.Transparent,
            Child = box,
        };
        var btn = new Button
        {
            Width = 42,
            VerticalAlignment = VerticalAlignment.Stretch,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Content = btnBg,
        };
        btn.Classes.Add("link");
        btn.PointerEntered += (_, _) => btnBg.Background = Brush.Parse(isClose ? "#E81123" : "#18FFFFFF");
        btn.PointerExited += (_, _) => btnBg.Background = Brushes.Transparent;
        btn.Click += (_, _) => onClick();
        return btn;
    }


    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
        }
        else
        {
            BeginMoveDrag(e);
        }
    }


    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }


    private Grid BuildResizeHandles()
    {
        _diagonalResizeCursor = CreateDiagonalResizeCursor(false);
        _oppositeDiagonalResizeCursor = CreateDiagonalResizeCursor(true);

        var grid = new Grid { ZIndex = 2000 };
        grid.PointerMoved += ResizeHandle_PointerMoved;
        grid.PointerReleased += ResizeHandle_PointerReleased;
        grid.PointerCaptureLost += (_, _) => _resizeEdge = null;

        Control MakeHandle(double? w, double? h, Thickness margin, HorizontalAlignment ha, VerticalAlignment va, Cursor cursor, WindowEdge edge)
        {
            var b = new Border
            {
                Background = Brushes.Transparent,
                Margin = margin,
                HorizontalAlignment = ha,
                VerticalAlignment = va,
                Cursor = cursor,
                Tag = edge,
            };
            if (w.HasValue) b.Width = w.Value;
            if (h.HasValue) b.Height = h.Value;
            b.PointerPressed += ResizeHandle_PointerPressed;
            return b;
        }

        grid.Children.AddRange([
            MakeHandle(null, 6, new Thickness(12, 0), HorizontalAlignment.Stretch, VerticalAlignment.Top, new Cursor(StandardCursorType.SizeNorthSouth), WindowEdge.North),
            MakeHandle(null, 6, new Thickness(12, 0), HorizontalAlignment.Stretch, VerticalAlignment.Bottom, new Cursor(StandardCursorType.SizeNorthSouth), WindowEdge.South),
            MakeHandle(6, null, new Thickness(0, 12), HorizontalAlignment.Left, VerticalAlignment.Stretch, new Cursor(StandardCursorType.SizeWestEast), WindowEdge.West),
            MakeHandle(6, null, new Thickness(0, 12), HorizontalAlignment.Right, VerticalAlignment.Stretch, new Cursor(StandardCursorType.SizeWestEast), WindowEdge.East),
            MakeHandle(12, 12, default, HorizontalAlignment.Left, VerticalAlignment.Top, _diagonalResizeCursor, WindowEdge.NorthWest),
            MakeHandle(12, 12, default, HorizontalAlignment.Right, VerticalAlignment.Top, _oppositeDiagonalResizeCursor, WindowEdge.NorthEast),
            MakeHandle(12, 12, default, HorizontalAlignment.Left, VerticalAlignment.Bottom, _oppositeDiagonalResizeCursor, WindowEdge.SouthWest),
            MakeHandle(12, 12, default, HorizontalAlignment.Right, VerticalAlignment.Bottom, _diagonalResizeCursor, WindowEdge.SouthEast),
        ]);
        return grid;
    }


    private void ResizeHandle_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (WindowState != WindowState.Normal || !CanResize || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (sender is not Control { Tag: WindowEdge edge }) return;
        e.Handled = true;
        if (!OperatingSystem.IsMacOS())
        {
            BeginResizeDrag(edge, e);
            return;
        }

        _resizeEdge = edge;
        _resizeStartPointer = this.PointToScreen(e.GetPosition(this));
        _resizeStartPosition = Position;
        _resizeStartSize = ClientSize;
        e.Pointer.Capture((Control)sender);
    }


    private void ResizeHandle_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_resizeEdge is not { } edge || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var pointer = this.PointToScreen(e.GetPosition(this));
        var scale = RenderScaling;
        const double resizeSensitivity = 1.5;
        var dx = (pointer.X - _resizeStartPointer.X) / scale * resizeSensitivity;
        var dy = (pointer.Y - _resizeStartPointer.Y) / scale * resizeSensitivity;
        var left = edge is WindowEdge.West or WindowEdge.NorthWest or WindowEdge.SouthWest;
        var right = edge is WindowEdge.East or WindowEdge.NorthEast or WindowEdge.SouthEast;
        var top = edge is WindowEdge.North or WindowEdge.NorthEast or WindowEdge.NorthWest;
        var bottom = edge is WindowEdge.South or WindowEdge.SouthEast or WindowEdge.SouthWest;
        var width = Math.Clamp(_resizeStartSize.Width + (left ? -dx : right ? dx : 0), MinWidth, MaxWidth);
        var height = Math.Clamp(_resizeStartSize.Height + (top ? -dy : bottom ? dy : 0), MinHeight, MaxHeight);
        var x = _resizeStartPosition.X + (left ? (int)Math.Round((_resizeStartSize.Width - width) * scale) : 0);
        var y = _resizeStartPosition.Y + (top ? (int)Math.Round((_resizeStartSize.Height - height) * scale) : 0);

        Width = width;
        Height = height;
        if (left || top) Position = new PixelPoint(x, y);
        e.Handled = true;
    }


    private void ResizeHandle_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_resizeEdge is null) return;
        _resizeEdge = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }


    private static Cursor CreateDiagonalResizeCursor(bool mirror)
    {
        const int size = 16;
        static bool IsArrow(int x, int y)
        {
            if (x < 2 || y < 2 || x > 13 || y > 13) return false;
            return x + y <= 8 || x + y >= 22 || (Math.Abs(x - y) <= 1 && x >= 4 && x <= 11);
        }

        using var bitmap = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var frame = bitmap.Lock())
        {
            var pixels = new byte[frame.RowBytes * size];
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var sampleX = mirror ? size - 1 - x : x;
                var inside = IsArrow(sampleX, y);
                var outline = false;
                for (var oy = -1; oy <= 1 && !outline; oy++)
                for (var ox = -1; ox <= 1 && !outline; ox++)
                    outline = IsArrow(sampleX + ox, y + oy);
                if (!outline) continue;
                var index = y * frame.RowBytes + x * 4;
                var color = inside ? (byte)0 : (byte)255;
                pixels[index] = pixels[index + 1] = pixels[index + 2] = color;
                pixels[index + 3] = 255;
            }
            Marshal.Copy(pixels, 0, frame.Address, pixels.Length);
        }
        return new Cursor(bitmap, new PixelPoint(size / 2, size / 2));
    }


    private void UpdateWindowChromeState()
    {
        var isNormal = WindowState == WindowState.Normal;
        if (_resizeHandles is not null) _resizeHandles.IsVisible = isNormal && CanResize;
        if (_windowSurface is not null) _windowSurface.CornerRadius = new CornerRadius(isNormal ? 12 : 0);
        if (_windowOutline is not null) _windowOutline.IsVisible = isNormal;

        var isMax = WindowState == WindowState.Maximized;
        if (_winPathMaximize is not null) _winPathMaximize.IsVisible = !isMax;
        if (_winPathRestore is not null) _winPathRestore.IsVisible = isMax;
    }


    private void StyleFooterButtons()
    {
        if (_footerEl.Child is Grid footerGrid)
        {
            footerGrid.Margin = new Thickness(20, 11);
        }

        foreach (var btn in new[] { _btn1, _btn2, _btn3 })
        {
            btn.MinWidth = 84;
            btn.Height = 30;
            btn.CornerRadius = new CornerRadius(7);
            btn.FontSize = 12.5;
            btn.Padding = new Thickness(14, 0);
        }
    }


    protected override void ApplyTheme()
    {
        _contentEl.Background = Brush.Parse("#23252B");
        _footerEl.Background = Brush.Parse("#1C1E23");
        _footerEl.BorderBrush = Brush.Parse("#12FFFFFF");
    }



    #region Override Methods

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        // a specific setting was requested -> jump to it (overrides the restored page)
        if (!string.IsNullOrWhiteSpace(_targetConfigId))
        {
            _viewEl.NavigateToConfig(_targetConfigId);
        }

        // a tool edit was requested -> open it once (NavigateToTool defers the child dialog itself)
        if (!_editToolShown && !string.IsNullOrWhiteSpace(_editToolId))
        {
            _editToolShown = true;
            _viewEl.NavigateToTool(_editToolId);
        }

        // focus the search box on open
        _viewEl.FocusSearch();
    }


    protected override void OnIgLanguageChanged()
    {
        base.OnIgLanguageChanged();

        Title = Core.Lang[LangId.Menu_MnuSettings];
        Button1Text = Core.Lang[LangId._OK];
        Button2Text = Core.Lang[LangId._Cancel];
        Button3Text = Core.Lang[LangId._Apply];
    }


    protected override async void OnDialogSubmitted(DialogEventArgs e)
    {
        if (!e.CanProceed) return;

        await CommitAndReportAsync();
        base.OnDialogSubmitted(e);
    }


    protected override async void OnDialogApplied(DialogEventArgs e)
    {
        if (!e.CanProceed) return;

        await CommitAndReportAsync();
        base.OnDialogApplied(e);
    }


    /// <summary>
    /// Commits the staged edits, reporting a failed write; the values stay applied for this session.
    /// </summary>
    private async Task CommitAndReportAsync()
    {
        if (await _vm.CommitAsync()) return;

        await ModalWindow.ShowSettingsSaveErrorAsync(this, Core.Lang[LangId.Menu_MnuSettings]);
    }


    protected override void OnDialogCancelled(DialogEventArgs e)
    {
        _vm.Discard();
        base.OnDialogCancelled(e);
    }


    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // capture bounds here (not OnClosed) — the native window is still alive,
        // so Position/ClientSize are valid; by OnClosed they read (0,0).

        // save state regardless of how the dialog was closed; the pre-minimize state keeps it maximized
        Core.Config.EnableSettingsWindowMaximized = RestorableWindowState == WindowState.Maximized;

        // the tracked windowed bounds, so a maximized size is never stored as the restore size,
        // while a session that ends maximized still records the monitor it was on
        if (WindowedBounds is { } bounds
            && bounds.Width >= MIN_RESTORE_WIDTH
            && bounds.Height >= MIN_RESTORE_HEIGHT)
        {
            Core.Config.SettingsWindowBounds = bounds;
        }

        _ = Core.Config.SaveAsync();

        base.OnClosing(e);
    }


    protected override void OnDialogAborted()
    {
        _vm.Discard();
        base.OnDialogAborted();
    }

    #endregion // Override Methods



    #region Public Methods

    /// <summary>
    /// Navigates the settings window to the page hosting the given config id and scrolls to it.
    /// </summary>
    public void NavigateToConfig(string? configId) => _viewEl.NavigateToConfig(configId);


    /// <summary>
    /// Navigates to the Tools page and opens the add/edit dialog for the given tool id.
    /// </summary>
    public void NavigateToTool(string? toolId) => _viewEl.NavigateToTool(toolId);

    #endregion // Public Methods


}
