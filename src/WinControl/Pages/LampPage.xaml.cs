using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using WinControl.Services;

namespace WinControl.Pages;

/// <summary>Página completa da lâmpada, ligada ao estado real (Lamp.Cur) e ao envio coalescido do Core.</summary>
public sealed partial class LampPage : Page
{
    private bool _updating = true;   // ignora eventos enquanto o XAML ainda está sendo montado
    private bool _refreshQueued;
    private Lampada? _alvo;          // null: todas as daqui
    private string _escolhaSig = "";

    public LampPage()
    {
        InitializeComponent();
        _updating = false;
        Loaded += (_, _) => { Bus.Changed += OnHub; Refresh(); };
        Unloaded += (_, _) => Bus.Changed -= OnHub;
    }

    private void OnHub()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Ui.Run(() => { _refreshQueued = false; Refresh(); });
    }

    /// <summary>Todas (null) ou só a escolhida. Se a escolhida não é mais deste lugar, volta para todas.</summary>
    private void AtualizarEscolha()
    {
        var aqui = Lamp.Aqui;
        if (_alvo != null && !aqui.Contains(_alvo)) _alvo = null;
        Escolha.Visibility = aqui.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        Titulo.Text = aqui.Count > 1 ? "Lâmpadas" : aqui.FirstOrDefault()?.Nome ?? "Lâmpada";
        string sig = string.Join("|", aqui.Select(l => l.Id + l.Nome));
        if (sig != _escolhaSig)
        {
            _escolhaSig = sig;
            Escolha.Items.Clear();
            Escolha.Items.Add("Todas");
            foreach (var l in aqui) Escolha.Items.Add(l.Nome);
        }
        int i = _alvo == null ? 0 : aqui.ToList().IndexOf(_alvo) + 1;
        if (Escolha.SelectedIndex != i) Escolha.SelectedIndex = i;
    }

    private void Escolha_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        int i = Escolha.SelectedIndex;
        _alvo = i > 0 && i <= Lamp.Aqui.Count ? Lamp.Aqui[i - 1] : null;
        Refresh();
    }

    private int TimerLeft() => _alvo?.TimerLeft() ?? Lamp.TimerLeft();

    private void Refresh()
    {
        _updating = true;
        try
        {
            AtualizarEscolha();
            var c = _alvo?.Cur ?? Lamp.Cur;
            bool usable = c.On && c.Online;
            bool colour = c.Mode == "colour";
            Power.IsEnabled = c.Online || c.Busy || (_alvo?.HasBle ?? Lamp.HasBle);
            Power.IsOn = c.On && c.Online;
            Status.Text = (_alvo != null ? Ui.LampStatus(_alvo, withTimer: false) : Ui.LampStatus(withTimer: false)) + (!c.Online && !c.Busy ? " · tentando de novo" : "");
            Body.Opacity = usable ? 1 : 0.5;
            Body.IsHitTestVisible = usable;

            if ((int)Brightness.Value != c.B) Brightness.Value = c.B;
            BrightnessText.Text = $"{c.B}%";
            if ((int)Temperature.Value != c.T) Temperature.Value = c.T;
            TempText.Text = Ui.TempLabel(c.T);

            ModeBar.SelectedItem = colour ? ColorItem : WhiteItem;
            WhitePanel.Visibility = colour ? Visibility.Collapsed : Visibility.Visible;
            ColorBox.Visibility = colour ? Visibility.Visible : Visibility.Collapsed;
            if (colour)
            {
                var (h, s) = Ui.ColorToHs(ColorWheel.Color);
                if (Math.Abs(h - c.H) > 2 || Math.Abs(s - c.S) > 20)
                    ColorWheel.Color = Ui.HsToColor(c.H, c.S / 1000.0);
            }

            int left = TimerLeft();
            TimerText.Text = left > 0 ? Lamp.FmtLeft(left) : "";
            foreach (var b in Timers.Children.OfType<ToggleButton>())
                b.IsChecked = left > 0 && c.TimerOpt == int.Parse((string)b.Tag);
        }
        finally { _updating = false; }
    }

    private void Power_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        var alvo = _alvo;
        Task.Run(() => { if (alvo != null) alvo.TogglePower(); else Lamp.TogglePower(); });
    }

    private void Brightness_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updating) return;
        BrightnessText.Text = $"{(int)e.NewValue}%";
        Core.SetLampBrightness((int)e.NewValue, _alvo);
    }

    private void Temperature_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updating) return;
        TempText.Text = Ui.TempLabel((int)e.NewValue);
        Core.SetLampTemperature((int)e.NewValue, _alvo);
    }

    private void ModeBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_updating) return;
        Core.SetLampMode(sender.SelectedItem == ColorItem, _alvo);
    }

    private void ColorWheel_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_updating) return;
        var (h, s) = Ui.ColorToHs(args.NewColor);
        Core.SetLampColour(h, s, _alvo);
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        var parts = ((string)((Button)sender).Tag).Split(',');
        int b = int.Parse(parts[0]), t = int.Parse(parts[1]);
        Core.Touch();
        var alvo = _alvo;
        Task.Run(() => { if (alvo != null) alvo.PresetOn(b, t); else Lamp.PresetOn(b, t); });
    }

    private void Timer_Click(object sender, RoutedEventArgs e)
    {
        int mins = int.Parse((string)((ToggleButton)sender).Tag);
        var alvo = _alvo;
        bool active = TimerLeft() > 0 && (alvo?.Cur ?? Lamp.Cur).TimerOpt == mins;
        // clicar no ativo cancela
        Task.Run(() => { if (alvo != null) alvo.SetTimer(active ? 0 : mins); else Lamp.SetTimer(active ? 0 : mins); });
        Refresh();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => App.Panel?.GoBack();
}
