using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Forms;

using UCXSyncTool.Services;
using UCXSyncTool.Models;

namespace UCXSyncTool
{
    public partial class MainWindow : Window
    {
        private readonly FileSyncService _syncService;
        private readonly PerformanceMonitoringService _perfMonService;
        private readonly NetworkCredentialsService _credService;
        private readonly DispatcherTimer _uiTimer;
        private readonly DispatcherTimer _perfTimer;
        private readonly ObservableCollection<ActiveCopyViewModel> _activeList = new();
        private AppSettings _settings;

        public MainWindow()
        {
            InitializeComponent();

            // Initialize services
            _syncService = new FileSyncService();
            _perfMonService = new PerformanceMonitoringService();
            _credService = new NetworkCredentialsService(
                Configuration.Nodes,
                Configuration.Shares,
                Configuration.DefaultUsername,
                Configuration.DefaultPassword
            );

            // Setup logging
            _perfMonService.Initialize(AppendLog);
            _credService.SetLogger(AppendLog);

            // Check SMB1 and setup credentials
            _credService.CheckSmb1Protocol();
            _credService.SetupCredentials();
            
            ActiveGrid.ItemsSource = _activeList;

            // Load settings
            _settings = SettingsService.Load();
            if (_settings.CachedProjects?.Count > 0)
            {
                ProjectCombo.ItemsSource = _settings.CachedProjects;
            }
            if (!string.IsNullOrEmpty(_settings.LastProject)) ProjectCombo.Text = _settings.LastProject;
            if (!string.IsNullOrEmpty(_settings.DestRoot)) DestRootText.Text = _settings.DestRoot;
            ThreadsText.Text = _settings.MaxParallelism.ToString();

            // Setup UI timer
            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Configuration.UiUpdateIntervalSeconds) };
            _uiTimer.Tick += UiTimer_Tick;
            _uiTimer.Start();

            // Setup performance timer
            _perfTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Configuration.PerformanceUpdateIntervalSeconds) };
            _perfTimer.Tick += PerfTimer_Tick;
            _perfTimer.Start();
            
            // Setup disk monitoring if destination is already set
            if (!string.IsNullOrEmpty(_settings.DestRoot))
            {
                _perfMonService.SetTargetDisk(_settings.DestRoot);
            }

            this.Closing += MainWindow_Closing;
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            // Stop all robocopy processes first
            _syncService.Stop();
            
            // Give processes time to terminate
            System.Threading.Thread.Sleep(1000);

            // Save settings
            try
            {
                _settings.LastProject = ProjectCombo.Text;
                _settings.DestRoot = DestRootText.Text;
                if (int.TryParse(ThreadsText.Text, out var threadsVal))
                {
                    _settings.MaxParallelism = threadsVal;
                }
                
                // Capture current items in combo
                if (ProjectCombo.ItemsSource is System.Collections.IEnumerable items)
                {
                    var list = new List<string>();
                    foreach (var it in items)
                    {
                        if (it is string s) list.Add(s);
                    }
                    _settings.CachedProjects = list;
                }
                SettingsService.Save(_settings);
            }
            catch { }

            // Dispose services
            try
            {
                _perfTimer?.Stop();
                _perfMonService?.Dispose();
            }
            catch { }

            // Close all network connections
            _credService.CloseAllConnections();
        }

        private void BrowseDestBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                using var dlg = new FolderBrowserDialog();
                dlg.Description = "Выберите папку назначения";
                dlg.UseDescriptionForTitle = true;
                if (!string.IsNullOrEmpty(DestRootText.Text) && Directory.Exists(DestRootText.Text))
                {
                    dlg.SelectedPath = DestRootText.Text;
                }
                
                var res = dlg.ShowDialog();
                if (res == System.Windows.Forms.DialogResult.OK || res == System.Windows.Forms.DialogResult.Yes)
                {
                    DestRootText.Text = dlg.SelectedPath;
                    
                    // Setup disk monitoring for selected path
                    _perfMonService.SetTargetDisk(dlg.SelectedPath);
                    
                    // Persist immediately
                    try
                    {
                        _settings.DestRoot = dlg.SelectedPath;
                        SettingsService.Save(_settings);
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
            _perfMonService.SetTargetDisk(dest);

            // Persist last used values
            _settings.LastProject = project;
            _settings.DestRoot = dest;
            if (int.TryParse(ThreadsText.Text, out var threadsVal))
            {
                _settings.MaxParallelism = threadsVal;
            }
            SettingsService.Save(_settings);

            StartBtn.IsEnabled = false;
            StopBtn.IsEnabled = true;

            LogText.AppendText($"Запуск синхронизации проекта {project} -> {dest}\n");

            await Task.Run(() => _syncService.Start(project, dest, AppendLog, _settings.MaxParallelism));
        }

        private void PerfTimer_Tick(object? sender, EventArgs e)
        {
            try
            {
                var metrics = _perfMonService.GetMetrics();

                CpuBar.Value = Math.Clamp(metrics.CpuPercent, 0, 100);
                CpuLabel.Text = $"{metrics.CpuPercent:F1}%";

                DiskBar.Value = Math.Clamp((float)metrics.DiskPercent, 0, 100);
                DiskLabel.Text = $"{metrics.DiskMBps:F2} MB/s ({metrics.DiskPercent:F1}% of {Configuration.MaxDiskThroughputMBps} MB/s)";

                NetBar.Value = Math.Clamp((float)metrics.NetworkPercent, 0, 100);
                NetLabel.Text = $"{metrics.NetworkMBps:F2} MB/s ({metrics.NetworkPercent:F1}% of 1Gbps)";

                // Update destination free space
                try
                {
                    var dest = DestRootText.Text?.Trim();
                    if (!string.IsNullOrEmpty(dest))
                    {
                        var freeSpace = _perfMonService.GetDiskFreeSpace(dest);
                        if (freeSpace.HasValue)
                        {
                            DestFreeLabel.Text = $"{freeSpace.Value.FreeGB:F2} GB ({freeSpace.Value.PercentFree:F1}% свободно)";
                        }
                        else
                        {
                            DestFreeLabel.Text = "—";
                        }
                    }
                    else
                    {
                        DestFreeLabel.Text = string.Empty;
                    }
                }
                catch
                {
                    DestFreeLabel.Text = "—";
                }
            }
            catch { }
        }

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
            // Re-add credentials for all UCX hosts
            _credService.SetupCredentials();
        }
    }
}
