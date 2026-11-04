using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinControl.Services;

namespace WinControl.Pages;

/// <summary>Cenas, agenda e predefinições, para não precisar editar o conforto.json à mão.</summary>
public sealed partial class ConfigCenasPage : Page
{
    private bool _preenchendo = true;
    private string? _cenaAtual;         // null: cena nova, ainda não salva
    private JsonObject? _telasPorTela;  // valores por tela não têm campo aqui: ficam guardados como estavam

    /// <summary>Uma linha da agenda. "Extra" guarda o que a tela não mostra (tema, Luz Noturna), para não se perder ao salvar.</summary>
    private sealed record LinhaAgenda(Grid Raiz, TextBox Hora, ComboBox Tipo, NumberBox Telas, ComboBox Cena, JsonObject Extra);
    private readonly List<LinhaAgenda> _linhas = [];
    private readonly List<(TextBox nome, NumberBox brilho, NumberBox temp)> _predefinicoes = [];

    public ConfigCenasPage()
    {
        InitializeComponent();
        CarregarCenas(null);
        AgendaAtiva.IsOn = Conforto.Flag("agenda_ativa");
        foreach (var item in Conforto.Sorted()) AdicionarLinha(item);
        foreach (var p in Conforto.Predefinicoes) AdicionarPredefinicao(p);
        LuzLampada.IsOn = Conforto.Flag("luz_noturna_lampada");
        LuzBrilho.Value = Conforto.Inteiro("luz_noturna_brilho", 1);
        LuzTemp.Value = Conforto.Inteiro("luz_noturna_temperatura", 0);
        _preenchendo = false;
        AtualizarLuz();
    }

    private void Avisar(InfoBarSeverity tipo, string texto)
    {
        Aviso.Severity = tipo;
        Aviso.Message = texto;
        Aviso.IsOpen = true;
    }

    private static string[] NomesDasCenas() => Conforto.Cenas.OfType<JsonObject>().Select(c => c["nome"]?.ToString() ?? "").Where(n => n.Length > 0).ToArray();

    // ---------------- cenas ----------------
    private void CarregarCenas(string? escolher)
    {
        bool antes = _preenchendo;
        _preenchendo = true;
        var nomes = NomesDasCenas();
        ListaCenas.Items.Clear();
        foreach (var n in nomes) ListaCenas.Items.Add(n);
        int i = Array.IndexOf(nomes, escolher);
        ListaCenas.SelectedIndex = i >= 0 ? i : nomes.Length > 0 ? 0 : -1;
        _preenchendo = antes;
        PreencherCena(ListaCenas.SelectedIndex >= 0 ? nomes[ListaCenas.SelectedIndex] : null);
    }

    private void ListaCenas_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_preenchendo && ListaCenas.SelectedItem is string n) PreencherCena(n);
    }

    private void PreencherCena(string? nome)
    {
        bool antes = _preenchendo;
        _preenchendo = true;
        _cenaAtual = nome;
        var c = nome == null ? null : Conforto.CenaPorNome(nome);
        NomeCena.Text = nome ?? "";
        RemoverCena.IsEnabled = nome != null;

        var lp = c?["lampada"] as JsonObject;
        LampadaModo.SelectedIndex = lp == null ? 0 : lp["ligada"]?.GetValue<bool>() == false ? 2 : 1;
        LampadaBrilho.Value = lp?["brilho"]?.GetValue<int>() ?? 50;
        LampadaTemp.Value = lp?["temperatura"]?.GetValue<int>() ?? 50;

        _telasPorTela = c?["telas"] as JsonObject;
        TelasModo.SelectedIndex = c?["telas"] == null ? 0 : _telasPorTela != null ? 2 : 1;
        TelasValor.Value = _telasPorTela == null && c?["telas"] != null ? c["telas"]!.GetValue<int>() : 50;

        TemaCena.SelectedIndex = c?["tema"]?.ToString() switch { "escuro" => 1, "claro" => 2, _ => 0 };
        LuzCena.SelectedIndex = c?["luz_noturna"]?.GetValue<bool>() switch { true => 1, false => 2, _ => 0 };
        _preenchendo = antes;
        AtualizarCampos();
    }

    private void AtualizarCampos()
    {
        LampadaValores.Visibility = LampadaModo.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        TelasValor.Visibility = TelasModo.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LampadaModo_SelectionChanged(object sender, SelectionChangedEventArgs e) => AtualizarCampos();

    private void TelasModo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_preenchendo && TelasModo.SelectedIndex == 2 && _telasPorTela == null)
        {
            Avisar(InfoBarSeverity.Informational, "Os valores por tela vêm de \"Salvar estado atual nesta cena\": botão direito na cena, no painel.");
            TelasModo.SelectedIndex = 0;
            return;
        }
        AtualizarCampos();
    }

    private void NovaCena_Click(object sender, RoutedEventArgs e)
    {
        ListaCenas.SelectedIndex = -1;
        PreencherCena(null);
        NomeCena.Focus(FocusState.Programmatic);
    }

    private void SalvarCena_Click(object sender, RoutedEventArgs e)
    {
        string nome = NomeCena.Text.Trim();
        if (nome.Length == 0) { Avisar(InfoBarSeverity.Warning, "Dê um nome à cena."); return; }
        if (nome != _cenaAtual && Conforto.CenaPorNome(nome) != null) { Avisar(InfoBarSeverity.Warning, "Já existe uma cena com este nome."); return; }
        if (LampadaModo.SelectedIndex == 1 && (double.IsNaN(LampadaBrilho.Value) || double.IsNaN(LampadaTemp.Value)))
        {
            Avisar(InfoBarSeverity.Warning, "Preencha o brilho e a temperatura.");
            return;
        }
        if (TelasModo.SelectedIndex == 1 && double.IsNaN(TelasValor.Value)) { Avisar(InfoBarSeverity.Warning, "Preencha o brilho das telas."); return; }

        var cena = new JsonObject { ["nome"] = nome };
        if (LampadaModo.SelectedIndex == 1) cena["lampada"] = new JsonObject { ["ligada"] = true, ["brilho"] = (int)LampadaBrilho.Value, ["temperatura"] = (int)LampadaTemp.Value };
        else if (LampadaModo.SelectedIndex == 2) cena["lampada"] = new JsonObject { ["ligada"] = false };
        if (TelasModo.SelectedIndex == 1) cena["telas"] = (int)TelasValor.Value;
        else if (TelasModo.SelectedIndex == 2 && _telasPorTela != null) cena["telas"] = _telasPorTela.DeepClone();
        if (TemaCena.SelectedIndex > 0) cena["tema"] = TemaCena.SelectedIndex == 1 ? "escuro" : "claro";
        if (LuzCena.SelectedIndex > 0) cena["luz_noturna"] = LuzCena.SelectedIndex == 1;
        Conforto.SalvarCena(_cenaAtual, cena);
        CarregarCenas(nome);
        AtualizarOpcoesDeCena();
        Avisar(InfoBarSeverity.Success, $"Cena {nome} salva.");
    }

    private async void RemoverCena_Click(object sender, RoutedEventArgs e)
    {
        if (_cenaAtual is not { } nome) return;
        bool naAgenda = Conforto.Agenda.OfType<JsonObject>().Any(i => i["cena"]?.ToString() == nome);
        var d = new ContentDialog
        {
            Title = $"Remover {nome}?",
            Content = naAgenda ? "Os horários da agenda que usam esta cena também saem." : "A cena sai do painel e do menu da bandeja.",
            PrimaryButtonText = "Remover",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
        };
        if (await d.ShowAsync() != ContentDialogResult.Primary) return;
        Conforto.RemoverCena(nome);
        CarregarCenas(null);
        LinhasAgenda.Children.Clear();
        _linhas.Clear();
        foreach (var item in Conforto.Sorted()) AdicionarLinha(item);
    }

    // ---------------- agenda ----------------
    private void AgendaAtiva_Toggled(object sender, RoutedEventArgs e)
    {
        if (_preenchendo || AgendaAtiva.IsOn == Conforto.Flag("agenda_ativa")) return;
        Conforto.SetOption("agenda_ativa", AgendaAtiva.IsOn);   // reativar aplica o horário de agora
    }

    private void AdicionarLinha(JsonObject? item)
    {
        var hora = new TextBox { Text = item?["hora"]?.ToString() ?? "", PlaceholderText = "18:30", Width = 80, VerticalAlignment = VerticalAlignment.Center };
        var tipo = new ComboBox { Width = 170, VerticalAlignment = VerticalAlignment.Center };
        tipo.Items.Add("Brilho das telas");
        tipo.Items.Add("Aplicar cena");
        var telas = new NumberBox { Minimum = 0, Maximum = 100, Width = 110, VerticalAlignment = VerticalAlignment.Center, PlaceholderText = "%" };
        var cena = new ComboBox { Width = 170, VerticalAlignment = VerticalAlignment.Center };
        foreach (var n in NomesDasCenas()) cena.Items.Add(n);
        var remover = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 }, VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(remover, "Tirar este horário");

        void Mostrar()
        {
            telas.Visibility = tipo.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            cena.Visibility = tipo.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        tipo.SelectionChanged += (_, _) => Mostrar();
        var nomeCena = item?["cena"]?.ToString();
        tipo.SelectedIndex = nomeCena != null ? 1 : 0;
        if (nomeCena != null) cena.SelectedItem = nomeCena;
        if (item?["telas"] is JsonValue v) telas.Value = v.GetValue<int>();
        Mostrar();

        var raiz = new Grid { ColumnSpacing = 8 };
        var painel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        painel.Children.Add(hora);
        painel.Children.Add(tipo);
        painel.Children.Add(telas);
        painel.Children.Add(cena);
        painel.Children.Add(remover);
        raiz.Children.Add(painel);
        var linha = new LinhaAgenda(raiz, hora, tipo, telas, cena, item?.DeepClone() as JsonObject ?? new JsonObject());
        remover.Click += (_, _) => { _linhas.Remove(linha); LinhasAgenda.Children.Remove(raiz); };
        _linhas.Add(linha);
        LinhasAgenda.Children.Add(raiz);
    }

    private void AtualizarOpcoesDeCena()
    {
        var nomes = NomesDasCenas();
        foreach (var l in _linhas)
        {
            var escolhida = l.Cena.SelectedItem as string;
            l.Cena.Items.Clear();
            foreach (var n in nomes) l.Cena.Items.Add(n);
            if (escolhida != null && nomes.Contains(escolhida)) l.Cena.SelectedItem = escolhida;
        }
    }

    private void AdicionarHorario_Click(object sender, RoutedEventArgs e) => AdicionarLinha(null);

    private void SalvarAgenda_Click(object sender, RoutedEventArgs e)
    {
        var agenda = new JsonArray();
        foreach (var l in _linhas)
        {
            string hora = l.Hora.Text.Trim();
            if (!Regex.IsMatch(hora, "^([01][0-9]|2[0-3]):[0-5][0-9]$"))
            {
                Avisar(InfoBarSeverity.Warning, $"\"{hora}\" não é um horário. Use o formato 18:30.");
                return;
            }
            var item = (JsonObject)l.Extra.DeepClone();
            item["hora"] = hora;
            if (l.Tipo.SelectedIndex == 1)
            {
                if (l.Cena.SelectedItem is not string nome) { Avisar(InfoBarSeverity.Warning, $"Escolha a cena das {hora}."); return; }
                item["cena"] = nome;
                item.Remove("telas");
            }
            else
            {
                item.Remove("cena");
                if (!double.IsNaN(l.Telas.Value)) item["telas"] = (int)l.Telas.Value;
                // brilho por tela não tem campo aqui: continua como estava; sem nada para fazer, a linha não serve
                else if (item["telas"] is not JsonObject && item.Count == 1) { Avisar(InfoBarSeverity.Warning, $"Preencha o brilho das {hora}."); return; }
            }
            agenda.Add(item);
        }
        Conforto.Definir("agenda", agenda);
        Avisar(InfoBarSeverity.Success, "Agenda salva.");
    }

    // ---------------- predefinições ----------------
    private void AdicionarPredefinicao(Conforto.Predefinicao p)
    {
        var nome = new TextBox { Text = p.Nome, Width = 160, Header = "Nome" };
        var brilho = new NumberBox { Value = p.Brilho, Minimum = 1, Maximum = 100, Width = 110, Header = "Brilho" };
        var temp = new NumberBox { Value = p.Temperatura, Minimum = 0, Maximum = 100, Width = 130, Header = "Temperatura" };
        var linha = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        linha.Children.Add(nome);
        linha.Children.Add(brilho);
        linha.Children.Add(temp);
        LinhasPredefinicoes.Children.Add(linha);
        _predefinicoes.Add((nome, brilho, temp));
    }

    private void SalvarPredefinicoes_Click(object sender, RoutedEventArgs e)
    {
        var lista = new JsonArray();
        foreach (var (nome, brilho, temp) in _predefinicoes)
        {
            if (nome.Text.Trim().Length == 0 || double.IsNaN(brilho.Value) || double.IsNaN(temp.Value))
            {
                Avisar(InfoBarSeverity.Warning, "Preencha nome, brilho e temperatura de todas.");
                return;
            }
            lista.Add(new JsonObject { ["nome"] = nome.Text.Trim(), ["brilho"] = (int)brilho.Value, ["temperatura"] = (int)temp.Value });
        }
        Conforto.Definir("predefinicoes", lista);
        Avisar(InfoBarSeverity.Success, "Predefinições salvas.");
    }

    // ---------------- Luz Noturna ----------------
    private void AtualizarLuz() => LuzBrilho.IsEnabled = LuzTemp.IsEnabled = LuzLampada.IsOn;

    private void LuzLampada_Toggled(object sender, RoutedEventArgs e)
    {
        AtualizarLuz();
        if (!_preenchendo) Conforto.Definir("luz_noturna_lampada", LuzLampada.IsOn);
    }

    private void SalvarLuz_Click(object sender, RoutedEventArgs e)
    {
        if (double.IsNaN(LuzBrilho.Value) || double.IsNaN(LuzTemp.Value)) { Avisar(InfoBarSeverity.Warning, "Preencha o brilho e a temperatura."); return; }
        Conforto.Definir("luz_noturna_brilho", (int)LuzBrilho.Value);
        Conforto.Definir("luz_noturna_temperatura", (int)LuzTemp.Value);
        Avisar(InfoBarSeverity.Success, "Salvo.");
    }
}
