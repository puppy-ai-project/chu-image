using Avalonia.Input;
using Avalonia.Input.GestureRecognizers;
using Avalonia.Interactivity;
using global::Avalonia;
using System;


namespace ImageGlass.UI.Viewer.ZoomAndPan;


/// <summary>
/// Indicates pan gesture status
/// </summary>
public enum PanGestureStatus
{
    Started,
    Running,
    Completed,
}

/// <summary>
/// Sets the pan directions
/// </summary>
[Flags]
public enum PanDirection
{
    /// <summary>
    /// Disables the pan
    /// </summary>
    None = 0,
    /// <summary>
    /// Allows pan to left
    /// </summary>
    Left = 1,
    /// <summary>
    /// Allows pan to right
    /// </summary>
    Right = 2,
    /// <summary>
    /// Allows pan up
    /// </summary>
    Up = 4,
    /// <summary>
    /// Allows pan down
    /// </summary>
    Down = 8,
}



/// <summary>
/// The gesture recognizer for pan gesture 
/// </summary>
public class PhPanGestureRecognizer : GestureRecognizer
{
    private IInputElement? _inputElement;
    private IPointer? _tracking;
    private Point _startPosition;
    private Point _delta;
    private PanGestureStatus _state;
    private Visual? _visual;
    private Visual? _parent;

    public event EventHandler<PanUpdatedEventArgs>? Panning;

    public PanDirection Direction { get; set; } = PanDirection.Left | PanDirection.Right | PanDirection.Up | PanDirection.Down;

    public float Threshold { get; set; } = 5;


    /// <inheritdoc />
    protected override void PointerPressed(PointerPressedEventArgs e)
    {
        if (e.Pointer.Type == PointerType.Mouse) return;

        _inputElement = Target;
        _tracking = e.Pointer;
        _visual = Target as Visual;
        _parent = _visual?.Parent as Visual;
        _startPosition = e.GetPosition(_parent);
        _state = PanGestureStatus.Started;
    }


    /// <inheritdoc />
    protected override void PointerMoved(PointerEventArgs e)
    {
        if (e.Pointer != _tracking) return;

        var currentPosition = e.GetPosition(_parent);
        _delta = currentPosition - _startPosition;

        var currentDirection = PanDirection.None;
        if (_delta.X < -Threshold)
        {
            currentDirection |= PanDirection.Left;
        }
        else if (_delta.X > Threshold)
        {
            currentDirection |= PanDirection.Right;
        }

        if (_delta.Y < -Threshold)
        {
            currentDirection |= PanDirection.Up;
        }
        else if (_delta.Y > Threshold)
        {
            currentDirection |= PanDirection.Down;
        }

        if ((currentDirection & Direction) == 0)
        {
            return;
        }

        if (Math.Abs(_delta.X) < Threshold && Math.Abs(_delta.Y) < Threshold)
        {
            return;
        }

        if (_state == PanGestureStatus.Started)
        {
            var args = new PanUpdatedEventArgs(PanGestureStatus.Started, 0, 0, currentPosition);
            Panning?.Invoke(_inputElement, args);
            e.Handled = args.Handled;
            if (e.Handled) Capture(e.Pointer);
        }


        var args2 = new PanUpdatedEventArgs(PanGestureStatus.Running, _delta.X, _delta.Y, currentPosition);
        Panning?.Invoke(_inputElement, args2);
        _state = PanGestureStatus.Running;

        e.Handled = args2.Handled;
        if (e.Handled) e.PreventGestureRecognition();
    }


    /// <inheritdoc />
    protected override void PointerReleased(PointerReleasedEventArgs e)
    {
        if (e.Pointer != _tracking) return;
        _tracking = null;

        if (_state != PanGestureStatus.Running) return;

        _state = PanGestureStatus.Completed;
        var currentPosition = e.GetPosition(_parent);
        var delta = currentPosition - _startPosition;

        var args = new PanUpdatedEventArgs(PanGestureStatus.Completed, delta.X, delta.Y, currentPosition);
        Panning?.Invoke(_inputElement, args);
        e.Handled = args.Handled;
    }


    /// <inheritdoc />
    protected override void PointerCaptureLost(IPointer pointer)
    {
        var delta = _delta;
        _tracking = null;
        _delta = default;

        if (_state != PanGestureStatus.Running) return;

        var args = new PanUpdatedEventArgs(PanGestureStatus.Completed, delta.X, delta.Y, null);
        Panning?.Invoke(_inputElement, args);
    }
}



/// <summary>
/// Contains the pan updates event data 
/// </summary>
public class PanUpdatedEventArgs : RoutedEventArgs
{
    public PanGestureStatus StatusType { get; set; }
    public double TotalX { get; set; }
    public double TotalY { get; set; }
    public Point? Position { get; set; }


    public PanUpdatedEventArgs(PanGestureStatus statusType, double totalX, double totalY, Point? position)
    {
        StatusType = statusType;
        TotalX = totalX;
        TotalY = totalY;
        Position = position;
    }

}
