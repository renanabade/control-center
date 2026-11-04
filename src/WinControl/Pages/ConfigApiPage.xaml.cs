using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinControl.Services;

namespace WinControl.Pages;

/// <summary>Sempre mostra o estado real do serviço, relido a cada poucos segundos e só com esta aba aberta.</summary>
public sealed partial class ConfigApiPage : Page
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _updating;
    private bool _ocupada;     // esperando o UAC ou o serviço: os interruptores não voltam atrás no meio
    private bool _lendo;
    private bool _preenchido;
    private int _porta = ApiServico.PortaPadrao;

    public ConfigApiPage()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => _ = Atualizar();
        Loaded += (_, _) => { _timer.Start(); _ = Atualizar(); };
        Unloaded += (_, _) => _timer.Stop();
        UsuarioBox.Text = ApiServico.Usuario;
    }

    private async Task Atualizar()
    {
        if (_lendo || _ocupada) return;
        _lendo = true;
        try
        {
            // serviço, rede e porta podem demorar: fora da thread de interface
            var (e, saude, publicas) = await Task.Run(async () =>
            {
                var est = ApiServico.Ler();
                return (est, est.Rodando ? await ApiServico.Saude(est.Porta) : null, ApiServico.RedesPublicas());
            });
            if (_ocupada) return;
            Mostrar(e, saude, publicas);
        }
        catch (Exception ex) { Log.Write($"API: erro ao atualizar a aba ({ex.Message})"); }
        finally { _lendo = false; }
    }

    private void Mostrar(ApiServico.Estado e, string? saude, List<string> publicas)
    {
        _updating = true;
        try
        {
            _porta = e.Porta;
            if (!_preenchido)
            {
                PortaBox.Value = e.Porta;
                Instalacao.IsExpanded = !e.Instalado;
                Instalacao.Header = e.Instalado ? "Reinstalar ou mudar a porta" : "Instalar";
                Instalar.Content = e.Instalado ? "Reinstalar" : "Instalar";
                _preenchido = true;
            }
            Controles.Visibility = e.Instalado ? Visibility.Visible : Visibility.Collapsed;
            Ligada.IsOn = e.Rodando;
            Sempre.IsOn = e.SempreLigada;
            Swagger.IsEnabled = e.Rodando;
            Enderecos.Text = e.Rodando ? string.Join("\n", ApiServico.Enderecos(e.Porta)) : "";
            Enderecos.Visibility = e.Rodando ? Visibility.Visible : Visibility.Collapsed;

            Status.Text = !e.Instalado ? "Não instalada"
                : !e.Rodando ? "Desligada"
                : saude == null ? "Ligando…"
                : saude == "Healthy" ? $"Ligada na porta {e.Porta}"
                : $"Ligada na porta {e.Porta}, mas sem o app: abra o Control Center de novo";

            bool avisar = e.Rodando && publicas.Count > 0;
            AvisoRede.IsOpen = avisar;
            if (avisar) AvisoRede.Message = $"{string.Join(" e ", publicas)} {(publicas.Count == 1 ? "está como rede pública" : "estão como redes públicas")}: outros aparelhos não conectam por aí. Marque como particular só uma rede em que você confia.";

            bool temScript = ApiServico.Script("instalar-servico.ps1") != null;
            Instalar.IsEnabled = temScript;
            if (!temScript) ToolTipService.SetToolTip(Instalar, "Instalar exige a pasta do projeto: o script tools\\instalar-servico.ps1 não está perto do programa.");
        }
        finally { _updating = false; }
    }

    private void Avisar(InfoBarSeverity tipo, string texto)
    {
        Aviso.Severity = tipo;
        Aviso.Message = texto;
        Aviso.IsOpen = true;
    }

    /// <summary>Espera o Windows confirmar. Mensagem de erro aparece; a tela volta ao estado real de qualquer forma.</summary>
    private async Task Comando(Func<Task<string?>> acao, string feito)
    {
        _ocupada = true;
        Controles.IsHitTestVisible = false;
        try
        {
            var erro = await acao();
            if (erro != null) Avisar(InfoBarSeverity.Warning, erro);
            else { Aviso.IsOpen = false; Log.Write($"API: {feito}"); }
        }
        catch (Exception ex) { Avisar(InfoBarSeverity.Error, ex.Message); }
        finally
        {
            _ocupada = false;
            Controles.IsHitTestVisible = true;
            await Atualizar();
        }
    }

    private async void Ligada_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updating || _ocupada) return;
        if (Ligada.IsOn) await Comando(ApiServico.Ligar, "ligada");
        else await Comando(ApiServico.Desligar, "desligada");
    }

    private async void Sempre_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updating || _ocupada) return;
        bool sempre = Sempre.IsOn;
        await Comando(() => ApiServico.DefinirSempreLigada(sempre), sempre ? "sempre ligada" : "inicio manual");
    }

    private void Instalar_Click(object sender, RoutedEventArgs e)
    {
        string usuario = UsuarioBox.Text.Trim();
        if (ApiServico.ValidarInstalacao(PortaBox.Value, usuario) is { } invalido)
        {
            Avisar(InfoBarSeverity.Warning, invalido);
            return;
        }
        if (ApiServico.Instalar((int)PortaBox.Value, usuario) is { } erro) Avisar(InfoBarSeverity.Warning, erro);
        else
        {
            Avisar(InfoBarSeverity.Informational, "Acompanhe na janela do instalador. Quando terminar, esta tela atualiza sozinha.");
            _preenchido = false;
        }
    }

    private async void Senha_Click(object sender, RoutedEventArgs e)
    {
        string usuario = UsuarioBox.Text.Trim();
        if (ApiServico.ValidarInstalacao(_porta, usuario) is { } invalido)
        {
            Avisar(InfoBarSeverity.Warning, invalido);
            return;
        }
        if (!await Confirmar("Trocar a senha?", $"O usuário passa a ser {usuario}. A senha nova aparece numa janela, uma única vez, e quem já está conectado precisa entrar de novo.", "Trocar")) return;
        if (ApiServico.TrocarSenha(usuario) is { } erro) Avisar(InfoBarSeverity.Warning, erro);
    }

    private async void Remover_Click(object sender, RoutedEventArgs e)
    {
        if (!await Confirmar("Remover a API?", "O serviço e a regra do firewall saem. A senha e o histórico continuam guardados, para o caso de instalar de novo.", "Remover")) return;
        _preenchido = false;
        await Comando(ApiServico.Remover, "removida");
    }

    private async Task<bool> Confirmar(string titulo, string texto, string acao)
    {
        var d = new ContentDialog
        {
            Title = titulo,
            Content = texto,
            PrimaryButtonText = acao,
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
        };
        return await d.ShowAsync() == ContentDialogResult.Primary;
    }

    private void Swagger_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo($"http://localhost:{_porta}/swagger") { UseShellExecute = true });

    private void Rede_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("ms-settings:network-status") { UseShellExecute = true });
}
