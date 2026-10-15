using System.Runtime.InteropServices;

namespace WinControl.Services;

/// <summary>
/// Os modos do Projetar do Windows (Win+P / Fn+F7). Usa a mesma chamada que o Windows usa,
/// então o próprio Win+P continua desfazendo qualquer coisa feita aqui.
/// </summary>
public static class DisplayProjection
{
    public enum Mode : uint
    {
        PcScreenOnly = 0x1,      // SDC_TOPOLOGY_INTERNAL
        Duplicate = 0x2,         // SDC_TOPOLOGY_CLONE
        Extend = 0x4,            // SDC_TOPOLOGY_EXTEND
        SecondScreenOnly = 0x8,  // SDC_TOPOLOGY_EXTERNAL
    }

    private const uint SDC_APPLY = 0x80;
    private const uint QDC_DATABASE_CURRENT = 0x4;

    // tamanhos das structs DISPLAYCONFIG_PATH_INFO e DISPLAYCONFIG_MODE_INFO
    private const int PathInfoSize = 72;
    private const int ModeInfoSize = 64;

    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(uint numPaths, IntPtr paths, uint numModes, IntPtr modes, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPaths, IntPtr paths, ref uint numModes,
                                                 IntPtr modes, out uint topologyId);

    public static bool Apply(Mode mode) => SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_APPLY | (uint)mode) == 0;

    /// <summary>Modo em uso agora, ou null se o Windows não informar.</summary>
    public static Mode? Current()
    {
        if (GetDisplayConfigBufferSizes(QDC_DATABASE_CURRENT, out uint np, out uint nm) != 0)
            return null;
        IntPtr paths = Marshal.AllocHGlobal((int)Math.Max(1, np) * PathInfoSize);
        IntPtr modes = Marshal.AllocHGlobal((int)Math.Max(1, nm) * ModeInfoSize);
        try
        {
            if (QueryDisplayConfig(QDC_DATABASE_CURRENT, ref np, paths, ref nm, modes, out uint topology) != 0)
                return null;
            return Enum.IsDefined(typeof(Mode), topology) ? (Mode)topology : null;
        }
        finally
        {
            Marshal.FreeHGlobal(paths);
            Marshal.FreeHGlobal(modes);
        }
    }

    public static string Label(Mode? mode) => mode switch
    {
        Mode.PcScreenOnly => "Só a tela do PC",
        Mode.Duplicate => "Duplicar",
        Mode.Extend => "Estender",
        Mode.SecondScreenOnly => "Só a segunda tela",
        _ => "Projetar",
    };
}
