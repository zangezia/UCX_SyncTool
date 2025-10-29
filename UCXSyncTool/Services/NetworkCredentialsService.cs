using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Win32;

namespace UCXSyncTool.Services
{
    /// <summary>
    /// Service for managing network credentials and SMB protocol configuration.
    /// </summary>
    public class NetworkCredentialsService
    {
        private readonly string[] _hosts;
        private readonly string _username;
        private readonly string _password;
        private readonly string[] _shares;
        private Action<string>? _logger;

        /// <summary>
        /// Create a new network credentials service.
        /// </summary>
        /// <param name="hosts">List of host names.</param>
        /// <param name="shares">List of share names.</param>
        /// <param name="username">Username for authentication.</param>
        /// <param name="password">Password for authentication.</param>
        public NetworkCredentialsService(string[] hosts, string[] shares, string username, string password)
        {
            _hosts = hosts ?? throw new ArgumentNullException(nameof(hosts));
            _shares = shares ?? throw new ArgumentNullException(nameof(shares));
            _username = username ?? throw new ArgumentNullException(nameof(username));
            _password = password ?? throw new ArgumentNullException(nameof(password));
        }

        /// <summary>
        /// Set logger for diagnostic messages.
        /// </summary>
        public void SetLogger(Action<string>? logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Check if SMB1 protocol is available on the system.
        /// Shows a warning dialog if SMB1 is not detected.
        /// </summary>
        public void CheckSmb1Protocol()
        {
            try
            {
                bool isSmb1Available = CheckSmb1Registry() || CheckSmb1Dism() || CheckSmb1Driver();

                if (!isSmb1Available)
                {
                    var result = MessageBox.Show(
                        "SMB1 protocol not detected on the system, which may be required for accessing legacy shared resources.\n\n" +
                        "Would you like to open the SMB1 enablement instructions?",
                        "Warning: SMB1 Not Detected",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning
                    );

                    if (result == MessageBoxResult.Yes)
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "https://learn.microsoft.com/windows-server/storage/file-server/troubleshoot/detect-enable-and-disable-smbv1-v2-v3",
                            UseShellExecute = true
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Error checking SMB1: {ex.Message}");
            }
        }

        /// <summary>
        /// Setup network credentials for all configured hosts.
        /// </summary>
        public void SetupCredentials()
        {
            try
            {
                foreach (var host in _hosts)
                {
                    AddCredential(host, _username, _password);
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Error setting up network credentials: {ex.Message}");
            }
        }

        /// <summary>
        /// Close all network connections to configured hosts and shares.
        /// </summary>
        public void CloseAllConnections()
        {
            try
            {
                foreach (var host in _hosts)
                {
                    foreach (var share in _shares)
                    {
                        var uncPath = $"\\\\{host}\\{share}";
                        try
                        {
                            var startInfo = new ProcessStartInfo
                            {
                                FileName = "net.exe",
                                Arguments = $"use {uncPath} /delete /y",
                                UseShellExecute = false,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                                CreateNoWindow = true
                            };

                            using var process = Process.Start(startInfo);
                            process?.WaitForExit(2000); // Max 2 seconds per connection
                        }
                        catch { /* Ignore individual connection errors */ }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error closing network connections: {ex.Message}");
            }
        }

        private bool CheckSmb1Registry()
        {
            try
            {
                // Check mrxsmb10 service (SMB1 client)
                using var smbClientKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\mrxsmb10");
                if (smbClientKey != null)
                {
                    var startValue = smbClientKey.GetValue("Start");
                    if (startValue is int startType && startType <= 3)
                    {
                        return true;
                    }
                }

                // Check SMB server parameters
                using var serverKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters");
                if (serverKey != null)
                {
                    var smb1Value = serverKey.GetValue("SMB1");
                    if (smb1Value == null || (smb1Value is int value && value == 1))
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Error checking SMB1 via registry: {ex.Message}");
            }

            return false;
        }

        private bool CheckSmb1Dism()
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "dism.exe",
                    Arguments = "/online /get-featureinfo /featurename:SMB1Protocol-Client",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(startInfo);
                if (process != null)
                {
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    
                    if (process.ExitCode == 0 && output.Contains("State : Enabled"))
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Error checking SMB1 via DISM: {ex.Message}");
            }

            return false;
        }

        private bool CheckSmb1Driver()
        {
            try
            {
                string smb1DriverPath = @"C:\Windows\System32\drivers\mrxsmb10.sys";
                if (File.Exists(smb1DriverPath))
                {
                    var netUseInfo = new ProcessStartInfo
                    {
                        FileName = "net.exe",
                        Arguments = "use",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    };

                    using var netProcess = Process.Start(netUseInfo);
                    if (netProcess != null)
                    {
                        netProcess.WaitForExit();
                        return netProcess.ExitCode == 0;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Error checking SMB1 driver: {ex.Message}");
            }

            return false;
        }

        private bool AddCredential(string target, string username, string password)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmdkey.exe",
                    Arguments = $"/add:{target} /user:{username} /pass:{password}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(startInfo);
                if (process != null)
                {
                    process.WaitForExit();
                    return process.ExitCode == 0;
                }
                return false;
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Error adding credentials for {target}: {ex.Message}");
                return false;
            }
        }
    }
}
