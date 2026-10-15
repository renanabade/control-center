using System.Runtime.InteropServices;

namespace WinControl.Services;

/// <summary>
/// Atalhos globais numa thread própria. Win+Ctrl+Shift+D usa RegisterHotKey. Ctrl+Alt+R e Ctrl+Alt+O usam um hook
/// de teclado que não bloqueia a tecla, como o atalho antigo do monitor: outro programa (placa de vídeo, jogos) já
/// registra o Ctrl+Alt+R, e o hook funciona mesmo assim.
/// </summary>
public static class Hotkeys
{
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;
    private const int WhKeyboardLl = 13, WmKeyDown = 0x0100, WmSysKeyDown = 0x0104, VkControl = 0x11, VkMenu = 0x12;

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg { public IntPtr hwnd; public uint message; public UIntPtr wParam; public IntPtr lParam; public uint time; public int x, y; }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern int GetMessageW(out Msg m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookExW(int id, HookProc cb, IntPtr mod, uint thread);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);

    private static HookProc? _proc;      // referência fixa: o coletor de lixo não pode soltar o delegate
    private static Action? _rotate;
    private static DateTime _lastRotate = DateTime.MinValue;

    private static IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && ((int)wParam == WmKeyDown || (int)wParam == WmSysKeyDown))
        {
            int vk = Marshal.ReadInt32(lParam);
            if ((vk == 'R' || vk == 'O')
                && (GetAsyncKeyState(VkControl) & 0x8000) != 0 && (GetAsyncKeyState(VkMenu) & 0x8000) != 0
                && (DateTime.UtcNow - _lastRotate).TotalMilliseconds > 700)
            {
                _lastRotate = DateTime.UtcNow;
                var act = _rotate;
                if (act != null) Task.Run(() => { try { act(); } catch (Exception e) { Log.Write($"erro no atalho: {e.Message}"); } });
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    public static void Start(Action toggleTheme, Action rotate)
    {
        _rotate = rotate;
        var thread = new Thread(() =>
        {
            if (!RegisterHotKey(IntPtr.Zero, 1, ModWin | ModControl | ModShift | ModNoRepeat, 'D'))
                Log.Write("Atalho Win+Ctrl+Shift+D ja esta em uso (desative o Light Switch no PowerToys)");

            _proc = OnKey;
            if (SetWindowsHookExW(WhKeyboardLl, _proc, GetModuleHandleW(null), 0) == IntPtr.Zero)
                Log.Write("Nao foi possivel instalar o atalho Ctrl+Alt+R");

            while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == 0x0312 && (int)msg.wParam == 1)    // WM_HOTKEY
                    Task.Run(() => { try { toggleTheme(); } catch (Exception e) { Log.Write($"erro no atalho: {e.Message}"); } });
            }
        }) { IsBackground = true, Name = "atalhos" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
}
