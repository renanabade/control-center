using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using WinControl.Services;

namespace WinControl.Pages;

public sealed partial class DualSensePage : Page
{
    private bool _updating;

    public DualSensePage()
    {
        InitializeComponent();
        Loaded += (_, _) => Bus.Changed += OnHub;
        Unloaded += (_, _) => Bus.Changed -= OnHub;
    }

    private string? _wheelFor;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _updating = true;
        Intensity.Value = DualSense.Cfg.Intensidade;
        _updating = false;
        _wheelFor = null;
        Refresh();
    }

    // a roda mostra a cor que vale agora; só é redesenhada quando muda o modo ou a opção, não a cada arrasto
    private void SyncWheel()
    {
        string key = DualSense.Cfg.CorPorModo ? DualSense.Efetivo.ToString() : "única";
        if (key == _wheelFor) return;
        _wheelFor = key;
        var (r, g, b) = DualSense.BaseColor();
        ColorWheel.Color = Windows.UI.Color.FromArgb(255, r, g, b);
    }

    private void OnHub() => Ui.Run(Refresh);

    private void Refresh()
    {
        _updating = true;
        try
        {
            Status.Text = DualSense.Status();
            IntensityText.Text = $"{DualSense.Cfg.Intensidade}%";
            PlayerLeds.IsOn = DualSense.Cfg.LedsJogador;
            PerMode.IsOn = DualSense.Cfg.CorPorModo;            ColorLabel.Text = DualSense.Cfg.CorPorModo ? $"Cor por modo · editando {DualSense.Label(DualSense.Efetivo)}" : "Cor por modo";
            SyncWheel();
            var m = DualSense.Efetivo;
            var jogo = Jogos.Atual;
            Auto.IsOn = DualSense.Cfg.Automatico;
            GameLine.Visibility = Forget.Visibility = DualSense.Cfg.Automatico ? Visibility.Visible : Visibility.Collapsed;
            Forget.IsEnabled = jogo != null;
            GameLine.Text = jogo is { } a
                ? $"{a.jogo.Nome} → {DualSense.Label(a.jogo.Modo)} ({Jogos.OrigemLabel(a.jogo.Origem)})"
                : "Nenhum jogo aberto: fica no nativo.";
            ModeTitle.Text = DualSense.Cfg.Automatico ? (jogo != null ? "Modo deste jogo" : "Emular como (desliga o automático)") : "Emular como";
            ModeNative.IsChecked = m == DualSense.Modo.DualSense;
            ModeX360.IsChecked = m == DualSense.Modo.Xbox360;
            ModePs4.IsChecked = m == DualSense.Modo.Ps4;
            Hint.Text = DualSense.Problema == "ViGEmBus não instalado"
                ? "Para emular, instale o ViGEmBus (veja docs/dualsense.md)."
                : m != DualSense.Modo.DualSense && !DualSense.HidHideInstalled
                    ? "Sem o HidHide, o jogo pode ver dois controles."
                    : "";
        }
        finally { _updating = false; }
    }

    private void Intensity_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updating) return;
        IntensityText.Text = $"{(int)e.NewValue}%";
        DualSense.SetIntensity((int)e.NewValue);
    }

    private void ColorWheel_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_updating) return;
        DualSense.SetColor(args.NewColor.R, args.NewColor.G, args.NewColor.B);
    }

    private void Mode_Click(object sender, RoutedEventArgs e) =>
        DualSense.SetModo(Enum.Parse<DualSense.Modo>((string)((ToggleButton)sender).Tag));

    private void PerMode_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_updating) DualSense.SetCorPorModo(PerMode.IsOn);
    }

    private void Auto_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_updating) DualSense.SetAutomatico(Auto.IsOn);
    }

    private void Forget_Click(object sender, RoutedEventArgs e) => Task.Run(Jogos.Esquecer);

    private void PlayerLeds_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_updating) DualSense.SetPlayerLeds(PlayerLeds.IsOn);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => App.Panel?.GoBack();
}
