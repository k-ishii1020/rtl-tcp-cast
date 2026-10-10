// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 いしかず
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace RtlCast.App;

public partial class SettingsWindow : Window
{
    public string RtlTcpPath => PathBox.Text.Trim().Trim('"');
    public bool StartWithWindows => WindowsStartupBox.IsChecked == true;
    public bool AutoStartStreaming => AutoStartStreamingBox.IsChecked == true;
    public SettingsWindow(string path, bool startWithWindows = false, bool autoStartStreaming = false)
    {
        InitializeComponent();
        PathBox.Text = path;
        WindowsStartupBox.IsChecked = startWithWindows;
        AutoStartStreamingBox.IsChecked = autoStartStreaming;
    }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "rtl_tcp.exeを選択", Filter = "実行ファイル (*.exe)|*.exe", CheckFileExists = true };
        if (picker.ShowDialog(this) == true) PathBox.Text = picker.FileName;
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (RtlTcpPath.Length > 0 && (!File.Exists(RtlTcpPath) || !Path.GetExtension(RtlTcpPath).Equals(".exe", StringComparison.OrdinalIgnoreCase)))
        { MessageBox.Show(this, "実在するrtl_tcp.exeを指定してください。", "設定", MessageBoxButton.OK); return; }
        DialogResult = true;
    }
}
