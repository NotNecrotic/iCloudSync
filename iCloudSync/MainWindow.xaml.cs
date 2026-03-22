using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ICloudSync
{
    public partial class MainWindow : Window
    {
        private DispatcherTimer _scheduler;
        private CancellationTokenSource _cts;
        private string _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        private bool _isExplicitExit = false;
        private readonly string[] _photoExts = { ".jpg", ".jpeg", ".png", ".heic", ".webp", ".bmp" };

        public MainWindow()
        {
            InitializeComponent();
            LoadConfig();
            SetupScheduler();
            _ = UpdateStorageStats(); // Run initial calculation
        }

        #region Sync Engine (Mirror Mode)
        private async Task RunSyncProcess()
        {
            if (!Directory.Exists(ICloudPathBox.Text) || !Directory.Exists(SyncPathBox.Text))
            {
                StatusText.Text = "Status: Invalid Paths";
                return;
            }

            SetUiState(isSyncing: true);
            _cts = new CancellationTokenSource();

            try
            {
                string source = ICloudPathBox.Text;
                string dest = SyncPathBox.Text;

                var sourceFiles = Directory.GetFiles(source, "*.*", SearchOption.AllDirectories);
                SyncProgressBar.Maximum = sourceFiles.Length;

                await Task.Run(() =>
                {
                    // 1. Forward Sync (Copy & Overwrite)
                    int count = 0;
                    foreach (var srcFile in sourceFiles)
                    {
                        if (_cts.Token.IsCancellationRequested) return;

                        string relPath = Path.GetRelativePath(source, srcFile);
                        string destFile = Path.Combine(dest, relPath);

                        Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
                        File.Copy(srcFile, destFile, true); 

                        count++;
                        Dispatcher.Invoke(() => {
                            SyncProgressBar.Value = count;
                            StatusText.Text = $"Status: Mirroring {count}/{sourceFiles.Length}";
                        });
                    }

                    // 2. Cleanup Phase (Delete from destination if missing in source)
                    Dispatcher.Invoke(() => StatusText.Text = "Status: Finalizing Mirror...");
                    var destFiles = Directory.GetFiles(dest, "*.*", SearchOption.AllDirectories);
                    foreach (var dFile in destFiles)
                    {
                        if (_cts.Token.IsCancellationRequested) return;
                        string rel = Path.GetRelativePath(dest, dFile);
                        if (!File.Exists(Path.Combine(source, rel)))
                        {
                            File.Delete(dFile);
                        }
                    }
                }, _cts.Token);

                if (!_cts.Token.IsCancellationRequested)
                {
                    StatusText.Text = "Status: Mirror Complete";
                    LastSyncText.Text = $"Last Sync: {DateTime.Now:HH:mm:ss}";
                    _ = UpdateStorageStats();
                }
            }
            catch (Exception ex)
            {
                // Only notify of errors as requested
                MessageBox.Show($"Sync Error: {ex.Message}", "Mirror Error", MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "Status: Error Occurred";
            }
            finally
            {
                SetUiState(isSyncing: false);
                _cts?.Dispose();
            }
        }
        #endregion

        #region Storage Stats Logic
        private async Task UpdateStorageStats()
        {
            string source = ICloudPathBox.Text;
            if (!Directory.Exists(source)) return;

            await Task.Run(() =>
            {
                try
                {
                    var files = Directory.GetFiles(source, "*.*", SearchOption.AllDirectories)
                                         .Select(f => new FileInfo(f)).ToList();

                    long totalBytes = files.Sum(f => f.Length);
                    long photoBytes = files.Where(f => _photoExts.Contains(f.Extension.ToLower()))
                                           .Sum(f => f.Length);

                    Dispatcher.Invoke(() => {
                        TotalSizeText.Text = FormatBytes(totalBytes);
                        PhotoSizeText.Text = FormatBytes(photoBytes);
                        FileCountText.Text = $"Files: {files.Count}";
                    });
                }
                catch { /* Access errors handled silently */ }
            });
        }

        private string FormatBytes(long bytes)
        {
            string[] Suffix = { "B", "KB", "MB", "GB", "TB" };
            int i; double dblSByte = bytes;
            for (i = 0; i < Suffix.Length && bytes >= 1024; i++, bytes /= 1024) dblSByte = bytes / 1024.0;
            return $"{dblSByte:0.##} {Suffix[i]}";
        }
        #endregion

        #region Configuration & Startup
        private void LoadConfig()
        {
            if (File.Exists(_configPath))
            {
                try {
                    var config = JsonSerializer.Deserialize<SyncConfig>(File.ReadAllText(_configPath));
                    if (config == null) return;
                    ICloudPathBox.Text = config.ICloudPath;
                    SyncPathBox.Text = config.SyncPath;
                    IntervalInput.Text = config.IntervalMinutes.ToString();
                    ScheduledSyncToggle.IsChecked = config.IsScheduled;
                    StartupToggle.IsChecked = config.RunAtStartup;
                } catch { }
            }
        }

        private void SaveConfig()
        {
            var config = new SyncConfig {
                ICloudPath = ICloudPathBox.Text,
                SyncPath = SyncPathBox.Text,
                IntervalMinutes = int.TryParse(IntervalInput.Text, out int m) ? m : 30,
                IsScheduled = ScheduledSyncToggle.IsChecked ?? false,
                RunAtStartup = StartupToggle.IsChecked ?? false
            };
            File.WriteAllText(_configPath, JsonSerializer.Serialize(config));
        }

        private void StartupToggle_Changed(object sender, RoutedEventArgs e)
        {
            try {
                using RegistryKey key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true)!;
                if (StartupToggle.IsChecked == true) key.SetValue("iCloudSyncer", Environment.ProcessPath!);
                else key.DeleteValue("iCloudSyncer", false);
                SaveConfig();
            } catch { }
        }
        
        private void SaveConfig_Event(object sender, EventArgs e) => SaveConfig();
        #endregion

        #region UI Event Handlers
        private void BrowseICloud_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog();
            if (dialog.ShowDialog() == true) { 
                ICloudPathBox.Text = dialog.FolderName; 
                SaveConfig(); 
                _ = UpdateStorageStats(); 
            }
        }

        private void BrowseSync_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog();
            if (dialog.ShowDialog() == true) { 
                SyncPathBox.Text = dialog.FolderName; 
                SaveConfig(); 
            }
        }

        private async void SyncNow_Click(object sender, RoutedEventArgs e) => await RunSyncProcess();

        private void CancelSync_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

        private void SetUiState(bool isSyncing)
        {
            SyncNow.IsEnabled = !isSyncing;
            CancelSync.IsEnabled = isSyncing;
            ICloudPathBox.IsEnabled = !isSyncing;
            SyncPathBox.IsEnabled = !isSyncing;
            if (!isSyncing) SyncProgressBar.Value = 0;
        }

        private void SetupScheduler()
        {
            _scheduler = new DispatcherTimer();
            _scheduler.Tick += async (s, e) => {
                if (ScheduledSyncToggle.IsChecked == true && SyncNow.IsEnabled)
                    await RunSyncProcess();
            };
            _scheduler.Interval = TimeSpan.FromMinutes(int.TryParse(IntervalInput.Text, out int m) ? m : 30);
            _scheduler.Start();
        }
        #endregion

        #region Tray & Exit Logic
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_isExplicitExit)
            {
                e.Cancel = true;
                this.Hide();
            }
            base.OnClosing(e);
        }

        private void ShowApp_Click(object sender, RoutedEventArgs e)
        {
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();
        }

        private void ExitApp_Click(object sender, RoutedEventArgs e)
        {
            _isExplicitExit = true;
            Application.Current.Shutdown();
        }
        #endregion
    }

    public class SyncConfig
    {
        public string ICloudPath { get; set; } = "";
        public string SyncPath { get; set; } = "";
        public int IntervalMinutes { get; set; } = 30;
        public bool IsScheduled { get; set; } = false;
        public bool RunAtStartup { get; set; } = false;
    }
}