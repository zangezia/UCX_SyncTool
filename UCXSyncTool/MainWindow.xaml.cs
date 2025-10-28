using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

using UCXSyncTool.Services;
using UCXSyncTool.Models;

namespace UCXSyncTool
{
    public partial class MainWindow : Window
    {
        private Services.SyncService _syncService;
        private DispatcherTimer _uiTimer;
        private ObservableCollection<Models.ActiveCopyViewModel> _activeList = new();
        private Models.AppSettings _settings;
    private PerformanceCounter? _cpuCounter;
    private PerformanceCounter? _diskReadCounter;
    private PerformanceCounter? _diskWriteCounter;
    private string? _targetDiskInstance; // Instance name for target disk (e.g., "0 C:", "1 D:")
    private DispatcherTimer? _perfTimer;
    private NetworkInterface? _netInterface;
    private long _lastNetBytes = 0;
    private DateTime _lastNetTime = DateTime.MinValue;
    // assumption for disk percent scaling (MB/s). Adjust if needed.
    private const double MaxDiskMBps = 200.0;

        public MainWindow()
        {
            InitializeComponent();

            CheckSmb1Protocol();
            SetupNetworkCredentials();
            
            ActiveGrid.ItemsSource = _activeList;
            _syncService = new Services.SyncService();

            // load settings
            _settings = Services.SettingsService.Load();
            if (_settings.CachedProjects?.Count > 0)
            {
                ProjectCombo.ItemsSource = _settings.CachedProjects;
            }
            if (!string.IsNullOrEmpty(_settings.LastProject)) ProjectCombo.Text = _settings.LastProject;
            if (!string.IsNullOrEmpty(_settings.DestRoot)) DestRootText.Text = _settings.DestRoot;
            ThreadsText.Text = _settings.RobocopyThreads.ToString();


            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _uiTimer.Tick += UiTimer_Tick;
            _uiTimer.Start();

            SetupPerformanceCounters();
            
            // Setup disk monitoring if destination is already set
            if (!string.IsNullOrEmpty(_settings.DestRoot))
            {
                SetTargetDisk(_settings.DestRoot);
            }

            this.Closing += MainWindow_Closing;
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            // stop all robocopy processes first
            _syncService.Stop();

            // on close persist current settings
            try
            {
                _settings.LastProject = ProjectCombo.Text;
                _settings.DestRoot = DestRootText.Text;
                if (int.TryParse(ThreadsText.Text, out var threadsVal)) _settings.RobocopyThreads = threadsVal;
                // also capture current items in combo
                if (ProjectCombo.ItemsSource is System.Collections.IEnumerable items)
                {
                    var list = new System.Collections.Generic.List<string>();
                    foreach (var it in items)
                    {
                        if (it is string s) list.Add(s);
                    }
                    _settings.CachedProjects = list;
                }
                Services.SettingsService.Save(_settings);
            }
            catch { }

            // dispose perf counters
            try
            {
                _perfTimer?.Stop();
                _cpuCounter?.Dispose();
                _diskReadCounter?.Dispose();
                _diskWriteCounter?.Dispose();
            }
            catch { }

            // close all network connections
            CloseAllNetworkConnections();
        }

        private void BrowseDestBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                using var dlg = new FolderBrowserDialog();
                dlg.Description = "Выберите папку назначения";
                dlg.UseDescriptionForTitle = true;
                if (!string.IsNullOrEmpty(DestRootText.Text) && Directory.Exists(DestRootText.Text)) dlg.SelectedPath = DestRootText.Text;
                var res = dlg.ShowDialog();
                if (res == System.Windows.Forms.DialogResult.OK || res == System.Windows.Forms.DialogResult.Yes)
                {
                    DestRootText.Text = dlg.SelectedPath;
                    
                    // Setup disk monitoring for selected path
                    SetTargetDisk(dlg.SelectedPath);
                    
                    // persist immediately
                    try
                    {
                        _settings.DestRoot = dlg.SelectedPath;
                        Services.SettingsService.Save(_settings);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                AppendLog("Ошибка выбора папки: " + ex.Message);
            }
        }

        private async void RefreshProjectsBtn_Click(object sender, RoutedEventArgs e)
        {
            RefreshProjectsBtn.IsEnabled = false;
            LogText.AppendText("Сканирование узлов и шар для проектов...\n");
            try
            {
                var projects = await Task.Run(() => _syncService.FindAvailableProjects(AppendLog));
                Dispatcher.Invoke(() =>
                {
                    ProjectCombo.ItemsSource = projects;
                    if (projects.Count > 0) ProjectCombo.Text = projects[0];
                });
                // update settings cache and persist
                _settings.CachedProjects = projects;
                if (projects.Count > 0) _settings.LastProject = projects[0];
                Services.SettingsService.Save(_settings);
                AppendLog($"Найдено проектов: {projects.Count}");
            }
            catch (Exception ex)
            {
                AppendLog("Ошибка при сканировании проектов: " + ex.Message);
            }
            finally
            {
                RefreshProjectsBtn.IsEnabled = true;
            }
        }

        private void UiTimer_Tick(object? sender, EventArgs e)
        {
            var list = _syncService.GetActiveCopies();
            // simple sync: replace contents
            _activeList.Clear();
            foreach (var item in list)
            {
                _active_list_add(item);
            }
        }

        // helper to preserve strong typing when adding
        private void _active_list_add(Models.ActiveCopyViewModel item)
        {
            _activeList.Add(item);
        }

        private async void StartBtn_Click(object sender, RoutedEventArgs e)
        {
            var project = ProjectCombo.Text?.Trim();
            var dest = DestRootText.Text?.Trim();
            if (string.IsNullOrEmpty(project) || string.IsNullOrEmpty(dest))
            {
                System.Windows.MessageBox.Show("Введите проект и папку назначения", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Setup disk monitoring for target path
            SetTargetDisk(dest);

            // persist last used values
            _settings.LastProject = project;
            _settings.DestRoot = dest;
            if (int.TryParse(ThreadsText.Text, out var threadsVal)) _settings.RobocopyThreads = threadsVal;
            Services.SettingsService.Save(_settings);

            StartBtn.IsEnabled = false;
            StopBtn.IsEnabled = true;

            LogText.AppendText($"Запуск синхронизации проекта {project} -> {dest}\n");

            await Task.Run(() => _syncService.Start(project, dest, AppendLog, _settings.RobocopyThreads));
        }

        private void SetupPerformanceCounters()
        {
            try
            {
                _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                // Disk counters will be initialized when target path is set
                _diskReadCounter = null;
                _diskWriteCounter = null;
                _targetDiskInstance = null;

                // choose an active network interface via NetworkInterface API (more reliable than perf counter instance names)
                // prioritize interface with IP address 192.168.200.*
                _netInterface = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up
                                && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                                && n.GetIPProperties().UnicastAddresses.Count > 0
                                && n.Speed > 0)
                    .OrderByDescending(n => {
                        // Check if this interface has IP in 192.168.200.* subnet
                        var hasTarget200Ip = n.GetIPProperties().UnicastAddresses
                            .Any(addr => addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                                        addr.Address.ToString().StartsWith("192.168.200."));
                        return hasTarget200Ip ? 1000 + n.Speed : n.Speed; // high priority for 192.168.200.*
                    })
                    .FirstOrDefault();
                if (_netInterface != null)
                {
                    try
                    {
                        var stats = _netInterface.GetIPv4Statistics();
                        _lastNetBytes = stats.BytesReceived + stats.BytesSent;
                        _lastNetTime = DateTime.UtcNow;
                        
                        // Log selected interface info
                        var targetIps = _netInterface.GetIPProperties().UnicastAddresses
                            .Where(addr => addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            .Select(addr => addr.Address.ToString())
                            .ToList();
                        var has200Ip = targetIps.Any(ip => ip.StartsWith("192.168.200."));
                        var priority = has200Ip ? " (приоритет: 192.168.200.*)" : "";
                        AppendLog($"Выбран сетевой интерфейс: {_netInterface.Name} [{string.Join(", ", targetIps)}]{priority}");
                    }
                    catch { _lastNetBytes = 0; _lastNetTime = DateTime.MinValue; }
                }
                else
                {
                    AppendLog("Подходящий сетевой интерфейс не найден");
                }

                _perfTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _perfTimer.Tick += (s, e) =>
                {
                    try
                    {
                        float cpu = _cpuCounter?.NextValue() ?? 0f;

                        // disk throughput (bytes/sec) from read + write counters -> convert to MB/s
                        float diskRead = _diskReadCounter?.NextValue() ?? 0f;
                        float diskWrite = _diskWriteCounter?.NextValue() ?? 0f;
                        double diskBytesPerSec = (double)diskRead + (double)diskWrite;
                        double diskMBs = diskBytesPerSec / 1024.0 / 1024.0;
                        // convert to percent of an assumed max throughput
                        double diskPct = MaxDiskMBps > 0 ? (diskMBs / MaxDiskMBps * 100.0) : 0.0;

                        CpuBar.Value = Math.Clamp(cpu, 0, 100);
                        CpuLabel.Text = $"{cpu:F1}%";

                        DiskBar.Value = Math.Clamp((float)diskPct, 0, 100);
                        DiskLabel.Text = $"{diskMBs:F2} MB/s ({diskPct:F1}% of {MaxDiskMBps} MB/s)";

                        // network: compute bytes/sec from NetworkInterface statistics (delta)
                        double netBytesPerSec = 0.0;
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
                                        netBytesPerSec = (curBytes - _lastNetBytes) / elapsed;
                                    }
                                }
                                _lastNetBytes = curBytes;
                                _lastNetTime = now;
                            }
                            catch { /* ignore per-tick errors */ }
                        }

                        var netPct = (netBytesPerSec * 8.0) / 1_000_000_000.0 * 100.0; // percent of 1 Gbps
                        NetBar.Value = Math.Clamp((float)netPct, 0, 100);
                        NetLabel.Text = $"{(netBytesPerSec / 1024.0 / 1024.0):F2} MB/s ({netPct:F1}% of 1Gbps)";

                        // update dest free space indicator
                        try
                        {
                            var dest = DestRootText.Text?.Trim();
                            if (!string.IsNullOrEmpty(dest))
                            {
                                if (GetDiskFreeSpaceEx(dest, out ulong freeBytes, out ulong totalBytes, out _))
                                {
                                    double freeGb = freeBytes / 1024.0 / 1024.0 / 1024.0;
                                    double pctFree = totalBytes > 0 ? (freeBytes * 100.0 / totalBytes) : 0;
                                    DestFreeLabel.Text = $"{freeGb:F2} GB ({pctFree:F1}% свободно)";
                                }
                                else
                                {
                                    DestFreeLabel.Text = "—";
                                }
                            }
                            else DestFreeLabel.Text = string.Empty;
                        }
                        catch { DestFreeLabel.Text = "—"; }
                    }
                    catch { }
                };
                // prime counters
                _cpuCounter?.NextValue();
                _diskReadCounter?.NextValue();
                _diskWriteCounter?.NextValue();
                // network stats primed earlier via _lastNetBytes when interface selected
                _perfTimer.Start();
            }
            catch (Exception ex)
            {
                AppendLog("Perf counters unavailable: " + ex.Message);
            }
        }

        private void SetTargetDisk(string? targetPath)
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
                    // No valid target - disable disk monitoring
                    return;
                }

                // Get drive letter (e.g., "C:", "D:")
                var driveInfo = new DriveInfo(Path.GetPathRoot(targetPath) ?? "");
                var driveLetter = driveInfo.Name.TrimEnd('\\', ':');

                // Find matching PhysicalDisk instance
                // Instance names are like "0 C:", "1 D:", etc.
                var category = new System.Diagnostics.PerformanceCounterCategory("PhysicalDisk");
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
                        
                        AppendLog($"Мониторинг диска: {instanceName}");
                        break;
                    }
                }

                if (_targetDiskInstance == null)
                {
                    AppendLog($"Не удалось найти счётчик для диска {driveLetter}");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"Ошибка настройки мониторинга диска: {ex.Message}");
            }
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool GetDiskFreeSpaceEx(string lpDirectoryName,
            out ulong lpFreeBytesAvailable, out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);

        private void StopBtn_Click(object sender, RoutedEventArgs e)
        {
            _syncService.Stop();
            StartBtn.IsEnabled = true;
            StopBtn.IsEnabled = false;
            LogText.AppendText("Остановка сервиса\n");
        }

        private void ExitBtn_Click(object sender, RoutedEventArgs e)
        {
            // Останавливаем все процессы robocopy
            _syncService.Stop();
            
            // Закрываем окно (это вызовет MainWindow_Closing где произойдёт очистка)
            this.Close();
        }

        private void AppendLog(string text)
        {
            Dispatcher.Invoke(() =>
            {
                LogText.AppendText(text + "\n");
                LogText.ScrollToEnd();
            });
        }

        private void CredentialsBtn_Click(object sender, RoutedEventArgs e)
        {
            // Повторно добавляем учетные данные для всех хостов UCX
            SetupNetworkCredentials();
        }

        private void CheckSmb1Protocol()
        {
            try 
            {
                bool isSmb1Available = false;

                // Метод 1: Проверка через реестр службы SMB1
                try 
                {
                    // Проверяем наличие службы mrxsmb10 (SMB1 клиент)
                    using var smbClientKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\mrxsmb10");
                    if (smbClientKey != null)
                    {
                        var startValue = smbClientKey.GetValue("Start");
                        if (startValue != null && startValue is int startType && startType <= 3) // 0=Boot, 1=System, 2=Automatic, 3=Manual
                        {
                            isSmb1Available = true;
                        }
                    }

                    // Проверяем параметры SMB сервера
                    if (!isSmb1Available)
                    {
                        using var serverKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters");
                        if (serverKey != null)
                        {
                            var smb1Value = serverKey.GetValue("SMB1");
                            if (smb1Value == null || (smb1Value is int value && value == 1))
                            {
                                // SMB1 не отключен явно, значит может быть доступен
                                isSmb1Available = true;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppendLog($"Ошибка проверки SMB1 через реестр: {ex.Message}");
                }

                // Метод 2: Проверка через DISM API (без PowerShell)
                if (!isSmb1Available)
                {
                    try 
                    {
                        // Запускаем DISM напрямую через процесс
                        var startInfo = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "dism.exe",
                            Arguments = "/online /get-featureinfo /featurename:SMB1Protocol-Client",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        };

                        using var process = System.Diagnostics.Process.Start(startInfo);
                        if (process != null)
                        {
                            string output = process.StandardOutput.ReadToEnd();
                            process.WaitForExit();
                            
                            if (process.ExitCode == 0 && output.Contains("State : Enabled"))
                            {
                                isSmb1Available = true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"Ошибка проверки SMB1 через DISM: {ex.Message}");
                    }
                }

                // Метод 3: Проверка доступности через попытку подключения к localhost
                if (!isSmb1Available)
                {
                    try
                    {
                        // Простая проверка: пытаемся получить информацию о SMB1 через .NET
                        var netUseInfo = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "net.exe",
                            Arguments = "use",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            CreateNoWindow = true
                        };

                        using var netProcess = System.Diagnostics.Process.Start(netUseInfo);
                        if (netProcess != null)
                        {
                            netProcess.WaitForExit();
                            // Если команда net use работает, значит базовая SMB функциональность доступна
                            if (netProcess.ExitCode == 0)
                            {
                                // Дополнительно проверим наличие файла mrxsmb10.sys
                                string smb1DriverPath = @"C:\Windows\System32\drivers\mrxsmb10.sys";
                                if (File.Exists(smb1DriverPath))
                                {
                                    isSmb1Available = true;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"Ошибка дополнительной проверки SMB1: {ex.Message}");
                    }
                }

                if (!isSmb1Available)
                {
                    var result = System.Windows.MessageBox.Show(
                        "В системе не обнаружен протокол SMB1, который может быть необходим для доступа к старым общим ресурсам.\n\n" +
                        "Хотите открыть инструкцию по включению SMB1?",
                        "Внимание: SMB1 не обнаружен",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning
                    );

                    if (result == MessageBoxResult.Yes)
                    {
                        // Открываем инструкцию по включению SMB1
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "https://learn.microsoft.com/ru-ru/windows-server/storage/file-server/troubleshoot/detect-enable-and-disable-smbv1-v2-v3",
                            UseShellExecute = true
                        });
                    }
                }
                // Убрано сообщение о доступности SMB1
            }
            catch (Exception ex)
            {
                AppendLog($"Ошибка при проверке SMB1: {ex.Message}");
            }
        }

        private void SetupNetworkCredentials()
        {
            try
            {
                // Фиксированные учетные данные и хосты
                const string username = "Administrator";
                const string password = "ultracam";
                
                // Список всех хостов UCX
                var hosts = new[]
                {
                    "WU01", "WU02", "WU03", "WU04", "WU05", "WU06", "WU07", 
                    "WU08", "WU09", "WU10", "WU11", "WU12", "WU13", "CU"
                };

                // Добавляем учетные данные для каждого хоста (тихо)
                foreach (var host in hosts)
                {
                    AddCredential(host, username, password);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"Ошибка при настройке сетевых учетных данных: {ex.Message}");
            }
        }

        private bool AddCredential(string target, string username, string password)
        {
            try
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmdkey.exe",
                    Arguments = $"/add:{target} /user:{username} /pass:{password}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = System.Diagnostics.Process.Start(startInfo);
                if (process != null)
                {
                    process.WaitForExit();
                    return process.ExitCode == 0;
                }
                return false;
            }
            catch (Exception ex)
            {
                AppendLog($"Ошибка добавления учетных данных для {target}: {ex.Message}");
                return false;
            }
        }

        private void CloseAllNetworkConnections()
        {
            try
            {
                // Список всех узлов и шар
                var nodes = new[]
                {
                    "WU01", "WU02", "WU03", "WU04", "WU05", "WU06", "WU07", 
                    "WU08", "WU09", "WU10", "WU11", "WU12", "WU13", "CU"
                };
                var shares = new[] { "E$", "F$" };

                // Закрываем все подключения к каждой шаре на каждом узле
                foreach (var node in nodes)
                {
                    foreach (var share in shares)
                    {
                        var uncPath = $"\\\\{node}\\{share}";
                        try
                        {
                            var startInfo = new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = "net.exe",
                                Arguments = $"use {uncPath} /delete /y",
                                UseShellExecute = false,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                                CreateNoWindow = true
                            };

                            using var process = System.Diagnostics.Process.Start(startInfo);
                            if (process != null)
                            {
                                process.WaitForExit(2000); // Wait max 2 seconds per connection
                            }
                        }
                        catch { /* Ignore errors for individual connections */ }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка при закрытии сетевых подключений: {ex.Message}");
            }
        }


    }
}
