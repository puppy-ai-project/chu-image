using ImageGlass.Common.Photoing;
using ImageGlass.Common.ServiceProviders;
using SkiaSharp;
using System;

namespace ImageGlass.Common.Windows;

/// <summary>State that belongs to one image window rather than the application process.</summary>
public sealed class ImageWindowSession
{
    public PhotoManager Photos { get; }
    public PhotoTransform ImageTransform { get; }
    internal bool IsBusy { get; set; }
    internal AppAPIProvider? Api { get; set; }
    internal Photo? ClipboardImage { get; set; }
    internal string? TempImagePath { get; set; }
    internal Core.TempImageState TempImageState { get; set; }
    internal ColorChannels ColorChannels { get; set; } = ColorChannels.RGBA;
    public IWindowColorProfileProvider? ColorProfileProvider { get; set; }
    internal SKColorSpace? DestColorProfile { get; set; }
    internal bool IsDestColorProfileSupported { get; set; } = true;
    /// <summary>The image this window should open when its view is first loaded.</summary>
    public string? InitialImagePath { get; set; }

    public ImageWindowSession(PhotoManager photos, string? initialImagePath = null, PhotoTransform? imageTransform = null)
    {
        Photos = photos ?? throw new ArgumentNullException(nameof(photos));
        InitialImagePath = initialImagePath;
        ImageTransform = imageTransform ?? new PhotoTransform();
    }
}
