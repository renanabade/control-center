using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinControl.Services;

/// <summary>
/// Brilho dos monitores (substitui o Twinkle Tray). Externos: DDC/CI (VCP 0x10) pela dxva2.dll.
/// Tela do notebook: WMI. Toda a comunicação roda numa única thread (DDC é lento e o WMI quer a mesma thread),
/// então quem chama nunca trava: SetBrightness só agenda, Refresh só pede leitura. Porta do telas.py.
/// </summary>
public static class Screens
{
    public sealed class Monitor
    {
        public string Key = "", Name = "", Kind = "ddc";   // Kind: "ddc" | "wmi"
        public IntPtr Handle;
        public string? WmiInst;
        public int X;
        public int? Value;           // brilho atual 0-100 (null = desconhecido)
        public uint Max = 100;
        public bool Ok = true;       // false se o monitor não respondeu ao DDC/CI
    }

    // ---------------- Win32 ----------------
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int L, T, R, B; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public uint cbSize; public Rect rcMonitor, rcWork; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public uint cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    private delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, ref Rect r, IntPtr data);

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(IntPtr hMon, ref MonitorInfoEx mi);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevicesW(string? dev, uint i, ref DisplayDevice dd, uint flags);
    [DllImport("dxva2.dll")] private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr h, out uint n);
    [DllImport("dxva2.dll")] private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr h, uint n, IntPtr arr);
    [DllImport("dxva2.dll")] private static extern bool DestroyPhysicalMonitor(IntPtr h);
    [DllImport("dxva2.dll")] private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr h, byte code, IntPtr type, out uint cur, out uint max);
    [DllImport("dxva2.dll")] private static extern bool SetVCPFeature(IntPtr h, byte code, uint value);

    private const byte VcpBrightness = 0x10;
    private const int PhysicalMonitorSize = 8 + 256;

    // ---------------- estado ----------------
    private static List<Monitor> _monitors = new();
    private static readonly Dictionary<string, int> Pending = new();
    private static readonly object Gate = new();
    private static readonly AutoResetEvent Wake = new(false);
    private static volatile bool _wantRefresh;
    private static readonly ManualResetEventSlim Ready = new(false);
    private static readonly List<object> Deferred = new();
    private static readonly HashSet<string> Warned = new();

    public static IReadOnlyList<Monitor> Monitors { get { lock (Gate) return _monitors.ToList(); } }

    public static void Refresh() { _wantRefresh = true; Wake.Set(); }

    public static void SetBrightness(string key, int v)
    {
        v = Math.Clamp(v, 0, 100);
        lock (Gate)
        {
            foreach (var m in _monitors) if (m.Key == key) m.Value = v;
            Pending[key] = v;
        }
        Wake.Set();
    }

    public static void SetAll(int v)
    {
        foreach (var m in Monitors) if (m.Ok) SetBrightness(m.Key, v);
    }

    /// <summary>values: número (todas as telas) ou {nome: brilho}.</summary>
    public static void SetByName(object values)
    {
        if (!Ready.IsSet) { lock (Gate) Deferred.Add(values); return; }   // ainda detectando as telas: aplica depois
        if (values is IDictionary<string, int> map)
        {
            foreach (var m in Monitors) if (map.TryGetValue(m.Name, out int v)) SetBrightness(m.Key, v);
        }
        else if (values is int all) SetAll(all);
    }

    public static int? Average()
    {
        var vals = Monitors.Where(m => m.Ok && m.Value != null).Select(m => m.Value!.Value).ToList();
        return vals.Count == 0 ? null : (int)Math.Round(vals.Average());
    }

    // ---------------- enumeração ----------------
    private static string? EdidName(string hwid, string inst)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{hwid}\{inst}\Device Parameters");
            if (k?.GetValue("EDID") is not byte[] edid) return null;
            foreach (int off in new[] { 54, 72, 90, 108 })
            {
                if (edid.Length >= off + 18 && edid[off] == 0 && edid[off + 1] == 0 && edid[off + 2] == 0 && edid[off + 3] == 0xFC)
                {
                    var name = System.Text.Encoding.ASCII.GetString(edid, off + 5, 13).Split('\n')[0].Trim();
                    return name.Length > 0 ? name : null;
                }
            }
        }
        catch { }
        return null;
    }

    private static Dictionary<string, ManagementObject> WmiInstances()
    {
        var d = new Dictionary<string, ManagementObject>();
        try
        {
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightness");
            foreach (ManagementObject o in s.Get()) d[(string)o["InstanceName"]] = o;
        }
        catch { }
        return d;
    }

    private static List<Monitor> Enumerate()
    {
        var hmons = new List<IntPtr>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr dc, ref Rect r, IntPtr l) => { hmons.Add(h); return true; }, IntPtr.Zero);
        var wmi = WmiInstances();
        var found = new List<Monitor>();
        foreach (var hm in hmons)
        {
            var mi = new MonitorInfoEx { cbSize = (uint)Marshal.SizeOf<MonitorInfoEx>() };
            GetMonitorInfoW(hm, ref mi);
            var dd = new DisplayDevice { cb = (uint)Marshal.SizeOf<DisplayDevice>() };
            string? hwid = null, inst = null;
            if (EnumDisplayDevicesW(mi.szDevice, 0, ref dd, 1))   // EDD_GET_DEVICE_INTERFACE_NAME
            {
                var parts = dd.DeviceID.Split('#');                // \\?\DISPLAY#GSM5B7F#5&abc&0&UID4352#{guid}
                if (parts.Length >= 3) { hwid = parts[1]; inst = parts[2]; }
            }
            string key = hwid != null ? $@"{hwid}\{inst}" : mi.szDevice;
            string? name = hwid != null ? EdidName(hwid, inst!) : null;
            string? wmiInst = hwid == null ? null
                : wmi.Keys.FirstOrDefault(w => w.StartsWith($@"DISPLAY\{hwid}\{inst}", StringComparison.OrdinalIgnoreCase));

            var handles = new List<IntPtr>();
            if (GetNumberOfPhysicalMonitorsFromHMONITOR(hm, out uint n) && n > 0)
            {
                IntPtr arr = Marshal.AllocHGlobal((int)n * PhysicalMonitorSize);
                try
                {
                    if (GetPhysicalMonitorsFromHMONITOR(hm, n, arr))
                        for (int i = 0; i < n; i++) handles.Add(Marshal.ReadIntPtr(arr, i * PhysicalMonitorSize));
                }
                finally { Marshal.FreeHGlobal(arr); }
            }

            if (wmiInst != null)    // tela interna: usa WMI e solta os handles DDC
            {
                foreach (var h in handles) DestroyPhysicalMonitor(h);
                found.Add(new Monitor { Key = key, Name = name ?? "Notebook", Kind = "wmi", WmiInst = wmiInst, X = mi.rcMonitor.L });
            }
            else if (handles.Count > 0)
            {
                foreach (var h in handles.Skip(1)) DestroyPhysicalMonitor(h);
                found.Add(new Monitor { Key = key, Name = name ?? "Monitor", Kind = "ddc", Handle = handles[0], X = mi.rcMonitor.L });
            }
        }
        found.Sort((a, b) => a.X.CompareTo(b.X));
        // nomes repetidos (dois monitores iguais) ganham número
        var seen = new Dictionary<string, int>();
        foreach (var m in found)
        {
            seen[m.Name] = seen.GetValueOrDefault(m.Name) + 1;
            if (seen[m.Name] > 1) m.Name = $"{m.Name} {seen[m.Name]}";
        }
        return found;
    }

    private static int? Read(Monitor m)
    {
        if (m.Kind == "wmi")
        {
            var inst = WmiInstances().GetValueOrDefault(m.WmiInst ?? "");
            return inst == null ? null : Convert.ToInt32(inst["CurrentBrightness"]);
        }
        for (int i = 0; i < 3; i++)   // DDC/CI falha de vez em quando; tentar de novo costuma resolver
        {
            if (GetVCPFeatureAndVCPFeatureReply(m.Handle, VcpBrightness, IntPtr.Zero, out uint cur, out uint max))
            {
                m.Max = max == 0 ? 100 : max;
                return (int)Math.Round(cur * 100.0 / m.Max);
            }
            Thread.Sleep(50);
        }
        return null;
    }

    private static bool Write(Monitor m, int v)
    {
        if (m.Kind == "wmi")
        {
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject o in s.Get())
            {
                if ((string)o["InstanceName"] != m.WmiInst) continue;
                o.InvokeMethod("WmiSetBrightness", new object[] { 1u, (byte)v });
                return true;
            }
            return false;
        }
        uint raw = (uint)Math.Round(v * m.Max / 100.0);
        for (int i = 0; i < 3; i++)
        {
            if (SetVCPFeature(m.Handle, VcpBrightness, raw)) return true;
            Thread.Sleep(50);
        }
        return false;
    }

    private static void DoRefresh()
    {
        var news = Enumerate();
        foreach (var m in news)
        {
            m.Value = Read(m);
            m.Ok = m.Value != null;
            if (!m.Ok && Warned.Add(m.Key))
                Log.Write($"Tela {m.Name}: sem resposta do DDC/CI (ative DDC/CI no menu do monitor)");
        }
        List<Monitor> old;
        lock (Gate) { old = _monitors; _monitors = news; }
        foreach (var m in old) if (m.Handle != IntPtr.Zero) DestroyPhysicalMonitor(m.Handle);
        Ready.Set();
        List<object> deferred;
        lock (Gate) { deferred = Deferred.ToList(); Deferred.Clear(); }
        foreach (var d in deferred) SetByName(d);
    }

    private static void Loop()
    {
        while (true)
        {
            Wake.WaitOne();
            try
            {
                if (_wantRefresh)
                {
                    _wantRefresh = false;
                    DoRefresh();
                    Bus.Notify();
                }
                while (true)
                {
                    string key; int v; Monitor? m;
                    lock (Gate)
                    {
                        if (Pending.Count == 0) break;
                        (key, v) = (Pending.Keys.First(), Pending.Values.First());
                        Pending.Remove(key);
                        m = _monitors.FirstOrDefault(x => x.Key == key);
                    }
                    if (m != null && !Write(m, v)) Log.Write($"Falha ao ajustar o brilho de {m.Name}");
                }
            }
            catch (Exception e) { Log.Write($"erro nas telas: {e.Message}"); }
        }
    }

    public static void Start()
    {
        new Thread(Loop) { IsBackground = true, Name = "telas" }.Start();
        Refresh();
    }
}
