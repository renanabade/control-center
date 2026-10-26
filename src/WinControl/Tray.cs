using System.Diagnostics;
using System.Drawing.Imaging;
using WinControl.Services;
using WF = System.Windows.Forms;
using Dr = System.Drawing;

namespace WinControl;

/// <summary>Ícone na bandeja: clique abre o painel; botão direito abre o menu (como o do glight).</summary>
public sealed class Tray : IDisposable
{
    private readonly WF.NotifyIcon _icon;
    private readonly WF.ContextMenuStrip _menu = new();
    private readonly Dictionary<string, Dr.Icon> _icons = new();
    private string? _key, _title;

    public Tray(Action togglePanel, Action quit)
    {
        var baseImg = new Dr.Bitmap(Path.Combine(AppContext.BaseDirectory, "Assets", "icon.png"));
        foreach (var s in new[] { "on", "off", "offline" }) _icons[s] = MakeIcon(baseImg, s);

        _icon = new WF.NotifyIcon { Icon = _icons["off"], Text = "Control Center", Visible = true, ContextMenuStrip = _menu };
        _icon.MouseClick += (_, e) => { if (e.Button == WF.MouseButtons.Left) togglePanel(); };
        _menu.Opening += (_, _) => BuildMenu(quit);

        Bus.Changed += () => Ui.Run(Sync);
        Core.RotateFromHotkey = () => RotateNow();
        Sync();
    }

    private static Dr.Icon MakeIcon(Dr.Bitmap src, string state)
    {
        var bmp = new Dr.Bitmap(32, 32);
        using (var g = Dr.Graphics.FromImage(bmp))
        {
            g.InterpolationMode = Dr.Drawing2D.InterpolationMode.HighQualityBicubic;
            if (state == "on") g.DrawImage(src, 0, 0, 32, 32);
            else
            {
                // apagada ou sem conexão: tons de cinza
                var cm = new ColorMatrix(new[]
                {
                    new[] { .3f * .55f, .3f * .55f, .3f * .55f, 0, 0 },
                    new[] { .59f * .55f, .59f * .55f, .59f * .55f, 0, 0 },
                    new[] { .11f * .55f, .11f * .55f, .11f * .55f, 0, 0 },
                    new[] { 0f, 0, 0, 1, 0 },
                    new[] { .27f, .27f, .27f, 0, 1 },
                });
                using var ia = new ImageAttributes();
                ia.SetColorMatrix(cm);
                g.DrawImage(src, new Dr.Rectangle(0, 0, 32, 32), 0, 0, src.Width, src.Height, Dr.GraphicsUnit.Pixel, ia);
                if (state == "offline")
                {
                    g.FillEllipse(new Dr.SolidBrush(Dr.Color.FromArgb(232, 72, 72)), 18, 18, 14, 14);
                    g.DrawEllipse(new Dr.Pen(Dr.Color.FromArgb(30, 30, 34), 2), 18, 18, 14, 14);
                }
            }
        }
        return Dr.Icon.FromHandle(bmp.GetHicon());
    }

    private static string TitleText()
    {
        string s = $"Lâmpada: {Ui.LampStatus()}";
        var avg = Screens.Average();
        if (avg != null) s += $"\nTelas: {avg}%";
        s += $"\nTema: {(Theme.IsDark() ? "escuro" : "claro")}";
        return s.Length > 120 ? s[..120] : s;
    }

    private void Sync()
    {
        var c = Lamp.Cur;
        string key = !c.Online ? "offline" : c.On ? "on" : "off";
        if (key != _key) { _icon.Icon = _icons[key]; _key = key; }
        string title = TitleText();
        if (title != _title) { _icon.Text = title; _title = title; }
    }

    public void Balloon(string title, string text) => _icon.ShowBalloonTip(2500, title, text, WF.ToolTipIcon.Info);

    public void RotateNow()
    {
        Task.Run(() =>
        {
            var (ok, desc) = Rotation.Toggle();
            Ui.Run(() => _icon.ShowBalloonTip(1500, "Girar monitor", ok ? $"Monitor alternado para: {desc}" : desc,
                                              ok ? WF.ToolTipIcon.Info : WF.ToolTipIcon.Warning));
        });
    }

    private WF.ToolStripMenuItem Item(string text, Action? click, bool enabled = true, bool? check = null)
    {
        var it = new WF.ToolStripMenuItem(text) { Enabled = enabled };
        if (check != null) { it.CheckOnClick = false; it.Checked = check.Value; }
        if (click != null) it.Click += (_, _) => Task.Run(click);
        return it;
    }

    private void BuildMenu(Action quit)
    {
        _menu.Items.Clear();
        var c = Lamp.Cur;
        bool usable = c.On && c.Online;

        _menu.Items.Add(Item($"Lâmpada: {Ui.LampStatus()}", null, enabled: false));
        _menu.Items.Add(Item("Ligar / Desligar lâmpada", Lamp.TogglePower));
        _menu.Items.Add(new WF.ToolStripSeparator());

        var cenas = new WF.ToolStripMenuItem("Cenas");
        foreach (var cena in Conforto.Cenas.OfType<System.Text.Json.Nodes.JsonObject>())
        {
            string nome = cena["nome"]?.ToString() ?? "?";
            cenas.DropDownItems.Add(Item(nome, () => Conforto.ApplyScene(nome), check: Conforto.Cena == nome));
        }
        _menu.Items.Add(cenas);

        var lamp = new WF.ToolStripMenuItem("Lâmpada");
        foreach (var (label, b, t) in new[] { ("Noite", 1, 0), ("Relaxar", 35, 15), ("Padrão", 50, 50), ("Foco", 100, 100) })
            lamp.DropDownItems.Add(Item($"Ligar: {label} ({b}%)", () => Lamp.PresetOn(b, t)));
        lamp.DropDownItems.Add(new WF.ToolStripSeparator());
        var timers = new WF.ToolStripMenuItem("Desligar em");
        foreach (var (mins, label) in new[] { (15, "15 min"), (30, "30 min"), (60, "1 h"), (120, "2 h") })
            timers.DropDownItems.Add(Item(label, () => Lamp.SetTimer(mins), enabled: usable,
                                          check: Lamp.TimerLeft() > 0 && c.TimerOpt == mins));
        timers.DropDownItems.Add(new WF.ToolStripSeparator());
        timers.DropDownItems.Add(Item("Cancelar timer", () => Lamp.SetTimer(0), enabled: Lamp.TimerLeft() > 0));
        lamp.DropDownItems.Add(timers);
        _menu.Items.Add(lamp);

        var proj = new WF.ToolStripMenuItem("Projetar");
        var current = DisplayProjection.Current();
        foreach (var m in new[] { DisplayProjection.Mode.PcScreenOnly, DisplayProjection.Mode.Extend, DisplayProjection.Mode.SecondScreenOnly })
            proj.DropDownItems.Add(Item(DisplayProjection.Label(m), () => { DisplayProjection.Apply(m); Bus.Notify(); }, check: current == m));
        _menu.Items.Add(proj);
        _menu.Items.Add(Item("Girar monitor (Ctrl+Alt+R)", RotateNow));

        var pad = new WF.ToolStripMenuItem($"Controle: {DualSense.Status()}");
        pad.DropDownItems.Add(Item("Automático por jogo", () => DualSense.SetAutomatico(!DualSense.Cfg.Automatico), check: DualSense.Cfg.Automatico));
        foreach (var m in Enum.GetValues<DualSense.Modo>())
            pad.DropDownItems.Add(Item($"Emular {DualSense.Label(m)}", () => DualSense.SetModo(m), check: DualSense.Efetivo == m));
        pad.DropDownItems.Add(Item("LED de jogador", () => DualSense.SetPlayerLeds(!DualSense.Cfg.LedsJogador), check: DualSense.Cfg.LedsJogador));        pad.DropDownItems.Add(Item("Editar jogos conhecidos", () => Process.Start("notepad.exe", Jogos.FilePath)));
        pad.DropDownItems.Add(new WF.ToolStripSeparator());
        foreach (var v in new[] { 0, 25, 50, 100 })
            pad.DropDownItems.Add(Item(v == 0 ? "Luz apagada" : $"Luz {v}%", () => DualSense.SetIntensity(v), check: DualSense.Cfg.Intensidade == v));
        _menu.Items.Add(pad);

        var peri = new WF.ToolStripMenuItem("Periféricos");
        peri.DropDownItems.Add(Item("Mouse segue o controle", () => { Perifericos.Cfg.Mouse.SeguirControle ^= true; Perifericos.Changed(); },
                                    check: Perifericos.Cfg.Mouse.SeguirControle));
        peri.DropDownItems.Add(Item("Teclado segue o controle", () => { Perifericos.Cfg.Teclado.SeguirControle ^= true; Perifericos.Changed(); },
                                    check: Perifericos.Cfg.Teclado.SeguirControle));
        _menu.Items.Add(peri);

        _menu.Items.Add(new WF.ToolStripSeparator());
        _menu.Items.Add(Item("Tema escuro", () => Core.SetDark(!Theme.IsDark()), check: Theme.IsDark()));
        _menu.Items.Add(Item("Tema segue a Luz Noturna", () => Core.ToggleOption("tema_automatico"), check: Conforto.Flag("tema_automatico")));
        _menu.Items.Add(Item("Agenda das telas", () => Core.ToggleOption("agenda_ativa"), check: Conforto.Flag("agenda_ativa")));
        _menu.Items.Add(new WF.ToolStripSeparator());
        _menu.Items.Add(Item("Iniciar com o Windows", Core.ToggleAutostart, check: Core.AutostartEnabled()));
        _menu.Items.Add(Item("Editar cenas e agenda", () => Process.Start("notepad.exe", AppPaths.Conforto)));
        _menu.Items.Add(Item("Abrir pasta de configuração", () => Process.Start("explorer.exe", AppPaths.Dir)));
        _menu.Items.Add(Item("Abrir log", () => Process.Start(new ProcessStartInfo(AppPaths.LogFile) { UseShellExecute = true })));
        _menu.Items.Add(new WF.ToolStripSeparator());
        _menu.Items.Add(Item("Sair", () => Ui.Run(quit)));
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
