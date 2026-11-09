using Avalonia;
using Avalonia.Controls;
using ImageGlass.Common.ServiceProviders;
using System;
using System.Diagnostics;

namespace ImageGlass.UI.Viewer;

public partial class ViewerControl
{
    internal bool EnableCropModifierPan { get; set; }
    private Vector _mousePanVelocity;
    private bool _mousePanPositionEnabled;
    private long _mousePanSampleTime;
    private int _mousePanGeneration;

    internal void PanCropImage(double x, double y)
    {
        if (FeatureManager.IsPanLocked()) return;
        CancelMousePan();
        _mousePanPositionEnabled = true;
        PanTo(x, y, null);
    }

    internal void ResetCropView()
    {
        CancelMousePan();
        _mousePanPositionEnabled = false;
        SetZoomMode(ZoomMode.ScaleToFit);
        InvalidateVisual();
    }

    private void BeginMousePan()
    {
        CancelMousePan();
        _mousePanSampleTime = Stopwatch.GetTimestamp();
    }

    private void TrackMousePan(double x, double y)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(_mousePanSampleTime, now).TotalSeconds;
        _mousePanSampleTime = now;
        if (elapsed <= 0 || elapsed > 0.15)
        {
            _mousePanVelocity = default;
            return;
        }

        // Smooth noisy pointer samples and cap unusually large input spikes.
        var sample = new Vector(Math.Clamp(x / elapsed, -3000, 3000), Math.Clamp(y / elapsed, -3000, 3000));
        var weight = 1 - Math.Exp(-elapsed / 0.04);
        _mousePanVelocity = _mousePanVelocity * (1 - weight) + sample * weight;
    }

    private void EndMousePan()
    {
        if (Stopwatch.GetElapsedTime(_mousePanSampleTime).TotalSeconds > 0.1
            || _mousePanVelocity.Length < 80 || FeatureManager.IsPanLocked())
        {
            CancelMousePan();
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var generation = ++_mousePanGeneration;
        TimeSpan? lastFrame = null;
        void Animate(TimeSpan time)
        {
            if (generation != _mousePanGeneration || FeatureManager.IsPanLocked() || TopLevel.GetTopLevel(this) != topLevel) return;
            if (lastFrame is { } previous)
            {
                var elapsed = Math.Clamp((time - previous).TotalSeconds, 0, 0.05);
                var before = _logicalSrcPoint;
                PanTo(_mousePanVelocity.X * elapsed, _mousePanVelocity.Y * elapsed, null);
                // Stop each axis independently when it reaches its pan boundary.
                _mousePanVelocity = new Vector(
                    Math.Abs(_logicalSrcPoint.X - before.X) < 0.00001 ? 0 : _mousePanVelocity.X,
                    Math.Abs(_logicalSrcPoint.Y - before.Y) < 0.00001 ? 0 : _mousePanVelocity.Y);
                _mousePanVelocity *= Math.Exp(-5 * elapsed);
                if (_mousePanVelocity.Length < 10) return;
            }
            lastFrame = time;
            topLevel.RequestAnimationFrame(Animate);
        }
        topLevel.RequestAnimationFrame(Animate);
    }

    private void CancelMousePan()
    {
        _mousePanGeneration++;
        _mousePanVelocity = default;
    }
}
