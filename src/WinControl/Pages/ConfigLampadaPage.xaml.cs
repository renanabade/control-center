using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinControl.Services;

namespace WinControl.Pages;

/// <summary>Os campos só são preenchidos ao abrir e ao recarregar: o Bus muda o status, não o que está sendo digitado.</summary>
public sealed partial class ConfigLampadaPage : Page
{
    private bool _temChave;

    public ConfigLampadaPage()
    {
        InitializeComponent();
        Preencher();
        Guia.IsExpanded = Lamp.ConfigProblem != null;
        Loaded += (_, _) => { Bus.Changed += OnBus; AtualizarStatus(); };
        Unloaded += (_, _) => Bus.Changed -= OnBus;
    }

    private void OnBus() => Ui.Run(AtualizarStatus);

    private void AtualizarStatus() => Status.Text = Ui.LampStatus(withTimer: false);

    private void Preencher()
    {
        var c = Lamp.LerConfig();
        IdBox.Text = c.Id;
        IpBox.Text = c.Ip;
        MacBox.Text = c.BleMac;
        _temChave = c.TemChave;
        ChaveBox.Password = "";   // a chave guardada nunca volta para a tela
        ChaveBox.PlaceholderText = c.TemChave ? "Guardada. Deixe vazio para manter" : "16 caracteres";
    }

    private void Avisar(InfoBarSeverity tipo, string texto)
    {
        Aviso.Severity = tipo;
        Aviso.Message = texto;
        Aviso.IsOpen = true;
    }

    private void Salvar_Click(object sender, RoutedEventArgs e)
    {
        string id = IdBox.Text.Trim(), chave = ChaveBox.Password, ip = IpBox.Text.Trim(), mac = MacBox.Text.Trim();
        if (Lamp.ValidarConfig(id, chave, ip, mac, _temChave) is { } erro)
        {
            Avisar(InfoBarSeverity.Warning, erro);
            return;
        }
        try
        {
            Lamp.SalvarConfig(id, chave, ip, mac);
            Preencher();
            Avisar(InfoBarSeverity.Success, "Salvo. Clique em Testar conexão para conferir.");
        }
        catch (Exception ex) { Avisar(InfoBarSeverity.Error, $"Não consegui salvar: {ex.Message}"); }
    }

    private async void Testar_Click(object sender, RoutedEventArgs e)
    {
        if (Lamp.ConfigProblem is { } p)
        {
            Avisar(InfoBarSeverity.Warning, $"Antes, salve a configuração: {p}.");
            return;
        }
        Testar.IsEnabled = false;
        Avisar(InfoBarSeverity.Informational, "Procurando a lâmpada…");
        bool ok = await Task.Run(Lamp.RefreshState);
        Testar.IsEnabled = true;
        Bus.Notify();
        if (ok) Avisar(InfoBarSeverity.Success, $"A lâmpada respondeu: {Ui.LampStatus(withTimer: false)}.");
        else Avisar(InfoBarSeverity.Error, Lamp.UltimoErro ?? "A lâmpada não respondeu.");
    }

    private void Recarregar_Click(object sender, RoutedEventArgs e)
    {
        Lamp.Load();
        Preencher();
        Core.PokePoller();
        Bus.Notify();
        Avisar(InfoBarSeverity.Informational, "Recarregado.");
    }
}
