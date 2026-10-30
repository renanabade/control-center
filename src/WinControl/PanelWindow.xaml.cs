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
    private DateTime _shownAt;
    private readonly DispatcherTimer _focusWatch = new() { Interval = TimeSpan.FromMilliseconds(200) };
    // escondido, o painel ainda segura a interface inteira (e o Direct3D na GPU): depois de um tempo fechado, é solto
    private readonly DispatcherTimer _release = new() { Interval = TimeSpan.FromMinutes(3) };
    private readonly Action _onBus;

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

        _focusWatch.Tick += FocusWatch_Tick;
        _release.Tick += Release_Tick;
        Nav.Navigate(typeof(MainPage));
        _onBus = () => Ui.Run(ApplyTheme);
        Bus.Changed += _onBus;
        Closed += (_, _) => { Bus.Changed -= _onBus; _focusWatch.Stop(); _release.Stop(); };
        ApplyTheme();
    }

    private void Release_Tick(object? sender, object e)
    {
        _release.Stop();
        if (IsShown) return;
        if (Nav.Content != null)
        {
            // tira as páginas da árvore primeiro: o Unloaded delas desliga o Bus; fecha no próximo tique
            Nav.BackStack.Clear();
            Nav.Content = null;
            _release.Interval = TimeSpan.FromSeconds(1);
            _release.Start();
            return;
        }
        Log.Write("painel solto da memória");
        Close();
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
        if (Nav.Content == null) Nav.Navigate(typeof(MainPage));   // reaberto no meio da soltura
        var area = DisplayArea.Primary.WorkArea;
        double scale = GetDpiScale();
        int w = (int)(WidthDip * scale), h = (int)(HeightDip * scale), m = (int)(MarginDip * scale);
        AppWindow.MoveAndResize(new RectInt32(area.X + area.Width - w - m, area.Y + area.Height - h - m, w, h));
        _wasActive = false;
        AppWindow.Show();
        Activate();
        ForceForeground();
        _shownAt = DateTime.UtcNow;
        _release.Stop();
        _focusWatch.Start();
        Core.PanelOpen = true;
        Core.PokePoller();          // lê a lâmpada agora, em vez de esperar o próximo ciclo
        Screens.Refresh();
        Bus.Notify();
    }

    /// <summary>
    /// Clicar fora fecha: o Activated nem sempre chega depois do ForceForeground, então vigia quem tem o foco.
    /// Janelas do próprio app (menus e popups do WinUI) não contam como "fora".
    /// </summary>
    private void FocusWatch_Tick(object? sender, object e)
    {
        if (!IsShown) { _focusWatch.Stop(); return; }
        if ((DateTime.UtcNow - _shownAt).TotalMilliseconds < 400) return;
        GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
        if (pid == (uint)Environment.ProcessId) _wasActive = true;
        else if (_wasActive) HidePanel();     // só depois de ter tido o foco: aberto em segundo plano, fica
    }

    public void HidePanel()
    {
        _focusWatch.Stop();
        Core.PanelOpen = false;
        _wasActive = false;
        AppWindow.Hide();
        _release.Interval = TimeSpan.FromMinutes(3);
        _release.Start();
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

    /// <summary>
    /// O clique na bandeja deixa o foco com a barra de tarefas (e o menu de ícones ocultos), e o Windows recusa o
    /// Activate: o painel abria por trás. Pegando carona na fila de entrada de quem tem o foco, ele aceita.
    /// </summary>
    private void ForceForeground()
    {
        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        uint fg = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero), me = GetCurrentThreadId();
        bool attached = fg != me && AttachThreadInput(me, fg, true);
        SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);   // HWND_TOPMOST, sem mover/redimensionar, mostrar
        BringWindowToTop(hwnd);
        SetForegroundWindow(hwnd);
        if (attached) AttachThreadInput(me, fg, false);
        // ativado com a fila emprestada, o WinUI pode não avisar o Activated: sem isso, clicar fora não fechava
        if (GetForegroundWindow() == hwnd) _wasActive = true;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr pid);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

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
