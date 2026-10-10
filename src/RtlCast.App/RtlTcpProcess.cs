// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 いしかず
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using RtlCast.Core;

namespace RtlCast.App;

/// <summary>Owns only the process launched by this instance; never searches for or kills other rtl_tcp instances.</summary>
public sealed class RtlTcpProcess : IAsyncDisposable
{
    private Process? process;
    private readonly object sync = new();
    private readonly Queue<string> diagnostics = new();
    public event Action<RelayLog>? Logged;
    public int Port { get; private set; }
    public int? ProcessId => process?.Id;
    public bool HasExited => process?.HasExited == true;

    public string ExitDescription
    {
        get
        {
            lock (sync)
                return $"rtl_tcpが終了しました（終了コード: {process?.ExitCode}）。SDRデバイス、WinUSBドライバー、同じフォルダーのDLLを確認してください。\n{string.Join("\n", diagnostics)}";
        }
    }

    public void Start(string executablePath, int excludedPort = 0, string deviceIdentifier = "0", int ppm = 0)
    {
        if (process != null) throw new InvalidOperationException("rtl_tcpはすでに起動されています。");
        if (string.IsNullOrWhiteSpace(deviceIdentifier)) throw new ArgumentException("SDRデバイスの番号またはシリアル番号を入力してください。");
        if (string.IsNullOrWhiteSpace(executablePath)) throw new ArgumentException("設定 → アプリ全体の設定でrtl_tcp.exeを指定してください。");
        var path = Path.GetFullPath(executablePath.Trim().Trim('"'));
        if (!File.Exists(path) || !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("設定 → アプリ全体の設定で実在するrtl_tcp.exeを指定してください。");
        var deviceIndex = SdrDevices.ResolveDeviceIndex(path, deviceIdentifier);
        do
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            Port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        }
        while (Port == excludedPort);
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(path)!,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("-a"); start.ArgumentList.Add("127.0.0.1");
        start.ArgumentList.Add("-p"); start.ArgumentList.Add(Port.ToString());
        start.ArgumentList.Add("-d"); start.ArgumentList.Add(deviceIndex);
        start.ArgumentList.Add("-P"); start.ArgumentList.Add(ppm.ToString());
        var owned = new Process { StartInfo = start };
        owned.OutputDataReceived += OnOutput;
        owned.ErrorDataReceived += OnOutput;
        try
        {
            lock (sync) diagnostics.Clear();
            if (!owned.Start()) throw new InvalidOperationException("rtl_tcpを起動できませんでした。");
            process = owned;
            owned.BeginOutputReadLine();
            owned.BeginErrorReadLine();
            Logged?.Invoke(new(DateTimeOffset.Now, $"rtl_tcpを起動しました: {path}（デバイス {deviceIndex}、PPM {ppm}、ポート {Port}）"));
        }
        catch
        {
            if (process == null) owned.Dispose();
            throw;
        }
    }

    private void OnOutput(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data)) return;
        var line = e.Data.Length > 1000 ? e.Data[..1000] : e.Data;
        lock (sync)
        {
            diagnostics.Enqueue(line);
            while (diagnostics.Count > 10) diagnostics.Dequeue();
        }
        Logged?.Invoke(new(DateTimeOffset.Now, $"rtl_tcp: {line}"));
    }

    public async Task StopAsync()
    {
        var owned = process;
        if (owned == null) return;
        try
        {
            if (!owned.HasExited)
            {
                // rtl_tcp is a hidden console process without a GUI close message.
                // Terminate this exact owned process; Windows releases its sockets and USB handles.
                try { owned.Kill(); }
                catch (InvalidOperationException) when (owned.HasExited) { }
            }
            await owned.WaitForExitAsync();
            Logged?.Invoke(new(DateTimeOffset.Now, "アプリが起動したrtl_tcpを停止しました。"));
        }
        finally { owned.Dispose(); process = null; }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
