using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinControl.Services;

namespace WinControl;

public partial class App : Application
{
    public static PanelWindow? Panel { get; private set; }
    public static Tray? Tray { get; private set; }

    private static Mutex? _instance;
    private static EventWaitHandle? _showSignal;

    public App()
    {
        InitializeComponent();
        // qualquer erro de interface vai para o log em vez de derrubar o app calado
        UnhandledException += (_, e) =>
        {
            Log.Write($"ERRO NA INTERFACE: {e.Exception}");
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"ERRO FATAL: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Write($"ERRO EM TAREFA: {e.Exception}"); e.SetObserved(); };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var argv = Environment.GetCommandLineArgs();
        if (argv.Contains("--selftest")) { SelfTest.Run(argv.Contains("--selftest-write")); return; }

        // instância única: abrir de novo só mostra o painel da que já está rodando
        _instance = new Mutex(true, "WinControlSingleInstance", out bool first);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "WinControlShowPanel");
        if (!first)
        {
            if (!argv.Contains("--background")) _showSignal.Set();
            Environment.Exit(0);
            return;
        }

        Ui.Queue = DispatcherQueue.GetForCurrentThread();
        Core.Start();
        Panel = new PanelWindow();
        Tray = new Tray(() => Panel.TogglePanel(), Quit);

        new Thread(() =>
        {
            while (true)
            {
                _showSignal.WaitOne();
                Ui.Run(() => Panel?.ShowNearTray());
            }
        }) { IsBackground = true }.Start();

        // para desenvolvimento: abre direto numa subpágina (--page=lamp | --page=project)
        var page = argv.FirstOrDefault(a => a.StartsWith("--page="))?[7..];
        if (page != null || argv.Contains("--show")) Panel.ShowNearTray();
        if (page == "lamp") Panel!.OpenPage(typeof(Pages.LampPage));
        else if (page == "project") Panel!.OpenPage(typeof(Pages.ProjectPage));
        else if (page == "dualsense") Panel!.OpenPage(typeof(Pages.DualSensePage));
        else if (!argv.Contains("--background") && page == null) Panel.ShowNearTray();
    }

    public static void Quit()
    {
        Log.Write("control-center encerrado");
        DualSense.Stop();
        Tray?.Dispose();
        Environment.Exit(0);
    }
}
