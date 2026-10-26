using System.Diagnostics;
using System.Management;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WinControl.Services;

/// <summary>
/// Modo automático do DualSense: percebe quando um jogo abre (WMI), descobre se ele suporta o DualSense pelas
/// categorias da loja da Steam (57/58) e escolhe nativo ou PS4. Respostas e escolhas manuais ficam em jogos.json.
/// </summary>
public static partial class Jogos
{
    public sealed class Jogo
    {
        public string Nome { get; set; } = "";
        public int? Appid { get; set; }
        public DualSense.Modo Modo { get; set; }
        public string Origem { get; set; } = "padrao";   // steam | voce | padrao
    }

    public sealed class Dados
    {
        public List<string> PastasExtras { get; set; } = new();
        public DualSense.Modo Padrao { get; set; } = DualSense.Modo.Ps4;
        /// <summary>Chave: pasta do jogo em minúsculas.</summary>
        public Dictionary<string, Jogo> Jogos { get; set; } = new();
    }

    public static Dados Db { get; private set; } = new();
    private static readonly object Gate = new();
    private static string File_ => Path.Combine(AppPaths.Dir, "jogos.json");
    public static string FilePath => File_;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    // jogos abertos agora: pid -> pasta. Vale o último que abriu
    private static readonly List<(int pid, string key)> Rodando = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };

    // executáveis que moram na pasta do jogo mas não são o jogo
    private static readonly string[] Ignorar =
    {
        "launcher", "crash", "anticheat", "easyanticheat", "beservice", "battleye", "unins", "setup", "redist",
        "vcredist", "vc_redist", "dxsetup", "dotnet", "ubisoftconnect", "upc", "eac", "report", "updater", "helper",
    };

    private static readonly string[] DllsDeControle = { "xinput1_4.dll", "xinput1_3.dll", "xinput9_1_0.dll", "dinput8.dll", "gameinput.dll" };

    public static void Start()
    {
        Load();
        new Thread(Watch) { IsBackground = true, Name = "jogos" }.Start();
    }

    private static void Load()
    {
        try { if (File.Exists(File_)) Db = JsonSerializer.Deserialize<Dados>(File.ReadAllText(File_), Json) ?? new(); }
        catch (Exception e) { Log.Write($"jogos.json inválido, começando do zero: {e.Message}"); }
    }

    private static void Save()
    {
        lock (Gate)
        {
            try { File.WriteAllText(File_, JsonSerializer.Serialize(Db, Json)); }
            catch (Exception e) { Log.Write($"não consegui salvar jogos.json: {e.Message}"); }
        }
    }

    // ---------------- estado para o DualSense e a interface ----------------
    public static (string key, Jogo jogo)? Atual
    {
        get
        {
            lock (Gate)
            {
                for (int i = Rodando.Count - 1; i >= 0; i--)
                    if (Db.Jogos.TryGetValue(Rodando[i].key, out var j)) return (Rodando[i].key, j);
                return null;
            }
        }
    }

    /// <summary>Você escolheu um modo com o jogo aberto: vale para ele daqui em diante.</summary>
    public static void Lembrar(DualSense.Modo m)
    {
        if (Atual is not { } a) return;
        lock (Gate) { a.jogo.Modo = m; a.jogo.Origem = "voce"; }
        Save();
        Log.Write($"jogos: {a.jogo.Nome} → {DualSense.Label(m)} (escolha sua)");
    }

    /// <summary>Apaga a escolha/resposta do jogo aberto; na próxima vez ele consulta a Steam de novo.</summary>
    public static void Esquecer()
    {
        if (Atual is not { } a) return;
        lock (Gate) Db.Jogos.Remove(a.key);
        Save();
        Task.Run(() => Classify(a.key, a.jogo.Nome, null, notify: true));
    }

    public static string OrigemLabel(string o) => o switch { "steam" => "pela Steam", "voce" => "escolha sua", _ => "padrão" };

    // ---------------- vigia de processos ----------------
    private static void Watch()
    {
        while (true)
        {
            try
            {
                // sem admin, o WMI verifica a cada 1 s por dentro; o rastreio do kernel exigiria rodar como administrador
                using var start = new ManagementEventWatcher(new WqlEventQuery(
                    "SELECT * FROM __InstanceCreationEvent WITHIN 1 WHERE TargetInstance ISA 'Win32_Process'"));
                using var stop = new ManagementEventWatcher(new WqlEventQuery(
                    "SELECT * FROM __InstanceDeletionEvent WITHIN 1 WHERE TargetInstance ISA 'Win32_Process'"));
                start.EventArrived += (_, e) =>
                {
                    var p = (ManagementBaseObject)e.NewEvent["TargetInstance"];
                    int pid = Convert.ToInt32(p["ProcessId"]);
                    if (p["ExecutablePath"] is string path) Task.Run(() => OnStart(pid, path));
                };
                stop.EventArrived += (_, e) =>
                {
                    var p = (ManagementBaseObject)e.NewEvent["TargetInstance"];
                    OnStop(Convert.ToInt32(p["ProcessId"]));
                };
                start.Start();
                stop.Start();
                Log.Write("jogos: vigiando processos");
                Thread.Sleep(Timeout.Infinite);
            }
            catch (Exception e) { Log.Write($"jogos: vigia caiu, tentando de novo: {e.Message}"); Thread.Sleep(10000); }
        }
    }

    private static void OnStart(int pid, string exe)
    {
        try
        {
            string name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
            if (Ignorar.Any(name.Contains)) return;
            var (key, nome, appid) = Identify(exe);
            if (key == null)
            {
                // último recurso: só sabemos que é jogo se ele carregar uma biblioteca de controle
                Thread.Sleep(4000);
                if (!LoadsControllerDll(pid)) return;
                key = Path.GetDirectoryName(exe)!.ToLowerInvariant();
                nome = GameName(exe);
                Log.Write($"jogos: {nome} reconhecido por carregar biblioteca de controle");
            }
            lock (Gate)
            {
                if (Rodando.Any(r => r.key == key)) { Rodando.Add((pid, key)); return; }   // segundo .exe do mesmo jogo
                Rodando.Add((pid, key));
            }
            bool known;
            lock (Gate) known = Db.Jogos.ContainsKey(key);
            if (!known) Classify(key, nome!, appid, notify: true);
            else Notify(key);
            DualSense.Reapply();
        }
        catch (Exception e) { Log.Write($"jogos: erro ao analisar {exe}: {e.Message}"); }
    }

    private static void OnStop(int pid)
    {
        bool changed;
        lock (Gate) changed = Rodando.RemoveAll(r => r.pid == pid) > 0;
        if (changed) DualSense.Reapply();   // sem jogo aberto volta para o nativo, ou para o jogo que ainda está aberto
    }

    private static void Notify(string key)
    {
        Jogo? j;
        lock (Gate) Db.Jogos.TryGetValue(key, out j);
        if (j == null) return;
        Log.Write($"jogos: {j.Nome} → {DualSense.Label(j.Modo)} ({OrigemLabel(j.Origem)})");
        Ui.Run(() => App.Tray?.Balloon("Controle", $"{j.Nome} → {DualSense.Label(j.Modo)} ({OrigemLabel(j.Origem)})"));
        Bus.Notify();
    }

    // ---------------- é jogo? qual? ----------------
    /// <summary>Pasta do jogo (chave), nome e appid da Steam quando dá para saber pelo caminho; key null = não é jogo conhecido.</summary>
    private static (string? key, string? nome, int? appid) Identify(string exe)
    {
        string lower = exe.ToLowerInvariant();

        // Steam: steamapps\common\<pasta>, appid pelo appmanifest
        var m = SteamCommon().Match(exe);
        if (m.Success)
        {
            string steamapps = m.Groups[1].Value, dir = m.Groups[2].Value;
            int? appid = null; string nome = dir;
            foreach (var acf in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
            {
                string txt = File.ReadAllText(acf);
                if (!string.Equals(VdfValue(txt, "installdir"), dir, StringComparison.OrdinalIgnoreCase)) continue;
                if (int.TryParse(VdfValue(txt, "appid"), out var id)) appid = id;
                nome = VdfValue(txt, "name") ?? dir;
                break;
            }
            return (Path.Combine(steamapps, "common", dir).ToLowerInvariant(), nome, appid);
        }

        // pastas de outras lojas e as suas (pastas_extras): a pasta logo abaixo vira o jogo
        var roots = new List<string>
        {
            @"C:\Program Files\Epic Games", @"C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher\games",
            @"C:\Program Files (x86)\GOG Galaxy\Games", @"C:\GOG Games", @"C:\Program Files\EA Games",
        };
        lock (Gate) roots.AddRange(Db.PastasExtras);
        foreach (var root in roots)
        {
            string r = root.TrimEnd('\\').ToLowerInvariant() + "\\";
            if (!lower.StartsWith(r)) continue;
            string dir = exe[r.Length..].Split('\\')[0];
            if (dir.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;   // .exe solto na raiz
            return ((r + dir).ToLowerInvariant(), dir, null);
        }

        // o Windows já conhece como jogo (Game Bar / Modo de jogo)
        if (WindowsGames().Contains(lower))
            return (Path.GetDirectoryName(exe)!.ToLowerInvariant(), GameName(exe), null);

        return (null, null, null);
    }

    private static string GameName(string exe)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(exe).ProductName?.Trim();
            if (!string.IsNullOrEmpty(v)) return v;
        }
        catch { }
        return Path.GetFileName(Path.GetDirectoryName(exe)) ?? Path.GetFileNameWithoutExtension(exe);
    }

    private static HashSet<string>? _winGames;
    private static DateTime _winGamesAt;

    // HKCU\System\GameConfigStore\Children\*\MatchedExeFullPath: não é documentado, por isso é só um sinal a mais
    private static HashSet<string> WindowsGames()
    {
        if (_winGames != null && DateTime.Now - _winGamesAt < TimeSpan.FromMinutes(1)) return _winGames;
        var set = new HashSet<string>();
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore\Children");
            foreach (var sub in k?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                using var c = k!.OpenSubKey(sub);
                if (c?.GetValue("MatchedExeFullPath") is string p) set.Add(p.ToLowerInvariant());
            }
        }
        catch (Exception e) { Log.Write($"jogos: não li a lista de jogos do Windows: {e.Message}"); }
        _winGamesAt = DateTime.Now;
        return _winGames = set;
    }

    private static bool LoadsControllerDll(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.Modules.Cast<ProcessModule>().Any(mod => DllsDeControle.Contains(mod.ModuleName.ToLowerInvariant()));
        }
        catch { return false; }   // já fechou, ou é protegido / 32 bits
    }

    // ---------------- Steam: suporta DualSense? ----------------
    private static void Classify(string key, string nome, int? appid, bool notify)
    {
        var jogo = new Jogo { Nome = nome, Modo = Db.Padrao, Origem = "padrao" };
        try
        {
            appid ??= SearchSteam(nome);
            if (appid is { } id)
            {
                var root = JsonNode.Parse(Http.GetStringAsync(
                    $"https://store.steampowered.com/api/appdetails?appids={id}&filters=categories").Result);
                var cats = root?[id.ToString()]?["data"]?["categories"]?.AsArray()
                           .Select(c => (int?)c?["id"] ?? 0).ToHashSet() ?? new();
                // 57/58: DualSense Controller Support. Sem isso, PS4 (mostra ícones PlayStation onde houver DualShock)
                jogo.Appid = id;
                jogo.Modo = cats.Contains(57) || cats.Contains(58) ? DualSense.Modo.DualSense : DualSense.Modo.Ps4;
                jogo.Origem = "steam";
            }
        }
        catch (Exception e) { Log.Write($"jogos: consulta à Steam falhou para {nome}: {e.Message}"); }
        lock (Gate) Db.Jogos[key] = jogo;
        Save();
        if (notify) Notify(key);
        DualSense.Reapply();
    }

    // jogos fora da Steam: busca pelo nome; aceita só nome igual ou que contém o outro, para não pegar o jogo errado
    private static int? SearchSteam(string nome)
    {
        var root = JsonNode.Parse(Http.GetStringAsync(
            $"https://store.steampowered.com/api/storesearch?term={Uri.EscapeDataString(nome)}&l=english&cc=US").Result);
        string want = Norm(nome);
        if (want.Length < 3) return null;
        var items = root?["items"]?.AsArray() ?? new JsonArray();
        foreach (var it in items)
        {
            string got = Norm((string?)it?["name"] ?? "");
            if (got == want || got.StartsWith(want) || want.StartsWith(got)) return (int?)it?["id"];
        }
        return null;
    }

    private static string Norm(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static string? VdfValue(string txt, string key)
    {
        var m = Regex.Match(txt, $"\"{key}\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Replace(@"\\", @"\") : null;
    }

    [GeneratedRegex(@"^(.*\\steamapps)\\common\\([^\\]+)\\", RegexOptions.IgnoreCase)]
    private static partial Regex SteamCommon();
}
