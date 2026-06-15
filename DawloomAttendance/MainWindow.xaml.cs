using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using DawloomAttendance.Data;
using DawloomAttendance.Data.Entities;
using DawloomAttendance.Device;
using DawloomAttendance.Device.Events;
using DawloomAttendance.Services;

namespace DawloomAttendance
{
    /// <summary>
    /// Phase 1 surface. Point at a K70, probe TCP reachability, and start a
    /// self-healing connection (connect → keep-alive → auto-reconnect) that captures
    /// live punches, persists them, and backfills device-stored records on each
    /// (re)connect — with a status indicator, live feed, and log.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly ZkDeviceClient _device = new ZkDeviceClient();
        private readonly ZkConnectionMonitor _monitor;
        private AppDb _db;
        private DeviceSettings _settings;       // current device target, edited via the Settings dialog
        private int _machineNumber = 1;         // captured when the monitor starts (avoids UI access off-thread)
        private bool _connectErrorShown;        // so we pop the error dialog once per connect attempt, not every retry

        public MainWindow()
        {
            InitializeComponent();

            _settings = DeviceSettings.Load();
            UpdateTargetText();

            InitDatabase();

            _device.StateChanged += OnStateChanged;
            _device.PunchReceived += OnPunchReceived;

            _monitor = new ZkConnectionMonitor(_device);
            _monitor.Log += OnMonitorLog;
            _monitor.ConnectedOrReconnected += OnConnectedOrReconnected;

            ApplyState(_device.State);

            Log($"File log: {LoggingSetup.LogDirectory}");
            Log(_device.SdkAvailable
                ? "ZKTeco SDK is registered on this PC."
                : "ZKTeco SDK is NOT registered. TCP Probe still works; install the SDK for a full connect.");

            Closed += (_, __) => { _monitor.Dispose(); _device.Dispose(); };   // stop loop, disconnect, end COM thread

            // Auto-connect once the window is shown (so the error dialog has an owner).
            Loaded += (_, __) => StartConnection();
        }

        private void InitDatabase()
        {
            try
            {
                _db = AppDb.CreateDefault();
                Log($"Database ready: {_db.DbPath} ({_db.CountPunches()} punches stored).");
                PreloadFeed();

                try
                {
                    if (Services.BackupService.RunDailyIfNeeded(_db))
                        Log("Daily backup created: " + Services.BackupService.GetBackupDir(_db));
                }
                catch (Exception bex) { Log("[WARN] Daily backup failed: " + bex.Message); }
            }
            catch (Exception ex)
            {
                // Persistence failure must not stop the connection tester from running.
                Log("[WARN] Database unavailable — punches will display but not be saved: " + ex.Message);
            }
        }

        private void PreloadFeed()
        {
            // GetRecentPunches returns newest-first; appending keeps newest on top.
            foreach (var p in _db.GetRecentPunches(20))
                FeedList.Items.Add(FormatPunch(p.Timestamp, p.EnrollNumber, p.AttState, p.VerifyMethod));
        }

        // ---- Sidebar navigation (swaps content in place) -----------------------------

        private void NavConnection_Click(object sender, RoutedEventArgs e) => ShowHome();

        private void NavDashboard_Click(object sender, RoutedEventArgs e)
        {
            if (_db == null) { Log("[FAIL] Database unavailable."); return; }
            ShowPage(new Views.DashboardWindow(_db, _device));
        }

        private void NavEmployees_Click(object sender, RoutedEventArgs e)
        {
            if (_db == null) { Log("[FAIL] Database unavailable."); return; }
            ShowPage(new Views.EmployeesWindow(_db, _device, _settings.MachineNumber));
        }

        private void NavHolidays_Click(object sender, RoutedEventArgs e)
        {
            if (_db == null) { Log("[FAIL] Database unavailable."); return; }
            ShowPage(new Views.HolidaysWindow(_db));
        }

        private void NavLeaves_Click(object sender, RoutedEventArgs e)
        {
            if (_db == null) { Log("[FAIL] Database unavailable."); return; }
            ShowPage(new Views.LeavesWindow(_db));
        }

        private void NavAttendance_Click(object sender, RoutedEventArgs e)
        {
            if (_db == null) { Log("[FAIL] Database unavailable."); return; }
            ShowPage(new Views.AttendanceWindow(_db));
        }

        private void NavReports_Click(object sender, RoutedEventArgs e)
        {
            if (_db == null) { Log("[FAIL] Database unavailable."); return; }
            ShowPage(new Views.ReportsView(_db));
        }

        private void NavBackup_Click(object sender, RoutedEventArgs e)
        {
            if (_db == null) { Log("[FAIL] Database unavailable."); return; }
            ShowPage(new Views.BackupView(_db));
        }

        private void ShowHome()
        {
            PageHost.Content = null;
            PageHost.Visibility = Visibility.Collapsed;
            HomePanel.Visibility = Visibility.Visible;
        }

        private void ShowPage(System.Windows.Controls.UserControl view)
        {
            HomePanel.Visibility = Visibility.Collapsed;
            PageHost.Content = view;            // replacing Content unloads the previous view
            PageHost.Visibility = Visibility.Visible;
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Views.SettingsWindow(_settings) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                _settings = dialog.Result;
                _settings.Save();             // persist so a re-point survives restart
                UpdateTargetText();
                Log($"Settings saved → {_settings.Ip}:{_settings.Port}, machine #{_settings.MachineNumber}.");
            }
        }

        private void UpdateTargetText()
        {
            TargetText.Text = $"Target: {_settings.Ip}:{_settings.Port}   •   machine #{_settings.MachineNumber}   " +
                              $"•   keep-alive {_settings.KeepAliveIntervalSeconds}s   •   retry {_settings.ReconnectIntervalSeconds}s";
        }

        private async void SyncTimeButton_Click(object sender, RoutedEventArgs e)
        {
            SyncTimeButton.IsEnabled = false;
            try
            {
                int machine = _settings.MachineNumber;
                var before = await Task.Run(() => _device.GetDeviceTime(machine));
                bool ok = await Task.Run(() => _device.SyncTime(machine));
                var after = await Task.Run(() => _device.GetDeviceTime(machine));
                Log($"Sync time: device was {Fmt(before)} → PC is {DateTime.Now:yyyy-MM-dd HH:mm:ss} → device now {Fmt(after)} — {(ok ? "OK" : "FAILED")}.");
            }
            finally
            {
                SyncTimeButton.IsEnabled = _device.State == DeviceConnectionState.Connected;
            }
        }

        private static string Fmt(DateTime? t) => t.HasValue ? t.Value.ToString("yyyy-MM-dd HH:mm:ss") : "(unknown)";

        private async void ProbeButton_Click(object sender, RoutedEventArgs e)
        {
            ProbeButton.IsEnabled = false;
            Log($"TCP probe → {_settings.Ip}:{_settings.Port} …");
            try
            {
                string detail = null;
                bool ok = await Task.Run(() => ZkDeviceClient.ProbeTcp(_settings.Ip, _settings.Port, 3000, out detail));
                Log((ok ? "[OK]  " : "[FAIL] ") + detail);
            }
            finally
            {
                ProbeButton.IsEnabled = true;
            }
        }

        private void ConnectButton_Click(object sender, RoutedEventArgs e) => StartConnection();

        private void StartConnection()
        {
            if (_monitor.IsRunning) return;
            _machineNumber = _settings.MachineNumber;
            _connectErrorShown = false;   // allow one error dialog for this attempt
            Log($"Starting auto-reconnect monitor → {_settings.Ip}:{_settings.Port}, machine #{_settings.MachineNumber} " +
                $"(keep-alive {_settings.KeepAliveIntervalSeconds}s, retry {_settings.ReconnectIntervalSeconds}s) …");

            _monitor.Start(_settings);   // background loop drives connect / keep-alive / reconnect
            UpdateButtons(_device.State);
        }

        private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            DisconnectButton.IsEnabled = false;
            Log("Stopping monitor …");
            await Task.Run(() => _monitor.Stop());   // Stop can block briefly; keep the UI responsive
            Log("Monitor stopped.");
            UpdateButtons(_device.State);
        }

        private async void BackfillButton_Click(object sender, RoutedEventArgs e)
        {
            if (_db == null) { Log("[FAIL] Database unavailable — cannot backfill."); return; }

            BackfillButton.IsEnabled = false;
            Log("Backfill: reading stored transactions from device …");
            try
            {
                var result = await Task.Run(() => BackfillService.Run(_device, _db, _settings.MachineNumber));
                Log($"[OK]  Backfill complete — {result}.");
                LastBackfillText.Text = $"Last backfill: {DateTime.Now:yyyy-MM-dd HH:mm:ss} (+{result.Inserted} new)";
                _db.InsertDeviceLog("Info", "Backfill", result.ToString());

                // Show any newly merged rows in the feed.
                FeedList.Items.Clear();
                PreloadFeed();
            }
            catch (Exception ex)
            {
                Log("[FAIL] Backfill error: " + ex.Message);
            }
            finally
            {
                BackfillButton.IsEnabled = _device.State == DeviceConnectionState.Connected;
            }
        }

        // Raised on the device's COM thread → persist here (off the UI thread), then marshal UI.
        private void OnPunchReceived(PunchEvent punch)
        {
            bool saved = true;
            try
            {
                if (_db != null)
                {
                    saved = _db.InsertPunchIfNew(new RawPunch
                    {
                        EnrollNumber = punch.EnrollNumber,
                        Timestamp    = punch.Timestamp,
                        AttState     = punch.AttState,
                        VerifyMethod = punch.VerifyMethod,
                        WorkCode     = punch.WorkCode,
                        IsValid      = punch.IsValid,
                        CapturedAt   = DateTime.Now,
                        Source       = "live"
                    });
                }
            }
            catch (Exception ex)
            {
                Log("[WARN] Failed to save punch: " + ex.Message);
            }

            string line = FormatPunch(punch.Timestamp, punch.EnrollNumber, punch.AttState, punch.VerifyMethod);
            Dispatcher.InvokeAsync(() =>
            {
                FeedList.Items.Insert(0, line);
                while (FeedList.Items.Count > 20)
                    FeedList.Items.RemoveAt(FeedList.Items.Count - 1);
            });

            Log(saved
                ? $"Punch saved: {punch.EnrollNumber} {punch.AttStateLabel} @ {punch.Timestamp:HH:mm:ss}"
                : $"Punch (duplicate, not saved): {punch.EnrollNumber} @ {punch.Timestamp:HH:mm:ss}");
        }

        private static string FormatPunch(DateTime ts, string enroll, int attState, int verifyMethod)
        {
            // Reuse PunchEvent's label maps for consistent IN/OUT + verify-method text.
            var labels = new PunchEvent { AttState = attState, VerifyMethod = verifyMethod };
            return $"{ts:HH:mm:ss}  ENROLL#{enroll}  {labels.AttStateLabel}  {labels.VerifyMethodLabel}";
        }

        // Monitor diagnostics → file (at the event's level) + UI + DeviceLog table.
        private void OnMonitorLog(string level, string @event, string detail)
        {
            Serilog.Log.Write(LoggingSetup.Map(level), "{Event}: {Detail}", @event, detail);
            AppendUi($"[{level}] {@event}: {detail}");
            try { _db?.InsertDeviceLog(level, @event, detail); }
            catch { /* never let diagnostics logging break the app */ }

            // Pop a dialog the first time a connect fails (not on every retry).
            if (@event == "ConnectFailed" && !_connectErrorShown)
            {
                _connectErrorShown = true;
                Dispatcher.InvokeAsync(() =>
                    MessageBox.Show(this, detail, "Connection failed",
                        MessageBoxButton.OK, MessageBoxImage.Error));
            }
        }

        // Runs synchronously on the monitor thread after each successful (re)connect,
        // so the device read here is serialized with the keep-alive ping.
        private void OnConnectedOrReconnected()
        {
            if (_db == null) return;
            try
            {
                var result = BackfillService.Run(_device, _db, _machineNumber);
                Log($"[OK]  Auto-backfill on connect — {result}.");
                _db.InsertDeviceLog("Info", "Backfill", "on-connect: " + result);

                Dispatcher.InvokeAsync(() =>
                {
                    LastBackfillText.Text = $"Last backfill: {DateTime.Now:yyyy-MM-dd HH:mm:ss} (+{result.Inserted} new)";
                    FeedList.Items.Clear();
                    PreloadFeed();
                });
            }
            catch (Exception ex)
            {
                Log("[WARN] Auto-backfill failed: " + ex.Message);
            }
        }

        private void OnStateChanged(DeviceConnectionState state)
        {
            if (state == DeviceConnectionState.Connected) _connectErrorShown = false;   // re-arm for a later drop

            string level = state == DeviceConnectionState.Error ? "Error" : "Info";
            Serilog.Log.Write(LoggingSetup.Map(level), "Device state: {State} ({Detail})", state, _device.LastMessage);
            try { _db?.InsertDeviceLog(level, state.ToString(), _device.LastMessage); }
            catch { /* never let diagnostics logging break the app */ }

            Dispatcher.InvokeAsync(() => ApplyState(state));
        }

        private void ApplyState(DeviceConnectionState state)
        {
            Brush color;
            switch (state)
            {
                case DeviceConnectionState.Connected:  color = Brushes.LimeGreen; break;
                case DeviceConnectionState.Connecting: color = Brushes.Orange;    break;
                case DeviceConnectionState.Error:      color = Brushes.Red;       break;
                default:                               color = Brushes.Gray;      break;
            }
            StatusDot.Fill = color;
            StatusText.Text = state.ToString();

            // Surface the real reason (e.g. SDK error / "not registered") next to the status.
            StatusDetail.Text = _device.LastMessage;
            StatusDetail.Foreground = state == DeviceConnectionState.Error ? Brushes.Red : Brushes.Gray;

            UpdateButtons(state);
        }

        private void UpdateButtons(DeviceConnectionState state)
        {
            bool running = _monitor != null && _monitor.IsRunning;
            bool connected = state == DeviceConnectionState.Connected;
            ConnectButton.IsEnabled = !running;                              // start only when stopped
            DisconnectButton.IsEnabled = running;                           // stop only when running
            SettingsButton.IsEnabled = !running;                            // don't edit target mid-connection
            BackfillButton.IsEnabled = connected;
            SyncTimeButton.IsEnabled = connected;
        }

        // General log line: shown in the UI and written to the rolling file (Information).
        private void Log(string message)
        {
            Serilog.Log.Information("{LogLine}", message);
            AppendUi(message);
        }

        // UI-only append (used when the caller logs to Serilog at a specific level itself).
        private void AppendUi(string message)
        {
            string line = $"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}";
            Dispatcher.InvokeAsync(() =>
            {
                LogBox.AppendText(line);
                LogBox.ScrollToEnd();
            });
        }
    }
}
