// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 いしかず
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Interop;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace RtlCast.App;

public partial class MainWindow : Window
{
    private const int WmDeviceChange = 0x0219;
    private const int DbtDevNodesChanged = 0x0007;
    private const int DbtDeviceArrival = 0x8000;
    private const int DbtDeviceRemoveComplete = 0x8004;
    private const int DbtDeviceRemovePending = 0x8003;
    private static readonly Guid UsbDeviceInterface = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");
    private struct DeviceInterfaceFilter
    {
        public uint Size;
        public uint DeviceType;
        public uint Reserved;
        public Guid ClassGuid;
        [MarshalAs(UnmanagedType.U2)] public char Name;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern nint RegisterDeviceNotificationW(nint recipient, ref DeviceInterfaceFilter filter, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterDeviceNotification(nint handle);

    private readonly bool persistSettings;
    private readonly string? settingsDirectory;
    private WorkspaceSettings workspace = new();
    private bool closing;
    private bool allowClose;
    private bool exitRequested;
    private bool loading;
    private bool startupStreamingAttempted;
    private int batchRevision;
    private TabItem? addTab;
    private readonly DispatcherTimer deviceRefreshTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer deviceFallbackTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
    private HwndSource? windowSource;
    private nint deviceNotification;
    private Forms.NotifyIcon? trayIcon;
    private System.Drawing.Icon? trayImage;
    private Forms.ContextMenuStrip? trayMenu;
    public Func<string, SerialNumberWindow> SerialWindowFactory { get; set; } = path => new SerialNumberWindow(path);
    public IReadOnlyList<SessionView> Sessions => SessionsTabs.Items.OfType<TabItem>()
        .Where(t => t.Content is SessionView).Select(t => (SessionView)t.Content).ToArray();
    public SessionView? SelectedSession => (SessionsTabs.SelectedItem as TabItem)?.Content as SessionView;
    public string RtlTcpPath => workspace.RtlTcpPath;

    public MainWindow() : this(true) { }
    public MainWindow(bool persistSettings, string? settingsDirectory = null)
    {
        this.persistSettings = persistSettings;
        this.settingsDirectory = settingsDirectory;
        InitializeComponent();
        SourceInitialized += RegisterForDeviceChanges;
        Closed += (_, _) =>
        {
            StopDeviceNotifications();
            trayIcon?.Dispose();
            trayIcon = null;
            trayImage?.Dispose();
            trayImage = null;
            trayMenu?.Dispose();
            trayMenu = null;
        };
        deviceRefreshTimer.Tick += async (_, _) =>
        {
            deviceRefreshTimer.Stop();
            if (!closing) await Task.WhenAll(Sessions.Select(s => s.RefreshDevicesAsync()));
        };
        deviceFallbackTimer.Tick += async (_, _) =>
        {
            if (!closing) await Task.WhenAll(Sessions.Select(s => s.RefreshDevicesAsync()));
        };
        Loaded += async (_, _) =>
        {
            if (closing) return;
            _ = CheckForUpdatesAsync();
            if (!string.IsNullOrWhiteSpace(workspace.RtlTcpPath))
                await Task.WhenAll(Sessions.Select(s => s.RefreshDevicesAsync()));
            if (!closing && workspace.AutoStartStreaming && !startupStreamingAttempted)
            {
                startupStreamingAttempted = true;
                await StartAllSessionsAsync();
            }
        };
        Title = $"RTL-TCP-CAST v{typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion}";
        loading = true;
        try { workspace = persistSettings ? WorkspaceSettingsStore.Load(settingsDirectory) : new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        { MessageBox.Show(this, $"設定を読み込めませんでした。\n{ex.Message}", "設定", MessageBoxButton.OK); }
        if (workspace.Sessions.Count == 0) workspace.Sessions.Add(new() { SessionId = Guid.NewGuid().ToString("N") });
        foreach (var settings in workspace.Sessions.ToArray()) CreateTab(settings);
        EnsureAddTab();
        SessionsTabs.SelectedItem = SessionsTabs.Items.OfType<TabItem>().FirstOrDefault(t => t.Content is SessionView session && session.CurrentSettings.SessionId == workspace.SelectedSessionId)
            ?? SessionsTabs.Items[0];
        UpdateCloseButtons();
        loading = false;
        EnsureTrayIcon();
    }

    private async Task CheckForUpdatesAsync()
    {
        var currentVersion = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(currentVersion)) return;

        var release = await ReleaseUpdateChecker.GetNewerReleaseAsync(currentVersion);
        if (closing || release?.TagName is not { } tagName) return;

        var releaseUrl = release.HtmlUrl;
        if (string.IsNullOrWhiteSpace(releaseUrl) || !Uri.TryCreate(releaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            releaseUrl = "https://github.com/k-ishii1020/rtl-tcp-cast/releases/latest";

        var result = MessageBox.Show(this,
            $"新しいバージョン {tagName} が公開されています。\n現在のバージョン: v{currentVersion}\n\nGitHubのリリースページを開きますか？",
            "最新バージョンあり", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (result == MessageBoxResult.Yes) OpenExternalLink(releaseUrl);
    }

    private void RegisterForDeviceChanges(object? sender, EventArgs e)
    {
        windowSource = PresentationSource.FromVisual(this) as HwndSource;
        if (windowSource == null) return;
        windowSource.AddHook(DeviceWindowProc);
        var filter = new DeviceInterfaceFilter
        {
            Size = (uint)Marshal.SizeOf<DeviceInterfaceFilter>(),
            DeviceType = 5,
            ClassGuid = UsbDeviceInterface
        };
        deviceNotification = RegisterDeviceNotificationW(windowSource.Handle, ref filter, 0);
        if (deviceNotification == 0) deviceFallbackTimer.Start();
    }

    private nint DeviceWindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmDeviceChange)
        {
            var change = wParam.ToInt64();
            if (change is DbtDevNodesChanged or DbtDeviceArrival or DbtDeviceRemovePending or DbtDeviceRemoveComplete)
            {
                SdrDevices.InvalidateCache();
                deviceRefreshTimer.Stop();
                deviceRefreshTimer.Start();
            }
        }
        return 0;
    }

    private void StopDeviceNotifications()
    {
        deviceRefreshTimer.Stop();
        deviceFallbackTimer.Stop();
        if (windowSource != null) windowSource.RemoveHook(DeviceWindowProc);
        if (deviceNotification != 0) UnregisterDeviceNotification(deviceNotification);
        deviceNotification = 0;
    }

    private void CreateTab(AppSettings settings)
    {
        var session = new SessionView(settings, () => workspace.RtlTcpPath, SaveSession, ValidateStart, () => workspace.ClientQueueCapacity);
        var indicator = new TextBlock { Text = "■", Foreground = Brushes.Firebrick, Margin = new Thickness(5,0,4,0) };
        var title = new TextBlock { Text = settings.SessionName };
        var count = new TextBlock();
        var close = new Button
        {
            Content = "×", Tag = session, Width = 20, Height = 20, Padding = new Thickness(0),
            Margin = new Thickness(5, 0, 0, 0), BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, FontSize = 15, VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center, ToolTip = "この配信タブを閉じる"
        };
        close.Click += CloseTab_Click;
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(indicator); header.Children.Add(title); header.Children.Add(count); header.Children.Add(close);
        var tab = new TabItem { Content = session, Header = header, Padding = new Thickness(5, 2, 4, 2) };
        var addIndex = addTab != null && SessionsTabs.Items.Contains(addTab)
            ? SessionsTabs.Items.IndexOf(addTab) : SessionsTabs.Items.Count;
        SessionsTabs.Items.Insert(addIndex, tab);
        UpdateCloseButtons();
        session.SessionNameChanged += name => title.Text = string.IsNullOrWhiteSpace(name) ? "（無題）" : name;
        void UpdateTabClientCount()
        {
            count.Text = $"({session.ConnectedClientCount})";
        }
        UpdateTabClientCount();
        UpdateApplicationStatus();
        session.StatusChanged += () =>
        {
            var isStreaming = session.IsStreaming;
            indicator.Text = isStreaming ? "●" : "■";
            indicator.Foreground = isStreaming ? Brushes.ForestGreen : Brushes.Firebrick;
            UpdateTabClientCount();
            UpdateApplicationStatus();
        };
    }

    private void UpdateApplicationStatus()
    {
        var sessions = Sessions;
        var activeCount = sessions.Count(session => session.IsStreaming);
        var hasStreaming = activeCount > 0;
        ApplicationStatusIndicator.Text = hasStreaming ? "●" : "■";
        ApplicationStatusIndicator.Foreground = hasStreaming ? Brushes.ForestGreen : Brushes.Firebrick;
        ApplicationActivityText.Text = $"SDRデバイスの動作数: {activeCount}/{sessions.Count}";
        ApplicationClientCountText.Text = $"全接続数: {sessions.Sum(session => session.ConnectedClientCount)}台";
        ApplicationTrafficText.Text = $"配信: {FormatTrafficRate(sessions.Sum(session => session.TrafficBytesPerSecond))}";
    }

    private static string FormatTrafficRate(double bytesPerSecond) => $"{bytesPerSecond / 1_000_000:F2} MB/s";

    private void EnsureTrayIcon()
    {
        if (trayIcon != null)
        {
            trayIcon.Visible = true;
            return;
        }

        trayMenu = new Forms.ContextMenuStrip();
        trayMenu.Items.Add("ウィンドウを表示", null, (_, _) => Dispatcher.BeginInvoke(new Action(RestoreWindowFromTray)));
        trayMenu.Items.Add(new Forms.ToolStripSeparator());
        trayMenu.Items.Add("終了", null, (_, _) => Dispatcher.BeginInvoke(new Action(RequestExit)));
        var appAssemblyName = typeof(MainWindow).Assembly.GetName().Name;
        var iconResource = new Uri($"pack://application:,,,/{appAssemblyName};component/Assets/rtl-tcp-cast.ico", UriKind.Absolute);
        using (var stream = Application.GetResourceStream(iconResource)!.Stream)
        using (var icon = new System.Drawing.Icon(stream))
            trayImage = (System.Drawing.Icon)icon.Clone();
        trayIcon = new Forms.NotifyIcon
        {
            Icon = trayImage,
            Text = "RTL-TCP-CAST",
            ContextMenuStrip = trayMenu,
            Visible = true
        };
        trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(new Action(RestoreWindowFromTray));
    }

    private void RestoreWindowFromTray()
    {
        if (closing) return;
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void RequestExit()
    {
        if (exitRequested) return;
        exitRequested = true;
        Close();
    }

    private void EnsureAddTab()
    {
        addTab ??= new TabItem { Header = "＋", Padding = new Thickness(9, 2, 9, 2), ToolTip = "配信を追加" };
        if (!SessionsTabs.Items.Contains(addTab)) SessionsTabs.Items.Add(addTab);
    }

    private void UpdateCloseButtons()
    {
        var canClose = Sessions.Count > 1;
        foreach (var tab in SessionsTabs.Items.OfType<TabItem>().Where(t => t.Content is SessionView))
        {
            if (tab.Header is StackPanel header && header.Children.OfType<Button>().FirstOrDefault() is { } close)
                close.IsEnabled = canClose;
        }
    }

    private void SaveSession(SessionView session, AppSettings settings)
    {
        var index = workspace.Sessions.FindIndex(s => s.SessionId == settings.SessionId);
        if (index < 0) return;
        workspace.Sessions[index] = settings with { RtlTcpPath = "" };
        SaveWorkspace();
    }

    private void SaveWorkspace()
    {
        if (!persistSettings || loading) return;
        try { WorkspaceSettingsStore.Save(workspace, settingsDirectory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { MessageBox.Show(this, $"設定を保存できませんでした。\n{ex.Message}", "設定", MessageBoxButton.OK); }
    }

    private void ValidateStart(SessionView requester, AppSettings settings)
    {
        if (settings.UseLocalRtlTcp && string.IsNullOrWhiteSpace(settings.DeviceIdentifier))
            throw new ArgumentException("SDRデバイスの番号またはシリアル番号を入力してください。");
        IReadOnlyList<SdrDevice>? currentDevices = null;
        var checkedCurrentDevices = false;
        foreach (var other in Sessions.Where(s => s != requester))
        {
            var active = other.CurrentSettings;
            if (other.TryGetListenPort(out var otherPort) && settings.ListenPort == otherPort)
                throw new ArgumentException($"配信ポートは「{active.SessionName}」のタブと重複しています。");
            if (other.IsRunning && settings.UseLocalRtlTcp && other.UsesLocalSource)
            {
                var liveDevices = GetCurrentDevices(ref currentDevices, ref checkedCurrentDevices);
                if (SameDevice(requester, settings.DeviceIdentifier, other, other.DeviceIdentifier, liveDevices))
                {
                    var deviceLabel = liveDevices == null ? null : FindDevice(liveDevices, settings.DeviceIdentifier)?.Label;
                    deviceLabel ??= requester.GetDeviceLabel(settings.DeviceIdentifier);
                    throw new ArgumentException($"デバイス「{deviceLabel}」は「{active.SessionName}」のタブで使用中または起動処理中です。別のタブを停止してから開始してください。");
                }
            }
        }
    }

    private IReadOnlyList<SdrDevice>? GetCurrentDevices(ref IReadOnlyList<SdrDevice>? devices, ref bool checkedDevices)
    {
        if (checkedDevices) return devices;
        checkedDevices = true;
        try { devices = SdrDevices.Enumerate(workspace.RtlTcpPath); }
        catch (Exception ex) when (ex is IOException or ArgumentException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        { }
        return devices;
    }

    private static bool SameDevice(SessionView first, string firstIdentifier, SessionView second, string secondIdentifier,
        IReadOnlyList<SdrDevice>? currentDevices)
    {
        if (string.IsNullOrWhiteSpace(firstIdentifier) || string.IsNullOrWhiteSpace(secondIdentifier)) return false;
        if (string.Equals(firstIdentifier, secondIdentifier, StringComparison.OrdinalIgnoreCase)) return true;
        if (currentDevices != null)
        {
            var firstCurrent = FindDevice(currentDevices, firstIdentifier);
            var secondCurrent = FindDevice(currentDevices, secondIdentifier);
            if (firstCurrent != null && secondCurrent != null) return firstCurrent.Index == secondCurrent.Index;
        }
        return first.ResolveDeviceKey(firstIdentifier) == first.ResolveDeviceKey(secondIdentifier) ||
               second.ResolveDeviceKey(firstIdentifier) == second.ResolveDeviceKey(secondIdentifier);
    }

    private static SdrDevice? FindDevice(IReadOnlyList<SdrDevice> devices, string identifier)
    {
        if (uint.TryParse(identifier, out var index)) return devices.FirstOrDefault(device => device.Index == index);
        return devices.FirstOrDefault(device => device.Identifier == identifier || device.Serial == identifier);
    }


    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var ports = Sessions.Select(s => s.TryGetListenPort(out var port) ? port : s.CurrentSettings.ListenPort).ToHashSet();
        var port = 11234;
        while (ports.Contains(port) && port < 65535) port++;
        if (ports.Contains(port)) return;
        var reservedDevices = Sessions.Where(s => s.UsesLocalSource)
            .Select(s => s.ResolveDeviceKey(s.DeviceIdentifier)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var device = 0;
        while (reservedDevices.Contains($"index:{device}")) device++;
        var settings = new AppSettings { SessionId = Guid.NewGuid().ToString("N"),
            SessionName = $"配信 {workspace.Sessions.Count + 1}", ListenPort = port, DeviceIdentifier = device.ToString() };
        workspace.Sessions.Add(settings);
        CreateTab(settings);
        EnsureAddTab();
        SessionsTabs.SelectedItem = SessionsTabs.Items.OfType<TabItem>().First(t => ReferenceEquals(t.Content, Sessions.Last()));
        SaveWorkspace();
    }

    private async void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (closing || sender is not Button { Tag: SessionView session }) return;
        await CloseSessionAsync(session);
    }

    private async Task CloseSessionAsync(SessionView session)
    {
        if (closing || Sessions.Count <= 1 || !Sessions.Contains(session)) return;
        var sessionId = session.CurrentSettings.SessionId;
        var tab = SessionsTabs.Items.OfType<TabItem>().First(t => ReferenceEquals(t.Content, session));
        tab.IsEnabled = false;
        await session.ShutdownAsync();
        workspace.Sessions.RemoveAll(s => s.SessionId == sessionId);
        if (ReferenceEquals(SessionsTabs.SelectedItem, tab))
        {
            SessionsTabs.SelectedItem = SessionsTabs.Items.OfType<TabItem>()
                .First(t => t.Content is SessionView && !ReferenceEquals(t, tab));
        }
        SessionsTabs.Items.Remove(tab);
        UpdateCloseButtons();
        UpdateApplicationStatus();
        SaveWorkspace();
    }

    public void SetRtlTcpPath(string path) { workspace = workspace with { RtlTcpPath = path }; SaveWorkspace(); }
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(workspace.RtlTcpPath, workspace.StartWithWindows, workspace.AutoStartStreaming, workspace.ClientQueueCapacity) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try
        {
            if (dialog.StartWithWindows != workspace.StartWithWindows && persistSettings)
                WindowsStartup.SetEnabled(dialog.StartWithWindows);
            workspace = workspace with { RtlTcpPath = dialog.RtlTcpPath, StartWithWindows = dialog.StartWithWindows,
                AutoStartStreaming = dialog.AutoStartStreaming, ClientQueueCapacity = dialog.ClientQueueCapacity };
            SaveWorkspace();
            foreach (var session in Sessions) _ = session.RefreshDevicesAsync();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        { MessageBox.Show(this, $"自動起動設定を変更できませんでした。\n{ex.Message}", "設定", MessageBoxButton.OK); }
    }
    private WorkspaceSettings CurrentWorkspace() => workspace with
    {
        SelectedSessionId = SelectedSession?.CurrentSettings.SessionId ?? workspace.SelectedSessionId,
        Sessions = Sessions.Select(s => s.GetCurrentSettings() with { RtlTcpPath = "" }).ToList()
    };

    private void ExportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "設定ファイルのエクスポート",
            Filter = "RTL-TCP-CAST設定ファイル (*.ini)|*.ini",
            DefaultExt = ".ini",
            AddExtension = true,
            FileName = "rtl-tcp-cast-settings.ini"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            WorkspaceSettingsStore.SaveFile(CurrentWorkspace(), dialog.FileName);
            MessageBox.Show(this, "設定ファイルをエクスポートしました。", "設定ファイル", MessageBoxButton.OK);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { MessageBox.Show(this, $"エクスポートできませんでした。\n{ex.Message}", "設定ファイル", MessageBoxButton.OK); }
    }

    private async void ImportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "設定ファイルのインポート",
            Filter = "RTL-TCP-CAST設定ファイル (*.ini)|*.ini|すべてのファイル (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        WorkspaceSettings imported;
        try { imported = WorkspaceSettingsStore.LoadFile(dialog.FileName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { MessageBox.Show(this, $"設定ファイルを読み込めませんでした。\n{ex.Message}", "設定ファイル", MessageBoxButton.OK); return; }

        if (MessageBox.Show(this, "現在の配信設定をインポートした内容に置き換えます。配信中のタブは停止します。続行しますか？",
                "設定ファイルのインポート", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        batchRevision++;
        IsEnabled = false;
        var previousStartup = workspace.StartWithWindows;
        var startupChanged = persistSettings && previousStartup != imported.StartWithWindows;
        try
        {
            await Task.WhenAll(Sessions.Select(s => s.StopSessionAsync()));
            if (startupChanged) WindowsStartup.SetEnabled(imported.StartWithWindows);
            try
            {
                if (persistSettings) WorkspaceSettingsStore.Save(imported, settingsDirectory);
            }
            catch
            {
                if (startupChanged) WindowsStartup.SetEnabled(previousStartup);
                throw;
            }

            loading = true;
            workspace = imported with { Sessions = imported.Sessions.ToList() };
            SessionsTabs.Items.Clear();
            foreach (var settings in workspace.Sessions) CreateTab(settings);
            EnsureAddTab();
            SessionsTabs.SelectedItem = SessionsTabs.Items.OfType<TabItem>()
                .First(t => t.Content is SessionView session && session.CurrentSettings.SessionId == workspace.SelectedSessionId);
            UpdateCloseButtons();
            loading = false;
            foreach (var session in Sessions) _ = session.RefreshDevicesAsync();
            MessageBox.Show(this, "設定ファイルをインポートしました。配信は停止したままです。", "設定ファイル", MessageBoxButton.OK);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            loading = false;
            MessageBox.Show(this, $"インポートできませんでした。\n{ex.Message}", "設定ファイル", MessageBoxButton.OK);
        }
        finally { if (!closing) IsEnabled = true; }
    }

    private async void SerialNumber_Click(object sender, RoutedEventArgs e)
    {
        if (closing) return;
        batchRevision++;
        IsEnabled = false;
        try
        {
            await Task.WhenAll(Sessions.Select(s => s.StopSessionAsync()));
            if (closing) return;
            var dialog = SerialWindowFactory(workspace.RtlTcpPath);
            dialog.Owner = this;
            if (dialog.ShowDialog() == true && dialog.ChangedDevice is { } changed)
            {
                foreach (var session in Sessions) session.UpdateDeviceSerial(changed, dialog.NewSerial);
                MessageBox.Show(this, "保存しました。USBを抜き差ししてから、デバイス一覧を更新してください。配信は停止したままです。", "シリアル番号変更", MessageBoxButton.OK);
            }
        }
        finally { if (!closing) IsEnabled = true; }
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != SessionsTabs) return;
        if (ReferenceEquals(SessionsTabs.SelectedItem, addTab))
        {
            Add_Click(sender, e);
            return;
        }
        if (SelectedSession is not { } selected) return;
        workspace = workspace with { SelectedSessionId = selected.CurrentSettings.SessionId };
        SaveWorkspace();
    }
    private async void StartAll_Click(object sender, RoutedEventArgs e)
        => await StartAllSessionsAsync();

    private async Task StartAllSessionsAsync()
    {
        if (closing) return;
        StartAllButton.IsEnabled = false;
        var revision = batchRevision;
        try
        {
            foreach (var session in Sessions)
            {
                if (closing || revision != batchRevision || !await session.StartSessionAsync()) break;
            }
        }
        finally { StartAllButton.IsEnabled = !closing; }
    }
    private async void StopAll_Click(object sender, RoutedEventArgs e)
    {
        batchRevision++;
        StopAllButton.IsEnabled = false;
        try { await Task.WhenAll(Sessions.Select(s => s.StopSessionAsync())); }
        finally { StopAllButton.IsEnabled = !closing; }
    }
    private void HelpPage_Click(object sender, RoutedEventArgs e) => OpenExternalLink("https://k-ishii1020.github.io/rtl-tcp-cast/guide.html");

    private void About_Click(object sender, RoutedEventArgs e)
    {
        new AboutWindow { Owner = this }.ShowDialog();
    }

    private void OpenExternalLink(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, $"リンクを開けませんでした。\n{ex.Message}", "ヘルプ", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => RequestExit();
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true;
        if (!exitRequested)
        {
            EnsureTrayIcon();
            if (!workspace.HideTrayNotice)
            {
                var notice = new TrayNoticeWindow { Owner = this };
                notice.ShowDialog();
                if (notice.DoNotShowAgain)
                {
                    workspace = workspace with { HideTrayNotice = true };
                    SaveWorkspace();
                }
            }
            Hide();
            return;
        }
        if (closing) return;
        closing = true;
        batchRevision++;
        IsEnabled = false;
        await Task.WhenAll(Sessions.Select(s => s.ShutdownAsync()));
        SaveWorkspace();
        allowClose = true;
        _ = Dispatcher.BeginInvoke(new Action(Close), DispatcherPriority.Background);
    }
}
