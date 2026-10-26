using System.Buffers.Binary;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace WinControl.Services;

/// <summary>
/// Fallback por Bluetooth (BLE) para acordar a lâmpada quando o Wi-Fi dela dorme depois de muito tempo apagada.
/// Protocolo Tuya BLE: pede DEVICE_INFO (cifrado com md5 dos 6 primeiros caracteres da local_key), deriva a chave de
/// sessão do "srand" da resposta e manda o DP 20 = ligado. Porta do ble_control.py do glight.
/// </summary>
public static class Ble
{
    private static readonly Guid NotifyChar = Guid.Parse("00002b10-0000-1000-8000-00805f9b34fb");
    private static readonly Guid WriteChar = Guid.Parse("00002b11-0000-1000-8000-00805f9b34fb");
    private const ushort ManufacturerId = 0x07D0;
    private const ushort FunDeviceInfo = 0x0000;
    private const ushort FunDps = 0x0002;
    private const int Mtu = 20;

    private static uint _seq;

    public static ulong? ParseMac(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return null;
        try { return Convert.ToUInt64(mac.Replace(":", "").Replace("-", ""), 16); }
        catch (FormatException) { return null; }
    }

    /// <summary>Liga a lâmpada pelo Bluetooth. Bloqueia alguns segundos: chame fora da thread da interface.</summary>
    public static bool TurnOn(string mac, string localKey, int scanSeconds = 4)
    {
        try { return TurnOnAsync(mac, localKey, scanSeconds).GetAwaiter().GetResult(); }
        catch (Exception e)
        {
            Log.Write($"BLE: falha ao ligar ({e.GetBaseException().Message})");
            return false;
        }
    }

    private static async Task<bool> TurnOnAsync(string mac, string localKey, int scanSeconds)
    {
        if (ParseMac(mac) is not { } addr) { Log.Write($"BLE: ble_mac invalido ({mac})"); return false; }
        if (localKey.Length < 6) { Log.Write("BLE: local_key ausente"); return false; }

        // o anúncio acorda o rádio e diz a versão do protocolo (byte 6 dos dados do fabricante)
        int protocol = await ScanProtocol(addr, scanSeconds) ?? 3;

        using var dev = await BluetoothLEDevice.FromBluetoothAddressAsync(addr);
        if (dev == null) { Log.Write("BLE: lampada nao encontrada"); return false; }
        var chars = await dev.GetGattServicesAsync(BluetoothCacheMode.Uncached);
        GattCharacteristic? wr = null, nt = null;
        foreach (var svc in chars.Services)
        {
            var cs = await svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            foreach (var c in cs.Characteristics)
            {
                if (c.Uuid == WriteChar) wr = c;
                else if (c.Uuid == NotifyChar) nt = c;
            }
        }
        if (wr == null || nt == null) { Log.Write("BLE: caracteristicas Tuya nao encontradas"); return false; }

        byte[] keyPrefix = Encoding.UTF8.GetBytes(localKey[..6]);
        byte[] loginKey = MD5.HashData(keyPrefix);
        byte[]? sessionKey = null;

        // remonta os fragmentos da notificação e decifra
        var buffer = new List<byte>();
        int expected = -1;
        var reply = new TaskCompletionSource<(ushort code, byte[] data)>(TaskCreationOptions.RunContinuationsAsynchronously);
        nt.ValueChanged += (_, e) =>
        {
            var chunk = e.CharacteristicValue.ToArray();
            int pos = 0;
            int num = ReadVarint(chunk, ref pos);
            if (num == 0)
            {
                buffer.Clear();
                expected = ReadVarint(chunk, ref pos);
                pos++;                                         // versão do protocolo
            }
            buffer.AddRange(chunk[pos..]);
            if (expected < 0 || buffer.Count < expected) return;
            try
            {
                var msg = buffer.ToArray();
                byte[] key = msg[0] == 4 ? loginKey : sessionKey ?? loginKey;
                using var aes = Aes.Create();
                aes.Key = key;
                var raw = aes.DecryptCbc(msg.AsSpan(17).ToArray(), msg.AsSpan(1, 16).ToArray(), PaddingMode.None);
                ushort code = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(8));
                int len = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(10));
                reply.TrySetResult((code, raw.AsSpan(12, len).ToArray()));
            }
            catch (Exception ex) { reply.TrySetException(ex); }
        };
        await nt.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);

        try
        {
            await Send(wr, FunDeviceInfo, Array.Empty<byte>(), loginKey, 4, protocol);
            var done = await Task.WhenAny(reply.Task, Task.Delay(5000));
            if (done != reply.Task) { Log.Write("BLE: lampada nao respondeu ao DEVICE_INFO"); return false; }
            var (_, info) = await reply.Task;
            if (info.Length < 12) { Log.Write("BLE: resposta DEVICE_INFO curta"); return false; }
            sessionKey = MD5.HashData([.. keyPrefix, .. info.AsSpan(6, 6)]);

            // DP 20 (liga/desliga), tipo bool, tamanho 1, valor 1
            await Send(wr, FunDps, [20, 1, 1, 1], sessionKey, 5, protocol);
            await Task.Delay(300);
            Log.Write("BLE: lampada ligada pelo Bluetooth");
            return true;
        }
        finally
        {
            try { await nt.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None); }
            catch { }
        }
    }

    private static async Task<int?> ScanProtocol(ulong addr, int seconds)
    {
        var tcs = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += (_, e) =>
        {
            if (e.BluetoothAddress != addr) return;
            foreach (var md in e.Advertisement.ManufacturerData)
            {
                var d = md.Data.ToArray();
                if (md.CompanyId == ManufacturerId && d.Length > 6) { tcs.TrySetResult(d[6]); return; }
            }
        };
        watcher.Start();
        try
        {
            var done = await Task.WhenAny(tcs.Task, Task.Delay(seconds * 1000));
            return done == tcs.Task ? await tcs.Task : null;
        }
        finally { watcher.Stop(); }
    }

    private static async Task Send(GattCharacteristic wr, ushort code, byte[] data, byte[] key, byte flag, int protocol)
    {
        var raw = new List<byte>();
        var head = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(head, Interlocked.Increment(ref _seq));
        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(8), code);
        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(10), (ushort)data.Length);
        raw.AddRange(head);
        raw.AddRange(data);
        ushort crc = Crc16(raw);
        raw.Add((byte)(crc >> 8)); raw.Add((byte)crc);
        while (raw.Count % 16 != 0) raw.Add(0);

        byte[] iv = RandomNumberGenerator.GetBytes(16);
        using var aes = Aes.Create();
        aes.Key = key;
        byte[] enc = [flag, .. iv, .. aes.EncryptCbc(raw.ToArray(), iv, PaddingMode.None)];

        int pos = 0, num = 0;
        while (pos < enc.Length)
        {
            var pkt = new List<byte>();
            WriteVarint(pkt, num);
            if (num == 0)
            {
                WriteVarint(pkt, enc.Length);
                pkt.Add((byte)(protocol << 4));
            }
            int take = Math.Min(Mtu - pkt.Count, enc.Length - pos);
            pkt.AddRange(enc.AsSpan(pos, take).ToArray());
            pos += take; num++;
            await wr.WriteValueAsync(pkt.ToArray().AsBuffer(), GattWriteOption.WriteWithoutResponse);
        }
    }

    private static ushort Crc16(IEnumerable<byte> data)
    {
        int crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1;
        }
        return (ushort)crc;
    }

    private static void WriteVarint(List<byte> o, int v)
    {
        do
        {
            byte b = (byte)(v & 0x7F);
            v >>= 7;
            if (v > 0) b |= 0x80;
            o.Add(b);
        } while (v > 0);
    }

    private static int ReadVarint(byte[] d, ref int pos)
    {
        int v = 0, shift = 0;
        while (pos < d.Length)
        {
            byte b = d[pos++];
            v |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        return v;
    }
}
