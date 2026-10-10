// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 いしかず
using RtlCast.Core;

namespace RtlCast.App;

public sealed record AppSettings : RelayOptions
{
    public string SessionId { get; init; } = "";
    public string SessionName { get; init; } = "配信 1";
    public string DeviceIdentifier { get; init; } = "0";
    public bool UseLocalRtlTcp { get; init; } = true;
    public string RtlTcpPath { get; init; } = "";
    public string PreferredLocalAddress { get; init; } = "";
}
