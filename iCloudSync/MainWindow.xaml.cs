using System;
using System.IO;
using System.Collections.Generic;
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
        private DispatcherTimer _scheduler = new();
        private CancellationTokenSource? _cts;
        private string _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        private bool _isExplicitExit = false;
        private int _secondsRemaining;

        public MainWindow()
        {
            InitializeComponent();
            LoadConfig();
            SetupScheduler();
            _ = UpdateStorageStats(); 
        }

        #region Sync Engine (Dual-Source Mirror Mode)
        private async Task RunSyncProcess()
        {
            SetUiState(isSyncing: true);
            
            // 1. CAPTURE UI VALUES (On the UI Thread)
            // We must read these strings here because the background thread cannot touch UI objects
            string pSource = PhotosSourceBox.Text;
            string pDest = PhotosDestBox.Text;
            string fSource = FilesSourceBox.Text;
            string fDest = FilesDestBox.Text;

            _cts = new CancellationTokenSource();

            try
            {
                // 2. Define the jobs using the captured strings and target labels
                var syncJobs = new List<(string Source, string Dest, string Name, TextBlock SizeLabel)>
                {
                    (pSource, pDest, "Photos", PhotoSizeText),
                    (fSource, fDest, "Files", FileSizeText)
                };

                await Task.Run(() =>
                {
                    foreach (var job in syncJobs)
                    {
                        // Safety check for empty or invalid paths
                        if (string.IsNullOrWhiteSpace(job.Source) || !Directory.Exists(job.Source)) continue;
                        if (string.IsNullOrWhiteSpace(job.Dest)) continue;

                        Directory.CreateDirectory(job.Dest);
                        var sourceFiles = Directory.GetFiles(job.Source, "*.*", SearchOption.AllDirectories);
                        int totalFiles = sourceFiles.Length;
                        int count = 0;

                        foreach (var srcFile in sourceFiles)
                        {
                            if (_cts.Token.IsCancellationRequested) return;

                            string relPath = Path.GetRelativePath(job.Source, srcFile);
                            string destFile = Path.Combine(job.Dest, relPath);
                            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);

                            var fiSrc = new FileInfo(srcFile);
                            var fiDest = new FileInfo(destFile);

                            // Logic: Only copy if file is missing, size changed, or timestamp changed
                            if (!fiDest.Exists || fiSrc.Length != fiDest.Length || fiSrc.LastWriteTime != fiDest.LastWriteTime)
                            {
                                File.Copy(srcFile, destFile, true);
                            }

                            count++;
                            
                            // Update UI every 10 files to maintain high performance
                            if (count % 10 == 0 || count == totalFiles)
                            {
                                double progress = (double)count / totalFiles * 100;
                                
                                // Calculate sizes in the background thread
                                long currentJobSize = GetDirSize(job.Dest); 
                                
                                // Calculate the "other" folder size using captured destination paths
                                string otherDestPath = (job.Name == "Photos") ? fDest : pDest;
                                long otherJobSize = GetDirSize(otherDestPath);

                                // Push data to the UI thread
                                Dispatcher.Invoke(() => {
                                    StatusText.Text = $"Status: Syncing {job.Name} ({count}/{totalFiles})";
                                    SyncProgressBar.Value = progress;
                                    
                                    // Update individual label and the total odometer
                                    job.SizeLabel.Text = FormatBytes(currentJobSize);
                                    TotalSizeText.Text = FormatBytes(currentJobSize + otherJobSize);
                                });
                            }
                        }

                        // Mirroring Cleanup: Remove files from Destination that no longer exist in Source
                        var destFiles = Directory.GetFiles(job.Dest, "*.*", SearchOption.AllDirectories);
                        foreach (var dFile in destFiles)
                        {
                            string rel = Path.GetRelativePath(job.Dest, dFile);
                            if (!File.Exists(Path.Combine(job.Source, rel))) 
                            {
                                try { File.Delete(dFile); } catch { /* File may be in use */ }
                            }
                        }
                    }
                }, _cts.Token);

                if (!_cts.Token.IsCancellationRequested)
                {
                    StatusText.Text = "Status: All Mirrors Complete";
                    LastSyncText.Text = $"Last Sync: {DateTime.Now:HH:mm:ss}";
                    _ = UpdateStorageStats(); // Final precise refresh
                }
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "Status: Sync Cancelled";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Sync Error: {ex.Message}");
            }
            finally
            {
                SetUiState(isSyncing: false);
            }
        }
        #endregion

        #region Storage Stats Logic
        private async Task UpdateStorageStats()
        {
            // Capture paths on UI thread before entering Task.Run
            string pSrc = PhotosSourceBox.Text;
            string fSrc = FilesSourceBox.Text;

            await Task.Run(() =>
            {
                try
                {
                    long photoBytes = GetDirSize(pSrc);
                    long fileBytes = GetDirSize(fSrc);
                    int photoCount = GetFileCount(pSrc);
                    int fileCount = GetFileCount(fSrc);

                    Dispatcher.Invoke(() => {
                        PhotoSizeText.Text = FormatBytes(photoBytes);
                        FileSizeText.Text = FormatBytes(fileBytes);
                        TotalSizeText.Text = FormatBytes(photoBytes + fileBytes);
                        FileCountText.Text = $"Files: {photoCount + fileCount}";
                    });
                }
                catch { }
            });
        }

        private long GetDirSize(string path) => 
            Directory.Exists(path) ? Directory.GetFiles(path, "*.*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0;

        private int GetFileCount(string path) => 
            Directory.Exists(path) ? Directory.GetFiles(path, "*.*", SearchOption.AllDirectories).Length : 0;

        private string FormatBytes(long bytes)
        {
            string[] suffix = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            double dblSByte = bytes;

            // Use a while loop with double division to keep precision
            while (dblSByte >= 1024 && i < suffix.Length - 1)
            {
                i++;
                dblSByte /= 1024;
            }

            // Displays 2 decimal places (e.g., 0.45 MB instead of 0 MB)
            return $"{dblSByte:0.##} {suffix[i]}";
        }
        #endregion

        #region Configuration
        private void LoadConfig()
        {
            if (!File.Exists(_configPath)) return;
            try {
                var config = JsonSerializer.Deserialize<SyncConfig>(File.ReadAllText(_configPath));
                if (config == null) return;
                
                PhotosSourceBox.Text = config.PhotosSource;
                PhotosDestBox.Text = config.PhotosDest;
                FilesSourceBox.Text = config.FilesSource;
                FilesDestBox.Text = config.FilesDest;
                
                IntervalInput.Text = config.IntervalMinutes.ToString();
                ScheduledSyncToggle.IsChecked = config.IsScheduled;
                StartupToggle.IsChecked = config.RunAtStartup;
            } catch { }
        }

        private void SaveConfig()
        {
            var config = new SyncConfig {
                PhotosSource = PhotosSourceBox.Text,
                PhotosDest = PhotosDestBox.Text,
                FilesSource = FilesSourceBox.Text,
                FilesDest = FilesDestBox.Text,
                IntervalMinutes = int.TryParse(IntervalInput.Text, out int m) ? m : 30,
                IsScheduled = ScheduledSyncToggle.IsChecked ?? false,
                RunAtStartup = StartupToggle.IsChecked ?? false
            };
            File.WriteAllText(_configPath, JsonSerializer.Serialize(config));
        }
        #endregion

        #region UI Event Handlers
        private void BrowsePhotosSource_Click(object sender, RoutedEventArgs e) => HandleBrowse(PhotosSourceBox);
        private void BrowsePhotosDest_Click(object sender, RoutedEventArgs e) => HandleBrowse(PhotosDestBox);
        private void BrowseFilesSource_Click(object sender, RoutedEventArgs e) => HandleBrowse(FilesSourceBox);
        private void BrowseFilesDest_Click(object sender, RoutedEventArgs e) => HandleBrowse(FilesDestBox);

        private void HandleBrowse(TextBox target)
        {
            var dialog = new OpenFolderDialog();
            if (dialog.ShowDialog() == true) { 
                target.Text = dialog.FolderName; 
                SaveConfig(); 
                _ = UpdateStorageStats(); 
            }
        }

        private async void SyncNow_Click(object sender, RoutedEventArgs e) => await RunSyncProcess();
        private void CancelSync_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();
        private void SaveConfig_Event(object sender, EventArgs e)
        {
            SaveConfig();
            
            // Reset the countdown immediately to match the new interval
            if (int.TryParse(IntervalInput.Text, out int m))
            {
                _secondsRemaining = m * 60;
            }
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

        private void SetUiState(bool isSyncing)
        {
            SyncNow.IsEnabled = !isSyncing;
            CancelSync.IsEnabled = isSyncing;
            
            PhotosSourceBox.IsEnabled = !isSyncing;
            PhotosDestBox.IsEnabled = !isSyncing;
            FilesSourceBox.IsEnabled = !isSyncing;
            FilesDestBox.IsEnabled = !isSyncing;
            
            if (!isSyncing) SyncProgressBar.Value = 0;
        }

        private void SetupScheduler()
        {
            // 1. Get the interval from the input (default to 30 mins)
            int intervalMins = int.TryParse(IntervalInput.Text, out int m) ? m : 30;
            _secondsRemaining = intervalMins * 60;

            _scheduler.Interval = TimeSpan.FromSeconds(1); // Tick every second
            _scheduler.Tick -= Scheduler_Tick; // Prevent double-subscription
            _scheduler.Tick += Scheduler_Tick;
            _scheduler.Start();
        }

        private async void Scheduler_Tick(object? sender, EventArgs e)
        {
            // Only run if Auto-Sync is checked and we aren't already syncing
            if (ScheduledSyncToggle.IsChecked != true || !SyncNow.IsEnabled) return;

            _secondsRemaining--;

            if (_secondsRemaining <= 0)
            {
                // RESET: Get the latest interval and restart countdown
                int intervalMins = int.TryParse(IntervalInput.Text, out int m) ? m : 30;
                _secondsRemaining = intervalMins * 60;
                
                await RunSyncProcess();
            }
            else
            {
                // UPDATE UI: Show countdown in the StatusText
                // Converts seconds back to a readable MM:SS format
                TimeSpan t = TimeSpan.FromSeconds(_secondsRemaining);
                string countdown = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
                
                StatusText.Text = $"Status: Idle (Next sync in {countdown})";
            }
        }
        #endregion

        #region Tray Logic
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_isExplicitExit) { e.Cancel = true; this.Hide(); }
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
        public string PhotosSource { get; set; } = "";
        public string PhotosDest { get; set; } = "";
        public string FilesSource { get; set; } = "";
        public string FilesDest { get; set; } = "";
        public int IntervalMinutes { get; set; } = 30;
        public bool IsScheduled { get; set; } = false;
        public bool RunAtStartup { get; set; } = false;
    }
}