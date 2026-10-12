// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 いしかず
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace RtlCast.Core;

/// <summary>RTL TCP wire-compatible relay. No USB or process ownership belongs here.</summary>
public sealed class RelayServer : IAsyncDisposable
{
    private static readonly TimeSpan RejectedClientLogDelay = TimeSpan.FromSeconds(3);
    private readonly object sync = new();
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly SemaphoreSlim commandWrite = new(1, 1);
    private readonly Dictionary<Guid, ClientConnection> clients = new();
    private readonly ConcurrentDictionary<Guid, Task> clientTasks = new();
    private CancellationTokenSource? runCts;
    private TcpListener? listener;
    private TcpClient? upstream;
    private Task? acceptTask;
    private Task? upstreamTask;
    private RelayOptions options = new();
    private byte[]? header;
    private RelayStatus status;
    private string message = "配信停止中です。";
    private long bytesReceived;
    private uint? frequency;
    private uint? sampleRate;
    private int? ppm;
    private bool? automaticGain;
    private int? gainTenthsDb;
    private uint? gainIndex;

    public event Action<RelayLog>? Logged;

    public RelaySnapshot GetSnapshot()
    {
        lock (sync)
            return new(status, message, Interlocked.Read(ref bytesReceived), frequency, sampleRate,
                clients.Values.Select(c => new ClientSnapshot(c.Id, c.Endpoint, c.ConnectedAt,
                    Interlocked.Read(ref c.BytesSent), !options.LockCenterFrequency || !options.LockPpm || !options.LockGain || !options.LockSampleRate)).ToArray(), ppm, automaticGain, gainTenthsDb, gainIndex);
    }

    public async Task StartAsync(RelayOptions settings, int? initialPpm = null)
    {
        settings.Validate();
        await lifecycle.WaitAsync();
        try
        {
            if (runCts != null) throw new InvalidOperationException("配信はすでに開始されています。先に停止してください。");
            var newListener = new TcpListener(IPAddress.Parse(settings.ListenAddress), settings.ListenPort);
            newListener.Start();
            options = settings;
            listener = newListener;
            runCts = new CancellationTokenSource();
            var startedAt = DateTimeOffset.UtcNow;
            lock (sync)
            {
                frequency = sampleRate = null;
                ppm = initialPpm;
                automaticGain = null;
                gainTenthsDb = null;
                gainIndex = null;
                Interlocked.Exchange(ref bytesReceived, 0);
                status = RelayStatus.Connecting;
                message = "rtl_tcpに接続しています。";
            }
            Log($"配信開始: {settings.ListenAddress}:{settings.ListenPort}");
            var token = runCts.Token;
            acceptTask = AcceptClientsAsync(newListener, token, startedAt);
            upstreamTask = ReceiveUpstreamAsync(token);
        }
        finally { lifecycle.Release(); }
    }

    public async Task StopAsync()
    {
        await lifecycle.WaitAsync();
        try
        {
            if (runCts == null) return;
            runCts.Cancel();
            listener?.Stop();
            lock (sync) { upstream?.Dispose(); CloseClientsLocked(); }
            await Task.WhenAll(acceptTask!, upstreamTask!);
            await Task.WhenAll(clientTasks.Values.ToArray());
            runCts.Dispose();
            runCts = null;
            listener = null;
            SetStatus(RelayStatus.Stopped, "配信停止中です。");
            Log("配信を停止しました。");
        }
        finally { lifecycle.Release(); }
    }

    public async Task<RelayOptions> ConfigureControlAsync(RelayOptions settings)
    {
        settings.Validate();
        await commandWrite.WaitAsync();
        try
        {
            RelayOptions effective;
            TcpClient? target;
            CancellationToken token;
            lock (sync)
            {
                // Snapshot the latest client requests only after acquiring commandWrite, so
                // an in-flight command finishes before a newly enabled lock adopts its value.
                effective = settings with
                {
                    CenterFrequency = !options.LockCenterFrequency && settings.LockCenterFrequency && frequency is uint requestedFrequency
                        ? requestedFrequency : settings.CenterFrequency,
                    Ppm = !options.LockPpm && settings.LockPpm && ppm is int requestedPpm
                        ? requestedPpm : settings.Ppm,
                    AutomaticGain = !options.LockGain && settings.LockGain && automaticGain is bool requestedAutomaticGain
                        ? requestedAutomaticGain : settings.AutomaticGain,
                    GainTenthsDb = !options.LockGain && settings.LockGain && gainTenthsDb is int requestedGain
                        ? requestedGain : settings.GainTenthsDb,
                    ManagedSampleRate = !options.LockSampleRate && settings.LockSampleRate && sampleRate is uint requestedSampleRate
                        ? requestedSampleRate : settings.ManagedSampleRate
                };
                options = options with { AcceptClientCommands = effective.AcceptClientCommands,
                    LockCenterFrequency = effective.LockCenterFrequency, LockPpm = effective.LockPpm,
                    LockGain = effective.LockGain, LockSampleRate = effective.LockSampleRate,
                    CenterFrequency = effective.CenterFrequency, Ppm = effective.Ppm,
                    AutomaticGain = effective.AutomaticGain, GainTenthsDb = effective.GainTenthsDb,
                    ManagedSampleRate = effective.ManagedSampleRate };
                target = upstream;
                token = runCts?.Token ?? CancellationToken.None;
            }
            if (target != null)
                await ApplyManagedSettingsAsync(target, effective, token);
            return effective;
        }
        finally { commandWrite.Release(); }
    }

    private async Task ApplyManagedSettingsAsync(TcpClient target, RelayOptions settings, CancellationToken token)
    {
        var commands = new List<(byte Code, uint Value)>();
        if (settings.LockCenterFrequency) commands.Add((1, settings.CenterFrequency));
        if (settings.LockSampleRate) commands.Add((2, settings.ManagedSampleRate));
        if (settings.LockPpm) commands.Add((5, unchecked((uint)settings.Ppm)));
        if (settings.LockGain)
        {
            // Opcode 3 selects tuner automatic or manual gain. Digital AGC is kept disabled.
            commands.Add((8, 0));
            commands.Add((3, settings.AutomaticGain ? 0u : 1u));
            if (!settings.AutomaticGain) commands.Add((4, unchecked((uint)settings.GainTenthsDb)));
        }
        foreach (var (code, value) in commands)
        {
            var packet = new byte[5];
            packet[0] = code;
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(1), value);
            await WriteWithTimeoutAsync(target.GetStream(), packet, token);
        }
        lock (sync)
        {
            if (upstream != target) return;
            if (settings.LockCenterFrequency) frequency = settings.CenterFrequency;
            if (settings.LockSampleRate) sampleRate = settings.ManagedSampleRate;
            if (settings.LockPpm) ppm = settings.Ppm;
            if (settings.LockGain)
            {
                automaticGain = settings.AutomaticGain;
                gainTenthsDb = settings.AutomaticGain ? null : settings.GainTenthsDb;
                if (!settings.AutomaticGain) gainIndex = null;
            }
        }
    }

    public void DisconnectClient(Guid id)
    {
        lock (sync)
            if (clients.TryGetValue(id, out var client)) client.Close();
        Log("選択したアプリを切断しました。");
    }

    private async Task AcceptClientsAsync(TcpListener server, CancellationToken token, DateTimeOffset startedAt)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var socket = await server.AcceptTcpClientAsync(token);
                socket.NoDelay = true;
                ClientConnection? client = null;
                lock (sync)
                {
                    if (header != null && clients.Count < options.MaxClients && !token.IsCancellationRequested)
                    {
                        client = new(socket, header, options.ClientQueueCapacity, token);
                        clients.Add(client.Id, client);
                    }
                }
                if (client == null)
                {
                    socket.Dispose();
                    if (!token.IsCancellationRequested && DateTimeOffset.UtcNow - startedAt >= RejectedClientLogDelay)
                        Log("接続を受け付けられませんでした。rtl_tcp未接続、または接続数が上限に達しています。");
                    continue;
                }
                Log($"アプリ接続: {client.Endpoint}");
                var task = RunClientAsync(client);
                clientTasks[client.Id] = task;
                _ = task.ContinueWith(_ => { clientTasks.TryRemove(client.Id, out var ignored); },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (Exception ex) when (token.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        catch (Exception ex)
        {
            SetStatus(RelayStatus.Faulted, "待受処理でエラーが発生しました。停止してから再度開始してください。");
            Log($"待受エラー: {ex.Message}");
            runCts?.Cancel();
        }
    }

    private async Task ReceiveUpstreamAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            using var socket = new TcpClient { NoDelay = true };
            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                connectCts.CancelAfter(options.ConnectTimeout);
                await socket.ConnectAsync(options.UpstreamHost.Trim(), options.UpstreamPort, connectCts.Token);
                var stream = socket.GetStream();
                var newHeader = new byte[12];
                await stream.ReadExactlyAsync(newHeader, connectCts.Token);
                if (!newHeader.AsSpan(0, 4).SequenceEqual("RTL0"u8))
                    throw new InvalidDataException("接続先がRTL TCP形式のヘッダーを返しませんでした。");
                await commandWrite.WaitAsync(token);
                try
                {
                    RelayOptions current;
                    lock (sync)
                    {
                        upstream = socket;
                        frequency = sampleRate = null;
                        automaticGain = null;
                        gainTenthsDb = null;
                        gainIndex = null;
                        current = options;
                    }
                    if (current.LockCenterFrequency || current.LockPpm || current.LockGain || current.LockSampleRate)
                        await ApplyManagedSettingsAsync(socket, current, token);
                    lock (sync) header = newHeader;
                }
                finally { commandWrite.Release(); }
                SetStatus(RelayStatus.Streaming, "配信中です。受信アプリを接続してください。");
                Log($"rtl_tcp接続: {options.UpstreamHost}:{options.UpstreamPort}");
                var buffer = new byte[32 * 1024];
                var pending = 0;
                while (!token.IsCancellationRequested)
                {
                    var count = await stream.ReadAsync(buffer.AsMemory(pending), token);
                    if (count == 0) throw new EndOfStreamException("rtl_tcpとの接続が切れました。");
                    Interlocked.Add(ref bytesReceived, count);
                    var total = count + pending;
                    var evenCount = total & ~1;
                    if (evenCount > 0)
                    {
                        // Share immutable blocks; every late-joining client starts on an I/Q pair boundary.
                        var block = buffer.AsSpan(0, evenCount).ToArray();
                        List<string> slow = new();
                        lock (sync)
                            foreach (var client in clients.Values)
                                if (!client.Queue.Writer.TryWrite(block))
                                {
                                    client.Close();
                                    slow.Add(client.Endpoint);
                                }
                        foreach (var endpoint in slow) Log($"送信待ちが上限に達したため切断しました: {endpoint}");
                    }
                    pending = total - evenCount;
                    if (pending != 0) buffer[0] = buffer[evenCount];
                }
            }
            catch (Exception ex) when (ex is SocketException or IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
            {
                if (!token.IsCancellationRequested)
                    Log($"rtl_tcp接続エラー: {ex.Message} 接続先、rtl_tcpの起動状態、ファイアウォールを確認してください。");
            }
            finally
            {
                lock (sync)
                {
                    upstream = null;
                    header = null;
                    frequency = sampleRate = null;
                    ppm = null;
                    automaticGain = null;
                    gainTenthsDb = null;
                    gainIndex = null;
                    CloseClientsLocked();
                }
            }
            if (token.IsCancellationRequested) break;
            if (!options.AutoReconnect)
            {
                SetStatus(RelayStatus.Faulted, "rtl_tcpに接続できません。停止して接続先を確認してください。");
                break;
            }
            SetStatus(RelayStatus.Reconnecting, "rtl_tcpへの再接続を待っています。復旧後、受信アプリを再接続してください。");
            try { await Task.Delay(options.ReconnectDelay, token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunClientAsync(ClientConnection client)
    {
        try
        {
            var send = SendSamplesAsync(client);
            var commands = ReceiveCommandsAsync(client);
            var completed = await Task.WhenAny(send, commands);
            if (!client.Token.IsCancellationRequested)
            {
                if (completed.IsCanceled) Log($"送信タイムアウトのためアプリを切断します: {client.Endpoint}");
                else if (completed.Exception != null) Log($"アプリ通信終了: {client.Endpoint}: {completed.Exception.GetBaseException().Message}");
            }
            client.Close();
            await Task.WhenAll(send, commands);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            if (!client.Token.IsCancellationRequested) Log($"アプリ通信エラー: {client.Endpoint}: {ex.Message}");
        }
        finally
        {
            client.Close();
            lock (sync)
            {
                clients.Remove(client.Id);
            }
            client.Dispose();
            Log($"アプリ切断: {client.Endpoint}");
        }
    }

    private async Task SendSamplesAsync(ClientConnection client)
    {
        var stream = client.Socket.GetStream();
        await WriteWithTimeoutAsync(stream, client.Header, client.Token);
        await foreach (var block in client.Queue.Reader.ReadAllAsync(client.Token))
        {
            await WriteWithTimeoutAsync(stream, block, client.Token);
            Interlocked.Add(ref client.BytesSent, block.Length);
        }
    }

    private async Task WriteWithTimeoutAsync(NetworkStream stream, byte[] data, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(options.SendTimeout);
        await stream.WriteAsync(data, timeout.Token);
    }

    private async Task ReceiveCommandsAsync(ClientConnection client)
    {
        var stream = client.Socket.GetStream();
        var command = new byte[5];
        while (!client.Token.IsCancellationRequested)
        {
            await stream.ReadExactlyAsync(command, client.Token);
            await commandWrite.WaitAsync(client.Token);
            try
            {
                var opcode = command[0];
                var value = BinaryPrimitives.ReadUInt32BigEndian(command.AsSpan(1));
                TcpClient? target;
                bool locked;
                bool canForward;
                lock (sync)
                {
                    locked = IsCommandLocked(opcode, options);
                    target = locked ? null : upstream;
                    canForward = !locked && target != null && target == upstream;
                    if (canForward)
                    {
                        RecordRequestedSetting(opcode, value);
                    }
                }
                var detail = $"パラメーター操作: [{client.Endpoint}] {FormatClientCommand(opcode, value)}";
                Log(locked ? detail + "（ロック中）"
                    : canForward ? detail
                    : detail + "（転送先なし）");
                if (!canForward || target is null)
                    continue;
                // RTL TCP commands are 1 opcode + 4 parameter bytes, forwarded verbatim.
                await WriteWithTimeoutAsync(target.GetStream(), command, client.Token);
            }
            finally { commandWrite.Release(); }
        }
    }

    private void RecordRequestedSetting(byte opcode, uint value)
    {
        if (opcode == 1) frequency = value;
        if (opcode == 2) sampleRate = value;
        if (opcode == 5) ppm = unchecked((int)value);
        if (opcode == 3) automaticGain = value == 0;
        if (opcode == 4) { gainTenthsDb = unchecked((int)value); gainIndex = null; }
        if (opcode == 13) { gainIndex = value; gainTenthsDb = null; }
    }

    private static string FormatClientCommand(byte opcode, uint value) => opcode switch
    {
        1 => $"中心周波数 {(value / 1_000_000m).ToString("0.######", CultureInfo.InvariantCulture)}MHz",
        2 => $"サンプルレート {(value / 1_000_000m).ToString("0.###", CultureInfo.InvariantCulture)}MS/s",
        3 => value == 0 ? "Gain 自動" : "Gain 手動",
        4 => $"Gain {(unchecked((int)value) / 10m).ToString("0.#", CultureInfo.InvariantCulture)}dB",
        5 => $"周波数補正 {unchecked((int)value)}ppm",
        6 => $"IF Gain stage {value >> 16} {(unchecked((short)value) / 10m).ToString("0.#", CultureInfo.InvariantCulture)}dB",
        7 => $"テストモード {(value == 0 ? "OFF" : "ON")}",
        8 => $"デジタルAGC {(value == 0 ? "OFF" : "ON")}",
        9 => $"直接サンプリング {value}",
        10 => $"オフセットチューニング {(value == 0 ? "OFF" : "ON")}",
        11 => $"RTL基準クロック {value}Hz",
        12 => $"チューナー基準クロック {value}Hz",
        13 => $"Gain 段階 {value}",
        14 => $"バイアスT {(value == 0 ? "OFF" : "ON")}",
        _ => $"コマンド 0x{opcode:X2} 0x{value:X8}"
    };

    private static bool IsCommandLocked(byte opcode, RelayOptions settings) => opcode switch
    {
        1 => settings.LockCenterFrequency,
        2 => settings.LockSampleRate,
        3 or 4 or 13 => settings.LockGain,
        5 => settings.LockPpm,
        _ => false
    };

    private void CloseClientsLocked()
    {
        foreach (var client in clients.Values) client.Close();
    }

    private void SetStatus(RelayStatus value, string text)
    {
        lock (sync) { status = value; message = text; }
    }

    private void Log(string text) => Logged?.Invoke(new(DateTimeOffset.Now, text));
    public async ValueTask DisposeAsync() => await StopAsync();

    private sealed class ClientConnection : IDisposable
    {
        private readonly CancellationTokenSource cts;
        private int closed;
        public Guid Id { get; } = Guid.NewGuid();
        public TcpClient Socket { get; }
        public byte[] Header { get; }
        public string Endpoint { get; }
        public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.Now;
        public Channel<byte[]> Queue { get; }
        public CancellationToken Token { get; }
        public long BytesSent;
        public ClientConnection(TcpClient socket, byte[] initialHeader, int capacity, CancellationToken token)
        {
            Socket = socket;
            Header = initialHeader;
            Endpoint = socket.Client.RemoteEndPoint?.ToString() ?? "不明";
            cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            Token = cts.Token;
            Queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(capacity)
                { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref closed, 1) != 0) return;
            cts.Cancel();
            Queue.Writer.TryComplete();
            Socket.Dispose();
        }

        public void Dispose() { Close(); cts.Dispose(); }
    }
}
