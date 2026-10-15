using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinControl.Services;

/// <summary>
/// Cenas (lâmpada + telas + tema de uma vez) e agenda por horário. Tudo fica em conforto.json.
/// Formato de uma cena ou item da agenda (todas as chaves são opcionais):
///   "lampada": {"ligada": true, "brilho": 1, "temperatura": 0}   ou {"ligada": false}
///   "telas": 40 (todas)  ou  {"LG ULTRAGEAR": 40, "Notebook": 20}
///   "tema": "escuro" | "claro"
///   "cena": "Noite"  (só na agenda: aplica uma cena pelo nome)
/// Porta do conforto.py.
/// </summary>
public static class Conforto
{
    private const string DefaultsJson = """
    {
      "tema_automatico": true,
      "telas_juntas": false,
      "agenda_ativa": true,
      "agenda": [ { "hora": "05:00", "telas": 85 }, { "hora": "18:30", "telas": 0 } ],
      "cenas": [
        { "nome": "Trabalho", "lampada": { "ligada": true, "brilho": 50, "temperatura": 50 }, "telas": 85, "tema": "claro" },
        { "nome": "Noite", "lampada": { "ligada": true, "brilho": 1, "temperatura": 0 }, "telas": 0, "tema": "escuro" },
        { "nome": "Filme", "lampada": { "ligada": false }, "telas": 40, "tema": "escuro" }
      ]
    }
    """;

    public static JsonObject Conf { get; private set; } = (JsonObject)JsonNode.Parse(DefaultsJson)!;
    public static volatile string? Cena;     // cena ativa (destaque no painel)
    private static DateTime _mtime;
    private static (DateOnly day, string hora)? _lastSlot;

    public static bool Flag(string key) => Conf[key]?.GetValue<bool>() ?? false;

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
        if (spec["cena"] != null) parts.Add($"cena {spec["cena"]}");
        return string.Join(" · ", parts);
    }

    /// <summary>Aplica uma cena ou item da agenda. Bloqueia enquanto fala com a lâmpada: chame numa thread.</summary>
    public static void Apply(JsonObject spec, string? nome = null)
    {
        if (spec["cena"] is { } c)
        {
            var cena = Cenas.OfType<JsonObject>().FirstOrDefault(x => x["nome"]?.ToString() == c.ToString());
            if (cena == null) { Log.Write($"Agenda: cena '{c}' nao existe"); return; }
            Apply(cena, cena["nome"]!.ToString());
            return;
        }
        if (spec["tema"] is { } tema) Theme.SetDark(tema.ToString() == "escuro");
        if (spec["telas"] is { } telas)
        {
            if (telas is JsonObject map)
                Screens.SetByName(map.ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<int>()));
            else Screens.SetByName(telas.GetValue<int>());
        }
        if (spec["lampada"] is JsonObject lp)
        {
            if (lp["ligada"]?.GetValue<bool>() ?? true)
                Lamp.WithBusy(() => Lamp.PowerOn(lp["brilho"]?.GetValue<int>(), lp["temperatura"]?.GetValue<int>()));
            else
                Lamp.WithBusy(() => Lamp.PowerOff());
        }
        Cena = nome;
        Log.Write($"Aplicado: {nome ?? Describe(spec)}");
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
                            Apply(item);
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
