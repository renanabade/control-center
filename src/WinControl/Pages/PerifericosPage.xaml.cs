using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using WinControl.Services;

namespace WinControl.Pages;

public sealed partial class PerifericosPage : Page
{
    private bool _updating;
    private int? _key;   // LED da tecla sendo editada; null = a roda edita a cor do aparelho

    public PerifericosPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Bus.Changed += OnHub;
        Unloaded += (_, _) => Bus.Changed -= OnHub;
        // pega a tecla antes de qualquer controle da página (senão Espaço/Enter apertam o botão)
        AddHandler(KeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler(OnKeyDown), true);
    }

    private bool IsKeyboard => Which.SelectedItem == KeyboardItem;
    private Perifericos.Aparelho Cur => IsKeyboard ? Perifericos.Cfg.Teclado : Perifericos.Cfg.Mouse;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        Which.SelectedItem ??= MouseItem;
        Load();
    }

    private void OnHub() => Ui.Run(Refresh);

    // troca de aparelho: carrega tudo, inclusive a roda (que não é redesenhada a cada aviso, senão briga com o arrasto)
    private void Load()
    {
        if (!IsKeyboard) { _key = null; PickKey.IsChecked = false; }
        _updating = true;
        var (r, g, b) = Perifericos.Parse(_key is { } k && Cur.Teclas.TryGetValue(k, out var kc) ? kc : Cur.Cor);
        ColorWheel.Color = Windows.UI.Color.FromArgb(255, r, g, b);
        Brightness.Value = Cur.Brilho;
        _updating = false;
        Refresh();
    }

    private void Refresh()
    {
        _updating = true;
        try
        {
            Status.Text = $"Mouse: {(Perifericos.MouseOk ? "ok" : "não encontrado")} · Teclado: {(Perifericos.TecladoOk ? "ok" : "não encontrado")}";
            Follow.IsOn = Cur.SeguirControle;
            bool wheel = _key != null || !Cur.SeguirControle;
            ColorWheel.IsEnabled = wheel;
            ColorWheel.Opacity = wheel ? 1 : 0.4;
            KeysPanel.Visibility = IsKeyboard ? Visibility.Visible : Visibility.Collapsed;
            RemoveKey.IsEnabled = _key is { } sel && Cur.Teclas.ContainsKey(sel);
            string lista = string.Join(", ", Perifericos.Cfg.Teclado.Teclas.Keys.Select(Perifericos.KeyName));
            KeyLine.Text = PickKey.IsChecked == true ? "Aperte a tecla no teclado…"
                : _key is { } kk ? $"Editando: {Perifericos.KeyName(kk)} (a roda muda só ela)"
                : lista.Length > 0 ? $"Com cor própria: {lista}" : "Nenhuma tecla com cor própria.";
            BrightnessText.Text = $"{Cur.Brilho}%";
            Hint.Text = Cur.SeguirControle
                ? "Acompanha a barra do DualSense: cor por modo, cor do jogo no PS4 e a transição."
                : "Cor própria: escolha na roda.";
        }
        finally { _updating = false; }
    }

    private void Which_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => Load();

    private void Follow_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        Cur.SeguirControle = Follow.IsOn;
        Perifericos.Changed();
    }

    private void Brightness_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updating) return;
        Cur.Brilho = (int)e.NewValue;
        BrightnessText.Text = $"{Cur.Brilho}%";
        Perifericos.Changed();
    }

    private void ColorWheel_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_updating) return;
        string hex = $"#{args.NewColor.R:X2}{args.NewColor.G:X2}{args.NewColor.B:X2}";
        if (_key is { } k) Cur.Teclas[k] = hex;
        else if (!Cur.SeguirControle) Cur.Cor = hex;
        else return;
        Perifericos.Changed();
    }

    // ---------------- cor por tecla ----------------
    private void PickKey_Click(object sender, RoutedEventArgs e)
    {
        if (PickKey.IsChecked != true) _key = null;   // desmarcar = parar de editar tecla
        Focus(FocusState.Programmatic);
        Refresh();
    }

    private void OnKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (PickKey.IsChecked != true || !IsKeyboard) return;
        e.Handled = true;
        int scan = (int)e.KeyStatus.ScanCode | (e.KeyStatus.IsExtendedKey ? 0x100 : 0);
        if (!Perifericos.Teclas.TryGetValue(scan, out var t))
        {
            KeyLine.Text = $"Essa tecla não tem LED conhecido (código {scan:X3}). Tente outra.";
            return;
        }
        PickKey.IsChecked = false;
        _key = t.led;
        // tecla nova começa na cor atual do teclado, para a roda partir de algo visível
        if (!Cur.Teclas.ContainsKey(t.led)) { Cur.Teclas[t.led] = "#FFFFFF"; Perifericos.Changed(); }
        Load();
    }

    private void RemoveKey_Click(object sender, RoutedEventArgs e)
    {
        if (_key is { } k && Cur.Teclas.Remove(k)) Perifericos.Changed();
        _key = null;
        Load();
    }

    private void ClearKeys_Click(object sender, RoutedEventArgs e)
    {
        Cur.Teclas.Clear();
        _key = null;
        Perifericos.Changed();
        Load();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => App.Panel?.GoBack();
}
