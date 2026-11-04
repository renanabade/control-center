using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using WinControl.Services;

namespace WinControl.Pages;

/// <summary>Página completa das lâmpadas, ligada ao estado real e à fila de envio de cada uma.</summary>
public sealed partial class LampPage : Page
{
    // sobrevive ao painel fechar: quem controla sempre as mesmas duas não precisa marcar de novo. Null: todas as daqui.
    private static HashSet<string>? _marcadas;
    private const int AlturaFileira = 44;

    private bool _updating = true;   // ignora eventos enquanto o XAML ainda está sendo montado
    private bool _refreshQueued;
    private string _chipsSig = "";
    private ToggleButton? _todas;
    private readonly Dictionary<string, (ToggleButton nome, FontIcon luz, Button botao)> _chips = new();
    private Windows.UI.Color? _corDoEstado;   // a cor posta na roda pelo programa, para o aviso dela não voltar como comando

    public LampPage()
    {
        InitializeComponent();
        _updating = false;
        Loaded += (_, _) => { Bus.Changed += OnHub; Refresh(); PedirAltura(); };
        Unloaded += (_, _) => { Bus.Changed -= OnHub; App.Panel?.AjustarAltura(0); };
    }

    // cada fileira de lâmpadas precisa de espaço próprio: sem isto, a parte de baixo da página ficava cortada
    private void PedirAltura() =>
        App.Panel?.AjustarAltura(Lamp.Aqui.Count > 1 ? Chips.RowDefinitions.Count * AlturaFileira : 0);

    private void OnHub()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Ui.Run(() => { _refreshQueued = false; Refresh(); });
    }

    private static IReadOnlyList<Lampada> Alvos()
    {
        var aqui = Lamp.Aqui;
        return _marcadas == null || aqui.Count == 1 ? aqui : aqui.Where(l => _marcadas.Contains(l.Id)).ToList();
    }

    // ---------------- escolher as lâmpadas ----------------
    private void MontarChips()
    {
        var aqui = Lamp.Aqui;
        string sig = string.Join("|", aqui.Select(l => l.Id + l.Nome));
        if (sig == _chipsSig) return;
        _chipsSig = sig;
        Chips.Children.Clear();
        Chips.RowDefinitions.Clear();
        Chips.ColumnDefinitions.Clear();
        _chips.Clear();
        _todas = null;
        Chips.Visibility = aqui.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (aqui.Count < 2)
        {
            PedirAltura();
            return;
        }

        const int porLinha = 6;
        int total = aqui.Count + 1;
        for (int i = 0; i < Math.Min(total, porLinha); i++) Chips.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < (total + porLinha - 1) / porLinha; i++) Chips.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _todas = new ToggleButton { Content = Rotulo("Todas"), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Padding = new Thickness(4, 6, 4, 6) };
        ToolTipService.SetToolTip(_todas, "Marcar todas, ou desmarcar para escolher só algumas");
        _todas.Click += Todas_Click;
        Posicionar(_todas, 0, porLinha);

        for (int i = 0; i < aqui.Count; i++)
        {
            var l = aqui[i];
            var luz = new FontIcon { Glyph = "", FontSize = 14 };
            var botao = new Button { Content = luz, Padding = new Thickness(6, 6, 6, 6), CornerRadius = new CornerRadius(4, 0, 0, 4), VerticalAlignment = VerticalAlignment.Stretch };
            ToolTipService.SetToolTip(botao, $"Ligar ou apagar {l.Nome}");
            botao.Click += (_, _) => { Core.Touch(); Task.Run(l.TogglePower); };

            var nome = new ToggleButton { Content = Rotulo(l.Nome), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Padding = new Thickness(4, 6, 4, 6), CornerRadius = new CornerRadius(0, 4, 4, 0) };
            ToolTipService.SetToolTip(nome, $"Incluir {l.Nome} nos controles abaixo");
            string id = l.Id;
            nome.Click += (_, _) => Marcar(id, nome.IsChecked == true);

            var chip = new Grid { ColumnSpacing = 1 };
            chip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            chip.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(nome, 1);
            chip.Children.Add(botao);
            chip.Children.Add(nome);
            Posicionar(chip, i + 1, porLinha);
            _chips[id] = (nome, luz, botao);
        }
        PedirAltura();
    }

    private static TextBlock Rotulo(string texto) => new()
    {
        Text = texto,
        TextTrimming = TextTrimming.CharacterEllipsis,
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
    };

    private void Posicionar(FrameworkElement e, int i, int porLinha)
    {
        Grid.SetRow(e, i / porLinha);
        Grid.SetColumn(e, i % porLinha);
        Chips.Children.Add(e);
    }

    private void Todas_Click(object sender, RoutedEventArgs e)
    {
        // de todas marcadas, um clique limpa: é o caminho mais curto para escolher só duas
        _marcadas = _marcadas == null ? [] : null;
        Refresh();
    }

    private void Marcar(string id, bool marcada)
    {
        var aqui = Lamp.Aqui;
        var conjunto = _marcadas ?? aqui.Select(l => l.Id).ToHashSet();
        if (marcada) conjunto.Add(id); else conjunto.Remove(id);
        _marcadas = aqui.All(l => conjunto.Contains(l.Id)) ? null : conjunto;
        Refresh();
    }

    private void AtualizarChips(IReadOnlyList<Lampada> alvos)
    {
        var aqui = Lamp.Aqui;
        Titulo.Text = aqui.Count > 1 ? "Lâmpadas" : aqui.FirstOrDefault()?.Nome ?? "Lâmpada";
        if (_todas != null) _todas.IsChecked = alvos.Count == aqui.Count;
        foreach (var l in aqui)
        {
            if (!_chips.TryGetValue(l.Id, out var c)) continue;
            c.nome.IsChecked = alvos.Contains(l);
            bool acesa = l.Cur.On && l.Cur.Online;
            // acesa em âmbar, como uma luz; sem conexão, apagada e mais fraca
            c.luz.Foreground = acesa ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 196, 61))
                                     : (Brush)Application.Current.Resources[l.Cur.Online ? "TextFillColorSecondaryBrush" : "TextFillColorDisabledBrush"];
            c.botao.IsEnabled = l.Cur.Online || l.HasBle || l.Cur.Busy;
            ToolTipService.SetToolTip(c.botao, $"{l.Nome}: {Ui.LampStatus(l, withTimer: false)}. Clique para ligar ou apagar.");
        }
    }

    // ---------------- estado -> interface ----------------
    private void Refresh()
    {
        _updating = true;
        try
        {
            MontarChips();
            var alvos = Alvos();
            AtualizarChips(alvos);
            var c = Lamp.Resumo(alvos);
            bool usable = c.On && c.Online;
            bool colour = c.Mode == "colour";
            Power.IsEnabled = alvos.Count > 0 && (c.Online || c.Busy || alvos.Any(l => l.HasBle));
            Power.IsOn = usable;
            Status.Text = Ui.LampStatus(alvos, withTimer: false) + (alvos.Count > 0 && !c.Online && !c.Busy ? " · tentando de novo" : "");
            Body.Opacity = usable ? 1 : 0.5;
            Body.IsHitTestVisible = usable;

            // com o usuário mexendo, o controle é a referência: a tela não o puxa de volta para o último estado lido
            bool mexendo = Core.UsuarioMexendo;
            if (!mexendo)
            {
                if ((int)Brightness.Value != c.B) Brightness.Value = c.B;
                if ((int)Temperature.Value != c.T) Temperature.Value = c.T;
                BrightnessText.Text = $"{c.B}%";
                TempText.Text = Ui.TempLabel(c.T);
                ModeBar.SelectedItem = colour ? ColorItem : WhiteItem;
            }
            bool mostraCor = ModeBar.SelectedItem == ColorItem;
            WhitePanel.Visibility = mostraCor ? Visibility.Collapsed : Visibility.Visible;
            ColorBox.Visibility = mostraCor ? Visibility.Visible : Visibility.Collapsed;
            if (colour && !mexendo)
            {
                var (h, s) = Ui.ColorToHs(ColorWheel.Color);
                if (Math.Abs(h - c.H) > 2 || Math.Abs(s - c.S) > 20)
                {
                    _corDoEstado = Ui.HsToColor(c.H, c.S / 1000.0);
                    ColorWheel.Color = _corDoEstado.Value;
                }
            }

            int left = Lamp.TimerLeft(alvos);
            TimerText.Text = left > 0 ? Lamp.FmtLeft(left) : "";
            foreach (var b in Timers.Children.OfType<ToggleButton>())
                b.IsChecked = left > 0 && c.TimerOpt == int.Parse((string)b.Tag);
        }
        finally { _updating = false; }
    }

    // ---------------- controles ----------------
    private void Power_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        var alvos = Alvos();
        Task.Run(() => Lamp.TogglePower(alvos));
    }

    private void Brightness_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updating) return;
        BrightnessText.Text = $"{(int)e.NewValue}%";
        Core.SetLampBrightness((int)e.NewValue, Alvos());
    }

    private void Temperature_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updating) return;
        TempText.Text = Ui.TempLabel((int)e.NewValue);
        Core.SetLampTemperature((int)e.NewValue, Alvos());
    }

    private void ModeBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_updating) return;
        Core.SetLampMode(sender.SelectedItem == ColorItem, Alvos());
    }

    private void ColorWheel_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        // o aviso da roda chega depois, com _updating já desligado: sem esta conferência, a cor antiga voltava como comando
        if (_updating || args.NewColor == _corDoEstado) return;
        _corDoEstado = null;
        var (h, s) = Ui.ColorToHs(args.NewColor);
        Core.SetLampColour(h, s, Alvos());
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        var parts = ((string)((Button)sender).Tag).Split(',');
        int b = int.Parse(parts[0]), t = int.Parse(parts[1]);
        Core.Touch();
        var alvos = Alvos();
        Task.Run(() => Lamp.PresetOn(alvos, b, t));
    }

    private void Timer_Click(object sender, RoutedEventArgs e)
    {
        int mins = int.Parse((string)((ToggleButton)sender).Tag);
        var alvos = Alvos();
        bool active = Lamp.TimerLeft(alvos) > 0 && Lamp.Resumo(alvos).TimerOpt == mins;
        Task.Run(() => Lamp.SetTimer(alvos, active ? 0 : mins));   // clicar no ativo cancela
        Refresh();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => App.Panel?.GoBack();
}
