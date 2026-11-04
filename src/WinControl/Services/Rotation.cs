using System.Runtime.InteropServices;
using System.Text;

namespace WinControl.Services;

/// <summary>
/// Gira o monitor-alvo entre retrato e paisagem (CCD / SetDisplayConfig). Porta do toggle_display.py.
/// O alvo é escolhido nas configurações pelo ID do EDID, nunca por posição ou ordem: com 3 telas isso muda e já girou o
/// monitor errado. Com uma tela só, não há o que escolher.
/// </summary>
public static class Rotation
{
    public sealed record Tela(string Id, string Nome);

    private const uint QdcOnlyActivePaths = 2;
    private const uint SdcApply = 0x80, SdcUseSupplied = 0x20, SdcAllowChanges = 0x400, SdcSaveToDatabase = 0x200;
    private const int RotIdentity = 1, RotRotate90 = 2, RotRotate270 = 4;

    // DISPLAYCONFIG_PATH_INFO tem 72 bytes e DISPLAYCONFIG_MODE_INFO tem 64
    private const int PathSize = 72, ModeSize = 64;
    private const int PathSrcModeIdx = 12, PathTargetAdapter = 20, PathTargetId = 28, PathRotation = 40;
    private const int ModeInfoType = 0, ModeSrcWidth = 16, ModeSrcHeight = 20, ModeSrcX = 28, ModeSrcY = 32;

    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint f, out uint np, out uint nm);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint f, ref uint np, IntPtr paths, ref uint nm, IntPtr modes, IntPtr topo);
    [DllImport("user32.dll")] private static extern int SetDisplayConfig(uint np, IntPtr paths, uint nm, IntPtr modes, uint flags);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(IntPtr request);

    /// <summary>Os monitores ativos agora, para a tela de configurações.</summary>
    public static List<Tela> Listar()
    {
        var telas = new List<Tela>();
        if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out uint np, out uint nm) != 0 || np < 1) return telas;
        IntPtr paths = Marshal.AllocHGlobal((int)np * PathSize), modes = Marshal.AllocHGlobal((int)nm * ModeSize);
        try
        {
            if (QueryDisplayConfig(QdcOnlyActivePaths, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return telas;
            for (int i = 0; i < np; i++)
            {
                var (caminho, nome) = TargetDevice(paths, i);
                string id = IdDoEdid(caminho);
                if (id.Length > 0 && telas.All(t => t.Id != id)) telas.Add(new(id, nome.Length > 0 ? nome : id));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(paths);
            Marshal.FreeHGlobal(modes);
        }
        return telas;
    }

    // \\?\DISPLAY#GSM5B55#...: o segundo pedaço identifica o modelo
    private static string IdDoEdid(string caminho) =>
        caminho.Split('#') is { Length: > 1 } p ? p[1].ToUpperInvariant() : "";

    private static (string caminho, string nome) TargetDevice(IntPtr paths, int index)
    {
        const int size = 420;       // DISPLAYCONFIG_TARGET_DEVICE_NAME
        IntPtr req = Marshal.AllocHGlobal(size);
        try
        {
            for (int i = 0; i < size; i++) Marshal.WriteByte(req, i, 0);
            Marshal.WriteInt32(req, 0, 2);          // GET_TARGET_NAME
            Marshal.WriteInt32(req, 4, size);
            Marshal.WriteInt64(req, 8, Marshal.ReadInt64(paths, index * PathSize + PathTargetAdapter));
            Marshal.WriteInt32(req, 16, Marshal.ReadInt32(paths, index * PathSize + PathTargetId));
            if (DisplayConfigGetDeviceInfo(req) != 0) return ("", "");
            return (Marshal.PtrToStringUni(req + 164, 128)?.Split('\0')[0] ?? "",     // monitorDevicePath
                    Marshal.PtrToStringUni(req + 36, 64)?.Split('\0')[0] ?? "");      // monitorFriendlyDeviceName
        }
        finally { Marshal.FreeHGlobal(req); }
    }

    public static (bool ok, string desc) Toggle(bool dryRun = false)
    {
        if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out uint np, out uint nm) != 0 || np < 1)
            return (false, "Nenhum monitor encontrado.");

        IntPtr paths = Marshal.AllocHGlobal((int)np * PathSize);
        IntPtr modes = Marshal.AllocHGlobal((int)nm * ModeSize);
        try
        {
            if (QueryDisplayConfig(QdcOnlyActivePaths, ref np, paths, ref nm, modes, IntPtr.Zero) != 0)
                return (false, "Erro ao consultar a configuracao dos monitores.");

            string alvo = Conforto.Texto("monitor_girar").ToUpperInvariant();
            int idx = -1;
            if (alvo.Length == 0)
            {
                if (np > 1) return (false, "Escolha qual monitor girar em Configuracoes > Aparelhos.");
                idx = 0;
            }
            for (int i = 0; i < np && idx < 0; i++)
                if (IdDoEdid(TargetDevice(paths, i).caminho) == alvo) idx = i;
            if (idx < 0)   // não chuta outro monitor: melhor falhar do que girar a tela errada
                return (false, "O monitor escolhido nao esta ativo. Troque em Configuracoes > Aparelhos.");

            int modeIdx = Marshal.ReadInt32(paths, idx * PathSize + PathSrcModeIdx);
            if (modeIdx < 0 || modeIdx >= nm || Marshal.ReadInt32(modes, modeIdx * ModeSize + ModeInfoType) != 1)
                return (false, "Modo de origem do monitor nao encontrado.");
            int mo = modeIdx * ModeSize;

            int w = Marshal.ReadInt32(modes, mo + ModeSrcWidth), h = Marshal.ReadInt32(modes, mo + ModeSrcHeight);
            int x = Marshal.ReadInt32(modes, mo + ModeSrcX), y = Marshal.ReadInt32(modes, mo + ModeSrcY);
            int rot = Marshal.ReadInt32(paths, idx * PathSize + PathRotation);

            // O painel é paisagem; o CCD pode informar largura/altura com ou sem a rotação aplicada.
            int nativeW = Math.Max(w, h), nativeH = Math.Min(w, h);
            bool wasPortrait = rot is RotRotate90 or RotRotate270;
            (int curW, int curH) = wasPortrait ? (nativeH, nativeW) : (nativeW, nativeH);
            (int targetRot, int newW, int newH, string desc) = wasPortrait
                ? (RotIdentity, nativeW, nativeH, "Paisagem (Horizontal)")
                : (Conforto.Inteiro("girar_sentido", 90) == 270 ? RotRotate270 : RotRotate90, nativeH, nativeW, "Retrato (Vertical)");

            // Mantém a borda direita (colada nos vizinhos) e o centro vertical onde estavam.
            int newX = x + curW - newW;
            int newY = (int)Math.Round(y + curH / 2.0 - newH / 2.0);
            Marshal.WriteInt32(modes, mo + ModeSrcX, newX);
            Marshal.WriteInt32(modes, mo + ModeSrcY, newY);
            Marshal.WriteInt32(paths, idx * PathSize + PathRotation, targetRot);

            if (dryRun) return (true, $"[teste] -> {desc}, pos=({newX},{newY})");

            int res = SetDisplayConfig(np, paths, nm, modes, SdcApply | SdcUseSupplied | SdcAllowChanges | SdcSaveToDatabase);
            if (res != 0) return (false, $"Falha ao aplicar configuracao: codigo {res}");
            Log.Write($"Monitor girado: {desc}");
            return (true, desc);
        }
        finally
        {
            Marshal.FreeHGlobal(paths);
            Marshal.FreeHGlobal(modes);
        }
    }
}
