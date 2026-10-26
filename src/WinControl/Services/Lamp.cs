using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinControl.Services;

/// <summary>
/// Lâmpada G-Light (Tuya) pela rede local, com cache de estado. Porta fiel do lampada.py.
/// DPs: 20 liga/desliga, 21 modo, 22 brilho (10-1000), 23 temperatura (0-1000),
/// 24 cor HSV em hex, 26 timer (segundos até inverter o estado).
/// </summary>
public static class Lamp
{
    public sealed class State
    {
        public bool On;
        public int B = 50, T = 50;
        public string Mode = "white";
        public int H = 30, S = 1000;
        public bool Online = true, Busy;
        public int Timer;                 // segundos restantes na última leitura
        public DateTime TimerAt;          // quando "Timer" foi lido
        public int? TimerOpt;             // opção escolhida (minutos), só para destacar o botão
    }

    public static readonly State Cur = new();
    private static JsonObject _cfg = new();
    private static readonly object CallLock = new();
    private static readonly SemaphoreSlim ToggleLock = new(1, 1);
    private static int _fails;
    private static string _lastErr = "";
    private static DateTime _lastFind = DateTime.MinValue;

    public static bool ToggleBusy => ToggleLock.CurrentCount == 0;

    private static readonly Dictionary<string, string> ErrHint = new()
    {
        ["901"] = "sem conexao: VPN bloqueando a rede local, IP errado ou lampada sem energia",
        ["905"] = "inacessivel: VPN bloqueando a rede local, IP errado ou lampada sem energia",
        ["902"] = "a lampada nao respondeu a tempo",
        ["914"] = "local_key ou versao errada (lampada readicionada no app?)",
        ["key"] = "rode o pegar_chave.py para buscar a chave",
    };

    public static void Load()
    {
        if (!File.Exists(AppPaths.Lamp))
        {
            // cria o modelo no lugar certo: quem for configurar já acha o arquivo (e o log diz onde ele está)
            File.WriteAllText(AppPaths.Lamp, """
            { "id": "SEU_DEVICE_ID", "ip": "", "local_key": "SUA_LOCAL_KEY", "version": 3.3, "ble_mac": "" }
            """);
        }
        try { _cfg = JsonNode.Parse(File.ReadAllText(AppPaths.Lamp)) as JsonObject ?? new(); }
        catch (Exception e) { Log.Write($"lamp_config.json invalido: {e.Message}"); }
        if (ConfigProblem is { } p) Log.Write($"Lampada nao configurada: {p} -> {AppPaths.Lamp} (veja docs/lampada.md)");
    }

    /// <summary>O que falta na config para falar com a lâmpada, ou null se ela está completa.</summary>
    public static string? ConfigProblem
    {
        get
        {
            string id = _cfg["id"]?.ToString() ?? "", key = _cfg["local_key"]?.ToString() ?? "";
            if (id.Length == 0 || id == "SEU_DEVICE_ID") return "falta o id";
            if (key.Length != 16) return "falta a local_key";
            return null;
        }
    }

    private static void SaveCfg() =>
        File.WriteAllText(AppPaths.Lamp, _cfg.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    private static string Ip => _cfg["ip"]?.ToString() ?? "";
    private static double Version => double.TryParse(_cfg["version"]?.ToString(), System.Globalization.NumberStyles.Float,
                                                     System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 3.3;

    /// <summary>Executa um comando. Devolve a resposta, ou null se falhou (e marca offline).</summary>
    private static JsonObject? Call(uint cmd, JsonObject extra)
    {
        Tuya.Reply r;
        lock (CallLock)
        {
            r = Tuya.Call(Ip, _cfg["id"]?.ToString() ?? "", _cfg["local_key"]?.ToString() ?? "", Version, cmd, extra);
        }
        if (r.Ok)
        {
            if (_fails > 0) Log.Write($"Conexao com a lampada restabelecida ({Ip})");
            _fails = 0; _lastErr = "";
            Cur.Online = true;
            return r.Data ?? new JsonObject();
        }
        string msg = $"Err {r.Err}: {r.Message}";
        _fails++;
        if (msg != _lastErr)                  // loga cada erro novo uma vez, sem encher o arquivo
        {
            string hint = ErrHint.TryGetValue(r.Err, out var h) ? $" ({h})" : "";
            Log.Write($"Falha ao falar com a lampada em {Ip} -> {msg}{hint}");
            _lastErr = msg;
        }
        Cur.Online = false;
        return null;
    }

    private static JsonObject Dps(JsonObject dps) => new() { ["dps"] = dps };

    /// <summary>Lê o estado real da lâmpada e atualiza o cache.</summary>
    public static bool RefreshState()
    {
        var st = Call(Tuya.CmdDpQuery, new JsonObject());
        if (st == null || st["dps"] is not JsonObject dps)
        {
            Cur.Online = false;
            return false;
        }
        Cur.On = dps["20"]?.GetValue<bool>() ?? false;
        Cur.Mode = dps["21"]?.ToString() == "colour" ? "colour" : "white";
        Cur.T = Math.Clamp((int)Math.Round(ToInt(dps["23"], 500) / 10.0), 0, 100);
        string hsv = dps["24"]?.ToString() ?? "";
        int v = 1000;
        if (hsv.Length == 12)
        {
            try
            {
                Cur.H = Convert.ToInt32(hsv[0..4], 16) % 360;
                Cur.S = Math.Clamp(Convert.ToInt32(hsv[4..8], 16), 0, 1000);
                v = Convert.ToInt32(hsv[8..12], 16);
            }
            catch (FormatException) { }
        }
        int rawB = Cur.Mode == "colour" ? v : ToInt(dps["22"], 500);
        Cur.B = Math.Clamp((int)Math.Round(rawB / 10.0), 1, 100);
        if (dps["26"] != null)
        {
            Cur.Timer = ToInt(dps["26"], 0);
            Cur.TimerAt = DateTime.UtcNow;
        }
        return true;
    }

    private static int ToInt(JsonNode? n, int def)
    {
        try { return n == null ? def : (int)n.GetValue<double>(); }
        catch { return def; }
    }

    private static JsonObject WhiteDps(int b, int t) => new()
    {
        ["21"] = "white",
        ["22"] = Math.Max(10, b * 10),
        ["23"] = t * 10,
    };

    /// <summary>Aplica brilho/temperatura SEM ligar (só envia se já estiver ligada).</summary>
    public static void ApplyWhite(int b, int t)
    {
        if (!Cur.On || !Cur.Online) return;
        b = Math.Clamp(b, 1, 100); t = Math.Clamp(t, 0, 100);
        if (Call(Tuya.CmdControl, Dps(WhiteDps(b, t))) != null)
        {
            Cur.B = b; Cur.T = t; Cur.Mode = "white";
        }
    }

    /// <summary>Define cor (matiz 0-359, saturação 0-1000) SEM ligar. Brilho = V do HSV.</summary>
    public static void ApplyColour(int h, int s, int? b = null)
    {
        if (!Cur.On || !Cur.Online) return;
        h = ((h % 360) + 360) % 360; s = Math.Clamp(s, 0, 1000);
        int br = Math.Clamp(b ?? Cur.B, 1, 100);
        string hex = $"{h:x4}{s:x4}{Math.Max(10, br * 10):x4}";
        var dps = new JsonObject { ["21"] = "colour", ["24"] = hex };
        if (Call(Tuya.CmdControl, Dps(dps)) != null)
        {
            Cur.H = h; Cur.S = s; Cur.B = br; Cur.Mode = "colour";
        }
    }

    /// <summary>
    /// Liga pelo PC. Sem preset explícito: o que a última cena combinou (ex.: Noite = 1% quente), uma vez só; sem cena,
    /// volta como estava quando apagou. Cancela timer pendente.
    /// </summary>
    public static bool PowerOn(int? b = null, int? t = null)
    {
        // sem preset nem cena pendente: só liga, e a lâmpada volta sozinha ao último brilho/temperatura/cor
        (int, int)? preset = b != null ? (b.Value, t ?? Cur.T) : Conforto.PendingLamp;
        Conforto.PendingLamp = null;                         // a cena vale para um "ligar", não para sempre
        var dps = preset is var (pb, pt) ? WhiteDps(pb, pt) : new JsonObject();
        dps["20"] = true;
        dps["26"] = 0;
        if (Call(Tuya.CmdControl, Dps(dps)) == null)
        {
            // Wi-Fi dormindo (lâmpada apagada há muito tempo): acorda pelo Bluetooth e depois acerta brilho pelo Wi-Fi
            if (!HasBle || !Ble.TurnOn(BleMac, _cfg["local_key"]?.ToString() ?? "")) return false;
            Cur.Online = true;
            Task.Run(() =>
            {
                Thread.Sleep(2500);
                if (Call(Tuya.CmdControl, Dps(dps)) == null) Log.Write("BLE: ligou, mas o Wi-Fi ainda nao voltou");
                Bus.Notify();
            });
        }
        Cur.On = true; Cur.Timer = 0; Cur.TimerOpt = null;
        if (preset is var (sb, st)) { Cur.B = sb; Cur.T = st; Cur.Mode = "white"; }
        else RefreshState();                                 // pega o estado que a lâmpada restaurou
        return true;
    }

    private static string BleMac => _cfg["ble_mac"]?.ToString() ?? "";
    public static bool HasBle => Ble.ParseMac(BleMac) != null;

    public static bool PowerOff()
    {
        // zera o timer junto: um timer ativo religaria a lâmpada quando terminasse
        var dps = new JsonObject { ["20"] = false, ["26"] = 0 };
        if (Call(Tuya.CmdControl, Dps(dps)) == null) return false;
        Cur.On = false; Cur.Timer = 0; Cur.TimerOpt = null;
        return true;
    }

    /// <summary>Desliga a lâmpada daqui a `minutes` minutos (0 cancela). Só com ela ligada.</summary>
    public static void SetTimer(int minutes)
    {
        if (!Cur.On || !Cur.Online) return;
        int secs = minutes * 60;
        if (Call(Tuya.CmdControl, Dps(new JsonObject { ["26"] = secs })) != null)
        {
            Cur.Timer = secs; Cur.TimerAt = DateTime.UtcNow; Cur.TimerOpt = minutes == 0 ? null : minutes;
            Log.Write(secs > 0 ? $"Timer: desligar em {minutes} min" : "Timer cancelado");
        }
        Bus.Notify();
    }

    public static int TimerLeft()
    {
        if (!Cur.On || Cur.Timer == 0) return 0;
        return Math.Max(0, Cur.Timer - (int)(DateTime.UtcNow - Cur.TimerAt).TotalSeconds);
    }

    public static string FmtLeft(int secs)
    {
        int m = (int)Math.Ceiling(secs / 60.0);
        return m < 60 ? $"{m} min" : $"{m / 60} h {m % 60:00}";
    }

    /// <summary>Roda a ação mostrando "Conectando..." e ignorando cliques repetidos enquanto isso.</summary>
    public static void WithBusy(Action fn)
    {
        if (!ToggleLock.Wait(0)) return;
        Cur.Busy = true;
        Bus.Notify();
        try { fn(); }
        finally
        {
            Cur.Busy = false;
            ToggleLock.Release();
            Bus.Notify();
        }
    }

    public static void TogglePower() => WithBusy(() =>
    {
        // se estava offline, o "ligada/desligada" do cache pode estar velho: relê antes
        if (!Cur.Online && !RefreshState())
        {
            // sem Wi-Fi: com Bluetooth configurado, a lâmpada provavelmente está apagada e dormindo, então liga
            if (HasBle) PowerOn();
            return;
        }
        if (Cur.On) PowerOff(); else PowerOn();
    });

    public static void PresetOn(int b, int t) => WithBusy(() => PowerOn(b, t));

    /// <summary>Depois de várias falhas, procura a lâmpada pelo aviso UDP; se mudou de IP, atualiza a config.</summary>
    public static void Rediscover()
    {
        if (_fails < 3 || (DateTime.UtcNow - _lastFind).TotalSeconds < 120) return;
        _lastFind = DateTime.UtcNow;
        var found = Tuya.Discover(_cfg["id"]?.ToString() ?? "");
        if (found == null)
        {
            Log.Write("Lampada nao apareceu na rede (sem energia ou fora do Wi-Fi?)");
            return;
        }
        var (ip, ver) = found.Value;
        if (ver == 0) ver = Version;
        if (ip != Ip || Math.Abs(ver - Version) > 0.01)
        {
            Log.Write($"Lampada encontrada em {ip} (v{ver}); atualizando lamp_config.json (antes {Ip})");
            _cfg["ip"] = ip;
            _cfg["version"] = ver;
            SaveCfg();
            _fails = 0;
        }
        else
        {
            Log.Write($"A lampada esta anunciando em {ip}, mas nao responde ao PC: provavelmente VPN ou firewall bloqueando a rede local");
        }
    }
}
