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
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ImageGlass.Common.Loggers;
using ImageGlass.Common.Extensions;
using ImageGlass.Common.Localization;
using ImageGlass.Common.Photoing;
using ImageGlass.Common.ServiceProviders;
using ImageGlass.Common.Types;
using ImageGlass.Tools;
using ImageGlass.UI;
using ImageGlass.UI.Windowing;
using ImageGlass.ViewModels;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ImageGlass.Common.Windows;


public partial class MainWindow : PhWindow
{
    protected override void UpdateBackground(bool isActive)
    {
        Background = Avalonia.Media.Brushes.Transparent;
    }

    protected override void OnIgBackdropStyleChanged(BackdropStyle style)
    {
        // Transparency is configured once in the constructor. Reapplying the same
        // hint can reset Avalonia's macOS transparency mode to opaque.
        UpdateBackground(IsActive);
    }

    protected override void OnIgFramelessModeChanged(bool enable)
    {
        // The main window provides its own title bar and caption buttons in every layout mode.
        // Remove the native caption buttons so the custom title bar remains the only one shown.
        // The window has no native decorations; enabling extended chrome also creates
        // Avalonia's drawn title-bar layer, which can cover the transparent corners.
        ExtendClientAreaToDecorationsHint = false;
        WindowDecorations = WindowDecorations.None;
    }

    private readonly AppStatusInfo _status;
    private WindowEdge? _resizeEdge;
    private PixelPoint _resizeStartPointer;
    private PixelPoint _resizeStartPosition;
    private Size _resizeStartSize;
    private Cursor? _diagonalResizeCursor;
    private Cursor? _oppositeDiagonalResizeCursor;

    private static Cursor CreateDiagonalResizeCursor(bool mirror)
    {
        const int size = 32;
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
                var sampleX = (mirror ? size - 1 - x : x) / 2;
                var sampleY = y / 2;
                var inside = IsArrow(sampleX, sampleY);
                var outline = false;
                for (var oy = -1; oy <= 1 && !outline; oy++)
                for (var ox = -1; ox <= 1 && !outline; ox++)
                    outline = IsArrow(sampleX + ox, sampleY + oy);
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
    private bool _isClosingHandled; // to handle closing for saving configs

    private const double DEFAULT_WIDTH = 800;
    private const double DEFAULT_HEIGHT = 500;
    private const double MIN_RESTORE_WIDTH = 200;
    private const double MIN_RESTORE_HEIGHT = 100;

    public MainWindowModel VM => (MainWindowModel)DataContext!;
    public Task<bool> ConfirmDeleteAsync(string title, string heading, string path, string? size)
        => PART_MainView.ConfirmDeleteAsync(title, heading, path, size);
    public Task<string?> PromptRenameAsync(string title, string heading, string filePath, Avalonia.Media.IImage? thumbnail)
        => PART_MainView.PromptRenameAsync(title, heading, filePath, thumbnail);
    public void OpenSettingsOverlay(string? section = null) => PART_MainView.OpenSettingsOverlay(section);

    private void ResizeHandle_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (WindowState != WindowState.Normal || !CanResize || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (sender is not Control { Tag: string edgeName } || !Enum.TryParse<WindowEdge>(edgeName, out var edge)) return;
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
        // A modest multiplier makes resizing cover more distance per drag.
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

    private void ResizeHandle_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        => _resizeEdge = null;


    public MainWindow()
    {
        StartupTrace.Mark("MainWindow:InitComponent:begin");
        InitializeComponent();
        UseCustomBackdrop = true;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        _diagonalResizeCursor = CreateDiagonalResizeCursor(false);
        _oppositeDiagonalResizeCursor = CreateDiagonalResizeCursor(true);
        PART_ResizeTopLeft.Cursor = PART_ResizeBottomRight.Cursor = _diagonalResizeCursor;
        PART_ResizeTopRight.Cursor = PART_ResizeBottomLeft.Cursor = _oppositeDiagonalResizeCursor;
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty || e.Property == CanResizeProperty)
            {
                PART_ResizeHandles.IsVisible = WindowState == WindowState.Normal && CanResize;
                PART_WindowSurface.CornerRadius = new CornerRadius(WindowState == WindowState.Normal ? 12 : 0);
                PART_WindowOutline.IsVisible = WindowState == WindowState.Normal;
            }
        };
        PART_ResizeHandles.IsVisible = WindowState == WindowState.Normal && CanResize;
        StartupTrace.Mark("MainWindow:InitComponent:end");
        _status = new AppStatusInfo(PART_MainView.PART_Viewer);


        // load window size & position
        RestoreWindowBounds(Core.Config.MainWindowBounds,
            new(DEFAULT_WIDTH, DEFAULT_HEIGHT),
            new(MIN_RESTORE_WIDTH, MIN_RESTORE_HEIGHT));

        if (!Core.Config.EnableWindowFit)
        {
            // load window state
            if (Core.Config.EnableMainWindowMaximized) WindowState = WindowState.Maximized;
        }

        // set zoom lock
        if (Core.Config.ZoomMode == UI.Viewer.ZoomMode.LockZoom)
        {
            PART_MainView.PART_Viewer.ZoomFactor = Core.Config.ZoomLockValue / 100f;
        }

        // events
        Core.AppInstance.InstanceInvoked += AppInstance_InstanceInvoked;
    }



    #region Override Methods


    protected override async void OnOpened(EventArgs e)
    {
        StartupTrace.Mark("MainWindow:opened");
        base.OnOpened(e);

        // load full screen; it wins over window fit, which the backup brings back on leaving it
        if (Core.Config.EnableFullScreen)
        {
            // through the API, so the saved windowed layout is backed up as the state to return to
            _ = await Core.API.RunApiAsync(API.IG_ToggleFullScreen, "true");
        }
        else if (Core.Config.EnableWindowFit)
        {
            // load Window fit
            _ = await Core.API.RunApiAsync(API.IG_ToggleWindowFit, "true");
        }

        // load color profile
        Core.UpdateDestColorProfile();

        // restore last opened tool
        _ = await Core.API.RunApiAsync(API.IG_OpenTool, Core.Config.LastOpenedTool);
    }


    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        // register app hotkeys
        StartupTrace.Mark("Hotkeys:register:begin");
        Core.API.RegisterHotkeys();
        StartupTrace.Mark("Hotkeys:register:end");

        // build the macOS menu bar (window-level) from the existing main menu;
        // the application (⌘) menu is defined in App.axaml
        if (OperatingSystem.IsMacOS())
        {
            NativeMenu.SetMenu(this, new NativeMenu());
        }

        // control events
        _status.Changed += Status_Changed;
        PART_MainView.PART_Toolbar.ItemClicked += PART_Toolbar_ItemClicked;
        PART_MainView.PART_Gallery.ItemClicked += PART_Gallery_ItemClicked;
    }


    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        // allow the close through on the second pass after async work is done
        if (_isClosingHandled)
        {
            base.OnClosing(e);
            return;
        }

        // cancel the close so async work can complete before the app tears down
        e.Cancel = true;
        _isClosingHandled = true;

        // let a save the user just triggered finish writing before anything is torn down
        if (Photo.PendingSaveCount > 0)
        {
            _ = PART_MainView.PART_Message.ShowAsync(Core.Photos.CurrentFilePath,
                Core.Lang[LangId.Menu_MnuSave_Saving], durationMs: 0);

            await Photo.WaitForPendingSavesAsync(TimeSpan.FromMinutes(2));
        }

        // control events
        _status.Changed -= Status_Changed;
        _status.Dispose();

        PART_MainView.PART_Toolbar.ItemClicked -= PART_Toolbar_ItemClicked;
        PART_MainView.PART_Gallery.ItemClicked -= PART_Gallery_ItemClicked;


        // this pass already accepted the close, so nothing below may escape and keep the window open
        try
        {
            // stop slideshow so pre-slideshow config values are restored before saving
            Core.API?.IG_ToggleSlideshow(false);

            // the projectors mirror this window; their settings are already in the config
            ProjectorToolControl.Manager?.CloseAll();

            // stop all external tool processes before saving config
            await Core.ToolRegistry.ExternalTools.StopAllAsync();

            // only save config here, do NOT dispose resources yet
            await SaveConfigOnClosingAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainWindow] Closing failed: {ex}");
        }

        // now close for real — _isClosingHandled lets the second pass through
        Close();
    }


    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        _diagonalResizeCursor?.Dispose();
        _oppositeDiagonalResizeCursor?.Dispose();

        // Dispose after the window (and its render loop) is fully closed
        Core.Dispose();
    }


    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;

        if (new Hotkey(e).IsSame(Key.Escape) && PART_MainView.IsRenameModalOpen)
        {
            e.Handled = true;
            PART_MainView.CloseRenameModal();
            return;
        }
        if (new Hotkey(e).IsSame(Key.Escape) && PART_MainView.IsDeleteConfirmationOpen)
        {
            e.Handled = true;
            PART_MainView.CloseDeleteConfirmation();
            return;
        }
        if (new Hotkey(e).IsSame(Key.Escape) && PART_MainView.IsSettingsOverlayOpen)
        {
            e.Handled = true;
            PART_MainView.HandleSettingsEscapeKey();
            return;
        }
        // Escape closes the image info overlay before any other action
        if (new Hotkey(e).IsSame(Key.Escape) && PART_MainView.IsImageInfoOverlayOpen)
        {
            e.Handled = true;
            PART_MainView.CloseImageInfoOverlay();
            return;
        }

        // Escape cancels cropping before the application's exit shortcut can run.
        if (new Hotkey(e).IsSame(Key.Escape)
            && PART_MainView.PART_ToolHost.Tool?.ToolId == CropImageToolControl.TOOL_ID)
        {
            e.Handled = true;
            await Core.API.RunApiAsync(API.IG_CloseTool, CropImageToolControl.TOOL_ID);
            return;
        }

        if (new Hotkey(e).IsSame(Key.Escape) && Core.Config.EnableFullScreen)
        {
            e.Handled = true;
            await Core.API.RunApiAsync(API.IG_ToggleFullScreen, "false");
            return;
        }

        if (e.Source is TextBox
            or NumericUpDown
            or MaskedTextBox
            or AutoCompleteBox) return;

        var viewer = PART_MainView.PART_Viewer;
        if (PART_MainView.PART_ToolHost.Tool?.ToolId == CropImageToolControl.TOOL_ID
            && viewer.SourceSelection.IsEmpty && e.KeyModifiers == Hotkey.Ctrl)
        {
            const double distance = 20;
            var (x, y) = e.Key switch
            {
                Key.Left => (distance, 0d),
                Key.Right => (-distance, 0d),
                Key.Up => (0d, distance),
                Key.Down => (0d, -distance),
                _ => (0d, 0d),
            };
            if (x != 0 || y != 0)
            {
                e.Handled = true;
                viewer.PanCropImage(x, y);
                return;
            }
        }

        // Other app shortcuts are disabled; ordinary control and text input still work.
    }


    #endregion // Override Methods



    #region Control Events

    private async void AppInstance_InstanceInvoked(AppInstance sender, InstanceInvokedEventArgs e)
    {
        // handle single instance command
        if (e.Command.Equals(AppCmds.SINGLE_INSTANCE))
        {
            // un-minimize back to the pre-minimize state, so a maximized window stays maximized
            RestoreAndActivate();

            // set instance arguments
            var modulePath = Core.Args.ElementAtOrDefault(0) ?? string.Empty;
            Core.Args = [modulePath, .. e.Arguments];
            Core.UpdateInitImagePath();

            // apply any -p: config overrides from the forwarded args
            Config.ApplyCliOverrides(Core.Config, e.Arguments);

            // Refresh lock manager after CLI overrides
            ServiceProviders.FeatureManager.Refresh();

            // load image path
            _ = await Core.API.RunApiAsync(API.IG_OpenPath, Core.InputImagePathFromArgs);

            Activate();
            Topmost = true;
            Topmost = Core.Config.EnableWindowTopMost;
        }
    }


    private void Status_Changed(object? sender, EventArgs e)
    {
        var items = _status.GetItems();

        var name = Core.ClipboardImage is not null
            ? Core.Lang[LangId._ClipboardImage]
            : Path.GetFileName(Core.Photos.CurrentFilePath);
        VM.Title = name ?? string.Empty;
        PART_MainView.PART_CustomTitle.Text = VM.Title;
        PART_MainView.PART_ImageInfoOverlay.SetItems(items);
        PART_MainView.UpdateImageInfoOverlayIfOpen();
        PART_MainView.UpdateBottomStatusBar();
    }

    internal void ToggleImageInfoWindow()
    {
        PART_MainView.ToggleImageInfoOverlay();
    }


    private void PART_Toolbar_ItemClicked(object sender, ToolbarItemClickEventArgs e)
    {
        _ = Core.API.RunActionAsync(e.VM.OnClick);
    }


    private async void PART_Gallery_ItemClicked(GalleryItem sender, GalleryItemClickEventArgs e)
    {
        var photoIndex = Core.Photos.IndexOf(sender.VM.FilePath);
        _ = await Core.API.RunApiAsync(API.IG_ViewByIndex, photoIndex.ToString());
    }


    #endregion Control Events




    /// <summary>
    /// Captures the current windowed layout, i.e. the state full screen mode returns to.
    /// </summary>
    public WindowLayoutSnapshot CaptureWindowLayout()
    {
        // the tracked windowed bounds, never the live ones: a maximized, full screen or minimized
        // window reports the bounds of that layout, which is not the one to come back to
        Rect? bounds = WindowedBounds is { } b
            && b.Width >= MIN_RESTORE_WIDTH
            && b.Height >= MIN_RESTORE_HEIGHT ? b : null;

        return new WindowLayoutSnapshot
        {
            IsMaximized = RestorableWindowState == WindowState.Maximized,
            Bounds = bounds,
            ShowToolbar = Core.Config.ShowToolbar,
            ShowGallery = Core.Config.ShowGallery,
            IsFrameless = Core.Config.EnableFrameless,
            IsWindowFit = Core.Config.EnableWindowFit,
        };
    }


    /// <summary>
    /// Captures live window/viewer state into config and saves it; also used before a self-update kills us.
    /// </summary>
    internal async Task<bool> CaptureAndSaveConfigAsync()
    {
        // 1. save full screen mode; a minimized window reports the state it will restore to
        Core.Config.EnableFullScreen = RestorableWindowState == WindowState.FullScreen;

        // 2. save the windowed layout: while in full screen that is the backup taken on entry,
        // never the live values, which full screen has already overwritten in Config
        var layout = Core.API.PreFullScreenLayout ?? CaptureWindowLayout();

        Core.Config.EnableMainWindowMaximized = layout.IsMaximized;
        Core.Config.ShowToolbar = layout.ShowToolbar;
        Core.Config.ShowGallery = layout.ShowGallery;
        Core.Config.EnableFrameless = layout.IsFrameless;
        Core.Config.EnableWindowFit = layout.IsWindowFit;
        if (layout.Bounds is { } bounds) Core.Config.MainWindowBounds = bounds;


        Core.Config.ZoomLockValue = PART_MainView.PART_Viewer.ZoomFactor * 100f;
        Core.Config.LastSeenImagePath = Core.Config.EnableLastSeenImage
            ? Core.Photos.CurrentFilePath
            : string.Empty;


        // persist the current hosted tool's settings, but keep LastOpenedTool intact
        // (IG_CloseTool clears it) so the tool is re-opened on next launch
        if (PART_MainView.PART_ToolHost.Tool is ITool toolToSave)
        {
            ToolRegistry.SaveToolSettings(toolToSave);
        }

        return await Core.Config.SaveAsync();
    }


    private async Task SaveConfigOnClosingAsync()
    {
        // hide the open windows
        Hide();
        App.SettingsWindow?.Hide();


        // save config to file
        var taskConfig = CaptureAndSaveConfigAsync();


        // permanently adds the data that is on the Clipboard so that it is available
        // after the data's original application closes.
        if (Clipboard is not null)
        {
            try
            {
                await Clipboard.FlushAsync();
            }
            catch { }
        }

        await taskConfig;

        // cleaning
        try
        {
            // delete trash
            var tempDir = BHelper.ConfigDir(Dir.Temporary);
            Directory.Delete(tempDir, true);
        }
        catch { }
    }



}
