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
        var versao = typeof(Core).Assembly.GetName().Version;
        Local.Text = $"Versão {versao?.ToString(3)} · Pasta: {AppPaths.Dir}";
        _updating = false;
    }

    private void Avisar(InfoBarSeverity tipo, string texto)
    {
        Aviso.Severity = tipo;
        Aviso.Message = texto;
        Aviso.IsOpen = true;
    }

    /// <summary>Pede a senha num diálogo. Devolve null se cancelar.</summary>
    private async Task<string?> PedirSenha(string titulo, string texto, bool confirmar)
    {
        var senha = new PasswordBox { Header = "Senha" };
        var outra = new PasswordBox { Header = "Repita a senha" };
        var corpo = new StackPanel { Spacing = 10 };
        corpo.Children.Add(new TextBlock { Text = texto, TextWrapping = TextWrapping.Wrap });
        corpo.Children.Add(senha);
        if (confirmar) corpo.Children.Add(outra);
        var d = new ContentDialog
        {
            Title = titulo,
            Content = corpo,
            PrimaryButtonText = "Continuar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
        };
        if (await d.ShowAsync() != ContentDialogResult.Primary) return null;
        if (confirmar && senha.Password != outra.Password)
        {
            Avisar(InfoBarSeverity.Warning, "As senhas não são iguais.");
            return null;
        }
        return senha.Password;
    }

    private async void Exportar_Click(object sender, RoutedEventArgs e)
    {
        var senha = await PedirSenha("Exportar a configuração", $"Crie uma senha de pelo menos {Backup.SenhaMinima} caracteres. Ela será pedida no outro PC, e não há como recuperá-la.", confirmar: true);
        if (senha == null) return;
        if (senha.Length < Backup.SenhaMinima)
        {
            Avisar(InfoBarSeverity.Warning, $"A senha precisa de pelo menos {Backup.SenhaMinima} caracteres.");
            return;
        }
        try
        {
            var (caminho, lampadas) = await Task.Run(() => Backup.Exportar(senha));
            Avisar(InfoBarSeverity.Success, $"Pronto: {lampadas} lâmpada(s) em {caminho}");
        }
        catch (Exception ex) { Avisar(InfoBarSeverity.Error, $"Não consegui exportar: {ex.Message}"); }
    }

    private async void Importar_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop };
            picker.FileTypeFilter.Add(Backup.Extensao);
            // janela sem pacote: o seletor precisa saber de qual janela ele é
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Settings!));
            var arquivo = await picker.PickSingleFileAsync();
            if (arquivo == null) return;
            var senha = await PedirSenha("Importar a configuração", "O que já existe neste PC é substituído, e fica uma cópia na pasta de configuração.", confirmar: false);
            if (senha == null) return;
            int gravados = await Task.Run(() => Backup.Importar(arquivo.Path, senha));
            Lamp.Load();
            Conforto.Load();
            Bus.Notify();
            Avisar(InfoBarSeverity.Success, $"Importado: {Lamp.Todas.Count} lâmpada(s), {Lamp.Aqui.Count} nesta rede. Feche e abra o app para aplicar também o controle e os periféricos.");
        }
        catch (Backup.Erro ex) { Avisar(InfoBarSeverity.Warning, ex.Message); }
        catch (Exception ex) { Avisar(InfoBarSeverity.Error, $"Não consegui importar: {ex.Message}"); }
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
