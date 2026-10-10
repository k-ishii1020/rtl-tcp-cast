// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 いしかず
using System.Windows;

namespace RtlCast.App;
public partial class App : Application
{
    private readonly bool showMainWindow;
    public App() : this(true) { }
    public App(bool showMainWindow) => this.showMainWindow = showMainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (showMainWindow) new MainWindow().Show();
    }
}
