using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UCXSyncTool.Models;

namespace UCXSyncTool.Services
{
    /// <summary>
    /// Service for synchronizing files from multiple sources to a destination without external tools.
    /// </summary>
    public class FileSyncService
    {
        private readonly string[] _nodes = Configuration.Nodes;
        private readonly string[] _shares = Configuration.Shares;
        
        private CancellationTokenSource? _cts;
        private readonly object _lock = new();
        private readonly ConcurrentDictionary<string, SyncTaskInfo> _activeTasks = new();
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, int>> _captureTracker = new();
        private Action<string>? _logger;
        
        // Capture statistics
        private int _completedCaptures = 0;
        private int _completedTestCaptures = 0;
        private string? _lastCaptureNumber = null;
        private string? _lastTestCaptureNumber = null;

        private class SyncTaskInfo
        {
            public string Node { get; set; } = string.Empty;
            public string Share { get; set; } = string.Empty;
            public Task? SyncTask { get; set; }
            public DateTime LastActivity { get; set; }
            public long TotalBytes;
            public long CopiedBytes;
            public int TotalFiles;
            public int CopiedFiles;
            public int FailedFiles;
            public CancellationTokenSource? TaskCts { get; set; }
        }

        /// <summary>
        /// Start synchronization for a project.
        /// </summary>
        public void Start(string project, string destRoot, Action<string>? logger = null, int maxParallelism = 8)
        {
            lock (_lock)
            {
                if (_cts != null) return; // Already running
                
                _cts = new CancellationTokenSource();
                _logger = logger;
                var token = _cts.Token;

                // Start main coordination loop
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await RunSyncLoop(project, destRoot, maxParallelism, token);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger?.Invoke("Synchronization cancelled");
                    }
                    catch (Exception ex)
                    {
                        _logger?.Invoke($"Synchronization error: {ex.Message}");
                    }
                });
            }
        }

        /// <summary>
        /// Stop all synchronization tasks.
        /// </summary>
        public void Stop()
        {
            lock (_lock)
            {
                if (_cts == null) return;

                try
                {
                    _cts.Cancel();
                }
                catch { }

                // Cancel all individual task tokens
                foreach (var task in _activeTasks.Values)
                {
                    try
                    {
                        task.TaskCts?.Cancel();
                    }
                    catch { }
                }

                // Wait for all tasks to complete
                var allTasks = _activeTasks.Values.Where(t => t.SyncTask != null).Select(t => t.SyncTask!).ToArray();
                try
                {
                    Task.WaitAll(allTasks, TimeSpan.FromSeconds(5));
                }
                catch { }

                _activeTasks.Clear();

                try
                {
                    _cts.Dispose();
                }
                catch { }

                _cts = null;
            }
        }

        /// <summary>
        /// Get the number of completed captures in current session.
        /// </summary>
        public int GetCompletedCapturesCount()
        {
            return _completedCaptures;
        }

        /// <summary>
        /// Get the number of completed test captures in current session.
        /// </summary>
        public int GetCompletedTestCapturesCount()
        {
            return _completedTestCaptures;
        }

        /// <summary>
        /// Get the last completed capture number.
        /// </summary>
        public string? GetLastCaptureNumber()
        {
            return _lastCaptureNumber;
        }

        /// <summary>
        /// Get the last completed test capture number.
        /// </summary>
        public string? GetLastTestCaptureNumber()
        {
            return _lastTestCaptureNumber;
        }

        /// <summary>
        /// Get current synchronization status for all active tasks.
        /// </summary>
        public List<ActiveCopyViewModel> GetActiveCopies()
        {
            lock (_lock)
            {
                return _activeTasks.Select(kv => new ActiveCopyViewModel
                {
                    Node = kv.Value.Node,
                    Share = Configuration.GetShareAlias(kv.Value.Share),
                    Status = kv.Value.SyncTask?.Status.ToString() ?? "Unknown",
                    LastChange = kv.Value.LastActivity,
                    FilesDownloaded = kv.Value.CopiedFiles,
                    ProgressPercent = kv.Value.TotalBytes > 0 
                        ? (double)(kv.Value.CopiedBytes * 100.0 / kv.Value.TotalBytes) 
                        : null
                }).ToList();
            }
        }

        /// <summary>
        /// Scan all nodes and shares for available projects.
        /// </summary>
        public List<string> FindAvailableProjects(Action<string>? logger = null)
        {
            var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var node in _nodes)
            {
                foreach (var share in _shares)
                {
                    var root = $"\\\\{node}\\{share}";
                    try
                    {
                        if (!Directory.Exists(root))
                        {
                            logger?.Invoke($"Root not found: {root}");
                            continue;
                        }

                        var dirs = Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly);
                        foreach (var dir in dirs)
                        {
                            try
                            {
                                var name = Path.GetFileName(dir);
                                if (!string.IsNullOrEmpty(name) && IsValidProjectName(name))
                                {
                                    projects.Add(name);
                                }
                            }
                            catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.Invoke($"Error scanning {root}: {ex.Message}");
                    }
                }
            }

            var result = projects.ToList();
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        private async Task RunSyncLoop(string project, string destRoot, int maxParallelism, CancellationToken token)
        {
            var destDir = Path.Combine(destRoot, project);
            Directory.CreateDirectory(destDir);

            while (!token.IsCancellationRequested)
            {
                foreach (var node in _nodes)
                {
                    if (token.IsCancellationRequested) break;

                    foreach (var share in _shares)
                    {
                        if (token.IsCancellationRequested) break;

                        var src = $"\\\\{node}\\{share}\\{project}";
                        var key = $"{node}-{share}";

                        // Check if task already exists
                        SyncTaskInfo? taskInfo = null;
                        int previouslyCopiedFiles = 0;
                        
                        if (_activeTasks.TryGetValue(key, out var existingTask))
                        {
                            // Save the file counter before removing
                            previouslyCopiedFiles = existingTask.CopiedFiles;
                            
                            // Check if task is still running
                            if (existingTask.SyncTask != null && 
                                !existingTask.SyncTask.IsCompleted &&
                                !existingTask.SyncTask.IsCanceled &&
                                !existingTask.SyncTask.IsFaulted)
                            {
                                continue; // Task still active
                            }

                            // Task completed, will be replaced with new one
                            _activeTasks.TryRemove(key, out _);
                        }

                        // Check if source exists
                        if (!Directory.Exists(src))
                        {
                            continue;
                        }

                        // Check available disk space
                        try
                        {
                            var driveInfo = new DriveInfo(Path.GetPathRoot(destDir) ?? "C:\\");
                            if (driveInfo.AvailableFreeSpace < Configuration.MinimumFreeDiskSpace + Configuration.DiskSpaceSafetyMargin)
                            {
                                _logger?.Invoke($"[{node}][{share}] Insufficient disk space, skipping");
                                continue;
                            }
                        }
                        catch { }

                        // Create new sync task, preserving cumulative file counter
                        var taskCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                        taskInfo = new SyncTaskInfo
                        {
                            Node = node,
                            Share = share,
                            LastActivity = DateTime.Now,
                            TaskCts = taskCts,
                            CopiedFiles = previouslyCopiedFiles // Preserve counter across sync sessions
                        };

                        var syncTask = Task.Run(async () =>
                        {
                            try
                            {
                                await SyncDirectory(src, destDir, taskInfo, maxParallelism, taskCts.Token);
                            }
                            catch (OperationCanceledException)
                            {
                                // Cancelled - no message needed
                            }
                            catch (Exception ex)
                            {
                                _logger?.Invoke($"[{node}][{share}] Sync error: {ex.Message}");
                            }
                        }, taskCts.Token);

                        taskInfo.SyncTask = syncTask;
                        _activeTasks[key] = taskInfo;
                    }
                }

                // Wait before next iteration
                await Task.Delay(TimeSpan.FromSeconds(Configuration.ServiceLoopIntervalSeconds), token);
            }
        }

        private async Task SyncDirectory(string sourceDir, string destDir, SyncTaskInfo taskInfo, int maxParallelism, CancellationToken token)
        {
            // Scan source directory for files
            var filesToSync = new List<FileInfo>();
            
            try
            {
                await Task.Run(() =>
                {
                    ScanDirectory(sourceDir, sourceDir, destDir, filesToSync, token);
                }, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            taskInfo.TotalFiles = filesToSync.Count;
            taskInfo.TotalBytes = filesToSync.Sum(f => f.Length);

            // Process files in parallel
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = maxParallelism,
                CancellationToken = token
            };

            try
            {
                await Parallel.ForEachAsync(filesToSync, options, async (fileInfo, ct) =>
                {
                    await CopyFileWithRetry(fileInfo, sourceDir, destDir, taskInfo, ct);
                });
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        private void ScanDirectory(string rootSource, string currentSource, string rootDest, List<FileInfo> files, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            try
            {
                // Get all files in current directory
                foreach (var filePath in Directory.EnumerateFiles(currentSource))
                {
                    token.ThrowIfCancellationRequested();

                    try
                    {
                        var fileInfo = new FileInfo(filePath);
                        
                        // Only add files that need to be copied
                        if (ShouldCopyFile(fileInfo, rootSource, rootDest))
                        {
                            files.Add(fileInfo);
                        }
                    }
                    catch { }
                }

                // Recurse into subdirectories
                foreach (var dirPath in Directory.EnumerateDirectories(currentSource))
                {
                    token.ThrowIfCancellationRequested();

                    var dirName = Path.GetFileName(dirPath);
                    
                    // Skip system directories
                    if (IsExcludedDirectory(dirName))
                    {
                        continue;
                    }

                    ScanDirectory(rootSource, dirPath, rootDest, files, token);
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Skip directories we can't access
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch { }
        }

        private bool ShouldCopyFile(FileInfo sourceFile, string sourceRoot, string destRoot)
        {
            try
            {
                var relativePath = Path.GetRelativePath(sourceRoot, sourceFile.FullName);
                var destPath = Path.Combine(destRoot, relativePath);
                var destFileInfo = new FileInfo(destPath);

                // Copy if destination doesn't exist
                if (!destFileInfo.Exists)
                {
                    return true;
                }

                // Copy if size differs or source is newer (with 2 second tolerance for FAT32)
                if (destFileInfo.Length != sourceFile.Length ||
                    destFileInfo.LastWriteTimeUtc < sourceFile.LastWriteTimeUtc.AddSeconds(-2))
                {
                    return true;
                }

                return false;
            }
            catch
            {
                // If we can't determine, assume we should copy
                return true;
            }
        }

        private async Task CopyFileWithRetry(FileInfo sourceFile, string sourceRoot, string destRoot, SyncTaskInfo taskInfo, CancellationToken token)
        {
            const int maxRetries = 3;
            const int retryDelayMs = 1000;

            // Calculate relative path and destination
            var relativePath = Path.GetRelativePath(sourceRoot, sourceFile.FullName);
            var destPath = Path.Combine(destRoot, relativePath);
            var destFileInfo = new FileInfo(destPath);

            // Ensure destination directory exists
            Directory.CreateDirectory(destFileInfo.DirectoryName ?? destRoot);

            // Copy file with retries
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    token.ThrowIfCancellationRequested();

                    // Copy file
                    await Task.Run(() =>
                    {
                        File.Copy(sourceFile.FullName, destPath, overwrite: true);
                    }, token);

                    // Preserve timestamps
                    File.SetCreationTime(destPath, sourceFile.CreationTime);
                    File.SetLastWriteTime(destPath, sourceFile.LastWriteTime);

                    // Update statistics
                    Interlocked.Increment(ref taskInfo.CopiedFiles);
                    Interlocked.Add(ref taskInfo.CopiedBytes, sourceFile.Length);
                    taskInfo.LastActivity = DateTime.Now;

                    // Track capture completion
                    TrackCaptureCompletion(sourceFile.Name, taskInfo.Node);

                    return; // Success
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (attempt == maxRetries - 1)
                    {
                        // Final attempt failed
                        Interlocked.Increment(ref taskInfo.FailedFiles);
                        _logger?.Invoke($"Failed to copy {relativePath}: {ex.Message}");
                        return;
                    }

                    // Wait before retry
                    await Task.Delay(retryDelayMs * (attempt + 1), token);
                }
            }
        }

        private static bool IsExcludedDirectory(string dirName)
        {
            var excluded = new[]
            {
                "System Volume Information",
                "RECYCLER",
                "RECYCLED",
                "$RECYCLE.BIN",
                ".git",
                ".svn",
                "node_modules"
            };

            return excluded.Any(e => dirName.Equals(e, StringComparison.OrdinalIgnoreCase));
        }

        private class CaptureInfo
        {
            public string? DataType { get; set; }
            public string? CaptureNumber { get; set; }
            public string? ProjectName { get; set; }
            public string? SessionId { get; set; }
            public bool IsTest { get; set; }
        }

        private CaptureInfo? ParseCaptureFileName(string fileName)
        {
            // Pattern: Lvl0X-00001-T-Test1-00-00-AA66B5AD_9209_4D88_A41B_DFFD3CD97D40.raw
            // Group 1: Lvl0X (data type)
            // Group 2: 00001 (capture number)
            // Group 3: T or empty (test marker)
            // Group 4: Test1 (project name)
            // Group 5: AA66B5AD_9209_4D88_A41B_DFFD3CD97D40 (session ID)
            
            var match = Regex.Match(fileName, @"^(Lvl\d+X)-(\d+)-(T-)?([^-]+)-\d+-\d+-([A-F0-9_]+)\.raw$", RegexOptions.IgnoreCase);
            
            if (match.Success)
            {
                return new CaptureInfo
                {
                    DataType = match.Groups[1].Value,
                    CaptureNumber = match.Groups[2].Value,
                    IsTest = !string.IsNullOrEmpty(match.Groups[3].Value),
                    ProjectName = match.Groups[4].Value,
                    SessionId = match.Groups[5].Value
                };
            }

            return null;
        }

        private void TrackCaptureCompletion(string fileName, string node)
        {
            var info = ParseCaptureFileName(fileName);
            if (info == null || string.IsNullOrEmpty(info.CaptureNumber))
            {
                return;
            }

            // Key: capture number
            var captureKey = info.CaptureNumber;
            
            // Get or create tracker for this capture
            var nodeTracker = _captureTracker.GetOrAdd(captureKey, _ => new ConcurrentDictionary<string, int>());
            
            // Mark this node as having completed this capture
            nodeTracker[node] = 1;
            
            // Check if all 13 nodes have completed this capture
            if (nodeTracker.Count == 13)
            {
                // Update counters based on capture type
                if (info.IsTest)
                {
                    Interlocked.Increment(ref _completedTestCaptures);
                    _lastTestCaptureNumber = info.CaptureNumber;
                    _logger?.Invoke($"✓ ТЕСТ снимок #{info.CaptureNumber} проекта '{info.ProjectName}' скачан полностью (13/13 файлов) [Тестовых: {_completedTestCaptures}]");
                }
                else
                {
                    Interlocked.Increment(ref _completedCaptures);
                    _lastCaptureNumber = info.CaptureNumber;
                    _logger?.Invoke($"✓ Снимок #{info.CaptureNumber} проекта '{info.ProjectName}' скачан полностью (13/13 файлов) [Всего: {_completedCaptures}]");
                }
                
                // Remove from tracker to free memory
                _captureTracker.TryRemove(captureKey, out _);
            }
        }

        private static bool IsValidProjectName(string name)
        {
            var excludedFolders = new[]
            {
                "system volume information", "recycler", "recycled", "$recycle.bin",
                "logs", "log", "temp", "tmp", "windows", "program files",
                "program files (x86)", "programdata", "users"
            };

            var lowercaseName = name.ToLowerInvariant();
            
            if (excludedFolders.Any(e => lowercaseName == e || lowercaseName.StartsWith(e + " ")))
            {
                return false;
            }

            if (name.StartsWith("$") || name.StartsWith(".") || name.Length <= 1)
            {
                return false;
            }

            return true;
        }

        private static string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }

            return $"{len:0.##} {sizes[order]}";
        }
    }
}
