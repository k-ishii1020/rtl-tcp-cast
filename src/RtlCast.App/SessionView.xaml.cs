// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 いしかず
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using RtlCast.Core;

namespace RtlCast.App;

public partial class SessionView : System.Windows.Controls.UserControl
{
    private enum SessionPhase { Stopped, Starting, Running, Stopping, Faulted, Closed }
    private const decimal FrequencyStepHz = 6_250m;

    private readonly RelayServer relay = new();
    private readonly RtlTcpProcess localTcp = new();
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private CancellationTokenSource? startCancellation;
    private string? lastError;
    private readonly ConcurrentQueue<RelayLog> pendingLogs = new();
    private readonly Queue<string> logLines = new();
    private readonly ObservableCollection<ClientRow> rows = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer settingsTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly Dictionary<Guid, long> previousSent = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private double previousSeconds;
    private IReadOnlyList<SdrDevice> devices = Array.Empty<SdrDevice>();
    private bool refreshingDevices;
    private SessionPhase phase = SessionPhase.Stopped;
    private bool closing;
    private bool cancelStartRequested;
    private RelayOptions activeOptions = new();
    private readonly Func<string> executablePath;
    private readonly Action<SessionView, AppSettings> save;
    private readonly Action<SessionView, AppSettings> validateStart;
    private bool running => phase == SessionPhase.Running;
    private bool busy => phase is SessionPhase.Starting or SessionPhase.Stopping;
    public bool IsRunning => phase is SessionPhase.Starting or SessionPhase.Running or SessionPhase.Stopping;
    public bool IsBusy => busy;
    public AppSettings GetCurrentSettings() => ReadOptions();
    public AppSettings CurrentSettings
    {
        get
        {
            try { return GetCurrentSettings(); }
            catch (ArgumentException) { return loadedSettings; }
        }
    }
    public bool UsesLocalSource => LocalSourceRadio.IsChecked == true;
    public string DeviceIdentifier => SelectedDeviceIdentifier();
    public bool TryGetListenPort(out int port) => int.TryParse(ListenPortBox.Text, out port);
    public string SessionStatus { get; private set; } = "配信停止中";
    public bool IsStreaming { get; private set; }
    public int ConnectedClientCount { get; private set; }
    public double TrafficBytesPerSecond { get; private set; }
    public event Action? StatusChanged;
    public event Action<string>? SessionNameChanged;
    public int? OwnedProcessId => localTcp.ProcessId;
    private AppSettings loadedSettings = new();

    public SessionView(AppSettings saved, Func<string> executablePath,
        Action<SessionView, AppSettings> save, Action<SessionView, AppSettings> validateStart)
    {
        this.executablePath = executablePath;
        this.save = save;
        this.validateStart = validateStart;
        InitializeComponent();
        foreach (var rate in new[] { "0.25", "1.024", "1.536", "1.8", "1.92", "2.048", "2.4", "2.56", "2.88", "3.2" })
            SampleRateBox.Items.Add(rate);
        SampleRateBox.SelectedItem = "2.048";
        ClientsGrid.ItemsSource = rows;
        relay.Logged += pendingLogs.Enqueue;
        localTcp.Logged += pendingLogs.Enqueue;
        try
        {
            loadedSettings = saved;
            LocalSourceRadio.IsChecked = saved.UseLocalRtlTcp;
            RemoteSourceRadio.IsChecked = !saved.UseLocalRtlTcp;
            DeviceBox.Text = saved.DeviceIdentifier;
            SessionNameText.Text = saved.SessionName;
            HostBox.Text = saved.UpstreamHost;
            UpstreamPortBox.Text = saved.UpstreamPort.ToString();
            ListenPortBox.Text = saved.ListenPort.ToString();
            ReconnectBox.IsChecked = saved.AutoReconnect;
            LockFrequencyBox.IsChecked = saved.LockCenterFrequency;
            LockPpmBox.IsChecked = saved.LockPpm;
            LockGainBox.IsChecked = saved.LockGain;
            LockSampleRateBox.IsChecked = saved.LockSampleRate;
            FrequencyBox.Text = (saved.CenterFrequency / 1_000_000m).ToString(CultureInfo.InvariantCulture);
            PpmBox.Text = saved.Ppm.ToString();
            AutoGainBox.IsChecked = saved.AutomaticGain;
            GainBox.Text = (Math.Clamp(saved.GainTenthsDb,
                RelayOptions.MinGainTenthsDb, RelayOptions.MaxGainTenthsDb) / 10m).ToString(CultureInfo.InvariantCulture);
            var savedRate = (saved.ManagedSampleRate / 1_000_000m).ToString(CultureInfo.InvariantCulture);
            if (!SampleRateBox.Items.Contains(savedRate)) SampleRateBox.Items.Add(savedRate);
            SampleRateBox.SelectedItem = savedRate;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            pendingLogs.Enqueue(new(DateTimeOffset.Now, $"設定を読み込めなかったため初期値を使用します: {ex.Message}"));
        }
        LanBox.IsChecked = IPAddress.TryParse(saved.ListenAddress, out var savedListenAddress) &&
            !IPAddress.IsLoopback(savedListenAddress);
        LanBox.Checked += (_, _) => QueueSettingsSave();
        LanBox.Unchecked += (_, _) => QueueSettingsSave();
        LocalSourceRadio.Checked += (_, _) => UpdateSource();
        RemoteSourceRadio.Checked += (_, _) => UpdateSource();
        UpdateSource();
        foreach (var box in new[] { LockFrequencyBox, LockPpmBox, LockGainBox, LockSampleRateBox })
        {
            box.Checked += (_, _) => { UpdateTuning(); QueueSettingsSave(); };
            box.Unchecked += (_, _) => { UpdateTuning(); QueueSettingsSave(); };
        }
        AutoGainBox.Checked += (_, _) => { UpdateTuning(); QueueSettingsSave(); };
        AutoGainBox.Unchecked += (_, _) => { UpdateTuning(); QueueSettingsSave(); };
        UpdateTuning();
        settingsTimer.Tick += async (_, _) => { settingsTimer.Stop(); await SaveSettingsAutomaticallyAsync(); };
        foreach (var box in new[] { HostBox, UpstreamPortBox, ListenPortBox, FrequencyBox, PpmBox, GainBox })
            box.TextChanged += (_, _) => QueueSettingsSave();
        SessionNameText.TextChanged += (_, _) =>
        {
            SessionNameChanged?.Invoke(SessionNameText.Text.Trim());
            QueueSettingsSave();
        };
        SampleRateBox.SelectionChanged += (_, _) => QueueSettingsSave();
        DeviceBox.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new System.Windows.Controls.TextChangedEventHandler((_, _) =>
        {
            QueueSettingsSave();
        }));
        LocalSourceRadio.Checked += (_, _) => QueueSettingsSave();
        RemoteSourceRadio.Checked += (_, _) => QueueSettingsSave();
        ReconnectBox.Checked += (_, _) => QueueSettingsSave();
        ReconnectBox.Unchecked += (_, _) => QueueSettingsSave();
        timer.Tick += async (_, _) =>
        {
            Refresh();
            if (phase == SessionPhase.Running && localTcp.HasExited)
            {
                lastError = localTcp.ExitDescription;
                pendingLogs.Enqueue(new(DateTimeOffset.Now, lastError));
                await StopSessionAsync(faulted: true);
            }
        };
        timer.Start();
        _ = RefreshDevicesAsync();
    }

    private AppSettings ReadOptions()
    {
        var localSelected = LocalSourceRadio.IsChecked == true;
        var remoteSelected = RemoteSourceRadio.IsChecked == true;
        if (localSelected == remoteSelected) throw new ArgumentException("受信元を選択してください。");
        var local = localSelected;
        if (!int.TryParse(ListenPortBox.Text, out var listenPort))
            throw new ArgumentException("ポート番号は1～65535の整数で入力してください。");
        if (!int.TryParse(UpstreamPortBox.Text, out var upstreamPort))
        {
            if (!local) throw new ArgumentException("ポート番号は1～65535の整数で入力してください。");
            upstreamPort = 1234;
        }
        if (local && upstreamPort is < 1 or > 65535) upstreamPort = 1234;
        static uint Megahertz(string text, string name)
        {
            if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ||
                value <= 0 || value > uint.MaxValue / 1_000_000m || value * 1_000_000 != decimal.Truncate(value * 1_000_000))
                throw new ArgumentException($"{name}には有効な正の数値を入力してください。");
            return (uint)(value * 1_000_000);
        }
        if (!int.TryParse(PpmBox.Text, out var ppm)) throw new ArgumentException("PPMには整数を入力してください。");
        if (!decimal.TryParse(GainBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var gain) ||
            gain < RelayOptions.MinGainTenthsDb / 10m || gain > RelayOptions.MaxGainTenthsDb / 10m ||
            gain * 10 != decimal.Truncate(gain * 10))
            throw new ArgumentException("Gainは-9.9～49.6 dB、0.1 dB単位で入力してください。");
        var settings = loadedSettings with
        {
            SessionName = string.IsNullOrWhiteSpace(SessionNameText.Text) ? loadedSettings.SessionName : SessionNameText.Text.Trim(),
            UpstreamHost = local && string.IsNullOrWhiteSpace(HostBox.Text) ? "127.0.0.1" : HostBox.Text.Trim(), UpstreamPort = upstreamPort,
            ListenAddress = LanBox.IsChecked == true ? "0.0.0.0" : "127.0.0.1", ListenPort = listenPort,
            AutoReconnect = ReconnectBox.IsChecked == true,
            UseLocalRtlTcp = local, RtlTcpPath = executablePath(), DeviceIdentifier = SelectedDeviceIdentifier(),
            LockCenterFrequency = LockFrequencyBox.IsChecked == true,
            LockPpm = LockPpmBox.IsChecked == true,
            LockGain = LockGainBox.IsChecked == true,
            LockSampleRate = LockSampleRateBox.IsChecked == true,
            AcceptClientCommands = !(LockFrequencyBox.IsChecked == true && LockPpmBox.IsChecked == true &&
                LockGainBox.IsChecked == true && LockSampleRateBox.IsChecked == true),
            CenterFrequency = Megahertz(FrequencyBox.Text, "中心周波数"), Ppm = ppm,
            AutomaticGain = AutoGainBox.IsChecked == true, GainTenthsDb = (int)(gain * 10),
            ManagedSampleRate = Megahertz(SampleRateBox.SelectedItem?.ToString() ?? "", "サンプルレート")
        };
        var effective = local ? settings with { UpstreamHost = "127.0.0.1", UpstreamPort = 1234 } : settings;
        effective.Validate();
        if (!local && IPAddress.TryParse(settings.UpstreamHost, out var host) &&
            settings.UpstreamPort == settings.ListenPort &&
            (host.Equals(IPAddress.Loopback) || host.Equals(IPAddress.IPv6Loopback) || host.ToString() == settings.ListenAddress))
            throw new ArgumentException("rtl_tcpの接続先と配信先が同じです。配信ポートを別の番号に変更してください。");
        return settings;
    }

    private async void StartStop_Click(object sender, RoutedEventArgs e)
    {
        if (phase == SessionPhase.Starting) await StopSessionAsync();
        else if (phase == SessionPhase.Running) await StopSessionAsync();
        else if (phase is SessionPhase.Stopped or SessionPhase.Faulted) await StartSessionAsync();
    }

    public async Task<bool> StartSessionAsync()
    {
        if (closing || phase is SessionPhase.Starting or SessionPhase.Stopping) return false;
        await lifecycleGate.WaitAsync();
        try
        {
            if (closing || phase is SessionPhase.Starting or SessionPhase.Stopping or SessionPhase.Closed) return false;
            if (phase == SessionPhase.Running) return true;
            return await StartSessionCoreAsync();
        }
        finally { lifecycleGate.Release(); }
    }

    private async Task<bool> StartSessionCoreAsync()
    {
        SetPhase(SessionPhase.Starting);
        settingsTimer.Stop();
        cancelStartRequested = false;
        var startTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        startCancellation = startTimeout;
        try
        {
            var settings = ReadOptions();
            validateStart(this, settings);
            loadedSettings = settings;
            lastError = null;
            RelayOptions effective = settings;
            if (settings.UseLocalRtlTcp)
            {
                localTcp.Start(settings.RtlTcpPath, settings.ListenPort, settings.DeviceIdentifier, settings.Ppm);
                effective = settings with { UpstreamHost = "127.0.0.1", UpstreamPort = localTcp.Port,
                    AutoReconnect = true, ReconnectDelay = TimeSpan.FromMilliseconds(250) };
            }
            await relay.StartAsync(effective, settings.UseLocalRtlTcp ? settings.Ppm : null);
            activeOptions = effective;
            if (settings.UseLocalRtlTcp)
            {
                while (relay.GetSnapshot().Status != RelayStatus.Streaming)
                {
                    if (localTcp.HasExited) throw new InvalidOperationException(localTcp.ExitDescription);
                    await Task.Delay(100, startTimeout.Token);
                }
                if (localTcp.HasExited) throw new InvalidOperationException(localTcp.ExitDescription);
            }
            SetPhase(SessionPhase.Running);
            previousSent.Clear();
            previousSeconds = clock.Elapsed.TotalSeconds;
            try { loadedSettings = settings; save(this, settings); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { pendingLogs.Enqueue(new(DateTimeOffset.Now, $"配信は開始しましたが、設定の保存に失敗しました: {ex.Message}")); }
            return true;
        }
        catch (Exception ex)
        {
            var cancelledByUser = ex is OperationCanceledException && cancelStartRequested;
            try { await StopResourcesAsync(); }
            catch (Exception stopError) { pendingLogs.Enqueue(new(DateTimeOffset.Now, $"起動後の停止処理に失敗しました: {stopError.Message}")); }
            lastError = cancelledByUser ? null : ex is OperationCanceledException
                ? "rtl_tcpの起動を確認できませんでした。SDRデバイス、ドライバー、同じフォルダーのDLLを確認してください。"
                : ex.Message;
            SetPhase(cancelledByUser ? SessionPhase.Stopped : SessionPhase.Faulted);
            if (!closing && !cancelledByUser)
                MessageBox.Show(Window.GetWindow(this), ex is SocketException ?
                    $"配信ポートを開けませんでした。別のアプリがそのポートを使用していないか確認してください。\n\n{ex.Message}" : lastError,
                    "配信を開始できません", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        finally
        {
            startTimeout.Dispose();
            if (ReferenceEquals(startCancellation, startTimeout)) startCancellation = null;
            cancelStartRequested = false;
            UpdateControls();
            Refresh();
        }
    }

    public Task StopSessionAsync() => StopSessionAsync(faulted: false);

    private async Task StopSessionAsync(bool faulted)
    {
        if (phase == SessionPhase.Starting)
        {
            cancelStartRequested = true;
            startCancellation?.Cancel();
        }
        await lifecycleGate.WaitAsync();
        try
        {
            if (phase != SessionPhase.Running) return;
            SetPhase(SessionPhase.Stopping);
            try
            {
                await StopResourcesAsync();
                SetPhase(faulted ? SessionPhase.Faulted : SessionPhase.Stopped);
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                SetPhase(SessionPhase.Faulted);
                pendingLogs.Enqueue(new(DateTimeOffset.Now, $"配信停止に失敗しました: {ex.Message}"));
                if (!closing) MessageBox.Show(Window.GetWindow(this), ex.Message, "配信を停止できません", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { UpdateControls(); Refresh(); }
        }
        finally { lifecycleGate.Release(); }
    }

    private void SetPhase(SessionPhase next)
    {
        phase = next;
        UpdateControls();
    }

    private void UpdateControls()
    {
        var enabled = !closing && phase != SessionPhase.Closed;
        var transitioning = phase is SessionPhase.Starting or SessionPhase.Stopping;
        var isRunning = phase == SessionPhase.Running;
        StartStopButton.IsEnabled = enabled && (!transitioning || phase == SessionPhase.Starting);
        SourcePanel.IsEnabled = LanBox.IsEnabled = ReconnectBox.IsEnabled = enabled && !isRunning && !transitioning;
        DistributionPanel.IsEnabled = enabled && !isRunning && !transitioning;
        SdrPanel.IsEnabled = enabled && !transitioning;
        StartStopButton.Content = phase switch
        {
            SessionPhase.Starting => "起動中止",
            SessionPhase.Running => "配信停止",
            SessionPhase.Stopping => "停止中…",
            SessionPhase.Closed => "終了",
            _ => "配信開始"
        };
    }

    private void UpdateSource()
    {
        var local = LocalSourceRadio.IsChecked == true;
        LocalSourcePanel.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = local ? Visibility.Collapsed : Visibility.Visible;
        ReconnectBox.IsEnabled = !local;
    }

    private string SelectedDeviceIdentifier() => DeviceBox.SelectedItem is SdrDevice selected
        ? selected.Identifier : DeviceBox.Text.Trim();

    public string ResolveDeviceKey(string identifier)
    {
        if (uint.TryParse(identifier, out var index)) return "index:" + index;
        var matches = devices.Where(d => d.Index.ToString() == identifier || d.Serial == identifier).ToArray();
        return matches.Length == 1 ? "index:" + matches[0].Index : identifier;
    }

    public string GetDeviceLabel(string identifier)
    {
        var device = uint.TryParse(identifier, out var index)
            ? devices.FirstOrDefault(d => d.Index == index)
            : devices.FirstOrDefault(d => d.Identifier == identifier || d.Serial == identifier);
        if (device != null) return device.Label;
        return uint.TryParse(identifier, out var fallbackIndex) ? $"RTL-SDRデバイス {fallbackIndex}" : $"RTL-SDRデバイス [{identifier}]";
    }

    private async void RefreshDevices_Click(object sender, RoutedEventArgs e) => await RefreshDevicesAsync();
    public async Task RefreshDevicesAsync(Func<string, IReadOnlyList<SdrDevice>>? provider = null)
    {
        if (refreshingDevices || closing) return;
        refreshingDevices = true;
        RefreshDevicesButton.IsEnabled = false;
        var identifier = SelectedDeviceIdentifier();
        try
        {
            var found = await Task.Run(() => (provider ?? SdrDevices.Enumerate)(executablePath()));
            if (closing) return;
            devices = found;
            DeviceBox.ItemsSource = devices;
            var selected = devices.FirstOrDefault(d => d.Identifier == identifier || d.Index.ToString() == identifier);
            DeviceBox.SelectedItem = selected;
            if (selected == null) DeviceBox.Text = identifier;
            if (devices.Count == 0) pendingLogs.Enqueue(new(DateTimeOffset.Now, "RTL-SDRデバイスが見つかりません。番号・シリアル番号の手入力も使用できます。"));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        { pendingLogs.Enqueue(new(DateTimeOffset.Now, ex.Message)); }
        finally { refreshingDevices = false; RefreshDevicesButton.IsEnabled = !closing; }
    }

    public void UpdateDeviceSerial(SdrDevice device, string serial)
    {
        var identifier = SelectedDeviceIdentifier();
        if (identifier != device.Serial || device.Serial.Length == 0 || uint.TryParse(device.Serial, out _)) return;
        DeviceBox.SelectedItem = null;
        DeviceBox.Text = uint.TryParse(serial, out _) ? device.Index.ToString() : serial;
        QueueSettingsSave();
    }

    private void QueueSettingsSave()
    {
        if (closing) return;
        settingsTimer.Stop();
        settingsTimer.Start();
    }

    private async Task SaveSettingsAutomaticallyAsync()
    {
        await lifecycleGate.WaitAsync();
        try
        {
            if (closing || busy) return;
            var settings = ReadOptions();
            if (running && (settings.LockCenterFrequency != activeOptions.LockCenterFrequency ||
                settings.LockPpm != activeOptions.LockPpm || settings.LockGain != activeOptions.LockGain ||
                settings.LockSampleRate != activeOptions.LockSampleRate ||
                settings.CenterFrequency != activeOptions.CenterFrequency || settings.Ppm != activeOptions.Ppm ||
                settings.AutomaticGain != activeOptions.AutomaticGain || settings.GainTenthsDb != activeOptions.GainTenthsDb ||
                settings.ManagedSampleRate != activeOptions.ManagedSampleRate))
            {
                var previousOptions = activeOptions;
                var applied = await relay.ConfigureControlAsync(settings);
                settings = settings with { CenterFrequency = applied.CenterFrequency, Ppm = applied.Ppm,
                    AutomaticGain = applied.AutomaticGain, GainTenthsDb = applied.GainTenthsDb,
                    ManagedSampleRate = applied.ManagedSampleRate };
                if (!previousOptions.LockCenterFrequency && settings.LockCenterFrequency)
                    FrequencyBox.Text = (settings.CenterFrequency / 1_000_000m).ToString(CultureInfo.InvariantCulture);
                if (!previousOptions.LockPpm && settings.LockPpm)
                    PpmBox.Text = settings.Ppm.ToString(CultureInfo.InvariantCulture);
                if (!previousOptions.LockGain && settings.LockGain)
                {
                    GainBox.Text = (settings.GainTenthsDb / 10m).ToString(CultureInfo.InvariantCulture);
                    AutoGainBox.IsChecked = settings.AutomaticGain;
                }
                if (!previousOptions.LockSampleRate && settings.LockSampleRate)
                    SampleRateBox.SelectedItem = (settings.ManagedSampleRate / 1_000_000m).ToString(CultureInfo.InvariantCulture);
                activeOptions = activeOptions with { AcceptClientCommands = settings.AcceptClientCommands,
                    LockCenterFrequency = settings.LockCenterFrequency, LockPpm = settings.LockPpm,
                    LockGain = settings.LockGain, LockSampleRate = settings.LockSampleRate,
                    CenterFrequency = settings.CenterFrequency, Ppm = settings.Ppm,
                    AutomaticGain = settings.AutomaticGain, GainTenthsDb = settings.GainTenthsDb,
                    ManagedSampleRate = settings.ManagedSampleRate };
            }
            loadedSettings = settings;
            save(this, settings);
        }
        catch (ArgumentException)
        {
            // Keep the last valid settings while a port or address is still being typed.
        }
        catch (Exception ex)
        {
            pendingLogs.Enqueue(new(DateTimeOffset.Now, $"設定の保存または反映に失敗しました: {ex.Message}"));
        }
        finally { lifecycleGate.Release(); }
    }

    private async Task StopResourcesAsync()
    {
        try { await relay.StopAsync(); }
        finally { await localTcp.StopAsync(); }
    }

    private void UpdateTuning()
    {
        FrequencyInput.IsEnabled = LockFrequencyBox.IsChecked == true;
        PpmInput.IsEnabled = LockPpmBox.IsChecked == true;
        GainInput.IsEnabled = LockGainBox.IsChecked == true && AutoGainBox.IsChecked != true;
        AutoGainBox.IsEnabled = LockGainBox.IsChecked == true;
        SampleRateBox.IsEnabled = LockSampleRateBox.IsChecked == true;
    }

    private void StepFrequency(int direction)
    {
        if (!decimal.TryParse(FrequencyBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var mhz)) return;
        var frequencyHz = mhz * 1_000_000m;
        if (frequencyHz < 1 || frequencyHz > uint.MaxValue || frequencyHz != decimal.Truncate(frequencyHz)) return;
        frequencyHz = Math.Clamp(frequencyHz + direction * FrequencyStepHz, 1m, (decimal)uint.MaxValue);
        FrequencyBox.Text = (frequencyHz / 1_000_000m).ToString("0.######", CultureInfo.InvariantCulture);
    }

    private void StepPpm(int direction)
    {
        if (!PpmBox.IsEnabled || !int.TryParse(PpmBox.Text, out var value)) return;
        PpmBox.Text = Math.Clamp((long)value + direction, -1000, 1000).ToString(CultureInfo.InvariantCulture);
    }

    private void StepGain(int direction)
    {
        if (!GainBox.IsEnabled || !decimal.TryParse(GainBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)) return;
        var minimum = RelayOptions.MinGainTenthsDb / 10m;
        var maximum = RelayOptions.MaxGainTenthsDb / 10m;
        GainBox.Text = Math.Clamp(Math.Clamp(value, minimum, maximum) + direction, minimum, maximum)
            .ToString(CultureInfo.InvariantCulture);
    }

    private void PpmUp_Click(object sender, RoutedEventArgs e) => StepPpm(1);
    private void PpmDown_Click(object sender, RoutedEventArgs e) => StepPpm(-1);
    private void FrequencyUp_Click(object sender, RoutedEventArgs e) => StepFrequency(1);
    private void FrequencyDown_Click(object sender, RoutedEventArgs e) => StepFrequency(-1);
    private void GainUp_Click(object sender, RoutedEventArgs e) => StepGain(1);
    private void GainDown_Click(object sender, RoutedEventArgs e) => StepGain(-1);
    private void Frequency_MouseWheel(object sender, MouseWheelEventArgs e) { StepFrequency(Math.Sign(e.Delta)); e.Handled = true; }
    private void Ppm_MouseWheel(object sender, MouseWheelEventArgs e) { StepPpm(Math.Sign(e.Delta)); e.Handled = true; }
    private void Gain_MouseWheel(object sender, MouseWheelEventArgs e) { StepGain(Math.Sign(e.Delta)); e.Handled = true; }
    private void Ppm_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down) { StepPpm(e.Key == Key.Up ? 1 : -1); e.Handled = true; }
    }
    private void Frequency_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down) { StepFrequency(e.Key == Key.Up ? 1 : -1); e.Handled = true; }
    }
    private void Gain_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down) { StepGain(e.Key == Key.Up ? 1 : -1); e.Handled = true; }
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        if (ClientsGrid.SelectedItem is ClientRow row) relay.DisconnectClient(row.Id);
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        pendingLogs.Clear();
        logLines.Clear();
        LogBox.Clear();
    }

    private void Refresh()
    {
        var snapshot = relay.GetSnapshot();
        var gain = snapshot.RequestedAutomaticGain == true ? "自動"
            : snapshot.RequestedGainTenthsDb is int gainTenths
                ? $"{(gainTenths / 10m).ToString("0.#", CultureInfo.InvariantCulture)} dB"
                : snapshot.RequestedGainIndex is uint gainIndex ? $"ゲイン段階 {gainIndex}" : "未取得";
        CurrentSettingsText.Text = $"中心周波数: {FormatSettingFrequency(snapshot.RequestedFrequency)}　" +
            $"周波数補正(ppm): {snapshot.RequestedPpm?.ToString(CultureInfo.InvariantCulture) ?? "未取得"}　" +
            $"Gain: {gain}　" +
            $"サンプルレート: {FormatSettingRate(snapshot.RequestedSampleRate)}";
        SessionStatus = snapshot.Status switch
        {
            RelayStatus.Connecting => "rtl_tcpに接続中", RelayStatus.Streaming => "配信中",
            RelayStatus.Reconnecting => "rtl_tcpへ再接続中", RelayStatus.Faulted => "接続を確認してください", _ => "配信停止中"
        };
        if (lastError != null && !running) SessionStatus = "rtl_tcpを確認してください";
        IsStreaming = snapshot.Status == RelayStatus.Streaming && running;
        var seconds = clock.Elapsed.TotalSeconds;
        var elapsed = Math.Max(seconds - previousSeconds, 0.001);
        ConnectedClientCount = snapshot.Clients.Count;
        var selected = (ClientsGrid.SelectedItem as ClientRow)?.Id;
        foreach (var stale in rows.Where(r => !snapshot.Clients.Any(c => c.Id == r.Id)).ToArray())
        { rows.Remove(stale); previousSent.Remove(stale.Id); }
        var totalSentRate = 0.0;
        foreach (var client in snapshot.Clients)
        {
            var rate = previousSent.TryGetValue(client.Id, out var sent) ? Math.Max(0, client.BytesSent - sent) / elapsed : 0;
            totalSentRate += rate;
            previousSent[client.Id] = client.BytesSent;
            var row = rows.FirstOrDefault(r => r.Id == client.Id);
            if (row == null) { row = new(client.Id, client.Endpoint); rows.Add(row); }
            row.Update((DateTimeOffset.Now - client.ConnectedAt).ToString(@"hh\:mm\:ss"), FormatRate(rate), client.HasControl ? "操作可能" : "受信専用");
        }
        TrafficBytesPerSecond = totalSentRate;
        if (selected.HasValue) ClientsGrid.SelectedItem = rows.FirstOrDefault(r => r.Id == selected);
        StatusChanged?.Invoke();
        previousSeconds = seconds;
        var updated = false;
        while (pendingLogs.TryDequeue(out var log))
        {
            logLines.Enqueue($"[{log.Time:HH:mm:ss}] {log.Message}");
            while (logLines.Count > 300) logLines.Dequeue();
            updated = true;
        }
        if (updated) { LogBox.Text = string.Join(Environment.NewLine, logLines); LogBox.ScrollToEnd(); }
    }

    private static string FormatRate(double bytes) => $"{bytes / 1_000_000:F2} MB/s";

    private static string FormatSettingFrequency(uint? value) => value is uint frequency
        ? $"{(frequency / 1_000_000m).ToString("0.######", CultureInfo.InvariantCulture)} MHz" : "未取得";

    private static string FormatSettingRate(uint? value) => value is uint rate
        ? $"{(rate / 1_000_000m).ToString("0.###", CultureInfo.InvariantCulture)} MS/s" : "未取得";

    public async Task ShutdownAsync()
    {
        closing = true;
        settingsTimer.Stop();
        cancelStartRequested = true;
        startCancellation?.Cancel();
        UpdateControls();
        timer.Stop();
        await lifecycleGate.WaitAsync();
        try
        {
            try { loadedSettings = ReadOptions(); save(this, loadedSettings); }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { }
            try { await StopResourcesAsync(); }
            catch (Exception ex) { pendingLogs.Enqueue(new(DateTimeOffset.Now, $"終了時の停止処理に失敗しました: {ex.Message}")); }
            SetPhase(SessionPhase.Closed);
        }
        finally { lifecycleGate.Release(); }
    }

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        loadedSettings = loadedSettings with { SessionName = name };
        SessionNameText.Text = name;
        save(this, loadedSettings);
    }

    private void SessionName_LostFocus(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SessionNameText.Text)) SessionNameText.Text = loadedSettings.SessionName;
    }

    private sealed class ClientRow(Guid id, string endpoint) : INotifyPropertyChanged
    {
        public Guid Id { get; } = id;
        public string Endpoint { get; } = endpoint;
        public string Duration { get; private set; } = "";
        public string Rate { get; private set; } = "";
        public string Control { get; private set; } = "";
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Update(string duration, string rate, string control)
        {
            Duration = duration; Rate = rate; Control = control;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
    }
}
