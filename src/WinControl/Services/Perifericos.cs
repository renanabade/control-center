using System.Runtime.InteropServices;
using System.Text.Json;
using HidSharp;
using Microsoft.Win32.SafeHandles;

namespace WinControl.Services;

/// <summary>
/// Luz do mouse (Razer DeathAdder V2, protocolo Chroma) e do teclado (Redragon Kumara V2, protocolo EVision), sem
/// Synapse nem programa da Redragon. Cada um pode seguir a cor do DualSense ou ter a sua. Protocolos do OpenRGB.
/// </summary>
public static class Perifericos
{
    public sealed class Aparelho
    {
        public bool SeguirControle { get; set; } = true;
        public string Cor { get; set; } = "#0050FF";
        public int Brilho { get; set; } = 100;   // 0-100
        /// <summary>Só teclado: cor própria por tecla (índice do LED -> cor); as outras ficam na cor do aparelho.</summary>
        public Dictionary<int, string> Teclas { get; set; } = new();
    }

    // scan code (com 0x100 nas teclas estendidas) -> (LED do EVision, nome). Grade do OpenRGB, layout físico
    public static readonly Dictionary<int, (int led, string nome)> Teclas = BuildKeyMap();

    private static Dictionary<int, (int, string)> BuildKeyMap()
    {
        var m = new Dictionary<int, (int, string)>();
        void K(int scan, int led, string nome) => m[scan] = (led, nome);
        K(0x01, 0, "Esc");
        for (int i = 0; i < 10; i++) K(0x3B + i, i < 4 ? 1 + i : i < 8 ? 5 + i - 4 : 9 + i - 8, $"F{i + 1}");
        K(0x57, 11, "F11"); K(0x58, 12, "F12");
        K(0x137, 14, "Print Screen"); K(0x46, 15, "Scroll Lock"); K(0x45, 16, "Pause");
        K(0x29, 21, "` (aspas)");
        for (int i = 0; i < 10; i++) K(0x02 + i, 22 + i, ((i + 1) % 10).ToString());
        K(0x0C, 32, "-"); K(0x0D, 33, "="); K(0x0E, 34, "Backspace");
        K(0x152, 35, "Insert"); K(0x147, 36, "Home"); K(0x149, 37, "Page Up");
        K(0x0F, 42, "Tab");
        string top = "QWERTYUIOP";
        int[] topLed = { 43, 44, 45, 46, 47, 48, 49, 50, 51, 52 };
        for (int i = 0; i < 10; i++) K(0x10 + i, topLed[i], top[i].ToString());
        K(0x1A, 53, "["); K(0x1B, 54, "]"); K(0x2B, 55, "\\");
        K(0x153, 56, "Delete"); K(0x14F, 57, "End"); K(0x151, 58, "Page Down");
        K(0x3A, 63, "Caps Lock");
        string mid = "ASDFGHJKL";
        for (int i = 0; i < 9; i++) K(0x1E + i, 64 + i, mid[i].ToString());
        K(0x27, 73, "; (Ç)"); K(0x28, 74, "' (~)"); K(0x1C, 76, "Enter");
        K(0x2A, 84, "Shift esquerdo"); K(0x56, 85, "\\ (ISO)");
        string bot = "ZXCVBNM";
        int[] botLed = { 86, 87, 88, 89, 90, 91, 92 };
        for (int i = 0; i < 7; i++) K(0x2C + i, botLed[i], bot[i].ToString());
        K(0x33, 93, ","); K(0x34, 94, "."); K(0x35, 95, "/ (;)"); K(0x73, 96, "/ (ABNT2)");
        K(0x36, 97, "Shift direito"); K(0x148, 99, "Seta ↑");
        K(0x1D, 105, "Ctrl esquerdo"); K(0x15B, 106, "Windows"); K(0x38, 107, "Alt"); K(0x39, 108, "Espaço");
        K(0x138, 109, "Alt Gr"); K(0x15D, 111, "Menu"); K(0x11D, 113, "Ctrl direito");
        K(0x14B, 119, "Seta ←"); K(0x150, 120, "Seta ↓"); K(0x14D, 121, "Seta →");
        return m;
    }

    public static string KeyName(int led) => Teclas.Values.FirstOrDefault(t => t.led == led).nome ?? $"LED {led}";

    public sealed class Config
    {
        public Aparelho Mouse { get; set; } = new();
        public Aparelho Teclado { get; set; } = new();
    }

    public static Config Cfg { get; private set; } = new();
    public static bool MouseOk { get; private set; }
    public static bool TecladoOk { get; private set; }

    private static string ConfigFile => Path.Combine(AppPaths.Dir, "perifericos.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static readonly AutoResetEvent Wake = new(false);
    private static (byte r, byte g, byte b) _controle = (0, 80, 255);
    private static (byte, byte, byte)? _sentMouse;
    private static string? _sentTeclado;

    public static void Start()
    {
        try { if (File.Exists(ConfigFile)) Cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigFile), Json) ?? new(); }
        catch (Exception e) { Log.Write($"perifericos.json inválido, usando o padrão: {e.Message}"); }
        new Thread(Worker) { IsBackground = true, Name = "perifericos" }.Start();
        // aparelho plugado depois, ou PC voltando da suspensão (a luz volta ao padrão): reenvia a cor
        DeviceList.Local.Changed += (_, _) => Resend();
        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Resume) Resend();
        };
    }

    private static void Resend()
    {
        _sentMouse = null; _sentTeclado = null;
        Wake.Set();
    }

    private static void Save()
    {
        try { File.WriteAllText(ConfigFile, JsonSerializer.Serialize(Cfg, Json)); }
        catch (Exception e) { Log.Write($"não consegui salvar perifericos.json: {e.Message}"); }
    }

    /// <summary>O DualSense chama isto com a cor base do modo (sem intensidade, transição nem cor do jogo).</summary>
    public static void FromController(byte r, byte g, byte b)
    {
        if (_controle == (r, g, b)) return;
        _controle = (r, g, b);
        if (Cfg.Mouse.SeguirControle || Cfg.Teclado.SeguirControle) Wake.Set();
    }

    public static void Changed()
    {
        Save();
        { _sentMouse = null; _sentTeclado = null; }
        Wake.Set();
        Bus.Notify();
    }

    public static (byte r, byte g, byte b) Parse(string cor)
    {
        var hex = cor.TrimStart('#');
        try { return (Convert.ToByte(hex[0..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16)); }
        catch { return (0, 80, 255); }
    }

    private static (byte, byte, byte) Target(Aparelho a)
    {
        var (r, g, b) = a.SeguirControle ? _controle : Parse(a.Cor);
        return Scale((r, g, b), a.Brilho);
    }

    private static (byte, byte, byte) Scale((byte r, byte g, byte b) c, int brilho)
    {
        double k = brilho / 100.0;
        return ((byte)Math.Round(c.r * k), (byte)Math.Round(c.g * k), (byte)Math.Round(c.b * k));
    }

    // o que o teclado precisa mostrar: cor de fundo + teclas próprias (a assinatura evita reenviar o mesmo)
    private static string TecladoSig(out (byte, byte, byte) fundo, out Dictionary<int, (byte, byte, byte)> teclas)
    {
        fundo = Target(Cfg.Teclado);
        teclas = Cfg.Teclado.Teclas.ToDictionary(kv => kv.Key, kv => Scale(Parse(kv.Value), Cfg.Teclado.Brilho));
        return fundo + "|" + string.Join(";", teclas.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
    }

    // ---------------- envio: no máximo ~7 vezes por segundo, só quando a cor muda ----------------
    private static void Worker()
    {
        Thread.Sleep(1500);
        Wake.Set();
        while (true)
        {
            // plugar e voltar da suspensão já acordam; os 10 min cobrem o que o Windows não avisar
            if (!Wake.WaitOne(10 * 60_000)) { _sentMouse = null; _sentTeclado = null; }
            Thread.Sleep(150);   // a transição do controle manda 30 quadros/s; aqui basta o último
            try
            {
                var m = Target(Cfg.Mouse);
                if (m != _sentMouse) { MouseOk = Razer.SetColor(m); _sentMouse = MouseOk ? m : null; }
                string sig = TecladoSig(out var fundo, out var teclas);
                if (sig != _sentTeclado)
                {
                    TecladoOk = teclas.Count == 0 ? EVision.SetColor(fundo) : EVision.SetKeys(fundo, teclas);
                    _sentTeclado = TecladoOk ? sig : null;
                }
            }
            catch (Exception e) { Log.Write($"periféricos: erro ao enviar: {e.Message}"); }
        }
    }

    // ---------------- Razer DeathAdder V2: relatório de recurso de 90 bytes ----------------
    private static class Razer
    {
        private const int Vid = 0x1532, Pid = 0x0084;
        private const byte LedRoda = 0x01, LedLogo = 0x04;
        private static bool _logged;

        public static bool SetColor((byte r, byte g, byte b) c)
        {
            var dev = DeviceList.Local.GetHidDevices(Vid, Pid).FirstOrDefault(d => d.GetMaxFeatureReportLength() >= 91);
            if (dev == null) { LogOnce("mouse Razer não encontrado"); return false; }
            // a interface 0 também é a do cursor: o Windows não deixa abrir para leitura/escrita, mas relatórios de
            // recurso funcionam num handle sem acesso nenhum
            using var h = CreateFile(dev.DevicePath, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid) { LogOnce($"não consegui abrir o mouse Razer (erro {Marshal.GetLastWin32Error()})"); return false; }
            foreach (var led in new[] { LedLogo, LedRoda })
            {
                // classe 0x0F / 0x02: efeito na matriz estendida; 0x01 = estático, uma cor
                if (!Send(h, 0x0F, 0x02, new byte[] { 0x01, led, 0x01, 0x00, 0x00, 0x01, c.r, c.g, c.b }))
                {
                    LogOnce($"mouse Razer recusou o comando (erro {Marshal.GetLastWin32Error()})");
                    return false;
                }
            }
            _logged = false;
            return true;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buf, int len);

        private static bool Send(SafeFileHandle h, byte cls, byte id, byte[] args)
        {
            var buf = new byte[91];   // byte 0 é o id do relatório (0)
            buf[2] = 0x3F;            // transaction id do DeathAdder V2
            buf[6] = (byte)args.Length;
            buf[7] = cls;
            buf[8] = id;
            args.CopyTo(buf, 9);
            byte crc = 0;
            for (int i = 3; i < 89; i++) crc ^= buf[i];   // XOR dos bytes 2..87 do relatório
            buf[89] = crc;
            bool ok = HidD_SetFeature(h, buf, buf.Length);
            Thread.Sleep(5);   // o mouse precisa de um respiro entre comandos
            return ok;
        }

        private static void LogOnce(string msg) { if (!_logged) Log.Write($"periféricos: {msg}"); _logged = true; }
    }

    // ---------------- Redragon (EVision 320F:5000): relatório de saída 0x04, interface 1 ----------------
    private static class EVision
    {
        private const int Vid = 0x320F, Pid = 0x5000;
        private static bool _logged;

        public static bool SetColor((byte r, byte g, byte b) c)
        {
            var dev = Find();
            if (dev == null || !dev.TryOpen(out var s)) { LogOnce("teclado Redragon não encontrado"); return false; }
            using (s)
            {
                s.ReadTimeout = 200;
                Packet(s, 0x01, Array.Empty<byte>(), fixedCmd: true);   // começo
                // modo estático (0x06), brilho máximo (4): o brilho fica no RGB, que tem mais degraus que os 5 do teclado
                Packet(s, 0x06, new byte[] { 0x06, 0x04, 0x03, 0x00, 0x00, c.r, c.g, c.b }, param: 0x00);
                Packet(s, 0x02, Array.Empty<byte>(), fixedCmd: true);   // fim
            }
            _logged = false;
            return true;
        }

        /// <summary>Modo personalizado (0x14): cor por LED. 126 LEDs x RGB, em pacotes de 54 bytes com a posição.</summary>
        public static bool SetKeys((byte r, byte g, byte b) fundo, Dictionary<int, (byte r, byte g, byte b)> teclas)
        {
            var dev = Find();
            if (dev == null || !dev.TryOpen(out var s)) { LogOnce("teclado Redragon não encontrado"); return false; }
            using (s)
            {
                s.ReadTimeout = 200;
                var data = new byte[7 * 0x36];
                for (int i = 0; i < 126; i++)
                {
                    var c = teclas.TryGetValue(i, out var t) ? t : fundo;
                    data[3 * i] = c.r; data[3 * i + 1] = c.g; data[3 * i + 2] = c.b;
                }
                Packet(s, 0x01, Array.Empty<byte>(), fixedCmd: true);
                Packet(s, 0x06, new byte[] { 0x14, 0x04, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00 }, param: 0x00);
                for (int off = 0; off < data.Length; off += 0x36)
                    Packet(s, 0x11, data[off..(off + 0x36)], offset: off);
                Packet(s, 0x02, Array.Empty<byte>(), fixedCmd: true);
            }
            _logged = false;
            return true;
        }

        private static HidDevice? Find() => DeviceList.Local.GetHidDevices(Vid, Pid)
            .FirstOrDefault(d => d.GetMaxOutputReportLength() == 64 && d.DevicePath.Contains("mi_01", StringComparison.OrdinalIgnoreCase));

        private static void Packet(HidStream s, byte cmd, byte[] data, byte param = 0, bool fixedCmd = false, int offset = -1)
        {
            var buf = new byte[64];
            buf[0] = 0x04;
            buf[3] = cmd;
            if (fixedCmd) buf[1] = cmd;   // começo/fim: checksum é o próprio comando
            else
            {
                buf[4] = (byte)data.Length;
                // parâmetro: byte 5 é qual; dados de cor: bytes 5-6 são a posição
                if (offset >= 0) { buf[5] = (byte)(offset & 0xFF); buf[6] = (byte)(offset >> 8); }
                else buf[5] = param;
                data.CopyTo(buf, 8);
                // soma como o OpenRGB faz (char com sinal), que é o que o teclado aceita comprovadamente
                ushort sum = 0;
                for (int i = 3; i < 64; i++) sum += (ushort)(sbyte)buf[i];
                buf[1] = (byte)(sum & 0xFF);
                buf[2] = (byte)(sum >> 8);
            }
            s.Write(buf);
            try { s.Read(new byte[64]); } catch (TimeoutException) { }   // o teclado responde cada pacote
        }

        private static void LogOnce(string msg) { if (!_logged) Log.Write($"periféricos: {msg}"); _logged = true; }
    }
}
