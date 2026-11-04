using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinControl.Services;

/// <summary>
/// Cenas (lâmpada + telas + tema de uma vez) e agenda por horário. Tudo fica em conforto.json.
/// Formato de uma cena ou item da agenda (todas as chaves são opcionais):
///   "lampada": {"ligada": true, "brilho": 1, "temperatura": 0}   ou {"ligada": false}
///   "telas": 40 (todas)  ou  {"Monitor externo": 40, "Notebook": 20}
///   "tema": "escuro" | "claro"
///   "luz_noturna": true | false
///   "cena": "Noite"  (só na agenda: aplica uma cena pelo nome)
/// Porta do conforto.py.
/// </summary>
public static class Conforto
{
    private const string DefaultsJson = """
    {
      "tema_automatico": true,
      "telas_juntas": false,
      "agenda_ativa": false,
      "agenda": [],
      "monitor_girar": "",
      "girar_sentido": 90,
      "atalho_girar": "Ctrl+Alt+R",
      "atalho_tema": "Win+Ctrl+Shift+D",
      "luz_noturna_lampada": true,
      "luz_noturna_brilho": 1,
      "luz_noturna_temperatura": 0,
      "predefinicoes": [
        { "nome": "Noite", "brilho": 1, "temperatura": 0 },
        { "nome": "Relaxar", "brilho": 35, "temperatura": 15 },
        { "nome": "Padrão", "brilho": 50, "temperatura": 50 },
        { "nome": "Foco", "brilho": 100, "temperatura": 100 }
      ],
      "cenas": [
        { "nome": "Trabalho", "lampada": { "ligada": true, "brilho": 50, "temperatura": 50 }, "telas": 85, "tema": "claro", "luz_noturna": false },
        { "nome": "Noite", "lampada": { "ligada": true, "brilho": 1, "temperatura": 0 }, "telas": 0, "tema": "escuro", "luz_noturna": true },
        { "nome": "Filme", "lampada": { "ligada": false }, "telas": 40, "tema": "escuro" }
      ]
    }
    """;

    public static JsonObject Conf { get; private set; } = (JsonObject)JsonNode.Parse(DefaultsJson)!;
    public static volatile string? Cena;     // cena ativa (destaque no painel)
    private static DateTime _mtime;
    private static (DateOnly day, string hora)? _lastSlot;

    public static bool Flag(string key) => Conf[key]?.GetValue<bool>() ?? false;

    public static string Texto(string key) => Conf[key]?.ToString() ?? "";

    public static int Inteiro(string key, int padrao)
    {
        try { return Conf[key]?.GetValue<int>() ?? padrao; }
        catch (Exception) { return padrao; }   // editado à mão, com um valor que não é número
    }

    /// <summary>Muda uma opção qualquer e grava.</summary>
    public static void Definir(string key, JsonNode? valor)
    {
        Conf[key] = valor;
        Save();
        Hotkeys.Recarregar();
        Bus.Notify();
    }

    public sealed record Predefinicao(string Nome, int Brilho, int Temperatura);

    /// <summary>Os botões da lâmpada no painel e na bandeja.</summary>
    public static List<Predefinicao> Predefinicoes =>
        (Conf["predefinicoes"] as JsonArray)?.OfType<JsonObject>().Select(p => new Predefinicao(
            p["nome"]?.ToString() ?? "?", Math.Clamp(p["brilho"]?.GetValue<int>() ?? 50, 1, 100), Math.Clamp(p["temperatura"]?.GetValue<int>() ?? 50, 0, 100))).ToList() ?? [];

    public static JsonObject? CenaPorNome(string nome) => Cenas.OfType<JsonObject>().FirstOrDefault(x => x["nome"]?.ToString() == nome);

    /// <summary>Cria (nomeOriginal nulo) ou troca uma cena. Na agenda, os itens que apontam para ela seguem o nome novo.</summary>
    public static void SalvarCena(string? nomeOriginal, JsonObject cena)
    {
        string novo = cena["nome"]!.ToString();
        var lista = Conf["cenas"] as JsonArray ?? (JsonArray)(Conf["cenas"] = new JsonArray());
        if (nomeOriginal != null && CenaPorNome(nomeOriginal) is { } velha)
        {
            lista[lista.IndexOf(velha)] = cena;
            if (nomeOriginal != novo)
            {
                foreach (var item in Agenda.OfType<JsonObject>().Where(i => i["cena"]?.ToString() == nomeOriginal)) item["cena"] = novo;
                if (Cena == nomeOriginal) Cena = novo;
            }
        }
        else lista.Add(cena);
        Save();
        Bus.Notify();
    }

    public static void RemoverCena(string nome)
    {
        if (CenaPorNome(nome) is { } c) Cenas.Remove(c);
        // item da agenda que só chamava esta cena não tem mais o que fazer
        foreach (var item in Agenda.OfType<JsonObject>().Where(i => i["cena"]?.ToString() == nome).ToList()) Agenda.Remove(item);
        if (Cena == nome) Cena = null;
        Save();
        Bus.Notify();
    }

    public static JsonArray Cenas => Conf["cenas"] as JsonArray ?? new JsonArray();
    public static JsonArray Agenda => Conf["agenda"] as JsonArray ?? new JsonArray();

    public static void Load()
    {
        if (!File.Exists(AppPaths.Conforto))
        {
            Conf = (JsonObject)JsonNode.Parse(DefaultsJson)!;
            Save();
        }
        try
        {
            var data = (JsonObject)JsonNode.Parse(File.ReadAllText(AppPaths.Conforto))!;
            var merged = (JsonObject)JsonNode.Parse(DefaultsJson)!;
            foreach (var kv in data) merged[kv.Key] = kv.Value?.DeepClone();
            Conf = merged;
            _mtime = File.GetLastWriteTimeUtc(AppPaths.Conforto);
        }
        catch (Exception e)
        {
            Log.Write($"conforto.json invalido, usando o padrao: {e.Message}");
            Conf = (JsonObject)JsonNode.Parse(DefaultsJson)!;
        }
        Hotkeys.Recarregar();
    }

    public static void Save()
    {
        File.WriteAllText(AppPaths.Conforto, Conf.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        _mtime = File.GetLastWriteTimeUtc(AppPaths.Conforto);
    }

    public static void SetOption(string key, bool value)
    {
        Conf[key] = value;
        Save();
        if (key == "agenda_ativa" && value) _lastSlot = null;   // reativou: aplica o horário atual já
        Bus.Notify();
    }

    /// <summary>Resumo curto de uma cena/item da agenda, ex.: "luz 1% · telas 0%".</summary>
    public static string Describe(JsonObject spec)
    {
        var parts = new List<string>();
        if (spec["lampada"] is JsonObject lp)
        {
            bool on = lp["ligada"]?.GetValue<bool>() ?? true;
            parts.Add(on && lp["brilho"] != null ? $"luz {lp["brilho"]}%" : on ? "luz on" : "luz off");
        }
        if (spec["telas"] is { } t) parts.Add(t is JsonObject ? "telas" : $"telas {t}%");
        if (spec["tema"] != null) parts.Add(spec["tema"]!.ToString());
        if (spec["luz_noturna"] is { } nl) parts.Add(nl.GetValue<bool>() ? "luz noturna" : "sem luz noturna");
        if (spec["cena"] != null) parts.Add($"cena {spec["cena"]}");
        return string.Join(" · ", parts);
    }

    /// <summary>O ajuste combinado por uma cena para o próximo "ligar". Cada lâmpada daqui guarda o seu: vale uma vez para cada.</summary>
    public static (int b, int t)? PendingLamp
    {
        get => Lamp.Aqui.Select(l => l.Pendente).FirstOrDefault(p => p != null);
        set { foreach (var l in Lamp.Aqui) l.Pendente = value; }
    }

    /// <summary>
    /// A parte da lâmpada de uma cena ou item da agenda, para cada lâmpada daqui. Nunca acende nenhuma: a que já está
    /// ligada só ajusta brilho e temperatura; a desligada guarda o ajuste para o próximo "ligar" dela. Uma cena que apaga
    /// a luz ("ligada": false) só apaga quando eu mesmo a escolho, nunca pela agenda.
    /// </summary>
    public static void ApplyLampPart(JsonObject lp, bool auto)
    {
        bool wantOn = lp["ligada"]?.GetValue<bool>() ?? true;
        if (!wantOn)
        {
            PendingLamp = null;
            if (!auto) Lamp.WithBusy(() => Lamp.PowerOff());
            return;
        }
        int? b = lp["brilho"]?.GetValue<int>(), t = lp["temperatura"]?.GetValue<int>();
        Lamp.Cada(l =>
        {
            if (b != null) l.Pendente = (b.Value, t ?? l.Cur.T);
            // não confia no cache: ligar/desligar pode ter sido pelo app da Tuya
            l.RefreshState(rapido: Lamp.Aqui.Count > 1);
            if (l.Cur.On && l.Cur.Online) l.ApplyWhite(b ?? l.Cur.B, t ?? l.Cur.T);
        });
    }

    /// <summary>Aplica uma cena ou item da agenda. Bloqueia enquanto fala com a lâmpada: chame numa thread.</summary>
    public static void Apply(JsonObject spec, string? nome = null, bool auto = false)
    {
        if (spec["cena"] is { } c)
        {
            var cena = Cenas.OfType<JsonObject>().FirstOrDefault(x => x["nome"]?.ToString() == c.ToString());
            if (cena == null) { Log.Write($"Agenda: cena '{c}' nao existe"); return; }
            Apply(cena, cena["nome"]!.ToString(), auto);
            return;
        }
        if (spec["luz_noturna"] is { } nl) Theme.SetNightLight(nl.GetValue<bool>());
        if (spec["tema"] is { } tema) Theme.SetDark(tema.ToString() == "escuro");
        if (spec["telas"] is { } telas)
        {
            if (telas is JsonObject map)
                Screens.SetByName(map.ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<int>()));
            else Screens.SetByName(telas.GetValue<int>());
        }
        if (spec["lampada"] is JsonObject lp)
        {
            try { ApplyLampPart(lp, auto); }
            catch (Exception e) { Log.Write($"Cena: falha na parte da lampada ({e.Message})"); }
        }
        Cena = nome;
        Log.Write($"Aplicado: {nome ?? Describe(spec)}");
        Bus.Notify();
    }

    /// <summary>Muda o tema que a cena aplica: "escuro", "claro" ou null (não mexe no tema).</summary>
    public static void SetSceneTheme(string nome, string? tema)
    {
        if (Cenas.OfType<JsonObject>().FirstOrDefault(x => x["nome"]?.ToString() == nome) is not { } cena) return;
        if (tema == null) cena.Remove("tema"); else cena["tema"] = tema;
        Save();
        Bus.Notify();
    }

    /// <summary>Liga ou desliga a Luz Noturna quando a cena é aplicada.</summary>
    public static void SetSceneNightLight(string nome, bool on)
    {
        if (Cenas.OfType<JsonObject>().FirstOrDefault(x => x["nome"]?.ToString() == nome) is not { } cena) return;
        cena["luz_noturna"] = on;
        Save();
        Bus.Notify();
    }

    /// <summary>Guarda na cena o estado de agora: lâmpada (brilho/temperatura ou apagada), brilho das telas e tema.</summary>
    public static void SaveSceneFromCurrent(string nome)
    {
        if (Cenas.OfType<JsonObject>().FirstOrDefault(x => x["nome"]?.ToString() == nome) is not { } cena) return;
        var c = Lamp.Cur;
        if (c.Online)
            cena["lampada"] = c.On
                ? new JsonObject { ["ligada"] = true, ["brilho"] = c.B, ["temperatura"] = c.T }
                : new JsonObject { ["ligada"] = false };
        var telas = new JsonObject();
        foreach (var m in Screens.Monitors.Where(m => m.Ok && m.Value != null)) telas[m.Name] = m.Value!.Value;
        if (telas.Count > 0) cena["telas"] = telas;
        cena["tema"] = Theme.IsDark() ? "escuro" : "claro";
        cena["luz_noturna"] = Theme.NightLightOn();
        Save();
        Log.Write($"Cena '{nome}' atualizada: {Describe(cena)}");
        Bus.Notify();
    }

    public static void ApplyScene(string nome)
    {
        var cena = Cenas.OfType<JsonObject>().FirstOrDefault(x => x["nome"]?.ToString() == nome);
        if (cena != null) Apply(cena, nome);
    }

    // ======================= agenda =======================
    private static int Mins(string hora)
    {
        var p = hora.Split(':');
        return int.Parse(p[0]) * 60 + int.Parse(p[1]);
    }

    public static List<JsonObject> Sorted()
    {
        var list = new List<(int m, JsonObject e)>();
        foreach (var e in Agenda.OfType<JsonObject>())
        {
            try { list.Add((Mins(e["hora"]!.ToString()), e)); }
            catch { Log.Write($"Agenda: item ignorado (hora invalida): {e.ToJsonString()}"); }
        }
        return list.OrderBy(x => x.m).Select(x => x.e).ToList();
    }

    /// <summary>Item da agenda em vigor agora e a data em que ele começou (ontem, se antes do 1º).</summary>
    public static (JsonObject? item, DateOnly day) CurrentSlot(DateTime? now = null)
    {
        var n = now ?? DateTime.Now;
        var ents = Sorted();
        var today = DateOnly.FromDateTime(n);
        if (ents.Count == 0) return (null, today);
        int m = n.Hour * 60 + n.Minute;
        var past = ents.Where(e => Mins(e["hora"]!.ToString()) <= m).ToList();
        return past.Count > 0 ? (past[^1], today) : (ents[^1], today.AddDays(-1));
    }

    public static JsonObject? NextItem(DateTime? now = null)
    {
        var n = now ?? DateTime.Now;
        var ents = Sorted();
        int m = n.Hour * 60 + n.Minute;
        return ents.FirstOrDefault(e => Mins(e["hora"]!.ToString()) > m) ?? ents.FirstOrDefault();
    }

    private static void AgendaLoop()
    {
        while (true)
        {
            try
            {
                // recarrega se o conforto.json foi editado à mão
                if (File.Exists(AppPaths.Conforto) && File.GetLastWriteTimeUtc(AppPaths.Conforto) != _mtime)
                {
                    Load();
                    Log.Write("conforto.json recarregado");
                    Bus.Notify();
                }
                if (Flag("agenda_ativa"))
                {
                    var (item, day) = CurrentSlot();
                    if (item != null)
                    {
                        var slot = (day, item["hora"]!.ToString());
                        if (_lastSlot != slot)
                        {
                            _lastSlot = slot;
                            Apply(item, auto: true);   // a agenda nunca liga nem desliga a lâmpada
                        }
                    }
                }
            }
            catch (Exception e) { Log.Write($"erro na agenda: {e.Message}"); }
            Thread.Sleep(20_000);
        }
    }

    public static void Start()
    {
        Load();
        new Thread(AgendaLoop) { IsBackground = true, Name = "agenda" }.Start();
    }
}
