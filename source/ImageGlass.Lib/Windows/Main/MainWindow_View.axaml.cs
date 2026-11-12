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
    private bool _fullscreenHidesToolbar;
    private bool _fullscreenHidesGallery;
    private MainWindow? _ownerWindow;
    private MainWindow OwnerWindow => _ownerWindow ?? (MainWindow)TopLevel.GetTopLevel(this)!;
    private bool _updatingRotation;
    private void RotationSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingRotation || !IsLoaded) return;
        PART_Viewer.SetDisplayRotation(e.NewValue);
        PART_RotationValue.Text = $"{e.NewValue:0}°";
    }
    private void RotationReset_Click(object? sender, RoutedEventArgs e) => PART_Viewer.SetDisplayRotation(0);
    private void Viewer_DisplayRotationChanged(object? sender, EventArgs e)
    {
        _updatingRotation = true;
        PART_RotationSlider.Value = PART_Viewer.DisplayRotation;
        PART_RotationValue.Text = $"{PART_Viewer.DisplayRotation:0}°";
        _updatingRotation = false;
    }
    private TaskCompletionSource<bool>? _deleteConfirmation;
    public bool IsDeleteConfirmationOpen => _deleteConfirmation != null;
    public Task<bool> ConfirmDeleteAsync(string title, string heading, string path, string? size)
    {
        if (_deleteConfirmation != null) return Task.FromResult(false);
        _deleteConfirmation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        PART_DeleteTitle.Text = title;
        PART_DeleteHeading.Text = heading;

        var fileName = Path.GetFileName(path);
        var folder = Path.GetDirectoryName(path);
        PART_DeleteFileName.Text = string.IsNullOrWhiteSpace(fileName) ? path : fileName;

        var metaParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(folder)) metaParts.Add(folder);
        if (!string.IsNullOrWhiteSpace(size)) metaParts.Add(size);
        PART_DeletePath.Text = string.Join("  ·  ", metaParts);

        PART_DeleteRemember.IsChecked = false;
        PART_DeleteRemember.Content = Core.Lang[LangId._DoNotShowThisMessageAgain];
        PART_DeleteCancel.Content = Core.Lang[LangId._Cancel];
        PART_DeleteConfirm.Content = Core.Lang[LangId._Yes];
        PART_DeleteModal.IsVisible = true;
        PART_DeleteCancel.Focus(NavigationMethod.Pointer);
        return _deleteConfirmation.Task;
    }

    public void UpdateBottomStatusBar()
    {
        var count = Photos.Count;
        var index = Photos.CurrentIndex;
        if (count == 0)
        {
            PART_StatusGroup.IsVisible = false;
            return;
        }

        PART_StatusGroup.IsVisible = true;
        PART_StatusIndex.Text = $"{index + 1} / {count}";

        var meta = Photos.Current?.Metadata;
        if (OwnerWindow.Session.ClipboardImage is { } clip)
        {
            PART_StatusSpecs.Text = $"{clip.Width} × {clip.Height}";
        }
        else if (meta is not null)
        {
            var w = meta.Width > 0 ? meta.Width : meta.OriginalWidth;
            var h = meta.Height > 0 ? meta.Height : meta.OriginalHeight;
            var parts = new List<string>();
            if (w > 0 && h > 0) parts.Add($"{w} × {h}");
            if (!string.IsNullOrWhiteSpace(meta.FileSizeFormatted)) parts.Add(meta.FileSizeFormatted);
            PART_StatusSpecs.Text = string.Join("  ·  ", parts);
        }
        else
        {
            PART_StatusSpecs.Text = string.Empty;
        }
    }
    public void CloseDeleteConfirmation(bool confirmed = false)
    {
        var pending = _deleteConfirmation;
        if (pending == null) return;
        if (confirmed && PART_DeleteRemember.IsChecked == true) Core.Config.EnableDeleteConfirmation = false;
        _deleteConfirmation = null;
        PART_DeleteModal.IsVisible = false;
        pending.TrySetResult(confirmed);
    }
    private void DeleteCancel_Click(object? sender, RoutedEventArgs e) => CloseDeleteConfirmation();
    private void DeleteConfirm_Click(object? sender, RoutedEventArgs e) => CloseDeleteConfirmation(true);
    private void DeleteBackdrop_PointerPressed(object? sender, PointerPressedEventArgs e) => CloseDeleteConfirmation();

    private TaskCompletionSource<string?>? _renameTcs;
    public bool IsRenameModalOpen => _renameTcs != null;

    public Task<string?> PromptRenameAsync(string title, string heading, string filePath, Avalonia.Media.IImage? thumbnail)
    {
        if (_renameTcs != null) return Task.FromResult<string?>(null);
        _renameTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        PART_RenameTitle.Text = title.TrimEnd('…', '.');
        PART_RenameHeading.Text = heading;

        var fileName = Path.GetFileName(filePath);
        var folder = Path.GetDirectoryName(filePath);
        var stem = Path.GetFileNameWithoutExtension(filePath);
        var ext = Path.GetExtension(filePath);

        PART_RenameOldName.Text = fileName;
        PART_RenamePath.Text = folder ?? string.Empty;

        PART_RenameThumbnail.Source = thumbnail;
        PART_RenameThumbnail.IsVisible = thumbnail is not null;
        PART_RenamePlaceholderIcon.IsVisible = thumbnail is null;

        PART_RenameExtBadge.IsVisible = !string.IsNullOrWhiteSpace(ext);
        PART_RenameExtText.Text = ext;

        PART_RenameCancel.Content = Core.Lang[LangId._Cancel];
        PART_RenameConfirm.Content = Core.Lang[LangId._OK];

        PART_RenameInput.Text = stem;
        ValidateRenameInput();

        PART_RenameModal.IsVisible = true;

        Dispatcher.UIThread.Post(() =>
        {
            PART_RenameInput.Focus();
            PART_RenameInput.SelectAll();
        }, DispatcherPriority.Input);

        return _renameTcs.Task;
    }

    private bool ValidateRenameInput()
    {
        var text = PART_RenameInput.Text?.Trim() ?? string.Empty;
        string? errorMsg = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            errorMsg = Core.Lang[LangId._Validation_Required];
        }
        else
        {
            var badChars = Path.GetInvalidFileNameChars();
            foreach (var c in badChars.AsSpan())
            {
                if (text.Contains(c))
                {
                    errorMsg = Core.Lang[LangId._Validation_FileNameValueOnly];
                    break;
                }
            }
        }

        var isValid = errorMsg is null;
        PART_RenameError.IsVisible = !isValid;
        PART_RenameError.Text = errorMsg ?? string.Empty;
        PART_RenameInputBox.Classes.Set("invalid", !isValid);
        PART_RenameConfirm.IsEnabled = isValid;
        return isValid;
    }

    public void CloseRenameModal(string? result = null)
    {
        var pending = _renameTcs;
        if (pending == null) return;
        _renameTcs = null;
        PART_RenameModal.IsVisible = false;
        pending.TrySetResult(result);
    }

    private void RenameCancel_Click(object? sender, RoutedEventArgs e) => CloseRenameModal();

    private void RenameConfirm_Click(object? sender, RoutedEventArgs e)
    {
        if (!ValidateRenameInput()) return;
        CloseRenameModal(PART_RenameInput.Text?.Trim());
    }

    private void RenameBackdrop_PointerPressed(object? sender, PointerPressedEventArgs e) => CloseRenameModal();

    private void RenameInput_TextChanged(object? sender, TextChangedEventArgs e) => ValidateRenameInput();

    private void RenameInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (ValidateRenameInput())
            {
                CloseRenameModal(PART_RenameInput.Text?.Trim());
            }
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseRenameModal();
        }
    }
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

    private void ZoomOutButton_Click(object? sender, RoutedEventArgs e)
    {
        if (FeatureManager.IsLocked(API.IG_ZoomOut)) return;
        if (PART_Viewer.ZoomLevels.Length > 0) PART_Viewer.ZoomOut();
        else PART_Viewer.StartDrawingAnimation(AnimationSources.ZoomOut, 100);
    }

    private void ZoomInButton_Click(object? sender, RoutedEventArgs e)
    {
        if (FeatureManager.IsLocked(API.IG_ZoomIn)) return;
        if (PART_Viewer.ZoomLevels.Length > 0) PART_Viewer.ZoomIn();
        else PART_Viewer.StartDrawingAnimation(AnimationSources.ZoomIn, 100);
    }

    private void ZoomActualSizeButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!FeatureManager.IsLocked(API.IG_SetZoom)) _ = PART_Viewer.ZoomToPoint(1);
    }

    private void CloseWindow_Click(object? sender, RoutedEventArgs e) => OwnerWindow.Close();

    private void MinimizeWindow_Click(object? sender, RoutedEventArgs e) =>
        OwnerWindow.WindowState = WindowState.Minimized;

    private void MaximizeWindow_Click(object? sender, RoutedEventArgs e) =>
        OwnerWindow.WindowState = OwnerWindow.WindowState == WindowState.Maximized
            ? WindowState.Normal : WindowState.Maximized;

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source) return;
        if (source.GetSelfAndVisualAncestors().Any(v => v is Button or ToggleButton or Slider or Thumb or ScrollBar or TextBox)) return;
        if (!e.GetCurrentPoint(PART_CustomTitleBar).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2)
            MaximizeWindow_Click(sender, e);
        else
            OwnerWindow.BeginMoveDrag(e);
        e.Handled = true;
    }

    private void BottomBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (source.GetSelfAndVisualAncestors().Any(v => v is Button or ToggleButton or Slider or Thumb)) return;
        OwnerWindow.BeginMoveDrag(e);
        e.Handled = true;
    }

    private void InitTitleBarPlatform()
    {
        var isMac = BHelper.OS == OSType.Mac;
        PART_MacButtons.IsVisible = isMac;
        PART_WinButtons.IsVisible = !isMac;
        PART_WinAppIcon.IsVisible = BHelper.OS == OSType.Windows;

        UpdateWindowStateButtons();

        if (OwnerWindow is not null)
        {
            OwnerWindow.PropertyChanged += MainWindow_PropertyChanged;
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
        if (OwnerWindow is null) return;
        var isMaximized = OwnerWindow.WindowState == WindowState.Maximized;
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


    #region Settings Overlay

    public bool IsSettingsOverlayOpen => PART_SettingsModal.IsVisible;

    public void OpenSettingsOverlay(string? section = null)
    {
        CloseAboutOverlay();
        PART_SettingsModal.Open(section, hex => UpdateViewerCustomBackground(hex));
    }

    public void CloseSettingsOverlay()
    {
        PART_SettingsModal.Close();
    }

    public void HandleSettingsEscapeKey()
    {
        PART_SettingsModal.HandleEscapeKey();
    }

    #endregion // Settings Overlay


    #region About Overlay

    public bool IsAboutOverlayOpen => PART_AboutModal.IsVisible;

    public void OpenAboutOverlay()
    {
        CloseImageInfoOverlay();
        OpenSettingsOverlay("about");
    }

    public void CloseAboutOverlay()
    {
        PART_AboutModal.Close();
    }

    #endregion // About Overlay

    protected override void OnIgThemeChanged(ThemePackChangedEventArgs e)
    {
        base.OnIgThemeChanged(e);
        UpdateViewerCustomBackground();
    }

    public void UpdateViewerCustomBackground(string? overrideHex = null)
    {
        var bgText = overrideHex ?? (Core.Config.EnableSlideshow
            ? Core.Config.SlideshowBackgroundColor
            : Core.Config.BackgroundColor);

        if (string.IsNullOrWhiteSpace(bgText))
        {
            PART_ViewerBgLayer.Background = Avalonia.Media.Brushes.Transparent;
            return;
        }

        var trimmed = bgText.Trim();
        var color = BHelper.ColorFromHex(trimmed);
        // 透明占位或 Kobe-Light 默认半透明白底 (#fffa) 时保留精致工作台渐变；
        // 仅当用户选择有效自定义底色时实时覆写画布背景
        if (color.A == 0 || string.Equals(trimmed, "#fffa", StringComparison.OrdinalIgnoreCase))
        {
            PART_ViewerBgLayer.Background = Avalonia.Media.Brushes.Transparent;
        }
        else
        {
            PART_ViewerBgLayer.Background = new Avalonia.Media.SolidColorBrush(color);
        }
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
        var folder = Photos.Current?.Metadata?.FolderPath;
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            BHelper.OpenFolderPath(folder);
        }
    }

    private void RefreshImageInfoContent()
    {
        var photo = Photos.Current;
        var meta = photo?.Metadata;
        var summary = new StringBuilder();

        if (meta is null || OwnerWindow.Session.ClipboardImage is not null)
        {
            var isClip = OwnerWindow.Session.ClipboardImage is not null;
            var name = isClip ? Core.Lang[LangId._ClipboardImage] : "未打开图片";
            PART_InfoFileName.Text = name;
            PART_ChipFormat.IsVisible = isClip;
            PART_InfoFormatText.Text = "剪贴板";
            var sizeStr = isClip && OwnerWindow.Session.ClipboardImage is { } clipboardImage
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


    /// <summary>Photo collection owned by this view's window.</summary>
    public PhotoManager Photos { get; private set; } = Core.Photos;

    public MainWindowView()
    {
        InitializeComponent();
        PART_Gallery.Photos = Photos;
        PART_Gallery.ItemsSource = Photos.Items;
    }

    /// <summary>Assigns the window's photo collection before the view is loaded.</summary>
    internal void SetPhotoManager(PhotoManager photos)
    {
        ArgumentNullException.ThrowIfNull(photos);
        if (IsLoaded) throw new InvalidOperationException("The photo manager must be assigned before the view is loaded.");
        Photos = photos;
        PART_Gallery.Photos = photos;
        PART_Gallery.ItemsSource = photos.Items;
    }



    #region Control Events

    protected override void OnLoaded(RoutedEventArgs e)
    {
        _ownerWindow = TopLevel.GetTopLevel(this) as MainWindow;
        using var windowScope = App.UseWindow(OwnerWindow);
        // apply app layout from settings
        ApplyAppLayout();

        base.OnLoaded(e);

        InitTitleBarPlatform();
        InitViewerContextMenu();
        UpdateViewerCustomBackground();

        // drag-drop events
        DragDrop.SetAllowDrop(PART_Viewer, true);
        DragDrop.AddDragOverHandler(PART_Viewer, PART_Viewer_DragOver);
        DragDrop.AddDropHandler(PART_Viewer, PART_Viewer_Drop);

        Core.Config.PropertyChanged += Config_PropertyChanged;
        Photos.Items.CollectionChanged += GalleryItems_CollectionChanged;
        PART_ViewerWrapper.SizeChanged += ViewerWrapper_SizeChanged;
        UpdateGalleryOverlayWidth();
        Photos.FileWatcherChanged += Photos_FileWatcherChanged;
        PART_Viewer.PhotoLoading += PART_Viewer_PhotoLoading;
        PART_Viewer.ZoomChanged += PART_Viewer_ZoomChanged;
        PART_Viewer.DisplayRotationChanged += Viewer_DisplayRotationChanged;
        UpdateZoomSlider();
        PART_Viewer.NavButtonClicked += PART_Viewer_NavButtonClicked;
        PART_Viewer.ViewerPointerClicked += PART_Viewer_ViewerPointerClicked;
        PART_Viewer.ViewerMouseWheel += PART_Viewer_ViewerMouseWheel;
        if (PART_Viewer.ContextMenu is not null)
        {
            PART_Viewer.ContextMenu.Opening += PART_Viewer_ContextMenu_Opening;
        }

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

        if (OwnerWindow is not null)
        {
            OwnerWindow.PropertyChanged -= MainWindow_PropertyChanged;
        }
        _ownerWindow = null;

        // drag-drop events
        DragDrop.RemoveDragOverHandler(PART_Viewer, PART_Viewer_DragOver);
        DragDrop.RemoveDropHandler(PART_Viewer, PART_Viewer_Drop);

        Core.Config.PropertyChanged -= Config_PropertyChanged;
        Photos.Items.CollectionChanged -= GalleryItems_CollectionChanged;
        PART_ViewerWrapper.SizeChanged -= ViewerWrapper_SizeChanged;
        Photos.FileWatcherChanged -= Photos_FileWatcherChanged;
        PART_Viewer.PhotoLoading -= PART_Viewer_PhotoLoading;
        PART_Viewer.ZoomChanged -= PART_Viewer_ZoomChanged;
        PART_Viewer.DisplayRotationChanged -= Viewer_DisplayRotationChanged;
        PART_Viewer.NavButtonClicked -= PART_Viewer_NavButtonClicked;
        PART_Viewer.ViewerPointerClicked -= PART_Viewer_ViewerPointerClicked;
        PART_Viewer.ViewerMouseWheel -= PART_Viewer_ViewerMouseWheel;
        if (PART_Viewer.ContextMenu is not null)
        {
            PART_Viewer.ContextMenu.Opening -= PART_Viewer_ContextMenu_Opening;
        }

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
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => { if (IsLoaded) Config_PropertyChanged(sender, e); });
            return;
        }
        if (e.PropertyName == nameof(Config.ThumbnailSize)) UpdateGalleryOverlayWidth();
        if (e.PropertyName == nameof(Config.Layout) || e.PropertyName == nameof(Config.ShowToolbar))
        {
            ApplyAppLayout();
        }
        else if (e.PropertyName == nameof(Config.EnableFileWatcher))
        {
            // set file watcher
            AppAPIProvider.SetFileWatcher(Photos, Core.Config.EnableFileWatcher);
        }
        else if (e.PropertyName is nameof(Config.BackgroundColor)
            or nameof(Config.SlideshowBackgroundColor)
            or nameof(Config.EnableSlideshow))
        {
            Core.UpdateViewerBackgroundBrushResource();
            UpdateViewerCustomBackground();
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
        using var windowScope = App.UseWindow(OwnerWindow);
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
        if (ReferenceEquals(Photos, Core.DefaultPhotos))
            Core.ShellProvider?.ForegroundShell = Core.ShellProvider?.GetForegroundWindowView();
        OwnerWindow.Session.InitialImagePath = BHelper.ResolvePath(paths[0]);

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
        // gallery is data-bound to Photos.Items, so items are already added;
        // we only need to scroll/display the last added file if the user opted in.
        if (e.FilePaths.Count == 0) return;

        // scroll gallery to the newly added file if configured
        if (Core.Config.EnableAutoOpenNewAddedImage)
        {
            var lastAdded = e.FilePaths[^1];
            var newIndex = Photos.IndexOf(lastAdded);

            if (newIndex >= 0)
            {
                var photo = Photos.Select(newIndex);
                _ = ViewPhotoAsync(photo);
            }
        }
        else
        {
            // re-select current photo to update its index (list shifted by inserts)
            _ = Photos.Select(Photos.CurrentFilePath);
            PART_Gallery.ScrollToItem(Photos.CurrentIndex);
        }
    }


    private void HandleFileWatcher_FilesDeleted(FileWatcherChangedEventArgs e)
    {
        using var windowScope = App.UseWindow(OwnerWindow);
        if (e.FilePaths.Count == 0) return;

        // if the currently viewed photo was deleted
        if (!string.IsNullOrEmpty(e.AffectedCurrentFilePath))
        {
            AppAPIProvider.ViewPhotoAfterCurrentRemoved();
        }
        else
        {
            // re-select current photo to fix its index after list shifted
            _ = Photos.Select(Photos.CurrentFilePath);
            PART_Gallery.ScrollToItem(Photos.CurrentIndex);
        }

        // update init photo path if it was deleted
        if (Photos.InitPhoto is not null)
        {
            for (var i = 0; i < e.FilePaths.Count; i++)
            {
                if (string.Equals(e.FilePaths[i], Photos.InitPhoto.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    var dirPath = Path.GetDirectoryName(e.FilePaths[i]) ?? string.Empty;
                    Photos.InitPhoto = !string.IsNullOrEmpty(dirPath)
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
            var photoIndex = Photos.IndexOf(filePath);
            var photo = Photos.Get(photoIndex);
            if (photo is null) continue;

            // force thumbnail reload
            PART_Gallery.LoadThumbnail(photoIndex, false);

            // if it's the currently viewed photo, reload it
            if (Photos.IsSelected(filePath))
            {
                _ = PART_Viewer.SetPhotoAsync(photo, new PhotoLoadingOptions
                {
                    ResetZoom = false,
                    UseCache = false,
                    Channels = OwnerWindow.Session.ColorChannels,
                });
            }
        }
    }


    private void HandleFileWatcher_FilesRenamed(FileWatcherChangedEventArgs e)
    {
        if (e.FilePaths.Count == 0 || e.OldFilePaths is null) return;

        var currentNeedsRefresh = false;
        for (var i = 0; i < e.FilePaths.Count; i++)
        {
            var newPath = e.FilePaths[i];
            var oldPath = i < e.OldFilePaths.Count ? e.OldFilePaths[i] : null;

            // update init photo if it was renamed
            if (Photos.InitPhoto is not null
                && oldPath is not null
                && string.Equals(oldPath, Photos.InitPhoto.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                Photos.InitPhoto = new Photo(newPath);
            }

            // check if the currently viewed photo was renamed
            if (Photos.IsSelected(newPath))
            {
                currentNeedsRefresh = true;
            }
        }

        // refresh the viewer title/info if the current photo was renamed
        if (currentNeedsRefresh)
        {
            // re-select to refresh bindings for current photo name/path
            _ = Photos.Select(Photos.CurrentIndex);
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
        using var windowScope = App.UseWindow(OwnerWindow);
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
        using var windowScope = App.UseWindow(OwnerWindow);
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
        using var windowScope = App.UseWindow(OwnerWindow);
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
        using var windowScope = App.UseWindow(OwnerWindow);
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


    private static Avalonia.Controls.Shapes.Path CreateContextMenuIcon(string pathData, string colorHex) => new()
    {
        Width = 14,
        Height = 14,
        Stretch = Avalonia.Media.Stretch.Uniform,
        Data = Avalonia.Media.Geometry.Parse(pathData),
        Fill = Avalonia.Media.Brush.Parse(colorHex),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private void InitViewerContextMenu()
    {
        if (PART_Viewer.ContextMenu is not ContextMenu mnuContext) return;
        if (mnuContext.Items.Count > 0) return;

        const string iconCopyPath = "M9.5 17H7A5 5 0 0 1 7 7H9.5A.75.75 0 0 1 9.5 8.5H7A3.5 3.5 0 0 0 7 15.5H9.5A.75.75 0 0 1 9.5 17ZM14.5 7H17A5 5 0 0 1 17 17H14.5A.75.75 0 0 1 14.5 15.5H17A3.5 3.5 0 0 0 17 8.5H14.5A.75.75 0 0 1 14.5 7ZM8.25 11.25H15.75A.75.75 0 0 1 15.75 12.75H8.25A.75.75 0 0 1 8.25 11.25Z";
        const string iconCopyFile = "M11.75 2C9.95507 2 8.5 3.45508 8.5 5.25V23.25C8.5 25.0449 9.95507 26.5 11.75 26.5H23.75C25.5449 26.5 27 25.0449 27 23.25V5.25C27 3.45507 25.5449 2 23.75 2H11.75ZM10.5 5.25C10.5 4.55964 11.0596 4 11.75 4H23.75C24.4404 4 25 4.55964 25 5.25V23.25C25 23.9404 24.4404 24.5 23.75 24.5H11.75C11.0596 24.5 10.5 23.9404 10.5 23.25V5.25ZM7 5.74902C5.82552 6.2388 5 7.39797 5 8.74994V23.4999C5 27.0898 7.91015 29.9999 11.5 29.9999H20.25C21.6021 29.9999 22.7613 29.1743 23.2511 27.9996H20.2786C20.2691 27.9998 20.2596 27.9999 20.25 27.9999H11.5C9.01472 27.9999 7 25.9852 7 23.4999V5.74902Z";
        const string iconRename = "M20.9519 3.0481C19.5543 1.65058 17.2885 1.65064 15.8911 3.04825L3.94103 14.9997C3.5347 15.4061 3.2491 15.9172 3.116 16.4762L2.02041 21.0777C1.96009 21.3311 2.03552 21.5976 2.21968 21.7817C2.40385 21.9659 2.67037 22.0413 2.92373 21.981L7.52498 20.8855C8.08418 20.7523 8.59546 20.4666 9.00191 20.0601L20.952 8.10861C22.3493 6.71112 22.3493 4.4455 20.9519 3.0481ZM16.9518 4.10884C17.7634 3.29709 19.0795 3.29705 19.8912 4.10876C20.7028 4.9204 20.7029 6.23632 19.8913 7.04801L19 7.93946L16.0606 5.00012L16.9518 4.10884ZM15 6.06084L17.9394 9.00018L7.94119 18.9995C7.73104 19.2097 7.46668 19.3574 7.17755 19.4263L3.76191 20.2395L4.57521 16.8237C4.64402 16.5346 4.79168 16.2704 5.00175 16.0603L15 6.06084Z";
        const string iconTrash = "M6.442 2.5a2 2 0 0 1 2-2h3.046a2 2 0 0 1 2 2v.314c0 .036-.003.07-.01.105h4.952a.5.5 0 1 1 0 1h-.764l-1.558 13.507a2.5 2.5 0 0 1-2.484 2.214H6.306a2.5 2.5 0 0 1-2.484-2.214L2.264 3.92H1.5a.5.5 0 0 1 0-1h4.953a.502.502 0 0 1-.011-.105V2.5Zm.989.419h5.068a.501.501 0 0 1-.01-.105V2.5a1 1 0 0 0-1-1H8.441a1 1 0 0 0-1 1v.314c0 .036-.004.07-.011.105Zm-4.16 1L4.815 17.31a1.5 1.5 0 0 0 1.49 1.329h7.318a1.5 1.5 0 0 0 1.49-1.329L16.66 3.92H3.27Zm5.38 3.337a.5.5 0 1 0-1 0v7.256a.5.5 0 0 0 1 0V7.256Zm3.128-.5a.5.5 0 0 1 .5.5v7.256a.5.5 0 0 1-1 0V7.256a.5.5 0 0 1 .5-.5Z";
        const string iconFolder = "M3.50014 6.25C3.50014 5.2835 4.28364 4.5 5.25014 4.5H8.12882C8.32773 4.5 8.5185 4.57902 8.65915 4.71967L10.7198 6.78033C10.8605 6.92098 11.0512 7 11.2501 7H16.7501C17.7166 7 18.5001 7.7835 18.5001 8.75C18.5001 8.83602 18.5146 8.91866 18.5413 8.99561L8.71867 8.99561C7.37893 8.99561 6.14095 9.71035 5.47108 10.8706L3.50014 14.2844V6.25ZM2.00036 17.7883C2.02089 19.5656 3.468 21 5.25014 21H11.0001C11.0292 21 11.058 20.9983 11.0862 20.9951H16.2812C17.6209 20.9951 18.8589 20.2804 19.5288 19.1201L22.5596 13.8706C23.7748 11.7657 22.3337 9.14964 19.9567 9.00214C19.9848 8.92334 20.0001 8.83846 20.0001 8.75C20.0001 6.95507 18.5451 5.5 16.7501 5.5H11.5608L9.71981 3.65901C9.29786 3.23705 8.72556 3 8.12882 3H5.25014C3.45522 3 2.00014 4.45507 2.00014 6.25V17.71C1.9999 17.7362 1.99997 17.7623 2.00036 17.7883ZM8.71867 10.4956L19.745 10.4956C21.0921 10.4956 21.9341 11.9539 21.2605 13.1206L18.2297 18.3701C17.8278 19.0663 17.085 19.4951 16.2812 19.4951H5.25485C3.9077 19.4951 3.06573 18.0368 3.7393 16.8701L6.77011 11.6206C7.17204 10.9245 7.91482 10.4956 8.71867 10.4956Z";

        var items = new object[]
        {
            new PhMenuItem
            {
                LangKey = LangId.Menu_MnuCopyPath,
                Tag = API.IG_CopyImagePath,
                Icon = CreateContextMenuIcon(iconCopyPath, "#CBD5E1"),
                Command = Core.API?.GetApiCommand(API.IG_CopyImagePath, OwnerWindow),
                HotkeyText = AppAPIProvider.GetMenuHotkeyText(LangId.Menu_MnuCopyPath),
            },
            new PhMenuItem
            {
                LangKey = LangId.Menu_MnuCopyFile,
                Tag = API.IG_CopyFiles,
                Icon = CreateContextMenuIcon(iconCopyFile, "#CBD5E1"),
                Command = Core.API?.GetApiCommand(API.IG_CopyFiles, OwnerWindow),
                HotkeyText = AppAPIProvider.GetMenuHotkeyText(LangId.Menu_MnuCopyFile),
            },
            new Separator(),
            new PhMenuItem
            {
                LangKey = LangId.Menu_MnuRename,
                Tag = API.IG_Rename,
                Icon = CreateContextMenuIcon(iconRename, "#CBD5E1"),
                Command = Core.API?.GetApiCommand(API.IG_Rename, OwnerWindow),
                HotkeyText = AppAPIProvider.GetMenuHotkeyText(LangId.Menu_MnuRename),
            },
            CreateDangerMenuItem(iconTrash),
            new PhMenuItem
            {
                LangKey = LangId.Menu_MnuOpenLocation,
                Tag = API.IG_OpenLocation,
                Icon = CreateContextMenuIcon(iconFolder, "#CBD5E1"),
                Command = Core.API?.GetApiCommand(API.IG_OpenLocation, OwnerWindow),
                HotkeyText = AppAPIProvider.GetMenuHotkeyText(LangId.Menu_MnuOpenLocation),
            },
        };

        foreach (var item in items)
        {
            if (item is PhMenuItem phItem)
            {
                phItem.LocalizeText();
            }
            mnuContext.Items.Add(item);
        }

        if (BHelper.OS == OSType.Windows)
        {
            var photosItem = new PhMenuItem
            {
                Header = "使用 Windows 照片打开",
                Icon = CreateContextMenuIcon(iconFolder, "#CBD5E1"),
            };
            photosItem.Click += OpenInWindowsPhotos_Click;
            mnuContext.Items.Add(photosItem);
        }
        else if (BHelper.OS == OSType.Mac)
        {
            var systemViewerItem = new PhMenuItem
            {
                Header = "使用系统查看器查看",
                Icon = CreateContextMenuIcon(iconFolder, "#CBD5E1"),
            };
            systemViewerItem.Click += OpenInMacSystemViewer_Click;
            mnuContext.Items.Add(systemViewerItem);
        }

        mnuContext.Opened += (_, _) => UpdateContextMenuIconColors(mnuContext);
        UpdateContextMenuIconColors(mnuContext);

        FeatureManager.HideLockedMenuItems(mnuContext.Items);
    }

    private async void OpenInWindowsPhotos_Click(object? sender, RoutedEventArgs e)
    {
        var path = Photos.CurrentFilePath;
        if (OwnerWindow.Session.ClipboardImage is not null || !File.Exists(path)) return;

        try
        {
            var uri = "ms-photos:viewer?fileName=" + Uri.EscapeDataString(Path.GetFullPath(path));
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            await ImageGlass.UI.Windowing.ModalWindow.ShowErrorAsync(OwnerWindow,
                new ImageGlass.UI.Windowing.ModalWindowOptions
                {
                    Title = "无法打开 Windows 照片",
                    Description = ex.Message,
                });
        }
    }

    private async void OpenInMacSystemViewer_Click(object? sender, RoutedEventArgs e)
    {
        var path = Photos.CurrentFilePath;
        if (OwnerWindow.Session.ClipboardImage is not null || !File.Exists(path)) return;

        try
        {
            var startInfo = new ProcessStartInfo("/usr/bin/open")
            {
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add(Path.GetFullPath(path));
            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            await ImageGlass.UI.Windowing.ModalWindow.ShowErrorAsync(OwnerWindow,
                new ImageGlass.UI.Windowing.ModalWindowOptions
                {
                    Title = "无法使用系统查看器打开图片",
                    Description = ex.Message,
                });
        }
    }

    private PhMenuItem CreateDangerMenuItem(string iconTrash)
    {
        var item = new PhMenuItem
        {
            LangKey = LangId.Menu_MnuMoveToRecycleBin,
            Tag = API.IG_Delete,
            Icon = CreateContextMenuIcon(iconTrash, "#F87171"),
            Command = Core.API?.GetApiCommand(API.IG_Delete, OwnerWindow),
            CommandParameter = "true",
            HotkeyText = AppAPIProvider.GetMenuHotkeyText(LangId.Menu_MnuMoveToRecycleBin),
        };
        item.Classes.Add("danger");
        return item;
    }

    private void PART_Viewer_ContextMenu_Opening(object? sender, CancelEventArgs e)
    {
        using var windowScope = App.UseWindow(OwnerWindow);
        if (sender is not ContextMenu mnuContext) return;

        var hasFile = OwnerWindow.Session.ClipboardImage is null && File.Exists(Photos.CurrentFilePath);
        UpdateContextMenuIconColors(mnuContext);

        foreach (var obj in mnuContext.Items)
        {
            if (obj is PhMenuItem item)
            {
                if (item.Command is null && item.Tag is API api)
                {
                    item.Command = Core.API?.GetApiCommand(api, OwnerWindow);
                }
                item.LocalizeText();
                if (item.LangKey is { } key)
                {
                    item.HotkeyText = AppAPIProvider.GetMenuHotkeyText(key);
                }
                item.IsEnabled = hasFile;
            }
        }
    }

    private void UpdateContextMenuIconColors(ContextMenu menu)
    {
        var dark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        foreach (var item in menu.Items.OfType<PhMenuItem>())
        {
            if (item.Icon is not Avalonia.Controls.Shapes.Path icon) continue;
            var color = GetContextMenuSemanticColor(item, dark);
            icon.Fill = Avalonia.Media.Brush.Parse(color);
        }
    }


    private static string GetContextMenuSemanticColor(PhMenuItem item, bool isDark)
    {
        if (item.Classes.Contains("danger") || item.LangKey == LangId.Menu_MnuMoveToRecycleBin)
        {
            return isDark ? "#F87171" : "#DC2626";
        }

        return item.LangKey switch
        {
            LangId.Menu_MnuCopyPath => isDark ? "#818CF8" : "#4F46E5",
            LangId.Menu_MnuCopyFile => isDark ? "#38BDF8" : "#0284C7",
            LangId.Menu_MnuRename => isDark ? "#FBBF24" : "#D97706",
            LangId.Menu_MnuOpenLocation => isDark ? "#34D399" : "#059669",
            _ => isDark ? "#E2E8F0" : "#334155",
        };
    }


    #endregion // Control Events



    #region Control Methods

    private async void LoadImagesFromCmdArgs()
    {
        var pathToLoad = OwnerWindow.Session.InitialImagePath;
        if (string.IsNullOrEmpty(pathToLoad))
        {
            return;
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

        // Cold macOS file activation may have populated InitialImagePath while awaiting above.
        if (!string.IsNullOrEmpty(OwnerWindow.Session.InitialImagePath))
        {
            pathToLoad = OwnerWindow.Session.InitialImagePath;
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
        var owner = OwnerWindow;
        _selectLastOnSearch = selectLastPhoto;

        // dispose the foreground shell if requested
        var isPrimaryCollection = ReferenceEquals(Photos, Core.DefaultPhotos);
        if (disposeForegroundShell && isPrimaryCollection) Core.ShellProvider?.ForegroundShell = null;


        // check if we should load images from foreground window
        var useForegroundWindow = isPrimaryCollection && (Core.ShellProvider?.CanUseForegroundShell() ?? false);
        var foregroundShell = useForegroundWindow
            ? Core.ShellProvider?.ForegroundShell
            : null;


        Dispatcher.UIThread.Post(async () =>
        {
            if (!IsLoaded || !ReferenceEquals(_ownerWindow, owner)) return;
            using var scope = App.UseWindow(owner);
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
            var initPhoto = Photos.StartLoadingFiles(resolvedPaths, resolvedCurrentPath, searchOptions, Files_Searched, reloadInitPhoto);


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
            if (!IsLoaded) return;
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
        var isEmptyList = Photos.Count == 0;

        // add files to list
        Photos.Add(e.Results);


        // display the last file in a folder; re-applied on every batch, so it settles on the
        // real last item once the search is done
        if (_selectLastOnSearch)
        {
            var lastIndex = (int)Photos.Count - 1;
            Photos.InitPhoto = Photos.Select(lastIndex);
            _ = ViewPhotoAsync(Photos.InitPhoto, true, false);
        }
        // the init photo owns the selection for every batch, incl. the later sub-dir ones
        else if (Photos.InitPhoto is not null)
        {
            // if we haven't found current index for the init photo yet
            if (Photos.CurrentIndex == -1)
            {
                // find index of the init photo and select it
                _ = Photos.Select(Photos.InitPhoto.FilePath);

                // save the init photo to the list
                var initIndex = Photos.CurrentIndex;
                if (initIndex >= 0 && initIndex < Photos.Items.Count)
                {
                    Photos.Items[initIndex]?.Dispose();
                    Photos.Items[initIndex] = Photos.InitPhoto;
                    Photos.Items[initIndex].IsCurrent = true;
                }
            }
        }
        // display the first file in a folder
        else
        {
            Photos.InitPhoto = Photos.Select(0);
            _ = ViewPhotoAsync(Photos.InitPhoto, true, false);
        }


        // Gallery: scroll to the selected item
        if (isEmptyList)
        {
            // set file watcher
            AppAPIProvider.SetFileWatcher(Photos, Core.Config.EnableFileWatcher);

            Dispatcher.UIThread.Post(() =>
            {
                // set photo to the viewer
                PART_Gallery.ScrollToItem(Photos.CurrentIndex);
            });
        }
    }


    public async Task ViewPhotoAsync(Photo? photo, bool useCache = true, bool scrollToThumbnail = true, bool resetZoom = true,
        TransitionRequest? transition = null)
    {
        var owner = OwnerWindow;
        using var windowScope = App.UseWindow(owner);
        // Sequential mode: close the navigation gate here, not inside the post below,
        // so the next key repeat already sees a load in flight
        var isSequential = Core.Config.BrowsingMode == BrowsingMode.Sequential;
        if (isSequential) Interlocked.Increment(ref _sequentialLoadDepth);

        // the old neighborhood's cache pass would compete with this load; a new one starts once it is shown
        Photos.CancelCaching();

        // clear the current in-app message
        _ = PART_Message.ClearAsync();

        Core.DisposeClipboardPhoto();
        OwnerWindow.Session.ImageTransform.Clear();

        // set read options for photo; a cached photo shows without a decode, so name its opening frame here
        if (photo is not null)
        {
            photo.ReadOptions = PhotoReadOptions.FromConfig(photo.GetDefaultFrameIndex());
        }

        Dispatcher.UIThread.Post(async () =>
        {
            using var postedWindowScope = App.UseWindow(owner);
            try
            {
                if (!IsLoaded || !ReferenceEquals(_ownerWindow, owner)) return;
                // apply user settings to the viewer
                PART_Viewer.EnableImagePreview = Core.Config.EnableImagePreview;

                if (scrollToThumbnail)
                {
                    // set photo to the viewer
                    PART_Gallery.ScrollToItem(Photos.CurrentIndex);
                }

                await PART_Viewer.SetPhotoAsync(photo, new PhotoLoadingOptions
                {
                    UseCache = useCache,
                    ResetZoom = resetZoom,
                    Channels = OwnerWindow.Session.ColorChannels,
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
            if (IsLoaded && ReferenceEquals(_ownerWindow, owner))
                Photos.RequestCacheAround(Photos.CurrentIndex);
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
        ApplyFullscreenVisibility();
    }

    internal void SetFullscreenVisibility(bool hideToolbar, bool hideGallery)
    {
        _fullscreenHidesToolbar = hideToolbar;
        _fullscreenHidesGallery = hideGallery;
        ApplyAppLayout();
    }

    private void ApplyFullscreenVisibility()
    {
        PART_Toolbar.IsVisible = !_fullscreenHidesToolbar;
        PART_Gallery.IsVisible = !_fullscreenHidesGallery;
        if (_fullscreenHidesGallery) PART_GalleryResizer.IsVisible = false;
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
        var count = Photos.Items.Count;
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
        if (!Core.Config.ShowGallery || _fullscreenHidesGallery)
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
