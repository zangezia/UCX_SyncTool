namespace UCXSyncTool
{
    /// <summary>
    /// Application configuration constants.
    /// </summary>
    public static class Configuration
    {
        /// <summary>
        /// List of UCX worker nodes.
        /// </summary>
        public static readonly string[] Nodes = new[]
        {
            "WU01", "WU02", "WU03", "WU04", "WU05", "WU06", "WU07",
            "WU08", "WU09", "WU10", "WU11", "WU12", "WU13", "CU"
        };

        /// <summary>
        /// List of network shares to monitor.
        /// </summary>
        public static readonly string[] Shares = new[] { "E$", "F$" };

        /// <summary>
        /// Default username for network authentication.
        /// </summary>
        public const string DefaultUsername = "Administrator";

        /// <summary>
        /// Default password for network authentication.
        /// </summary>
        public const string DefaultPassword = "ultracam";

        /// <summary>
        /// Default number of threads for robocopy operations.
        /// </summary>
        public const int DefaultRobocopyThreads = 8;

        /// <summary>
        /// UI update interval in seconds.
        /// </summary>
        public const int UiUpdateIntervalSeconds = 2;

        /// <summary>
        /// Performance monitoring update interval in seconds.
        /// </summary>
        public const int PerformanceUpdateIntervalSeconds = 1;

        /// <summary>
        /// Robocopy log parsing interval in seconds.
        /// </summary>
        public const int RobocopyLogParseIntervalSeconds = 10;

        /// <summary>
        /// Main service loop check interval in seconds.
        /// </summary>
        public const int ServiceLoopIntervalSeconds = 10;

        /// <summary>
        /// Directory scan throttle interval in seconds.
        /// </summary>
        public const int DirectoryScanIntervalSeconds = 10;

        /// <summary>
        /// Minimum free disk space in bytes before stopping copy operations.
        /// </summary>
        public const long MinimumFreeDiskSpace = 50L * 1024 * 1024; // 50 MB

        /// <summary>
        /// Safety margin for disk space in bytes.
        /// </summary>
        public const long DiskSpaceSafetyMargin = 100L * 1024 * 1024; // 100 MB

        /// <summary>
        /// Maximum disk throughput for percentage calculation (MB/s).
        /// </summary>
        public const double MaxDiskThroughputMBps = 200.0;

        /// <summary>
        /// Network speed for percentage calculation (1 Gbps in bps).
        /// </summary>
        public const long NetworkSpeedBps = 1_000_000_000;

        /// <summary>
        /// Number of CPU readings to smooth over.
        /// </summary>
        public const int CpuSmoothingSamples = 3;

        /// <summary>
        /// Robocopy log read size in bytes (last N bytes to read for summary).
        /// </summary>
        public const int RobocopyLogReadSize = 512 * 1024; // 512 KB

        /// <summary>
        /// Timeout for cmdkey operations in seconds.
        /// </summary>
        public const int CmdKeyTimeoutSeconds = 5;

        /// <summary>
        /// Timeout for network connection cleanup in milliseconds per connection.
        /// </summary>
        public const int NetworkConnectionCloseTimeoutMs = 2000;

        /// <summary>
        /// Recent activity threshold for robocopy logs in minutes.
        /// </summary>
        public const double LogActivityThresholdMinutes = 2.0;

        /// <summary>
        /// Share name friendly aliases.
        /// </summary>
        public static string GetShareAlias(string? share)
        {
            if (string.IsNullOrEmpty(share)) return string.Empty;
            return share.ToUpperInvariant() switch
            {
                "E$" => "Fast",
                "F$" => "Normal",
                _ => share
            };
        }
    }
}
