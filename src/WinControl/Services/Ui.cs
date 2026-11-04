using Microsoft.UI.Dispatching;

namespace WinControl.Services;

/// <summary>Apoio da interface: fila da thread de UI e textos de status compartilhados.</summary>
public static class Ui
{
    public static DispatcherQueue? Queue { get; set; }

    public static void Run(Action a)
    {
        if (Queue == null) return;
        if (Queue.HasThreadAccess) a();
        else Queue.TryEnqueue(() => { try { a(); } catch (Exception e) { Log.Write($"erro na interface: {e.Message}"); } });
    }

    public static int Kelvin(int t) => 2700 + (6500 - 2700) * t / 100;

    public static string TempLabel(int t)
    {
        string n = t < 34 ? "Quente" : t < 67 ? "Neutra" : "Fria";
        return $"{n} · {Kelvin(t)}K";
    }

    /// <summary>Texto de todas as daqui. Com uma lâmpada só, é o texto dela.</summary>
    public static string LampStatus(bool withTimer = true)
    {
        if (Lamp.ConfigProblem is { } p) return p == "nenhuma lâmpada nesta rede" ? "Nenhuma nesta rede" : $"Não configurada ({p})";
        return LampStatus(Lamp.Aqui, withTimer);
    }

    /// <summary>Texto de um grupo de lâmpadas: as daqui, ou as marcadas no painel.</summary>
    public static string LampStatus(IReadOnlyList<Lampada> a, bool withTimer = true)
    {
        if (a.Count == 1) return LampStatus(a[0], withTimer);
        if (a.Count == 0) return "Escolha uma ou mais lâmpadas";
        if (Lamp.Resumo(a).Busy) return "Conectando…";
        int ligadas = a.Count(l => l.Cur.On && l.Cur.Online), online = a.Count(l => l.Cur.Online);
        if (online == 0) return a.Any(l => l.HasBle) ? "Dormindo (acordam pelo Bluetooth)" : "Sem conexão";
        return ligadas == 0 ? $"{a.Count} apagadas" : $"{ligadas} de {a.Count} ligadas";
    }

    public static string LampStatus(Lampada l, bool withTimer = true)
    {
        var c = l.Cur;
        if (c.Busy) return "Conectando…";
        if (l.ConfigProblem is { } p) return $"Não configurada ({p})";
        if (!c.Online) return l.HasBle ? "Dormindo (acorda pelo Bluetooth)" : "Sem conexão";
        if (!c.On) return "Desligada";
        string s = $"Ligada · {c.B}% · " + (c.Mode == "colour" ? "cor" : $"{Kelvin(c.T)}K");
        int left = l.TimerLeft();
        if (withTimer && left > 0) s += $" · desliga em {Lamp.FmtLeft(left)}";
        return s;
    }

    /// <summary>Matiz 0-359, saturação 0-1 (valor sempre 1: o brilho é outro controle) -> cor RGB.</summary>
    public static Windows.UI.Color HsToColor(double h, double s)
    {
        double c = s, hp = h / 60.0, x = c * (1 - Math.Abs(hp % 2 - 1)), m = 1 - c;
        (double r, double g, double b) = (int)hp switch
        {
            0 => (c, x, 0.0), 1 => (x, c, 0.0), 2 => (0.0, c, x),
            3 => (0.0, x, c), 4 => (x, 0.0, c), _ => (c, 0.0, x),
        };
        return Windows.UI.Color.FromArgb(255, (byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    public static (int h, int s) ColorToHs(Windows.UI.Color col)
    {
        double r = col.R / 255.0, g = col.G / 255.0, b = col.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        double s = max == 0 ? 0 : d / max;
        return ((int)Math.Round(h) % 360, (int)Math.Round(s * 1000));
    }

    public static string SceneGlyph(string name) => name switch
    {
        "Trabalho" => "",
        "Noite" => "",
        "Filme" => "",
        _ => "",
    };
}
