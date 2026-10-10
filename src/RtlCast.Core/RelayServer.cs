// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 いしかず
using System.Buffers.Binary;
using System.Collections.Concurrent;
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
                    Interlocked.Read(ref c.BytesSent), options.AcceptClientCommands)).ToArray(), ppm, automaticGain, gainTenthsDb, gainIndex);
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

    public async Task ConfigureControlAsync(RelayOptions settings)
    {
        settings.Validate();
        await commandWrite.WaitAsync();
        try
        {
            TcpClient? target;
            CancellationToken token;
            lock (sync)
            {
                options = options with { AcceptClientCommands = settings.AcceptClientCommands,
                    CenterFrequency = settings.CenterFrequency, Ppm = settings.Ppm,
                    AutomaticGain = settings.AutomaticGain, GainTenthsDb = settings.GainTenthsDb,
                    ManagedSampleRate = settings.ManagedSampleRate };
                target = upstream;
                token = runCts?.Token ?? CancellationToken.None;
            }
            if (target != null && !settings.AcceptClientCommands)
                await ApplyManagedSettingsAsync(target, settings, token);
        }
        finally { commandWrite.Release(); }
    }

    private async Task ApplyManagedSettingsAsync(TcpClient target, RelayOptions settings, CancellationToken token)
    {
        // Frequency/PPM/gain are shared by every receiver. Disable digital AGC for manual gain.
        var commands = new List<(byte Code, uint Value)> { (1, settings.CenterFrequency),
            (2, settings.ManagedSampleRate), (5, unchecked((uint)settings.Ppm)),
            (8, 0), (3, settings.AutomaticGain ? 0u : 1u) };
        if (!settings.AutomaticGain) commands.Add((4, unchecked((uint)settings.GainTenthsDb)));
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
            frequency = settings.CenterFrequency;
            sampleRate = settings.ManagedSampleRate;
            ppm = settings.Ppm;
            automaticGain = settings.AutomaticGain;
            gainTenthsDb = settings.AutomaticGain ? null : settings.GainTenthsDb;
            gainIndex = null;
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
                        ppm = null;
                        automaticGain = null;
                        gainTenthsDb = null;
                        gainIndex = null;
                        current = options;
                    }
                    if (!current.AcceptClientCommands) await ApplyManagedSettingsAsync(socket, current, token);
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
                TcpClient? target;
                lock (sync) target = options.AcceptClientCommands ? upstream : null;
                if (target == null)
                {
                    if (!client.ReadOnlyNoticeLogged)
                    {
                        client.ReadOnlyNoticeLogged = true;
                        Log($"クライアント操作ロック中のため、設定変更を無視しました: {client.Endpoint}");
                    }
                    continue;
                }
                // RTL TCP commands are 1 opcode + 4 parameter bytes, forwarded verbatim.
                await WriteWithTimeoutAsync(target.GetStream(), command, client.Token);
                var value = BinaryPrimitives.ReadUInt32BigEndian(command.AsSpan(1));
                lock (sync)
                {
                    if (target != upstream) continue;
                    if (command[0] == 1) frequency = value;
                    if (command[0] == 2) sampleRate = value;
                    if (command[0] == 5) ppm = unchecked((int)value);
                    if (command[0] == 3) automaticGain = value == 0;
                    if (command[0] == 4) { gainTenthsDb = unchecked((int)value); gainIndex = null; }
                    if (command[0] == 13) { gainIndex = value; gainTenthsDb = null; }
                }
            }
            finally { commandWrite.Release(); }
        }
    }

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
        public bool ReadOnlyNoticeLogged;

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
