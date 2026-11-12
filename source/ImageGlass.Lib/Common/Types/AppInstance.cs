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
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ImageGlass.Common.Types;

public partial class AppInstance : PhDisposable
{
    private const string ACK_ACCEPTED = "OK";
    private const string ACK_REJECTED = "REJECT";
    private static readonly UTF8Encoding _utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _id;
    private readonly string _pipeName;

    // Windows uses a named mutex; Unix cannot, so it locks a file instead (see TryLockFile)
    private Mutex? _mutex;
    private FileStream? _lockFile;
    private volatile bool _isAccepting = true;
    private int _pipeStarted;
    private Task? _pipeServerTask;
    private readonly CancellationTokenSource _pipeStopCts = new();
    private readonly TaskCompletionSource _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stoppedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);


    /// <summary>
    /// Indicates whether this is the first instance of this app.
    /// </summary>
    public bool IsFirstInstance { get; private set; }

    /// <summary>
    /// Indicates whether this primary instance is still accepting forwarded invocations.
    /// </summary>
    public bool IsAccepting => _isAccepting && !IsDisposed;


    /// <summary>
    /// Event raised when arguments are received from successive instances.
    /// </summary>
    public event TEventHandler<AppInstance, InstanceInvokedEventArgs>? InstanceInvoked;


    public AppInstance(string id)
    {
        _id = id;

        // the Unix pipe path (/tmp/CoreFxPipe_*) is not user-scoped, so keep users apart by name
        _pipeName = OperatingSystem.IsWindows()
            ? id
            : $"{id}-{Environment.UserName}";

        IsFirstInstance = TryAcquireLock();

        // start the pipe server
        if (IsFirstInstance)
        {
            StartPipeServer();
        }
    }


    private bool TryAcquireLock()
    {
        if (OperatingSystem.IsWindows())
        {
            var mutex = new Mutex(true, _id, out var isFirstInstance);
            if (isFirstInstance)
            {
                _mutex = mutex;
                return true;
            }

            mutex.Dispose();
            return false;
        }

        return TryLockFile(_pipeName, out _lockFile);
    }


    /// <summary>
    /// Marks the primary instance ready to dispatch forwarded arguments to <see cref="InstanceInvoked"/>.
    /// </summary>
    public void MarkReady() => _readyTcs.TrySetResult();

    /// <summary>
    /// Stops accepting new forwarded invocations so closing instances reject clients immediately instead of dropping arguments.
    /// </summary>
    public void StopAccepting()
    {
        _isAccepting = false;
        _stoppedTcs.TrySetResult();
    }


    /// <summary>
    /// Takes an exclusive lock on a file next to the pipe socket, the Unix stand-in for the named
    /// mutex: NativeAOT has no cross-process named mutex there, so every launch would look first.
    /// </summary>
    /// <returns><c>true</c> if this process took the lock, i.e. it is the first instance.</returns>
    private static bool TryLockFile(string name, out FileStream? lockFile)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{name}.lock");

        try
        {
            // the OS drops this lock when the process dies, so a crash never blocks the next launch
            lockFile = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            lockFile = null;
            return false;
        }
        // an unusable lock path must not stop the app from starting, so claim the first instance
        catch
        {
            lockFile = null;
            return true;
        }
    }


    protected override void OnDisposing()
    {
        base.OnDisposing();

        StopAccepting();
        _pipeStopCts.Cancel();
        try { _pipeServerTask?.Wait(500); } catch { }
        _pipeStopCts.Dispose();

        if (_mutex is not null && IsFirstInstance)
        {
            try { _mutex.ReleaseMutex(); } catch { }
            _mutex.Dispose();
            _mutex = null;
        }

        // releases the lock; the empty file is left behind for the next instance to re-lock
        _lockFile?.Dispose();
        _lockFile = null;
    }


    /// <summary>
    /// Sends arguments to an existing instance, or promotes this process to primary if the existing one is shutting down.
    /// </summary>
    /// <returns><c>true</c> if an existing instance accepted the arguments; <c>false</c> if this process should run as primary.</returns>
    public bool SendArgsOrPromoteToPrimary(string cmd, string[] args, int retryTimeoutMs = 2500)
    {
        if (IsFirstInstance) return false;

        var sw = Stopwatch.StartNew();
        var connectTimeoutMs = 5000;

        while (true)
        {
            if (TrySendArgsOnce(cmd, args, connectTimeoutMs, out var wasRejected))
            {
                return true;
            }

            if (TryAcquireLock())
            {
                IsFirstInstance = true;
                StartPipeServer();
                return false;
            }

            if (sw.ElapsedMilliseconds >= retryTimeoutMs)
            {
                return false;
            }

            // Once rejected (or after first connect attempt), use short retries while the exiting primary releases its lock.
            connectTimeoutMs = wasRejected ? 150 : 500;
            Thread.Sleep(25);
        }
    }


    /// <summary>
    /// Sends arguments to existing instances.
    /// </summary>
    /// <returns><c>false</c> if no existing instance took them, so the caller must start normally.</returns>
    public bool SendArgsToExistingInstances(string cmd, params string[] args)
    {
        // only send if this is NOT the first instance
        if (IsFirstInstance) return false;

        return TrySendArgsOnce(cmd, args, connectTimeoutMs: 5000, out _);
    }


    private bool TrySendArgsOnce(string cmd, string[] args, int connectTimeoutMs, out bool wasRejected)
    {
        wasRejected = false;

        try
        {
            // Allow a starting primary process time to create and accept the pipe, then wait for ACK/REJECT.
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            client.Connect(connectTimeoutMs);

            using var writer = new StreamWriter(client, _utf8NoBom, leaveOpen: true);
            writer.WriteLine(args.Length + 1); // args count + cmd
            writer.WriteLine(cmd);

            foreach (var arg in args)
            {
                writer.WriteLine(arg);
            }

            writer.Flush();

            using var reader = new StreamReader(client, _utf8NoBom, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            using var ackCts = new CancellationTokenSource(5000);
            var ack = reader.ReadLineAsync(ackCts.Token).AsTask().GetAwaiter().GetResult();

            if (string.Equals(ack, ACK_ACCEPTED, StringComparison.Ordinal))
            {
                return true;
            }

            wasRejected = string.Equals(ack, ACK_REJECTED, StringComparison.Ordinal);
            return false;
        }
        catch
        {
            return false;
        }
    }


    /// <summary>
    /// Starts a pipe server on a background thread.
    /// </summary>
    private void StartPipeServer()
    {
        if (Interlocked.CompareExchange(ref _pipeStarted, 1, 0) != 0) return;
        _pipeServerTask = Task.Run(() => StartPipeServerAsync__());
    }


    /// <summary>
    /// Start a pipe server.
    /// </summary>
    private async Task StartPipeServerAsync__()
    {
        while (!IsDisposed && !_pipeStopCts.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(_pipeName,
                    PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                // wait for connection
                await server.WaitForConnectionAsync(_pipeStopCts.Token);

                using var reader = new StreamReader(server, _utf8NoBom, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

                // read first line which contains the number of arguments
                var countStr = await reader.ReadLineAsync(_pipeStopCts.Token);
                if (!int.TryParse(countStr, out var argsCount) || argsCount is < 1 or > 128) continue;

                // create array to hold the arguments
                var args = new string[argsCount - 1]; // exclude cmd (first arg)
                var cmd = string.Empty;

                // read each argument line by line
                for (var i = 0; i < argsCount; i++)
                {
                    var text = await reader.ReadLineAsync(_pipeStopCts.Token) ?? string.Empty;

                    if (i == 0) cmd = text;
                    else args[i - 1] = text;
                }

                var accepted = false;
                if (_isAccepting && !IsDisposed)
                {
                    if (!_readyTcs.Task.IsCompleted)
                    {
                        var completed = await Task.WhenAny(_readyTcs.Task, _stoppedTcs.Task).WaitAsync(_pipeStopCts.Token);
                        if (completed != _readyTcs.Task) _isAccepting = false;
                    }

                    if (_isAccepting && !IsDisposed && InstanceInvoked is not null)
                    {
                        var invokeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        using (_pipeStopCts.Token.Register(static s => ((TaskCompletionSource<bool>)s!).TrySetResult(false), invokeTcs))
                        {
                            Dispatcher.UIThread.Post(() =>
                            {
                                if (!_isAccepting || IsDisposed || _pipeStopCts.IsCancellationRequested || InstanceInvoked is not { } handler)
                                {
                                    _ = invokeTcs.TrySetResult(false);
                                    return;
                                }

                                try
                                {
                                    var eventArgs = new InstanceInvokedEventArgs(_id, cmd, args);
                                    handler.Invoke(this, eventArgs);
                                    _ = invokeTcs.TrySetResult(eventArgs.Accepted);
                                }
                                catch
                                {
                                    _ = invokeTcs.TrySetResult(false);
                                }
                            });

                            accepted = await invokeTcs.Task;
                        }
                    }
                }

                using var writer = new StreamWriter(server, _utf8NoBom, leaveOpen: true);
                await writer.WriteLineAsync(accepted ? ACK_ACCEPTED : ACK_REJECTED);
                await writer.FlushAsync();

                if (OperatingSystem.IsWindows())
                {
                    try { server.WaitForPipeDrain(); } catch { }
                }
            }
            catch (OperationCanceledException) when (_pipeStopCts.IsCancellationRequested || IsDisposed)
            {
                break;
            }
            catch { }
        }
    }


}



/// <summary>
/// Holds a list of arguments given to an application at startup.
/// </summary>
public class InstanceInvokedEventArgs(string id, string cmd, string[] args) : EventArgs
{
    public string Id { get; set; } = id;
    public string Command { get; set; } = cmd;
    public string[] Arguments { get; set; } = args;
    public bool Accepted { get; set; }
}

