using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinControl.Services;

namespace WinControl.Pages;

/// <summary>Os campos só são preenchidos ao escolher ou recarregar: o Bus muda o status, não o que está sendo digitado.</summary>
public sealed partial class ConfigLampadaPage : Page
{
    private bool _temChave;
    private bool _preenchendo;
    private List<Lampada> _lampadas = [];
    private Lampada? _atual;     // null: lâmpada nova, ainda não salva

    public ConfigLampadaPage()
    {
        InitializeComponent();
        CarregarLista(null);
        Guia.IsExpanded = Lamp.Todas.Count == 0;
        Loaded += (_, _) => { Bus.Changed += OnBus; AtualizarStatus(); };
        Unloaded += (_, _) => Bus.Changed -= OnBus;
    }

    private void OnBus() => Ui.Run(AtualizarStatus);

    private void AtualizarStatus()
    {
        int t = Lamp.Todas.Count, a = Lamp.Aqui.Count;
        Status.Text = t == 0 ? "Nenhuma configurada" : $"{t} configurada(s), {a} nesta rede · {Ui.LampStatus(withTimer: false)}";
        if (_atual != null)
            Detalhe.Text = $"{Ui.LampStatus(_atual, withTimer: false)} · protocolo {_atual.Version:0.0} · {(_atual.Ip.Length > 0 ? _atual.Ip : "IP ainda não encontrado")}";
    }

    private void CarregarLista(string? escolher)
    {
        _preenchendo = true;
        _lampadas = Lamp.Todas.ToList();
        Lista.Items.Clear();
        foreach (var l in _lampadas) Lista.Items.Add(Lamp.Aqui.Contains(l) ? l.Nome : $"{l.Nome} (outra rede)");
        int i = _lampadas.FindIndex(l => l.Id == escolher);
        Lista.SelectedIndex = i >= 0 ? i : _lampadas.Count > 0 ? 0 : -1;
        _preenchendo = false;
        Preencher(Lista.SelectedIndex >= 0 ? _lampadas[Lista.SelectedIndex] : null);
    }

    private void Lista_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_preenchendo && Lista.SelectedIndex >= 0) Preencher(_lampadas[Lista.SelectedIndex]);
    }

    private void Preencher(Lampada? l)
    {
        _atual = l;
        var c = l == null ? null : Lamp.LerConfig(l);
        NomeBox.Text = c?.Nome ?? "";
        IdBox.Text = c?.Id ?? "";
        IpBox.Text = c?.Ip ?? "";
        MacBox.Text = c?.BleMac ?? "";
        NestaRede.IsOn = c?.NestaRede ?? true;
        _temChave = c?.TemChave ?? false;
        ChaveBox.Password = "";   // a chave guardada nunca volta para a tela
        ChaveBox.PlaceholderText = _temChave ? "Guardada. Deixe vazio para manter" : "16 caracteres";
        Remover.IsEnabled = l != null;
        Testar.IsEnabled = l != null;
        Detalhe.Text = l == null ? "Lâmpada nova" : "";
        AtualizarStatus();
    }

    private void Avisar(InfoBarSeverity tipo, string texto)
    {
        Aviso.Severity = tipo;
        Aviso.Message = texto;
        Aviso.IsOpen = true;
    }

    private void Nova_Click(object sender, RoutedEventArgs e)
    {
        Lista.SelectedIndex = -1;
        Preencher(null);
        NomeBox.Focus(FocusState.Programmatic);
    }

    private void Salvar_Click(object sender, RoutedEventArgs e)
    {
        string nome = NomeBox.Text.Trim(), id = IdBox.Text.Trim(), chave = ChaveBox.Password, ip = IpBox.Text.Trim(), mac = MacBox.Text.Trim();
        string? erro = Lamp.ValidarConfig(id, chave, ip, mac, _temChave);
        if (erro == null && Lamp.IdEmUso(id, _atual?.Id)) erro = "Já existe uma lâmpada com este ID.";
        if (erro != null)
        {
            Avisar(InfoBarSeverity.Warning, erro);
            return;
        }
        try
        {
            Lamp.SalvarConfig(_atual?.Id, id, nome, chave, ip, mac, NestaRede.IsOn);
            CarregarLista(id);
            Avisar(InfoBarSeverity.Success, "Salvo. Clique em Testar conexão para conferir.");
        }
        catch (Exception ex) { Avisar(InfoBarSeverity.Error, $"Não consegui salvar: {ex.Message}"); }
    }

    private async void Remover_Click(object sender, RoutedEventArgs e)
    {
        if (_atual is not { } l) return;
        var d = new ContentDialog
        {
            Title = $"Remover {l.Nome}?",
            Content = "Ela sai do Control Center, e a chave dela é apagada deste PC. A lâmpada continua funcionando no Smart Life.",
            PrimaryButtonText = "Remover",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
        };
        if (await d.ShowAsync() != ContentDialogResult.Primary) return;
        Lamp.Remover(l.Id);
        CarregarLista(null);
        Avisar(InfoBarSeverity.Informational, "Removida.");
    }

    private async void Testar_Click(object sender, RoutedEventArgs e)
    {
        if (_atual is not { } l) return;
        Testar.IsEnabled = false;
        Avisar(InfoBarSeverity.Informational, $"Procurando {l.Nome}…");
        bool ok = await Task.Run(() => l.RefreshState());
        Testar.IsEnabled = true;
        Bus.Notify();
        if (ok) Avisar(InfoBarSeverity.Success, $"{l.Nome} respondeu: {Ui.LampStatus(l, withTimer: false)}.");
        else Avisar(InfoBarSeverity.Error, l.UltimoErro ?? "A lâmpada não respondeu.");
        AtualizarStatus();
    }

    private void Recarregar_Click(object sender, RoutedEventArgs e)
    {
        Lamp.Load();
        CarregarLista(_atual?.Id);
        Core.PokePoller();
        Bus.Notify();
        Avisar(InfoBarSeverity.Informational, "Recarregado.");
    }
}
