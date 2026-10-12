// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 いしかず
using System.Net;

namespace RtlCast.Core;

public record RelayOptions
{
    public const int MinGainTenthsDb = -99;
    public const int MaxGainTenthsDb = 496;

    public string UpstreamHost { get; init; } = "127.0.0.1";
    public int UpstreamPort { get; init; } = 1234;
    public string ListenAddress { get; init; } = "127.0.0.1";
    public int ListenPort { get; init; } = 11234;
    public bool AutoReconnect { get; init; } = true;
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public int ClientQueueCapacity { get; init; } = 64;
    public int MaxClients { get; init; } = 16;
    public bool AcceptClientCommands { get; init; } = true;
    public bool LockCenterFrequency { get; init; }
    public bool LockPpm { get; init; }
    public bool LockGain { get; init; }
    public bool LockSampleRate { get; init; }
    public uint CenterFrequency { get; init; } = 100_000_000;
    public int Ppm { get; init; }
    public bool AutomaticGain { get; init; } = true;
    public int GainTenthsDb { get; init; }
    public uint ManagedSampleRate { get; init; } = 2_048_000;

    public void Validate()
    {
        if (CenterFrequency == 0 || ManagedSampleRate == 0 ||
            GainTenthsDb is < MinGainTenthsDb or > MaxGainTenthsDb || Ppm is < -1000 or > 1000)
            throw new ArgumentException("中心周波数は正の値、PPMとGainは指定範囲内で入力してください。");
        if (string.IsNullOrWhiteSpace(UpstreamHost)) throw new ArgumentException("rtl_tcpの接続先を入力してください。");
        if (UpstreamPort is < 1 or > 65535 || ListenPort is < 1 or > 65535)
            throw new ArgumentException("ポート番号は1～65535で指定してください。");
        if (!IPAddress.TryParse(ListenAddress, out _)) throw new ArgumentException("待受アドレスにはIPアドレスを指定してください。");
        if (ClientQueueCapacity is < 1 or > 1024 || MaxClients is < 1 or > 256)
            throw new ArgumentException("キュー容量または接続数の上限が範囲外です。");
        if (ReconnectDelay <= TimeSpan.Zero || ConnectTimeout <= TimeSpan.Zero || SendTimeout <= TimeSpan.Zero)
            throw new ArgumentException("タイムアウトと再接続間隔は正の値で指定してください。");
    }
}

public enum RelayStatus { Stopped, Connecting, Streaming, Reconnecting, Faulted }
public sealed record ClientSnapshot(Guid Id, string Endpoint, DateTimeOffset ConnectedAt, long BytesSent, bool HasControl);
public sealed record RelaySnapshot(RelayStatus Status, string Message, long BytesReceived,
    uint? RequestedFrequency, uint? RequestedSampleRate, IReadOnlyList<ClientSnapshot> Clients, int? RequestedPpm = null,
    bool? RequestedAutomaticGain = null, int? RequestedGainTenthsDb = null, uint? RequestedGainIndex = null);
public sealed record RelayLog(DateTimeOffset Time, string Message);
