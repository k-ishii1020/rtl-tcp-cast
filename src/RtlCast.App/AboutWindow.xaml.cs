// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 いしかず
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;

namespace RtlCast.App;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var assembly = typeof(AboutWindow).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "不明";
        VersionText.Text = $"v{version}";
        CopyrightText.Text = assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright
            ?? "Copyright (c) 2026 いしかず";
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, $"リンクを開けませんでした。\n{ex.Message}", "バージョン情報", MessageBoxButton.OK, MessageBoxImage.Error); }
        e.Handled = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
