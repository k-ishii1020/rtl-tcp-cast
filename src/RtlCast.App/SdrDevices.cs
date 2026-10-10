// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 いしかず
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace RtlCast.App;

public sealed record SdrDevice(uint Index, string Name, string Serial, string Identifier)
{
    public string Label => $"{Index}: {CleanName(Name) ?? "RTL-SDR"}" + (Serial.Length > 0 ? $" [{Serial}]" : "");

    private static string? CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var cleaned = new string(name.Where(character => !char.IsControl(character)).ToArray()).Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }
}

public static class SdrDevices
{
    private static readonly object DeviceCacheLock = new();
    private static readonly Dictionary<string, IReadOnlyList<SdrDevice>> DeviceCache = new(StringComparer.OrdinalIgnoreCase);

    public static void InvalidateCache()
    {
        lock (DeviceCacheLock) DeviceCache.Clear();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryExW(string path, nint file, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint CountDevices();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint DeviceName(uint index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int UsbStrings(uint index, [Out] byte[] manufacturer, [Out] byte[] product, [Out] byte[] serial);

    internal static nint Load(string rtlTcpPath)
    {
        if (string.IsNullOrWhiteSpace(rtlTcpPath)) throw new ArgumentException("アプリ全体の設定でRTL-TCPのパスを指定してください。");
        var directory = Path.GetDirectoryName(Path.GetFullPath(rtlTcpPath))!;
        var dll = new[] { "rtlsdr.dll", "librtlsdr.dll" }.Select(n => Path.Combine(directory, n)).FirstOrDefault(File.Exists);
        if (dll == null) throw new FileNotFoundException("RTL-TCPと同じフォルダーのrtlsdr.dll／librtlsdr.dllを確認してください。番号・シリアル番号の手入力も使用できます。");
        var library = LoadLibraryExW(dll, 0, 0x100 | 0x1000);
        if (library == 0) throw new IOException($"デバイス一覧用DLLを読み込めません（Windowsエラー {Marshal.GetLastWin32Error()}）。64bit版のDLLと依存DLLを確認してください。");
        return library;
    }

    public static IReadOnlyList<SdrDevice> Enumerate(string rtlTcpPath)
    {
        lock (DeviceCacheLock) return EnumerateCore(rtlTcpPath);
    }

    public static string ResolveDeviceIndex(string rtlTcpPath, string identifier)
    {
        if (uint.TryParse(identifier, out _)) return identifier;
        var matches = Enumerate(rtlTcpPath).Where(device =>
            string.Equals(device.Serial, identifier, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length switch
        {
            1 => matches[0].Index.ToString(),
            0 => throw new ArgumentException($"シリアル番号「{identifier}」のデバイスを確認できません。デバイス一覧を更新してください。"),
            _ => throw new ArgumentException($"シリアル番号「{identifier}」が重複しています。デバイス番号を選び直してください.")
        };
    }

    private static IReadOnlyList<SdrDevice> EnumerateCore(string rtlTcpPath)
    {
        var library = Load(rtlTcpPath);
        try
        {
            var count = Marshal.GetDelegateForFunctionPointer<CountDevices>(NativeLibrary.GetExport(library, "rtlsdr_get_device_count"));
            var name = Marshal.GetDelegateForFunctionPointer<DeviceName>(NativeLibrary.GetExport(library, "rtlsdr_get_device_name"));
            var strings = Marshal.GetDelegateForFunctionPointer<UsbStrings>(NativeLibrary.GetExport(library, "rtlsdr_get_device_usb_strings"));
            var devices = new List<SdrDevice>();
            var total = Math.Min(count(), 256u);
            var cacheKey = Path.GetFullPath(rtlTcpPath);
            DeviceCache.TryGetValue(cacheKey, out var previous);
            for (uint index = 0; index < total; index++)
            {
                var serial = new byte[256];
                var result = strings(index, new byte[256], new byte[256], serial);
                var length = Array.IndexOf(serial, (byte)0);
                var serialText = result == 0 ? Encoding.UTF8.GetString(serial, 0, length < 0 ? serial.Length : length) : "";
                var cachedName = previous?.FirstOrDefault(device => device.Index == index)?.Name;
                var deviceName = SdrDeviceName(Marshal.PtrToStringUTF8(name(index))) ?? SdrDeviceName(cachedName) ?? "RTL-SDR";
                devices.Add(new(index, deviceName, serialText, index.ToString()));
            }
            // An in-use device may refuse USB string reads. Reuse successful reads only
            // while the device topology is unchanged; USB notifications invalidate this cache.
            if (previous != null && previous.Count == devices.Count &&
                devices.All(d => previous.Any(p => p.Index == d.Index && p.Name == d.Name)))
            {
                devices = devices.Select(d => d.Serial.Length == 0
                    ? d with { Serial = previous.First(p => p.Index == d.Index).Serial }
                    : d).ToList();
            }
            DeviceCache[cacheKey] = devices;
            // Numeric serials can be interpreted as an index by rtl_tcp; select those devices by index.
            return devices.Select(d => d with { Identifier = d.Serial.Length > 0 && !uint.TryParse(d.Serial, out _) &&
                devices.Count(x => x.Serial == d.Serial) == 1 ? d.Serial : d.Index.ToString() }).ToArray();
        }
        finally { NativeLibrary.Free(library); }
    }

    private static string? SdrDeviceName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var cleaned = new string(name.Where(character => !char.IsControl(character)).ToArray()).Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }
}
