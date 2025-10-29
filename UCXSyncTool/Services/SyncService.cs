using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Linq;
using System.Threading;
using System.Collections.Concurrent;
using UCXSyncTool.Models;

namespace UCXSyncTool.Services
{
    public class SyncService
    {
        private readonly string[] Nodes = new[] { "WU01","WU02","WU03","WU04","WU05","WU06","WU07","WU08","WU09","WU10","WU11","WU12","WU13","CU" };
        private readonly string[] Shares = new[] { "E$", "F$" };

        private CancellationTokenSource? _cts;
        private readonly object _lock = new();

        // Map share names to friendly aliases
        private static string GetShareAlias(string? share)
        {
            if (string.IsNullOrEmpty(share)) return string.Empty;
            return share.ToUpperInvariant() switch
            {
                "E$" => "Fast",
                "F$" => "Normal",
                _ => share
            };
        }

        // key -> info
        private class ActiveInfo
        {
            public Process? proc;
            public DateTime lastChange;
            public string? node;
            public string? share;
            public string? logPath;
            // tracking sizes for speed/progress
            public long totalBytes = 0;
            public long lastDestBytes = 0;
            public DateTime lastSampleTime = DateTime.MinValue;
            // last time we scanned the destination directory (to avoid frequent expensive scans)
            public DateTime lastDirScanTime = DateTime.MinValue;
            // count of files downloaded in this session
            public int filesDownloaded = 0;
        }
    private readonly ConcurrentDictionary<string, ActiveInfo> _active = new();

        private Action<string>? _logger;

        public void Start(string project, string destRoot, Action<string>? logger = null, int robocopyThreads = 8)
        {
            lock (_lock)
            {
                if (_cts != null) return; // already running
                _cts = new CancellationTokenSource();
                _logger = logger;
                var token = _cts.Token;
                // run the main loop on the threadpool as an async Task so exceptions and async/await are handled correctly
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        await RunLoop(project, destRoot, token, robocopyThreads);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        _logger?.Invoke("Ошибка сервиса: " + ex.ToString());
                    }
                });
            }
        }

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
                _cts = null;

                // kill processes
                foreach (var kv in _active.Values.ToList())
                {
                    try { if (kv.proc != null && !kv.proc.HasExited) kv.proc.Kill(true); } catch { }
                }
                _active.Clear();
            }
        }

        public List<ActiveCopyViewModel> GetActiveCopies()
        {
            lock (_lock)
            {
                return _active.Select(kv => new ActiveCopyViewModel
                {
                    Node = kv.Value.node ?? string.Empty,
                    Share = GetShareAlias(kv.Value.share),
                    Status = (kv.Value.proc == null || kv.Value.proc.HasExited) ? "Exited" : "Running",
                    LastChange = kv.Value.lastChange,
                    LogPath = kv.Value.logPath ?? string.Empty,
                    FilesDownloaded = kv.Value.filesDownloaded,
                    ProgressPercent = (kv.Value.totalBytes > 0) ? (double?)((kv.Value.lastDestBytes * 100.0) / kv.Value.totalBytes) : null
                }).ToList();
            }
        }

        /// <summary>
        /// Scan all nodes and shares and return distinct project folder names found directly under each share root.
        /// </summary>
        public List<string> FindAvailableProjects(Action<string>? logger = null)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in Nodes)
            {
                foreach (var share in Shares)
                {
                    var root = $"\\\\{node}\\{share}";
                    try
                    {
                        if (!Directory.Exists(root))
                        {
                            logger?.Invoke($"Не найден корень: {root}");
                            continue;
                        }

                        var dirs = Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly);
                        foreach (var d in dirs)
                        {
                            try
                            {
                                var name = Path.GetFileName(d);
                                if (!string.IsNullOrEmpty(name) && IsValidProjectName(name))
                                {
                                    set.Add(name);
                                }
                            }
                            catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.Invoke($"Ошибка при сканировании {root}: {ex.Message}");
                    }
                }
            }

            var list = set.ToList();
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        /// <summary>
        /// Check if a folder name is a valid project name (exclude system folders and logs)
        /// </summary>
        private static bool IsValidProjectName(string name)
        {
            var lowercaseName = name.ToLowerInvariant();
            
            // Exclude system folders
            var excludedFolders = new[]
            {
                "system volume information",
                "recycler",
                "recycled",
                "$recycle.bin",
                "logs",
                "log",
                "temp",
                "tmp",
                "windows",
                "program files",
                "program files (x86)",
                "programdata",
                "users",
                "documents and settings",
                "config.msi",
                "msocache",
                "recovery",
                "boot",
                "efi",
                "perflogs"
            };

            // Check if name matches any excluded folder
            foreach (var excluded in excludedFolders)
            {
                if (lowercaseName == excluded || lowercaseName.StartsWith(excluded + " "))
                {
                    return false;
                }
            }

            // Exclude folders starting with $ or containing only special characters
            if (name.StartsWith("$") || name.StartsWith("."))
            {
                return false;
            }

            // Exclude very short names (likely system folders)
            if (name.Length <= 1)
            {
                return false;
            }

            return true;
        }

        private async System.Threading.Tasks.Task RunLoop(string project, string destRoot, CancellationToken token, int robocopyThreads)
        {
            var logRoot = Path.Combine(destRoot, "Logs");
            Directory.CreateDirectory(logRoot);

            // Ensure cmdkey credentials exist for all nodes (adds Administrator/ultracam if missing)
            try
            {
                // call once and pass listing to avoid calling cmdkey /list repeatedly
                var cmdkeyList = RunProcessCaptureOutput("cmdkey.exe", "/list", TimeSpan.FromSeconds(5));
                EnsureCmdKeyCredentialsForAllNodes(cmdkeyList);
            }
            catch (Exception ex)
            {
                _logger?.Invoke("Ошибка при проверке/добавлении cmdkey: " + ex.Message);
            }

            while (!token.IsCancellationRequested)
            {
                foreach (var node in Nodes)
                {
                    foreach (var share in Shares)
                    {
                        var src = $"\\\\{node}\\{share}\\{project}";
                        var key = node + "-" + share;

                        // Check if we already have an active copy
                        // Check if we already have an active copy (use TryGetValue to avoid race)
                        ActiveInfo? info = null;
                        lock (_lock)
                        {
                            _active.TryGetValue(key, out info);
                        }
                        if (info != null)
                        {
                            if (info.proc == null || info.proc.HasExited)
                            {
                                _logger?.Invoke($"[{node}][{share}] Процесс robocopy неожиданно завершился. Перезапуск...");
                                _active.TryRemove(key, out _);
                                continue; // Will restart in next iteration
                            }

                            // update lastChange by checking robocopy log activity (every 30 seconds)
                            var now = DateTime.Now;
                            if ((now - info.lastDirScanTime).TotalSeconds > 30)
                            {
                                // Check if robocopy log shows recent activity
                                bool hasRecentActivity = false;
                                
                                try
                                {
                                    if (File.Exists(info.logPath))
                                    {
                                        var logLastWrite = File.GetLastWriteTime(info.logPath);
                                        // If log was written in last 2 minutes, consider it active
                                        if ((now - logLastWrite).TotalMinutes < 2)
                                        {
                                            hasRecentActivity = true;
                                            lock (_lock) { info.lastChange = now; }
                                        }
                                    }
                                }
                                catch { }
                                
                                // Also check source directory for new files if no log activity
                                if (!hasRecentActivity && Directory.Exists(src))
                                {
                                    var latest = GetLatestWriteTime(src);
                                    if (latest.HasValue && latest.Value > info.lastChange)
                                    {
                                        // Found newer files in source
                                        lock (_lock) { 
                                            info.lastChange = latest.Value; 
                                            _logger?.Invoke($"[{node}][{share}] Обнаружены новые файлы: {latest.Value}");
                                        }
                                    }
                                }
                                
                                lock (_lock) { info.lastDirScanTime = now; }
                            }

                                // update speed/progress by measuring destination size (throttle to every 10 seconds)
                                var nowTime = DateTime.Now;
                                if ((nowTime - info.lastSampleTime).TotalSeconds >= 10)
                                {
                                    try
                                    {
                                        var destDir = Path.Combine(destRoot, project);
                                        long currentDestBytes = 0;
                                        
                                        // Try to get bytes from robocopy log first
                                        var logBytes = TryParseRobocopyLogBytes(info.logPath);
                                        if (logBytes.HasValue)
                                        {
                                            currentDestBytes = logBytes.Value;
                                        }
                                        else
                                        {
                                            // Fallback to directory size scan
                                            currentDestBytes = GetDirectorySize(destDir);
                                        }

                                        // Update file count from robocopy log
                                        var fileCount = TryParseRobocopyLogFileCount(info.logPath);
                                        if (fileCount.HasValue)
                                        {
                                            info.filesDownloaded = fileCount.Value;
                                        }

                                        info.lastDestBytes = currentDestBytes;
                                        info.lastSampleTime = nowTime;
                                    }
                                    catch 
                                    {
                                        // On error, keep current file count
                                    }
                                }

                                // check free space during copy and stop if very low
                                try
                                {
                                    var destDir = Path.Combine(destRoot, project);
                                    var freeNow = GetAvailableFreeBytes(destDir);
                                    const long minFree = 50L * 1024 * 1024; // 50 MB
                                    if (freeNow < minFree)
                                    {
                                        _logger?.Invoke($"[{node}][{share}] Критически мало свободного места ({freeNow} байт) — останавливаю robocopy.");
                                        try { if (info.proc != null && !info.proc.HasExited) info.proc.Kill(true); } catch { }
                                        _active.TryRemove(key, out _);
                                        continue;
                                    }
                                }
                                catch { }

                            continue;
                        }

                        // If source doesn't exist, skip
                        if (!Directory.Exists(src)) continue;

                        // respect worker count: allow processes on all workers (one per node/share)
                        int runningCount;
                        lock (_lock)
                        {
                            runningCount = _active.Count(v => v.Value.proc != null && !v.Value.proc.HasExited);
                        }
                        // allow up to number of workers (Nodes.Length) concurrent processes
                        if (runningCount >= Math.Max(1, Nodes.Length))
                        {
                            // skip starting new process this iteration; will retry on next loop
                            continue;
                        }

                        // ensure dest
                        var dest = Path.Combine(destRoot, project);
                        Directory.CreateDirectory(dest);

                        var logPath = Path.Combine(logRoot, $"{node}_{share}.log");

                        _logger?.Invoke($"[{node}][{share}] Найден проект, проверяю свободное место и запускаю непрерывную синхронизацию с мониторингом...");

                        // estimate source size (may be slow) and check free space on destination drive
                        long sourceTotal = 0;
                        try
                        {
                            sourceTotal = GetDirectorySize(src);
                        }
                        catch { sourceTotal = 0; }

                        long freeBytes = 0;
                        try
                        {
                            freeBytes = GetAvailableFreeBytes(dest);
                        }
                        catch { freeBytes = 0; }

                        // safety margin to avoid completely filling the disk
                        const long safetyMargin = 100L * 1024 * 1024; // 100 MB

                        if (sourceTotal > 0 && freeBytes < sourceTotal + safetyMargin)
                        {
                            _logger?.Invoke($"[{node}][{share}] Недостаточно свободного места на диске: требуется {sourceTotal} байт, доступно {freeBytes} байт. Пропускаю запуск.");
                            continue;
                        }

                        // build robocopy args - preserve existing files, only copy newer/missing files
                        var args = $"\"{src}\" \"{dest}\" /S /E /MON:1 /MOT:1 /XO /FFT /R:2 /W:3 /Z /MT:{robocopyThreads} /XD \"System Volume Information\" \"RECYCLER\" \"RECYCLED\" \"$RECYCLE.BIN\" /LOG+:\"{logPath}\"";

                        var psi = new ProcessStartInfo
                        {
                            FileName = "robocopy.exe",
                            Arguments = args,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };

                        try
                        {
                            var proc = Process.Start(psi)!;
                            var now = DateTime.Now;
                            
                            // Get actual last change time from source
                            var actualLastChange = GetLatestWriteTime(src) ?? now;
                            
                            _logger?.Invoke($"[{node}][{share}] Запущен robocopy PID={proc.Id} с непрерывным мониторингом. Последнее изменение: {actualLastChange}");
                            
                            var infoObj = new ActiveInfo
                            {
                                proc = proc,
                                lastChange = actualLastChange,
                                node = node,
                                share = share,
                                logPath = logPath,
                                totalBytes = sourceTotal,
                                lastDestBytes = 0,
                                lastSampleTime = DateTime.Now,
                                lastDirScanTime = now,
                                filesDownloaded = 0
                            };

                            // compute total source size asynchronously if unknown (may be slow)
                            if (infoObj.totalBytes == 0)
                            {
                                _ = System.Threading.Tasks.Task.Run(() =>
                                {
                                    try
                                    {
                                        infoObj.totalBytes = GetDirectorySize(src);
                                    }
                                    catch { }
                                });
                            }

                            lock (_lock)
                            {
                                _active[key] = infoObj;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.Invoke($"Ошибка запуска robocopy для {node} {share}: {ex.Message}");
                        }
                    }
                }

                await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(30), token);
            }
        }

        // Ensure cmdkey contains credentials for all Nodes. Adds Administrator / ultracam when missing.
        private void EnsureCmdKeyCredentialsForAllNodes(string existingCmdKeyList)
        {
            foreach (var node in Nodes)
            {
                try
                {
                    EnsureCmdKeyCredentials(node, existingCmdKeyList);
                }
                catch (Exception ex)
                {
                    _logger?.Invoke($"Ошибка cmdkey для {node}: {ex.Message}");
                }
            }
        }

        private void EnsureCmdKeyCredentials(string node, string existingCmdKeyList)
        {
            // If the listing already contains the node name, assume credential present
            if (!string.IsNullOrEmpty(existingCmdKeyList) &&
                (existingCmdKeyList.IndexOf(node, StringComparison.OrdinalIgnoreCase) >= 0 || existingCmdKeyList.IndexOf("\\" + node, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                // Credential exists - skip silently
                return;
            }

            // Not found — add credential silently
            var addArgs = $"/add:{node} /user:Administrator /pass:ultracam";
            var addOut = RunProcessCaptureOutput("cmdkey.exe", addArgs, TimeSpan.FromSeconds(5));
        }

        private string RunProcessCaptureOutput(string fileName, string arguments, TimeSpan timeout)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc == null) return string.Empty;
                var output = proc.StandardOutput.ReadToEnd();
                var err = proc.StandardError.ReadToEnd();
                if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
                {
                    try { proc.Kill(true); } catch { }
                }
                return (output + "\n" + err).Trim();
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private DateTime? GetLatestWriteTime(string root)
        {
            try
            {
                if (!Directory.Exists(root)) return null;
                
                DateTime? latest = null;
                
                // Check directory itself
                var dirInfo = new DirectoryInfo(root);
                latest = dirInfo.LastWriteTime;
                
                // Check files and subdirectories
                var files = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories);
                foreach (var entry in files)
                {
                    try
                    {
                        DateTime dt;
                        if (Directory.Exists(entry))
                        {
                            dt = Directory.GetLastWriteTime(entry);
                        }
                        else
                        {
                            dt = File.GetLastWriteTime(entry);
                        }
                        
                        if (!latest.HasValue || dt > latest.Value) 
                        {
                            latest = dt;
                        }
                    }
                    catch { }
                }
                return latest;
            }
            catch { return null; }
        }

        // Compute total size of files under a directory (safe, best-effort).
        private long GetDirectorySize(string path)
        {
            long size = 0;
            try
            {
                if (!Directory.Exists(path)) return 0;
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var fi = new FileInfo(file);
                        size += fi.Length;
                    }
                    catch { }
                }
            }
            catch { }
            return size;
        }

        // Try to parse the robocopy log file and extract the cumulative bytes copied.
        // Returns null if parsing fails or log not present.
    private static readonly Regex[] _robocopyRegexes = new[] {
            // English robocopy patterns
            new Regex(@"Total\s+Copied\s+Skipped\s+Mismatch\s+FAILED\s+Extras.*?Files\s*:\s*\d+\s+(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline),
            new Regex(@"Bytes\s*:\s*[\d\s,\.]+\s+(\d[\d\s,\.]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            new Regex(@"Total\s+Bytes\s*[:=]\s*([\d\s,\.]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            new Regex(@"Copied\s*:\s*([\d\s,\.]+)\s*bytes", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            // Russian robocopy patterns  
            new Regex(@"Байт[а-я]*\s*[:=]\s*([\d\s,\.]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            new Regex(@"Скопировано\s*:\s*([\d\s,\.]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            // Progress patterns
            new Regex(@"(\d+)%\s+(\d[\d\s,\.]*)\s*байт", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            new Regex(@"(\d+)%\s+(\d[\d\s,\.]*)\s*bytes", RegexOptions.Compiled | RegexOptions.IgnoreCase)
        };

    private int? TryParseRobocopyLogFileCount(string? logPath)
    {
        try
        {
            if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath)) return null;

            const int readSize = 512 * 1024; // read last 512KB to get summary section
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var length = fs.Length;
            var toRead = (int)Math.Min(readSize, length);
            fs.Seek(-toRead, SeekOrigin.End);
            using var sr = new StreamReader(fs);
            var tail = sr.ReadToEnd();

            // Robocopy outputs summary statistics at the end in this format:
            //               Total    Copied   Skipped  Mismatch    FAILED    Extras
            //    Dirs :        10         0        10         0         0         0
            //   Files :       150        25       125         0         0         0
            //   Bytes :   1.5 g     500 m    1.0 g         0         0         0
            
            // Parse "Files :" line to get "Copied" column (second number after "Files :")
            // Support both English and Russian robocopy output
            var filePatterns = new[]
            {
                @"Files\s*:\s*(\d+)\s+(\d+)",           // English: Files :   150    25
                @"Файлы\s*:\s*(\d+)\s+(\d+)",           // Russian: Файлы :   150    25
                @"Файлов\s*:\s*(\d+)\s+(\d+)"           // Russian alternative
            };

            foreach (var pattern in filePatterns)
            {
                var match = Regex.Match(tail, pattern, RegexOptions.IgnoreCase | RegexOptions.RightToLeft);
                if (match.Success && match.Groups.Count >= 3)
                {
                    // Group 1 = Total, Group 2 = Copied
                    if (int.TryParse(match.Groups[2].Value, out var copied))
                    {
                        return copied;
                    }
                }
            }

            return null;
        }
        catch { }
        return null;
    }

    private long? TryParseRobocopyLogBytes(string? logPath)
    {
        try
        {
            if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath)) return null;

            const int readSize = 512 * 1024; // read last 512KB to get summary section
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var length = fs.Length;
            var toRead = (int)Math.Min(readSize, length);
            fs.Seek(-toRead, SeekOrigin.End);
            using var sr = new StreamReader(fs);
            var tail = sr.ReadToEnd();

            // First, try to parse from robocopy summary statistics:
            //   Bytes :   1.5 g     500 m    1.0 g         0         0         0
            //             Total   Copied  Skipped  ...
            
            // Parse "Bytes :" line and extract the "Copied" value (second column)
            var bytesPatterns = new[]
            {
                @"Bytes\s*:\s*[\d\s,\.]+\s*[kmgtKMGT]?\s+([\d\s,\.]+)\s*([kmgtKMGT])?",  // English
                @"Байт[а-я]*\s*:\s*[\d\s,\.]+\s*[кмгтКМГТ]?\s+([\d\s,\.]+)\s*([кмгтКМГТ])?"  // Russian
            };

            foreach (var pattern in bytesPatterns)
            {
                var match = Regex.Match(tail, pattern, RegexOptions.IgnoreCase | RegexOptions.RightToLeft);
                if (match.Success && match.Groups.Count >= 2)
                {
                    // Extract number and unit
                    var numberStr = match.Groups[1].Value.Replace(",", "").Replace(" ", "").Trim();
                    var unit = match.Groups.Count > 2 ? match.Groups[2].Value.ToLowerInvariant() : "";
                    
                    if (double.TryParse(numberStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number))
                    {
                        // Convert based on unit suffix
                        long bytes = unit switch
                        {
                            "k" or "к" => (long)(number * 1024),
                            "m" or "м" => (long)(number * 1024 * 1024),
                            "g" or "г" => (long)(number * 1024 * 1024 * 1024),
                            "t" or "т" => (long)(number * 1024L * 1024 * 1024 * 1024),
                            _ => (long)number
                        };
                        
                        if (bytes > 1000)
                        {
                            return bytes;
                        }
                    }
                }
            }

            // Fallback: use existing regex patterns for progress lines
            long maxBytes = 0;
            foreach (var rx in _robocopyRegexes)
            {
                var matches = rx.Matches(tail);
                foreach (Match match in matches)
                {
                    for (int i = 1; i < match.Groups.Count; i++)
                    {
                        var num = match.Groups[i].Value;
                        var digits = Regex.Replace(num, @"[^\d]", "");
                        if (long.TryParse(digits, out var val) && val > maxBytes && val > 1000)
                        {
                            maxBytes = val;
                        }
                    }
                }
            }

            return maxBytes > 0 ? maxBytes : null;
        }
        catch { }
        return null;
    }

        // Get available free bytes on the drive that contains the given path.
        private long GetAvailableFreeBytes(string path)
        {
            try
            {
                var root = Path.GetPathRoot(path);
                if (string.IsNullOrEmpty(root)) return 0;
                var di = new DriveInfo(root);
                return di.AvailableFreeSpace;
            }
            catch { return 0; }
        }
    }
}
