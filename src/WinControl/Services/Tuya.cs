using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace WinControl.Services;

/// <summary>
/// Protocolo local Tuya (TCP 6668). 3.3: quadros 0x55AA, AES-128-ECB com a local_key, CRC32.
/// 3.5: quadros 0x6699, AES-GCM com uma chave combinada a cada conexão.
/// </summary>
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

    /// <summary>Conecta com limite de tempo. A tentativa que passa do limite termina depois, sozinha: sem observá-la, cada uma virava um erro falso no log.</summary>
    private static bool Conectar(TcpClient client, string ip, int timeoutMs)
    {
        var tentativa = client.ConnectAsync(ip, 6668);
        tentativa.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        return tentativa.Wait(timeoutMs);
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
        // a local_key tem 16 caracteres; sem ela (ainda "SUA_LOCAL_KEY") o AES estourava e derrubava cenas e vigias
        if (Encoding.UTF8.GetByteCount(key) != 16)
            return new Reply { Err = "key", Message = "local_key ausente ou invalida em lamp_config.json" };
        if (Math.Abs(version - 3.5) < 0.01) return Call35(ip, key, cmd, extra, timeoutMs, retries);
        if (Math.Abs(version - 3.3) > 0.01)
            return new Reply { Err = "ver", Message = $"versao {version} do protocolo nao suportada (so 3.3 e 3.5)" };

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
                if (!Conectar(client, ip, timeoutMs))
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

    // ======================= protocolo 3.5 =======================
    private const uint Prefixo35 = 0x00006699, Sufixo35 = 0x00009966;
    private const uint CmdNegInicio = 0x03, CmdNegResposta = 0x04, CmdNegFim = 0x05, CmdControlNovo = 0x0D, CmdQueryNovo = 0x10;

    private static void Escrever35(NetworkStream s, uint cmd, byte[] plain, byte[] key)
    {
        var iv = RandomNumberGenerator.GetBytes(12);
        var buf = new byte[18 + 12 + plain.Length + 16 + 4];
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0), Prefixo35);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(6), Interlocked.Increment(ref _seq));
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(10), cmd);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(14), (uint)(12 + plain.Length + 16));
        iv.CopyTo(buf, 18);
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(iv, plain, buf.AsSpan(30, plain.Length), buf.AsSpan(30 + plain.Length, 16), buf.AsSpan(4, 14));
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(30 + plain.Length + 16), Sufixo35);
        s.Write(buf);
    }

    private static (uint cmd, byte[] plain) Ler35(NetworkStream s, byte[] key)
    {
        var head = ReadExact(s, 18) ?? throw new IOException("conexao fechada");
        if (BinaryPrimitives.ReadUInt32BigEndian(head) != Prefixo35) throw new IOException("quadro fora do protocolo 3.5");
        uint cmd = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(10));
        int len = (int)BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(14));
        if (len < 28 || len > 64 * 1024) throw new IOException("quadro com tamanho invalido");
        var data = ReadExact(s, len + 4) ?? throw new IOException("quadro incompleto");
        var plain = new byte[len - 28];
        using var gcm = new AesGcm(key, 16);
        gcm.Decrypt(data.AsSpan(0, 12), data.AsSpan(12, plain.Length), data.AsSpan(12 + plain.Length, 16), plain, head.AsSpan(4, 14));
        return (cmd, plain);
    }

    /// <summary>Combina a chave da conexão: um número de cada lado, cifrados com a local_key. Quem não tem a chave não passa daqui.</summary>
    private static byte[]? NegociarSessao(NetworkStream s, byte[] key)
    {
        var meu = Encoding.ASCII.GetBytes(Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant());
        Escrever35(s, CmdNegInicio, meu, key);
        byte[]? resposta = null;
        for (int frames = 0; frames < 4 && resposta == null; frames++)
        {
            var (cmd, plain) = Ler35(s, key);
            if (cmd == CmdNegResposta) resposta = plain;
        }
        if (resposta == null) return null;
        // o número dela, mais o HMAC do meu; alguns firmwares põem um código de retorno de 4 bytes na frente
        var esperado = HMACSHA256.HashData(key, meu);
        int inicio = resposta.Length >= 52 && resposta.AsSpan(20, 32).SequenceEqual(esperado) ? 4 : 0;
        if (resposta.Length < inicio + 48 || !resposta.AsSpan(inicio + 16, 32).SequenceEqual(esperado)) return null;
        var dela = resposta.AsSpan(inicio, 16).ToArray();
        Escrever35(s, CmdNegFim, HMACSHA256.HashData(key, dela), key);

        var mistura = new byte[16];
        for (int i = 0; i < 16; i++) mistura[i] = (byte)(meu[i] ^ dela[i]);
        var sessao = new byte[16];
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(meu.AsSpan(0, 12), mistura, sessao, new byte[16]);
        return sessao;
    }

    private static Reply Call35(string ip, string key, uint cmd, JsonObject extra, int timeoutMs, int retries)
    {
        var chave = Encoding.UTF8.GetBytes(key);
        // a consulta nova não leva nada; o controle novo embrulha os dps e leva o cabeçalho de versão
        uint cmdReal = cmd == CmdDpQuery ? CmdQueryNovo : CmdControlNovo;
        byte[] plain;
        if (cmd == CmdDpQuery) plain = Encoding.UTF8.GetBytes("{}");
        else
        {
            var corpo = new JsonObject { ["protocol"] = 5, ["t"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["data"] = extra.DeepClone() };
            var json = Encoding.UTF8.GetBytes(corpo.ToJsonString());
            plain = new byte[15 + json.Length];
            Encoding.ASCII.GetBytes("3.5").CopyTo(plain, 0);
            json.CopyTo(plain, 15);
        }

        Reply last = new() { Err = "905", Message = "sem resposta" };
        for (int attempt = 0; attempt <= retries; attempt++)
        {
            try
            {
                using var client = new TcpClient();
                if (!Conectar(client, ip, timeoutMs)) throw new TimeoutException("conexao");
                client.ReceiveTimeout = client.SendTimeout = timeoutMs;
                using var stream = client.GetStream();
                if (NegociarSessao(stream, chave) is not { } sessao)
                    return new Reply { Err = "914", Message = "a lampada recusou a chave" };
                Escrever35(stream, cmdReal, plain, sessao);

                for (int frames = 0; frames < 6; frames++)
                {
                    var (rcmd, pl) = Ler35(stream, sessao);
                    if (rcmd != cmdReal) continue;                      // ignora heartbeats e avisos
                    if (pl.Length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(pl) is > 0 and < 256)
                        return new Reply { Err = "904", Message = $"retorno {BinaryPrimitives.ReadUInt32BigEndian(pl)} da lampada" };
                    int abre = Array.IndexOf(pl, (byte)'{');
                    if (abre < 0) return new Reply { Ok = true, Data = new JsonObject() };
                    if (JsonNode.Parse(Encoding.UTF8.GetString(pl, abre, pl.Length - abre)) is not JsonObject obj)
                        return new Reply { Err = "914", Message = "resposta invalida" };
                    // o 3.5 pode embrulhar em "data": deixa no mesmo formato do 3.3
                    if (obj["data"] is JsonObject dentro && dentro["dps"] is { } dps) obj["dps"] = dps.DeepClone();
                    return new Reply { Ok = true, Data = obj };
                }
                last = new Reply { Err = "902", Message = "a lampada nao respondeu a tempo" };
            }
            catch (TimeoutException) { last = new Reply { Err = "905", Message = "inacessivel" }; }
            catch (CryptographicException) { return new Reply { Err = "914", Message = "a lampada recusou a chave" }; }
            catch (Exception e) when (e is SocketException or IOException or AggregateException)
            {
                last = new Reply { Err = e is IOException ? "902" : "905", Message = e.GetBaseException().Message };
            }
            catch (Exception e) { last = new Reply { Err = "exc", Message = e.Message }; }
            Thread.Sleep(100);
        }
        return last;
    }

    /// <summary>Quais destes endereços têm a porta das lâmpadas aberta. Só conecta e fecha, sem enviar nada.</summary>
    public static List<string> ComPortaAberta(IEnumerable<string> ips)
    {
        var abertos = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.ForEach(ips, new ParallelOptions { MaxDegreeOfParallelism = 48 }, ip =>
        {
            try
            {
                using var c = new TcpClient();
                if (Conectar(c, ip, 600) && c.Connected) abertos.Add(ip);
            }
            catch (Exception) { /* fechada: é o caso da maioria */ }
        });
        return abertos.ToList();
    }

    // ======================= avisos UDP =======================
    private static readonly byte[] ChaveUdp = MD5.HashData(Encoding.UTF8.GetBytes("yGAdlopoPVldABfn"));

    /// <summary>Lê um aviso. Null se não for de um aparelho Tuya que dê para entender.</summary>
    public static (string id, string ip, double version)? LerAviso(byte[] pkt)
    {
        try
        {
            if (pkt.Length < 28) return null;
            byte[] plain;
            if (BinaryPrimitives.ReadUInt32BigEndian(pkt) == Prefixo35)
            {
                int len = (int)BinaryPrimitives.ReadUInt32BigEndian(pkt.AsSpan(14));
                if (len < 28 || pkt.Length < 18 + len) return null;
                plain = new byte[len - 28];
                using var gcm = new AesGcm(ChaveUdp, 16);
                gcm.Decrypt(pkt.AsSpan(18, 12), pkt.AsSpan(30, plain.Length), pkt.AsSpan(30 + plain.Length, 16), plain, pkt.AsSpan(4, 14));
            }
            else
            {
                var corpo = pkt.AsSpan(20, pkt.Length - 28).ToArray();
                try { plain = Decrypt(corpo, ChaveUdp); }
                catch (CryptographicException) { plain = corpo; }       // na porta 6666, os antigos mandam sem cifrar
            }
            int abre = Array.IndexOf(plain, (byte)'{');
            if (abre < 0 || JsonNode.Parse(Encoding.UTF8.GetString(plain, abre, plain.Length - abre)) is not JsonObject j) return null;
            if (j["gwId"]?.ToString() is not { Length: > 0 } id || j["ip"]?.ToString() is not { Length: > 0 } ip) return null;
            double.TryParse(j["version"]?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double ver);
            return (id, ip, ver);
        }
        catch (Exception) { return null; }
    }

    /// <summary>Fica escutando os avisos. Parado na recepção, não gasta CPU; cada lâmpada acordada manda um a cada poucos segundos.</summary>
    public static void Ouvir(Action<string, string, double> aoOuvir)
    {
        foreach (int porta in new[] { 6666, 6667 })
        {
            new Thread(() =>
            {
                try
                {
                    using var c = new UdpClient();
                    c.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    c.Client.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Any, porta));
                    while (true)
                    {
                        System.Net.IPEndPoint? de = null;
                        var pkt = c.Receive(ref de);
                        if (LerAviso(pkt) is var (id, ip, ver))
                        {
                            try { aoOuvir(id, ip, ver); }
                            catch (Exception e) { Log.Write($"erro ao tratar aviso de lampada: {e.Message}"); }
                        }
                    }
                }
                catch (SocketException e) { Log.Write($"Avisos das lampadas: porta {porta} indisponivel ({e.Message})"); }
            }) { IsBackground = true, Name = $"avisos-{porta}" }.Start();
        }
    }
}
