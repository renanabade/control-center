using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinControl.Services;

/// <summary>Tema claro/escuro do Windows e Luz Noturna (mesma sequência do Light Switch do PowerToys).</summary>
public static class Theme
{
    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string NightLightKey =
        @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current" +
        @"\default$windows.data.bluelightreduction.bluelightreductionstate" +
        @"\windows.data.bluelightreduction.bluelightreductionstate";

    private static readonly object Gate = new();

    public static bool NightLightOn()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(NightLightKey);
            return k?.GetValue("Data") is byte[] d && d.Length > 24 && d[18] == 0x15;
        }
        catch { return false; }
    }

    /// <summary>
    /// Liga/desliga a Luz Noturna regravando o estado no CloudStore: byte 18 = 0x15 (ligada) ou 0x13, com o campo
    /// 0x10 0x00 inserido/removido na posição 23, e o carimbo de tempo (bytes 10-14) avançado para o Windows notar.
    /// </summary>
    /// <summary>Quando o app mudou a Luz Noturna (por uma cena): o tema automático não deve desfazer o tema da cena.</summary>
    public static DateTime NightLightSetAt = DateTime.MinValue;

    public static void SetNightLight(bool on)
    {
        if (NightLightOn() == on) return;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(NightLightKey, writable: true);
            if (k?.GetValue("Data") is not byte[] d || d.Length < 25) { Log.Write("Luz Noturna: estado nao encontrado no registro"); return; }
            byte[] n;
            if (on)
            {
                n = new byte[d.Length + 2];
                Array.Copy(d, n, 23);
                Array.Copy(d, 23, n, 25, d.Length - 23);
                n[18] = 0x15; n[23] = 0x10; n[24] = 0x00;
            }
            else
            {
                n = new byte[d.Length - 2];
                Array.Copy(d, n, 23);
                Array.Copy(d, 25, n, 23, d.Length - 25);
                n[18] = 0x13;
            }
            for (int i = 10; i < 15; i++)
                if (n[i] != 0xFF) { n[i]++; break; }
            NightLightSetAt = DateTime.UtcNow;
            k.SetValue("Data", n, RegistryValueKind.Binary);
            Log.Write($"Luz Noturna: {(on ? "ligada" : "desligada")}");
        }
        catch (Exception e) { Log.Write($"Luz Noturna: falha ao mudar ({e.Message})"); }
        Bus.Notify();
    }

    private static int? Read(string name)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Personalize);
            return k?.GetValue(name) as int?;
        }
        catch { return null; }
    }

    public static bool IsDark() => Read("AppsUseLightTheme") == 0;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg, UIntPtr wParam, string? lParam,
                                                     uint flags, uint timeout, out UIntPtr result);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam,
                                                     uint flags, uint timeout, out UIntPtr result);

    private static readonly IntPtr HwndBroadcast = (IntPtr)0xFFFF;
    private const uint WmSettingChange = 0x001A, WmThemeChanged = 0x031A, WmDwmColorizationChanged = 0x0320;
    private const uint SmtoAbortIfHung = 0x0002;

    private static void Broadcast(uint msg, string? text = null)
    {
        // 5 s por janela, como o PowerToys: com pouco tempo, apps mais lentos perdem o aviso
        if (text != null) SendMessageTimeoutW(HwndBroadcast, msg, UIntPtr.Zero, text, SmtoAbortIfHung, 5000, out _);
        else SendMessageTimeoutW(HwndBroadcast, msg, UIntPtr.Zero, IntPtr.Zero, SmtoAbortIfHung, 5000, out _);
    }

    private static void Write(string name, bool light)
    {
        using (var k = Registry.CurrentUser.OpenSubKey(Personalize, writable: true)!)
        {
            k.SetValue(name, light ? 1 : 0, RegistryValueKind.DWord);
            // cor de destaque na barra de tarefas não existe no tema claro: sem isso ela fica "meio a meio"
            if (name == "SystemUsesLightTheme" && light)
                k.SetValue("ColorPrevalence", 0, RegistryValueKind.DWord);
        }
        Broadcast(WmSettingChange, "ImmersiveColorSet");
        Broadcast(WmThemeChanged);
        if (name == "SystemUsesLightTheme" && light) Broadcast(WmDwmColorizationChanged);
    }

    public static void SetDark(bool dark)
    {
        bool light = !dark;
        bool changed = false;
        lock (Gate)
        {
            foreach (var name in new[] { "SystemUsesLightTheme", "AppsUseLightTheme" })  // sistema primeiro, como o PowerToys
            {
                if (Read(name) != (light ? 1 : 0))
                {
                    Write(name, light);
                    changed = true;
                }
            }
        }
        if (changed) Log.Write($"Tema: {(dark ? "escuro" : "claro")}");
        Bus.Notify();
    }

    public static void Toggle() => SetDark(!IsDark());
}
