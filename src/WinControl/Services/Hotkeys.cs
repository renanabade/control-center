using System.Runtime.InteropServices;

namespace WinControl.Services;

/// <summary>
/// Atalhos globais, escolhidos nas configurações, numa thread própria. Usam um hook de teclado que não bloqueia a tecla:
/// outro programa (placa de vídeo, jogos) pode já ter registrado a mesma combinação, e o hook funciona mesmo assim.
/// </summary>
public static class Hotkeys
{
    public readonly record struct Atalho(bool Ctrl, bool Alt, bool Shift, bool Win, int Vk);

    private const int WhKeyboardLl = 13, WmKeyDown = 0x0100, WmSysKeyDown = 0x0104;
    private const int VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12, VkLWin = 0x5B, VkRWin = 0x5C;

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg { public IntPtr hwnd; public uint message; public UIntPtr wParam; public IntPtr lParam; public uint time; public int x, y; }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")] private static extern int GetMessageW(out Msg m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookExW(int id, HookProc cb, IntPtr mod, uint thread);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);

    private static HookProc? _proc;      // referência fixa: o coletor de lixo não pode soltar o delegate
    private static Action? _rotate, _theme;
    private static Atalho? _atalhoGirar, _atalhoTema;
    private static DateTime _ultimo = DateTime.MinValue;

    /// <summary>A tela está gravando um atalho novo: apertar a combinação atual não pode disparar a ação.</summary>
    public static volatile bool Pausado;

    // ---------------- texto <-> atalho ----------------
    /// <summary>"Ctrl+Alt+R" vira o atalho. Sem Ctrl, Alt, Shift ou Win, não vale: uma tecla sozinha dispararia ao digitar.</summary>
    public static Atalho? Ler(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return null;
        bool ctrl = false, alt = false, shift = false, win = false;
        int vk = 0;
        foreach (var parte in texto.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (parte.ToUpperInvariant())
            {
                case "CTRL": ctrl = true; break;
                case "ALT": alt = true; break;
                case "SHIFT": shift = true; break;
                case "WIN": win = true; break;
                default: vk = TeclaDe(parte); break;
            }
        }
        return vk != 0 && (ctrl || alt || shift || win) ? new Atalho(ctrl, alt, shift, win, vk) : null;
    }

    private static int TeclaDe(string nome)
    {
        string n = nome.ToUpperInvariant();
        if (n.Length == 1 && n[0] is (>= 'A' and <= 'Z') or (>= '0' and <= '9')) return n[0];
        if (n[0] == 'F' && int.TryParse(n[1..], out int f) && f is >= 1 and <= 24) return 0x70 + f - 1;
        if (n.StartsWith("VK") && int.TryParse(n[2..], System.Globalization.NumberStyles.HexNumber, null, out int vk)) return vk;
        return 0;
    }

    public static string Texto(Atalho a)
    {
        var p = new List<string>();
        if (a.Win) p.Add("Win");
        if (a.Ctrl) p.Add("Ctrl");
        if (a.Alt) p.Add("Alt");
        if (a.Shift) p.Add("Shift");
        p.Add(a.Vk is (>= 'A' and <= 'Z') or (>= '0' and <= '9') ? ((char)a.Vk).ToString()
            : a.Vk is >= 0x70 and <= 0x87 ? $"F{a.Vk - 0x70 + 1}" : $"VK{a.Vk:X2}");
        return string.Join("+", p);
    }

    /// <summary>Lê as escolhas de novo. Chamado sempre que o conforto.json muda, pela tela ou à mão.</summary>
    public static void Recarregar()
    {
        _atalhoGirar = Ler(Conforto.Texto("atalho_girar"));
        _atalhoTema = Ler(Conforto.Texto("atalho_tema"));
    }

    /// <summary>Para mostrar no menu e nas dicas: " (Ctrl+Alt+R)", ou nada se o atalho estiver desligado.</summary>
    public static string Sufixo(string chave) => Ler(Conforto.Texto(chave)) is { } a ? $" ({Texto(a)})" : "";

    // ---------------- hook ----------------
    private static bool Apertada(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static bool Bate(Atalho? a, int vk) => a is { } x && x.Vk == vk
        && x.Ctrl == Apertada(VkControl) && x.Alt == Apertada(VkMenu) && x.Shift == Apertada(VkShift)
        && x.Win == (Apertada(VkLWin) || Apertada(VkRWin));

    private static IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && !Pausado && ((int)wParam == WmKeyDown || (int)wParam == WmSysKeyDown))
        {
            int vk = Marshal.ReadInt32(lParam);
            var acao = Bate(_atalhoGirar, vk) ? _rotate : Bate(_atalhoTema, vk) ? _theme : null;
            // segurar a tecla repete o aviso: vale um disparo só
            if (acao != null && (DateTime.UtcNow - _ultimo).TotalMilliseconds > 700)
            {
                _ultimo = DateTime.UtcNow;
                Task.Run(() => { try { acao(); } catch (Exception e) { Log.Write($"erro no atalho: {e.Message}"); } });
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    public static void Start(Action toggleTheme, Action rotate)
    {
        _rotate = rotate;
        _theme = toggleTheme;
        Recarregar();
        var thread = new Thread(() =>
        {
            _proc = OnKey;
            if (SetWindowsHookExW(WhKeyboardLl, _proc, GetModuleHandleW(null), 0) == IntPtr.Zero)
                Log.Write("Nao foi possivel instalar os atalhos de teclado");
            while (GetMessageW(out _, IntPtr.Zero, 0, 0) > 0) { }
        }) { IsBackground = true, Name = "atalhos" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
}
