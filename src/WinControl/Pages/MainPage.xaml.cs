using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using WinControl.Services;

namespace WinControl.Pages;

public sealed partial class MainPage : Page
{
    private bool _updating;
    private bool _refreshQueued;
    private string _slidersSig = "", _scenesSig = "";
    private readonly Dictionary<string, (Slider slider, TextBlock text)> _rows = new();
    private readonly Dictionary<string, ToggleButton> _sceneButtons = new();

    public MainPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
        Loaded += (_, _) => { Bus.Changed += OnHub; Refresh(); };
        Unloaded += (_, _) => Bus.Changed -= OnHub;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) => Refresh();

    private void OnHub()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Ui.Run(() => { _refreshQueued = false; Refresh(); });
    }

    // ---------------- estado -> interface ----------------
    private void Refresh()
    {
        _updating = true;
        try
        {
            var c = Lamp.Cur;
            LampTile.IsChecked = c.On && c.Online;
            LampTile.Opacity = c.Online ? 1 : 0.5;
            ThemeTile.IsChecked = Theme.IsDark();
            AgendaTile.IsChecked = Conforto.Flag("agenda_ativa");
            JoinTile.IsChecked = Conforto.Flag("telas_juntas");
            ProjectCaption.Text = DisplayProjection.Label(DisplayProjection.Current());
            PadCaption.Text = !DualSense.Conectado ? "Controle" : DualSense.Efetivo switch
            {
                DualSense.Modo.Xbox360 => "Xbox 360",
                DualSense.Modo.Ps4 => "PS4",
                _ => "DualSense",
            };
            LampLine.Text = $"Lâmpada · {Ui.LampStatus()}";
            AgendaLine.Text = Conforto.Flag("agenda_ativa") && Conforto.NextItem() is { } nxt
                ? $"Agenda · {nxt["hora"]} → {Conforto.Describe(nxt)}"
                : "Agenda pausada";
            BuildScenes();
            BuildSliders();
        }
        finally { _updating = false; }
    }

    private void BuildScenes()
    {
        var cenas = Conforto.Cenas.OfType<JsonObject>().Take(4).ToList();
        string sig = string.Join("|", cenas.Select(x => $"{x["nome"]}:{Conforto.Describe(x)}"));
        if (sig != _scenesSig)
        {
            _scenesSig = sig;
            Scenes.Children.Clear();
            Scenes.ColumnDefinitions.Clear();
            _sceneButtons.Clear();
            for (int i = 0; i < cenas.Count; i++)
            {
                string nome = cenas[i]["nome"]?.ToString() ?? "?";
                Scenes.ColumnDefinitions.Add(new ColumnDefinition());
                var btn = new ToggleButton
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(4, 10, 4, 10),
                    Content = new StackPanel
                    {
                        Spacing = 4,
                        Children =
                        {
                            new FontIcon { Glyph = Ui.SceneGlyph(nome), FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center },
                            new TextBlock { Text = nome, HorizontalAlignment = HorizontalAlignment.Center, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] },
                        },
                    },
                };
                ToolTipService.SetToolTip(btn, $"{Conforto.Describe(cenas[i])}\nBotão direito: editar");
                btn.ContextFlyout = SceneMenu(nome, cenas[i]["tema"]?.ToString(), cenas[i]["luz_noturna"]?.GetValue<bool>());
                btn.Click += (_, _) =>
                {
                    Conforto.Cena = nome;
                    Refresh();
                    Task.Run(() => Conforto.ApplyScene(nome));
                };
                Grid.SetColumn(btn, i);
                Scenes.Children.Add(btn);
                _sceneButtons[nome] = btn;
            }
        }
        foreach (var (nome, btn) in _sceneButtons) btn.IsChecked = Conforto.Cena == nome;   // só uma marcada
    }

    /// <summary>Botão direito numa cena: escolher o tema dela ou gravar nela o estado atual.</summary>
    private static MenuFlyout SceneMenu(string nome, string? tema, bool? nightLight)
    {
        var menu = new MenuFlyout();
        foreach (var (label, value) in new[] { ("Tema escuro", "escuro"), ("Tema claro", "claro"), ("Não mudar o tema", (string?)null) })
        {
            var it = new RadioMenuFlyoutItem { Text = label, GroupName = "tema", IsChecked = tema == value };
            it.Click += (_, _) => Conforto.SetSceneTheme(nome, value);
            menu.Items.Add(it);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var nl = new ToggleMenuFlyoutItem { Text = "Ligar a Luz Noturna", IsChecked = nightLight == true };
        nl.Click += (_, _) => Conforto.SetSceneNightLight(nome, nl.IsChecked);
        menu.Items.Add(nl);
        menu.Items.Add(new MenuFlyoutSeparator());
        var save = new MenuFlyoutItem { Text = "Salvar estado atual nesta cena", Icon = new FontIcon { Glyph = "" } };
        save.Click += (_, _) => Conforto.SaveSceneFromCurrent(nome);
        menu.Items.Add(save);
        return menu;
    }

    private void BuildSliders()
    {
        var mons = Screens.Monitors;
        string sig = "lamp|" + string.Join("|", mons.Select(m => $"{m.Key}:{m.Name}:{m.Ok}"));
        if (sig != _slidersSig)
        {
            _slidersSig = sig;
            Sliders.Children.Clear();
            _rows.Clear();
            AddRow("lamp", "Lâmpada", "", enabled: true, v => Core.SetLampBrightness(v), min: 1);
            foreach (var m in mons)
            {
                string key = m.Key;
                AddRow(key, m.Ok ? m.Name : $"{m.Name} (sem DDC/CI)", m.Kind == "wmi" ? "" : "",
                       m.Ok, v => Core.SetScreenBrightness(v, key));
            }
        }
        var c = Lamp.Cur;
        SetRow("lamp", c.B, c.On && c.Online);
        foreach (var m in mons) SetRow(m.Key, m.Value ?? 0, m.Ok);
    }

    private void AddRow(string id, string label, string glyph, bool enabled, Action<int> onChange, int min = 0)
    {
        var slider = new Slider { Minimum = min, Maximum = 100, IsEnabled = enabled, VerticalAlignment = VerticalAlignment.Center };
        var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Width = 40, TextAlignment = Microsoft.UI.Xaml.TextAlignment.Right };
        slider.ValueChanged += (_, e) => { if (!_updating) { text.Text = $"{(int)e.NewValue}%"; onChange((int)e.NewValue); } };
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new FontIcon { Glyph = glyph, FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(slider, 1); Grid.SetColumn(text, 2);
        row.Children.Add(icon); row.Children.Add(slider); row.Children.Add(text);
        Sliders.Children.Add(new StackPanel
        {
            Spacing = 0,
            Children =
            {
                new TextBlock { Text = label, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] },
                row,
            },
        });
        _rows[id] = (slider, text);
    }

    private void SetRow(string id, int value, bool enabled)
    {
        if (!_rows.TryGetValue(id, out var r)) return;
        r.slider.IsEnabled = enabled;
        if ((int)r.slider.Value != value) r.slider.Value = value;
        r.text.Text = enabled ? $"{value}%" : "—";
    }

    // ---------------- cliques ----------------
    private void LampTile_Click(object sender, RoutedEventArgs e)
    {
        Task.Run(Lamp.TogglePower);
        Refresh();
    }

    private void OpenLamp_Click(object sender, RoutedEventArgs e) => App.Panel?.OpenPage(typeof(LampPage));

    private void OpenProject_Click(object sender, RoutedEventArgs e) => App.Panel?.OpenPage(typeof(ProjectPage));

    private void OpenDualSense_Click(object sender, RoutedEventArgs e) => App.Panel?.OpenPage(typeof(DualSensePage));

    private void Rotate_Click(object sender, RoutedEventArgs e) => App.Tray?.RotateNow();

    private void ThemeTile_Click(object sender, RoutedEventArgs e) => Core.SetDark(ThemeTile.IsChecked == true);

    private void AgendaTile_Click(object sender, RoutedEventArgs e) => Core.ToggleOption("agenda_ativa");

    private void JoinTile_Click(object sender, RoutedEventArgs e) => Core.ToggleOption("telas_juntas");
}
