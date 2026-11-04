using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinControl.Services;

/// <summary>
/// Uma lâmpada G-Light (Tuya) pela rede local, com cache de estado. Porta fiel do lampada.py.
/// DPs: 20 liga/desliga, 21 modo, 22 brilho (10-1000), 23 temperatura (0-1000),
/// 24 cor HSV em hex, 26 timer (segundos até inverter o estado).
/// </summary>
public sealed class Lampada(JsonObject cfg)
{
    public sealed class State
    {
        public bool On;
        public int B = 50, T = 50;
        public string Mode = "white";
        public string ModoBruto = "";     // o que a lâmpada informou: "scene" e "music" também aparecem aqui
        public int H = 30, S = 1000;
        public bool Online = true, Busy;
        public int Timer;                 // segundos restantes na última leitura
        public DateTime TimerAt;          // quando "Timer" foi lido
        public int? TimerOpt;             // opção escolhida (minutos), só para destacar o botão
    }

    private static readonly Dictionary<string, string> ErrHint = new()
    {
        ["901"] = "sem conexao: VPN bloqueando a rede local, IP errado ou lampada sem energia",
        ["905"] = "inacessivel: VPN bloqueando a rede local, IP errado ou lampada sem energia",
        ["902"] = "a lampada nao respondeu a tempo",
        ["914"] = "local_key ou versao errada (lampada readicionada no app?)",
        ["key"] = "preencha a chave em Configuracoes > Lampada",
    };
    // o Bluetooth do PC faz uma conexão de cada vez: ligar várias dormindo é em fila
    private static readonly object BleGate = new();

    public readonly State Cur = new();
    private JsonObject _cfg = cfg;
    private readonly object _callLock = new();
    private readonly SemaphoreSlim _toggleLock = new(1, 1);
    private int _fails;
    private string _lastErr = "";
    private volatile string? _ultimoErro;

    /// <summary>
    /// Brilho/temperatura que esta lâmpada deve usar quando for ligada: o que a última cena pediu (ex.: Noite = 1% quente).
    /// Cena e agenda nunca ligam a lâmpada sozinhas; elas só deixam isso combinado para o próximo "ligar".
    /// </summary>
    public (int b, int t)? Pendente;

    public void TrocarCfg(JsonObject novo) => _cfg = novo;

    public string Id => _cfg["id"]?.ToString() ?? "";
    public string Nome => _cfg["nome"]?.ToString() is { Length: > 0 } n ? n : "Lâmpada";
    public string Ip => _cfg["ip"]?.ToString() ?? "";
    public string BleMac => _cfg["ble_mac"]?.ToString() ?? "";
    public bool HasBle => Ble.ParseMac(BleMac) != null;
    public bool TemChave => (_cfg["local_key"]?.ToString() ?? "").Length == 16;
    public bool ToggleBusy => _toggleLock.CurrentCount == 0;
    public double Version => double.TryParse(_cfg["version"]?.ToString(), System.Globalization.NumberStyles.Float,
                                             System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 3.3;

    /// <summary>A última falha ao falar com a lâmpada, com a dica. A mensagem não leva a chave.</summary>
    public string? UltimoErro => _ultimoErro;

    /// <summary>O que falta para falar com ela, ou null se a config está completa.</summary>
    public string? ConfigProblem
    {
        get
        {
            if (Id.Length == 0 || Id == "SEU_DEVICE_ID") return "falta o id";
            return TemChave ? null : "falta a local_key";
        }
    }

    public void LimparErros() { _fails = 0; _lastErr = ""; _ultimoErro = null; }

    /// <summary>Só a lâmpada dona desta chave consegue responder com o estado dela: é assim que se reconhece quem é quem. Apenas lê.</summary>
    public bool RespondeEm(string ip, double versao)
    {
        lock (_callLock)
        {
            var r = Tuya.Call(ip, Id, _cfg["local_key"]?.ToString() ?? "", versao, Tuya.CmdDpQuery, new JsonObject(), timeoutMs: 1500, retries: 0);
            return r.Ok && r.Data?["dps"] is JsonObject;
        }
    }

    /// <summary>Executa um comando. Devolve a resposta, ou null se falhou (e marca offline).</summary>
    private JsonObject? Call(uint cmd, JsonObject extra, bool rapido = false, bool marcarOffline = true)
    {
        if (Ip.Length == 0)
        {
            // ainda não mandou aviso nenhum: apagada, ou com o Wi-Fi dormindo
            _ultimoErro = "O app ainda não encontrou esta lâmpada na rede. Ela está na energia e com o Wi-Fi acordado?";
            Cur.Online = false;
            return null;
        }
        Tuya.Reply r;
        lock (_callLock)
        {
            r = rapido
                ? Tuya.Call(Ip, Id, _cfg["local_key"]?.ToString() ?? "", Version, cmd, extra, timeoutMs: 2000, retries: 0)
                : Tuya.Call(Ip, Id, _cfg["local_key"]?.ToString() ?? "", Version, cmd, extra);
        }
        if (r.Ok)
        {
            if (_fails > 0) Log.Write($"{Nome}: conexao restabelecida ({Ip})");
            LimparErros();
            Cur.Online = true;
            Lamp.Respondeu(this);
            return r.Data ?? new JsonObject();
        }
        string msg = $"Err {r.Err}: {r.Message}";
        _fails++;
        if (msg != _lastErr)                  // loga cada erro novo uma vez, sem encher o arquivo
        {
            string hint = ErrHint.TryGetValue(r.Err, out var h) ? $" ({h})" : "";
            Log.Write($"{Nome}: falha ao falar com a lampada em {Ip} -> {msg}{hint}");
            _lastErr = msg;
            _ultimoErro = msg + hint;
        }
        if (marcarOffline) Cur.Online = false;
        return null;
    }

    private static JsonObject Dps(JsonObject dps) => new() { ["dps"] = dps };

    // ---------------- envio dos controles ----------------
    private readonly object _envioGate = new();
    private string? _envioPendente;
    private bool _enviando;
    private DateTime _protegidoAte = DateTime.MinValue;
    private long _geracao;
    private int _enviosOk, _enviosFalhos;

    public int EnviosOk => _enviosOk;
    public int EnviosFalhos => _enviosFalhos;

    /// <summary>Tem valor escolhido no controle que a lâmpada ainda pode não ter recebido.</summary>
    public bool EnvioEmAndamento
    {
        get { lock (_envioGate) return _enviando || _envioPendente != null || DateTime.UtcNow < _protegidoAte; }
    }

    /// <summary>
    /// Pede o envio do que está em Cur. Cada lâmpada tem a própria fila, para a lenta não segurar as outras, e só o valor
    /// mais recente importa: uma rajada do controle de cor vira poucos envios, sempre terminando no último.
    /// </summary>
    public void Enviar(string tipo)
    {
        lock (_envioGate)
        {
            _envioPendente = tipo;
            _geracao++;
            if (_enviando) return;
            _enviando = true;
        }
        Task.Run(LoopEnvio);
    }

    private void LoopEnvio()
    {
        int falhas = 0;
        while (true)
        {
            string tipo;
            lock (_envioGate)
            {
                if (_envioPendente == null)
                {
                    _enviando = false;
                    // a lâmpada leva um instante para refletir o comando: uma leitura logo depois devolveria o valor antigo
                    _protegidoAte = DateTime.UtcNow.AddSeconds(1.5);
                    return;
                }
                tipo = _envioPendente;
                _envioPendente = null;
            }
            bool ok;
            try { ok = EnviarAgora(tipo); }
            catch (Exception e) { Log.Write($"{Nome}: erro ao enviar ({e.Message})"); ok = false; }
            if (ok)
            {
                falhas = 0;
                Interlocked.Increment(ref _enviosOk);
                continue;
            }
            Interlocked.Increment(ref _enviosFalhos);
            // o último valor precisa chegar: tenta de novo, a menos que já exista um mais novo na fila
            if (++falhas >= 4)
            {
                Cur.Online = false;
                Bus.Notify();
                falhas = 0;
                continue;
            }
            Thread.Sleep(200 * falhas);
            lock (_envioGate) _envioPendente ??= tipo;
        }
    }

    private bool EnviarAgora(string tipo)
    {
        var c = Cur;
        if (!c.On) return true;             // apagada não recebe ajuste: nada a repetir
        JsonObject dps;
        if (tipo == "c" || (tipo == "b" && c.Mode == "colour"))
        {
            int h = ((c.H % 360) + 360) % 360, s = Math.Clamp(c.S, 0, 1000), br = Math.Clamp(c.B, 1, 100);
            dps = new JsonObject { ["21"] = "colour", ["24"] = $"{h:x4}{s:x4}{Math.Max(10, br * 10):x4}" };
        }
        else dps = WhiteDps(Math.Clamp(c.B, 1, 100), Math.Clamp(c.T, 0, 100));
        return Call(Tuya.CmdControl, Dps(dps), rapido: true, marcarOffline: false) != null;
    }

    /// <summary>Lê o estado real da lâmpada e atualiza o cache. "rapido" não insiste: com várias, uma dormindo não segura as outras.</summary>
    public bool RefreshState(bool rapido = false)
    {
        long geracao;
        lock (_envioGate) geracao = _geracao;
        var st = Call(Tuya.CmdDpQuery, new JsonObject(), rapido);
        if (st == null || st["dps"] is not JsonObject dps)
        {
            Cur.Online = false;
            return false;
        }
        // leitura de antes do último comando: traria de volta o valor antigo e ele seria reenviado por cima do novo
        lock (_envioGate)
            if (geracao != _geracao || _enviando || _envioPendente != null || DateTime.UtcNow < _protegidoAte) return true;
        Cur.On = dps["20"]?.GetValue<bool>() ?? false;
        Cur.ModoBruto = dps["21"]?.ToString() ?? "";
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
    public void ApplyWhite(int b, int t)
    {
        if (!Cur.On || !Cur.Online) return;
        b = Math.Clamp(b, 1, 100); t = Math.Clamp(t, 0, 100);
        if (Call(Tuya.CmdControl, Dps(WhiteDps(b, t))) != null)
        {
            Cur.B = b; Cur.T = t; Cur.Mode = "white";
        }
    }

    /// <summary>Define cor (matiz 0-359, saturação 0-1000) SEM ligar. Brilho = V do HSV.</summary>
    public void ApplyColour(int h, int s, int? b = null)
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
    public bool PowerOn(int? b = null, int? t = null)
    {
        // sem preset nem cena pendente: só liga, e a lâmpada volta sozinha ao último brilho/temperatura/cor
        (int, int)? preset = b != null ? (b.Value, t ?? Cur.T) : Pendente;
        Pendente = null;                                     // a cena vale para um "ligar", não para sempre
        var dps = preset is var (pb, pt) ? WhiteDps(pb, pt) : new JsonObject();
        dps["20"] = true;
        dps["26"] = 0;
        if (Call(Tuya.CmdControl, Dps(dps)) == null)
        {
            // Wi-Fi dormindo (lâmpada apagada há muito tempo): acorda pelo Bluetooth e depois acerta brilho pelo Wi-Fi
            if (!HasBle) return false;
            bool acordou;
            lock (BleGate) acordou = Ble.TurnOn(BleMac, _cfg["local_key"]?.ToString() ?? "");
            if (!acordou) return false;
            Cur.Online = true;
            Task.Run(() =>
            {
                Thread.Sleep(2500);
                if (Call(Tuya.CmdControl, Dps(dps)) == null) Log.Write($"{Nome}: BLE ligou, mas o Wi-Fi ainda nao voltou");
                Bus.Notify();
            });
        }
        Cur.On = true; Cur.Timer = 0; Cur.TimerOpt = null;
        if (preset is var (sb, st)) { Cur.B = sb; Cur.T = st; Cur.Mode = "white"; }
        else RefreshState();                                 // pega o estado que a lâmpada restaurou
        return true;
    }

    public bool PowerOff()
    {
        // zera o timer junto: um timer ativo religaria a lâmpada quando terminasse
        var dps = new JsonObject { ["20"] = false, ["26"] = 0 };
        if (Call(Tuya.CmdControl, Dps(dps)) == null) return false;
        Cur.On = false; Cur.Timer = 0; Cur.TimerOpt = null;
        return true;
    }

    /// <summary>Desliga a lâmpada daqui a `minutes` minutos (0 cancela). Só com ela ligada.</summary>
    public void SetTimer(int minutes)
    {
        if (!Cur.On || !Cur.Online) return;
        int secs = minutes * 60;
        if (Call(Tuya.CmdControl, Dps(new JsonObject { ["26"] = secs })) != null)
        {
            Cur.Timer = secs; Cur.TimerAt = DateTime.UtcNow; Cur.TimerOpt = minutes == 0 ? null : minutes;
            Log.Write(secs > 0 ? $"{Nome}: timer para desligar em {minutes} min" : $"{Nome}: timer cancelado");
        }
        Bus.Notify();
    }

    public int TimerLeft()
    {
        if (!Cur.On || Cur.Timer == 0) return 0;
        return Math.Max(0, Cur.Timer - (int)(DateTime.UtcNow - Cur.TimerAt).TotalSeconds);
    }

    /// <summary>Roda a ação mostrando "Conectando..." e ignorando cliques repetidos enquanto isso.</summary>
    public void WithBusy(Action fn)
    {
        if (!_toggleLock.Wait(0)) return;
        Cur.Busy = true;
        Bus.Notify();
        try { fn(); }
        finally
        {
            Cur.Busy = false;
            _toggleLock.Release();
            Bus.Notify();
        }
    }

    public void TogglePower() => WithBusy(() =>
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

    public void PresetOn(int b, int t) => WithBusy(() => PowerOn(b, t));
}

/// <summary>
/// As lâmpadas configuradas, e os comandos que valem para "todas as daqui": só as lâmpadas da rede em que o PC está
/// aparecem e são consultadas. Com uma lâmpada, tudo se comporta como se só existisse ela.
/// </summary>
public static class Lamp
{
    private const string Lampadas = "lampadas";
    private static readonly object Gate = new();
    private static readonly SemaphoreSlim GrupoLock = new(1, 1);
    private static JsonObject _raiz = new();
    private static List<Lampada> _todas = [];
    private static volatile IReadOnlyList<Lampada> _aqui = [];
    private static bool _ouvindo, _redeLigada;
    private static volatile bool _grupoBusy;

    public static IReadOnlyList<Lampada> Todas { get { lock (Gate) return _todas.ToList(); } }
    public static IReadOnlyList<Lampada> Aqui => _aqui;
    public static bool ToggleBusy => GrupoLock.CurrentCount == 0 || _aqui.Any(l => l.ToggleBusy);
    public static bool HasBle => _aqui.Any(l => l.HasBle);

    public static void Load()
    {
        if (!File.Exists(AppPaths.Lamp)) File.WriteAllText(AppPaths.Lamp, "{ \"lampadas\": [] }");
        lock (Gate)
        {
            try { _raiz = JsonNode.Parse(File.ReadAllText(AppPaths.Lamp)) as JsonObject ?? new(); }
            catch (Exception e) { Log.Write($"lamp_config.json invalido: {e.Message}"); return; }
            Migrar();
            var velhas = _todas;
            _todas = Itens().Select(cfg =>
            {
                string id = cfg["id"]?.ToString() ?? "";
                // recarregar o arquivo não pode apagar o estado de quem já estava aberto
                if (velhas.FirstOrDefault(v => v.Id == id) is not { } l) return new Lampada(cfg);
                l.TrocarCfg(cfg);
                return l;
            }).ToList();
        }
        if (!_redeLigada)
        {
            _redeLigada = true;
            Rede.Start();
            Rede.Mudou += () => { RecalcularAqui(); Core.PokePoller(); ProcurarSemIp(agora: true); };
        }
        RecalcularAqui();
        // sem lâmpada configurada, nada fica escutando: o app segue igual para quem não usa isso
        if (!_ouvindo && Todas.Count > 0)
        {
            _ouvindo = true;
            Tuya.Ouvir(AoOuvir);
        }
        if (Todas.Count == 0) Log.Write($"Nenhuma lampada configurada -> Configuracoes > Lampada ({AppPaths.Lamp})");
        else Log.Write($"Lampadas: {Todas.Count} configurada(s), {Aqui.Count} nesta rede");
    }

    private static List<JsonObject> Itens() => (_raiz[Lampadas] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];

    /// <summary>Formato antigo, de uma lâmpada só: vira o primeiro item da lista. O marcador do modelo simplesmente some.</summary>
    private static void Migrar()
    {
        string[] antigos = ["id", "ip", "local_key", "version", "ble_mac"];
        if (!antigos.Any(_raiz.ContainsKey) && _raiz[Lampadas] is JsonArray) return;
        var lista = _raiz[Lampadas] as JsonArray ?? new JsonArray();
        if (_raiz["id"]?.ToString() is { Length: > 0 } id && id != "SEU_DEVICE_ID")
        {
            var item = new JsonObject();
            foreach (var k in antigos) if (_raiz[k] is { } v) item[k] = v.DeepClone();
            lista.Add(item);
        }
        foreach (var k in antigos) _raiz.Remove(k);
        _raiz[Lampadas] = lista;
        Salvar();
    }

    private static void Salvar()
    {
        lock (Gate)
        {
            File.WriteAllText(AppPaths.Lamp, _raiz.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
        }
    }

    private static JsonObject? ItemDe(string id) => Itens().FirstOrDefault(i => i["id"]?.ToString() == id);

    // ---------------- em que lugar cada lâmpada mora ----------------
    /// <summary>"redes" ausente: ainda não se sabe, e a lâmpada fica na rede de agora. Vazia: escondida até dar sinal de vida.</summary>
    private static bool MoraEm(JsonObject item, IReadOnlyList<string> redes) =>
        item["redes"] is not JsonArray a || a.Any(r => redes.Contains(r?.ToString() ?? ""));

    public static void RecalcularAqui()
    {
        var redes = Rede.Atuais;
        List<Lampada> aqui;
        bool mudou = false;
        lock (Gate)
        {
            aqui = [];
            foreach (var l in _todas)
            {
                if (ItemDe(l.Id) is not { } item || l.ConfigProblem != null) continue;
                if (!MoraEm(item, redes)) continue;
                if (item["redes"] == null && redes.Count > 0)
                {
                    item["redes"] = new JsonArray(redes[0]);
                    mudou = true;
                }
                aqui.Add(l);
            }
        }
        if (mudou) Salvar();
        bool outraLista = !aqui.SequenceEqual(_aqui);
        _aqui = aqui;
        if (outraLista)
        {
            Log.Write($"Lampadas nesta rede: {(aqui.Count == 0 ? "nenhuma" : string.Join(", ", aqui.Select(l => l.Nome)))}");
            Bus.Notify();
        }
    }

    private static void AoOuvir(string id, string ip, double versao)
    {
        Lampada? l;
        bool mudou = false;
        lock (Gate)
        {
            if (ItemDe(id) is not { } item) return;     // aparelho que não é da nossa lista: nada a fazer com ele
            l = _todas.FirstOrDefault(x => x.Id == id);
            if (item["ip"]?.ToString() != ip)
            {
                Log.Write($"{item["nome"]}: encontrada em {ip}");
                item["ip"] = ip;
                mudou = true;
            }
            if (versao > 0 && Math.Abs((l?.Version ?? 0) - versao) > 0.01)
            {
                item["version"] = versao;
                mudou = true;
            }
            mudou |= AdicionarRedes(item);
        }
        if (mudou)
        {
            Salvar();
            l?.LimparErros();
            RecalcularAqui();
            Core.PokePoller();
        }
    }

    /// <summary>Quem dá sinal de vida aqui mora aqui também: a lâmpada que mudou de casa aparece sem ninguém configurar nada.</summary>
    private static bool AdicionarRedes(JsonObject item)
    {
        var redes = Rede.Atuais;
        if (redes.Count == 0) return false;
        if (item["redes"] is not JsonArray a) { item["redes"] = new JsonArray(redes[0]); return true; }
        if (a.Any(r => redes.Contains(r?.ToString() ?? ""))) return false;
        a.Add(redes[0]);
        return true;
    }

    private static readonly SemaphoreSlim BuscaLock = new(1, 1);
    private static DateTime _ultimaBusca = DateTime.MinValue;

    /// <summary>
    /// Nem toda lâmpada manda aviso UDP. Quem continua sem IP é procurado entre os aparelhos com a porta aberta. Só roda se
    /// faltar alguém, e no máximo a cada 5 minutos: lâmpada dormindo não abre a porta, e insistir não a acorda.
    /// </summary>
    public static void ProcurarSemIp(bool agora = false)
    {
        if (!_aqui.Any(l => l.Ip.Length == 0)) return;
        if (!agora && (DateTime.UtcNow - _ultimaBusca).TotalMinutes < 5) return;
        if (!BuscaLock.Wait(0)) return;
        try
        {
            _ultimaBusca = DateTime.UtcNow;
            var faltando = new System.Collections.Concurrent.ConcurrentDictionary<Lampada, bool>(_aqui.Where(l => l.Ip.Length == 0).Select(l => KeyValuePair.Create(l, true)));
            var conhecidos = Todas.Select(l => l.Ip).ToHashSet();
            var candidatos = Tuya.ComPortaAberta(Rede.Vizinhos().Where(ip => !conhecidos.Contains(ip)));
            Parallel.ForEach(candidatos, ip =>
            {
                foreach (var l in faltando.Keys)
                {
                    foreach (double versao in new[] { 3.3, 3.5 })
                    {
                        if (!faltando.ContainsKey(l) || !l.RespondeEm(ip, versao)) continue;
                        if (faltando.TryRemove(l, out _)) AoOuvir(l.Id, ip, versao);
                        return;
                    }
                }
            });
            if (faltando.Count > 0) Log.Write($"Busca na rede: sem resposta de {string.Join(", ", faltando.Keys.Select(l => l.Nome))} (apagada, dormindo ou sem energia)");
        }
        catch (Exception e) { Log.Write($"erro na busca de lampadas: {e.Message}"); }
        finally { BuscaLock.Release(); }
    }

    /// <summary>Respondeu pela rede: confere se esta rede já está registrada.</summary>
    internal static void Respondeu(Lampada l)
    {
        bool mudou;
        lock (Gate) mudou = ItemDe(l.Id) is { } item && AdicionarRedes(item);
        if (mudou) { Salvar(); RecalcularAqui(); }
    }

    // ---------------- todas as daqui ----------------
    /// <summary>Em paralelo: uma lâmpada dormindo não pode segurar as outras.</summary>
    public static void Cada(Action<Lampada> acao) => Cada(_aqui, acao);

    public static void Cada(IReadOnlyList<Lampada> a, Action<Lampada> acao)
    {
        if (a.Count == 1) { acao(a[0]); return; }
        Parallel.ForEach(a, l =>
        {
            try { acao(l); }
            catch (Exception e) { Log.Write($"{l.Nome}: erro no comando ({e.Message})"); }
        });
    }

    /// <summary>O estado que o painel mostra: o da lâmpada, se só há uma, ou o resumo de todas as daqui.</summary>
    public static Lampada.State Cur => Resumo(_aqui);

    /// <summary>O resumo de um grupo de lâmpadas: as daqui, ou as marcadas no painel.</summary>
    public static Lampada.State Resumo(IReadOnlyList<Lampada> a)
    {
        if (a.Count == 1) return a[0].Cur;
        var s = new Lampada.State { Online = false, Busy = _grupoBusy };
        if (a.Count == 0) return s;
        // a referência é uma lâmpada acesa: é o brilho dela que faz sentido mostrar no controle do grupo
        var r = a.FirstOrDefault(l => l.Cur.On && l.Cur.Online) ?? a.FirstOrDefault(l => l.Cur.Online) ?? a[0];
        s.On = a.Any(l => l.Cur.On && l.Cur.Online);
        s.Online = a.Any(l => l.Cur.Online);
        s.Busy |= a.Any(l => l.Cur.Busy);
        (s.B, s.T, s.Mode, s.H, s.S) = (r.Cur.B, r.Cur.T, r.Cur.Mode, r.Cur.H, r.Cur.S);
        (s.Timer, s.TimerAt, s.TimerOpt) = (r.Cur.Timer, r.Cur.TimerAt, r.Cur.TimerOpt);
        return s;
    }

    public static string? ConfigProblem
    {
        get
        {
            var t = Todas;
            if (t.Count == 0) return "nenhuma lâmpada configurada";
            if (_aqui.Count > 0) return null;
            return t.All(l => l.ConfigProblem != null) ? t[0].ConfigProblem : "nenhuma lâmpada nesta rede";
        }
    }

    public static bool RefreshState(bool rapido = false)
    {
        int ok = 0;
        // com várias, sempre rápido: quem falhar é consultado de novo no próximo ciclo
        Cada(l => { if (l.RefreshState(rapido || _aqui.Count > 1)) Interlocked.Increment(ref ok); });
        return ok > 0;
    }

    public static void ApplyWhite(int b, int t) => Cada(l => l.ApplyWhite(b, t));

    public static void ApplyColour(int h, int s, int? b = null) => Cada(l => l.ApplyColour(h, s, b));

    public static bool PowerOn(int? b = null, int? t = null)
    {
        int ok = 0;
        Cada(l => { if (l.PowerOn(b, t)) Interlocked.Increment(ref ok); });
        return ok > 0;
    }

    public static bool PowerOff()
    {
        int ok = 0;
        Cada(l => { if (l.PowerOff()) Interlocked.Increment(ref ok); });
        return ok > 0;
    }

    public static void SetTimer(int minutes) => Cada(l => l.SetTimer(minutes));

    public static int TimerLeft() => TimerLeft(_aqui);

    public static int TimerLeft(IReadOnlyList<Lampada> a) => a.Select(l => l.TimerLeft()).DefaultIfEmpty(0).Max();

    public static string FmtLeft(int secs)
    {
        int m = (int)Math.Ceiling(secs / 60.0);
        return m < 60 ? $"{m} min" : $"{m / 60} h {m % 60:00}";
    }

    public static void WithBusy(Action fn) => WithBusy(_aqui, fn);

    private static void WithBusy(IReadOnlyList<Lampada> a, Action fn)
    {
        if (a.Count == 1) { a[0].WithBusy(fn); return; }
        if (!GrupoLock.Wait(0)) return;
        _grupoBusy = true;
        Bus.Notify();
        try { fn(); }
        finally
        {
            _grupoBusy = false;
            GrupoLock.Release();
            Bus.Notify();
        }
    }

    public static void TogglePower() => TogglePower(_aqui);

    /// <summary>Com alguma acesa, apaga todas do grupo; com todas apagadas, liga todas.</summary>
    public static void TogglePower(IReadOnlyList<Lampada> a)
    {
        if (a.Count == 1) { a[0].TogglePower(); return; }
        WithBusy(a, () =>
        {
            // o cache de quem estava offline pode estar velho: relê antes de decidir
            Cada(a, l => { if (!l.Cur.Online) l.RefreshState(rapido: true); });
            if (a.Any(l => l.Cur.On && l.Cur.Online)) Cada(a, l => { if (l.Cur.Online) l.PowerOff(); });
            else Cada(a, l => l.PowerOn());
        });
    }

    public static void PresetOn(int b, int t) => PresetOn(_aqui, b, t);

    public static void PresetOn(IReadOnlyList<Lampada> a, int b, int t) => WithBusy(a, () => Cada(a, l => l.PowerOn(b, t)));

    public static void SetTimer(IReadOnlyList<Lampada> a, int minutes) => Cada(a, l => l.SetTimer(minutes));

    // ---------------- tela de configurações ----------------
    /// <summary>O que a tela mostra. A chave nunca sai daqui: só se ela já existe.</summary>
    public sealed record Config(string Id, string Nome, string Ip, string BleMac, bool TemChave, bool NestaRede, double Versao);

    public static Config LerConfig(Lampada l)
    {
        lock (Gate)
        {
            var item = ItemDe(l.Id);
            return new(l.Id, item?["nome"]?.ToString() ?? "", l.Ip, l.BleMac, l.TemChave, item != null && MoraEm(item, Rede.Atuais), l.Version);
        }
    }

    /// <summary>Chave vazia mantém a atual. Devolve o que está errado, ou null.</summary>
    public static string? ValidarConfig(string id, string chave, string ip, string bleMac, bool temChave)
    {
        if (id.Length == 0 || id.Any(char.IsWhiteSpace)) return "Preencha o ID do dispositivo, sem espaços.";
        if (chave.Length == 0 ? !temChave : chave.Length != 16) return "A chave local tem exatamente 16 caracteres.";
        if (ip.Length > 0 && !(System.Net.IPAddress.TryParse(ip, out var a) && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && ip.Count(c => c == '.') == 3))
            return "O IP precisa estar no formato 192.168.0.10, ou ficar vazio.";
        if (bleMac.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(bleMac, "^([0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$"))
            return "O MAC Bluetooth precisa estar no formato 42:07:67:06:8A:51, ou ficar vazio.";
        return null;
    }

    public static bool IdEmUso(string id, string? menos) => id != menos && Todas.Any(l => l.Id == id);

    /// <summary>Cria (idOriginal nulo) ou altera uma lâmpada.</summary>
    public static void SalvarConfig(string? idOriginal, string id, string nome, string chave, string ip, string bleMac, bool nestaRede)
    {
        lock (Gate)
        {
            var item = idOriginal == null ? null : ItemDe(idOriginal);
            if (item == null)
            {
                item = new JsonObject { ["version"] = 3.3 };
                (_raiz[Lampadas] as JsonArray ?? (JsonArray)(_raiz[Lampadas] = new JsonArray())).Add(item);
            }
            item["id"] = id;
            item["nome"] = nome;
            if (chave.Length > 0) item["local_key"] = chave;
            item["ip"] = ip;
            item["ble_mac"] = bleMac;
            DefinirNestaRede(item, nestaRede);
        }
        Salvar();
        Log.Write($"{(nome.Length > 0 ? nome : "Lampada")}: configuracao salva pela tela de configuracoes");
        Load();
        Todas.FirstOrDefault(l => l.Id == id)?.LimparErros();
        Core.PokePoller();
        Bus.Notify();
    }

    private static void DefinirNestaRede(JsonObject item, bool nestaRede)
    {
        var redes = Rede.Atuais;
        var a = item["redes"] as JsonArray ?? new JsonArray();
        if (nestaRede)
        {
            if (redes.Count > 0 && !a.Any(r => redes.Contains(r?.ToString() ?? ""))) a.Add(redes[0]);
            if (redes.Count == 0 && a.Count == 0) { item.Remove("redes"); return; }
        }
        else
        {
            foreach (var r in a.Where(r => redes.Contains(r?.ToString() ?? "")).ToList()) a.Remove(r);
        }
        item["redes"] = a;
    }

    public static void Remover(string id)
    {
        lock (Gate)
        {
            if (ItemDe(id) is { } item) (_raiz[Lampadas] as JsonArray)?.Remove(item);
        }
        Salvar();
        Load();
        Log.Write("Lampada removida pela tela de configuracoes");
        Bus.Notify();
    }
}
