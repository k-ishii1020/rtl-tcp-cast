// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 いしかず
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using RtlCast.Core;

// No external test packages: exercises actual loopback TCP sockets and the production relay.
var tests = new (string Name, Func<Task> Run)[]
{
    ("Header + identical fan-out + I/Q alignment + all-client commands", FanoutAndControl),
    ("Upstream disconnect + reconnect + all-client commands after reconnect", Reconnect),
    ("Stop while connecting + restart + release listen port", StopAndRestart),
    ("Invalid upstream header + no automatic reconnect", InvalidHeader),
    ("Slow consumer is disconnected without blocking another client", SlowConsumer),
    ("Occupied listen port leaves server reusable", OccupiedPort),
    ("Maximum clients is enforced", MaximumClients),
    ("Managed settings, blocked clients, live changes and reconnect reapply", ManagedControl)
};
var failed = 0;
foreach (var test in tests)
{
    try { await test.Run().WaitAsync(TimeSpan.FromSeconds(20)); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {test.Name}: {ex}"); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} passed");
return failed == 0 ? 0 : 1;

static async Task FanoutAndControl()
{
    using var mock = new MockUpstream();
    await using var relay = new RelayServer();
    await relay.StartAsync(Options(mock.Port));
    using var source = await mock.Accept();
    await mock.SendHeader(source, fragmented: true);
    await Until(() => relay.GetSnapshot().Status == RelayStatus.Streaming);
    using var a = await Connect(relay);
    using var b = await Connect(relay);
    await Until(() => relay.GetSnapshot().Clients.Count == 2);
    Assert(relay.GetSnapshot().Clients.All(c => c.HasControl), "Every client can control");

    // One odd byte is retained so later clients cannot start on a Q byte.
    await source.GetStream().WriteAsync(new byte[] { 10, 11, 12 });
    Assert((await Read(a, 2)).SequenceEqual(new byte[] { 10, 11 }), "A sample bytes");
    Assert((await Read(b, 2)).SequenceEqual(new byte[] { 10, 11 }), "B sample bytes");
    using var c = await Connect(relay);
    await source.GetStream().WriteAsync(new byte[] { 13, 14, 15 });
    foreach (var receiver in new[] { a, b, c })
        Assert((await Read(receiver, 4)).SequenceEqual(new byte[] { 12, 13, 14, 15 }), "IQ alignment/fan-out");

    await b.GetStream().WriteAsync(Command(1, 999));
    Assert((await Read(source, 5)).SequenceEqual(Command(1, 999)), "Second client command accepted");
    await a.GetStream().WriteAsync(Command(1, 100_000_000));
    Assert((await Read(source, 5)).SequenceEqual(Command(1, 100_000_000)), "First client command accepted");
    await Until(() => relay.GetSnapshot().RequestedFrequency == 100_000_000);
    await a.GetStream().WriteAsync(Command(5, unchecked((uint)-23)));
    Assert((await Read(source, 5)).SequenceEqual(Command(5, unchecked((uint)-23))), "Signed PPM command forwarded");
    await Until(() => relay.GetSnapshot().RequestedPpm == -23);
    await a.GetStream().WriteAsync(Command(3, 1));
    await a.GetStream().WriteAsync(Command(4, unchecked((uint)-10)));
    Assert((await Read(source, 10)).SequenceEqual(Command(3, 1).Concat(Command(4, unchecked((uint)-10)))), "Manual gain wire commands");
    await Until(() => relay.GetSnapshot().RequestedAutomaticGain == false && relay.GetSnapshot().RequestedGainTenthsDb == -10);
    await b.GetStream().WriteAsync(Command(13, 7));
    await Read(source, 5);
    await Until(() => relay.GetSnapshot().RequestedGainIndex == 7 && relay.GetSnapshot().RequestedGainTenthsDb == null);
    var bId = relay.GetSnapshot().Clients.Single(c => c.Endpoint.EndsWith($":{((IPEndPoint)b.Client.LocalEndPoint!).Port}")).Id;
    // Fragment a five-byte command across writes.
    var sampleCommand = Command(2, 2_048_000);
    await b.GetStream().WriteAsync(sampleCommand.AsMemory(0, 2));
    await b.GetStream().WriteAsync(sampleCommand.AsMemory(2));
    Assert((await Read(source, 5)).SequenceEqual(sampleCommand), "Fragmented command");
    await Until(() => relay.GetSnapshot().RequestedSampleRate == 2_048_000);
    relay.DisconnectClient(bId);
    await Until(() => relay.GetSnapshot().Clients.Count == 2);
    Assert(relay.GetSnapshot().Clients.All(c => c.HasControl), "Remaining clients keep control");
}

static async Task ManagedControl()
{
    using var mock = new MockUpstream();
    await using var relay = new RelayServer();
    var settings = Options(mock.Port) with { AcceptClientCommands = false,
        LockCenterFrequency = true, LockPpm = true, LockGain = true, LockSampleRate = true,
        CenterFrequency = 145_500_000, Ppm = -17, AutomaticGain = false, GainTenthsDb = 143 };
    var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    relay.Logged += log => { if (log.Message.Contains("（ロック中）", StringComparison.Ordinal)) blocked.TrySetResult(); };
    await relay.StartAsync(settings);
    var source = await mock.Accept();
    await mock.SendHeader(source);
    async Task CheckManaged(TcpClient target, uint frequency, int ppm, int gain)
    {
        var expected = new[] { Command(1, frequency), Command(2, 2_048_000),
            Command(5, unchecked((uint)ppm)), Command(8, 0), Command(3, 1), Command(4, unchecked((uint)gain)) }.SelectMany(x => x).ToArray();
        Assert((await Read(target, 30)).SequenceEqual(expected), "Managed wire commands including signed PPM and gain units");
    }
    await CheckManaged(source, 145_500_000, -17, 143);
    await Until(() => relay.GetSnapshot().Status == RelayStatus.Streaming);
    using var client = await Connect(relay);
    await client.GetStream().WriteAsync(Command(2, 999));
    await blocked.Task.WaitAsync(TimeSpan.FromSeconds(3));
    Assert(!relay.GetSnapshot().Clients[0].HasControl, "All clients blocked in managed mode");
    settings = settings with { CenterFrequency = 144_000_000, Ppm = 12, GainTenthsDb = -10 };
    await relay.ConfigureControlAsync(settings);
    await CheckManaged(source, 144_000_000, 12, -10); // Would detect an incorrectly forwarded client command.
    Assert(relay.GetSnapshot().RequestedFrequency == 144_000_000, "Live managed frequency");
    Assert(relay.GetSnapshot().RequestedGainTenthsDb == -10 && relay.GetSnapshot().RequestedAutomaticGain == false, "Managed gain snapshot");
    source.Dispose();
    await Until(() => relay.GetSnapshot().Status == RelayStatus.Reconnecting);
    using var next = await mock.Accept();
    await mock.SendHeader(next);
    await CheckManaged(next, 144_000_000, 12, -10);
    await Until(() => relay.GetSnapshot().Status == RelayStatus.Streaming);
    var unlocked = settings with { AcceptClientCommands = true,
        LockCenterFrequency = false, LockPpm = false, LockGain = false, LockSampleRate = false };
    await relay.ConfigureControlAsync(unlocked);
    using var newClient = await Connect(relay);
    await newClient.GetStream().WriteAsync(Command(1, 100));
    Assert((await Read(next, 5)).SequenceEqual(Command(1, 100)), "Switch back enables clients");
    await newClient.GetStream().WriteAsync(Command(3, 0));
    Assert((await Read(next, 5)).SequenceEqual(Command(3, 0)), "Client can select automatic gain");
    await relay.ConfigureControlAsync(unlocked with { LockGain = true, AutomaticGain = true });
    var auto = await Read(next, 10);
    Assert(auto.AsSpan(0, 5).SequenceEqual(Command(8, 0)) && auto.AsSpan(5).SequenceEqual(Command(3, 0)),
        "Auto gain command and no manual gain");
    Assert(relay.GetSnapshot().RequestedAutomaticGain == true && relay.GetSnapshot().RequestedGainTenthsDb == null, "Auto gain snapshot");
    await relay.StopAsync();
    Assert(relay.GetSnapshot().RequestedAutomaticGain == null && relay.GetSnapshot().RequestedGainTenthsDb == null, "Stopped gain unknown");
}

static async Task Reconnect()
{
    using var mock = new MockUpstream();
    await using var relay = new RelayServer();
    await relay.StartAsync(Options(mock.Port));
    var firstSource = await mock.Accept();
    await mock.SendHeader(firstSource);
    await Until(() => relay.GetSnapshot().Status == RelayStatus.Streaming);
    using var first = await Connect(relay);
    await first.GetStream().WriteAsync(Command(5, unchecked((uint)-17)));
    Assert((await Read(firstSource, 5)).SequenceEqual(Command(5, unchecked((uint)-17))), "PPM forwarded before reconnect");
    await Until(() => relay.GetSnapshot().RequestedPpm == -17);
    firstSource.Dispose();
    await Until(() => relay.GetSnapshot().Status == RelayStatus.Reconnecting);
    Assert(relay.GetSnapshot().RequestedPpm == null, "PPM is unknown after upstream disconnect");
    await Until(() => relay.GetSnapshot().Clients.Count == 0);
    using var secondSource = await mock.Accept();
    await mock.SendHeader(secondSource);
    await Until(() => relay.GetSnapshot().Status == RelayStatus.Streaming);
    using var second = await Connect(relay);
    await Until(() => relay.GetSnapshot().Clients.Count == 1);
    Assert(relay.GetSnapshot().Clients[0].HasControl, "Reconnect accepts client commands");
    await second.GetStream().WriteAsync(Command(1, 123));
    Assert((await Read(secondSource, 5)).SequenceEqual(Command(1, 123)), "Control after reconnect");
    await secondSource.GetStream().WriteAsync(new byte[] { 1, 2 });
    Assert((await Read(second, 2)).SequenceEqual(new byte[] { 1, 2 }), "Data after reconnect");
}

static async Task StopAndRestart()
{
    using var mock = new MockUpstream();
    await using var relay = new RelayServer();
    var settings = Options(mock.Port);
    await relay.StartAsync(settings);
    using var waiting = await mock.Accept(); // Never return a header.
    await relay.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
    Assert(relay.GetSnapshot().Status == RelayStatus.Stopped, "Stopped state");
    using (var rebound = new TcpListener(IPAddress.Loopback, settings.ListenPort)) rebound.Start();
    await relay.StartAsync(settings);
    using var source = await mock.Accept();
    await mock.SendHeader(source);
    await Until(() => relay.GetSnapshot().Status == RelayStatus.Streaming);
    using var receiver = await Connect(relay);
    await relay.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
    Assert(relay.GetSnapshot().Clients.Count == 0, "Stop clears clients");
    await relay.StopAsync();
}

static async Task InvalidHeader()
{
    using var mock = new MockUpstream();
    await using var relay = new RelayServer();
    await relay.StartAsync(Options(mock.Port) with { AutoReconnect = false });
    using var source = await mock.Accept();
    await source.GetStream().WriteAsync(new byte[12]);
    await Until(() => relay.GetSnapshot().Status == RelayStatus.Faulted);
    using var rejected = new TcpClient();
    await rejected.ConnectAsync(IPAddress.Loopback, TestPorts.Last);
    Assert((await ReadOrEof(rejected)) == 0, "Client rejected before valid header");
}

static async Task SlowConsumer()
{
    using var mock = new MockUpstream();
    await using var relay = new RelayServer();
    var settings = Options(mock.Port) with { ClientQueueCapacity = 4, SendTimeout = TimeSpan.FromMilliseconds(250) };
    await relay.StartAsync(settings);
    using var source = await mock.Accept();
    await mock.SendHeader(source);
    await Until(() => relay.GetSnapshot().Status == RelayStatus.Streaming);
    using var slow = await Connect(relay);
    slow.ReceiveBufferSize = 1024;
    await Until(() => relay.GetSnapshot().Clients.Count == 1);
    var slowId = relay.GetSnapshot().Clients[0].Id;
    using var healthy = await Connect(relay);
    long healthyBytes = 0;
    var marker = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var drain = Task.Run(async () =>
    {
        var buffer = new byte[32768];
        var tail = new List<byte>();
        while (true)
        {
            var count = await healthy.GetStream().ReadAsync(buffer, deadline.Token);
            Assert(count > 0, "Healthy client remains connected");
            Interlocked.Add(ref healthyBytes, count);
            tail.AddRange(buffer.Take(count));
            if (tail.Count >= marker.Length && tail.TakeLast(marker.Length).SequenceEqual(marker)) break;
            if (tail.Count > 65536) tail.RemoveRange(0, tail.Count - 512);
        }
    });
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    var flood = Task.Run(async () =>
    {
        var block = new byte[32 * 1024];
        while (relay.GetSnapshot().Clients.Any(c => c.Id == slowId))
        {
            await source.GetStream().WriteAsync(block, timeout.Token);
            await Task.Delay(1, timeout.Token);
        }
    });
    await Until(() => !relay.GetSnapshot().Clients.Any(c => c.Id == slowId), 8000);
    await flood;
    Assert(relay.GetSnapshot().Status == RelayStatus.Streaming, "Slow consumer must not stop upstream");
    Assert(Interlocked.Read(ref healthyBytes) > 0, "Healthy client receives while slow client stalls");
    await source.GetStream().WriteAsync(marker);
    await drain;
}

static async Task OccupiedPort()
{
    using var occupied = new TcpListener(IPAddress.Loopback, 0);
    occupied.Start();
    await using var relay = new RelayServer();
    try
    {
        await relay.StartAsync(new() { ListenPort = ((IPEndPoint)occupied.LocalEndpoint).Port });
        throw new Exception("Expected occupied port failure");
    }
    catch (SocketException) { }
    using var mock = new MockUpstream();
    await relay.StartAsync(Options(mock.Port));
    using var source = await mock.Accept();
    await mock.SendHeader(source);
    await Until(() => relay.GetSnapshot().Status == RelayStatus.Streaming);
}

static async Task MaximumClients()
{
    using var mock = new MockUpstream();
    await using var relay = new RelayServer();
    await relay.StartAsync(Options(mock.Port) with { MaxClients = 1 });
    using var source = await mock.Accept();
    await mock.SendHeader(source);
    await Until(() => relay.GetSnapshot().Status == RelayStatus.Streaming);
    using var accepted = await Connect(relay);
    using var rejected = new TcpClient();
    await rejected.ConnectAsync(IPAddress.Loopback, TestPorts.Last);
    Assert(await ReadOrEof(rejected) == 0, "Limit rejects extra client");
    Assert(relay.GetSnapshot().Clients.Count == 1, "Client count capped");
}

static RelayOptions Options(int upstreamPort)
{
    using var available = new TcpListener(IPAddress.Loopback, 0);
    available.Start();
    // Register on Connect via last assigned port; tests run sequentially.
    TestPorts.Last = ((IPEndPoint)available.LocalEndpoint).Port;
    return new() { UpstreamPort = upstreamPort, ListenPort = TestPorts.Last,
        ReconnectDelay = TimeSpan.FromMilliseconds(300), ConnectTimeout = TimeSpan.FromSeconds(2) };
}

static async Task<TcpClient> Connect(RelayServer relay)
{
    var port = TestPorts.Last;
    var socket = new TcpClient { NoDelay = true };
    await socket.ConnectAsync(IPAddress.Loopback, port);
    var header = await Read(socket, 12);
    Assert(header.SequenceEqual(MockUpstream.Header), "Exact upstream header");
    return socket;
}

static byte[] Command(byte opcode, uint value)
{
    var bytes = new byte[5]; bytes[0] = opcode;
    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(1), value);
    return bytes;
}

static async Task<byte[]> Read(TcpClient socket, int length)
{
    var buffer = new byte[length];
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    await socket.GetStream().ReadExactlyAsync(buffer, timeout.Token);
    return buffer;
}

static async Task<int> ReadOrEof(TcpClient socket)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    try { return await socket.GetStream().ReadAsync(new byte[1], timeout.Token); }
    catch (IOException) { return 0; }
}

static async Task Until(Func<bool> condition, int milliseconds = 4000)
{
    var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
    while (!condition())
    {
        if (DateTime.UtcNow > until) throw new TimeoutException("Condition not reached");
        await Task.Delay(10);
    }
}

static void Assert(bool value, string message)
{
    if (!value) throw new Exception(message);
}

static class TestPorts
{
    public static int Last;
    // A single relay is exercised per sequential test.

}

sealed class MockUpstream : IDisposable
{
    private readonly TcpListener server = new(IPAddress.Loopback, 0);
    public static readonly byte[] Header = [82, 84, 76, 48, 0, 0, 0, 5, 0, 0, 0, 29];
    public int Port => ((IPEndPoint)server.LocalEndpoint).Port;
    public MockUpstream() => server.Start();
    public async Task<TcpClient> Accept() => await server.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(4));
    public async Task SendHeader(TcpClient socket, bool fragmented = false)
    {
        if (!fragmented) { await socket.GetStream().WriteAsync(Header); return; }
        foreach (var value in Header)
        {
            await socket.GetStream().WriteAsync(new byte[] { value });
            await Task.Delay(2);
        }
    }
    public void Dispose() => server.Stop();
}
