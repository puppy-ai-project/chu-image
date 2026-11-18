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
using D2Phap.FileWatcherEx;
using ImageGlass.Common.ServiceProviders.FileSearchService;
using ImageGlass.Common.Types;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace ImageGlass.Common.Photoing;


/// <summary>
/// Event args for <see cref="PhotoManager.FileWatcherChanged"/>.
/// </summary>
public class FileWatcherChangedEventArgs(
    ChangeType changeType,
    IReadOnlyList<string> filePaths,
    IReadOnlyList<string>? oldFilePaths = null,
    string? affectedCurrentFilePath = null) : EventArgs
{
    /// <summary>
    /// Gets the type of change that occurred.
    /// </summary>
    public ChangeType ChangeType => changeType;

    /// <summary>
    /// Gets the affected file paths (new paths for rename, current path otherwise).
    /// </summary>
    public IReadOnlyList<string> FilePaths => filePaths;

    /// <summary>
    /// Gets the old file paths (only for <see cref="ChangeType.RENAMED"/>).
    /// </summary>
    public IReadOnlyList<string>? OldFilePaths => oldFilePaths;

    /// <summary>
    /// Gets the affected current file path.
    /// </summary>
    public string AffectedCurrentFilePath => affectedCurrentFilePath ?? string.Empty;
}


public partial class PhotoManager
{
    // File system watcher
    private FileSystemWatcherEx _fileWatcher = new();

    // Pending delete queue processed by a background thread
    private readonly ConcurrentQueue<string> _deleteQueue = new();
    private Thread? _deleteWorkerThread;
    private volatile bool _deleteWorkerRunning;

    // Debounce key caches (must be stable delegate instances for BHelper.Debounce)
    private readonly Action _processAddedFilesAction;
    private readonly Action _processChangedFilesAction;

    // Pending add / change buffers
    private readonly ConcurrentQueue<string> _pendingAdds = new();
    private readonly ConcurrentQueue<string> _pendingChanges = new();


    /// <summary>
    /// Raised after the photo list has been modified by a file system event.
    /// </summary>
    public event TEventHandler<PhotoManager, FileWatcherChangedEventArgs>? FileWatcherChanged;


    /// <summary>
    /// Gets the path of the current directory being watched.
    /// </summary>
    public string FileWatcherFolderPath => _fileWatcher.FolderPath;




    /// <summary>
    /// Start watching a directory for changes.
    /// </summary>
    public void StartFileWatcher(string dirPath)
    {
        StopFileWatcher();

        if (string.IsNullOrWhiteSpace(dirPath)) return;

        _fileWatcher.FolderPath = dirPath;
        _fileWatcher.IncludeSubdirectories = Core.Config.EnableSubfoldersLoading;

        _fileWatcher.OnCreated += FileWatcher_OnCreated;
        _fileWatcher.OnDeleted += FileWatcher_OnDeleted;
        _fileWatcher.OnChanged += FileWatcher_OnChanged;
        _fileWatcher.OnRenamed += FileWatcher_OnRenamed;

        // start background thread for processing deletes
        _deleteWorkerRunning = true;
        _deleteWorkerThread = new Thread(ProcessDeleteQueue)
        {
            Priority = ThreadPriority.BelowNormal,
            IsBackground = true,
            Name = "PhotoManager.DeleteWorker",
        };
        _deleteWorkerThread.Start();

        try
        {
            _fileWatcher.Start();
        }
        catch (ArgumentException)
        {
            // SymlinkAwareFileWatcher.Init() may throw when the watched
            // directory tree contains symlinks that resolve to paths already
            // registered (duplicate key). Fall back to a non-recursive watcher
            // so the app can still start.
            _fileWatcher.Stop();
            _fileWatcher = new FileSystemWatcherEx
            {
                FolderPath = dirPath,
                IncludeSubdirectories = false,
            };

            _fileWatcher.OnCreated += FileWatcher_OnCreated;
            _fileWatcher.OnDeleted += FileWatcher_OnDeleted;
            _fileWatcher.OnChanged += FileWatcher_OnChanged;
            _fileWatcher.OnRenamed += FileWatcher_OnRenamed;

            _fileWatcher.Start();
        }
    }


    /// <summary>
    /// Stops file watcher and unsubscribes all event handlers.
    /// </summary>
    public void StopFileWatcher()
    {
        _fileWatcher.Stop();

        _fileWatcher.OnCreated -= FileWatcher_OnCreated;
        _fileWatcher.OnDeleted -= FileWatcher_OnDeleted;
        _fileWatcher.OnChanged -= FileWatcher_OnChanged;
        _fileWatcher.OnRenamed -= FileWatcher_OnRenamed;

        // stop delete worker
        _deleteWorkerRunning = false;

        _fileWatcher = new FileSystemWatcherEx();
    }


    /// <summary>
    /// Disposes file watcher and cleans up resources.
    /// </summary>
    private void DisposeFileWatcher()
    {
        StopFileWatcher();
        _fileWatcher.Dispose();
    }


    /// <summary>
    /// Checks if the file extension is in the supported file formats.
    /// </summary>
    private static bool IsSupportedFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;

        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext)) return false;

        // a save in progress writes a sibling temp file that keeps the real extension
        if (SaveStaging.IsStagingFile(filePath)) return false;

        return Core.GetSupportedFileExtensions().Contains(ext);
    }


    /// <summary>
    /// Checks whether the file, or any sub-folder holding it, is excluded from browsing.
    /// Keeps the watcher from re-adding what the file search deliberately skipped.
    /// </summary>
    private bool IsExcludedFile(string? filePath)
    {
        return BHelper.IsPathSkippedUnderRoot(filePath, FileWatcherFolderPath,
            Core.Config.EnableHiddenImagesLoading);
    }



    #region File watcher event handlers

    private void FileWatcher_OnCreated(object? sender, FileChangedEvent e)
    {
        if (IsDisposed) return;
        if (!IsSupportedFile(e.FullPath)) return;
        if (IsExcludedFile(e.FullPath)) return;

        // macOS FSEvents 合并标志位在文件创建后紧接着重命名/删除时，可能仍上报 Created 事件；
        // 若路径已在列表中但磁盘上已不存在，则转入删除队列避免残留幽灵项
        if (!File.Exists(e.FullPath))
        {
            if (IndexOf(e.FullPath) >= 0)
            {
                _deleteQueue.Enqueue(e.FullPath);
            }
            return;
        }

        if (IndexOf(e.FullPath) >= 0) return;

        _pendingAdds.Enqueue(e.FullPath);
        BHelper.Debounce(300, _processAddedFilesAction);
    }


    private void FileWatcher_OnDeleted(object? sender, FileChangedEvent e)
    {
        if (IsDisposed) return;
        if (!IsSupportedFile(e.FullPath)) return;

        _deleteQueue.Enqueue(e.FullPath);
    }


    private void FileWatcher_OnChanged(object? sender, FileChangedEvent e)
    {
        if (IsDisposed) return;
        if (!IsSupportedFile(e.FullPath)) return;

        // if the file is not in our list, treat it as a new file
        if (IndexOf(e.FullPath) < 0)
        {
            if (IsExcludedFile(e.FullPath)) return;
            if (!File.Exists(e.FullPath)) return;

            _pendingAdds.Enqueue(e.FullPath);
            BHelper.Debounce(300, _processAddedFilesAction);
            return;
        }

        // macOS FSEvents 在连续快速重命名/删除合并批次时，可能对已移走的旧路径上报 Changed 事件；
        // 若该路径已在列表中但磁盘上已不存在，则转入删除队列避免残留幽灵项
        if (!File.Exists(e.FullPath))
        {
            _deleteQueue.Enqueue(e.FullPath);
            return;
        }

        _pendingChanges.Enqueue(e.FullPath);
        BHelper.Debounce(300, _processChangedFilesAction);
    }


    private void FileWatcher_OnRenamed(object? sender, FileChangedEvent e)
    {
        if (IsDisposed) return;

        var newFilePath = e.FullPath ?? "";
        var oldFilePath = e.OldFullPath ?? "";

        // a finished save renames its staging file onto the target: that is a content change,
        // not a new file, so routing it here avoids a duplicate entry for a photo already listed
        if (SaveStaging.IsStagingFile(oldFilePath))
        {
            FileWatcher_OnChanged(sender, e);
            return;
        }

        var oldSupported = IsSupportedFile(oldFilePath);
        var newSupported = IsSupportedFile(newFilePath);

        // neither old nor new is a supported file type
        if (!oldSupported && !newSupported) return;

        // old was supported, new is not -> treat as delete
        if (oldSupported && !newSupported)
        {
            _deleteQueue.Enqueue(oldFilePath);
            return;
        }

        // renamed into a hidden folder (or made hidden) -> drop it from the list
        var newExcluded = IsExcludedFile(newFilePath);

        // old was not supported, new is -> treat as add
        if (!oldSupported && newSupported)
        {
            if (newExcluded) return;
            if (IndexOf(newFilePath) >= 0) return;

            _pendingAdds.Enqueue(newFilePath);
            BHelper.Debounce(300, _processAddedFilesAction);
            return;
        }

        // 若应用内重命名（如 IG_RenameAsync）已原地将 oldFilePath 更新为 newFilePath，则无需再入队
        if (IndexOf(oldFilePath) < 0 && IndexOf(newFilePath) >= 0)
        {
            return;
        }

        // 封送到 UI 线程原地更新路径，避免后台线程修改 Photo / AvaloniaList 引发跨线程异常
        Dispatcher.UIThread.Post(() => ApplyFileRenamedOnUI(oldFilePath, newFilePath, newExcluded));
    }


    private void ApplyFileRenamedOnUI(string oldFilePath, string newFilePath, bool newExcluded)
    {
        if (IsDisposed) return;

        var imgIndex = IndexOf(oldFilePath);
        if (imgIndex >= 0)
        {
            if (newExcluded)
            {
                _deleteQueue.Enqueue(oldFilePath);
                return;
            }

            // 仅大小写变更且已在应用内同步完成时，跳过重复更新
            if (string.Equals(GetFilePath(imgIndex), newFilePath, StringComparison.Ordinal))
            {
                return;
            }

            // 连续重命名场景下，旧事件的新路径可能已在磁盘上再次被改名，避免覆盖为失效路径；
            // 若旧路径也已从磁盘消失，则将其移入删除队列，防止列表残留不存在的幽灵项
            if (!File.Exists(newFilePath))
            {
                if (!File.Exists(oldFilePath))
                {
                    _deleteQueue.Enqueue(oldFilePath);
                }
                return;
            }

            SetFilePath(imgIndex, newFilePath);

            FileWatcherChanged?.Invoke(this, new FileWatcherChangedEventArgs(
                ChangeType.RENAMED,
                [newFilePath],
                [oldFilePath]));
        }
        else if (!newExcluded && IndexOf(newFilePath) < 0 && File.Exists(newFilePath))
        {
            // file not in our list yet -> add it
            _pendingAdds.Enqueue(newFilePath);
            BHelper.Debounce(300, _processAddedFilesAction);
        }
    }

    #endregion // File watcher event handlers



    #region Batch processors

    /// <summary>
    /// Drains the pending-add queue, inserts all new files into the list
    /// at the correct sorted position, and raises a single event.
    /// </summary>
    private void ProcessPendingAdds()
    {
        try
        {
            if (IsDisposed) return;

            // 出队去重，同时过滤掉连续重命名过程中已经不存在的中间文件名
            var newFilesSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (_pendingAdds.TryDequeue(out var path))
            {
                if (IndexOf(path) < 0 && File.Exists(path))
                {
                    newFilesSet.Add(path);
                }
            }

            if (newFilesSet.Count == 0) return;
            var newFiles = new List<string>(newFilesSet);

            // determine sorted insertion index for each file
            var options = new FileSearchOptions()
            {
                AllowedExtensions = Core.GetSupportedFileExtensions(),
                UseExplorerSortOrder = Core.Config.EnableExplorerSortOrder,
                SearchSubDirectories = Core.Config.EnableSubfoldersLoading,
                GroupByDir = Core.Config.EnableImageFolderGrouping,
                IncludeHidden = Core.Config.EnableHiddenImagesLoading,
                OrderBy = Core.Config.ImageLoadingOrder,
                OrderType = Core.Config.ImageLoadingOrderType,
                ForegroundShell = Core.ShellProvider?.ForegroundShell,
            };


            List<string> currentPaths;
            lock (_lock)
            {
                // pre-allocate and copy paths without LINQ
                currentPaths = new List<string>(Items.Count);
                for (var i = 0; i < Items.Count; i++)
                {
                    currentPaths.Add(Items[i].FilePath);
                }
            }

            // macOS FSEvents 在高频连续重命名/移动时可能漏报旧路径的 Deleted/Renamed 事件，仅上报新路径的 Created；
            // Windows/Linux 在高频批量脚本场景下也可能溢出缓冲区。借本次 300ms Debounce 后台排序时机，
            // 对新增文件所在目录做轻量级 readdir 对账，并用 !File.Exists 二次确认（兼容 macOS NFD/NFC 中文名与符号链接）。
            // 为避免 Windows 超大目录或慢速 SMB/UNC 网络共享盘耗时，非 macOS 下仅对 <= 2000 项的本地目录执行。
            if (OperatingSystem.IsMacOS() || currentPaths.Count <= 2000)
            {
                var affectedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in newFiles)
                {
                    var dir = Path.GetDirectoryName(f);
                    if (string.IsNullOrEmpty(dir)) continue;
                    if (OperatingSystem.IsWindows() && dir.StartsWith(@"\\", StringComparison.Ordinal)) continue;
                    affectedDirs.Add(dir);
                }

                var diskFilesByDir = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var dir in affectedDirs)
                {
                    try
                    {
                        if (Directory.Exists(dir))
                        {
                            diskFilesByDir[dir] = new HashSet<string>(
                                Directory.EnumerateFiles(dir),
                                StringComparer.OrdinalIgnoreCase);
                        }
                    }
                    catch { }
                }

                if (diskFilesByDir.Count > 0)
                {
                    for (var i = currentPaths.Count - 1; i >= 0; i--)
                    {
                        var p = currentPaths[i];
                        var dir = Path.GetDirectoryName(p);
                        if (string.IsNullOrEmpty(dir)) continue;

                        if (diskFilesByDir.TryGetValue(dir, out var diskFiles)
                            && !diskFiles.Contains(p)
                            && !File.Exists(p))
                        {
                            _deleteQueue.Enqueue(p);
                            currentPaths.RemoveAt(i);
                        }
                    }
                }
            }

            // 在后台线程完成排序，避免阻塞 UI
            var combinedList = new List<string>(currentPaths.Count + newFiles.Count);
            combinedList.AddRange(currentPaths);
            combinedList.AddRange(newFiles);

            var sortedList = FileSearchProvider.SortFiles(combinedList, options).ToList();

            // AvaloniaList<Photo> Items 直接绑定到 Gallery UI，必须在 UI 线程执行增删，
            // 否则 CollectionChanged 会触发跨线程异常并导致 TaskScheduler.UnobservedTaskException
            Dispatcher.UIThread.Post(() => ApplyPendingAddsOnUI(newFiles, sortedList));
        }
        catch (Exception ex)
        {
            App.LogUnhandledException(ex, nameof(ProcessPendingAdds));
        }
    }


    private void ApplyPendingAddsOnUI(List<string> candidateFiles, List<string> sortedList)
    {
        if (IsDisposed) return;

        string currentFilePath;
        int oldCurrentIndex;
        lock (_lock)
        {
            oldCurrentIndex = CurrentIndex;
            currentFilePath = CurrentFilePath;
        }

        var addedFiles = new List<string>(candidateFiles.Count);

        // insert each new file at its sorted position
        foreach (var filePath in candidateFiles)
        {
            // UI 线程内再次校验，防止排队期间已被添加或因连续重命名而消失
            if (IndexOf(filePath) >= 0 || !File.Exists(filePath)) continue;

            var insertIndex = sortedList.IndexOf(filePath);

            // clamp to valid range (list may have shifted from prior inserts)
            lock (_lock)
            {
                if (insertIndex < 0 || insertIndex > (int)Count)
                {
                    insertIndex = (int)Count;
                }
            }

            Add(filePath, insertIndex);
            addedFiles.Add(filePath);
        }

        if (addedFiles.Count == 0) return;

        // insertions shifted indexes, so clear cache index tracking;
        // the bitmap data in Photo objects is still valid and will be
        // re-discovered by the next caching pass
        lock (_cacheLock)
        {
            _cachedIndexes.Clear();
        }

        // restore the original index
        if (!string.IsNullOrEmpty(currentFilePath))
        {
            var restoredIndex = IndexOf(currentFilePath);
            if (restoredIndex >= 0)
            {
                _currentIndex = restoredIndex;
            }
        }
        var affectedCurrentFilePath = CurrentIndex != oldCurrentIndex ? currentFilePath : null;

        FileWatcherChanged?.Invoke(this, new FileWatcherChangedEventArgs(
            ChangeType.CREATED,
            addedFiles,
            null,
            affectedCurrentFilePath));
    }


    /// <summary>
    /// Drains the pending-change queue and raises a single event for all changed files.
    /// The actual reload of image data is left to the subscriber.
    /// </summary>
    private void ProcessPendingChanges()
    {
        try
        {
            if (IsDisposed) return;

            var changedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (_pendingChanges.TryDequeue(out var path))
            {
                if (IndexOf(path) < 0) continue;

                // 若该路径已在磁盘上消失（如 macOS FSEvents 合并重命名/删除事件），转入删除队列
                if (!File.Exists(path))
                {
                    _deleteQueue.Enqueue(path);
                    continue;
                }

                changedSet.Add(path);
            }

            if (changedSet.Count == 0) return;
            var changedList = new List<string>(changedSet);

            // invalidate cached data for changed files so stale bitmaps are not reused
            foreach (var path in changedList)
            {
                InvalidateCacheAt(path);
                ThumbnailDiskCache.Invalidate(path);
            }

            string currentFilePath;
            lock (_lock)
            {
                currentFilePath = CurrentFilePath;
            }

            // check if the currently viewed photo was changed
            var currentWasChanged = changedList.Contains(currentFilePath, StringComparer.OrdinalIgnoreCase);
            var affectedCurrentFilePath = currentWasChanged ? currentFilePath : null;

            FileWatcherChanged?.Invoke(this, new FileWatcherChangedEventArgs(
                ChangeType.CHANGED,
                changedList,
                null,
                affectedCurrentFilePath));
        }
        catch (Exception ex)
        {
            App.LogUnhandledException(ex, nameof(ProcessPendingChanges));
        }
    }


    /// <summary>
    /// Background thread loop that processes queued file deletions.
    /// Batches multiple rapid deletions together before raising the event.
    /// </summary>
    private void ProcessDeleteQueue()
    {
        while (_deleteWorkerRunning)
        {
            try
            {
                if (_deleteQueue.IsEmpty)
                {
                    Thread.Sleep(200);
                    continue;
                }

                // small delay to collect more items that may arrive in a burst
                Thread.Sleep(100);

                var deletedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (_deleteQueue.TryDequeue(out var filePath))
                {
                    // 文件仍在磁盘上存在（例如仅大小写重命名中转或覆盖保存）时，不应视为已删除
                    if (!File.Exists(filePath))
                    {
                        deletedSet.Add(filePath);
                    }
                }
                var deletedList = new List<string>(deletedSet);

                if (deletedList.Count == 0 || !_deleteWorkerRunning || IsDisposed) continue;

                // 投递到 UI 线程执行 Remove，避免在后台线程修改绑定到 Gallery 的 AvaloniaList
                Dispatcher.UIThread.Post(() => ApplyPendingDeletesOnUI(deletedList));
            }
            catch (Exception ex)
            {
                App.LogUnhandledException(ex, nameof(ProcessDeleteQueue));
            }
        }
    }


    internal void RemoveMissingFileOnUI(string filePath)
        => ApplyPendingDeletesOnUI([filePath]);

    private void ApplyPendingDeletesOnUI(List<string> candidateDeleted)
    {
        if (IsDisposed) return;

        // UI 线程内再次过滤：确保文件确实在列表中且已从磁盘消失
        var deletedList = new List<string>(candidateDeleted.Count);
        var deletedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var filePath in candidateDeleted)
        {
            if (IndexOf(filePath) >= 0 && !File.Exists(filePath) && deletedSet.Add(filePath))
            {
                deletedList.Add(filePath);
            }
        }

        if (deletedList.Count == 0) return;

        string currentFilePath;
        int currentIndex;
        lock (_lock)
        {
            currentIndex = CurrentIndex;
            currentFilePath = CurrentFilePath;
        }

        // check if the currently viewed photo was deleted
        var currentWasDeleted = deletedSet.Contains(currentFilePath);
        var affectedCurrentFilePath = currentWasDeleted ? currentFilePath : null;

        // deletions ahead of the current photo shift the slot its successor lands in
        var deletedBeforeCurrent = 0;
        if (currentWasDeleted)
        {
            foreach (var filePath in deletedList)
            {
                var index = IndexOf(filePath);
                if (index >= 0 && index < currentIndex) deletedBeforeCurrent++;
            }
        }

        // remove from list
        var removedAny = false;
        foreach (var filePath in deletedList)
        {
            if (IndexOf(filePath) < 0) continue;

            Remove(filePath);
            removedAny = true;
        }

        if (removedAny)
        {
            // Remove() never touches the selection, so re-anchor it: the surviving current photo's
            // new index, else the slot its successor shifted into (Count when it had none)
            lock (_lock)
            {
                _currentIndex = currentWasDeleted
                    ? Math.Min(currentIndex - deletedBeforeCurrent, (int)Count)
                    : IndexOf(currentFilePath);
            }

            // removals shifted indexes, so cache index tracking no longer maps to the right photos
            lock (_cacheLock)
            {
                _cachedIndexes.Clear();
            }
        }

        FileWatcherChanged?.Invoke(this, new FileWatcherChangedEventArgs(
            ChangeType.DELETED,
            deletedList,
            null,
            affectedCurrentFilePath));
    }

    #endregion // Batch processors


}
