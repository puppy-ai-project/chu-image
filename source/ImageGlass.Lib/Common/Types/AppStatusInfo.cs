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
using Avalonia.Threading;
using ImageGlass.Common.Extensions;
using ImageGlass.Common.Localization;
using ImageGlass.Common.Photoing;
using ImageGlass.Common.Types;
using ImageGlass.UI.Viewer;
using ImageGlass.UI.Viewer.ZoomAndPan;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;

namespace ImageGlass.Common;

public partial class AppStatusInfo : PhDisposable
{
    /// <summary>
    /// Gets the tag of the item naming a clipboard image, which no image info tag stands for.
    /// </summary>
    public const string CLIPBOARD_TAG = "ClipboardImage";

    /// <summary>
    /// Gets the separator between the items of the status text.
    /// </summary>
    public const string TEXT_SEPARATOR = "  ︱  ";

    private readonly ImageGlass.Common.Photoing.PhotoManager _photos;
    private readonly ImageGlass.Common.Photoing.PhotoTransform _imageTransform;
    private ViewerControl _viewer;
    private string? _filePath = null;

    public event EventHandler? Changed;


    #region Image Info Tags

    internal string? AppName
    {
        get
        {
            if (Core.Config.ImageInfoTags.Contains(nameof(AppName)))
            {
                return BHelper.AppDisplayName;
            }

            return null;
        }
    }


    internal string? Name
    {
        get
        {
            // skip for clipboard image
            if (Core.ClipboardImage is not null) return null;

            if (Core.Config.ImageInfoTags.Contains(nameof(Name)))
            {
                var askterisk = _imageTransform.HasChanges ? "*" : string.Empty;
                return $"{System.IO.Path.GetFileName(_filePath)}{askterisk}";
            }

            return null;
        }
    }


    internal string? Path
    {
        get
        {
            // skip for clipboard image
            if (Core.ClipboardImage is not null) return null;

            if (Core.Config.ImageInfoTags.Contains(nameof(Path)))
            {
                var askterisk = _imageTransform.HasChanges ? "*" : string.Empty;
                return $"{_filePath}{askterisk}";
            }

            return null;
        }
    }


    internal string? FileSize
    {
        get
        {
            // skip for clipboard image
            if (Core.ClipboardImage is not null) return null;

            if (Core.Config.ImageInfoTags.Contains(nameof(FileSize))
                && _photos.CurrentMetadata != null)
            {
                return _photos.CurrentMetadata.FileSizeFormatted;
            }

            return null;
        }
    }


    internal string? ModifiedDateTime
    {
        get
        {
            // skip for clipboard image
            if (Core.ClipboardImage is not null) return null;

            if ((Core.Config.ImageInfoTags.Contains(nameof(ModifiedDateTime))
                || Core.Config.ImageInfoTags.Contains(nameof(DateTimeAuto)))
                && _photos.CurrentMetadata != null)
            {
                return _photos.CurrentMetadata.FileLastWriteTimeFormatted + " (m)";
            }

            return null;
        }
    }


    internal string? Dimension
    {
        get
        {
            if (Core.Config.ImageInfoTags.Contains(nameof(Dimension)))
            {
                if (Core.ClipboardImage is not null && !Core.ClipboardImage.Size.IsEmpty)
                {
                    return $"{Core.ClipboardImage.Width:n0}×{Core.ClipboardImage.Height:n0}";
                }
                else if (!_viewer.BitmapSize.IsEmpty)
                {
                    var dimension = $"{_viewer.BitmapSize.Width:n0}×{_viewer.BitmapSize.Height:n0}";

                    // the file is bigger than what could be decoded
                    var scale = _viewer.Photo?.DecodeScale ?? 1;
                    if (scale < 1 && _photos.CurrentMetadata is { } meta)
                    {
                        dimension += $" ({meta.Width:n0}×{meta.Height:n0}) – {Math.Round(scale * 100):n0}%";
                    }

                    return dimension;
                }
            }

            return null;
        }
    }


    internal string? FrameCount
    {
        get
        {
            // skip for clipboard image
            if (Core.ClipboardImage is not null) return null;

            if (Core.Config.ImageInfoTags.Contains(nameof(FrameCount))
                && _photos.CurrentMetadata != null
                && _photos.CurrentMetadata.FrameCount > 1)
            {
                var frameInfo = new StringBuilder();
                frameInfo.Append((_viewer.Photo?.FrameIndex ?? 0) + 1);
                frameInfo.Append('/');
                frameInfo.Append(_photos.CurrentMetadata.FrameCount);

                return Core.Lang[LangId._ImageInfo_FrameCount, frameInfo];
            }

            return null;
        }
    }


    internal string? ListCount
    {
        get
        {
            // skip for clipboard image
            if (Core.ClipboardImage is not null) return null;

            if (Core.Config.ImageInfoTags.Contains(nameof(ListCount))
                && _photos.Count > 0)
            {
                var listInfo = new StringBuilder();
                listInfo.Append(_photos.CurrentIndex + 1);
                listInfo.Append('/');
                listInfo.Append(_photos.Count);

                return Core.Lang[LangId._ImageInfo_ListCount, listInfo.ToString()];
            }

            return null;
        }
    }


    internal string? Zoom
    {
        get
        {
            if (Core.Config.ImageInfoTags.Contains(nameof(Zoom)) && _photos.Count > 0)
            {
                return $"{Math.Round(_viewer.ZoomFactor * 100, 2):n2}%";
            }

            return null;
        }
    }


    internal string? ExifRating
    {
        get
        {
            // skip for clipboard image
            if (Core.ClipboardImage is not null) return null;

            if (Core.Config.ImageInfoTags.Contains(nameof(ExifRating))
                && _photos.CurrentMetadata != null)
            {
                return _photos.CurrentMetadata.ExifRatingFormatted;
            }

            return null;
        }
    }


    internal string? ExifDateTime
    {
        get
        {
            // skip for clipboard image
            if (Core.ClipboardImage is not null) return null;

            if ((Core.Config.ImageInfoTags.Contains(nameof(ExifDateTime))
                || Core.Config.ImageInfoTags.Contains(nameof(DateTimeAuto)))
                && _photos.CurrentMetadata != null
                && _photos.CurrentMetadata.ExifDateTime != null)
            {
                return BHelper.FormatDateTime(_photos.CurrentMetadata.ExifDateTime) + " (e)";
            }

            return null;
        }
    }


    internal string? ExifDateTimeOriginal
    {
        get
        {
            // skip for clipboard image
            if (Core.ClipboardImage is not null) return null;

            if ((Core.Config.ImageInfoTags.Contains(nameof(ExifDateTimeOriginal))
                || Core.Config.ImageInfoTags.Contains(nameof(DateTimeAuto)))
                && _photos.CurrentMetadata != null
                && _photos.CurrentMetadata.ExifDateTimeOriginal != null)
            {
                return BHelper.FormatDateTime(_photos.CurrentMetadata.ExifDateTimeOriginal) + " (o)";
            }

            return null;
        }
    }


    internal string? DateTimeAuto
    {
        get
        {
            // skip for clipboard image
            if (Core.ClipboardImage is not null) return null;

            if (Core.Config.ImageInfoTags.Contains(nameof(DateTimeAuto))
                && _photos.CurrentMetadata != null)
            {
                if (_photos.CurrentMetadata.ExifDateTimeOriginal != null)
                {
                    return ExifDateTimeOriginal;
                }

                if (_photos.CurrentMetadata.ExifDateTime != null)
                {
                    return ExifDateTime;
                }

                return ModifiedDateTime;
            }

            return null;
        }
    }


    internal string? HdrInfo
    {
        get
        {
            // skip for clipboard image
            if (Core.ClipboardImage is not null) return null;

            if (Core.Config.ImageInfoTags.Contains(nameof(HdrInfo))
                && _photos.CurrentMetadata is { } meta
                && (meta.IsHdr || meta.IsWideGamut))
            {
                var parts = new System.Collections.Generic.List<string>(4);

                if (meta.IsHdr)
                {
                    var fn = meta.HdrTransferFn switch
                    {
                        Photoing.HdrTransferFunction.PQ => "HDR PQ",
                        Photoing.HdrTransferFunction.HLG => "HDR HLG",
                        Photoing.HdrTransferFunction.GainMap => "HDR Gain Map",
                        Photoing.HdrTransferFunction.ScRgb => "HDR scRGB",
                        _ => "HDR",
                    };
                    parts.Add(fn);

                    if (meta.ContentPeakNits > 0)
                    {
                        parts.Add($"{meta.ContentPeakNits:0} nits");
                    }
                }
                else if (meta.IsWideGamut)
                {
                    parts.Add("Wide Gamut");
                }

                if (meta.BitsPerChannel > 8)
                {
                    parts.Add($"{meta.BitsPerChannel}-bit");
                }

                return string.Join(", ", parts);
            }

            return null;
        }
    }


    internal string? ColorSpace
    {
        get
        {
            // skip for clipboard image
            if (Core.ClipboardImage is not null) return null;

            if (Core.Config.ImageInfoTags.Contains(nameof(ColorSpace))
                && _photos.CurrentMetadata != null
                && _photos.CurrentMetadata.ColorSpace != ImageMagick.ColorSpace.Undefined)
            {
                var colorSpace = _photos.CurrentMetadata.ColorSpace.ToString();
                var colorProfile = !string.IsNullOrEmpty(_photos.CurrentMetadata.ColorProfileName)
                    ? _photos.CurrentMetadata.ColorProfileName
                    : "-";

                if (colorSpace.Equals(colorProfile, StringComparison.OrdinalIgnoreCase))
                {
                    return colorSpace;
                }

                return $"{colorSpace}/{colorProfile}";
            }

            return null;
        }
    }


    internal string? DPI
    {
        get
        {
            // skip for clipboard image
            if (Core.ClipboardImage is not null) return null;

            if (Core.Config.ImageInfoTags.Contains(nameof(DPI))
                && _photos.CurrentMetadata != null
                && _photos.CurrentMetadata.DpiX > 0
                && _photos.CurrentMetadata.DpiY > 0)
            {
                return $"{_photos.CurrentMetadata.DpiX:n0}×{_photos.CurrentMetadata.DpiY:n0} DPI";
            }

            return null;
        }
    }

    #endregion // Image Info Tags



    /// <summary>
    /// Gets the values of the image info tags in their configured order, skipping the empty ones.
    /// </summary>
    public List<ImageInfoItem> GetItems()
    {
        var items = new List<ImageInfoItem>(Core.Config.ImageInfoTags.Count + 1);

        if (Core.ClipboardImage is not null)
        {
            items.Add(new(CLIPBOARD_TAG, Core.Lang[LangId._ClipboardImage]));
        }

        foreach (var tag in Core.Config.ImageInfoTags)
        {
            var tagValue = GetTagValue(tag);

            if (!string.IsNullOrWhiteSpace(tagValue))
            {
                items.Add(new(tag, tagValue));
            }
        }

        return items;
    }


    /// <summary>
    /// Joins the items into the status text.
    /// </summary>
    public static string ToText(IReadOnlyList<ImageInfoItem> items)
    {
        var strBuilder = new StringBuilder();

        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                strBuilder.Append(TEXT_SEPARATOR);
            }

            strBuilder.Append(items[i].Value);
        }

        return strBuilder.ToString();
    }


    /// <summary>
    /// Gets the value of an image info tag; <c>null</c> when it has none or the tag is unknown.
    /// </summary>
    private string? GetTagValue(string tag) => tag switch
    {
        nameof(AppName) => AppName,
        nameof(Name) => Name,
        nameof(Path) => Path,
        nameof(FileSize) => FileSize,
        nameof(ModifiedDateTime) => ModifiedDateTime,

        nameof(Dimension) => Dimension,
        nameof(FrameCount) => FrameCount,
        nameof(ListCount) => ListCount,
        nameof(Zoom) => Zoom,

        nameof(ExifRating) => ExifRating,
        nameof(ExifDateTime) => ExifDateTime,
        nameof(ExifDateTimeOriginal) => ExifDateTimeOriginal,
        nameof(DateTimeAuto) => DateTimeAuto,
        nameof(HdrInfo) => HdrInfo,
        nameof(ColorSpace) => ColorSpace,
        nameof(DPI) => DPI,
        _ => null,
    };


    public AppStatusInfo(ViewerControl viewer, ImageGlass.Common.Photoing.PhotoManager photos, ImageGlass.Common.Photoing.PhotoTransform imageTransform)
    {
        _viewer = viewer;
        _photos = photos;
        _imageTransform = imageTransform;

        _photos.PropertyChanged += Photos_PropertyChanged;
        _imageTransform.Changed += ImageTransform_Changed;
        Core.Config.PropertyChanged += Config_PropertyChanged;
        Core.LanguageChanged += Core_LanguageChanged;
        _viewer.ZoomChanged += Viewer_ZoomChanged;
        _viewer.PhotoFrameChanged += Viewer_PhotoFrameChanged;
    }


    protected override void OnDisposing()
    {
        base.OnDisposing();

        _photos.PropertyChanged -= Photos_PropertyChanged;
        _imageTransform.Changed -= ImageTransform_Changed;
        Core.Config.PropertyChanged -= Config_PropertyChanged;
        Core.LanguageChanged -= Core_LanguageChanged;
        _viewer.ZoomChanged -= Viewer_ZoomChanged;
        _viewer.PhotoFrameChanged -= Viewer_PhotoFrameChanged;
    }


    private void Core_LanguageChanged(object? sender, EventArgs e)
    {
        // some values are localized, e.g. the file count
        Changed?.Invoke(this, EventArgs.Empty);
    }


    private void Config_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // the status text is built from these tags, so refresh it when they change
        if (e.PropertyName != nameof(Core.Config.ImageInfoTags)) return;

        Dispatcher.UIThread.Post(() =>
        {
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }


    private void Photos_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(_photos.CurrentFilePath))
        {
            _filePath = string.IsNullOrEmpty(_photos.CurrentFilePath)
                ? _photos.GetFilePath(_photos.CurrentIndex)
                : BHelper.ResolvePath(_photos.CurrentFilePath);
        }


        Dispatcher.UIThread.Post(() =>
        {
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }


    private void ImageTransform_Changed(PhotoTransform sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }


    private void Viewer_ZoomChanged(ViewerControl sender, ViewerZoomEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }


    private void Viewer_PhotoFrameChanged(ViewerControl sender, PhotoFrameChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }


}


/// <summary>
/// The formatted value of one image info tag (see <see cref="Config.ImageInfoTags"/>), e.g. the file size.
/// </summary>
public readonly record struct ImageInfoItem(string Tag, string Value);
