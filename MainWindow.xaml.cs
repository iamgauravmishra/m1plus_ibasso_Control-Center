using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using System.Diagnostics;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using MediaColor = System.Windows.Media.Color;
using MediaBrushes = System.Windows.Media.Brushes;
using IOPath = System.IO.Path;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Buffers.Binary;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Management;
using NAudio.CoreAudioApi;
using HidSharp;

namespace DC07Pro.Wpf;

public partial class MainWindow : Window
{
    private readonly Dc07Controller _controller = new();
    private bool _loadedState;
    private bool _updatingUi;
    private readonly DispatcherTimer _dc07ApplyTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _dc07VolumeTimer = new() { Interval = TimeSpan.FromMilliseconds(12) };
    private readonly DispatcherTimer _shanlingApplyTimer = new() { Interval = TimeSpan.FromMilliseconds(25) };
    private readonly DispatcherTimer _deviceDetectTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private bool _dc07Present;
    private bool _shanlingConnected;
    private bool _autoConnecting;
    private DateTime _lastShanlingProbeUtc = DateTime.MinValue;
    private LowLevelKeyboardHook? _keyboardHook;
    private int _knobBusy;
    private readonly VolumeOsdWindow _volumeOsd = new();
    private readonly SemaphoreSlim _dc07ApplyLock = new(1, 1);
    private readonly SemaphoreSlim _dc07VolumeLock = new(1, 1);
    private readonly SemaphoreSlim _shanlingApplyLock = new(1, 1);
    private readonly Forms.NotifyIcon _trayIcon = new();
    private AppSettings _settings = AppSettings.Load();
    private bool _loadingSettings = true;
    private bool _allowClose;
    private readonly bool _launchBackground = Environment.GetCommandLineArgs().Any(a => a.Equals("--background", StringComparison.OrdinalIgnoreCase));

    public MainWindow()
    {
        InitializeComponent();
        InitializeTrayIcon();
        LoadSettingsIntoUi();
        RunAtStartupCheck.Checked += (_, _) => SaveSettingsFromUi();
        RunAtStartupCheck.Unchecked += (_, _) => SaveSettingsFromUi();
        MinimizeToTrayCheck.Checked += (_, _) => SaveSettingsFromUi();
        MinimizeToTrayCheck.Unchecked += (_, _) => SaveSettingsFromUi();
        ShowOnDeviceCheck.Checked += (_, _) => SaveSettingsFromUi();
        ShowOnDeviceCheck.Unchecked += (_, _) => SaveSettingsFromUi();
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized && MinimizeToTrayCheck.IsChecked == true)
                HideToTray();
        };

        VolumeSlider.ValueChanged += (_, _) => { VolumeValue.Text = $"{(int)VolumeSlider.Value} / 100"; if (!_updatingUi && _loadedState) { _dc07VolumeTimer.Stop(); _dc07VolumeTimer.Start(); } };
        BalanceSlider.ValueChanged += (_, _) => { BalanceValue.Text = $"{(int)BalanceSlider.Value} / 20"; ScheduleDc07Apply(); };
        FilterCombo.SelectionChanged += (_, _) => ScheduleDc07Apply();
        GainCombo.SelectionChanged += (_, _) => ScheduleDc07Apply();
        TimeoutCombo.SelectionChanged += (_, _) => ScheduleDc07Apply();
        HpFilterCheck.Checked += (_, _) => ScheduleDc07Apply();
        HpFilterCheck.Unchecked += (_, _) => ScheduleDc07Apply();
        SpdifCheck.Checked += (_, _) => ScheduleDc07Apply();
        SpdifCheck.Unchecked += (_, _) => ScheduleDc07Apply();
        RotationCheck.Checked += (_, _) => ScheduleDc07Apply();
        RotationCheck.Unchecked += (_, _) => ScheduleDc07Apply();
        HardwareKeysCheck.Checked += (_, _) => ScheduleDc07Apply();
        HardwareKeysCheck.Unchecked += (_, _) => ScheduleDc07Apply();

        ShanlingVolumeSlider.ValueChanged += (_, _) =>
        {
            ShanlingVolumeValue.Text = $"{(int)ShanlingVolumeSlider.Value}";
            if (!_updatingUi) { _shanlingApplyTimer.Stop(); _shanlingApplyTimer.Start(); }
        };
        _dc07ApplyTimer.Tick += async (_, _) => { _dc07ApplyTimer.Stop(); await ApplyDc07ChangesAsync(); };
        _dc07VolumeTimer.Tick += async (_, _) => { _dc07VolumeTimer.Stop(); await ApplyDc07VolumeFastAsync(); };
        _shanlingApplyTimer.Tick += async (_, _) => { _shanlingApplyTimer.Stop(); await ApplyShanlingVolumeAsync(); };
        _deviceDetectTimer.Tick += async (_, _) => { await DetectDc07Async(); await DetectShanlingAsync(); };
        Loaded += async (_, _) =>
        {
            _keyboardHook = new LowLevelKeyboardHook(HandleVolumeKey);
            if (_launchBackground && MinimizeToTrayCheck.IsChecked == true)
                HideToTray();
            _deviceDetectTimer.Start();
            await DetectDc07Async();
            await DetectShanlingAsync(forceProbe: true);
        };
        ShanlingIpText.LostFocus += async (_, _) => await DetectShanlingAsync(forceProbe: true);
        Closing += MainWindow_Closing;
        Closed += (_, _) => { _deviceDetectTimer.Stop(); _dc07VolumeTimer.Stop(); _keyboardHook?.Dispose(); _volumeOsd.Close(); ShanlingClient.Disconnect(); _trayIcon.Visible = false; _trayIcon.Dispose(); };
    }

    private void InitializeTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open DAC Control Center", null, (_, _) => RestoreFromTray());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { _allowClose = true; Close(); });
        _trayIcon.Text = "DAC Control Center";
        _trayIcon.Icon = Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? Drawing.SystemIcons.Application;
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        _trayIcon.Visible = false;
    }

    private void LoadSettingsIntoUi()
    {
        RunAtStartupCheck.IsChecked = _settings.RunAtStartup;
        MinimizeToTrayCheck.IsChecked = _settings.MinimizeToTray;
        ShowOnDeviceCheck.IsChecked = _settings.ShowOnDeviceDetected;
        _loadingSettings = false;
        UpdateStartupRegistration(_settings.RunAtStartup);
    }

    private void SaveSettingsFromUi()
    {
        if (_loadingSettings) return;
        _settings = new AppSettings(
            RunAtStartupCheck.IsChecked == true,
            MinimizeToTrayCheck.IsChecked == true,
            ShowOnDeviceCheck.IsChecked == true);
        _settings.Save();
        UpdateStartupRegistration(_settings.RunAtStartup);
        if (!_settings.MinimizeToTray) _trayIcon.Visible = false;
    }

    private static void UpdateStartupRegistration(bool enabled)
    {
        const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string appValue = "DAC Control Center";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(runKey, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(runKey, writable: true)
                ?? throw new InvalidOperationException("Could not open the Windows startup registry key.");
            if (!enabled) { key.DeleteValue(appValue, throwOnMissingValue: false); return; }
            string exe = IOPath.Combine(AppContext.BaseDirectory, "DAC Control Center.exe");
            if (!File.Exists(exe)) exe = Environment.ProcessPath ?? string.Empty;
            if (string.IsNullOrWhiteSpace(exe)) return;
            key.SetValue(appValue, $"\"{exe}\" --background");
        }
        catch { /* Startup registration is optional; app remains usable without it. */ }
    }

    private void HideToTray()
    {
        if (!MinimizeToTrayCheck.IsChecked.GetValueOrDefault()) return;
        _trayIcon.Visible = true;
        Hide();
        WindowState = WindowState.Normal;
    }

    private void RestoreFromTray()
    {
        _trayIcon.Visible = false;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ShowWhenDeviceAppears()
    {
        if (ShowOnDeviceCheck.IsChecked == true && !IsVisible)
            RestoreFromTray();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose && MinimizeToTrayCheck.IsChecked == true)
        {
            e.Cancel = true;
            HideToTray();
        }
    }

    // Return true only when exactly one DAC is connected. In that case the app
    // owns the key and shows the DAC's raw hardware step in its own OSD.
    // With both/neither connected, let Windows handle the key and native flyout.
    private bool HandleVolumeKey(bool up, bool alt)
    {
        bool dc = DeviceList.Local.GetHidDevices(0x2FC6, 0xF0B7).Any();
        bool sh = _shanlingConnected;
        if (!(dc ^ sh)) return false;

        int step = alt ? 5 : 1;
        if (Interlocked.Exchange(ref _knobBusy, 1) != 0) return true;
        _ = Task.Run(async () =>
        {
            try
            {
                if (dc)
                {
                    int current = (int)await Dispatcher.InvokeAsync(() => VolumeSlider.Value);
                    int next = Math.Clamp(current + (up ? step : -step), 0, 100);
                    _controller.SetVolumeFast(next);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        _updatingUi = true; VolumeSlider.Value = next; VolumeValue.Text = $"{next} / 100"; _updatingUi = false;
                        _volumeOsd.Show("iBasso DC07 Pro", next, 100, up);
                    });
                }
                else
                {
                    int max = (int)await Dispatcher.InvokeAsync(() => ShanlingVolumeSlider.Maximum);
                    int current = (int)await Dispatcher.InvokeAsync(() => ShanlingVolumeSlider.Value);
                    int next = Math.Clamp(current + (up ? step : -step), 0, max);
                    string ip = await Dispatcher.InvokeAsync(() => ShanlingIpText.Text.Trim());
                    ShanlingClient.SetVolumeFast(ip, next);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        _updatingUi = true; ShanlingVolumeSlider.Value = next; ShanlingVolumeValue.Text = $"{next}/{max}"; _updatingUi = false;
                        _volumeOsd.Show("Shanling M1 Plus", next, max, up);
                    });
                }
            }
            catch (Exception ex) { await Dispatcher.InvokeAsync(() => StatusText.Text = $"Keyboard volume: {ex.Message}"); }
            finally { Interlocked.Exchange(ref _knobBusy, 0); }
        });
        return true; // suppress native Windows OSD only when a single DAC is targeted
    }

    private async Task ApplyDc07VolumeFastAsync()
    {
        if (!_loadedState || !_dc07Present) return;
        if (!await _dc07VolumeLock.WaitAsync(0))
        {
            _dc07VolumeTimer.Stop(); _dc07VolumeTimer.Start();
            return;
        }
        int value = (int)VolumeSlider.Value;
        try
        {
            await Task.Run(() => _controller.SetVolumeFast(value));
            StatusText.Text = $"DC07 Pro volume · {value}/100";
        }
        catch (Exception ex) { StatusText.Text = $"DC07 volume update failed: {ex.Message}"; }
        finally { _dc07VolumeLock.Release(); }
        if (_dc07VolumeTimer.IsEnabled) { _dc07VolumeTimer.Stop(); _dc07VolumeTimer.Start(); }
    }

    private void ScheduleDc07Apply()
    {
        if (!_loadedState || _updatingUi) return;
        _dc07ApplyTimer.Stop();
        _dc07ApplyTimer.Start();
        StatusText.Text = "Pending changes…";
    }

    private async Task ApplyDc07ChangesAsync()
    {
        if (!_loadedState) return;
        if (!await _dc07ApplyLock.WaitAsync(0))
        {
            _dc07ApplyTimer.Interval = TimeSpan.FromMilliseconds(120);
            _dc07ApplyTimer.Stop(); _dc07ApplyTimer.Start();
            return;
        }
        _dc07ApplyTimer.Interval = TimeSpan.FromMilliseconds(100);
        int volume = (int)VolumeSlider.Value;
        int filter = FilterCombo.SelectedIndex;
        int hp = HpFilterCheck.IsChecked == true ? 1 : 0;
        int gain = GainCombo.SelectedIndex is >= 0 and <= 2 ? GainCombo.SelectedIndex : 1;
        int balance = (int)BalanceSlider.Value;
        int spdif = SpdifCheck.IsChecked == true ? 1 : 0;
        int rotation = RotationCheck.IsChecked == true ? 1 : 0;
        int keys = HardwareKeysCheck.IsChecked == true ? 1 : 0;
        int timeout = TimeoutCombo.SelectedIndex is >= 0 and <= 3 ? TimeoutCombo.SelectedIndex : 0;
        try
        {
            StatusText.Text = "Applying DC07 changes…";
            var state = await Task.Run(() => _controller.ApplySettings(volume, filter, hp, spdif, balance, gain, rotation, keys, timeout));
            ShowState(state);
            StatusText.Text = "DC07 settings synchronized · readback verified";
        }
        catch (Exception ex) { StatusText.Text = $"DC07 update failed: {ex.Message}"; }
        finally { _dc07ApplyLock.Release(); }
        if (_dc07ApplyTimer.IsEnabled) { _dc07ApplyTimer.Stop(); _dc07ApplyTimer.Start(); }
    }

    private async Task ApplyShanlingVolumeAsync()
    {
        if (!await _shanlingApplyLock.WaitAsync(0))
        {
            _shanlingApplyTimer.Interval = TimeSpan.FromMilliseconds(25);
            _shanlingApplyTimer.Stop(); _shanlingApplyTimer.Start();
            return;
        }
        _shanlingApplyTimer.Interval = TimeSpan.FromMilliseconds(25);
        string ip = ShanlingIpText.Text.Trim();
        int value = (int)ShanlingVolumeSlider.Value;
        try
        {
            ShanlingStatusText.Text = $"Setting volume to {value}…";
            await Task.Run(() => ShanlingClient.SetVolumeFast(ip, value));
            ShanlingVolumeValue.Text = $"{value}/{(int)ShanlingVolumeSlider.Maximum}";
            ShanlingStatusText.Text = $"Connected · volume {value}";
        }
        catch (Exception ex) { _updatingUi = false; ShanlingStatusText.Text = $"Volume update failed: {ex.Message}"; }
        finally { _shanlingApplyLock.Release(); }
        if (_shanlingApplyTimer.IsEnabled) { _shanlingApplyTimer.Stop(); _shanlingApplyTimer.Start(); }
    }

    private async Task DetectShanlingAsync(bool forceProbe = false)
    {
        if (_autoConnecting) return;
        // The M1 Plus is considered connected only when its USB Audio DAC
        // interface is enumerated AND SyncLink login + GetVolume succeeds.
        bool usbPresent = await Task.Run(IsShanlingUsbAudioPresent);
        if (!usbPresent)
        {
            if (_shanlingConnected) ShanlingClient.Disconnect();
            _shanlingConnected = false;
            ShanlingStatusText.Text = "Disconnected · Shanling USB DAC not detected";
            return;
        }

        if (!forceProbe && _shanlingConnected && DateTime.UtcNow - _lastShanlingProbeUtc < TimeSpan.FromSeconds(3))
            return;

        _autoConnecting = true;
        string ip = ShanlingIpText.Text.Trim();
        try
        {
            // Refresh performs authenticated SyncLink connection (if needed)
            // and a live volume request; TCP reachability alone is insufficient.
            var state = await Task.Run(() => ShanlingClient.Refresh(ip));
            _lastShanlingProbeUtc = DateTime.UtcNow;
            bool wasConnected = _shanlingConnected;
            _shanlingConnected = true;
            if (!wasConnected) ShowWhenDeviceAppears();
            // Refresh the hardware-reported maximum on every successful SyncLink
            // probe. The M1 Plus maximum is configurable (not always 90), so the
            // slider, keyboard bounds and OSD denominator must follow live state.
            // Do not overwrite the user's in-progress slider value while dragging
            // or while a debounced write is pending.
            _updatingUi = true;
            int previousMax = (int)ShanlingVolumeSlider.Maximum;
            ShanlingVolumeSlider.Maximum = Math.Max(1, state.MaxVolume);
            bool userChangingVolume = ShanlingVolumeSlider.IsMouseCaptureWithin || _shanlingApplyTimer.IsEnabled;
            if (!userChangingVolume)
                ShanlingVolumeSlider.Value = Math.Clamp(state.Volume, 0, state.MaxVolume);
            ShanlingVolumeValue.Text = $"{(int)ShanlingVolumeSlider.Value}/{state.MaxVolume}";
            _updatingUi = false;
            if (previousMax != state.MaxVolume)
                StatusText.Text = $"Shanling volume range updated · 0–{state.MaxVolume}";
            ShanlingStatusText.Text = $"Connected · USB DAC + SyncLink · {ip}";
        }
        catch (Exception ex)
        {
            ShanlingClient.Disconnect();
            _shanlingConnected = false;
            _lastShanlingProbeUtc = DateTime.UtcNow;
            ShanlingStatusText.Text = $"Disconnected · USB detected, SyncLink unavailable ({ex.Message})";
        }
        finally { _autoConnecting = false; }
    }

    private static bool IsShanlingUsbAudioPresent()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT PNPDeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE PNPDeviceID LIKE '%VID_20B1&PID_301F%'");
            using var results = searcher.Get();
            foreach (ManagementObject device in results)
            {
                using (device)
                {
                    var id = device["PNPDeviceID"]?.ToString() ?? string.Empty;
                    var error = device["ConfigManagerErrorCode"];
                    if (id.Contains("VID_20B1&PID_301F", StringComparison.OrdinalIgnoreCase) &&
                        (error is null || Convert.ToInt32(error) == 0)) return true;
                }
            }
        }
        catch { }
        return false;
    }

    private async Task DetectDc07Async()
    {
        bool present = await Task.Run(() => DeviceList.Local.GetHidDevices(0x2FC6, 0xF0B7).Any());
        if (present && !_dc07Present)
        {
            try
            {
                StatusText.Text = "DC07 Pro detected · reading settings…";
                var state = await Task.Run(() => _controller.ReadState());
                ShowState(state); SetConnected(true);
                ShowWhenDeviceAppears();
                StatusText.Text = "DC07 Pro detected automatically · live control ready";
            }
            catch (Exception ex) { _dc07Present = false; StatusText.Text = $"DC07 detected, retrying connection: {ex.Message}"; }
        }
        else if (!present && _dc07Present)
        {
            _loadedState = false; SetConnected(false);
            StatusText.Text = "DC07 Pro disconnected · waiting for device";
        }
        _dc07Present = present;
    }

    private void ShowShanlingState(ShanlingState state)
    {
        ShanlingVolumeSlider.Maximum = Math.Max(1, state.MaxVolume);
        ShanlingVolumeSlider.Value = Math.Clamp(state.Volume, 0, (int)ShanlingVolumeSlider.Maximum);
        ShanlingVolumeValue.Text = $"{state.Volume}/{state.MaxVolume}";
    }

    private void ShowState(Dc07State state)
    {
        _updatingUi = true;
        _loadedState = true;
        VolumeSlider.Value = state.Volume;
        VolumeValue.Text = $"{state.Volume} / 100";
        FilterCombo.SelectedIndex = state.Filter is >= 0 and <= 4 ? state.Filter : 4;
        HpFilterCheck.IsChecked = state.HpFilter == 1;
        FirmwareText.Text = state.Firmware ?? "Not reported";
        SpdifCheck.IsChecked = state.Spdif == 1;
        BalanceSlider.Value = state.Balance;
        BalanceValue.Text = $"{state.Balance} / 20";
        GainCombo.SelectedIndex = state.Gain is >= 0 and <= 2 ? state.Gain : 1;
        RotationCheck.IsChecked = state.Rotation == 1;
        HardwareKeysCheck.IsChecked = state.HardwareKeys == 1;
        TimeoutCombo.SelectedIndex = state.ScreenTimeout is >= 0 and <= 3 ? state.ScreenTimeout : 0;
        _updatingUi = false;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleWindowState();
            return;
        }
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeWindow_Click(object sender, RoutedEventArgs e) => ToggleWindowState();

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleWindowState()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        MaximizeWindowButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private void SetConnected(bool connected)
    {
        ConnectionDot.Fill = connected ? MediaBrushes.SeaGreen : MediaBrushes.Gray;
        ConnectionText.Text = connected ? "Connected" : "Disconnected";
    }

}


internal sealed record AppSettings(bool RunAtStartup, bool MinimizeToTray, bool ShowOnDeviceDetected)
{
    private static string SettingsPath => IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DAC Control Center", "settings.json");
    public static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new(false, true, true); }
        catch { return new(false, true, true); }
    }
    public void Save()
    {
        try
        {
            string path = SettingsPath;
            Directory.CreateDirectory(IOPath.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

internal sealed record ShanlingState(
    int MaxVolume, int Volume, bool Muted, bool LineOut,
    string Title, string Artist, string Album,
    int Position, int Duration, int PlayStatus,
    int QueueIndex, int QueueTotal, int PlayMode)
{
    public string PlayModeLabel => PlayMode switch
    {
        0 => "Sequential", 1 => "Repeat one", 2 => "Random", 3 => "Repeat all", _ => "Unknown"
    };
}

internal static class ShanlingClient
{
    private const uint LoginReq = 257, LoginResp = 258, LogoutReq = 259;
    private const uint GetModeReq = 769, GetModeResp = 770, SetModeReq = 771, SetModeResp = 772;
    private const uint GetVolumeReq = 774, GetVolumeResp = 775, SetVolumeReq = 776, SetVolumeResp = 777;
    private const uint GetInfoReq = 779, GetInfoResp = 780;
    private const uint PlayControlReq = 787, PlayControlResp = 788, SeekReq = 790;
    private const uint GetStatusReq = 803, GetStatusResp = 804;

    // Keep one authenticated SyncLink TCP session alive after Connect. The M1 Plus
    // is used in DAC mode, so this client deliberately sends volume commands only.
    private static readonly object SessionGate = new();
    private static SyncSession? _activeSession;
    private static string? _activeHost;
    private static VolumeData? _cachedVolume;

    public static ShanlingState Refresh(string host)
    {
        lock (SessionGate)
        {
            try
            {
                EnsureSession(host);
                _cachedVolume = _activeSession!.ReadVolumeState();
                return ToUiState(_cachedVolume!);
            }
            catch
            {
                CloseSession();
                throw;
            }
        }
    }

    public static void SetVolumeFast(string host, int value)
    {
        lock (SessionGate)
        {
            EnsureSession(host);
            var current = _cachedVolume ?? _activeSession!.ReadVolumeState();
            if (current.LineOut) throw new InvalidOperationException("M1 Plus is in Line Out mode; volume is fixed.");
            value = Math.Clamp(value, 0, current.Max);
            byte[] nested = ProtoInt(1, current.Max).Concat(ProtoInt(2, value)).Concat(ProtoInt(3, current.Muted ? 1 : 0)).ToArray();
            _activeSession!.Send(SetVolumeReq, ProtoBytes(1, nested));
            _cachedVolume = current with { Volume = value };
        }
    }

    public static ShanlingState SetVolume(string host, int value)
    {
        lock (SessionGate)
        {
            EnsureSession(host);
            try
            {
                // Volume max/mute state is cached from initial connect. During slider
                // movement send only SetVolume and consume its acknowledgement: no
                // extra GetVolume round trips, which makes continuous adjustment faster.
                var current = _cachedVolume ?? _activeSession!.ReadVolumeState();
                if (current.LineOut) throw new InvalidOperationException("M1 Plus is in Line Out mode; its player volume is fixed.");
                value = Math.Clamp(value, 0, current.Max);
                byte[] nested = ProtoInt(1, current.Max).Concat(ProtoInt(2, value)).Concat(ProtoInt(3, current.Muted ? 1 : 0)).ToArray();
                _activeSession.Request(SetVolumeReq, ProtoBytes(1, nested), SetVolumeResp);
                _cachedVolume = current with { Volume = value };
                return ToUiState(_cachedVolume!);
            }
            catch
            {
                // A failed TCP session must not poison the next slider adjustment.
                CloseSession();
                throw;
            }
        }
    }

    private static ShanlingState ToUiState(VolumeData v) =>
        new(v.Max, v.Volume, v.Muted, v.LineOut, "", "", "", 0, 0, 0, 0, 0, 0);

    private static void EnsureSession(string host)
    {
        if (!IPAddress.TryParse(host, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("Enter a valid IPv4 address for the Shanling M1 Plus.");
        if (_activeSession is not null && string.Equals(_activeHost, host, StringComparison.OrdinalIgnoreCase)) return;
        CloseSession();
        _activeSession = new SyncSession(address);
        _activeHost = host;
    }

    public static void Disconnect() { lock (SessionGate) CloseSession(); }

    private static void CloseSession()
    {
        try { _activeSession?.Dispose(); } catch { }
        _activeSession = null;
        _activeHost = null;
        _cachedVolume = null;
    }

    public static ShanlingState Control(string host, string action) => WithSession(host, session =>
    {
        int command = action switch { "play" => 0, "next" => 1, "previous" => 2, "pause" => 5, _ => throw new ArgumentOutOfRangeException(nameof(action)) };
        session.Request(PlayControlReq, ProtoInt(1, command), PlayControlResp);
        return session.ReadState();
    });

    public static ShanlingState Seek(string host, int position) => WithSession(host, session =>
    {
        var state = session.ReadState();
        int absolute = Math.Max(0, position);
        session.Send(SeekReq, ProtoInt(1, absolute));
        return session.ReadState();
    });

    public static ShanlingState SetMode(string host, int mode) => WithSession(host, session =>
    {
        if (mode is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(mode));
        session.Request(SetModeReq, ProtoInt(1, mode), SetModeResp);
        return session.ReadState();
    });

    private static T WithSession<T>(string host, Func<SyncSession, T> action)
    {
        if (!IPAddress.TryParse(host, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("Enter a valid IPv4 address for the Shanling M1 Plus.");
        using var session = new SyncSession(address);
        return action(session);
    }

    private sealed class SyncSession : IDisposable
    {
        private readonly TcpClient _client = new(AddressFamily.InterNetwork);
        private readonly NetworkStream _stream;
        private int _sequence = 1000;

        public SyncSession(IPAddress address)
        {
            _client.NoDelay = true; // Disable Nagle buffering for low-latency slider updates.
            _client.ReceiveTimeout = 1500;
            _client.SendTimeout = 1500;
            _client.Connect(address, 1751);
            _stream = _client.GetStream();
            Send( LoginReq, [], 99, 64);
            _ = ReadExpected(LoginResp, TimeSpan.FromSeconds(4));
        }

        public VolumeData ReadVolumeState() => ParseVolume(Request(GetVolumeReq, [], GetVolumeResp));

        public ShanlingState ReadState()
        {
            var volume = ParseVolume(Request(GetVolumeReq, [], GetVolumeResp));
            var info = ParsePlayback(Request(GetInfoReq, [], GetInfoResp));
            var status = ParseFields(Request(GetStatusReq, [], GetStatusResp));
            var mode = ParseFields(Request(GetModeReq, [], GetModeResp));
            return new ShanlingState(
                volume.Max, volume.Volume, volume.Muted, volume.LineOut,
                info.Title, info.Artist, info.Album, info.Position, info.Duration,
                GetInt(status, 1), GetInt(status, 2), GetInt(status, 3), GetInt(mode, 1));
        }

        public byte[] Request(uint command, byte[] payload, uint response)
        {
            Send(command, payload);
            return ReadExpected(response, TimeSpan.FromSeconds(5));
        }

        public void Send(uint command, byte[] payload, int? fixedSequence = null, byte priority = 64)
        {
            int seq = fixedSequence ?? _sequence++;
            byte[] header = new byte[12];
            header[0] = (byte)seq; header[1] = (byte)(seq >> 8); header[2] = (byte)(seq >> 16);
            header[3] = priority;
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), command);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), (uint)payload.Length);
            _stream.Write(header, 0, header.Length);
            if (payload.Length > 0) _stream.Write(payload, 0, payload.Length);
            _stream.Flush();
        }

        private byte[] ReadExpected(uint expected, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                byte[] header = ReadExact(12);
                uint command = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
                uint length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
                if (length > 16 * 1024 * 1024) throw new InvalidDataException($"Invalid SyncLink frame length: {length}");
                byte[] payload = ReadExact((int)length);
                if (command == expected) return payload;
                if (command == 260) throw new IOException("M1 Plus ended the SyncLink session.");
                // Unsolicited notifications are ignored here; the next refresh obtains current state.
            }
            throw new TimeoutException($"M1 Plus did not return SyncLink command {expected}.");
        }

        private byte[] ReadExact(int length)
        {
            byte[] buffer = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                int read = _stream.Read(buffer, offset, length - offset);
                if (read == 0) throw new IOException("M1 Plus closed the SyncLink connection.");
                offset += read;
            }
            return buffer;
        }

        public void Dispose()
        {
            try { Send(LogoutReq, [], 98, 0); } catch { }
            _stream.Dispose();
            _client.Dispose();
        }
    }

    private sealed record VolumeData(int Max, int Volume, bool Muted, bool LineOut);
    private sealed record PlaybackData(string Title, string Artist, string Album, int Position, int Duration);

    private static VolumeData ParseVolume(byte[] payload)
    {
        var fields = ParseFields(payload);
        if (fields.TryGetValue(1, out var nested) && nested.LastOrDefault() is byte[] bytes)
            fields = ParseFields(bytes);
        return new VolumeData(GetInt(fields, 1), GetInt(fields, 2), GetInt(fields, 3) == 1, GetInt(fields, 4) == 1);
    }

    private static PlaybackData ParsePlayback(byte[] payload)
    {
        var fields = ParseFields(payload);
        if (fields.TryGetValue(1, out var nested) && nested.LastOrDefault() is byte[] bytes)
            fields = ParseFields(bytes);
        return new PlaybackData(GetString(fields, 4), GetString(fields, 5), GetString(fields, 6),
            GetInt(fields, 15), GetInt(fields, 16));
    }

    private static Dictionary<int, List<object>> ParseFields(byte[] data)
    {
        var result = new Dictionary<int, List<object>>();
        int pos = 0;
        ulong Varint()
        {
            ulong value = 0; int shift = 0;
            while (pos < data.Length && shift <= 63)
            {
                byte b = data[pos++];
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return value;
                shift += 7;
            }
            throw new InvalidDataException("Invalid protobuf varint in SyncLink payload.");
        }
        while (pos < data.Length)
        {
            ulong key = Varint();
            int field = (int)(key >> 3), wire = (int)(key & 7);
            if (field == 0) throw new InvalidDataException("Invalid SyncLink protobuf field.");
            object value;
            switch (wire)
            {
                case 0: value = Varint(); break;
                case 1:
                    if (pos + 8 > data.Length) throw new InvalidDataException("Truncated SyncLink field.");
                    value = data.AsSpan(pos, 8).ToArray(); pos += 8; break;
                case 2:
                    int len = checked((int)Varint());
                    if (len < 0 || pos + len > data.Length) throw new InvalidDataException("Truncated SyncLink field.");
                    value = data.AsSpan(pos, len).ToArray(); pos += len; break;
                case 5:
                    if (pos + 4 > data.Length) throw new InvalidDataException("Truncated SyncLink field.");
                    value = data.AsSpan(pos, 4).ToArray(); pos += 4; break;
                default: throw new InvalidDataException($"Unsupported SyncLink wire type {wire}.");
            }
            if (!result.TryGetValue(field, out var list)) result[field] = list = [];
            list.Add(value);
        }
        return result;
    }

    private static int GetInt(Dictionary<int, List<object>> fields, int field) =>
        fields.TryGetValue(field, out var values) && values.LastOrDefault() is ulong n ? (int)Math.Min(int.MaxValue, n) : 0;

    private static string GetString(Dictionary<int, List<object>> fields, int field) =>
        fields.TryGetValue(field, out var values) && values.LastOrDefault() is byte[] bytes
            ? Encoding.UTF8.GetString(bytes).TrimEnd('\0') : "";

    private static byte[] ProtoInt(int field, int value)
    {
        if (value == 0) return [];
        return Varint((ulong)((field << 3) | 0)).Concat(Varint((ulong)value)).ToArray();
    }

    private static byte[] ProtoBytes(int field, byte[] value) =>
        Varint((ulong)((field << 3) | 2)).Concat(Varint((ulong)value.Length)).Concat(value).ToArray();

    private static byte[] Varint(ulong value)
    {
        var bytes = new List<byte>();
        while (value > 0x7F) { bytes.Add((byte)((value & 0x7F) | 0x80)); value >>= 7; }
        bytes.Add((byte)value);
        return bytes.ToArray();
    }
}

internal sealed record Dc07State(
    int Volume, int Filter, int HpFilter, int Spdif, int Balance, int Gain,
    int Rotation, int HardwareKeys, int ScreenTimeout, string? Firmware);

internal sealed class Dc07Controller
{
    private const int Vid = 0x2FC6;
    private const int Pid = 0xF0B7;

    public Dc07State ReadState()
    {
        var connection = Open();
        using var stream = connection.Stream;
        return ReadAll(connection.Device, stream);
    }

    public Dc07State ApplySettings(int volume, int filter, int hp, int spdif, int balance, int gain,
        int rotation, int hardwareKeys, int timeout)
    {
        var connection = Open();
        using var stream = connection.Stream;
        var current = ReadAll(connection.Device, stream);

        if (current.Filter != filter || current.HpFilter != hp)
        {
            Write(connection.Device, stream, [17, 32, 223, 0, (byte)filter, (byte)hp, 0, 0]);
            Thread.Sleep(180);
        }
        if (current.Spdif != spdif || current.Balance != balance || current.Gain != gain)
        {
            Write(connection.Device, stream, [25, 48, 207, 0, (byte)spdif, (byte)balance, (byte)gain, 0]);
            Thread.Sleep(180);
        }
        if (current.Rotation != rotation || current.HardwareKeys != hardwareKeys || current.ScreenTimeout != timeout)
        {
            Write(connection.Device, stream, [25, 64, 191, 0, (byte)rotation, (byte)hardwareKeys, (byte)timeout, 0]);
            Thread.Sleep(180);
        }

        var after = ReadAll(connection.Device, stream);
        if (after.Filter != filter || after.HpFilter != hp ||
            after.Spdif != spdif || after.Balance != balance || after.Gain != gain ||
            after.Rotation != rotation || after.HardwareKeys != hardwareKeys || after.ScreenTimeout != timeout)
            throw new InvalidOperationException("One or more DC07 settings did not match the requested values after readback.");
        return after;
    }

    // Low-latency volume path: one HID write only. No full-state read, sleep, or readback.
    // The device is read back during the normal periodic refresh, not on every knob tick.
    public void SetVolumeFast(int value)
    {
        if (value is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(value));
        var connection = Open();
        using var stream = connection.Stream;
        Write(connection.Device, stream, [1, 16, 239, (byte)value, (byte)value, 0, 0, 0]);
    }

    public Dc07State SetVolume(int value)
    {
        if (value is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(value));
        var connection = Open();
        using var stream = connection.Stream;
        _ = ReadAll(connection.Device, stream);
        Write(connection.Device, stream, [1, 16, 239, (byte)value, (byte)value, 0, 0, 0]);
        Thread.Sleep(250);
        var after = ReadAll(connection.Device, stream);
        if (after.Volume != value) throw new InvalidOperationException($"Volume readback mismatch. Expected {value}, received {after.Volume}.");
        return after;
    }

    public Dc07State SetFilter(int filter, int hpFilter)
    {
        if (filter is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(filter));
        if (hpFilter is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(hpFilter));
        var connection = Open();
        using var stream = connection.Stream;
        _ = ReadAll(connection.Device, stream);
        Write(connection.Device, stream, [17, 32, 223, 0, (byte)filter, (byte)hpFilter, 0, 0]);
        Thread.Sleep(250);
        var after = ReadAll(connection.Device, stream);
        if (after.Filter != filter || after.HpFilter != hpFilter)
            throw new InvalidOperationException($"Filter readback mismatch. Expected DAC={filter}, HP={hpFilter}; received DAC={after.Filter}, HP={after.HpFilter}.");
        return after;
    }

    public Dc07State SetAudio(int spdif, int balance, int gain)
    {
        if (spdif is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(spdif));
        if (balance is < 0 or > 20) throw new ArgumentOutOfRangeException(nameof(balance));
        if (gain is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(gain));
        var connection = Open();
        using var stream = connection.Stream;
        _ = ReadAll(connection.Device, stream);
        Write(connection.Device, stream, [25, 48, 207, 0, (byte)spdif, (byte)balance, (byte)gain, 0]);
        Thread.Sleep(250);
        var after = ReadAll(connection.Device, stream);
        if (after.Spdif != spdif || after.Balance != balance || after.Gain != gain)
            throw new InvalidOperationException($"Audio readback mismatch. Expected S/PDIF={spdif}, balance={balance}, gain={gain}; received S/PDIF={after.Spdif}, balance={after.Balance}, gain={after.Gain}.");
        return after;
    }

    public Dc07State SetDisplay(int rotation, int hardwareKeys, int timeout)
    {
        if (rotation is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(rotation));
        if (hardwareKeys is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(hardwareKeys));
        if (timeout is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(timeout));
        var connection = Open();
        using var stream = connection.Stream;
        _ = ReadAll(connection.Device, stream);
        Write(connection.Device, stream, [25, 64, 191, 0, (byte)rotation, (byte)hardwareKeys, (byte)timeout, 0]);
        Thread.Sleep(250);
        var after = ReadAll(connection.Device, stream);
        if (after.Rotation != rotation || after.HardwareKeys != hardwareKeys || after.ScreenTimeout != timeout)
            throw new InvalidOperationException($"Display readback mismatch. Expected rotation={rotation}, keys={hardwareKeys}, timeout={timeout}; received rotation={after.Rotation}, keys={after.HardwareKeys}, timeout={after.ScreenTimeout}.");
        return after;
    }

    private static (HidDevice Device, HidStream Stream) Open()
    {
        var device = DeviceList.Local.GetHidDevices(Vid, Pid)
            .FirstOrDefault(d => d.DevicePath.Contains("&mi_02", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("DC07 Pro MI_02 HID interface not found. Check USB connection.");
        var stream = device.Open();
        stream.ReadTimeout = 500;
        stream.WriteTimeout = 1000;
        return (device, stream);
    }

    private static Dc07State ReadAll(HidDevice device, HidStream stream)
    {
        var volume = Query(device, stream, 66, [66, 17, 238, 0, 0, 0, 0, 0]);
        var filter = Query(device, stream, 89, [89, 33, 222, 0, 0, 0, 0, 0]);
        var audio = Query(device, stream, 96, [96, 49, 206, 0, 0, 0, 0, 0]);
        var display = Query(device, stream, 97, [97, 65, 190, 0, 0, 0, 0, 0]);
        string? firmware = null;
        try
        {
            var fpga = Query(device, stream, 88, [88, 241, 14, 0, 0, 0, 0, 0]);
            if (fpga.Length >= 8)
            {
                int minor = fpga[5];
                int patch = ((fpga[6] >> 4) * 10) + (fpga[6] & 0x0F);
                firmware = $"{fpga[4]}.{minor}.{patch}";
            }
        }
        catch (TimeoutException) { /* Firmware query is optional; controls remain usable. */ }

        if (volume.Length < 5 || filter.Length < 6 || audio.Length < 7 || display.Length < 7)
            throw new InvalidDataException("DC07 response was shorter than expected.");
        return new Dc07State(volume[4], filter[4], filter[5],
            audio[4], audio[5], audio[6],
            display[4], display[5], display[6], firmware);
    }

    private static byte[] Query(HidDevice device, HidStream stream, byte command, byte[] packet)
    {
        Write(device, stream, packet);
        var until = DateTime.UtcNow.AddSeconds(2);
        var buffer = new byte[device.MaxInputReportLength];
        while (DateTime.UtcNow < until)
        {
            stream.ReadTimeout = 250;
            int count;
            try { count = stream.Read(buffer, 0, buffer.Length); }
            catch (TimeoutException) { continue; }
            if (count <= 0) continue;

            byte[] data = buffer.AsSpan(0, count).ToArray();
            if ((data.Length == 9 || data.Length == 33) && data[0] == 0)
                data = data[1..];

            byte responseCommand = data.Length == 32 && data.Length > 6
                ? data[6]
                : data.Length > 0 ? data[0] : (byte)0xFF;

            if (responseCommand == command || (data.Length > 0 && data[0] == command))
                return data;
        }
        throw new TimeoutException($"Timed out waiting for DC07 response to command {command}.");
    }

    private static void Write(HidDevice device, HidStream stream, byte[] packet)
    {
        if (packet.Length != 8) throw new ArgumentException("DC07 packet must be 8 bytes.");
        if (device.MaxOutputReportLength < 9) throw new InvalidOperationException("Unexpected HID output report length.");
        var report = new byte[device.MaxOutputReportLength];
        report[0] = 0;
        Buffer.BlockCopy(packet, 0, report, 1, 8);
        stream.Write(report);
    }
}


internal sealed class LowLevelKeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104, VK_VOLUME_UP = 0xAF, VK_VOLUME_DOWN = 0xAE, VK_MENU = 0x12;
    private readonly HookProc _proc; private readonly IntPtr _hook; private readonly Func<bool,bool,bool> _handler;
    public LowLevelKeyboardHook(Func<bool,bool,bool> handler) { _handler=handler; _proc=Callback; _hook=SetWindowsHookEx(WH_KEYBOARD_LL,_proc,GetModuleHandle(null),0); if(_hook==IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam) { if(code>=0 && (wParam.ToInt32()==WM_KEYDOWN || wParam.ToInt32()==WM_SYSKEYDOWN)) { int vk=Marshal.ReadInt32(lParam); if(vk==VK_VOLUME_UP || vk==VK_VOLUME_DOWN) { bool alt=(GetKeyState(VK_MENU)&0x8000)!=0; if (_handler(vk==VK_VOLUME_UP,alt)) return new IntPtr(1); return CallNextHookEx(_hook,code,wParam,lParam); } } return CallNextHookEx(_hook,code,wParam,lParam); }
    public void Dispose(){ if(_hook!=IntPtr.Zero) UnhookWindowsHookEx(_hook); }
    private delegate IntPtr HookProc(int code,IntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll",SetLastError=true)] private static extern IntPtr SetWindowsHookEx(int id,HookProc callback,IntPtr module,uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr wParam,IntPtr lParam);
    [DllImport("kernel32.dll",CharSet=CharSet.Auto)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
}


internal sealed class VolumeOsdWindow : Window
{
    private readonly TextBlock _device = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(MediaColor.FromRgb(220, 232, 248)) };
    private readonly TextBlock _value = new() { FontSize = 34, FontWeight = FontWeights.Bold, Foreground = MediaBrushes.White };
    private readonly TextBlock _direction = new() { FontSize = 12, Foreground = new SolidColorBrush(MediaColor.FromRgb(141, 232, 255)), VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _fill = new() { Height = 7, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(MediaColor.FromRgb(141, 232, 255)), HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
    private readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromMilliseconds(1250) };
    public VolumeOsdWindow()
    {
        Width = 310; Height = 126; WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = MediaBrushes.Transparent;
        ShowInTaskbar = false; Topmost = true; ShowActivated = false; ResizeMode = ResizeMode.NoResize;
        var panel = new Border { CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(MediaColor.FromArgb(150, 130, 170, 210)), Background = new SolidColorBrush(MediaColor.FromArgb(238, 24, 38, 58)), Padding = new Thickness(18) };
        var stack = new StackPanel();
        var head = new DockPanel(); DockPanel.SetDock(_direction, Dock.Right); head.Children.Add(_direction); head.Children.Add(new TextBlock { Text = "VOLUME", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(MediaColor.FromRgb(141, 232, 255)) });
        stack.Children.Add(head); stack.Children.Add(_device); stack.Children.Add(_value);
        var track = new Border { Height = 7, Background = new SolidColorBrush(MediaColor.FromRgb(57, 75, 99)), CornerRadius = new CornerRadius(4), Child = _fill, Margin = new Thickness(0, 5, 0, 0) }; stack.Children.Add(track); panel.Child = stack; Content = panel;
        _hide.Tick += (_, _) => { _hide.Stop(); Hide(); };
    }
    public void Show(string device, int value, int max, bool up)
    {
        _device.Text = device; _value.Text = $"{value} / {max}"; _direction.Text = up ? "+" : "−";
        _fill.Width = 270.0 * value / Math.Max(1, max);
        var area = SystemParameters.WorkArea; Left = area.Right - Width - 28; Top = area.Bottom - Height - 28;
        if (!IsVisible) Show(); _hide.Stop(); _hide.Start();
    }
}
