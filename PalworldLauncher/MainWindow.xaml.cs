using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Diagnostics;
using Microsoft.Win32;

namespace PalworldLauncher
{
    public partial class MainWindow : Window
    {
        private bool _isBusy = false;
        private System.Threading.CancellationTokenSource _serverCheckCts;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Load local configurations
            ConfigManager.Load();

            // Auto-detect Palworld path if not already set
            if (string.IsNullOrEmpty(ConfigManager.Current.PalworldPath))
            {
                string detectedPath = LauncherLogic.TryGetGameInstallPath();
                if (!string.IsNullOrEmpty(detectedPath))
                {
                    ConfigManager.Current.PalworldPath = detectedPath;
                    LogToConsole($"[System] Auto-detected Palworld installation at: {detectedPath}");
                }
                else
                {
                    LogToConsole("[Warning] Could not auto-detect Palworld folder. Please set it manually.");
                }
            }

            // Populate UI fields
            TxtPalworldPath.Text = ConfigManager.Current.PalworldPath;
            TxtManifestUrl.Text = ConfigManager.Current.ManifestUrl;

            // Start background server status checks
            StartServerCheckLoop();
        }

        private void MainWindow_Closed(object sender, EventArgs e)
        {
            StopServerCheckLoop();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void BrowsePath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select Palworld Installation Directory",
                InitialDirectory = string.IsNullOrEmpty(TxtPalworldPath.Text) ? "C:\\" : TxtPalworldPath.Text
            };

            if (dialog.ShowDialog() == true)
            {
                TxtPalworldPath.Text = dialog.FolderName;
                LogToConsole($"[Settings] Selected path: {dialog.FolderName}");
            }
        }

        private void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            if (SaveCurrentSettings())
            {
                MessageBox.Show("Settings saved successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private bool SaveCurrentSettings()
        {
            ConfigManager.Current.PalworldPath = TxtPalworldPath.Text;
            ConfigManager.Current.ManifestUrl = TxtManifestUrl.Text;

            ConfigManager.Save();
            LogToConsole("[Settings] Launcher settings saved to local config.");

            // Restart server check loop to fetch new manifest details if changed
            StopServerCheckLoop();
            StartServerCheckLoop();

            return true;
        }

        private void LogToConsole(string message)
        {
            Dispatcher.Invoke(() =>
            {
                TxtConsoleLog.AppendText($"\n[{DateTime.Now:HH:mm:ss}] {message}");
                TxtConsoleLog.ScrollToEnd();
            });
        }

        private async void BtnPlay_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;

            // Save settings first
            if (!SaveCurrentSettings()) return;

            SetBusyState(true);
            TxtConsoleLog.Text = "[Process Started] Syncing files and launching Palworld...";

            try
            {
                string manifestUrl = TxtManifestUrl.Text;
                string gamePath = TxtPalworldPath.Text;

                if (string.IsNullOrEmpty(gamePath) || !Directory.Exists(gamePath))
                {
                    throw new DirectoryNotFoundException("Palworld installation directory not found or invalid. Please check settings.");
                }

                // 1. Fetch Remote Manifest
                UpdateStatus("Fetching mod manifest...", 5);
                LogToConsole($"[Manifest] Downloading from: {manifestUrl}");
                var manifest = await Task.Run(() => LauncherLogic.FetchManifestAsync(manifestUrl));
                LogToConsole("[Manifest] Successfully downloaded manifest.");

                // Apply fallbacks for whitelisting if manifest is missing them
                if (string.IsNullOrEmpty(manifest.AuthApiUrl) && !string.IsNullOrEmpty(manifest.ServerIp))
                {
                    manifest.AuthApiUrl = $"http://{manifest.ServerIp}:8000/allow-connection";
                    LogToConsole($"[Security] Inferred Auth API URL: {manifest.AuthApiUrl}");
                }
                if (string.IsNullOrEmpty(manifest.ApiSecret))
                {
                    manifest.ApiSecret = "MySuperPrivateLauncherKey2026!25";
                    LogToConsole("[Security] Using default API secret key.");
                }

                LogToConsole($"[Manifest] Server Target: {manifest.ServerIp}:{manifest.ServerPort}");
                LogToConsole($"[Manifest] Prescribed Mods: {manifest.Mods.Count} item(s)");

                // 1.5 Check for active Steam Workshop Mod subscriptions (Block and Warn)
                string workshopPath = Path.GetFullPath(Path.Combine(gamePath, @"..\..\workshop\content\1623730"));
                if (Directory.Exists(workshopPath))
                {
                    var workshopDirs = Directory.GetDirectories(workshopPath);
                    if (workshopDirs.Length > 0)
                    {
                        string warningMsg = "تنبيه هام / Important Warning:\n\n" +
                            "لقد تم رصد مودات محملة من ستيم وورك شوب. يرجى إلغاء الاشتراك من جميع مودات لعبة بال وورلد في ستيم لتجنب تضارب الملفات والمشاكل.\n\n" +
                            "We detected downloaded Steam Workshop mods. Please unsubscribe from all Palworld mods in the Steam Workshop to prevent file conflicts and issues.";

                        MessageBox.Show(warningMsg, "تنبيه مودات ستيم وورك شوب / Steam Workshop Mods Detected", MessageBoxButton.OK, MessageBoxImage.Warning);

                        LogToConsole("[System] Steam Workshop mods detected. Launch blocked.");

                        UpdateStatus("Launch cancelled: Steam Workshop mods detected", 0);
                        LogToConsole("[Launcher] Launch cancelled due to active Steam Workshop mod subscriptions.");
                        SetBusyState(false);
                        return;
                    }
                }

                // Legacy check for missing required Workshop Mods (retained just in case)
                var missingWorkshopIds = new List<string>();
                if (manifest.ApprovedWorkshopIds != null && manifest.ApprovedWorkshopIds.Count > 0)
                {
                    foreach (var id in manifest.ApprovedWorkshopIds)
                    {
                        string itemPath = Path.Combine(workshopPath, id);
                        if (!Directory.Exists(itemPath))
                        {
                            missingWorkshopIds.Add(id);
                        }
                    }

                    if (missingWorkshopIds.Count > 0)
                    {
                        var result = MessageBox.Show(
                            $"You are missing {missingWorkshopIds.Count} required Steam Workshop mod(s) for this server.\n\nWould you like the launcher to open their Steam Workshop pages so you can subscribe to them?",
                            "Missing Required Mods",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Question
                        );

                        if (result == MessageBoxResult.Yes)
                        {
                            string collectionId = string.IsNullOrEmpty(manifest.WorkshopCollectionId) ? "3756614372" : manifest.WorkshopCollectionId;
                            try
                            {
                                Process.Start(new ProcessStartInfo($"steam://url/CommunityFilePage/{collectionId}") { UseShellExecute = true });
                                LogToConsole($"[System] Opening Steam Workshop Collection page: {collectionId}");
                            }
                            catch (Exception ex)
                            {
                                LogToConsole($"[Warning] Failed to open Steam Collection page: {ex.Message}");
                            }
                            
                            MessageBox.Show(
                                "Please click 'Subscribe to all' (or subscribe to the missing mods manually) on the Steam Workshop Collection page that opened, wait for Steam to finish downloading the mods, and then click 'Sync & Play' again.",
                                "Subscription Required",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information
                            );
                        }
                        
                        UpdateStatus("Launch cancelled: Mods missing", 0);
                        LogToConsole("[Launcher] Launch cancelled due to missing required workshop mods.");
                        SetBusyState(false);
                        return;
                    }
                }

                // 2. Sync and Clean
                bool forceClean = false;
                if (await LauncherLogic.IsSyncRequiredAsync(gamePath, manifest))
                {
                    var result = MessageBox.Show(
                        "توجد ملفات مودات ناقصة أو لم يتم تحميلها بعد. هل ترغب في تحميل وتثبيت حزمة المودات المعتمدة الآن؟\n\n" +
                        "Some mod files are missing or have not been downloaded yet. Would you like to download and install the approved modpack now?",
                        "تثبيت المودات المطلوبة / Mod Installation Required",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question
                    );

                    if (result == MessageBoxResult.No)
                    {
                        UpdateStatus("Launch cancelled: Mod download rejected", 0);
                        LogToConsole("[Launcher] Sync cancelled because user rejected downloading mods.");
                        SetBusyState(false);
                        return;
                    }

                    forceClean = true;
                }
                else if (LauncherLogic.HasUnapprovedMods(gamePath, manifest))
                {
                    var result = MessageBox.Show(
                        "تم الكشف عن ملفات أو مودات إضافية غير مصرح بها. لتجنب المشاكل والتمكن من دخول السيرفر، يجب حذف هذه الملفات الآن. هل توافق على حذفها؟\n\n" +
                        "Unapproved mods or additional files have been detected. To prevent issues and connect to the server, these files must be deleted. Do you agree to delete them now?",
                        "تنبيه مودات غير مصرح بها / Unapproved Mods Detected",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning
                    );

                    if (result == MessageBoxResult.No)
                    {
                        UpdateStatus("Launch cancelled: Unapproved mods cleanup rejected", 0);
                        LogToConsole("[Launcher] Launch cancelled because user rejected deleting unapproved mods.");
                        SetBusyState(false);
                        return;
                    }
                }

                UpdateStatus("Verifying and cleaning mods...", 15);
                await Task.Run(() => LauncherLogic.SyncAndCleanAsync(gamePath, manifest, (statusText, percent) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        UpdateStatus(statusText, percent);
                    });
                }, forceClean));

                // 2.5 Server Knocking (if configured)
                if (!string.IsNullOrEmpty(manifest.AuthApiUrl))
                {
                    UpdateStatus("Authenticating with server...", 85);
                    LogToConsole("[Security] Getting active Steam ID...");
                    string steamId = LauncherLogic.GetActiveSteamId64();
                    if (string.IsNullOrEmpty(steamId))
                    {
                        throw new Exception("Steam must be running and you must be logged in to connect to the server.");
                    }
                    LogToConsole($"[Security] Steam ID found: {steamId}");
                    LogToConsole("[Security] Sending authentication knock to server...");
                    await Task.Run(() => LauncherLogic.KnockServerApiAsync(manifest.AuthApiUrl, manifest.ApiSecret, steamId));
                    LogToConsole("[Security] Authentication success! Whitelist lease established.");
                }

                // 3. Launch game
                UpdateStatus("Launching Palworld...", 95);
                LogToConsole("[Launcher] Starting Palworld shipping executable...");

                string ip = manifest.ServerIp;
                int port = manifest.ServerPort;
                string pwd = manifest.ServerPassword;
                bool autoConnect = true;

                // Copy the connection IP:Port to the clipboard for easy pasting since Palworld doesn't support command-line connect
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        Clipboard.SetText($"{ip}:{port}");
                        LogToConsole($"[System] Copied server address ({ip}:{port}) to clipboard!");
                        LogToConsole("[System] In-game: Click 'Join Multiplayer Game', paste it (Ctrl+V) in the direct connect box at the bottom, and click 'Connect'.");
                        if (!string.IsNullOrEmpty(pwd))
                        {
                            LogToConsole($"[System] Server Password is: {pwd} (Please enter it if prompted).");
                        }
                    }
                    catch (Exception ex)
                    {
                        LogToConsole($"[Warning] Failed to copy server IP to clipboard: {ex.Message}");
                    }
                });

                // Launch via Steam connect URI
                await Task.Run(() => LauncherLogic.LaunchPalworld(gamePath, ip, port, pwd, autoConnect));

                LogToConsole("[Launcher] Waiting for Palworld process to start...");
                System.Diagnostics.Process? actualGameProcess = null;
                for (int i = 0; i < 40; i++) // Check for 40 seconds
                {
                    var processes = System.Diagnostics.Process.GetProcessesByName("Palworld-Win64-Shipping");
                    if (processes.Length > 0)
                    {
                        actualGameProcess = processes[0];
                        break;
                    }
                    var processesFallback = System.Diagnostics.Process.GetProcessesByName("Palworld");
                    if (processesFallback.Length > 0)
                    {
                        actualGameProcess = processesFallback[0];
                        break;
                    }
                    await Task.Delay(1000);
                }

                if (actualGameProcess != null)
                {
                    LogToConsole("[Launcher] Game started. Launcher will hide and monitor integrity in background...");
                    
                    // Hide the launcher window
                    Dispatcher.Invoke(() => { this.Hide(); });

                    // Run the heartbeat and integrity check loop in background
                    await Task.Run(async () =>
                    {
                        try
                        {
                            while (!actualGameProcess.HasExited)
                            {
                                // 1. Check Process Integrity & Loaded Modules
                                bool isClean = LauncherLogic.CheckProcessIntegrity(actualGameProcess, manifest.DllCleanupList);
                                if (!isClean)
                                {
                                    LogToConsole($"[Security] UNAPPROVED DLL DETECTED: {LauncherLogic.LastViolationInfo}");
                                    LogToConsole("[Security] Terminating game session...");
                                    try { actualGameProcess.Kill(); } catch { }
                                    break;
                                }

                                // 2. Send Heartbeat to server API (only if AuthApiUrl is set)
                                if (!string.IsNullOrEmpty(manifest.AuthApiUrl))
                                {
                                    try
                                     {
                                         string steamId = LauncherLogic.GetActiveSteamId64();
                                         if (!string.IsNullOrEmpty(steamId))
                                         {
                                             await LauncherLogic.KnockServerApiAsync(manifest.AuthApiUrl, manifest.ApiSecret, steamId);
                                         }
                                     }
                                    catch (Exception ex)
                                    {
                                        System.Diagnostics.Debug.WriteLine($"Heartbeat failed: {ex.Message}");
                                    }
                                }

                                // Wait 10 seconds before next check/heartbeat
                                await Task.Delay(10000);
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Monitoring error: {ex.Message}");
                        }
                        finally
                        {
                            // Restore launcher window once game exits
                            Dispatcher.Invoke(() =>
                            {
                                this.Show();
                                this.WindowState = WindowState.Normal;
                                this.Activate();
                            });
                        }
                    });

                    LogToConsole("[Launcher] Game session ended. Restored launcher window.");
                    UpdateStatus("Ready to Play", 100);
                }
                else
                {
                    LogToConsole("[Warning] Could not detect running Palworld game process within 40 seconds.");
                    UpdateStatus("Ready to Play", 100);
                }
            }
            catch (Exception ex)
            {
                LogToConsole($"[ERROR] {ex.Message}");
                UpdateStatus($"Failed: {ex.Message}", 0);
                MessageBox.Show($"Launcher Error:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusyState(false);
            }
        }

        private void UpdateStatus(string statusText, double percent)
        {
            TxtStatus.Text = statusText;
            PrgProgress.Value = percent;
            TxtPercent.Text = $"{percent:F0}%";
            if (percent > 0 && percent < 100)
            {
                LogToConsole($"[Sync] {statusText} ({percent:F0}%)");
            }
        }

        private string _cachedServerIp = "";
        private int _cachedServerPort = 8211;
        private int _cachedAuthPort = 8000;

        private void SetBusyState(bool busy)
        {
            _isBusy = busy;
            BtnPlay.IsEnabled = !busy;
            TxtPalworldPath.IsEnabled = !busy;
            TxtManifestUrl.IsEnabled = !busy;
        }

        #region Server Check Loop

        private void StartServerCheckLoop()
        {
            _serverCheckCts = new System.Threading.CancellationTokenSource();
            Task.Run(() => ServerCheckLoopAsync(_serverCheckCts.Token));
        }

        private void StopServerCheckLoop()
        {
            _serverCheckCts?.Cancel();
        }

        private async Task ServerCheckLoopAsync(System.Threading.CancellationToken token)
        {
            // First, fetch the manifest once to retrieve the latest Server IP and Port
            try
            {
                string manifestUrl = "";
                Dispatcher.Invoke(() => { manifestUrl = TxtManifestUrl.Text; });

                if (!string.IsNullOrEmpty(manifestUrl))
                {
                    var manifest = await LauncherLogic.FetchManifestAsync(manifestUrl);
                    _cachedServerIp = manifest.ServerIp;
                    _cachedServerPort = manifest.ServerPort;

                    // Extract TCP API Auth Port from manifest AuthApiUrl
                    if (!string.IsNullOrEmpty(manifest.AuthApiUrl) && Uri.TryCreate(manifest.AuthApiUrl, UriKind.Absolute, out var uri))
                    {
                        _cachedAuthPort = uri.Port;
                    }
                    else if (!string.IsNullOrEmpty(manifest.ServerIp))
                    {
                        _cachedAuthPort = 8000; // default fallback port
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to retrieve manifest for server status check: {ex.Message}");
            }

            while (!token.IsCancellationRequested)
            {
                string ip = _cachedServerIp;
                int port = _cachedServerPort;

                if (!string.IsNullOrEmpty(ip))
                {
                    bool isOnline = await ServerQuery.CheckServerStatusAsync(ip, port, _cachedAuthPort, 1500);

                    Dispatcher.Invoke(() =>
                    {
                        if (isOnline)
                        {
                            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x00, 0xFF, 0x66)); // AccentGreen
                            TxtServerStatus.Text = "Server Online";
                            TxtServerStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xFF, 0x66));
                        }
                        else
                        {
                            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x33, 0x66)); // AccentRed
                            TxtServerStatus.Text = "Server Offline / Unreachable";
                            TxtServerStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8E, 0x8E));
                        }
                    });
                }

                try
                {
                    await Task.Delay(10000, token); // Check every 10 seconds
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }

        #endregion
    }
}