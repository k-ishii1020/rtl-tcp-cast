// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 いしかず
using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RtlCast.App;

namespace RtlCast.GuiSmoke;

internal static class Program
{
    private static Process? processToClose;
    private static Process? secondProcessToClose;
    private static string guiSettingsDirectory = "";
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--initialize-user-settings"))
        {
            var saved = SettingsStore.Load();
            Console.WriteLine($"Settings initialized: {SettingsStore.IniPath}");
            return 0;
        }
        if (args.Length == 2 && args[0] == "--list-devices")
        {
            foreach (var device in SdrDevices.Enumerate(args[1])) Console.WriteLine(device.Label);
            return 0;
        }
        ExerciseSettings();
        ExerciseEeprom();
        var result = 1;
        var app = new RtlCast.App.App(showMainWindow: false);
        app.InitializeComponent();
        guiSettingsDirectory = Path.GetFullPath(Path.Combine("artifacts", "live-settings-tests", Guid.NewGuid().ToString("N")));
        var initialSessionId = Guid.NewGuid().ToString("N");
        WorkspaceSettingsStore.Save(new WorkspaceSettings
        {
            HideTrayNotice = true,
            SelectedSessionId = initialSessionId,
            Sessions = new() { new AppSettings { SessionId = initialSessionId } }
        }, guiSettingsDirectory);
        var window = new MainWindow(persistSettings: true, settingsDirectory: guiSettingsDirectory);
        var started = false;
        var hiddenToTray = false;
        var processesContinuedAfterHide = false;
        var restoredFromTray = false;
        var exitedFromTray = false;
        window.ContentRendered += async (_, _) =>
        {
            if (started) return;
            started = true;
            try
            {
                await Exercise(window, args.FirstOrDefault() ?? "artifacts/screenshots");
                result = 0;
                Console.WriteLine("PASS GUI start/stop, settings lock, client list, all-client control, disconnect, LAN bind, rendering");
            }
            catch (Exception ex) { Console.Error.WriteLine($"FAIL GUI: {ex}"); }
            finally
            {
                window.Close();
                var trayIcon = typeof(MainWindow).GetField("trayIcon", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
                hiddenToTray = !window.IsVisible && (bool)trayIcon.GetType().GetProperty("Visible")!.GetValue(trayIcon)!;
                processesContinuedAfterHide = (processToClose == null || !processToClose.HasExited) &&
                    (secondProcessToClose == null || !secondProcessToClose.HasExited);
                typeof(MainWindow).GetMethod("RestoreWindowFromTray", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(window, null);
                restoredFromTray = window.IsVisible;
                window.Hide();
                var contextMenu = trayIcon.GetType().GetProperty("ContextMenuStrip")!.GetValue(trayIcon)!;
                var menuItems = (System.Collections.IEnumerable)contextMenu.GetType().GetProperty("Items")!.GetValue(contextMenu)!;
                var exitItem = menuItems.Cast<object>().Single(item => item.GetType().GetProperty("Text")?.GetValue(item)?.ToString() == "終了");
                exitItem.GetType().GetMethod("PerformClick")!.Invoke(exitItem, null);
                exitedFromTray = true;
            }
        };
        app.Run(window);
        if (!hiddenToTray || !processesContinuedAfterHide || !restoredFromTray || !exitedFromTray)
        {
            result = 1;
            Console.Error.WriteLine("FAIL closing window hides to tray, keeps streams alive, restores, and exits from tray");
        }
        else Console.WriteLine("PASS closing window hides to tray; tray restores and exits");
        if (processToClose != null)
        {
            using (processToClose)
            {
                if (!processToClose.HasExited) { result = 1; Console.Error.WriteLine("FAIL tray exit left owned rtl_tcp running"); }
                else Console.WriteLine("PASS tray exit stops owned rtl_tcp");
            }
        }
        if (secondProcessToClose != null)
        {
            using (secondProcessToClose) Assert(secondProcessToClose.HasExited, "Tray exit stops second owned process");
            Console.WriteLine("PASS tray exit stops all owned processes");
        }
        return result;
    }

    private static byte[] SerialImage()
    {
        var image = Enumerable.Repeat((byte)0x55, 256).ToArray();
        image[0] = 0x28; image[1] = 0x32;
        var offset = 9;
        foreach (var value in new[] { "Realtek", "RTL2838UHIDIR", "OLD" })
        {
            var bytes = System.Text.Encoding.Unicode.GetBytes(value);
            image[offset] = (byte)(bytes.Length + 2); image[offset + 1] = 3;
            bytes.CopyTo(image, offset + 2); offset += bytes.Length + 2;
        }
        return image;
    }

    private static void ExerciseEeprom()
    {
        var original = SerialImage();
        var info = EepromSerial.Inspect(original);
        Assert(info.Serial == "OLD" && info.MaximumLength == 11, "Dynamic EEPROM length");
        var changed = EepromSerial.Replace(original, "ABCDEFGHIJK");
        Assert(EepromSerial.Inspect(changed).Serial == "ABCDEFGHIJK", "Maximum serial round trip");
        Assert(original.SequenceEqual(SerialImage()), "Original remains unchanged");
        Assert(changed.Skip(78).SequenceEqual(original.Skip(78)), "EEPROM tail preserved");
        Assert(changed.Take(6).SequenceEqual(original.Take(6)) && changed.Skip(7).Take(info.Offset - 7).SequenceEqual(original.Skip(7).Take(info.Offset - 7)), "Header and other strings preserved");
        foreach (var invalid in new[] { "", "ABCDEFGHIJKL", "日本語", "a b" })
        {
            var rejected = false;
            try { EepromSerial.Replace(original, invalid); } catch (ArgumentException) { rejected = true; }
            Assert(rejected, "Invalid serial rejected");
        }
        original[10] = 0;
        var malformed = false;
        try { EepromSerial.Inspect(original); } catch (ArgumentException) { malformed = true; }
        Assert(malformed, "Malformed descriptors rejected");
        Console.WriteLine("PASS EEPROM boundaries, validation, preservation");
    }

    private static async Task ExerciseSerial(MainWindow window, string directory)
    {
        var devices = new[] { new SdrDevice(0, "Test SDR", "OLD", "OLD"), new SdrDevice(1, "Test SDR", "OTHER", "OTHER") };
        var opened = false;
        Exception? failure = null;
        window.SerialWindowFactory = path =>
        {
            Assert(window.Sessions.All(s => !s.IsRunning), "All streams stop before serial tool opens");
            opened = true;
            var tool = new SerialNumberWindow(path, _ => devices, (_, _) => SerialImage(), (_, _, _, _) => throw new Exception("Unexpected hardware write"));
            tool.Loaded += async (_, _) =>
            {
                try { await Until(() => ((TextBox)tool.FindName("SerialBox")).IsEnabled); Capture(tool, directory, "serial-number.png"); }
                catch (Exception ex) { failure = ex; }
                finally { tool.Close(); }
            };
            return tool;
        };
        ((MenuItem)window.FindName("SerialNumberMenu")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(() => opened && window.IsEnabled);
        if (failure != null) throw failure;
        byte[]? saved = null;
        var dialog = new SerialNumberWindow("fixture", _ => devices, (_, _) => SerialImage(), (_, _, image, id) => saved = EepromSerial.Replace(image, id));
        dialog.Owner = window;
        dialog.Loaded += async (_, _) =>
        {
            try
            {
                var text = (TextBox)dialog.FindName("SerialBox");
                var save = (Button)dialog.FindName("SaveButton");
                await Until(() => text.IsEnabled);
                Assert(text.MaxLength == 11, "GUI dynamic max length");
                text.Text = "OTHER"; Assert(!save.IsEnabled, "Duplicate serial disabled");
                text.Text = "bad id"; Assert(!save.IsEnabled, "Invalid ID disabled");
                text.Text = "NEW-SDR"; Assert(save.IsEnabled, "Valid serial enabled");
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            catch (Exception ex) { failure = ex; dialog.Close(); }
        };
        Assert(dialog.ShowDialog() == true, "Serial dialog saves successfully");
        if (failure != null) throw failure;
        Assert(saved != null && EepromSerial.Inspect(saved).Serial == "NEW-SDR" && dialog.NewSerial == "NEW-SDR", "Injected serial writer verified");
        Console.WriteLine("PASS serial dialog, duplicate ID, stop-all before opening, injected save");
    }

    private static void ExerciseSettings()
    {
        var directory = Path.GetFullPath(Path.Combine("artifacts", "settings-tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        var original = new AppSettings
        {
            UseLocalRtlTcp = false, RtlTcpPath = @"C:\受信 tools\a=b;#\rtl_tcp.exe",
            UpstreamHost = "192.168.1.55", UpstreamPort = 1456, ListenPort = 7890,
            AutoReconnect = false, ReconnectDelay = TimeSpan.FromSeconds(1.25), MaxClients = 4
        };
        File.WriteAllText(Path.Combine(directory, "settings.json"), JsonSerializer.Serialize(original));
        Assert(SettingsStore.Load(directory) == original, "Legacy JSON migration preserves settings");
        Assert(File.Exists(Path.Combine(directory, "settings.ini")), "Migration creates INI");
        Assert(File.Exists(Path.Combine(directory, "settings.json")), "Legacy JSON retained");
        var changed = original with { AcceptClientCommands = false, CenterFrequency = 145_500_000, Ppm = -12, AutomaticGain = false, GainTenthsDb = 143, UseLocalRtlTcp = true, ListenPort = 8000 };
        SettingsStore.Save(changed, directory);
        Assert(SettingsStore.Load(directory) == changed, "INI round trip and INI takes precedence");
        File.AppendAllText(Path.Combine(directory, "settings.ini"), "\n; comment\n[Other]\nExtra=ignored\n");
        Assert(SettingsStore.Load(directory) == changed, "Comments and unknown keys accepted");
        File.WriteAllText(Path.Combine(directory, "settings.ini"), "[Network]\nListenPort=invalid\n");
        try { SettingsStore.Load(directory); throw new Exception("Malformed INI accepted"); }
        catch (ArgumentException) { }
        File.Delete(Path.Combine(directory, "settings.ini"));
        File.Delete(Path.Combine(directory, "settings.json"));
        Assert(SettingsStore.Load(directory) == new AppSettings(), "Defaults written as INI on first launch");
        var freshWorkspaceDirectory = Path.Combine(directory, "fresh-workspace");
        WorkspaceSettingsStore.Load(freshWorkspaceDirectory);
        Assert(File.Exists(Path.Combine(freshWorkspaceDirectory, "settings.ini")) &&
            !File.Exists(Path.Combine(freshWorkspaceDirectory, "settings.legacy.ini")), "Fresh workspace does not create legacy backup");
        var workspaceDirectory = Path.Combine(directory, "workspace");
        SettingsStore.Save(original, workspaceDirectory);
        var migrated = WorkspaceSettingsStore.Load(workspaceDirectory);
        Assert(migrated.RtlTcpPath == original.RtlTcpPath && migrated.Sessions[0].ListenPort == original.ListenPort, "Single-session settings migrate");
        Assert(!migrated.AutoStartStreaming, "Auto-start streaming defaults off for migrated settings");
        Assert(File.Exists(Path.Combine(workspaceDirectory, "settings.legacy.ini")), "Legacy INI backup retained");
        var secondSession = changed with { SessionId = Guid.NewGuid().ToString("N"), SessionName = "日本語配信", DeviceIdentifier = "SERIAL-2", RtlTcpPath = "" };
        migrated.Sessions.Add(secondSession);
        WorkspaceSettingsStore.Save(migrated with { SelectedSessionId = secondSession.SessionId }, workspaceDirectory);
        var restored = WorkspaceSettingsStore.Load(workspaceDirectory);
        Assert(restored.Sessions.Count == 2 && restored.Sessions[1] == secondSession && restored.SelectedSessionId == secondSession.SessionId, "Multi-session INI round trip");
        var registryPath = @"Software\RTL-TCP-Cast\Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            WindowsStartup.SetEnabled(true, @"C:\Apps with spaces\RTL-TCP-Cast.exe", registryPath);
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(registryPath))
                Assert((string?)key!.GetValue(WindowsStartup.ValueName) == "\"" + @"C:\Apps with spaces\RTL-TCP-Cast.exe" + "\"", "Startup executable correctly quoted");
            WindowsStartup.SetEnabled(false, registryPath: registryPath);
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(registryPath))
                Assert(key!.GetValue(WindowsStartup.ValueName) == null, "Startup disable removes registration");
        }
        finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKey(registryPath, false); }
        WorkspaceSettingsStore.Save(restored with { StartWithWindows = true, AutoStartStreaming = true }, workspaceDirectory);
        var startupPreferences = WorkspaceSettingsStore.Load(workspaceDirectory);
        Assert(startupPreferences.StartWithWindows && startupPreferences.AutoStartStreaming, "Startup preferences persist");
        Console.WriteLine("PASS startup registration in isolated test key");
        Console.WriteLine("PASS INI persistence, Unicode/equal-sign paths, legacy migration, malformed values, defaults");
    }

    private static async Task Exercise(MainWindow window, string screenshotDirectory)
    {
        T Find<T>(string name) where T : class => window.SelectedSession!.FindName(name) as T ?? throw new Exception($"Missing {name}");
        T FindApp<T>(string name) where T : class => window.FindName(name) as T ?? throw new Exception($"Missing application control {name}");
        var menu = ((DockPanel)window.Content).Children.OfType<Menu>().Single();
        var topLevelMenus = menu.Items.OfType<MenuItem>().ToArray();
        var helpItems = topLevelMenus.Last().Items.OfType<MenuItem>().ToArray();
        Assert(topLevelMenus.Select(item => item.Header.ToString()).SequenceEqual(["ファイル(_F)", "設定(_S)", "ヘルプ(_H)"]) &&
            helpItems.First().Header.ToString() == "ヘルプページ(_P)", "Help is a top-level menu beside Settings");
        string TabConnectionCount(SessionView session)
        {
            var tabs = (TabControl)window.FindName("SessionsTabs");
            var tab = tabs.Items.OfType<TabItem>().Single(t => ReferenceEquals(t.Content, session));
            return ((StackPanel)tab.Header).Children.OfType<TextBlock>().Skip(2).First().Text;
        }
        TextBlock TabStatusIndicator(SessionView session)
        {
            var tabs = (TabControl)window.FindName("SessionsTabs");
            var tab = tabs.Items.OfType<TabItem>().Single(t => ReferenceEquals(t.Content, session));
            return ((StackPanel)tab.Header).Children.OfType<TextBlock>().First();
        }
        var start = Find<Button>("StartStopButton");
        var clients = Find<DataGrid>("ClientsGrid");
        var host = Find<TextBox>("HostBox");
        Assert(Find<RadioButton>("LocalSourceRadio").IsChecked == true, "Local launch is default");
        var version = typeof(MainWindow).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion;
        Assert(window.Title == $"RTL-TCP-CAST v{version}", "Window title uses assembly version");
        Assert(window.Height == 800, "Window opens with a taller default height");
        Assert(FindApp<TextBlock>("ApplicationActivityText").Text == "SDRデバイスの動作数: 0/1", "Application status starts with the total tab count");
        Assert(window.SelectedSession!.FindName("StatusIndicator") == null && window.SelectedSession.FindName("ClientCountText") == null,
            "Per-session status bar is removed");
        Assert(window.SelectedSession!.FindName("LocalIpText") == null && window.SelectedSession!.FindName("CopyEndpointButton") == null, "IP display and copy button removed");
        Assert(window.SelectedSession!.FindName("SaveSettingsButton") == null && window.SelectedSession!.FindName("SettingsFileText") == null, "Save button and file path removed");
        Find<TextBox>("ListenPortBox").Text = "12000";
        window.SetRtlTcpPath(@"C:\日本語 tools\rtl_tcp.exe");
        await Until(() => WorkspaceSettingsStore.Load(guiSettingsDirectory).Sessions[0].ListenPort == 12000 && WorkspaceSettingsStore.Load(guiSettingsDirectory).RtlTcpPath.Contains("日本語"));
        Find<TextBox>("ListenPortBox").Text = "-";
        await Task.Delay(450);
        Assert(WorkspaceSettingsStore.Load(guiSettingsDirectory).Sessions[0].ListenPort == 12000, "Invalid edit preserves last valid settings");
        Find<TextBox>("ListenPortBox").Text = "11234";
        await Until(() => WorkspaceSettingsStore.Load(guiSettingsDirectory).Sessions[0].ListenPort == 11234);
        foreach (var name in new[] { "LockFrequencyBox", "LockPpmBox", "LockGainBox", "LockSampleRateBox" })
            Find<CheckBox>(name).IsChecked = true;
        Find<CheckBox>("AutoGainBox").IsChecked = false;
        Find<TextBox>("PpmBox").Text = "0";
        Find<TextBox>("GainBox").Text = "0";
        Find<RepeatButton>("PpmUpButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Find<RepeatButton>("GainUpButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert(Find<TextBox>("PpmBox").Text == "1" && Find<TextBox>("GainBox").Text == "1", "Spin buttons step by one");
        Find<RepeatButton>("PpmDownButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Find<RepeatButton>("GainDownButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert(Find<TextBox>("PpmBox").Text == "0" && Find<TextBox>("GainBox").Text == "0", "Down buttons step by one");
        foreach (var name in new[] { "PpmBox", "GainBox" })
            Find<TextBox>(name).RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
        Assert(Find<TextBox>("PpmBox").Text == "-1" && Find<TextBox>("GainBox").Text == "-1", "Wheel steps by one");
        var sampleRate = Find<ComboBox>("SampleRateBox");
        Assert(!sampleRate.IsEditable, "Sample rate selected from dropdown");
        sampleRate.SelectedItem = "2.4";
        await Until(() => WorkspaceSettingsStore.Load(guiSettingsDirectory).Sessions[0].ManagedSampleRate == 2_400_000 &&
            WorkspaceSettingsStore.Load(guiSettingsDirectory).Sessions[0].Ppm == -1 && WorkspaceSettingsStore.Load(guiSettingsDirectory).Sessions[0].GainTenthsDb == -10);
        sampleRate.SelectedItem = "2.048";
        Find<TextBox>("PpmBox").Text = "0";
        Find<TextBox>("GainBox").Text = "0";
        Find<CheckBox>("AutoGainBox").IsChecked = true;
        Assert(!Find<RepeatButton>("GainUpButton").IsEnabled, "Auto gain disables spinner");
        foreach (var name in new[] { "LockFrequencyBox", "LockPpmBox", "LockGainBox", "LockSampleRateBox" })
            Find<CheckBox>(name).IsChecked = false;
        Assert(FindApp<TextBlock>("ApplicationStatusIndicator").Text == "■" && FindApp<TextBlock>("ApplicationStatusIndicator").Foreground == Brushes.Firebrick,
            "Stopped application status indicator");
        Assert(clients.ActualHeight > Find<TextBox>("LogBox").ActualHeight, "Connected apps area receives more vertical space than the log");
        var clearLogButton = Find<Button>("ClearLogButton");
        var logGroup = Find<GroupBox>("LogGroup");
        var clearLogBounds = clearLogButton.TransformToAncestor(logGroup).TransformBounds(new Rect(new Size(clearLogButton.ActualWidth, clearLogButton.ActualHeight)));
        Assert(clearLogBounds.Right >= logGroup.ActualWidth - 100 && clearLogBounds.Right <= logGroup.ActualWidth - 8,
            "Clear button stays inside the right edge of the log header");
        var clientHeaders = clients.Columns.Select(column => column.Header?.ToString()).ToArray();
        Assert(clientHeaders.Contains("接続時間") && clientHeaders.Contains("操作権限"), "Connected app headers clarify duration and control permissions");
        Capture(window, screenshotDirectory, "local-default.png");
        window.Width = 680;
        window.Height = 560;
        await Task.Delay(100);
        Capture(window, screenshotDirectory, "compact-680x560.png");
        var content = (FrameworkElement)window.Content;
        foreach (var element in new FrameworkElement[] { start, Find<TextBox>("ListenPortBox"), clients, Find<Button>("DisconnectButton"), Find<TextBox>("LogBox"), FindApp<TextBlock>("ApplicationTrafficText"), Find<ComboBox>("SampleRateBox") })
        {
            var bounds = element.TransformToAncestor(content).TransformBounds(new Rect(new Size(element.ActualWidth, element.ActualHeight)));
            Assert(bounds.Bottom <= content.ActualHeight + 1 && bounds.Right <= content.ActualWidth + 1,
                $"Essential controls fit at 680x560: {element.Name} ends at {bounds.Right:F0},{bounds.Bottom:F0} in {content.ActualWidth:F0}x{content.ActualHeight:F0}");
        }
        Find<RadioButton>("RemoteSourceRadio").IsChecked = true;
        var lan = Find<CheckBox>("LanBox");
        lan.IsChecked = true;
        Assert(window.SelectedSession!.CurrentSettings.ListenAddress == "0.0.0.0", "LAN mode updates bind address");
        lan.IsChecked = false;
        using var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var relayPort = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();
        host.Text = "127.0.0.1";
        Find<TextBox>("UpstreamPortBox").Text = ((IPEndPoint)upstream.LocalEndpoint).Port.ToString();
        Find<TextBox>("ListenPortBox").Text = relayPort.ToString();
        start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        using var source = await upstream.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await source.GetStream().WriteAsync(new byte[] { 82, 84, 76, 48, 0, 0, 0, 5, 0, 0, 0, 29 });
        await Until(() => window.SelectedSession!.IsStreaming && FindApp<TextBlock>("ApplicationActivityText").Text == "SDRデバイスの動作数: 1/1");
        Assert(!host.IsEnabled && !lan.IsEnabled, "Settings disabled during stream");
        using var a = new TcpClient();
        using var b = new TcpClient();
        await a.ConnectAsync(IPAddress.Loopback, relayPort);
        await b.ConnectAsync(IPAddress.Loopback, relayPort);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await a.GetStream().ReadExactlyAsync(new byte[12], timeout.Token);
        await b.GetStream().ReadExactlyAsync(new byte[12], timeout.Token);
        await Until(() => clients.Items.Count == 2);
        await Until(() => TabConnectionCount(window.SelectedSession!) == "(2)");
        var logBox = Find<TextBox>("LogBox");
        await Until(() => logBox.Text.Length > 0);
        Find<Button>("ClearLogButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert(logBox.Text.Length == 0, "Clear button empties the log");
        Assert(FindApp<TextBlock>("ApplicationStatusIndicator").Text == "●" && FindApp<TextBlock>("ApplicationStatusIndicator").Foreground == Brushes.ForestGreen &&
            TabStatusIndicator(window.SelectedSession!).Text == "●" && TabStatusIndicator(window.SelectedSession!).Foreground == Brushes.ForestGreen &&
            FindApp<TextBlock>("ApplicationClientCountText").Text == "全接続数: 2台", "Application streaming state and total client count");
        clients.SelectedIndex = 1;
        Assert(window.SelectedSession!.FindName("GrantControlButton") == null, "Individual ownership button removed");
        await b.GetStream().WriteAsync(new byte[] { 1, 0, 0, 0, 123 });
        var command = new byte[5];
        await source.GetStream().ReadExactlyAsync(command, timeout.Token);
        Assert(command[4] == 123, "GUI accepts commands from second client");
        await b.GetStream().WriteAsync(new byte[] { 5, 255, 255, 255, 244 });
        await source.GetStream().ReadExactlyAsync(command, timeout.Token);
        Assert(command[0] == 5 && System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(command.AsSpan(1, 4)) == -12,
            "Client PPM command reaches upstream");
        await b.GetStream().WriteAsync(new byte[] { 1, 8, 172, 39, 96 }); // 145.5 MHz
        await source.GetStream().ReadExactlyAsync(command, timeout.Token);
        await b.GetStream().WriteAsync(new byte[] { 2, 0, 15, 160, 0 }); // 1.024 MS/s
        await source.GetStream().ReadExactlyAsync(command, timeout.Token);
        await b.GetStream().WriteAsync(new byte[] { 5, 255, 255, 255, 235 }); // -21 ppm
        await source.GetStream().ReadExactlyAsync(command, timeout.Token);
        await b.GetStream().WriteAsync(new byte[] { 3, 1, 0, 0, 0 }.Concat(new byte[] { 4, 0, 0, 0, 143 }).ToArray());
        await source.GetStream().ReadExactlyAsync(new byte[10], timeout.Token);
        await Until(() => Find<TextBox>("FrequencyBox").Text == "145.5" &&
            Find<TextBox>("PpmBox").Text == "-21" && Find<TextBox>("GainBox").Text == "14.3" &&
            Find<CheckBox>("AutoGainBox").IsChecked == false && Find<ComboBox>("SampleRateBox").SelectedItem?.ToString() == "1.024");
        Assert(!Find<TextBox>("FrequencyBox").IsEnabled && !Find<TextBox>("PpmBox").IsEnabled &&
            !Find<TextBox>("GainBox").IsEnabled && !Find<ComboBox>("SampleRateBox").IsEnabled,
            "Unlocked setting fields reflect client values while disabled");
        foreach (var name in new[] { "LockFrequencyBox", "LockPpmBox", "LockGainBox", "LockSampleRateBox" })
            Find<CheckBox>(name).IsChecked = true;
        var managed = new byte[30];
        await source.GetStream().ReadExactlyAsync(managed, timeout.Token);
        Assert(managed[0] == 1 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(managed.AsSpan(1, 4)) == 145_500_000,
            "GUI managed frequency reaches upstream");
        Assert(managed[5] == 2 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(managed.AsSpan(6, 4)) == 1_024_000,
            "GUI managed sample rate reaches upstream");
        Assert(managed[10] == 5 && System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(managed.AsSpan(11, 4)) == -21,
            "GUI signed PPM reaches upstream");
        Assert(managed[20] == 3 && managed[25] == 4 && System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(managed.AsSpan(26, 4)) == 143,
            "GUI gain uses tenths of dB");
        await Until(() => WorkspaceSettingsStore.Load(guiSettingsDirectory).Sessions[0].LockCenterFrequency &&
            WorkspaceSettingsStore.Load(guiSettingsDirectory).Sessions[0].LockPpm &&
            WorkspaceSettingsStore.Load(guiSettingsDirectory).Sessions[0].LockGain &&
            WorkspaceSettingsStore.Load(guiSettingsDirectory).Sessions[0].LockSampleRate &&
            WorkspaceSettingsStore.Load(guiSettingsDirectory).Sessions[0].GainTenthsDb == 143);
        Assert(Find<TextBox>("FrequencyBox").IsEnabled, "Managed controls editable while streaming");
        Capture(window, screenshotDirectory, "managed-control.png");
        Find<TextBox>("FrequencyBox").Text = "144";
        await source.GetStream().ReadExactlyAsync(managed, timeout.Token);
        Assert(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(managed.AsSpan(1, 4)) == 144_000_000, "Live tuning edit");
        foreach (var name in new[] { "LockFrequencyBox", "LockPpmBox", "LockGainBox", "LockSampleRateBox" })
            Find<CheckBox>(name).IsChecked = false;
        await Until(() => WorkspaceSettingsStore.Load(guiSettingsDirectory).Sessions[0].AcceptClientCommands);
        Assert(!Find<TextBox>("FrequencyBox").IsEnabled, "Client control disables local tuning");
        await source.GetStream().WriteAsync(new byte[] { 12, 13 });
        await a.GetStream().ReadExactlyAsync(new byte[2], timeout.Token);
        await b.GetStream().ReadExactlyAsync(new byte[2], timeout.Token);
        await Task.Delay(600);
        Capture(window, screenshotDirectory, "streaming.png");
        Capture(window, screenshotDirectory, "clients.png");
        Find<Button>("DisconnectButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => clients.Items.Count == 1);
        start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => start.Content?.ToString() == "配信開始" && start.IsEnabled);
        Assert(host.IsEnabled && lan.IsEnabled, "Settings restored after stop");
        Assert(clients.Items.Count == 0, "GUI clears clients after stop");
        await Until(() => FindApp<TextBlock>("ApplicationActivityText").Text == "SDRデバイスの動作数: 0/1" &&
            FindApp<TextBlock>("ApplicationClientCountText").Text == "全接続数: 0台");
        Assert(FindApp<TextBlock>("ApplicationStatusIndicator").Text == "■" && FindApp<TextBlock>("ApplicationStatusIndicator").Foreground == Brushes.Firebrick,
            "Stopped application status updates globally");
        Capture(window, screenshotDirectory, "stopped.png");
        Assert(upstream.Server.IsBound, "Remote server is not stopped by GUI");
        await ExerciseLocal(window, screenshotDirectory);
    }

    private static async Task ExerciseLocal(MainWindow window, string directory)
    {
        // The apphost fixture uses the same .NET installation as the test process.
        var runtimeDirectory = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        var dotnetRoot = Path.GetFullPath(Path.Combine(runtimeDirectory, "..", "..", ".."));
        Environment.SetEnvironmentVariable("DOTNET_ROOT", dotnetRoot);
        var builtFixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "RtlCast.FakeRtlTcp", "bin", "Release", "net10.0", "RtlCast.FakeRtlTcp.exe"));
        Assert(File.Exists(builtFixture), "Fixture executable exists");
        var fixtureDirectory = Path.GetFullPath(Path.Combine(directory, "..", "fake rtl tcp"));
        Directory.CreateDirectory(fixtureDirectory);
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(builtFixture)!))
            File.Copy(file, Path.Combine(fixtureDirectory, Path.GetFileName(file)), true);
        var fixture = Path.Combine(fixtureDirectory, Path.GetFileName(builtFixture));

        await using var unrelated = new RtlTcpProcess();
        unrelated.Start(fixture);
        await using (var manager = new RtlTcpProcess())
        {
            manager.Start(fixture);
            var id = manager.ProcessId!.Value;
            using var owned = Process.GetProcessById(id);
            await Task.Delay(200);
            Assert(!manager.HasExited, "Owned process starts");
            await manager.StopAsync();
            Assert(owned.HasExited, "Only owned process is stopped");
            Assert(!unrelated.HasExited, "An unrelated rtl_tcp is left running");
            try { manager.Start(Path.Combine(directory, "missing.exe")); throw new Exception("Missing executable accepted"); }
            catch (ArgumentException) { }
        }
        await unrelated.StopAsync();

        var flag = Path.Combine(Path.GetDirectoryName(fixture)!, "exit-immediately.flag");
        try
        {
            File.WriteAllText(flag, "test");
            await using var failed = new RtlTcpProcess();
            failed.Start(fixture);
            await Until(() => failed.HasExited);
            Assert(failed.ExitDescription.Contains("終了コード: 7"), "Early exit code is reported");
        }
        finally { File.Delete(flag); }

        var radio = (RadioButton)window.SelectedSession!.FindName("LocalSourceRadio");
        var path = (ComboBox)window.SelectedSession!.FindName("DeviceBox");
        var start = (Button)window.SelectedSession!.FindName("StartStopButton");
        radio.IsChecked = true;
        window.SetRtlTcpPath(fixture);
        start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => start.IsEnabled && start.Content?.ToString() == "配信停止");
        Assert(!path.IsEnabled && !radio.IsEnabled, "Local settings locked during streaming");
        Assert(window.SelectedSession!.SessionStatus == "配信中", "Local GUI reaches streaming");
        Capture(window, directory, "local-streaming.png");
        start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => start.IsEnabled && start.Content?.ToString() == "配信開始");
        Assert(path.IsEnabled && radio.IsEnabled, "Local settings restored on stop");
        // Restart, then simulate an external crash to exercise GUI monitoring and cleanup.
        start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => start.IsEnabled && start.Content?.ToString() == "配信停止");
        var managerField = typeof(SessionView).GetField("localTcp", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var windowManager = (RtlTcpProcess)managerField.GetValue(window.SelectedSession)!;
        using (var owned = Process.GetProcessById(windowManager.ProcessId!.Value)) { owned.Kill(); await owned.WaitForExitAsync(); }
        await Until(() => start.IsEnabled && start.Content?.ToString() == "配信開始");
        Assert(window.SelectedSession!.SessionStatus == "rtl_tcpを確認してください", "GUI reports unexpected exit");
        start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => start.IsEnabled && start.Content?.ToString() == "配信停止");
        var first = window.SelectedSession!;
        var tabs = (TabControl)window.FindName("SessionsTabs");
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().Last();
        var second = window.SelectedSession!;
        ((TextBox)second.FindName("SessionNameText")).Text = "ドングル 2";
        await Until(() => WorkspaceSettingsStore.Load(guiSettingsDirectory).Sessions.Single(s => s.SessionId == second.CurrentSettings.SessionId).SessionName == "ドングル 2");
        var secondTab = tabs.Items.OfType<TabItem>().Single(t => ReferenceEquals(t.Content, second));
        var secondHeader = (StackPanel)secondTab.Header;
        Assert(secondHeader.Children.OfType<TextBlock>().First().Text == "■" && secondHeader.Children.OfType<TextBlock>().Skip(1).First().Text == "ドングル 2" &&
            secondHeader.Children.OfType<TextBlock>().Skip(2).First().Text == "(0)", "Inline name and client count appear in tab header");
        Assert(window.Sessions.Count == 2 && first != second, "Add independent session tab");
        Assert(((ComboBox)second.FindName("DeviceBox")).Text == "1", "Next dongle number assigned");
        await Task.Delay(100);
        await second.RefreshDevicesAsync(_ => new[] { new SdrDevice(0, "Fixture SDR", "SERIAL-A", "SERIAL-A"), new SdrDevice(1, "Fixture SDR", "SERIAL-B", "SERIAL-B") });
        var devicePicker = (ComboBox)second.FindName("DeviceBox");
        Assert(devicePicker.SelectedItem is SdrDevice { Serial: "SERIAL-B" }, "Dropdown selects matching device index");
        await Until(() => second.CurrentSettings.DeviceIdentifier == "SERIAL-B");
        Assert(second.ResolveDeviceKey("1") == second.ResolveDeviceKey("SERIAL-B"), "Index and serial refer to same device");
        devicePicker.SelectedItem = null;
        devicePicker.Text = "1";
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        ((TextBox)second.FindName("ListenPortBox")).Text = ((IPEndPoint)reservation.LocalEndpoint).Port.ToString();
        reservation.Stop();
        await second.StartSessionAsync();
        Assert(first.IsRunning && second.IsRunning && first.OwnedProcessId != second.OwnedProcessId, "Two owned rtl_tcp processes stream simultaneously");
        await Until(() => ((TextBlock)window.FindName("ApplicationActivityText")).Text == "SDRデバイスの動作数: 2/2");
        Assert(((TextBlock)window.FindName("ApplicationClientCountText")).Text == "全接続数: 0台", "Application status sums client counts across tabs");
        var validate = typeof(MainWindow).GetMethod("ValidateStart", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        try { validate.Invoke(window, new object[] { second, second.CurrentSettings with { DeviceIdentifier = first.CurrentSettings.DeviceIdentifier } }); throw new Exception("Duplicate dongle accepted"); }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is ArgumentException) { }
        var persisted = WorkspaceSettingsStore.Load(guiSettingsDirectory);
        Assert(persisted.Sessions.Count == 2 && persisted.RtlTcpPath == fixture, "Multiple sessions and shared path saved");
        Assert(File.ReadAllLines(Path.Combine(guiSettingsDirectory, "settings.ini")).Count(l => l.StartsWith("RtlTcpPath=")) == 1, "RTL-TCP path has a single source");
        Capture(window, directory, "multiple-sessions.png");
        await first.StopSessionAsync();
        Assert(second.IsRunning && second.OwnedProcessId.HasValue, "Stopping one session leaves another running");
        await Until(() => ((TextBlock)window.FindName("ApplicationActivityText")).Text == "SDRデバイスの動作数: 1/2");
        await first.StartSessionAsync();
        using (var child = Process.GetProcessById(second.OwnedProcessId!.Value)) { child.Kill(); await child.WaitForExitAsync(); }
        await Until(() => !second.IsRunning);
        Assert(first.IsRunning, "One process failure does not stop another session");
        await Until(() => ((TextBlock)window.FindName("ApplicationActivityText")).Text == "SDRデバイスの動作数: 1/2");
        var secondLifecycleGate = (SemaphoreSlim)typeof(SessionView).GetField("lifecycleGate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(second)!;
        await Until(() => secondLifecycleGate.CurrentCount == 1);
        var closeButton = secondHeader.Children.OfType<Button>().Single();
        Assert(closeButton.IsEnabled, "Close button enabled when multiple tabs exist");
        closeButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, closeButton));
        await Until(() => window.Sessions.Count == 1);
        await Until(() => ((TextBlock)window.FindName("ApplicationActivityText")).Text == "SDRデバイスの動作数: 1/1");
        Assert(WorkspaceSettingsStore.Load(guiSettingsDirectory).Sessions.Count == 1, "Deleting a session persists removal");
        Assert(!((Button)((StackPanel)tabs.Items.OfType<TabItem>().Single(t => t.Content is SessionView).Header).Children.OfType<Button>().Single()).IsEnabled,
            "Last session tab cannot be closed");
        var settingsDialog = new SettingsWindow(fixture, true, true) { Owner = window };
        settingsDialog.Show();
        await Task.Delay(100);
        Assert(settingsDialog.RtlTcpPath == fixture && settingsDialog.StartWithWindows && settingsDialog.AutoStartStreaming,
            "Shared settings screen loads path and startup preferences");
        Capture(settingsDialog, directory, "application-settings.png");
        settingsDialog.Close();
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().Last();
        var finalSecond = window.SelectedSession!;
        using var finalReservation = new TcpListener(IPAddress.Loopback, 0);
        finalReservation.Start();
        ((TextBox)finalSecond.FindName("ListenPortBox")).Text = ((IPEndPoint)finalReservation.LocalEndpoint).Port.ToString();
        finalReservation.Stop();
        await finalSecond.StartSessionAsync();
        await ExerciseSerial(window, directory);
        await first.StartSessionAsync();
        await finalSecond.StartSessionAsync();
        secondProcessToClose = Process.GetProcessById(finalSecond.OwnedProcessId!.Value);
        processToClose = Process.GetProcessById(first.OwnedProcessId!.Value);
        Console.WriteLine("PASS multiple processes, independent stop/failure, duplicate device, tab persistence, shared settings");
        Console.WriteLine("PASS local process startup, stop, restart, missing file, early exit, unexpected exit");
    }

    private static void Capture(Window window, string directory, string filename)
    {
        Directory.CreateDirectory(directory);
        var content = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(content);
        var image = new RenderTargetBitmap((int)(content.ActualWidth * dpi.DpiScaleX),
            (int)(content.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        image.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(Path.Combine(directory, filename));
        encoder.Save(file);
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("GUI state did not update");
            await Task.Delay(50);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
