using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

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
    private PerformanceCounter? _diskCounter;
    private PerformanceCounter? _netCounter;
    private DispatcherTimer? _perfTimer;

        public MainWindow()
        {
            InitializeComponent();

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
            IdleText.Text = _settings.IdleMinutes.ToString();
            ThreadsText.Text = _settings.RobocopyThreads.ToString();


            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _uiTimer.Tick += UiTimer_Tick;
            _uiTimer.Start();

            SetupPerformanceCounters();

            this.Closing += MainWindow_Closing;
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            // on close persist current settings
            try
            {
                _settings.LastProject = ProjectCombo.Text;
                _settings.DestRoot = DestRootText.Text;
                if (int.TryParse(IdleText.Text, out int idle)) _settings.IdleMinutes = idle;
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
                _diskCounter?.Dispose();
                _netCounter?.Dispose();
            }
            catch { }
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

            // persist last used values
            _settings.LastProject = project;
            _settings.DestRoot = dest;
            if (int.TryParse(IdleText.Text, out var idleVal)) _settings.IdleMinutes = idleVal;
            if (int.TryParse(ThreadsText.Text, out var threadsVal)) _settings.RobocopyThreads = threadsVal;
            Services.SettingsService.Save(_settings);

            if (!int.TryParse(IdleText.Text, out int idle)) idle = 5;

            StartBtn.IsEnabled = false;
            StopBtn.IsEnabled = true;

            LogText.AppendText($"Запуск синхронизации проекта {project} -> {dest}\n");

            await Task.Run(() => _syncService.Start(project, dest, idle, AppendLog, _settings.RobocopyThreads));
        }

        private void SetupPerformanceCounters()
        {
            try
            {
                _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                _diskCounter = new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total");

                // choose a network interface instance (skip ones that look like tunnels)
                var nicCat = new PerformanceCounterCategory("Network Interface");
                var instances = nicCat.GetInstanceNames()
                    .Where(n => !n.ToLower().Contains("loopback") && !n.ToLower().Contains("isatap") && !n.ToLower().Contains("teredo") && !n.ToLower().Contains("vethernet"))
                    .ToArray();
                string? netInst = instances.FirstOrDefault();
                if (netInst != null)
                {
                    _netCounter = new PerformanceCounter("Network Interface", "Bytes Total/sec", netInst);
                }

                _perfTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _perfTimer.Tick += (s, e) =>
                {
                    try
                    {
                        float cpu = _cpuCounter?.NextValue() ?? 0f;
                        float disk = _diskCounter?.NextValue() ?? 0f;
                        float netBytes = _netCounter?.NextValue() ?? 0f;

                        CpuBar.Value = Math.Clamp(cpu, 0, 100);
                        CpuLabel.Text = $"{cpu:F1}%";

                        DiskBar.Value = Math.Clamp(disk, 0, 100);
                        DiskLabel.Text = $"{disk:F1}%";

                        // compute network percent of 1 Gbit
                        var netPct = (netBytes * 8.0) / 1_000_000_000.0 * 100.0;
                        NetBar.Value = Math.Clamp((float)netPct, 0, 100);
                        NetLabel.Text = $"{(netBytes / 1024.0 / 1024.0):F2} MB/s ({netPct:F1}% of 1Gbps)";

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
                _cpuCounter.NextValue();
                _diskCounter.NextValue();
                _netCounter?.NextValue();
                _perfTimer.Start();
            }
            catch (Exception ex)
            {
                AppendLog("Perf counters unavailable: " + ex.Message);
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

        private void AppendLog(string text)
        {
            Dispatcher.Invoke(() =>
            {
                LogText.AppendText(text + "\n");
                LogText.ScrollToEnd();
            });
        }
    }
}
