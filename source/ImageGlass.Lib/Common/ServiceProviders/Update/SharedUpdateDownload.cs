/*
ChuImage - A Modern, Seamless Photo Viewer
Copyright (C) 2010 - 2026 DUONG DIEU PHAP
Secondary development by puppy.
*/
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ImageGlass.Common.ServiceProviders.Update;

public enum SharedDownloadStatus { Idle, Downloading, Ready, Failed }

public sealed record SharedDownloadState(SharedDownloadStatus Status, UpdateReleaseInfo? Release = null,
    string? FileName = null, string? FilePath = null, long BytesRead = 0, long TotalBytes = -1,
    double BytesPerSecond = 0, string? Error = null);

/// <summary>One application-wide update download, independent of any window.</summary>
public static class SharedUpdateDownload
{
    private static CancellationTokenSource? _cancellation;
    public static SharedDownloadState State { get; private set; } = new(SharedDownloadStatus.Idle);
    public static event EventHandler? Changed;

    // All state changes and notifications happen on the UI thread.
    private static void SetState(SharedDownloadState state)
    {
        State = state;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void Start(UpdateReleaseInfo release)
    {
        if (State.Status == SharedDownloadStatus.Downloading) return;
        if (!Uri.TryCreate(release.UpdateUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            SetState(new(SharedDownloadStatus.Failed, release, Error: "更新包链接不符合安全标准（必须使用 HTTPS）。"));
            return;
        }

        var extension = OperatingSystem.IsMacOS() ? ".zip" : OperatingSystem.IsWindows() ? ".msi" : ".zip";
        var name = Path.GetFileName(uri.LocalPath);
        if (string.IsNullOrWhiteSpace(name) || !name.Contains('.')) name = $"ChuImage-{release.Version}{extension}";
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (!Directory.Exists(directory)) directory = Path.GetTempPath();
        var target = Path.Combine(directory, name);
        for (var index = 1; File.Exists(target); index++)
            target = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(name)} ({index}){Path.GetExtension(name)}");
        _cancellation = new CancellationTokenSource();
        SetState(new(SharedDownloadStatus.Downloading, release, name));
        _ = DownloadAsync(release, uri, target, _cancellation);
    }

    public static void Cancel() => _cancellation?.Cancel();

    private static async Task DownloadAsync(UpdateReleaseInfo release, Uri uri, string target, CancellationTokenSource cancellation)
    {
        var temp = target + ".download-" + Guid.NewGuid().ToString("N");
        var token = cancellation.Token;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
                throw new IOException("下载请求被重定向到了非安全链接。");
            var total = response.Content.Headers.ContentLength ?? -1;
            const long maxSize = 1024L * 1024 * 1024;
            if (total > maxSize) throw new IOException("安装包大小超出合理限制（> 1GB）。");
            long read = 0, lastRead = 0;
            var watch = Stopwatch.StartNew();
            var lastUpdate = TimeSpan.Zero;
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                int count;
                while ((count = await input.ReadAsync(buffer, token)) > 0)
                {
                    read += count;
                    if (read > maxSize) throw new IOException("下载数据大小超出安全限制。");
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                    var elapsed = watch.Elapsed - lastUpdate;
                    if (elapsed.TotalMilliseconds < 200) continue;
                    var speed = (read - lastRead) / elapsed.TotalSeconds;
                    lastRead = read;
                    lastUpdate = watch.Elapsed;
                    SetState(new(SharedDownloadStatus.Downloading, release, Path.GetFileName(target),
                        BytesRead: read, TotalBytes: total, BytesPerSecond: speed));
                }
            }
            token.ThrowIfCancellationRequested();
            if (total >= 0 && read != total) throw new IOException("安装包下载不完整，请重试。");
            File.Move(temp, target, true);
            SetState(new(SharedDownloadStatus.Ready, release, Path.GetFileName(target), target, read, total));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            SetState(new(SharedDownloadStatus.Idle, release));
        }
        catch (Exception ex)
        {
            SetState(new(SharedDownloadStatus.Failed, release, Error: ex.Message));
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
            cancellation.Dispose();
        }
    }
}
