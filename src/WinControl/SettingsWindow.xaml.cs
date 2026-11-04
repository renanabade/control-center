using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinControl.Pages;
using WinControl.Services;

namespace WinControl;

/// <summary>
/// Janela normal, não o painel: o painel some ao perder o foco, e aqui é preciso ir a outro programa copiar uma chave
/// sem perder o que já foi digitado. Existe só enquanto está aberta.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private readonly Action _onBus;

    public SettingsWindow()
    {
        InitializeComponent();
        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        double escala = GetDpiForWindow(hwnd) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(880 * escala), (int)(680 * escala)));
        string icone = Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico");
        if (File.Exists(icone)) AppWindow.SetIcon(icone);

        _onBus = () => Ui.Run(AplicarTema);
        Bus.Changed += _onBus;
        Closed += (_, _) => Bus.Changed -= _onBus;
        AplicarTema();
    }

    private void AplicarTema()
    {
        bool escuro = Theme.IsDark();
        Root.RequestedTheme = escuro ? ElementTheme.Dark : ElementTheme.Light;
        // a barra de título é do sistema: sem isto, ficava clara sobre o conteúdo escuro
        AppWindow.TitleBar.PreferredTheme = escuro ? TitleBarTheme.Dark : TitleBarTheme.Light;
    }

    public void Mostrar(string aba)
    {
        Abas.SelectedItem = aba switch { "lampada" => AbaLampada, "cenas" => AbaCenas, "aparelhos" => AbaAparelhos, "api" => AbaApi, _ => AbaGeral };
        Activate();
        SetForegroundWindow(Win32Interop.GetWindowFromWindowId(AppWindow.Id));
    }

    private void Abas_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var pagina = (args.SelectedItem as NavigationViewItem)?.Tag as string switch
        {
            "lampada" => typeof(ConfigLampadaPage),
            "cenas" => typeof(ConfigCenasPage),
            "aparelhos" => typeof(ConfigAparelhosPage),
            "api" => typeof(ConfigApiPage),
            _ => typeof(ConfigGeralPage),
        };
        if (Conteudo.CurrentSourcePageType != pagina) Conteudo.Navigate(pagina);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
}
