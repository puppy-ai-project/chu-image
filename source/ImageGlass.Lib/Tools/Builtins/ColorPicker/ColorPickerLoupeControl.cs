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
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ImageGlass.Common.Extensions;
using ImageGlass.Common.Types;
using ImageGlass.UI;
using ImageGlass.UI.Viewer;
using System;
using System.Globalization;

namespace ImageGlass.Tools;


/// <summary>
/// A floating pixel-loupe magnifier overlay that follows the cursor on the viewer
/// while the Color Picker tool is active.
/// </summary>
internal sealed class ColorPickerLoupeControl : PhControl
{
    public const int GRID_COUNT = 9; // 9x9 pixel grid (odd so there is an exact center pixel)
    private const double CELL_SIZE = 12.0;
    private const double LOUPE_SIZE = GRID_COUNT * CELL_SIZE; // 108px
    private const double PILL_HEIGHT = 24.0;
    private const double PILL_GAP = 6.0;
    private const double CONTROL_WIDTH = LOUPE_SIZE + 24.0;
    private const double CONTROL_HEIGHT = LOUPE_SIZE + PILL_GAP + PILL_HEIGHT + 16.0;
    private const double CURSOR_OFFSET = 20.0;

    private readonly Color[] _pixels = new Color[GRID_COUNT * GRID_COUNT];
    private readonly TranslateTransform _offset = new();
    private readonly FontFamily _codeFont = new(Const.FONT_CODE);

    private Point _viewerPointerPos;
    private PixelPoint _sourcePoint;
    private Color _centerColor = Const.COLOR_EMPTY;
    private bool _isActive;

    private static readonly IImmutableBrush _pillBgBrush = new ImmutableSolidColorBrush(Color.FromArgb(230, 20, 22, 27));
    private static readonly ImmutablePen _pillBorderPen = new(new ImmutableSolidColorBrush(Color.FromArgb(65, 255, 255, 255)), 1);
    private static readonly IImmutableBrush _checkerLightBrush = new ImmutableSolidColorBrush(Color.FromRgb(56, 60, 70));
    private static readonly IImmutableBrush _checkerDarkBrush = new ImmutableSolidColorBrush(Color.FromRgb(38, 41, 49));
    private static readonly ImmutablePen _gridHairlinePen = new(new ImmutableSolidColorBrush(Color.FromArgb(36, 255, 255, 255)), 0.6);
    private static readonly ImmutablePen _crosshairLinePen = new(new ImmutableSolidColorBrush(Color.FromArgb(80, 255, 255, 255)), 1);
    private static readonly ImmutablePen _outerRimDarkPen = new(new ImmutableSolidColorBrush(Color.FromArgb(190, 12, 14, 18)), 3.2);
    private static readonly ImmutablePen _centerWhitePen = new(Brushes.White, 1.8);
    private static readonly ImmutablePen _centerBlackPen = new(Brushes.Black, 0.9);


    public ColorPickerLoupeControl()
    {
        IsHitTestVisible = false;
        IsVisible = false;
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        Width = CONTROL_WIDTH;
        Height = CONTROL_HEIGHT;
        RenderTransform = _offset;
        ClipToBounds = false;
    }


    public void UpdatePosition(ViewerControl viewer, Point pointerPos, PixelPoint srcPoint, Color centerColor)
    {
        _viewerPointerPos = pointerPos;
        _sourcePoint = srcPoint;
        _centerColor = centerColor;

        if (!viewer.GetColorGridAt(srcPoint.X, srcPoint.Y, GRID_COUNT, _pixels))
        {
            HideLoupe();
            return;
        }

        _isActive = true;
        IsVisible = true;

        // Place bottom-right of cursor by default; flip if close to right/bottom viewer edge
        var bounds = viewer.Bounds;
        var targetX = pointerPos.X + CURSOR_OFFSET - (CONTROL_WIDTH - LOUPE_SIZE) / 2;
        var targetY = pointerPos.Y + CURSOR_OFFSET;

        if (targetX + CONTROL_WIDTH > bounds.Width - 8)
        {
            targetX = pointerPos.X - CURSOR_OFFSET - LOUPE_SIZE - (CONTROL_WIDTH - LOUPE_SIZE) / 2;
        }
        if (targetY + CONTROL_HEIGHT > bounds.Height - 8)
        {
            targetY = pointerPos.Y - CURSOR_OFFSET - CONTROL_HEIGHT + 8;
        }

        _offset.X = Math.Clamp(targetX, 2, Math.Max(2, bounds.Width - CONTROL_WIDTH - 2));
        _offset.Y = Math.Clamp(targetY, 2, Math.Max(2, bounds.Height - CONTROL_HEIGHT - 2));

        InvalidateVisual();
    }


    public void HideLoupe()
    {
        if (!_isActive && !IsVisible) return;
        _isActive = false;
        IsVisible = false;
    }


    public override void Render(DrawingContext c)
    {
        base.Render(c);
        if (!_isActive) return;

        var loupeLeft = (CONTROL_WIDTH - LOUPE_SIZE) / 2;
        const double loupeTop = 4.0;
        var loupeRect = new Rect(loupeLeft, loupeTop, LOUPE_SIZE, LOUPE_SIZE);
        var center = loupeRect.Center;
        var radius = LOUPE_SIZE / 2;

        // 1. Circular clip for the magnified pixel grid
        var circleGeo = new EllipseGeometry(loupeRect);
        using (c.PushGeometryClip(circleGeo))
        {
            // Draw 9x9 pixel cells
            for (var gy = 0; gy < GRID_COUNT; gy++)
            {
                var cellY = loupeTop + gy * CELL_SIZE;
                for (var gx = 0; gx < GRID_COUNT; gx++)
                {
                    var cellX = loupeLeft + gx * CELL_SIZE;
                    var cellRect = new Rect(cellX, cellY, CELL_SIZE, CELL_SIZE);
                    var pxColor = _pixels[gy * GRID_COUNT + gx];

                    // Subtle dark checkerboard under transparent/out-of-bounds pixels
                    if (pxColor.A < 255)
                    {
                        var half = CELL_SIZE / 2;
                        c.FillRectangle(_checkerDarkBrush, new Rect(cellX, cellY, half, half));
                        c.FillRectangle(_checkerLightBrush, new Rect(cellX + half, cellY, half, half));
                        c.FillRectangle(_checkerLightBrush, new Rect(cellX, cellY + half, half, half));
                        c.FillRectangle(_checkerDarkBrush, new Rect(cellX + half, cellY + half, half, half));
                    }

                    if (!pxColor.IsEmpty && pxColor.A > 0)
                    {
                        c.FillRectangle(new ImmutableSolidColorBrush(pxColor), cellRect);
                    }

                    // Hairline pixel grid
                    c.DrawRectangle(null, _gridHairlinePen, cellRect);
                }
            }

            // Crosshair guide lines pointing to the center cell
            var midIdx = GRID_COUNT / 2;
            var centerCellRect = new Rect(
                loupeLeft + midIdx * CELL_SIZE,
                loupeTop + midIdx * CELL_SIZE,
                CELL_SIZE,
                CELL_SIZE);

            c.DrawLine(_crosshairLinePen, new Point(center.X, loupeTop), new Point(center.X, centerCellRect.Top));
            c.DrawLine(_crosshairLinePen, new Point(center.X, centerCellRect.Bottom), new Point(center.X, loupeRect.Bottom));
            c.DrawLine(_crosshairLinePen, new Point(loupeLeft, center.Y), new Point(centerCellRect.Left, center.Y));
            c.DrawLine(_crosshairLinePen, new Point(centerCellRect.Right, center.Y), new Point(loupeRect.Right, center.Y));

            // Highlight center pixel (double contrasting border: outer black, inner white)
            c.DrawRectangle(null, _centerBlackPen, centerCellRect.Inflate(0.8));
            c.DrawRectangle(null, _centerWhitePen, centerCellRect.Deflate(0.4));
        }

        // 2. Loupe circular ring: dark outer rim + current color inner ring + white highlight hairline
        c.DrawEllipse(null, _outerRimDarkPen, center, radius + 1.2, radius + 1.2);

        var ringColor = _centerColor.IsEmpty || _centerColor.A < 40
            ? Color.FromRgb(56, 189, 248)
            : _centerColor.NoAlpha();
        var colorRingPen = new ImmutablePen(new ImmutableSolidColorBrush(ringColor), 2.4);
        c.DrawEllipse(null, colorRingPen, center, radius, radius);
        c.DrawEllipse(null, _pillBorderPen, center, radius - 1.2, radius - 1.2);

        // 3. Bottom HUD capsule: color dot + HEX + (X, Y)
        var pillTop = loupeRect.Bottom + PILL_GAP;
        var hexStr = _centerColor.ToHex(skipAlpha: true);
        var labelText = $"{hexStr}  {_sourcePoint.X},{_sourcePoint.Y}";

        var formatted = new FormattedText(
            labelText,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(_codeFont, FontStyle.Normal, FontWeight.SemiBold),
            10.5,
            Brushes.White);

        var dotSize = 9.0;
        var pillWidth = Math.Max(92.0, Math.Min(CONTROL_WIDTH, dotSize + 6.0 + formatted.Width + 16.0));
        var pillLeft = (CONTROL_WIDTH - pillWidth) / 2;
        var pillRect = new Rect(pillLeft, pillTop, pillWidth, PILL_HEIGHT);

        c.DrawRectangle(_pillBgBrush, _pillBorderPen, new RoundedRect(pillRect, new CornerRadius(PILL_HEIGHT / 2)));

        // Swatch dot inside capsule
        var contentStartX = pillLeft + (pillWidth - (dotSize + 5.0 + formatted.Width)) / 2;
        var dotCenter = new Point(contentStartX + dotSize / 2, pillTop + PILL_HEIGHT / 2);
        c.DrawEllipse(new ImmutableSolidColorBrush(ringColor), new ImmutablePen(Brushes.White, 1), dotCenter, dotSize / 2, dotSize / 2);

        // Text inside capsule
        var textOrigin = new Point(contentStartX + dotSize + 5.0, pillTop + (PILL_HEIGHT - formatted.Height) / 2);
        c.DrawText(formatted, textOrigin);
    }
}
