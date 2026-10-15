using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using WinControl.Services;

namespace WinControl.Pages;

/// <summary>Página completa da lâmpada, ligada ao estado real (Lamp.Cur) e ao envio coalescido do Core.</summary>
public sealed partial class LampPage : Page
{
    private bool _updating = true;   // ignora eventos enquanto o XAML ainda está sendo montado
    private bool _refreshQueued;

    public LampPage()
    {
        InitializeComponent();
        _updating = false;
        Loaded += (_, _) => { Bus.Changed += OnHub; Refresh(); };
        Unloaded += (_, _) => Bus.Changed -= OnHub;
    }

    private void OnHub()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Ui.Run(() => { _refreshQueued = false; Refresh(); });
    }

    private void Refresh()
    {
        _updating = true;
        try
        {
            var c = Lamp.Cur;
            bool usable = c.On && c.Online;
            bool colour = c.Mode == "colour";
            Power.IsEnabled = c.Online || c.Busy;
            Power.IsOn = c.On && c.Online;
            Status.Text = Ui.LampStatus(withTimer: false) + (!c.Online && !c.Busy ? " · tentando de novo" : "");
            Body.Opacity = usable ? 1 : 0.5;
            Body.IsHitTestVisible = usable;

            if ((int)Brightness.Value != c.B) Brightness.Value = c.B;
            BrightnessText.Text = $"{c.B}%";
            if ((int)Temperature.Value != c.T) Temperature.Value = c.T;
            TempText.Text = Ui.TempLabel(c.T);

            ModeBar.SelectedItem = colour ? ColorItem : WhiteItem;
            WhitePanel.Visibility = colour ? Visibility.Collapsed : Visibility.Visible;
            ColorBox.Visibility = colour ? Visibility.Visible : Visibility.Collapsed;
            if (colour)
            {
                var (h, s) = Ui.ColorToHs(ColorWheel.Color);
                if (Math.Abs(h - c.H) > 2 || Math.Abs(s - c.S) > 20)
                    ColorWheel.Color = Ui.HsToColor(c.H, c.S / 1000.0);
            }

            int left = Lamp.TimerLeft();
            TimerText.Text = left > 0 ? Lamp.FmtLeft(left) : "";
            foreach (var b in Timers.Children.OfType<ToggleButton>())
                b.IsChecked = left > 0 && c.TimerOpt == int.Parse((string)b.Tag);
        }
        finally { _updating = false; }
    }

    private void Power_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        Task.Run(Lamp.TogglePower);
    }

    private void Brightness_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updating) return;
        BrightnessText.Text = $"{(int)e.NewValue}%";
        Core.SetLampBrightness((int)e.NewValue);
    }

    private void Temperature_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updating) return;
        TempText.Text = Ui.TempLabel((int)e.NewValue);
        Core.SetLampTemperature((int)e.NewValue);
    }

    private void ModeBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_updating) return;
        Core.SetLampMode(sender.SelectedItem == ColorItem);
    }

    private void ColorWheel_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_updating) return;
        var (h, s) = Ui.ColorToHs(args.NewColor);
        Core.SetLampColour(h, s);
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        var parts = ((string)((Button)sender).Tag).Split(',');
        int b = int.Parse(parts[0]), t = int.Parse(parts[1]);
        Core.Touch();
        Task.Run(() => Lamp.PresetOn(b, t));
    }

    private void Timer_Click(object sender, RoutedEventArgs e)
    {
        int mins = int.Parse((string)((ToggleButton)sender).Tag);
        bool active = Lamp.TimerLeft() > 0 && Lamp.Cur.TimerOpt == mins;
        Task.Run(() => Lamp.SetTimer(active ? 0 : mins));   // clicar no ativo cancela
        Refresh();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => App.Panel?.GoBack();
}
