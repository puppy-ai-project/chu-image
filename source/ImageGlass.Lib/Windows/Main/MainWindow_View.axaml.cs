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
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using D2Phap.FileWatcherEx;
using ImageGlass.Common.Localization;
using ImageGlass.Common.Loggers;
using ImageGlass.Common.Photoing;
using ImageGlass.Common.ServiceProviders;
using ImageGlass.Common.ServiceProviders.FileSearchService;
using ImageGlass.Common.Types;
using ImageGlass.Tools;
using ImageGlass.UI;
using ImageGlass.UI.Viewer;
using ImageGlass.UI.Viewer.Transitions;
using ImageGlass.UI.Viewer.ZoomAndPan;
using ImageGlass.ViewModels;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ImageGlass.Common.Windows;

public partial class MainWindowView : PhControl
{
    private bool _updatingZoomSlider;

    private void UpdateZoomSlider()
    {
        if (_updatingZoomSlider) return;
        var min = Math.Max(0.001, PART_Viewer.MinZoom);
        var max = Math.Max(min * 1.001, PART_Viewer.MaxZoom);
        var zoom = Math.Clamp(PART_Viewer.ZoomFactor, min, max);
        _updatingZoomSlider = true;
        PART_ZoomSlider.Value = 1000 * Math.Log(zoom / min) / Math.Log(max / min);
        PART_ZoomPercentage.Text = $"{PART_Viewer.ZoomFactor * 100:0.#}%";
        _updatingZoomSlider = false;
    }

    private void ZoomSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (!IsLoaded || _updatingZoomSlider || FeatureManager.IsZoomLocked()) return;
        if (PART_Viewer.BitmapSize.Width <= 0 || PART_Viewer.BitmapSize.Height <= 0) return;
        var min = Math.Max(0.001, PART_Viewer.MinZoom);
        var max = Math.Max(min * 1.001, PART_Viewer.MaxZoom);
        var factor = min * Math.Pow(max / min, e.NewValue / 1000);
        _updatingZoomSlider = true;
        try
        {
            PART_Viewer.ZoomAroundImageCenter((float)factor);
            PART_ZoomPercentage.Text = $"{PART_Viewer.ZoomFactor * 100:0.#}%";
        }
        finally
        {
            _updatingZoomSlider = false;
        }
    }

    private async void ZoomOutButton_Click(object? sender, RoutedEventArgs e)
        => await Core.API.RunApiAsync(API.IG_ZoomOut);

    private async void ZoomInButton_Click(object? sender, RoutedEventArgs e)
        => await Core.API.RunApiAsync(API.IG_ZoomIn);

    private async void ZoomActualSizeButton_Click(object? sender, RoutedEventArgs e)
        => await Core.API.RunApiAsync(API.IG_SetZoom, "1");

    private void CloseWindow_Click(object? sender, RoutedEventArgs e) => App.MainWindow.Close();

    private void MinimizeWindow_Click(object? sender, RoutedEventArgs e) =>
        App.MainWindow.WindowState = WindowState.Minimized;

    private void MaximizeWindow_Click(object? sender, RoutedEventArgs e) =>
        App.MainWindow.WindowState = App.MainWindow.WindowState == WindowState.Maximized
            ? WindowState.Normal : WindowState.Maximized;

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Control source || source is Button) return;
        if (!e.GetCurrentPoint(PART_CustomTitleBar).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2)
            MaximizeWindow_Click(sender, e);
        else
            App.MainWindow.BeginMoveDrag(e);
        e.Handled = true;
    }

    private void BottomBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (source.GetSelfAndVisualAncestors().Any(v => v is Button or ToggleButton or Slider or Thumb)) return;
        App.MainWindow.BeginMoveDrag(e);
        e.Handled = true;
    }

    private void BottomResizeGrip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (App.MainWindow is null || App.MainWindow.WindowState != WindowState.Normal) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        App.MainWindow.BeginResizeDrag(WindowEdge.Bottom, e);
        e.Handled = true;
    }

    private void InitTitleBarPlatform()
    {
        var isMac = BHelper.OS == OSType.Mac;
        PART_MacButtons.IsVisible = isMac;
        PART_WinButtons.IsVisible = !isMac;
        PART_WinAppIcon.IsVisible = BHelper.OS == OSType.Windows;

        UpdateWindowStateButtons();

        if (App.MainWindow is not null)
        {
            App.MainWindow.PropertyChanged += MainWindow_PropertyChanged;
        }
    }

    private void MainWindow_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty)
        {
            UpdateWindowStateButtons();
        }
    }

    private void UpdateWindowStateButtons()
    {
        if (App.MainWindow is null) return;
        var isMaximized = App.MainWindow.WindowState == WindowState.Maximized;
        PART_PathMaximize.IsVisible = !isMaximized;
        PART_PathRestore.IsVisible = isMaximized;
        ToolTip.SetTip(PART_WinBtnMaximize, isMaximized ? "向下还原" : "最大化");
    }


    #region Image Info Overlay

    private string _cachedImageInfoSummary = string.Empty;

    public bool IsImageInfoOverlayOpen => PART_ImageInfoModal.IsVisible;

    public void ToggleImageInfoOverlay()
    {
        if (IsImageInfoOverlayOpen)
        {
            CloseImageInfoOverlay();
        }
        else
        {
            OpenImageInfoOverlay();
        }
    }

    public void OpenImageInfoOverlay()
    {
        RefreshImageInfoContent();
        PART_ImageInfoModal.IsVisible = true;
    }

    public void CloseImageInfoOverlay()
    {
        PART_ImageInfoModal.IsVisible = false;
    }

    public void UpdateImageInfoOverlayIfOpen()
    {
        if (IsImageInfoOverlayOpen)
        {
            RefreshImageInfoContent();
        }
    }

    private void ImageInfoBackdrop_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        CloseImageInfoOverlay();
        e.Handled = true;
    }

    private void BtnCloseImageInfo_Click(object? sender, RoutedEventArgs e)
    {
        CloseImageInfoOverlay();
    }

    private async void BtnCopyImageInfo_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_cachedImageInfoSummary)) return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(_cachedImageInfoSummary);
            PART_TxtCopyImageInfo.Text = "已复制 ✓";
            _ = Task.Delay(1500).ContinueWith(_ =>
            {
                Dispatcher.UIThread.Post(() => PART_TxtCopyImageInfo.Text = "复制");
            });
        }
    }

    private void BtnOpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        var folder = Core.Photos.Current?.Metadata?.FolderPath;
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            BHelper.OpenFolderPath(folder);
        }
    }

    private void RefreshImageInfoContent()
    {
        var photo = Core.Photos.Current;
        var meta = photo?.Metadata;
        var summary = new StringBuilder();

        if (meta is null || Core.ClipboardImage is not null)
        {
            var isClip = Core.ClipboardImage is not null;
            var name = isClip ? Core.Lang[LangId._ClipboardImage] : "未打开图片";
            PART_InfoFileName.Text = name;
            PART_ChipFormat.IsVisible = isClip;
            PART_InfoFormatText.Text = "剪贴板";
            var sizeStr = isClip && Core.ClipboardImage is { } clipboardImage
                ? $"{clipboardImage.Width} × {clipboardImage.Height} 像素" : "-";
            PART_InfoDimensionsText.Text = sizeStr;
            PART_ChipMegapixels.IsVisible = false;
            PART_InfoFileSizeText.Text = "-";
            PART_SectionExif.IsVisible = false;
            PART_InfoColorSpace.Text = "-";
            PART_InfoDpi.Text = "-";
            PART_RowFrames.IsVisible = false;
            PART_RowHdr.IsVisible = false;
            PART_InfoDateCreated.Text = "-";
            PART_InfoDateModified.Text = "-";
            PART_InfoFilePath.Text = isClip ? Core.Lang[LangId._ClipboardImage] : "-";
            PART_BtnOpenFolder.IsEnabled = false;
            _cachedImageInfoSummary = name;
            return;
        }

        // 1. File name & format
        var ext = meta.FileExtension.TrimStart('.').ToUpperInvariant();
        PART_InfoFileName.Text = meta.FileName;
        PART_ChipFormat.IsVisible = !string.IsNullOrWhiteSpace(ext);
        PART_InfoFormatText.Text = string.IsNullOrWhiteSpace(ext) ? "IMG" : ext;

        // 2. Dimensions & Megapixels
        var w = meta.Width > 0 ? meta.Width : meta.OriginalWidth;
        var h = meta.Height > 0 ? meta.Height : meta.OriginalHeight;
        PART_InfoDimensionsText.Text = (w > 0 && h > 0) ? $"{w} × {h} 像素" : "-";
        if (w > 0 && h > 0)
        {
            var mp = (w * (double)h) / 1000000.0;
            PART_ChipMegapixels.IsVisible = true;
            PART_InfoMegapixelsText.Text = mp >= 1.0 ? $"{mp:F1} MP" : $"{mp:F2} MP";
        }
        else
        {
            PART_ChipMegapixels.IsVisible = false;
        }

        // 3. File size
        PART_InfoFileSizeText.Text = meta.FileSizeFormatted;

        // 4. EXIF Section
        var hasExif = !string.IsNullOrWhiteSpace(meta.ExifModel)
            || meta.ExifFNumber.HasValue
            || meta.ExifExposureTime is > 0
            || meta.ExifISOSpeed.HasValue
            || meta.ExifFocalLength.HasValue;

        PART_SectionExif.IsVisible = hasExif;
        if (hasExif)
        {
            PART_InfoCameraModel.Text = !string.IsNullOrWhiteSpace(meta.ExifModel)
                ? $"📷  {meta.ExifModel}"
                : "📷  相机参数";

            PART_InfoAperture.Text = meta.ExifFNumber.HasValue ? $"f/{meta.ExifFNumber.Value:0.#}" : "-";
            PART_InfoShutter.Text = meta.ExifExposureTime is > 0
                ? (meta.ExifExposureTime.Value < 1 ? $"1/{Math.Round(1.0 / meta.ExifExposureTime.Value)}s" : $"{meta.ExifExposureTime.Value:0.#}s")
                : "-";
            PART_InfoIso.Text = meta.ExifISOSpeed.HasValue ? $"ISO {meta.ExifISOSpeed.Value}" : "-";
            PART_InfoFocalLength.Text = meta.ExifFocalLength.HasValue ? $"{meta.ExifFocalLength.Value:0.#}mm" : "-";
        }

        // 5. Image Specs
        PART_InfoColorSpace.Text = !string.IsNullOrWhiteSpace(meta.ColorProfileName)
            ? meta.ColorProfileName
            : meta.ColorSpace != ImageMagick.ColorSpace.Undefined ? meta.ColorSpace.ToString() : "-";

        var dpi = meta.DpiX > 0 ? $"{Math.Round(meta.DpiX)} DPI" : "-";
        PART_InfoDpi.Text = dpi;

        var hasFrames = meta.FrameCount > 1;
        PART_RowFrames.IsVisible = hasFrames;
        PART_InfoFrames.Text = hasFrames ? $"{meta.FrameCount} 帧" : "单帧";

        var isHdr = meta.IsHdr;
        var isLive = meta.IsLivePhoto;
        PART_RowHdr.IsVisible = isHdr || isLive;
        PART_InfoFeatures.Text = isHdr && isLive ? "HDR • 实况照片 (Live Photo)" : (isHdr ? "HDR 高动态范围" : (isLive ? "实况照片 (Live Photo)" : "-"));

        // 6. Dates & Storage
        PART_InfoDateCreated.Text = !string.IsNullOrWhiteSpace(meta.ExifDateTimeOriginalFormatted)
            ? meta.ExifDateTimeOriginalFormatted
            : meta.FileCreationTimeFormatted;
        PART_InfoDateModified.Text = meta.FileLastWriteTimeFormatted;
        PART_InfoFilePath.Text = meta.FilePath;
        PART_BtnOpenFolder.IsEnabled = Directory.Exists(meta.FolderPath);

        // 7. Summary for Copy
        summary.AppendLine($"文件名: {meta.FileName}");
        summary.AppendLine($"尺寸: {w} × {h} 像素 ({(w * (double)h) / 1000000.0:F1} MP)");
        summary.AppendLine($"大小: {meta.FileSizeFormatted}");
        if (hasExif)
        {
            if (!string.IsNullOrWhiteSpace(meta.ExifModel)) summary.AppendLine($"相机: {meta.ExifModel}");
            if (meta.ExifFNumber.HasValue) summary.AppendLine($"光圈: f/{meta.ExifFNumber.Value:0.#}");
            if (meta.ExifExposureTime is > 0)
            {
                var shutter = meta.ExifExposureTime.Value < 1 ? $"1/{Math.Round(1.0 / meta.ExifExposureTime.Value)}s" : $"{meta.ExifExposureTime.Value:0.#}s";
                summary.AppendLine($"快门: {shutter}");
            }
            if (meta.ExifISOSpeed.HasValue) summary.AppendLine($"ISO: {meta.ExifISOSpeed.Value}");
            if (meta.ExifFocalLength.HasValue) summary.AppendLine($"焦距: {meta.ExifFocalLength.Value:0.#}mm");
        }
        if (!string.IsNullOrWhiteSpace(meta.ColorProfileName)) summary.AppendLine($"色彩配置文件: {meta.ColorProfileName}");
        if (meta.DpiX > 0) summary.AppendLine($"分辨率: {Math.Round(meta.DpiX)} DPI");
        summary.AppendLine($"拍摄/创建时间: {PART_InfoDateCreated.Text}");
        summary.AppendLine($"修改时间: {meta.FileLastWriteTimeFormatted}");
        summary.AppendLine($"完整路径: {meta.FilePath}");
        _cachedImageInfoSummary = summary.ToString();
    }

    #endregion // Image Info Overlay

    // how long to wait for the first paint before giving up (hidden window may never draw)
    private static readonly TimeSpan _sequentialRenderTimeout = TimeSpan.FromSeconds(2);

    // > 0 while a photo load + paint is in flight, Sequential browsing only
    private int _sequentialLoadDepth;

    // land on the last photo of the list being searched (backward folder switch)
    private bool _selectLastOnSearch;

    // the message's own margin, before it is inset to keep clear of the image info bar
    private Thickness _messageMargin;


    public MainWindowViewModel VM => (MainWindowViewModel)DataContext!;


    /// <summary>
    /// Gets whether a photo is currently loading and rendering under
    /// <see cref="BrowsingMode.Sequential"/>.
    /// </summary>
    public bool IsPhotoLoadInProgress => Volatile.Read(ref _sequentialLoadDepth) > 0;


    public MainWindowView()
    {
        InitializeComponent();

        PART_Viewer.ContextMenu = new();
    }



    #region Control Events

    protected override void OnLoaded(RoutedEventArgs e)
    {
        // apply app layout from settings
        ApplyAppLayout();

        base.OnLoaded(e);

        InitTitleBarPlatform();

        // drag-drop events
        DragDrop.SetAllowDrop(PART_Viewer, true);
        DragDrop.AddDragOverHandler(PART_Viewer, PART_Viewer_DragOver);
        DragDrop.AddDropHandler(PART_Viewer, PART_Viewer_Drop);

        Core.Config.PropertyChanged += Config_PropertyChanged;
        Core.Photos.Items.CollectionChanged += GalleryItems_CollectionChanged;
        PART_ViewerWrapper.SizeChanged += ViewerWrapper_SizeChanged;
        UpdateGalleryOverlayWidth();
        Core.Photos.FileWatcherChanged += Photos_FileWatcherChanged;
        PART_Viewer.PhotoLoading += PART_Viewer_PhotoLoading;
        PART_Viewer.ZoomChanged += PART_Viewer_ZoomChanged;
        UpdateZoomSlider();
        PART_Viewer.NavButtonClicked += PART_Viewer_NavButtonClicked;
        PART_Viewer.ViewerPointerClicked += PART_Viewer_ViewerPointerClicked;
        PART_Viewer.ViewerMouseWheel += PART_Viewer_ViewerMouseWheel;
        PART_Viewer.ContextMenu?.Opened += PART_Viewer_ContextMenu_Opened;

        // motion/live photo overlay button
        PART_ToolHost.PropertyChanged += PART_ToolHost_PropertyChanged;
        UpdateMotionButtonState();

        // the in-app message keeps clear of the image info bar
        _messageMargin = PART_Message.Margin;
        PART_ImageInfoOverlay.PropertyChanged += PART_ImageInfoOverlay_PropertyChanged;
        UpdateMessageMargin();

        // hook viewer events for external tool broadcasting
        PART_Viewer.PhotoLoading += Core.Viewer_PhotoLoadingForPlugins;
        PART_Viewer.ViewerPointerMoved += Core.Viewer_PointerMovedForPlugins;
        PART_Viewer.ViewerPointerPressed += Core.Viewer_PointerPressedForPlugins;
        PART_Viewer.SelectionChanged += Core.Viewer_SelectionChangedForPlugins;
        PART_Viewer.PhotoFrameChanged += Core.Viewer_FrameChangedForPlugins;


        // load image from command line arguments
        LoadImagesFromCmdArgs();

        StartupTrace.Mark("MainView:loaded");
        StartupTrace.Flush();
    }


    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);

        if (App.MainWindow is not null)
        {
            App.MainWindow.PropertyChanged -= MainWindow_PropertyChanged;
        }

        // drag-drop events
        DragDrop.RemoveDragOverHandler(PART_Viewer, PART_Viewer_DragOver);
        DragDrop.RemoveDropHandler(PART_Viewer, PART_Viewer_Drop);

        Core.Config.PropertyChanged -= Config_PropertyChanged;
        Core.Photos.Items.CollectionChanged -= GalleryItems_CollectionChanged;
        PART_ViewerWrapper.SizeChanged -= ViewerWrapper_SizeChanged;
        Core.Photos.FileWatcherChanged -= Photos_FileWatcherChanged;
        PART_Viewer.PhotoLoading -= PART_Viewer_PhotoLoading;
        PART_Viewer.ZoomChanged -= PART_Viewer_ZoomChanged;
        PART_Viewer.NavButtonClicked -= PART_Viewer_NavButtonClicked;
        PART_Viewer.ViewerPointerClicked -= PART_Viewer_ViewerPointerClicked;
        PART_Viewer.ViewerMouseWheel -= PART_Viewer_ViewerMouseWheel;
        PART_Viewer.ContextMenu?.Opened -= PART_Viewer_ContextMenu_Opened;

        // motion/live photo overlay button
        PART_ToolHost.PropertyChanged -= PART_ToolHost_PropertyChanged;

        PART_ImageInfoOverlay.PropertyChanged -= PART_ImageInfoOverlay_PropertyChanged;

        // unhook viewer events for external tools
        PART_Viewer.PhotoLoading -= Core.Viewer_PhotoLoadingForPlugins;
        PART_Viewer.ViewerPointerMoved -= Core.Viewer_PointerMovedForPlugins;
        PART_Viewer.ViewerPointerPressed -= Core.Viewer_PointerPressedForPlugins;
        PART_Viewer.SelectionChanged -= Core.Viewer_SelectionChangedForPlugins;
        PART_Viewer.PhotoFrameChanged -= Core.Viewer_FrameChangedForPlugins;
    }


    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);

        UpdateGalleryWidth();
    }


    private void Config_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Config.ThumbnailSize)) UpdateGalleryOverlayWidth();
        if (e.PropertyName == nameof(Config.Layout) || e.PropertyName == nameof(Config.ShowToolbar))
        {
            ApplyAppLayout();
        }
        else if (e.PropertyName == nameof(Config.EnableFileWatcher))
        {
            // set file watcher
            AppAPIProvider.SetFileWatcher(Core.Config.EnableFileWatcher);
        }
        else if (e.PropertyName is nameof(Config.BackgroundColor)
            or nameof(Config.SlideshowBackgroundColor)
            or nameof(Config.EnableSlideshow))
        {
            Core.UpdateViewerBackgroundBrushResource();
        }
    }


    private void PART_Viewer_DragOver(object? sender, DragEventArgs e)
    {
        // if drag data contains files or folders
        if (e.DataTransfer.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.Copy | DragDropEffects.Link;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }


    private async void PART_Viewer_Drop(object? sender, DragEventArgs e)
    {
        // if drag data contains files or folders
        if (e.DataTransfer.TryGetFiles() is not IStorageItem[] sItems) return;

        // 1. get dropped paths
        var paths = sItems.Select(i => i.Path.LocalPath).ToArray();


        // 2. load multiple paths
        if (paths.Length > 1)
        {
            PrepareLoadPhotoList(paths,
                currentFilePath: null, disposeForegroundShell: true, reloadInitPhoto: true);
            return;
        }


        // 3. load single file path
        // 3.1 get foreground shell; always captured, since a search window is the only source of its own result list
        Core.ShellProvider?.ForegroundShell = Core.ShellProvider?.GetForegroundWindowView();
        Core.UpdateInitImagePath(paths[0]);

        // 3.2 open the path
        _ = await Core.API.RunApiAsync(API.IG_OpenPath, paths[0]);
    }


    private void Photos_FileWatcherChanged(PhotoManager sender, FileWatcherChangedEventArgs e)
    {
        if (e.ChangeType == ChangeType.LOG) return;

        Dispatcher.UIThread.Post(() =>
        {
            switch (e.ChangeType)
            {
                case ChangeType.CREATED:
                    HandleFileWatcher_FilesAdded(e);
                    break;

                case ChangeType.DELETED:
                    HandleFileWatcher_FilesDeleted(e);
                    break;

                case ChangeType.CHANGED:
                    HandleFileWatcher_FilesChanged(e);
                    break;

                case ChangeType.RENAMED:
                    HandleFileWatcher_FilesRenamed(e);
                    break;
            }
        });
    }


    private void HandleFileWatcher_FilesAdded(FileWatcherChangedEventArgs e)
    {
        // gallery is data-bound to Core.Photos.Items, so items are already added;
        // we only need to scroll/display the last added file if the user opted in.
        if (e.FilePaths.Count == 0) return;

        // scroll gallery to the newly added file if configured
        if (Core.Config.EnableAutoOpenNewAddedImage)
        {
            var lastAdded = e.FilePaths[^1];
            var newIndex = Core.Photos.IndexOf(lastAdded);

            if (newIndex >= 0)
            {
                var photo = Core.Photos.Select(newIndex);
                _ = ViewPhotoAsync(photo);
            }
        }
        else
        {
            // re-select current photo to update its index (list shifted by inserts)
            _ = Core.Photos.Select(Core.Photos.CurrentFilePath);
            PART_Gallery.ScrollToItem(Core.Photos.CurrentIndex);
        }
    }


    private void HandleFileWatcher_FilesDeleted(FileWatcherChangedEventArgs e)
    {
        if (e.FilePaths.Count == 0) return;

        // if the currently viewed photo was deleted
        if (!string.IsNullOrEmpty(e.AffectedCurrentFilePath))
        {
            AppAPIProvider.ViewPhotoAfterCurrentRemoved();
        }
        else
        {
            // re-select current photo to fix its index after list shifted
            _ = Core.Photos.Select(Core.Photos.CurrentFilePath);
            PART_Gallery.ScrollToItem(Core.Photos.CurrentIndex);
        }

        // update init photo path if it was deleted
        if (Core.Photos.InitPhoto is not null)
        {
            for (var i = 0; i < e.FilePaths.Count; i++)
            {
                if (string.Equals(e.FilePaths[i], Core.Photos.InitPhoto.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    var dirPath = Path.GetDirectoryName(e.FilePaths[i]) ?? string.Empty;
                    Core.Photos.InitPhoto = !string.IsNullOrEmpty(dirPath)
                        ? new Photo(dirPath)
                        : null;
                    break;
                }
            }
        }
    }


    private void HandleFileWatcher_FilesChanged(FileWatcherChangedEventArgs e)
    {
        if (e.FilePaths.Count == 0) return;

        for (var i = 0; i < e.FilePaths.Count; i++)
        {
            var filePath = e.FilePaths[i];
            var photoIndex = Core.Photos.IndexOf(filePath);
            var photo = Core.Photos.Get(photoIndex);
            if (photo is null) continue;

            // force thumbnail reload
            PART_Gallery.LoadThumbnail(photoIndex, false);

            // if it's the currently viewed photo, reload it
            if (Core.Photos.IsSelected(filePath))
            {
                _ = PART_Viewer.SetPhotoAsync(photo, new PhotoLoadingOptions
                {
                    ResetZoom = false,
                    UseCache = false,
                    Channels = Core.ColorChannels,
                });
            }
        }
    }


    private static void HandleFileWatcher_FilesRenamed(FileWatcherChangedEventArgs e)
    {
        if (e.FilePaths.Count == 0 || e.OldFilePaths is null) return;

        var currentNeedsRefresh = false;
        for (var i = 0; i < e.FilePaths.Count; i++)
        {
            var newPath = e.FilePaths[i];
            var oldPath = i < e.OldFilePaths.Count ? e.OldFilePaths[i] : null;

            // update init photo if it was renamed
            if (Core.Photos.InitPhoto is not null
                && oldPath is not null
                && string.Equals(oldPath, Core.Photos.InitPhoto.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                Core.Photos.InitPhoto = new Photo(newPath);
            }

            // check if the currently viewed photo was renamed
            if (Core.Photos.IsSelected(newPath))
            {
                currentNeedsRefresh = true;
            }
        }

        // refresh the viewer title/info if the current photo was renamed
        if (currentNeedsRefresh)
        {
            // re-select to refresh bindings for current photo name/path
            _ = Core.Photos.Select(Core.Photos.CurrentIndex);
        }
    }


    private void PART_Viewer_PhotoLoading(ViewerControl sender, PhotoLoadingEventArgs e)
    {
        // Note: events are not fired in order

        // 1. handle loading error first
        if (e.Photo.Error is not null)
        {
            var heading = $"{Core.Lang[LangId._PicMain_ErrorText]} 😶";
            var err = BHelper.GetInAppError(e.Photo.Error);

            // show error message
            _ = PART_Message.ShowAsync(err.DebugInfo, heading, err.Details, 0);
        }

        // 2. handle photo loading
        else if (e.State == PhotoState.Preview)
        {
            // show loading message after 2s
            _ = PART_Message.ShowAsync(Core.Lang[LangId._Loading], durationMs: 0, delayMs: 2000);
        }

        // 3. handle photo loaded
        else if (e.State == PhotoState.Loaded)
        {
            // clear in-app message
            _ = PART_Message.ShowAsync(null);
        }

        UpdateMotionButtonState();
    }


    private void PART_ToolHost_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == BoundsProperty) UpdateGalleryOverlayMargin();
        if (e.Property == ToolHostControl.PluginContentProperty)
        {
            UpdateMotionButtonState();
        }
    }


    private void PART_ImageInfoOverlay_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // the bar's reach, or the edge it sits on
        var isPlacementChanged = e.Property == ImageInfoOverlayControl.CoveredHeightProperty
            || e.Property == VerticalAlignmentProperty;
        if (!isPlacementChanged) return;

        UpdateMessageMargin();
    }


    private void PART_Viewer_ZoomChanged(ViewerControl sender, ViewerZoomEventArgs e)
    {
        UpdateZoomSlider();
        // update window fit
        if (Core.Config.EnableWindowFit
            && e.ChangeSource != ZoomChangeSource.SizeChanged
            && e.ChangeSource != ZoomChangeSource.FrameChanged
            && (e.IsManualZoom || e.IsZoomModeChange))
        {
            AppAPIProvider.ApplyWindowFitMode(e.ChangeSource == ZoomChangeSource.ZoomMode);
        }
    }


    private async void PART_Viewer_NavButtonClicked(ViewerControl sender, NavButtonClickedEventArgs e)
    {
        if (e.Direction == NavButtonDirection.Right)
        {
            _ = await Core.API.RunApiAsync(API.IG_ViewNext);
        }
        else
        {
            _ = await Core.API.RunApiAsync(API.IG_ViewPrevious);
        }
    }


    private async void PART_Viewer_ViewerPointerClicked(ViewerControl sender, ViewerPointerClickEventArgs e)
    {
        // get pointer click action from user settings
        var action = Core.Config.MouseClickActions.GetValueOrDefault(e.ClickEvent);

        // fallback to the default action
        if (action is null)
        {
            action = Config.DefaultMouseClickActions.GetValueOrDefault(e.ClickEvent);
        }

        await Core.API!.RunActionAsync(action);
    }


    private async void PART_Viewer_ViewerMouseWheel(ViewerControl sender, ViewerMouseWheelEventArgs e)
    {
        // get mouse wheel action from user settings
        if (!Core.Config.MouseWheelActions.TryGetValue(e.WheelEvent, out var wheelAction))
        {
            // fallback to the default action
            _ = Config.DefaultMouseWheelActions.TryGetValue(e.WheelEvent, out wheelAction);
        }

        switch (wheelAction)
        {
            case MouseWheelAction.Zoom:
                // respect a zoom feature lock (this path skips the RunApiAsync lock gate)
                if (!FeatureManager.IsZoomLocked())
                    _ = PART_Viewer.ZoomByDeltaToPoint(e.Delta, e.Position);
                break;

            case MouseWheelAction.PanVertically:
                if (!FeatureManager.IsPanLocked())
                    PART_Viewer.PanTo(0, -e.Delta, e.Position);
                break;

            case MouseWheelAction.PanHorizontally:
                if (!FeatureManager.IsPanLocked())
                    PART_Viewer.PanTo(-e.Delta, 0, e.Position);
                break;

            case MouseWheelAction.BrowseImages:
                if (e.Delta > 0) _ = await Core.API.RunApiAsync(API.IG_ViewPrevious);
                else _ = await Core.API.RunApiAsync(API.IG_ViewNext);
                break;

            case MouseWheelAction.DoNothing:
            default:
                break;
        }
    }


    private void PART_GalleryResizer_DragCompleted(object? sender, VectorEventArgs e)
    {
        var panelEl = PART_Gallery.FindVirtualPanel();
        if (panelEl is null) return;

        // save number of columns
        Core.Config.GalleryColumns = (uint)panelEl.ColumnsPerRow;
    }


    private void PART_Viewer_ContextMenu_Opened(object? sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu mnuContext) return;
        mnuContext.Items.Clear();

        var hasClipboardImage = Core.ClipboardImage != null;
        var imageNotFound = !File.Exists(Core.Photos.CurrentFilePath);


        #region Menu group: Slideshow
        if (Core.Slideshow?.IsRunning == true)
        {
            mnuContext.Items.Add(new PhMenuItem
            {
                LangKey = LangId._MnuPauseResumeSlideshow,
                Command = Core.API.GetApiCommand(API.IG_ToggleSlideshowPlayback),
                HotkeyText = AppAPIProvider.GetMenuHotkeyText(LangId._MnuPauseResumeSlideshow),
            });
            mnuContext.Items.Add(new PhMenuItem
            {
                LangKey = LangId._MnuExitSlideshow,
                Command = Core.API.GetApiCommand(API.IG_ToggleSlideshow),
                CommandParameter = false,
                HotkeyText = string.Join(", ", [
                    new Hotkey(Key.Escape),
                    AppAPIProvider.GetMenuHotkeyText(LangId.Menu_MnuSlideshow)
                ]),
            });
            mnuContext.Items.Add(new PhMenuItem
            {
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = Core.Config.EnableSlideshowCountdown,
                LangKey = LangId._MnuToggleCountdown,
                Command = Core.API.GetApiCommand(API.IG_ToggleSlideshowCountdown),
                CommandParameter = !Core.Config.EnableSlideshowCountdown,
            });
            mnuContext.Items.Add("-"); //------------
        }
        #endregion // Menu group: Slideshow


        #region Menu group: Layout
        // menu toolbar
        mnuContext.Items.Add(new PhMenuItem
        {
            LangKey = LangId.Menu_MnuToggleToolbar,
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = Core.Config.ShowToolbar,
            Command = Core.API.GetApiCommand(API.IG_ToggleToolbar),
            HotkeyText = AppAPIProvider.GetMenuHotkeyText(LangId.Menu_MnuToggleToolbar),
        });
        // menu Top most
        mnuContext.Items.Add(new PhMenuItem
        {
            LangKey = LangId.Menu_MnuToggleTopMost,
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = Core.Config.EnableWindowTopMost,
            Command = Core.API.GetApiCommand(API.IG_ToggleWindowTopMost),
            HotkeyText = AppAPIProvider.GetMenuHotkeyText(LangId.Menu_MnuToggleTopMost),
        });
        #endregion // Menu group: Layout


        #region Menu group: Loading orders
        if (!imageNotFound) mnuContext.Items.Add("-");
        if (!imageNotFound && !hasClipboardImage)
        {
            var mnuLoadingOrders = new PhMenuItem
            {
                LangKey = LangId.Menu_MnuLoadingOrders,
                HotkeyText = AppAPIProvider.GetMenuHotkeyText(LangId.Menu_MnuLoadingOrders),
            };
            mnuContext.Items.Add(mnuLoadingOrders);


            // use Explorer sort order
            if (Core.ShellProvider is not null && BHelper.OS == OSType.Windows)
            {
                mnuLoadingOrders.Items.Add(new PhMenuItem
                {
                    LangKey = LangId.Settings_EnableExplorerSortOrder,
                    ToggleType = MenuItemToggleType.CheckBox,
                    IsChecked = Core.Config.EnableExplorerSortOrder,
                    Command = Core.API.GetApiCommand(API.IG_ToggleExplorerSortOrder),
                    HotkeyText = AppAPIProvider.GetMenuHotkeyText(LangId.Settings_EnableExplorerSortOrder),
                });
                mnuLoadingOrders.Items.Add("-");
            }


            // order by
            foreach (var orderBy in Enum.GetNames<ImageOrderBy>())
            {
                mnuLoadingOrders.Items.Add(new PhMenuItem
                {
                    LangKey = Lang.GetKey($"{nameof(ImageOrderBy)}_{orderBy}"),
                    ToggleType = MenuItemToggleType.Radio,
                    IsChecked = Core.Config.ImageLoadingOrder == Enum.Parse<ImageOrderBy>(orderBy),
                    Command = Core.API.GetApiCommand(API.IG_SetLoadingOrderBy),
                    CommandParameter = orderBy,
                });
            }

            // order type
            mnuLoadingOrders.Items.Add("-");
            foreach (var orderType in Enum.GetNames<ImageOrderType>())
            {
                mnuLoadingOrders.Items.Add(new PhMenuItem
                {
                    LangKey = Lang.GetKey($"{nameof(ImageOrderType)}_{orderType}"),
                    ToggleType = MenuItemToggleType.Radio,
                    IsChecked = Core.Config.ImageLoadingOrderType == Enum.Parse<ImageOrderType>(orderType),
                    Command = Core.API.GetApiCommand(API.IG_SetLoadingOrderType),
                    CommandParameter = orderType,
                });
            }
        }
        #endregion // Menu group: Loading orders


        #region Menu group: View channels
        if (!PART_Viewer.IsImageAnimating && (!imageNotFound || hasClipboardImage))
        {
            var mnuChannels = new PhMenuItem
            {
                LangKey = LangId.Menu_MnuViewChannels,
                HotkeyText = AppAPIProvider.GetMenuHotkeyText(LangId.Menu_MnuViewChannels),
            };
            mnuContext.Items.Add(mnuChannels);

            foreach (var item in PART_Toolbar.PART_MnuViewChannels.Items)
            {
                if (item is not PhMenuItem oriItem) continue;

                var mnuItem = new PhMenuItem
                {
                    Header = oriItem.Header,
                    ToggleType = oriItem.ToggleType,
                    HotkeyText = oriItem.HotkeyText,
                    CommandParameter = oriItem.CommandParameter,
                };
                if (Enum.TryParse<ColorChannels>((string)oriItem.CommandParameter!, true, out var val))
                {
                    mnuItem.IsChecked = Core.ColorChannels.HasFlag(val);
                }
                mnuItem.Click += PART_Toolbar.MainMenu_ViewChannelItem_Click;
                mnuChannels.Items.Add(mnuItem);
            }
        }
        #endregion // Menu group: View channels


        #region Menu group: Edit
        if (!imageNotFound) mnuContext.Items.Add("-");
        if (!imageNotFound || hasClipboardImage)
        {
            var mnuEdit = new PhMenuItem
            {
                LangKey = LangId.Menu_MnuEdit,
                Command = Core.API.GetApiCommand(API.IG_OpenEditingApp),
                HotkeyText = AppAPIProvider.GetMenuHotkeyText(LangId.Menu_MnuEdit),
            };

            EditingApp.UpdateAppNameForMenuEdit(mnuEdit);
            mnuContext.Items.Add(mnuEdit);
        }
        #endregion // Menu group: Edit


        #region Menu group: Desktop wallpaper, lock screen
        if ((!imageNotFound && !Core.Photos.IsCurrentError) || hasClipboardImage)
        {
            mnuContext.Items.Add(new PhMenuItem
            {
                LangKey = LangId.Menu_MnuSetDesktopBackground,
                Command = Core.API.GetApiCommand(API.IG_SetDesktopBackground),
                HotkeyText = AppAPIProvider.GetMenuHotkeyText(LangId.Menu_MnuSetDesktopBackground),
            });

            if (BHelper.OS == OSType.Windows)
            {
                mnuContext.Items.Add(new PhMenuItem
                {
                    LangKey = LangId.Menu_MnuSetLockScreen,
                    Command = Core.API.GetApiCommand(API.IG_SetLockScreenImage),
                    HotkeyText = AppAPIProvider.GetMenuHotkeyText(LangId.Menu_MnuSetLockScreen),
                });
            }
        }
        #endregion // Menu group: Desktop wallpaper, lock screen


        #region Menu group: Clipboard
        mnuContext.Items.Add(new PhMenuItem("-")); //------------

        mnuContext.Items.Add(new PhMenuItem
        {
            LangKey = LangId.Menu_MnuPasteImage,
            Command = Core.API.GetApiCommand(API.IG_PasteImage),
            HotkeyText = AppAPIProvider.GetMenuHotkeyText(LangId.Menu_MnuPasteImage),
        });
        mnuContext.Items.Add(new PhMenuItem
        {
            LangKey = LangId.Menu_MnuCopyImagePixels,
            Command = Core.API.GetApiCommand(API.IG_CopyImagePixels),
            HotkeyText = AppAPIProvider.GetMenuHotkeyText(LangId.Menu_MnuCopyImagePixels),
        });

        if (!imageNotFound && !hasClipboardImage)
        {
            mnuContext.Items.Add(new PhMenuItem
            {
                LangKey = LangId.Menu_MnuCopyPath,
                Command = Core.API.GetApiCommand(API.IG_CopyImagePath),
                HotkeyText = AppAPIProvider.GetMenuHotkeyText(LangId.Menu_MnuCopyPath),
            });
            mnuContext.Items.Add(new PhMenuItem
            {
                LangKey = LangId.Menu_MnuCopyFile,
                Command = Core.API.GetApiCommand(API.IG_CopyFiles),
                HotkeyText = AppAPIProvider.GetMenuHotkeyText(LangId.Menu_MnuCopyFile),
            });
            if (BHelper.OS != OSType.Mac) {
                mnuContext.Items.Add(new PhMenuItem
                {
                    LangKey = LangId.Menu_MnuCutFile,
                    Command = Core.API.GetApiCommand(API.IG_CutFiles),
                    HotkeyText = AppAPIProvider.GetMenuHotkeyText(LangId.Menu_MnuCutFile),
                });
            }
            mnuContext.Items.Add(new PhMenuItem
            {
                LangKey = LangId.Menu_MnuClearClipboard,
                Command = Core.API.GetApiCommand(API.IG_ClearClipboard),
                HotkeyText = AppAPIProvider.GetMenuHotkeyText(LangId.Menu_MnuClearClipboard),
            });
        }
        #endregion // Menu group: Clipboard


        #region Menu group: File Operation
        if (!imageNotFound && !hasClipboardImage)
        {
            mnuContext.Items.Add(new PhMenuItem("-")); //------------
            mnuContext.Items.Add(new PhMenuItem
            {
                LangKey = LangId.Menu_MnuRename,
                Command = Core.API.GetApiCommand(API.IG_Rename),
                HotkeyText = AppAPIProvider.GetMenuHotkeyText(LangId.Menu_MnuRename),
            });
            mnuContext.Items.Add(new PhMenuItem
            {
                LangKey = LangId.Menu_MnuMoveToRecycleBin,
                Command = Core.API.GetApiCommand(API.IG_Delete),
                CommandParameter = "true",
                HotkeyText = AppAPIProvider.GetMenuHotkeyText(LangId.Menu_MnuMoveToRecycleBin),
            });
            mnuContext.Items.Add(new PhMenuItem
            {
                LangKey = LangId.Menu_MnuOpenLocation,
                Command = Core.API.GetApiCommand(API.IG_OpenLocation),
                HotkeyText = AppAPIProvider.GetMenuHotkeyText(LangId.Menu_MnuOpenLocation),
            });
            mnuContext.Items.Add(new PhMenuItem
            {
                LangKey = LangId.Menu_MnuImageProperties,
                Command = Core.API.GetApiCommand(API.IG_OpenProperties),
                HotkeyText = AppAPIProvider.GetMenuHotkeyText(LangId.Menu_MnuImageProperties),
            });
        }
        #endregion // Menu group: File Operation


        // menu Exit
        mnuContext.Items.Add("-"); //------------
        mnuContext.Items.Add(new PhMenuItem
        {
            LangKey = LangId.Menu_MnuExit,
            Command = Core.API.GetApiCommand(API.IG_Exit),
            HotkeyText = AppAPIProvider.GetMenuHotkeyText(LangId.Menu_MnuExit),
        });


        // Hide locked menu items
        FeatureManager.HideLockedMenuItems(mnuContext.Items);
    }


    #endregion // Control Events



    #region Control Methods

    private async void LoadImagesFromCmdArgs()
    {
        var pathToLoad = Core.InputImagePathFromArgs;

        // check for last seen image
        if (string.IsNullOrEmpty(pathToLoad)
            && Core.Config.EnableLastSeenImage
            && BHelper.CheckPath(Core.Config.LastSeenImagePath) == PathType.File)
        {
            pathToLoad = Core.Config.LastSeenImagePath;
        }

        // check for Welcome image
        if (string.IsNullOrEmpty(pathToLoad))
        {
            if (Core.Config.EnableWelcomeImage)
            {
                pathToLoad = BHelper.BaseDir("default.webp");
            }
            else
            {
                return;
            }
        }

        // a non-built-in type may need a codec plugin still loading; wait for discovery
        var ext = Path.GetExtension(pathToLoad);
        if (!string.IsNullOrEmpty(ext)
            && !Config.DefaultFileFormats.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            StartupTrace.Mark("plugins:await:begin");
            try { await Core.PluginDiscoveryTask; } catch { }
            StartupTrace.Mark("plugins:await:end");
            StartupTrace.Flush();
        }

        // start loading path with the foreground shell
        PrepareLoadPhotoList([pathToLoad],
            currentFilePath: null, disposeForegroundShell: false, reloadInitPhoto: true);
    }


    /// <param name="selectLastPhoto">
    /// Lands on the last photo of the loaded list instead of the first. The list order is only
    /// known after the search runs (it may come from Explorer), so the choice can't be a path.
    /// </param>
    public void PrepareLoadPhotoList(ICollection<string> inputPaths, string? currentFilePath, bool disposeForegroundShell, bool reloadInitPhoto, bool selectLastPhoto = false)
    {
        _selectLastOnSearch = selectLastPhoto;

        // dispose the foreground shell if requested
        if (disposeForegroundShell) Core.ShellProvider?.ForegroundShell = null;


        // check if we should load images from foreground window
        var useForegroundWindow = Core.ShellProvider?.CanUseForegroundShell() ?? false;
        var foregroundShell = useForegroundWindow
            ? Core.ShellProvider?.ForegroundShell
            : null;


        Dispatcher.UIThread.Post(async () =>
        {
            // resolve first: a shortcut must contribute its target's type, never ".lnk"
            var resolvedPaths = inputPaths.Select(BHelper.ResolvePath).ToList();
            var resolvedCurrentPath = BHelper.ResolvePath(currentFilePath);

            // include the opened file's own type so it's listed even before its plugin loads
            var allowedExts = Core.GetSupportedFileExtensions();
            AddFileExtension(allowedExts, resolvedCurrentPath);
            foreach (var p in resolvedPaths) AddFileExtension(allowedExts, p);

            // start loading files
            var searchOptions = new FileSearchOptions()
            {
                AllowedExtensions = allowedExts,
                UseExplorerSortOrder = Core.Config.EnableExplorerSortOrder,
                ForegroundShell = foregroundShell,
                SearchSubDirectories = Core.Config.EnableSubfoldersLoading,
                GroupByDir = Core.Config.EnableImageFolderGrouping,
                IncludeHidden = Core.Config.EnableHiddenImagesLoading,
                OrderBy = Core.Config.ImageLoadingOrder,
                OrderType = Core.Config.ImageLoadingOrderType,
            };
            var initPhoto = Core.Photos.StartLoadingFiles(resolvedPaths, resolvedCurrentPath, searchOptions, Files_Searched, reloadInitPhoto);


            if (reloadInitPhoto && initPhoto is not null)
            {
                _ = ViewPhotoAsync(initPhoto);
            }
        });
    }


    /// <summary>
    /// Adds a file path's extension to the allowed-extensions set (no-op for a directory or empty path).
    /// </summary>
    private static void AddFileExtension(HashSet<string> exts, string? path)
    {
        if (string.IsNullOrEmpty(path)) return;

        var ext = Path.GetExtension(path);
        if (!string.IsNullOrEmpty(ext)) exts.Add(ext);
    }


    private void Files_Searched(FileSearchingEventArgs e)
    {
        // The Win32 provider posts progress to the UI thread, the base one (Linux/macOS) calls
        // straight from its worker, so the whole body must marshal: Items is bound to the gallery.
        Dispatcher.UIThread.Invoke(() =>
        {
            try
            {
                Files_Searched__(e);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌❌❌ {nameof(Files_Searched)}: {ex.Message}");
            }
        });
    }


    private void Files_Searched__(FileSearchingEventArgs e)
    {
        var isEmptyList = Core.Photos.Count == 0;

        // add files to list
        Core.Photos.Add(e.Results);


        // display the last file in a folder; re-applied on every batch, so it settles on the
        // real last item once the search is done
        if (_selectLastOnSearch)
        {
            var lastIndex = (int)Core.Photos.Count - 1;
            Core.Photos.InitPhoto = Core.Photos.Select(lastIndex);
            _ = ViewPhotoAsync(Core.Photos.InitPhoto, true, false);
        }
        // the init photo owns the selection for every batch, incl. the later sub-dir ones
        else if (Core.Photos.InitPhoto is not null)
        {
            // if we haven't found current index for the init photo yet
            if (Core.Photos.CurrentIndex == -1)
            {
                // find index of the init photo and select it
                _ = Core.Photos.Select(Core.Photos.InitPhoto.FilePath);

                // save the init photo to the list
                var initIndex = Core.Photos.CurrentIndex;
                if (initIndex >= 0 && initIndex < Core.Photos.Items.Count)
                {
                    Core.Photos.Items[initIndex]?.Dispose();
                    Core.Photos.Items[initIndex] = Core.Photos.InitPhoto;
                    Core.Photos.Items[initIndex].IsCurrent = true;
                }
            }
        }
        // display the first file in a folder
        else
        {
            Core.Photos.InitPhoto = Core.Photos.Select(0);
            _ = ViewPhotoAsync(Core.Photos.InitPhoto, true, false);
        }


        // Gallery: scroll to the selected item
        if (isEmptyList)
        {
            // set file watcher
            AppAPIProvider.SetFileWatcher(Core.Config.EnableFileWatcher);

            Dispatcher.UIThread.Post(() =>
            {
                // set photo to the viewer
                PART_Gallery.ScrollToItem(Core.Photos.CurrentIndex);
            });
        }
    }


    public async Task ViewPhotoAsync(Photo? photo, bool useCache = true, bool scrollToThumbnail = true, bool resetZoom = true,
        TransitionRequest? transition = null)
    {
        // Sequential mode: close the navigation gate here, not inside the post below,
        // so the next key repeat already sees a load in flight
        var isSequential = Core.Config.BrowsingMode == BrowsingMode.Sequential;
        if (isSequential) Interlocked.Increment(ref _sequentialLoadDepth);

        // the old neighborhood's cache pass would compete with this load; a new one starts once it is shown
        Core.Photos.CancelCaching();

        // clear the current in-app message
        _ = PART_Message.ClearAsync();

        Core.DisposeClipboardPhoto();
        Core.ImageTransform.Clear();

        // set read options for photo; a cached photo shows without a decode, so name its opening frame here
        if (photo is not null)
        {
            photo.ReadOptions = PhotoReadOptions.FromConfig(photo.GetDefaultFrameIndex());
        }

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                // apply user settings to the viewer
                PART_Viewer.EnableImagePreview = Core.Config.EnableImagePreview;

                if (scrollToThumbnail)
                {
                    // set photo to the viewer
                    PART_Gallery.ScrollToItem(Core.Photos.CurrentIndex);
                }

                await PART_Viewer.SetPhotoAsync(photo, new PhotoLoadingOptions
                {
                    UseCache = useCache,
                    ResetZoom = resetZoom,
                    Channels = Core.ColorChannels,
                    Transition = transition,
                });

                // reopening the gate at decode time is too early: the next navigation would
                // dispose the image before it is ever painted
                if (isSequential)
                {
                    await PART_Viewer.WaitForPhotoRenderedAsync(_sequentialRenderTimeout);
                }
            }
            finally
            {
                if (isSequential) Interlocked.Decrement(ref _sequentialLoadDepth);
            }

            // trigger background caching of adjacent photos
            // after the current photo finishes loading
            Core.Photos.RequestCacheAround(Core.Photos.CurrentIndex);
        });
    }



    /// <summary>
    /// Updates app layout.
    /// </summary>
    public void ApplyAppLayout()
    {
        // 1. read control's layouts from setting
        var toolbarPos = Config.GetControlLayout(LayoutControl.Toolbar);
        var galleryPos = Config.GetControlLayout(LayoutControl.Gallery);


        // 2. create layout
        if (toolbarPos == LayoutPosition.Top)
        {
            PART_Toolbar.ItemTooltipPlacement = PlacementMode.Bottom;

            if (galleryPos == LayoutPosition.Top)
            {
                PART_Layout.RowDefinitions = new("Auto, Auto, *");
                PART_Layout.ColumnDefinitions = new("*");

                PART_GalleryResizer.IsVisible = false;
                PART_Gallery.MaxWidth = double.PositiveInfinity;
                PART_Gallery.ViewMode = PhVirtualizingUniformPanelViewMode.FilmStrip;
                PART_Gallery.ItemTooltipPlacement = PlacementMode.Bottom;


                Grid.SetRow(PART_Gallery, 1);
                Grid.SetRow(PART_ViewerWrapper, 2);

                Grid.SetColumn(PART_Gallery, 0);
                Grid.SetColumn(PART_ViewerWrapper, 0);
            }
            else if (galleryPos == LayoutPosition.Bottom)
            {
                PART_Layout.RowDefinitions = new("Auto, *, Auto");
                PART_Layout.ColumnDefinitions = new("*");

                PART_GalleryResizer.IsVisible = false;
                PART_Gallery.MaxWidth = double.PositiveInfinity;
                PART_Gallery.ViewMode = PhVirtualizingUniformPanelViewMode.FilmStrip;
                PART_Gallery.ItemTooltipPlacement = PlacementMode.Top;


                Grid.SetRow(PART_Gallery, 2);
                Grid.SetRow(PART_GalleryResizer, 0);
                Grid.SetRow(PART_ViewerWrapper, 1);

                Grid.SetColumn(PART_Gallery, 0);
                Grid.SetColumn(PART_GalleryResizer, 0);
                Grid.SetColumn(PART_ViewerWrapper, 0);
            }
            else if (galleryPos == LayoutPosition.Left)
            {
                PART_Layout.RowDefinitions = new("Auto, *");
                PART_Layout.ColumnDefinitions = new("Auto, Auto, *");

                PART_GalleryResizer.IsVisible = true;
                PART_Gallery.ViewMode = PhVirtualizingUniformPanelViewMode.Gallery;
                PART_Gallery.ItemTooltipPlacement = PlacementMode.Pointer;


                Grid.SetRow(PART_Gallery, 1);
                Grid.SetRow(PART_GalleryResizer, 1);
                Grid.SetRow(PART_ViewerWrapper, 1);

                Grid.SetColumn(PART_Gallery, 0);
                Grid.SetColumn(PART_GalleryResizer, 1);
                Grid.SetColumn(PART_ViewerWrapper, 2);
            }
            else if (galleryPos == LayoutPosition.Right)
            {
                PART_Layout.RowDefinitions = new("Auto, *");
                PART_Layout.ColumnDefinitions = new("*, Auto, Auto");

                PART_GalleryResizer.IsVisible = true;
                PART_Gallery.ViewMode = PhVirtualizingUniformPanelViewMode.Gallery;
                PART_Gallery.ItemTooltipPlacement = PlacementMode.Pointer;


                Grid.SetRow(PART_Gallery, 1);
                Grid.SetRow(PART_GalleryResizer, 1);
                Grid.SetRow(PART_ViewerWrapper, 1);

                Grid.SetColumn(PART_Gallery, 2);
                Grid.SetColumn(PART_GalleryResizer, 1);
                Grid.SetColumn(PART_ViewerWrapper, 0);
            }
        }
        else if (toolbarPos == LayoutPosition.Bottom)
        {
            PART_Toolbar.ItemTooltipPlacement = PlacementMode.Top;

            if (galleryPos == LayoutPosition.Top)
            {
                PART_Layout.RowDefinitions = new("Auto, *, Auto");
                PART_Layout.ColumnDefinitions = new("*");

                PART_GalleryResizer.IsVisible = false;
                PART_Gallery.MaxWidth = double.PositiveInfinity;
                PART_Gallery.ViewMode = PhVirtualizingUniformPanelViewMode.FilmStrip;
                PART_Gallery.ItemTooltipPlacement = PlacementMode.Bottom;


                Grid.SetRow(PART_Gallery, 0);
                Grid.SetRow(PART_GalleryResizer, 0);
                Grid.SetRow(PART_ViewerWrapper, 1);

                Grid.SetColumn(PART_Gallery, 0);
                Grid.SetColumn(PART_GalleryResizer, 0);
                Grid.SetColumn(PART_ViewerWrapper, 0);
            }
            else if (galleryPos == LayoutPosition.Bottom)
            {
                PART_Layout.RowDefinitions = new("*, Auto, Auto");
                PART_Layout.ColumnDefinitions = new("*");

                PART_GalleryResizer.IsVisible = false;
                PART_Gallery.MaxWidth = double.PositiveInfinity;
                PART_Gallery.ViewMode = PhVirtualizingUniformPanelViewMode.FilmStrip;
                PART_Gallery.ItemTooltipPlacement = PlacementMode.Top;


                Grid.SetRow(PART_Gallery, 1);
                Grid.SetRow(PART_GalleryResizer, 0);
                Grid.SetRow(PART_ViewerWrapper, 0);

                Grid.SetColumn(PART_Gallery, 0);
                Grid.SetColumn(PART_GalleryResizer, 0);
                Grid.SetColumn(PART_ViewerWrapper, 0);
            }
            else if (galleryPos == LayoutPosition.Left)
            {
                PART_Layout.RowDefinitions = new("*, Auto");
                PART_Layout.ColumnDefinitions = new("Auto, Auto, *");

                PART_GalleryResizer.IsVisible = true;
                PART_Gallery.ViewMode = PhVirtualizingUniformPanelViewMode.Gallery;
                PART_Gallery.ItemTooltipPlacement = PlacementMode.Pointer;


                Grid.SetRow(PART_Gallery, 0);
                Grid.SetRow(PART_GalleryResizer, 0);
                Grid.SetRow(PART_ViewerWrapper, 0);

                Grid.SetColumn(PART_Gallery, 0);
                Grid.SetColumn(PART_GalleryResizer, 1);
                Grid.SetColumn(PART_ViewerWrapper, 2);
            }
            else if (galleryPos == LayoutPosition.Right)
            {
                PART_Layout.RowDefinitions = new("*, Auto");
                PART_Layout.ColumnDefinitions = new("*, Auto, Auto");

                PART_GalleryResizer.IsVisible = true;
                PART_Gallery.ViewMode = PhVirtualizingUniformPanelViewMode.Gallery;
                PART_Gallery.ItemTooltipPlacement = PlacementMode.Pointer;


                Grid.SetRow(PART_Gallery, 0);
                Grid.SetRow(PART_GalleryResizer, 0);
                Grid.SetRow(PART_ViewerWrapper, 0);

                Grid.SetColumn(PART_Gallery, 2);
                Grid.SetColumn(PART_GalleryResizer, 1);
                Grid.SetColumn(PART_ViewerWrapper, 0);
            }
        }


        // The bottom filmstrip floats over the viewer while side galleries retain their layout.
        var galleryOverlay = galleryPos == LayoutPosition.Bottom;
        PART_Gallery.HideItemFileNames = galleryOverlay;
        PART_Gallery.Classes.Set("overlay", galleryOverlay);
        PART_Gallery.VerticalAlignment = galleryOverlay ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
        if (galleryOverlay)
        {
            Grid.SetRow(PART_Gallery, Grid.GetRow(PART_ViewerWrapper));
            Grid.SetColumn(PART_Gallery, Grid.GetColumn(PART_ViewerWrapper));
        }
        UpdateGalleryOverlayMargin();
        UpdateGalleryOverlayWidth();

        // update overlay margin to avoid overlap with floating top toolbar
        var isToolbarTop = false;
        PART_ImageInfoOverlay.Margin = new Thickness(20, isToolbarTop ? 62 : 10, 20, 0);

        // 3. update gallery initial width
        UpdateGalleryWidth();
    }

    private void UpdateGalleryOverlayMargin()
    {
        var overlay = Config.GetControlLayout(LayoutControl.Gallery) == LayoutPosition.Bottom;
        PART_Gallery.Margin = overlay ? new Thickness(0, 0, 0, PART_ToolHost.Bounds.Height) : default;
    }

    private void GalleryItems_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => UpdateGalleryOverlayWidth();

    private void ViewerWrapper_SizeChanged(object? sender, SizeChangedEventArgs e)
        => UpdateGalleryOverlayWidth();

    private void UpdateGalleryOverlayWidth()
    {
        var overlay = Config.GetControlLayout(LayoutControl.Gallery) == LayoutPosition.Bottom;
        PART_Gallery.HorizontalAlignment = overlay ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        if (!overlay)
        {
            PART_Gallery.Width = double.NaN;
            return;
        }
        var count = Core.Photos.Items.Count;
        var cellWidth = Core.Config.ThumbnailSize + GalleryControl.GalleryItemMargin.Left + GalleryControl.GalleryItemMargin.Right;
        var desiredWidth = count == 0 ? 0 : count * cellWidth + GalleryControl.GalleryPadding.Left + GalleryControl.GalleryPadding.Right;
        PART_Gallery.Width = Math.Min(desiredWidth, Math.Max(0, PART_ViewerWrapper.Bounds.Width));
    }


    /// <summary>
    /// Updates the width of the gallery and its resizer columns based on the current layout.
    /// </summary>
    private void UpdateGalleryWidth()
    {
        if (PART_Layout.ColumnDefinitions.Count == 0) return;

        // 1. get gallery position
        var galleryPos = Core.Config.Layout.GetValueOrDefault(LayoutControl.Gallery, LayoutPosition.Bottom);
        if (galleryPos is LayoutPosition.Top or LayoutPosition.Bottom) return;

        // 2. get column indexes
        var galleryColIndex = Grid.GetColumn(PART_Gallery);
        var galleryResizerColIndex = Grid.GetColumn(PART_GalleryResizer);


        // 3. hide gallery space
        if (!Core.Config.ShowGallery)
        {
            PART_Layout.ColumnDefinitions[galleryColIndex].Width = new(0);
            PART_Layout.ColumnDefinitions[galleryResizerColIndex].Width = new(0);
            PART_GalleryResizer.IsVisible = false;
        }

        // 4. update gallery size
        else
        {
            var galleryViewMinWidth = GalleryControl.CalculateWidthForGalleryView(1);
            var galleryViewWidth = GalleryControl.CalculateWidthForGalleryView(Core.Config.GalleryColumns);
            galleryViewWidth = Math.Min(galleryViewWidth, Bounds.Width * 0.8); // max width = 80% window width

            PART_Layout.ColumnDefinitions[galleryColIndex].Width = new(galleryViewWidth);
            PART_Layout.ColumnDefinitions[galleryColIndex].MinWidth = galleryViewMinWidth;
            PART_Layout.ColumnDefinitions[galleryResizerColIndex].Width = new(5);
            PART_GalleryResizer.IsVisible = true;
        }
    }


    /// <summary>
    /// Shows the motion-video button for a live photo while the Frame nav tool is closed.
    /// </summary>
    private void UpdateMotionButtonState()
    {
        var photo = PART_Viewer.Photo;
        var isLivePhoto = photo?.Error is null && (photo?.Metadata?.IsLivePhoto ?? false);
        var isFrameNavOpen = PART_ToolHost.Tool?.ToolId == FrameNavToolControl.TOOL_ID;

        PART_MotionOverlay.IsShown = isLivePhoto && !isFrameNavOpen;
    }


    /// <summary>
    /// Insets the message from the edge the image info bar sits on by the bar's reach, so they never overlap.
    /// </summary>
    private void UpdateMessageMargin()
    {
        var inset = PART_ImageInfoOverlay.CoveredHeight;
        var isBarAtBottom = PART_ImageInfoOverlay.VerticalAlignment == VerticalAlignment.Bottom;
        var topInset = isBarAtBottom ? 0 : inset;
        var bottomInset = isBarAtBottom ? inset : 0;

        PART_Message.Margin = new Thickness(
            _messageMargin.Left,
            _messageMargin.Top + topInset,
            _messageMargin.Right,
            _messageMargin.Bottom + bottomInset);
    }


    #endregion // Control Methods


}
