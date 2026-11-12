using ImageGlass.Common.Photoing;
using System;

namespace ImageGlass.Common.Windows;

/// <summary>State that belongs to one image window rather than the application process.</summary>
public sealed class ImageWindowSession
{
    public PhotoManager Photos { get; }

    public ImageWindowSession(PhotoManager photos)
    {
        Photos = photos ?? throw new ArgumentNullException(nameof(photos));
    }
}
