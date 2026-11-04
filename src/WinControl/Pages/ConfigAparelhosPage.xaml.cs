using System.Text.Json.Nodes;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;
using WinControl.Services;

namespace WinControl.Pages;

/// <summary>O que muda de um PC para outro: qual monitor gira, atalhos, modelos de periféricos e pastas de jogos.</summary>
public sealed partial class ConfigAparelhosPage : Page
{
    private bool _preenchendo = true;
    private List<Services.Rotation.Tela> _telas = [];   // "Rotation" sozinho, numa página, é a propriedade de giro do próprio WinUI

    public ConfigAparelhosPage()
    {
        InitializeComponent();
        foreach (var box in new[] { AtalhoGirar, AtalhoTema })
        {
            box.PreviewKeyDown += GravarAtalho;
            box.GotFocus += (_, _) => Hotkeys.Pausado = true;
            box.LostFocus += (_, _) => Hotkeys.Pausado = false;
        }
        Unloaded += (_, _) => Hotkeys.Pausado = false;
        Preencher();
        _preenchendo = false;
    }

    private void Avisar(InfoBarSeverity tipo, string texto)
    {
        Aviso.Severity = tipo;
        Aviso.Message = texto;
        Aviso.IsOpen = true;
    }

    private void Preencher()
    {
        _telas = Services.Rotation.Listar();
        string alvo = Conforto.Texto("monitor_girar");
        // o monitor escolhido pode estar desligado agora: continua na lista, para a escolha não se perder sozinha
        if (alvo.Length > 0 && _telas.All(t => !t.Id.Equals(alvo, StringComparison.OrdinalIgnoreCase)))
            _telas.Add(new(alvo, $"{alvo} (não conectado)"));
        Monitores.Items.Clear();
        // tela de notebook costuma não informar nome: aí o código do modelo aparece uma vez só
        foreach (var t in _telas) Monitores.Items.Add(t.Nome == t.Id ? $"Tela {t.Id}" : $"{t.Nome} ({t.Id})");
        Monitores.SelectedIndex = _telas.FindIndex(t => t.Id.Equals(alvo, StringComparison.OrdinalIgnoreCase));
        Monitores.PlaceholderText = _telas.Count == 1 ? "A única tela conectada" : "Escolha um monitor";
        Sentido.SelectedIndex = Conforto.Inteiro("girar_sentido", 90) == 270 ? 1 : 0;

        AtalhoGirar.Text = Conforto.Texto("atalho_girar");
        AtalhoTema.Text = Conforto.Texto("atalho_tema");

        var achados = new List<string>();
        if (Perifericos.MouseOk) achados.Add(Perifericos.NomeMouse);
        if (Perifericos.TecladoOk) achados.Add(Perifericos.NomeTeclado);
        achados.AddRange(Perifericos.Dinamicos.Select(d => $"{d} (Iluminação Dinâmica)"));
        PerifericosLinha.Text = achados.Count > 0 ? $"Encontrados agora: {string.Join(", ", achados)}" : "Nenhum aparelho compatível encontrado agora.";
        MousesBox.Text = string.Join(", ", Perifericos.Cfg.MousesExtras);
        TecladosBox.Text = string.Join(", ", Perifericos.Cfg.TecladosExtras);

        PastasBox.Text = string.Join("\r", Jogos.Db.PastasExtras);
        ModoPadrao.Items.Clear();
        foreach (var m in Enum.GetValues<DualSense.Modo>()) ModoPadrao.Items.Add(DualSense.Label(m));
        ModoPadrao.SelectedIndex = Array.IndexOf(Enum.GetValues<DualSense.Modo>(), Jogos.Db.Padrao);
    }

    // ---------------- monitor ----------------
    private void Monitores_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_preenchendo || Monitores.SelectedIndex < 0) return;
        Conforto.Definir("monitor_girar", _telas[Monitores.SelectedIndex].Id);
    }

    private void Sentido_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_preenchendo) return;
        Conforto.Definir("girar_sentido", Sentido.SelectedIndex == 1 ? 270 : 90);
    }

    private void GirarAgora_Click(object sender, RoutedEventArgs e) => App.Tray?.RotateNow();

    // ---------------- atalhos ----------------
    private static bool Segurando(VirtualKey k) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(CoreVirtualKeyStates.Down);

    private void GravarAtalho(object sender, KeyRoutedEventArgs e)
    {
        var box = (TextBox)sender;
        var k = e.Key;
        if (k == VirtualKey.Tab) return;   // Tab continua saindo do campo
        e.Handled = true;
        if (k == VirtualKey.Escape) { box.Text = Conforto.Texto((string)box.Tag); return; }
        if (k is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu
            or VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift or VirtualKey.LeftWindows or VirtualKey.RightWindows) return;

        var atalho = new Hotkeys.Atalho(Segurando(VirtualKey.Control), Segurando(VirtualKey.Menu), Segurando(VirtualKey.Shift),
                                        Segurando(VirtualKey.LeftWindows) || Segurando(VirtualKey.RightWindows), (int)k);
        if (!atalho.Ctrl && !atalho.Alt && !atalho.Shift && !atalho.Win)
        {
            Avisar(InfoBarSeverity.Warning, "Use a tecla junto com Ctrl, Alt, Shift ou Win: sozinha, ela dispararia enquanto você digita.");
            return;
        }
        string texto = Hotkeys.Texto(atalho), outro = (string)box.Tag == "atalho_girar" ? "atalho_tema" : "atalho_girar";
        if (Conforto.Texto(outro).Equals(texto, StringComparison.OrdinalIgnoreCase))
        {
            Avisar(InfoBarSeverity.Warning, "Esta combinação já está no outro atalho.");
            return;
        }
        box.Text = texto;
        Conforto.Definir((string)box.Tag, texto);
        Aviso.IsOpen = false;
    }

    private void DesligarAtalho_Click(object sender, RoutedEventArgs e)
    {
        string chave = (string)((Button)sender).Tag;
        Conforto.Definir(chave, "");
        (chave == "atalho_girar" ? AtalhoGirar : AtalhoTema).Text = "";
    }

    // ---------------- periféricos ----------------
    private static List<string> Separar(string texto) =>
        texto.Split(new[] { ',', ';', ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();

    private void SalvarPerifericos_Click(object sender, RoutedEventArgs e)
    {
        var mouses = Separar(MousesBox.Text);
        var teclados = Separar(TecladosBox.Text);
        if (mouses.FirstOrDefault(i => !Perifericos.IdValido(i, comTransacao: true)) is { } m)
        {
            Avisar(InfoBarSeverity.Warning, $"\"{m}\" não está no formato 1532:0084:3F.");
            return;
        }
        if (teclados.FirstOrDefault(i => !Perifericos.IdValido(i, comTransacao: false)) is { } t)
        {
            Avisar(InfoBarSeverity.Warning, $"\"{t}\" não está no formato 320F:5000.");
            return;
        }
        Perifericos.Cfg.MousesExtras = mouses;
        Perifericos.Cfg.TecladosExtras = teclados;
        Perifericos.Changed();
        Avisar(InfoBarSeverity.Success, "Salvo. O app vai procurar estes aparelhos agora.");
    }

    // ---------------- jogos ----------------
    private void SalvarJogos_Click(object sender, RoutedEventArgs e)
    {
        var pastas = PastasBox.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
        if (pastas.FirstOrDefault(p => !Directory.Exists(p)) is { } faltando)
        {
            Avisar(InfoBarSeverity.Warning, $"A pasta \"{faltando}\" não existe.");
            return;
        }
        Jogos.Definir(pastas, Enum.GetValues<DualSense.Modo>()[Math.Max(0, ModoPadrao.SelectedIndex)]);
        Avisar(InfoBarSeverity.Success, "Salvo.");
    }
}
