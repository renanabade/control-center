using System.Diagnostics;
using System.IO.Hashing;
using System.Text.Json;
using HidSharp;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.DualShock4;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace WinControl.Services;

/// <summary>
/// DualSense por USB ou Bluetooth: cor e intensidade da barra de luz, e emulação de Xbox 360 / DualShock 4 pelo
/// ViGEmBus. No modo "dualsense" nada é emulado: o jogo vê o controle original.
/// </summary>
public static class DualSense
{
    public enum Modo { DualSense, Xbox360, Ps4 }

    public sealed class Config
    {
        public string Cor { get; set; } = "#0050FF";       // a única cor, ou a do nativo com cor_por_modo
        public bool CorPorModo { get; set; }
        public string CorXbox360 { get; set; } = "#00C040";
        public string CorPs4 { get; set; } = "#FF2020";
        public int Intensidade { get; set; } = 60;           // 0-100; a barra não tem brilho próprio, escala o RGB
        public Modo Modo { get; set; } = Modo.DualSense;
        public bool LedsJogador { get; set; }
        /// <summary>Escolhe o modo pelo jogo aberto (Jogos.cs); sem jogo, fica no nativo.</summary>
        public bool Automatico { get; set; }
    }

    private static void SetGameColor((byte r, byte g, byte b)? cor)
    {
        if (_gameColor == cor) return;
        _gameColor = cor;
        OutputEvent.Set();
    }

    /// <summary>O modo que vale agora: o do jogo aberto no automático, ou o escolhido.</summary>
    public static Modo Efetivo => Cfg.Automatico ? Jogos.Atual?.jogo.Modo ?? Modo.DualSense : Cfg.Modo;

    public static Config Cfg { get; private set; } = new();
    public static bool Conectado { get; private set; }
    public static bool Bluetooth { get; private set; }
    public static int? Bateria { get; private set; }
    /// <summary>Por que a emulação não está rodando (driver ausente etc.); null quando está tudo certo.</summary>
    public static string? Problema { get; private set; }

    private static string ConfigFile => Path.Combine(AppPaths.Dir, "dualsense.json");
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    // DualSense e DualSense Edge
    private const int Sony = 0x054C;
    private static readonly int[] Pids = { 0x0CE6, 0x0DF2 };

    private static readonly AutoResetEvent OutputEvent = new(false);
    private static HidStream? _stream;
    private static string? _devicePath;
    private static byte _btSeq;
    private static byte _rumbleL, _rumbleR;
    private static bool _setupLightbar;
    private static volatile bool _forceSend;
    private static (byte r, byte g, byte b)? _gameColor;   // cor pedida pelo jogo no modo PS4

    private static ViGEmClient? _vigem;
    private static IVirtualGamepad? _pad;
    private static Modo? _padModo;

    public static void Start()
    {
        Load();
        Jogos.Start();
        new Thread(Reader) { IsBackground = true, Name = "dualsense" }.Start();
        new Thread(Writer) { IsBackground = true, Name = "dualsense-saida" }.Start();
    }

    /// <summary>Ao sair: tira o controle virtual e devolve o real aos jogos.</summary>
    public static void Stop()
    {
        DisconnectPad();
        HidHide(false);
    }

    private static void Load()
    {
        try
        {
            if (File.Exists(ConfigFile)) Cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigFile), Json) ?? new();
        }
        catch (Exception e) { Log.Write($"dualsense.json inválido, usando o padrão: {e.Message}"); }
    }

    private static void Save()
    {
        try { File.WriteAllText(ConfigFile, JsonSerializer.Serialize(Cfg, Json)); }
        catch (Exception e) { Log.Write($"não consegui salvar dualsense.json: {e.Message}"); }
    }

    // ---------------- comandos da interface ----------------
    /// <summary>Com cor por modo, muda a cor do modo que está valendo agora.</summary>
    public static void SetColor(byte r, byte g, byte b)
    {
        string hex = $"#{r:X2}{g:X2}{b:X2}";
        if (!Cfg.CorPorModo || Efetivo == Modo.DualSense) Cfg.Cor = hex;
        else if (Efetivo == Modo.Xbox360) Cfg.CorXbox360 = hex;
        else Cfg.CorPs4 = hex;
        Changed();
    }

    public static void SetCorPorModo(bool on) { Cfg.CorPorModo = on; Changed(); }

    public static void SetIntensity(int v) { Cfg.Intensidade = Math.Clamp(v, 0, 100); Changed(); }

    public static void SetPlayerLeds(bool on) { Cfg.LedsJogador = on; Changed(); }

    public static void SetModo(Modo m)
    {
        // no automático com jogo aberto, a escolha vale para aquele jogo e o automático continua
        if (Cfg.Automatico && Jogos.Atual != null) Jogos.Lembrar(m);
        else { Cfg.Modo = m; Cfg.Automatico = false; Save(); }
        Reapply();
    }

    public static void SetAutomatico(bool on)
    {
        Cfg.Automatico = on;
        Save();
        Reapply();
    }

    /// <summary>Recalcula o modo (jogo abriu/fechou, escolha mudou).</summary>
    public static void Reapply()
    {
        Task.Run(ApplyModo);
        Bus.Notify();
    }

    private static void Changed()
    {
        Save();
        OutputEvent.Set();   // o envio coalescido manda só o último valor
        Bus.Notify();
    }

    public static (byte r, byte g, byte b) BaseColor()
    {
        string cor = !Cfg.CorPorModo ? Cfg.Cor : Efetivo switch
        {
            Modo.Xbox360 => Cfg.CorXbox360,
            Modo.Ps4 => Cfg.CorPs4,
            _ => Cfg.Cor,
        };
        var hex = cor.TrimStart('#');
        try { return (Convert.ToByte(hex[0..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16)); }
        catch { return (0, 80, 255); }
    }

    public static string Label(Modo m) => m switch
    {
        Modo.Xbox360 => "Xbox 360",
        Modo.Ps4 => "PS4 (DualShock 4)",
        _ => "DualSense (nativo)",
    };

    public static string Status()
    {
        if (!Conectado) return "Desconectado";
        string s = (Bluetooth ? "Bluetooth" : "USB") + (Bateria is { } b ? $" · {b}%" : "");
        s += $" · {Label(Efetivo)}";
        if (Cfg.Automatico) s += Jogos.Atual is { } a ? $" ({a.jogo.Nome})" : " (automático)";
        if (Problema != null) s += $" · {Problema}";
        return s;
    }

    // ---------------- leitura ----------------
    private static HidDevice? Find() =>
        DeviceList.Local.GetHidDevices(Sony).FirstOrDefault(d => Pids.Contains(d.ProductID) && d.GetMaxInputReportLength() >= 64);

    private static void Reader()
    {
        while (true)
        {
            var dev = Find();
            if (dev == null || !dev.TryOpen(out var stream)) { Thread.Sleep(2000); continue; }
            try
            {
                Bluetooth = dev.GetMaxInputReportLength() > 64;
                _devicePath = dev.DevicePath;
                stream.ReadTimeout = Timeout.Infinite;
                // por Bluetooth o controle começa no relatório simples; ler a calibração (0x05) liga o completo (0x31)
                if (Bluetooth) { var f = new byte[41]; f[0] = 0x05; try { stream.GetFeature(f); } catch { } }
                _stream = stream;
                _setupLightbar = true;
                _forceSend = true;   // ao (re)conectar o controle volta ao azul de fábrica: manda a sua cor mesmo com jogo aberto
                Conectado = true;
                Log.Write($"DualSense conectado ({(Bluetooth ? "Bluetooth" : "USB")})");
                ApplyModo();
                OutputEvent.Set();
                Bus.Notify();

                var buf = new byte[dev.GetMaxInputReportLength()];
                while (true)
                {
                    int n = stream.Read(buf, 0, buf.Length);
                    // USB: 0x01 com os dados a partir do byte 1; Bluetooth: 0x31 a partir do byte 2
                    int o = buf[0] == 0x01 && !Bluetooth ? 1 : buf[0] == 0x31 ? 2 : -1;
                    if (o < 0 || n < o + 53) continue;
                    var span = new ReadOnlySpan<byte>(buf, o, n - o);
                    UpdateBattery(span);
                    Forward(span, Shortcut(span));
                }
            }
            catch (Exception e) { Log.Write($"DualSense desconectado: {e.Message}"); }
            finally
            {
                try { stream.Dispose(); } catch { }
                _stream = null;
                Conectado = false;
                Bateria = null;
                DisconnectPad();   // sem controle físico, o virtual some também
                Bus.Notify();
            }
        }
    }

    private static void UpdateBattery(ReadOnlySpan<byte> d)
    {
        // byte 52: nível nos 4 bits de baixo (0-10), estado de carga nos de cima
        int lvl = Math.Min(d[52] & 0x0F, 10) * 10;
        if (Bateria != lvl) { Bateria = lvl; Bus.Notify(); }
    }

    // ---------------- emulação ----------------
    private static readonly object PadGate = new();

    private static void ApplyModo()
    {
        lock (PadGate)
        {
            var want = Conectado ? Efetivo : Modo.DualSense;
            if (_pad != null && _padModo == want) return;
            DisconnectPad();
            if (want != _lastWant) { _lastWant = want; StartTransition(); }
            Problema = null;
            if (want == Modo.DualSense) { HidHide(false); Bus.Notify(); return; }
            try
            {
                _vigem ??= new ViGEmClient();
                if (want == Modo.Xbox360)
                {
                    var x = _vigem.CreateXbox360Controller();
                    x.AutoSubmitReport = false;
                    x.FeedbackReceived += (_, e) => Rumble(e.LargeMotor, e.SmallMotor);
                    x.Connect();
                    _pad = x;
                }
                else
                {
                    var p = _vigem.CreateDualShock4Controller();
                    p.AutoSubmitReport = false;
                    bool first = true;
                    p.FeedbackReceived += (_, e) =>
                    {
                        // a cor que o jogo pede ao DS4 vai para a barra do DualSense; a primeira é o padrão do
                        // driver ao criar o controle (azul), não do jogo
                        var c = e.LightbarColor;
                        var cor = (c.Red, c.Green, c.Blue);
                        // preto = o jogo largou a barra (ex.: Cyberpunk depois da polícia): volta a sua cor
                        (byte, byte, byte)? want = cor == (0, 0, 0) ? null : cor;
                        // sem jogo aberto quem pede cor é a Steam (que também vê o DS4 virtual): ignora
                        if (Jogos.Atual == null) want = null;
                        if (first) first = false;
                        else if (_gameColor != want)
                        {
                            Log.Write($"DS4 virtual pediu cor {cor} ({(Jogos.Atual is { } a ? a.jogo.Nome : "sem jogo, ignorada")})");
                            SetGameColor(want);
                            OutputEvent.Set();
                        }
                        Rumble(e.LargeMotor, e.SmallMotor);
                    };
                    p.Connect();
                    _pad = p;
                }
                _padModo = want;
                HidHide(true);
                Log.Write($"emulando {Label(want)}");
            }
            catch (Nefarius.ViGEm.Client.Exceptions.VigemBusNotFoundException)
            {
                Problema = "ViGEmBus não instalado";
                Log.Write("emulação: ViGEmBus não encontrado");
            }
            catch (Exception e)
            {
                Problema = "emulação falhou";
                Log.Write($"emulação falhou: {e.Message}");
            }
            Bus.Notify();
        }
    }

    private static void DisconnectPad()
    {
        lock (PadGate)
        {
            if (_pad == null) return;
            try { _pad.Disconnect(); } catch { }
            _pad = null;
            _padModo = null;
            SetGameColor(null);
            Rumble(0, 0);
        }
    }

    private static void Rumble(byte large, byte small)
    {
        if (_rumbleL == large && _rumbleR == small) return;
        (_rumbleL, _rumbleR) = (large, small);
        OutputEvent.Set();
    }

    // ---------------- atalho: Mute + direcional troca o modo ----------------
    private static int _lastDpad = 8;
    private static DateTime _buzzUntil;

    /// <summary>Trata Mute + ↑/←/→/↓; devolve true enquanto o Mute está segurado (o direcional não vai para o jogo).</summary>
    private static bool Shortcut(ReadOnlySpan<byte> d)
    {
        bool mute = (d[9] & 0x04) != 0;
        int dpad = d[7] & 0x0F;
        if (mute && dpad != _lastDpad)
        {
            (Action? act, string txt) = dpad switch
            {
                0 => ((Action?)(() => SetModo(Modo.DualSense)), Label(Modo.DualSense)),
                6 => (() => SetModo(Modo.Xbox360), Label(Modo.Xbox360)),
                2 => (() => SetModo(Modo.Ps4), Label(Modo.Ps4)),
                4 => (() => SetAutomatico(!Cfg.Automatico), Cfg.Automatico ? "Automático desligado" : "Automático ligado"),
                _ => (null, ""),
            };
            if (act != null)
            {
                act();
                // confirmação: vibração curta; a luz apaga e volta na cor do modo (StartTransition)
                _buzzUntil = DateTime.Now.AddMilliseconds(180);
                _forceSend = true;
                OutputEvent.Set();
                Log.Write($"atalho do controle: {txt}");
                Ui.Run(() => App.Tray?.Balloon("Controle", txt));
            }
        }
        _lastDpad = dpad;
        return mute;
    }

    private static void Forward(ReadOnlySpan<byte> d, bool muteHeld)
    {
        var pad = _pad;
        if (pad == null) return;
        byte lx = d[0], ly = d[1], rx = d[2], ry = d[3], l2 = d[4], r2 = d[5];
        byte b0 = d[7], b1 = d[8], b2 = d[9];
        int dpad = muteHeld ? 8 : b0 & 0x0F;
        bool sq = (b0 & 0x10) != 0, cross = (b0 & 0x20) != 0, circle = (b0 & 0x40) != 0, tri = (b0 & 0x80) != 0;
        bool l1 = (b1 & 0x01) != 0, r1 = (b1 & 0x02) != 0, create = (b1 & 0x10) != 0, options = (b1 & 0x20) != 0;
        bool l3 = (b1 & 0x40) != 0, r3 = (b1 & 0x80) != 0, ps = (b2 & 0x01) != 0, touch = (b2 & 0x02) != 0;
        try
        {
            if (pad is IXbox360Controller x)
            {
                x.SetAxisValue(Xbox360Axis.LeftThumbX, Axis(lx));
                x.SetAxisValue(Xbox360Axis.LeftThumbY, Axis(ly, invert: true));
                x.SetAxisValue(Xbox360Axis.RightThumbX, Axis(rx));
                x.SetAxisValue(Xbox360Axis.RightThumbY, Axis(ry, invert: true));
                x.SetSliderValue(Xbox360Slider.LeftTrigger, l2);
                x.SetSliderValue(Xbox360Slider.RightTrigger, r2);
                x.SetButtonState(Xbox360Button.A, cross);
                x.SetButtonState(Xbox360Button.B, circle);
                x.SetButtonState(Xbox360Button.X, sq);
                x.SetButtonState(Xbox360Button.Y, tri);
                x.SetButtonState(Xbox360Button.LeftShoulder, l1);
                x.SetButtonState(Xbox360Button.RightShoulder, r1);
                x.SetButtonState(Xbox360Button.LeftThumb, l3);
                x.SetButtonState(Xbox360Button.RightThumb, r3);
                x.SetButtonState(Xbox360Button.Back, create || touch);   // touchpad clicado vira Back, como no DSX
                x.SetButtonState(Xbox360Button.Start, options);
                x.SetButtonState(Xbox360Button.Guide, ps);
                x.SetButtonState(Xbox360Button.Up, dpad is 7 or 0 or 1);
                x.SetButtonState(Xbox360Button.Right, dpad is 1 or 2 or 3);
                x.SetButtonState(Xbox360Button.Down, dpad is 3 or 4 or 5);
                x.SetButtonState(Xbox360Button.Left, dpad is 5 or 6 or 7);
                x.SubmitReport();
            }
            else if (pad is IDualShock4Controller p)
            {
                p.SetAxisValue(DualShock4Axis.LeftThumbX, lx);
                p.SetAxisValue(DualShock4Axis.LeftThumbY, ly);
                p.SetAxisValue(DualShock4Axis.RightThumbX, rx);
                p.SetAxisValue(DualShock4Axis.RightThumbY, ry);
                p.SetSliderValue(DualShock4Slider.LeftTrigger, l2);
                p.SetSliderValue(DualShock4Slider.RightTrigger, r2);
                p.SetButtonState(DualShock4Button.Cross, cross);
                p.SetButtonState(DualShock4Button.Circle, circle);
                p.SetButtonState(DualShock4Button.Square, sq);
                p.SetButtonState(DualShock4Button.Triangle, tri);
                p.SetButtonState(DualShock4Button.ShoulderLeft, l1);
                p.SetButtonState(DualShock4Button.ShoulderRight, r1);
                p.SetButtonState(DualShock4Button.TriggerLeft, l2 > 0);
                p.SetButtonState(DualShock4Button.TriggerRight, r2 > 0);
                p.SetButtonState(DualShock4Button.ThumbLeft, l3);
                p.SetButtonState(DualShock4Button.ThumbRight, r3);
                p.SetButtonState(DualShock4Button.Share, create);
                p.SetButtonState(DualShock4Button.Options, options);
                p.SetButtonState(DualShock4SpecialButton.Ps, ps);
                p.SetButtonState(DualShock4SpecialButton.Touchpad, touch);
                p.SetDPadDirection(dpad switch
                {
                    0 => DualShock4DPadDirection.North, 1 => DualShock4DPadDirection.Northeast,
                    2 => DualShock4DPadDirection.East, 3 => DualShock4DPadDirection.Southeast,
                    4 => DualShock4DPadDirection.South, 5 => DualShock4DPadDirection.Southwest,
                    6 => DualShock4DPadDirection.West, 7 => DualShock4DPadDirection.Northwest,
                    _ => DualShock4DPadDirection.None,
                });
                p.SubmitReport();
            }
        }
        catch (Exception e) { Log.Write($"emulação: erro ao enviar: {e.Message}"); }
    }

    // 0-255 com centro em 128 -> -32768..32767 do Xbox (Y do Xbox cresce para cima)
    private static short Axis(byte v, bool invert = false)
    {
        int c = v - 128;
        if (invert) c = -c - 1;
        return (short)Math.Clamp(c * 257, short.MinValue, short.MaxValue);
    }

    // ---------------- saída: luz e vibração ----------------
    private static void Writer()
    {
        while (true)
        {
            // reenvia a cada segundo, senão a Steam cobre a cor (o último a escrever ganha);
            // exceção: nativo com jogo aberto, aí a luz é do jogo
            // na transição manda ~30 quadros por segundo para o "breathing" ficar suave
            var now = DateTime.Now;
            bool anim = now < _transEnd;
            int wait = anim ? 33 : now < _buzzUntil ? (int)(_buzzUntil - now).TotalMilliseconds + 5 : 1000;
            bool changed = OutputEvent.WaitOne(wait);
            if (changed) Thread.Sleep(15);   // junta várias mudanças seguidas do slider num envio só
            var s = _stream;
            if (s == null) continue;
            bool forced = _forceSend;
            _forceSend = false;
            // a transição sai até com jogo aberto no nativo; o último quadro (cor cheia) também
            anim = DateTime.Now < _transEnd + TimeSpan.FromMilliseconds(100);
            if (Efetivo == Modo.DualSense && !anim && !forced)
            {
                // nativo com jogo aberto: luz e LEDs são todos do jogo/Steam. No desktop, reenvia como nos
                // outros modos, senão a Steam acende o LED de jogador
                if (Jogos.Atual != null) continue;
            }
            try { s.Write(BuildOutput()); }
            catch (Exception e) { Log.Write($"DualSense: erro ao enviar saída: {e.Message}"); }
        }
    }

    // ---------------- transição ao trocar de modo: apaga e acende respirando ----------------
    private static Modo? _lastWant;
    private static DateTime _transStart, _transEnd;
    private static readonly TimeSpan Escuro = TimeSpan.FromMilliseconds(1500), Acender = TimeSpan.FromMilliseconds(1200);

    // durante a troca o controle virtual some e volta, e jogo/Steam mandam cores no meio: apagado, ninguém vê o pisca
    private static void StartTransition()
    {
        _transStart = DateTime.Now;
        _transEnd = _transStart + Escuro + Acender;
        OutputEvent.Set();
    }

    /// <summary>0 apagado .. 1 cor cheia; fora da transição é 1.</summary>
    private static double TransitionLevel()
    {
        var t = DateTime.Now - _transStart;
        if (DateTime.Now >= _transEnd) return 1;
        if (t < Escuro) return 0;
        double x = (t - Escuro) / Acender;
        return (1 - Math.Cos(Math.PI * x)) / 2;   // meia senoide: começa e termina devagar, como respiração
    }

    private static byte[] BuildOutput()
    {
        // mesmo bloco de dados nos dois; muda só o cabeçalho e, no Bluetooth, o CRC no fim
        var common = new byte[47];
        common[0] = 0x03;                                  // vibração compatível (motores simples)
        common[1] = 0x01 | 0x04 | 0x10;                    // vale o LED do Mute, a barra de luz e os LEDs de jogador
        bool buzz = DateTime.Now < _buzzUntil;
        common[2] = buzz ? (byte)0x60 : _rumbleR;          // motor direito (fraco)
        common[3] = buzz ? (byte)0x60 : _rumbleL;          // motor esquerdo (forte)
        common[8] = 0x00;                                  // LED do Mute sempre apagado: o botão é do atalho
        if (_setupLightbar) { common[38] = 0x02; common[41] = 0x02; _setupLightbar = false; }   // solta a barra do firmware
        common[42] = 0x00;                                 // brilho dos LEDs de jogador: máximo
        common[43] = (byte)(Cfg.LedsJogador ? 0x04 : 0x00);   // só o LED do meio, jogador 1
        var (r, g, b) = _gameColor ?? BaseColor();
        var (br, bg, bb) = BaseColor();
        double k = Cfg.Intensidade / 100.0 * TransitionLevel();
        common[44] = (byte)Math.Round(r * k);
        common[45] = (byte)Math.Round(g * k);
        common[46] = (byte)Math.Round(b * k);
        // mouse e teclado seguem a cor base do modo: sem intensidade, transição nem cor do jogo.
        // Reescrever o teclado a cada quadro da transição faz ele piscar
        Perifericos.FromController(br, bg, bb);

        if (!Bluetooth)
        {
            var usb = new byte[48];
            usb[0] = 0x02;
            common.CopyTo(usb, 1);
            return usb;
        }
        var bt = new byte[78];
        bt[0] = 0x31;
        bt[1] = (byte)(_btSeq << 4);
        _btSeq = (byte)((_btSeq + 1) & 0x0F);
        bt[2] = 0x10;
        common.CopyTo(bt, 3);
        // CRC32 sobre o byte 0xA2 (cabeçalho HID de saída) + o relatório sem os 4 bytes finais
        var crc = new Crc32();
        crc.Append(new byte[] { 0xA2 });
        crc.Append(bt.AsSpan(0, 74));
        crc.GetCurrentHash().CopyTo(bt, 74);   // já sai em little-endian
        return bt;
    }

    // ---------------- HidHide: esconde o controle real enquanto emula ----------------
    private static readonly string HidHideCli =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Nefarius Software Solutions", "HidHide", "x64", "HidHideCLI.exe");

    public static bool HidHideInstalled => File.Exists(HidHideCli);

    private static void HidHide(bool hide)
    {
        if (!HidHideInstalled || _devicePath == null) return;
        string id = InstanceId(_devicePath);
        if (hide)
        {
            // o próprio control-center precisa continuar vendo o controle escondido
            RunHidHide($"--app-reg \"{Environment.ProcessPath}\"");
            RunHidHide($"--dev-hide \"{id}\"");
            RunHidHide("--cloak-on");
        }
        else RunHidHide($"--dev-unhide \"{id}\"");
    }

    // \\?\hid#vid_054c&pid_0ce6#7&abc&0&0000#{guid} -> HID\VID_054C&PID_0CE6\7&ABC&0&0000
    private static string InstanceId(string path)
    {
        var parts = path.Replace(@"\\?\", "").Split('#');
        return parts.Length >= 3 ? $"{parts[0]}\\{parts[1]}\\{parts[2]}".ToUpperInvariant() : path;
    }

    private static void RunHidHide(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(HidHideCli, args) { CreateNoWindow = true, UseShellExecute = false });
            p?.WaitForExit(3000);
        }
        catch (Exception e) { Log.Write($"HidHide falhou ({args}): {e.Message}"); }
    }
}
