// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 いしかず
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace RtlCast.App;

public sealed record SerialInfo(string Serial, int Offset, int DescriptorLength, int MaximumLength);

public static class EepromSerial
{
    public static SerialInfo Inspect(byte[] image)
    {
        if (image.Length != 256 || image[0] != 0x28 || image[1] != 0x32)
            throw new ArgumentException("対応するRTL2832のEEPROM形式ではありません。");
        var offset = 9;
        for (var i = 0; i < 2; i++) offset += DescriptorLength(image, offset);
        var length = DescriptorLength(image, offset);
        var serial = Encoding.Unicode.GetString(image, offset + 2, length - 2);
        return new(serial, offset, length, Math.Max(0, (78 - offset - 2) / 2));
    }

    private static int DescriptorLength(byte[] image, int offset)
    {
        if (offset < 9 || offset + 2 > 78 || image[offset + 1] != 3 || image[offset] < 2 ||
            image[offset] % 2 != 0 || offset + image[offset] > 78)
            throw new ArgumentException("EEPROMの文字列領域が不正、または非対応です。書き込みを中止しました。");
        return image[offset];
    }

    public static void Validate(string serial, int maximumLength)
    {
        if (serial.Length == 0 || serial.Length > maximumLength ||
            serial.Any(c => !(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')))
            throw new ArgumentException($"新しいIDは英数字・ハイフン・アンダースコアで1～{maximumLength}文字にしてください。");
    }

    public static byte[] Replace(byte[] original, string serial)
    {
        var info = Inspect(original);
        Validate(serial, info.MaximumLength);
        var result = original.ToArray();
        var descriptorLength = 2 + serial.Length * 2;
        Array.Clear(result, info.Offset, Math.Max(info.DescriptorLength, descriptorLength));
        result[info.Offset] = (byte)descriptorLength;
        result[info.Offset + 1] = 3;
        Encoding.Unicode.GetBytes(serial).CopyTo(result, info.Offset + 2);
        result[6] = 0xa5;
        return result;
    }
}

public static class SdrEeprom
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int OpenDevice(out nint device, uint index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CloseDevice(nint device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReadImage(nint device, [Out] byte[] data, byte offset, ushort length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int WriteImage(nint device, byte[] data, byte offset, ushort length);

    private static T WithDevice<T>(string path, uint index, Func<nint, nint, T> action)
    {
        var library = SdrDevices.Load(path);
        nint device = 0;
        try
        {
            var open = Marshal.GetDelegateForFunctionPointer<OpenDevice>(NativeLibrary.GetExport(library, "rtlsdr_open"));
            if (open(out device, index) < 0 || device == 0)
                throw new IOException("デバイスを開けません。他の受信ソフトやrtl_tcpを停止してください。");
            return action(library, device);
        }
        finally
        {
            if (device != 0) Marshal.GetDelegateForFunctionPointer<CloseDevice>(NativeLibrary.GetExport(library, "rtlsdr_close"))(device);
            NativeLibrary.Free(library);
        }
    }

    private static byte[] Read(nint library, nint device)
    {
        var image = new byte[256];
        var read = Marshal.GetDelegateForFunctionPointer<ReadImage>(NativeLibrary.GetExport(library, "rtlsdr_read_eeprom"));
        if (read(device, image, 0, 256) < 0) throw new IOException("EEPROMを読み取れません。書き込みを中止しました。");
        EepromSerial.Inspect(image);
        return image;
    }

    public static byte[] Read(string path, uint index) => WithDevice(path, index, Read);

    public static void Write(string path, uint index, byte[] original, string serial, string? backupDirectory = null)
    {
        var updated = EepromSerial.Replace(original, serial);
        WithDevice(path, index, (library, device) =>
        {
            if (!Read(library, device).SequenceEqual(original))
                throw new IOException("選択後にデバイスの状態が変わりました。デバイスを選び直してください。");
            backupDirectory ??= Path.Combine(SettingsStore.DefaultDirectory, "eeprom-backups");
            Directory.CreateDirectory(backupDirectory);
            var backup = Path.Combine(backupDirectory, $"device-{index}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.bin");
            File.WriteAllBytes(backup, original);
            var write = Marshal.GetDelegateForFunctionPointer<WriteImage>(NativeLibrary.GetExport(library, "rtlsdr_write_eeprom"));
            if (write(device, updated, 0, 256) < 0 || !Read(library, device).SequenceEqual(updated))
                throw new IOException($"EEPROMの書き込み・検証に失敗しました。元データのバックアップ: {backup}");
            return true;
        });
    }
}
