using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace WinControl.Services;

/// <summary>Protocolo local Tuya 3.3 (TCP 6668): quadros 0x55AA, AES-128-ECB com a local_key, CRC32.</summary>
public static class Tuya
{
    public const uint CmdControl = 0x07;
    public const uint CmdDpQuery = 0x0A;

    public sealed class Reply
    {
        public bool Ok;
        public JsonObject? Data;
        public string Err = "";      // código no estilo do tinytuya (901, 902, 905, 914...)
        public string Message = "";
    }

    private static readonly uint[] CrcTable = BuildCrcTable();
    private static uint _seq;

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFF;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }

    private static byte[] Encrypt(byte[] plain, string key)
    {
        using var aes = Aes.Create();
        aes.Key = Encoding.UTF8.GetBytes(key);
        return aes.EncryptEcb(plain, PaddingMode.PKCS7);
    }

    public static byte[] Decrypt(byte[] cipher, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptEcb(cipher, PaddingMode.PKCS7);
    }

    private static byte[] Pack(uint cmd, byte[] payload)
    {
        var buf = new byte[16 + payload.Length + 8];
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0), 0x000055AA);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4), Interlocked.Increment(ref _seq));
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(8), cmd);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(12), (uint)(payload.Length + 8));
        payload.CopyTo(buf, 16);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(16 + payload.Length), Crc32(buf.AsSpan(0, 16 + payload.Length)));
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(20 + payload.Length), 0x0000AA55);
        return buf;
    }

    private static byte[]? ReadExact(NetworkStream s, int n)
    {
        var buf = new byte[n];
        int got = 0;
        while (got < n)
        {
            int r = s.Read(buf, got, n - got);
            if (r <= 0) return null;
            got += r;
        }
        return buf;
    }

    /// <summary>Uma conversa completa: monta o comando, envia e lê a resposta.</summary>
    public static Reply Call(string ip, string devId, string key, double version, uint cmd, JsonObject extra,
                             int timeoutMs = 3000, int retries = 2)
    {
        if (Math.Abs(version - 3.3) > 0.01)
            return new Reply { Err = "ver", Message = $"versao {version} do protocolo nao suportada (so 3.3)" };

        var body = new JsonObject();
        if (cmd == CmdDpQuery) body["gwId"] = devId;
        body["devId"] = devId;
        body["uid"] = devId;
        body["t"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        foreach (var kv in extra) body[kv.Key] = kv.Value?.DeepClone();

        byte[] cipher = Encrypt(Encoding.UTF8.GetBytes(body.ToJsonString()), key);
        byte[] payload;
        if (cmd == CmdDpQuery) payload = cipher;                        // consulta não leva o cabeçalho "3.3"
        else
        {
            payload = new byte[15 + cipher.Length];
            Encoding.ASCII.GetBytes("3.3").CopyTo(payload, 0);          // "3.3" + 12 bytes zero
            cipher.CopyTo(payload, 15);
        }

        Reply last = new() { Err = "905", Message = "sem resposta" };
        for (int attempt = 0; attempt <= retries; attempt++)
        {
            try
            {
                using var client = new TcpClient();
                if (!client.ConnectAsync(ip, 6668).Wait(timeoutMs))
                    throw new TimeoutException("conexao");
                client.ReceiveTimeout = client.SendTimeout = timeoutMs;
                using var stream = client.GetStream();
                stream.Write(Pack(cmd, payload));

                for (int frames = 0; frames < 6; frames++)
                {
                    var head = ReadExact(stream, 16);
                    if (head == null) throw new IOException("conexao fechada");
                    uint rcmd = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(8));
                    int len = (int)BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(12));
                    var data = ReadExact(stream, len);
                    if (data == null || len < 8) throw new IOException("quadro incompleto");
                    if (rcmd != cmd) continue;                          // ignora heartbeats e avisos

                    int start = 0;
                    uint retcode = 0;
                    if (len >= 12 && (BinaryPrimitives.ReadUInt32BigEndian(data) & 0xFFFFFF00) == 0)
                    {
                        retcode = BinaryPrimitives.ReadUInt32BigEndian(data);
                        start = 4;
                    }
                    var pl = data.AsSpan(start, len - 8 - start).ToArray();
                    if (retcode != 0)
                        return new Reply { Err = "904", Message = $"retorno {retcode} da lampada" };
                    if (pl.Length == 0)
                        return new Reply { Ok = true, Data = new JsonObject() };

                    if (pl.Length > 15 && pl[0] == (byte)'3' && pl[1] == (byte)'.') pl = pl[15..];
                    string text;
                    try { text = Encoding.UTF8.GetString(Decrypt(pl, Encoding.UTF8.GetBytes(key))); }
                    catch (CryptographicException) { text = Encoding.UTF8.GetString(pl); }

                    if (JsonNode.Parse(text) is JsonObject obj)
                    {
                        if (obj.ContainsKey("Error") || obj.ContainsKey("Err"))
                            return new Reply { Err = obj["Err"]?.ToString() ?? "?", Message = obj["Error"]?.ToString() ?? text };
                        return new Reply { Ok = true, Data = obj };
                    }
                    return new Reply { Err = "914", Message = text };
                }
                last = new Reply { Err = "902", Message = "a lampada nao respondeu a tempo" };
            }
            catch (TimeoutException) { last = new Reply { Err = "905", Message = "inacessivel" }; }
            catch (Exception e) when (e is SocketException or IOException or AggregateException)
            {
                last = new Reply { Err = e is IOException ? "902" : "905", Message = e.GetBaseException().Message };
            }
            catch (Exception e)
            {
                last = new Reply { Err = "exc", Message = e.Message };
            }
            Thread.Sleep(100);
        }
        return last;
    }

    /// <summary>Procura a lâmpada pelo aviso UDP que ela manda na rede (portas 6666 e 6667).</summary>
    public static (string ip, double version)? Discover(string devId, int seconds = 8)
    {
        byte[] udpKey = MD5.HashData(Encoding.UTF8.GetBytes("yGAdlopoPVldABfn"));
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        var clients = new List<UdpClient>();
        try
        {
            foreach (int port in new[] { 6666, 6667 })
            {
                try
                {
                    var c = new UdpClient();
                    c.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    c.Client.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Any, port));
                    c.Client.ReceiveTimeout = 500;
                    clients.Add(c);
                }
                catch (SocketException) { /* porta ocupada por outro programa */ }
            }
            while (DateTime.UtcNow < deadline)
            {
                foreach (var c in clients)
                {
                    try
                    {
                        System.Net.IPEndPoint? from = null;
                        var pkt = c.Receive(ref from);
                        if (pkt.Length < 28) continue;
                        var enc = pkt.AsSpan(20, pkt.Length - 28).ToArray();
                        var json = JsonNode.Parse(Encoding.UTF8.GetString(Decrypt(enc, udpKey))) as JsonObject;
                        if (json?["gwId"]?.ToString() == devId && json["ip"] is { } ip)
                        {
                            double.TryParse(json["version"]?.ToString(), System.Globalization.NumberStyles.Float,
                                            System.Globalization.CultureInfo.InvariantCulture, out double ver);
                            return (ip.ToString(), ver);
                        }
                    }
                    catch (SocketException) { }
                    catch (Exception) { }
                }
            }
        }
        finally
        {
            foreach (var c in clients) c.Dispose();
        }
        return null;
    }
}
