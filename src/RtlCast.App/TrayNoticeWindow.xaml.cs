// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 いしかず
using System.Windows;

namespace RtlCast.App;

public partial class TrayNoticeWindow : Window
{
    public bool DoNotShowAgain => DoNotShowAgainBox.IsChecked == true;

    public TrayNoticeWindow() => InitializeComponent();

    private void Ok_Click(object sender, RoutedEventArgs e) => Close();
}
