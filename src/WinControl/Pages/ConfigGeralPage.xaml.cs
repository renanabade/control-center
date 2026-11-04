using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinControl.Services;

namespace WinControl.Pages;

public sealed partial class ConfigGeralPage : Page
{
    private bool _updating = true;   // ignora os eventos de quando os valores são preenchidos

    public ConfigGeralPage()
    {
        InitializeComponent();
        Autostart.IsOn = Core.AutostartEnabled();
        TemaAuto.IsOn = Conforto.Flag("tema_automatico");
        TelasJuntas.IsOn = Conforto.Flag("telas_juntas");
        Agenda.IsOn = Conforto.Flag("agenda_ativa");
        Local.Text = $"Pasta: {AppPaths.Dir}";
        _updating = false;
    }

    private void Autostart_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_updating && Autostart.IsOn != Core.AutostartEnabled()) Core.ToggleAutostart();
    }

    private void Opcao_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        var t = (ToggleSwitch)sender;
        string chave = (string)t.Tag;
        if (t.IsOn != Conforto.Flag(chave)) Core.ToggleOption(chave);
    }

    private void Cenas_Click(object sender, RoutedEventArgs e) => Process.Start("notepad.exe", AppPaths.Conforto);

    private void Pasta_Click(object sender, RoutedEventArgs e) => Process.Start("explorer.exe", AppPaths.Dir);

    private void Log_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(AppPaths.LogFile) { UseShellExecute = true });
}
