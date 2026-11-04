using Microsoft.Win32;

namespace WinControl.Services;

/// <summary>
/// O que o tray.py fazia por trás da interface: envio coalescido para a lâmpada, atualização periódica,
/// Luz Noturna, tema automático e início com o Windows.
/// </summary>
public static class Core
{
    private static readonly Dictionary<Lampada, string> Pendentes = [];
    private static readonly AutoResetEvent SendEvent = new(false);
    private static readonly AutoResetEvent Poke = new(false);
    private static DateTime _userAt = DateTime.MinValue;

    /// <summary>O painel está aberto: a lâmpada é lida a cada 3 s em vez de 30 s.</summary>
    public static volatile bool PanelOpen;

    public static void PokePoller() => Poke.Set();

    /// <summary>O usuário mexeu em algo: pausa o poller um pouco e tira o destaque da cena.</summary>
    public static void Touch()
    {
        _userAt = DateTime.UtcNow;
        Conforto.Cena = null;
    }

    // ---------------- lâmpada: envio ----------------
    private static void Send(string kind, IEnumerable<Lampada> alvos)
    {
        Touch();
        lock (Pendentes) foreach (var l in alvos) Pendentes[l] = kind;
        SendEvent.Set();
    }

    private static void SendWorker()
    {
        while (true)
        {
            SendEvent.WaitOne();
            try
            {
                KeyValuePair<Lampada, string>[] fila;
                lock (Pendentes)
                {
                    fila = Pendentes.ToArray();
                    Pendentes.Clear();
                }
                Parallel.ForEach(fila, p =>
                {
                    var c = p.Key.Cur;
                    if (p.Value == "c" || (p.Value == "b" && c.Mode == "colour")) p.Key.ApplyColour(c.H, c.S, c.B);
                    else p.Key.ApplyWhite(c.B, c.T);
                });
                if (fila.Any(p => !p.Key.Cur.Online)) Bus.Notify();
            }
            catch (Exception e) { Log.Write($"erro ao enviar para a lampada: {e.Message}"); }
        }
    }

    /// <summary>Uma lâmpada, ou todas as daqui quando nenhuma é indicada.</summary>
    private static IReadOnlyList<Lampada> Alvos(Lampada? alvo) => alvo != null ? [alvo] : Lamp.Aqui;

    public static void SetLampBrightness(int b, Lampada? alvo = null)
    {
        var a = Alvos(alvo);
        foreach (var l in a) l.Cur.B = Math.Clamp(b, 1, 100);
        Bus.Notify();
        Send("b", a);
    }

    public static void SetLampTemperature(int t, Lampada? alvo = null)
    {
        var a = Alvos(alvo);
        foreach (var l in a) { l.Cur.T = Math.Clamp(t, 0, 100); l.Cur.Mode = "white"; }
        Bus.Notify();
        Send("t", a);
    }

    public static void SetLampColour(int h, int s, Lampada? alvo = null)
    {
        var a = Alvos(alvo);
        foreach (var l in a) { l.Cur.H = ((h % 360) + 360) % 360; l.Cur.S = Math.Clamp(s, 0, 1000); l.Cur.Mode = "colour"; }
        Bus.Notify();
        Send("c", a);
    }

    public static void SetLampMode(bool colour, Lampada? alvo = null)
    {
        // trocar de aba só muda o que é mostrado; a lâmpada muda quando o usuário mexer no controle
        var a = Alvos(alvo);
        foreach (var l in a) l.Cur.Mode = colour ? "colour" : "white";
        Bus.Notify();
        Send(colour ? "c" : "t", a);
    }

    // ---------------- telas ----------------
    public static void SetScreenBrightness(int v, string? key = null)
    {
        v = Math.Clamp(v, 0, 100);
        if (key == null || Conforto.Flag("telas_juntas")) Screens.SetAll(v);
        else Screens.SetBrightness(key, v);
        Touch();
        Bus.Notify();
    }

    // ---------------- tema ----------------
    public static void SetDark(bool dark)
    {
        Task.Run(() => { Theme.SetDark(dark); Conforto.Cena = null; Bus.Notify(); });
    }

    public static void ToggleOption(string key)
    {
        Conforto.SetOption(key, !Conforto.Flag(key));
        if (key == "tema_automatico" && Conforto.Flag(key)) SetDark(Theme.NightLightOn());
    }

    // ---------------- início com o Windows ----------------
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool AutostartEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue("WinControl") != null;
    }

    public static void ToggleAutostart()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)!;
        if (AutostartEnabled()) k.DeleteValue("WinControl", false);
        else k.SetValue("WinControl", $"\"{Environment.ProcessPath}\" --background");
        Bus.Notify();
    }

    // ---------------- threads de fundo ----------------
    /// <summary>Luz Noturna mudou: tema automático e, ao ativar, lâmpada a 1% / mais quente.</summary>
    private static void NightWatcher()
    {
        // ao iniciar não mexe no tema: só segue quando a Luz Noturna muda (senão todo boot de dia virava tema claro)
        bool prev = Theme.NightLightOn();
        while (true)
        {
            // o Windows avisa quando a chave é regravada; sem ela, volta a olhar a cada 3 s
            using var watch = Theme.NightLightWatch.Arm();
            try
            {
                bool now = Theme.NightLightOn();
                if (now != prev)
                {
                    prev = now;     // antes da lâmpada: se ela falhar, não reage de novo à mesma mudança
                    bool byScene = (DateTime.UtcNow - Theme.NightLightSetAt).TotalSeconds < 15;
                    if (Conforto.Flag("tema_automatico") && !byScene) Theme.SetDark(now);
                    if (now && Lamp.RefreshState() && Lamp.Cur.On)
                    {
                        Lamp.ApplyWhite(1, 0);
                        Log.Write("Night Light ativou: lampada 1% / mais quente");
                    }
                    Bus.Notify();
                }
                prev = now;
            }
            catch (Exception e) { Log.Write($"erro no vigia da Luz Noturna: {e.Message}"); }
            if (watch == null) Thread.Sleep(3000);
            else watch.Wait(5 * 60_000);   // o limite é só rede de segurança
        }
    }

    private static string Snapshot() =>
        string.Join("|", Lamp.Aqui.Select(l => { var c = l.Cur; return $"{l.Id}:{c.On}:{c.Online}:{c.B}:{c.T}:{c.Mode}:{c.H}:{c.S}"; }));

    /// <summary>Mantém as lâmpadas daqui em dia: a cada 3 s com o painel aberto, a cada 30 s com ele fechado.</summary>
    private static void Poller()
    {
        Thread.Sleep(1000);
        while (true)
        {
            int wait;
            try
            {
                bool busy = (DateTime.UtcNow - _userAt).TotalSeconds < 2 || Lamp.ToggleBusy;
                if (!busy)
                {
                    var before = Snapshot();
                    Lamp.RefreshState();
                    Lamp.ProcurarSemIp();
                    if (PanelOpen || before != Snapshot()) Bus.Notify();
                }
                wait = busy ? 1 : PanelOpen ? (Lamp.Cur.Online ? 3 : 5) : 30;
            }
            catch (Exception e) { Log.Write($"erro no poller: {e.Message}"); wait = 3; }
            Poke.WaitOne(wait * 1000);
        }
    }

    public static void Start()
    {
        // primeira linha do log: de onde a config vem. Aberto de dentro de outro app empacotado (ex.: Claude), o Windows
        // pode redirecionar o %APPDATA% para uma cópia isolada, e aí nada do que está na pasta real vale
        Log.Write($"control-center {typeof(Core).Assembly.GetName().Version} | config em {AppPaths.Dir} | exe {Environment.ProcessPath}");
        Lamp.Load();
        Conforto.Start();
        DualSense.Start();
        Perifericos.Start();
        Screens.Start();
        new Thread(SendWorker) { IsBackground = true, Name = "envio-lampada" }.Start();
        new Thread(NightWatcher) { IsBackground = true, Name = "luz-noturna" }.Start();
        new Thread(Poller) { IsBackground = true, Name = "lampada-poller" }.Start();
        Hotkeys.Start(() => { Theme.Toggle(); Conforto.Cena = null; Bus.Notify(); }, () => RotateFromHotkey?.Invoke());
        if (ApiServico.Ativa) ApiBridge.Start();   // desligada, nem o pipe existe
        Log.Write("control-center iniciado");
    }

    /// <summary>A bandeja assina isto para mostrar o resultado do giro.</summary>
    public static Action? RotateFromHotkey;
}
