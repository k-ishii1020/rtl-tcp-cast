// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 いしかず
using System.Net;
using System.Net.Sockets;

// Test fixture: behaves as a standalone rtl_tcp process without requiring USB hardware.
var portIndex = Array.IndexOf(args, "-p");
if (portIndex < 0 || !int.TryParse(args.ElementAtOrDefault(portIndex + 1), out var port)) return 2;
if (File.Exists(Path.Combine(AppContext.BaseDirectory, "exit-immediately.flag")))
{
    Console.Error.WriteLine("No supported devices found (test fixture)");
    return 7;
}
using var listener = new TcpListener(IPAddress.Loopback, port);
listener.Start();
Console.Error.WriteLine($"Listening on 127.0.0.1:{port}");
while (true)
{
    using var client = await listener.AcceptTcpClientAsync();
    try
    {
        var stream = client.GetStream();
        await stream.WriteAsync(new byte[] { 82, 84, 76, 48, 0, 0, 0, 5, 0, 0, 0, 29 });
        var bytes = new byte[4096];
        while (true)
        {
            await stream.WriteAsync(bytes);
            await Task.Delay(10);
        }
    }
    catch (IOException) { }
    catch (SocketException) { }
}
