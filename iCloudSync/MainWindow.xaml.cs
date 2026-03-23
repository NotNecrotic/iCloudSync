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
        private LogWindow _logWindow = new();

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
            _logWindow.AppendLog(">>> Sync Started");

            // 1. DYNAMIC PATH RESOLUTION
            // If boxes are empty, we fall back to the standard iCloud paths automatically
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            
            string pSource = string.IsNullOrWhiteSpace(PhotosSourceBox.Text) 
                ? Path.Combine(userProfile, "Pictures", "iCloud Photos") : PhotosSourceBox.Text;
            
            string fSource = string.IsNullOrWhiteSpace(FilesSourceBox.Text) 
                ? Path.Combine(userProfile, "iCloudDrive") : FilesSourceBox.Text;

            string pDest = PhotosDestBox.Text;
            string fDest = FilesDestBox.Text;

            _cts = new CancellationTokenSource();

            try
            {
                var syncJobs = new List<(string Source, string Dest, string Name, TextBlock SizeLabel)>
                {
                    (pSource, pDest, "Photos", PhotoSizeText),
                    (fSource, fDest, "Files", FileSizeText)
                };

                await Task.Run(() =>
                {
                    foreach (var job in syncJobs)
                    {
                        if (_cts.Token.IsCancellationRequested) return;

                        // Validation
                        if (string.IsNullOrWhiteSpace(job.Source) || !Directory.Exists(job.Source))
                        {
                            _logWindow.AppendLog($"[Skip] {job.Name} source path not found: {job.Source}");
                            continue;
                        }
                        if (string.IsNullOrWhiteSpace(job.Dest))
                        {
                            _logWindow.AppendLog($"[Skip] {job.Name} destination not set.");
                            continue;
                        }

                        _logWindow.AppendLog($"Scanning {job.Name}...");
                        Directory.CreateDirectory(job.Dest);
                        
                        var sourceFiles = Directory.GetFiles(job.Source, "*.*", SearchOption.AllDirectories);
                        int totalFiles = sourceFiles.Length;
                        int count = 0;

                        // --- PHASE 1: COPY / UPDATE ---
                        foreach (var srcFile in sourceFiles)
                        {
                            if (_cts.Token.IsCancellationRequested) return;

                            try
                            {
                                string relPath = Path.GetRelativePath(job.Source, srcFile);
                                string destFile = Path.Combine(job.Dest, relPath);
                                
                                var fiSrc = new FileInfo(srcFile);
                                var fiDest = new FileInfo(destFile);

                                // Only copy if changed or missing
                                if (!fiDest.Exists || fiSrc.Length != fiDest.Length || fiSrc.LastWriteTime != fiDest.LastWriteTime)
                                {
                                    Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
                                    File.Copy(srcFile, destFile, true);
                                    _logWindow.AppendLog($"Copied: {relPath}");
                                }
                            }
                            catch (Exception ex)
                            {
                                _logWindow.AppendLog($"Error copying {Path.GetFileName(srcFile)}: {ex.Message}");
                            }

                            count++;
                            
                            // UI Throttling: Update progress every 10 files
                            if (count % 10 == 0 || count == totalFiles)
                            {
                                double progress = (double)count / totalFiles * 100;
                                long currentJobSize = GetDirSize(job.Dest); 
                                string otherDestPath = (job.Name == "Photos") ? fDest : pDest;
                                long otherJobSize = GetDirSize(otherDestPath);

                                Dispatcher.Invoke(() => {
                                    StatusText.Text = $"Status: Syncing {job.Name} ({count}/{totalFiles})";
                                    SyncProgressBar.Value = progress;
                                    job.SizeLabel.Text = FormatBytes(currentJobSize);
                                    TotalSizeText.Text = FormatBytes(currentJobSize + otherJobSize);
                                });
                            }
                        }

                        // --- PHASE 2: MIRROR CLEANUP ---
                        _logWindow.AppendLog($"Cleaning up {job.Name} destination...");
                        var destFiles = Directory.GetFiles(job.Dest, "*.*", SearchOption.AllDirectories);
                        foreach (var dFile in destFiles)
                        {
                            if (_cts.Token.IsCancellationRequested) return;

                            string rel = Path.GetRelativePath(job.Dest, dFile);
                            if (!File.Exists(Path.Combine(job.Source, rel))) 
                            {
                                try 
                                { 
                                    File.Delete(dFile); 
                                    _logWindow.AppendLog($"Removed: {rel} (No longer in source)");
                                } 
                                catch { /* File likely in use */ }
                            }
                        }
                    }
                }, _cts.Token);

                if (!_cts.Token.IsCancellationRequested)
                {
                    StatusText.Text = "Status: All Mirrors Complete";
                    LastSyncText.Text = $"Last Sync: {DateTime.Now:HH:mm:ss}";
                    _logWindow.AppendLog(">>> Sync Finished Successfully.");
                    _ = UpdateStorageStats(); 
                }
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "Status: Sync Cancelled";
                _logWindow.AppendLog("!!! Sync Cancelled by user.");
            }
            catch (Exception ex)
            {
                _logWindow.AppendLog($"FATAL ERROR: {ex.Message}");
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

        private void OpenLogs_Click(object sender, RoutedEventArgs e)
        {
            _logWindow.Show();
            _logWindow.Activate();
        }

        #region Configuration
        private void LoadConfig()
        {
            // Define Defaults
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string defaultPhotos = Path.Combine(userProfile, "Pictures", "iCloud Photos");
            string defaultFiles = Path.Combine(userProfile, "iCloudDrive");

            if (!File.Exists(_configPath)) 
            {
                // First time run: set defaults immediately
                PhotosSourceBox.Text = defaultPhotos;
                FilesSourceBox.Text = defaultFiles;
                return;
            }

            try {
                var config = JsonSerializer.Deserialize<SyncConfig>(File.ReadAllText(_configPath));
                if (config == null) return;
                
                // Use saved value OR default if saved value is missing/empty
                PhotosSourceBox.Text = string.IsNullOrWhiteSpace(config.PhotosSource) ? defaultPhotos : config.PhotosSource;
                PhotosDestBox.Text = config.PhotosDest;
                FilesSourceBox.Text = string.IsNullOrWhiteSpace(config.FilesSource) ? defaultFiles : config.FilesSource;
                FilesDestBox.Text = config.FilesDest;
                
                IntervalInput.Text = config.IntervalMinutes.ToString();
                ScheduledSyncToggle.IsChecked = config.IsScheduled;
                StartupToggle.IsChecked = config.RunAtStartup;
            } catch { 
                // Fallback for corrupt JSON
                PhotosSourceBox.Text = defaultPhotos;
                FilesSourceBox.Text = defaultFiles;
            }
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