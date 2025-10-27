using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Linq;
using System.Threading;
using UCXSyncTool.Models;

namespace UCXSyncTool.Services
{
    public class SyncService
    {
        private readonly string[] Nodes = new[] { "WU01","WU02","WU03","WU04","WU05","WU06","WU07","WU08","WU09","WU10","WU11","WU12","WU13","CU" };
        private readonly string[] Shares = new[] { "E$", "F$" };

        private CancellationTokenSource? _cts;
        private readonly object _lock = new();

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
            public double speedBytesPerSec = 0;
        }
        private readonly Dictionary<string, ActiveInfo> _active = new();

        private Action<string>? _logger;

        public void Start(string project, string destRoot, int idleMinutes, Action<string>? logger = null, int robocopyThreads = 8)
        {
            lock (_lock)
            {
                if (_cts != null) return; // already running
                _cts = new CancellationTokenSource();
                _logger = logger;
                var token = _cts.Token;
                ThreadPool.QueueUserWorkItem(async _ =>
                {
                    try
                    {
                        await RunLoop(project, destRoot, idleMinutes, token, robocopyThreads);
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
                    Share = kv.Value.share ?? string.Empty,
                    Status = (kv.Value.proc == null || kv.Value.proc.HasExited) ? "Exited" : "Running",
                    LastChange = kv.Value.lastChange,
                    ProcessId = (kv.Value.proc == null || kv.Value.proc.HasExited) ? null : kv.Value.proc.Id,
                    LogPath = kv.Value.logPath ?? string.Empty,
                    SpeedBytesPerSec = kv.Value.speedBytesPerSec,
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
                                if (!string.IsNullOrEmpty(name)) set.Add(name);
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

        private async System.Threading.Tasks.Task RunLoop(string project, string destRoot, int idleMinutes, CancellationToken token, int robocopyThreads)
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
                                _logger?.Invoke($"[{node}][{share}] Копирование завершено.");
                                _active.Remove(key);
                                continue;
                            }

                                            // update lastChange by scanning files
                                            if (Directory.Exists(src))
                                            {
                                                var latest = GetLatestWriteTime(src);
                                                if (latest.HasValue)
                                                {
                                                    // update lastChange safely
                                                    lock (_lock) { info.lastChange = latest.Value; }
                                                }
                                            }

                                // update speed/progress by measuring destination size
                                try
                                {
                                    var destDir = Path.Combine(destRoot, project);
                                    // Prefer parsing robocopy log for bytes copied (cheaper than full directory scan)
                                    long? currentDestBytes = null;
                                    try
                                    {
                                        currentDestBytes = TryParseRobocopyLogBytes(info.logPath);
                                    }
                                    catch { }

                                    if (!currentDestBytes.HasValue)
                                    {
                                        // fallback to directory size scan — throttle scans to once every 5s per active
                                        var nowT = DateTime.Now;
                                        if ((nowT - info.lastDirScanTime).TotalSeconds > 5)
                                        {
                                            currentDestBytes = GetDirectorySize(destDir);
                                            lock (_lock) { info.lastDirScanTime = nowT; }
                                        }
                                        else
                                        {
                                            // reuse last known destination bytes to avoid heavy IO
                                            currentDestBytes = info.lastDestBytes;
                                        }
                                    }

                                    var nowTime = DateTime.Now;
                                    var timeDiff = (nowTime - info.lastSampleTime).TotalSeconds;
                                    if (timeDiff > 0.5)
                                    {
                                        var delta = currentDestBytes.GetValueOrDefault() - info.lastDestBytes;
                                        info.speedBytesPerSec = delta / Math.Max(1.0, timeDiff);
                                        info.lastDestBytes = currentDestBytes.GetValueOrDefault();
                                        info.lastSampleTime = nowTime;
                                    }

                                    // check free space during copy and stop if very low
                                    try
                                    {
                                        var freeNow = GetAvailableFreeBytes(destDir);
                                        const long minFree = 50L * 1024 * 1024; // 50 MB
                                        if (freeNow < minFree)
                                        {
                                            _logger?.Invoke($"[{node}][{share}] Критически мало свободного места ({freeNow} байт) — останавливаю robocopy.");
                                            try { if (info.proc != null && !info.proc.HasExited) info.proc.Kill(true); } catch { }
                                            _active.Remove(key);
                                            continue;
                                        }
                                    }
                                    catch { }
                                }
                                catch { }

                                // check idle time
                                var last = info.lastChange;
                                var idle = (DateTime.Now - last).TotalMinutes;
                                if (idle >= idleMinutes)
                                {
                                    _logger?.Invoke($"[{node}][{share}] Нет новых файлов {idleMinutes} минут — останавливаю robocopy.");
                                    try { if (info.proc != null && !info.proc.HasExited) info.proc.Kill(true); } catch { }
                                    _active.Remove(key);
                                }

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

                        _logger?.Invoke($"[{node}][{share}] Найден проект, проверяю свободное место и запускаю синхронизацию...");

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

                        // build robocopy args
                        var args = $"\"{src}\" \"{dest}\" /S /E /MON:1 /MOT:1 /FFT /R:2 /W:3 /Z /MT:{robocopyThreads} /XD \"System Volume Information\" \"RECYCLER\" \"RECYCLED\" /LOG+:\"{logPath}\"";

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
                            var infoObj = new ActiveInfo
                            {
                                proc = proc,
                                lastChange = now,
                                node = node,
                                share = share,
                                logPath = logPath,
                                totalBytes = sourceTotal,
                                lastDestBytes = 0,
                                lastSampleTime = DateTime.Now,
                                speedBytesPerSec = 0
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
                _logger?.Invoke($"cmdkey: credentials exist for {node}");
                return;
            }

            // Not found — add credential
            var addArgs = $"/add:{node} /user:Administrator /pass:ultracam";
            var addOut = RunProcessCaptureOutput("cmdkey.exe", addArgs, TimeSpan.FromSeconds(5));
            _logger?.Invoke($"cmdkey: added credentials for {node}");
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
                var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);
                DateTime? latest = null;
                foreach (var f in files)
                {
                    try
                    {
                        var dt = File.GetLastWriteTime(f);
                        if (!latest.HasValue || dt > latest.Value) latest = dt;
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
            new Regex(@"Bytes\s*[:=]\s*([\d\s,\.]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            new Regex(@"Байт[а-я]*\s*[:=]\s*([\d\s,\.]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            new Regex(@"Total\s+Bytes\s*[:=]\s*([\d\s,\.]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            new Regex(@"Copied\s*:\s*([\d\s,\.]+)\s*bytes", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        };

    private long? TryParseRobocopyLogBytes(string? logPath)
    {
        try
        {
            if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath)) return null;

            const int readSize = 256 * 1024; // read last 256KB
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var length = fs.Length;
            var toRead = (int)Math.Min(readSize, length);
            fs.Seek(-toRead, SeekOrigin.End);
            using var sr = new StreamReader(fs);
            var tail = sr.ReadToEnd();

            // Try compiled regexes, take last match when available
            foreach (var rx in _robocopyRegexes)
            {
                var matches = rx.Matches(tail);
                if (matches.Count > 0)
                {
                    var m = matches[matches.Count - 1];
                    var num = m.Groups[1].Value;
                    var digits = Regex.Replace(num, "\\D", "");
                    if (long.TryParse(digits, out var val)) return val;
                }
            }

            // fallback: last large integer
            var fallback = Regex.Matches(tail, "(\\d{4,})");
            if (fallback.Count > 0)
            {
                var s = fallback[fallback.Count - 1].Value;
                if (long.TryParse(s, out var v2)) return v2;
            }
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
