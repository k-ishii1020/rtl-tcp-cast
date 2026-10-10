// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 いしかず
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace RtlCast.App;

public partial class SerialNumberWindow : Window
{
    private readonly string path;
    private readonly Func<string, IReadOnlyList<SdrDevice>> enumerate;
    private readonly Func<string, uint, byte[]> read;
    private readonly Action<string, uint, byte[], string> write;
    private byte[]? original;
    private IReadOnlyList<SdrDevice> devices = Array.Empty<SdrDevice>();
    private bool working;
    private bool closed;
    private int selectionRevision;
    public SdrDevice? ChangedDevice { get; private set; }
    public string NewSerial { get; private set; } = "";

    public SerialNumberWindow(string path, Func<string, IReadOnlyList<SdrDevice>>? enumerate = null,
        Func<string, uint, byte[]>? read = null, Action<string, uint, byte[], string>? write = null)
    {
        this.path = path;
        this.enumerate = enumerate ?? SdrDevices.Enumerate;
        this.read = read ?? SdrEeprom.Read;
        this.write = write ?? ((p, i, image, id) => SdrEeprom.Write(p, i, image, id));
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            try
            {
                devices = await Task.Run(() => this.enumerate(path));
                if (closed) return;
                DevicePicker.ItemsSource = devices;
                if (devices.Count > 0) DevicePicker.SelectedIndex = 0;
                else MessageText.Text = "デバイスが見つかりません。USB接続とドライバーを確認してください。";
            }
            catch (Exception ex) { MessageText.Text = ex.Message; }
        };
    }

    private async void Device_Changed(object sender, SelectionChangedEventArgs e)
    {
        var revision = ++selectionRevision;
        original = null;
        SaveButton.IsEnabled = SerialBox.IsEnabled = false;
        if (DevicePicker.SelectedItem is not SdrDevice device) return;
        MessageText.Text = "EEPROMを読み取っています…";
        try
        {
            var image = await Task.Run(() => read(path, device.Index));
            if (revision != selectionRevision) return;
            var info = EepromSerial.Inspect(image);
            original = image;
            SerialBox.MaxLength = info.MaximumLength;
            SerialBox.Text = info.Serial;
            LimitText.Text = $"英数字・ハイフン・アンダースコア、最大{info.MaximumLength}文字";
            SerialBox.IsEnabled = info.MaximumLength > 0;
            MessageText.Text = "保存後はUSBを抜き差しして、新しいIDを反映してください。";
            UpdateSaveEnabled();
        }
        catch (Exception ex) { if (revision == selectionRevision) MessageText.Text = ex.Message; }
    }

    private void Serial_Changed(object sender, TextChangedEventArgs e) => UpdateSaveEnabled();
    private void UpdateSaveEnabled()
    {
        if (SaveButton == null) return;
        try
        {
            if (original == null || working) { SaveButton.IsEnabled = false; return; }
            var info = EepromSerial.Inspect(original);
            EepromSerial.Validate(SerialBox.Text, info.MaximumLength);
            SaveButton.IsEnabled = SerialBox.Text != info.Serial && !devices.Any(d => d.Index != (DevicePicker.SelectedItem as SdrDevice)?.Index && d.Serial == SerialBox.Text);
        }
        catch (ArgumentException) { SaveButton.IsEnabled = false; }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveButton.IsEnabled || working || original == null || DevicePicker.SelectedItem is not SdrDevice selected) return;
        var serial = SerialBox.Text;
        try { EepromSerial.Validate(serial, EepromSerial.Inspect(original).MaximumLength); }
        catch (ArgumentException ex) { MessageText.Text = ex.Message; return; }
        working = true;
        SaveButton.IsEnabled = DevicePicker.IsEnabled = SerialBox.IsEnabled = CloseButton.IsEnabled = false;
        MessageText.Text = "シリアル番号を保存しています…";
        try
        {
            var found = await Task.Run(() => enumerate(path));
            if (!found.Any(d => d.Index == selected.Index && d.Name == selected.Name && d.Serial == selected.Serial))
                throw new IOException("デバイス構成が変わりました。画面を開き直してください。");
            if (found.Any(d => d.Index != selected.Index && d.Serial == serial)) throw new ArgumentException("同じIDのデバイスが存在します。");
            await Task.Run(() => write(path, selected.Index, original, serial));
            ChangedDevice = selected;
            NewSerial = serial;
            working = false;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageText.Text = ex.Message;
            working = false;
            DevicePicker.IsEnabled = SerialBox.IsEnabled = CloseButton.IsEnabled = true;
            UpdateSaveEnabled();
        }
    }
    private void Window_Closing(object? sender, CancelEventArgs e) { if (working) { e.Cancel = true; return; } closed = true; selectionRevision++; }
}
