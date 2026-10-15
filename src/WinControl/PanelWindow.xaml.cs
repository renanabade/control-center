using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using Windows.System;
using WinControl.Pages;
using WinControl.Services;

namespace WinControl;

/// <summary>Painel flutuante no canto do relógio. Fica escondido e reaparece pelo ícone da bandeja.</summary>
public sealed partial class PanelWindow : Window
{
    private const int WidthDip = 640;
    private const int HeightDip = 410;
    private const int MarginDip = 12;
    private bool _wasActive;

    public PanelWindow()
    {
        InitializeComponent();

        // sem barra de título, fora do Alt+Tab, sempre por cima
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(true, false);
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.IsAlwaysOnTop = true;
        }
        AppWindow.IsShownInSwitchers = false;

        Activated += (_, e) =>
        {
            // some ao clicar fora, como o painel do Windows. Só depois de ter tido o foco:
            // aberto em segundo plano, o Windows pode nunca entregá-lo.
            if (e.WindowActivationState != WindowActivationState.Deactivated) _wasActive = true;
            else if (_wasActive) HidePanel();
        };

        Nav.Navigate(typeof(MainPage));
        Bus.Changed += () => Ui.Run(ApplyTheme);
        ApplyTheme();
    }

    /// <summary>O painel acompanha o tema do Windows (claro/escuro), inclusive quando ele muda com o app aberto.</summary>
    public void ApplyTheme() => Root.RequestedTheme = Theme.IsDark() ? ElementTheme.Dark : ElementTheme.Light;

    public bool IsShown => AppWindow.IsVisible;

    public void TogglePanel()
    {
        if (IsShown) HidePanel(); else ShowNearTray();
    }

    public void ShowNearTray()
    {
        ApplyTheme();
        while (Nav.CanGoBack) Nav.GoBack();
        var area = DisplayArea.Primary.WorkArea;
        double scale = GetDpiScale();
        int w = (int)(WidthDip * scale), h = (int)(HeightDip * scale), m = (int)(MarginDip * scale);
        AppWindow.MoveAndResize(new RectInt32(area.X + area.Width - w - m, area.Y + area.Height - h - m, w, h));
        _wasActive = false;
        AppWindow.Show();
        Activate();
        Core.PanelOpen = true;
        Core.PokePoller();          // lê a lâmpada agora, em vez de esperar o próximo ciclo
        Screens.Refresh();
        Bus.Notify();
    }

    public void HidePanel()
    {
        Core.PanelOpen = false;
        _wasActive = false;
        AppWindow.Hide();
    }

    public void OpenPage(Type page) =>
        Nav.Navigate(page, null, new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight });

    public void GoBack()
    {
        if (Nav.CanGoBack)
            Nav.GoBack(new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromLeft });
    }

    private double GetDpiScale()
    {
        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        return GetDpiForWindow(hwnd) / 96.0;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        if (Nav.CanGoBack) GoBack(); else HidePanel();
    }

    // ---------------- menu de configurações ----------------
    private void Menu_Opening(object? sender, object e)
    {
        MiAutostart.IsChecked = Core.AutostartEnabled();
        MiAutoTheme.IsChecked = Conforto.Flag("tema_automatico");
    }

    private void MiAutostart_Click(object sender, RoutedEventArgs e) => Core.ToggleAutostart();

    private void MiAutoTheme_Click(object sender, RoutedEventArgs e) => Core.ToggleOption("tema_automatico");

    private void MiEdit_Click(object sender, RoutedEventArgs e) => Process.Start("notepad.exe", AppPaths.Conforto);

    private void MiLog_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(AppPaths.LogFile) { UseShellExecute = true });

    private void MiExit_Click(object sender, RoutedEventArgs e) => App.Quit();
}
