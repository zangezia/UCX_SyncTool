using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace UCXSyncTool.Services
{
    /// <summary>
    /// Service for monitoring system performance metrics (CPU, disk, network, memory).
    /// </summary>
    public class PerformanceMonitoringService : IDisposable
    {
        private PerformanceCounter? _cpuCounter;
        private PerformanceCounter? _diskReadCounter;
        private PerformanceCounter? _diskWriteCounter;
        private string? _targetDiskInstance;
        private NetworkInterface? _netInterface;
        private long _lastNetBytes;
        private DateTime _lastNetTime = DateTime.MinValue;
        private readonly Queue<float> _cpuReadings = new();
        
        private const int CpuSmoothingSamples = 3;
        private const double MaxDiskMBps = 200.0;
        private const long GigabitBps = 1_000_000_000;

        private Action<string>? _logger;
        private bool _disposed;

        /// <summary>
        /// Initialize performance monitoring.
        /// </summary>
        /// <param name="logger">Optional logger for diagnostic messages.</param>
        public void Initialize(Action<string>? logger = null)
        {
            _logger = logger;
            
            try
            {
                InitializeCpuCounter();
                InitializeNetworkInterface();
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Performance counters initialization error: {ex.Message}");
            }
        }

        /// <summary>
        /// Set target disk for monitoring based on path.
        /// </summary>
        public void SetTargetDisk(string? targetPath)
        {
            try
            {
                // Dispose old counters
                _diskReadCounter?.Dispose();
                _diskWriteCounter?.Dispose();
                _diskReadCounter = null;
                _diskWriteCounter = null;
                _targetDiskInstance = null;

                if (string.IsNullOrEmpty(targetPath) || !Directory.Exists(targetPath))
                {
                    return;
                }

                var driveInfo = new DriveInfo(Path.GetPathRoot(targetPath) ?? "");
                var driveLetter = driveInfo.Name.TrimEnd('\\', ':');

                var category = new PerformanceCounterCategory("PhysicalDisk");
                var instanceNames = category.GetInstanceNames();
                
                foreach (var instanceName in instanceNames)
                {
                    if (instanceName.Contains($" {driveLetter}:", StringComparison.OrdinalIgnoreCase))
                    {
                        _targetDiskInstance = instanceName;
                        _diskReadCounter = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", instanceName);
                        _diskWriteCounter = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", instanceName);
                        
                        // Prime counters
                        _diskReadCounter.NextValue();
                        _diskWriteCounter.NextValue();
                        
                        _logger?.Invoke($"Диск назначения: {driveLetter}:");
                        break;
                    }
                }

                if (_targetDiskInstance == null)
                {
                    _logger?.Invoke($"Could not find counter for disk {driveLetter}");
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Disk monitoring setup error: {ex.Message}");
            }
        }

        /// <summary>
        /// Get current performance metrics.
        /// </summary>
        public PerformanceMetrics GetMetrics()
        {
            var metrics = new PerformanceMetrics();

            try
            {
                // CPU with smoothing
                float cpuRaw = _cpuCounter?.NextValue() ?? 0f;
                _cpuReadings.Enqueue(cpuRaw);
                if (_cpuReadings.Count > CpuSmoothingSamples)
                {
                    _cpuReadings.Dequeue();
                }
                metrics.CpuPercent = _cpuReadings.Average();

                // Disk throughput
                float diskRead = _diskReadCounter?.NextValue() ?? 0f;
                float diskWrite = _diskWriteCounter?.NextValue() ?? 0f;
                metrics.DiskBytesPerSec = (double)diskRead + (double)diskWrite;
                metrics.DiskMBps = metrics.DiskBytesPerSec / 1024.0 / 1024.0;
                metrics.DiskPercent = MaxDiskMBps > 0 ? (metrics.DiskMBps / MaxDiskMBps * 100.0) : 0.0;

                // Network
                if (_netInterface != null)
                {
                    try
                    {
                        var stats = _netInterface.GetIPv4Statistics();
                        long curBytes = stats.BytesReceived + stats.BytesSent;
                        var now = DateTime.UtcNow;
                        
                        if (_lastNetTime != DateTime.MinValue)
                        {
                            var elapsed = (now - _lastNetTime).TotalSeconds;
                            if (elapsed > 0)
                            {
                                metrics.NetworkBytesPerSec = (curBytes - _lastNetBytes) / elapsed;
                            }
                        }
                        
                        _lastNetBytes = curBytes;
                        _lastNetTime = now;
                    }
                    catch { /* Ignore per-tick errors */ }
                }

                metrics.NetworkMBps = metrics.NetworkBytesPerSec / 1024.0 / 1024.0;
                metrics.NetworkPercent = (metrics.NetworkBytesPerSec * 8.0) / GigabitBps * 100.0;
            }
            catch { /* Return partial metrics on error */ }

            return metrics;
        }

        /// <summary>
        /// Get free disk space for a path.
        /// </summary>
        public (double FreeGB, double PercentFree)? GetDiskFreeSpace(string? path)
        {
            try
            {
                if (string.IsNullOrEmpty(path))
                {
                    return null;
                }

                if (GetDiskFreeSpaceEx(path, out ulong freeBytes, out ulong totalBytes, out _))
                {
                    double freeGb = freeBytes / 1024.0 / 1024.0 / 1024.0;
                    double pctFree = totalBytes > 0 ? (freeBytes * 100.0 / totalBytes) : 0;
                    return (freeGb, pctFree);
                }
            }
            catch { }
            
            return null;
        }

        private void InitializeCpuCounter()
        {
            try
            {
                // Try "Processor Information" for more accurate readings on modern systems
                _cpuCounter = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total");
            }
            catch
            {
                try
                {
                    // Fallback to legacy counter
                    _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                }
                catch (Exception ex)
                {
                    _logger?.Invoke($"CPU counter initialization failed: {ex.Message}");
                }
            }

            // Prime counter
            _cpuCounter?.NextValue();
        }

        private void InitializeNetworkInterface()
        {
            _netInterface = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                            && n.GetIPProperties().UnicastAddresses.Count > 0
                            && n.Speed > 0)
                .OrderByDescending(n =>
                {
                    // Prioritize 192.168.200.* subnet
                    var hasTarget200Ip = n.GetIPProperties().UnicastAddresses
                        .Any(addr => addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                                    addr.Address.ToString().StartsWith("192.168.200."));
                    return hasTarget200Ip ? 1000 + n.Speed : n.Speed;
                })
                .FirstOrDefault();

            if (_netInterface != null)
            {
                try
                {
                    var stats = _netInterface.GetIPv4Statistics();
                    _lastNetBytes = stats.BytesReceived + stats.BytesSent;
                    _lastNetTime = DateTime.UtcNow;
                    
                    var targetIps = _netInterface.GetIPProperties().UnicastAddresses
                        .Where(addr => addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        .Select(addr => addr.Address.ToString())
                        .ToList();
                    var speedGbps = _netInterface.Speed / 1_000_000_000.0;
                    var ipList = string.Join(", ", targetIps);
                    _logger?.Invoke($"Сеть: {ipList} - {speedGbps:F1} Gbps");
                }
                catch
                {
                    _lastNetBytes = 0;
                    _lastNetTime = DateTime.MinValue;
                }
            }
            else
            {
                _logger?.Invoke("Suitable network interface not found");
            }
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool GetDiskFreeSpaceEx(
            string lpDirectoryName,
            out ulong lpFreeBytesAvailable,
            out ulong lpTotalNumberOfBytes,
            out ulong lpTotalNumberOfFreeBytes);

        public void Dispose()
        {
            if (_disposed) return;
            
            _cpuCounter?.Dispose();
            _diskReadCounter?.Dispose();
            _diskWriteCounter?.Dispose();
            
            _disposed = true;
        }
    }

    /// <summary>
    /// Performance metrics snapshot.
    /// </summary>
    public class PerformanceMetrics
    {
        public float CpuPercent { get; set; }
        public double DiskBytesPerSec { get; set; }
        public double DiskMBps { get; set; }
        public double DiskPercent { get; set; }
        public double NetworkBytesPerSec { get; set; }
        public double NetworkMBps { get; set; }
        public double NetworkPercent { get; set; }
    }
}
