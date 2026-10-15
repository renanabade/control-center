using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using WinControl.Services;

namespace WinControl.Pages;

public sealed partial class ProjectPage : Page
{
    public ProjectPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) => ShowCurrent(DisplayProjection.Current());

    private void ShowCurrent(DisplayProjection.Mode? mode)
    {
        PcOnly.IsChecked = mode == DisplayProjection.Mode.PcScreenOnly;
        Extend.IsChecked = mode == DisplayProjection.Mode.Extend;
        SecondOnly.IsChecked = mode == DisplayProjection.Mode.SecondScreenOnly;
    }

    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        var mode = Enum.Parse<DisplayProjection.Mode>((string)((ToggleButton)sender).Tag);
        Task.Run(() =>
        {
            bool ok = DisplayProjection.Apply(mode);
            Log.Write($"Projetar: {DisplayProjection.Label(mode)} ({(ok ? "ok" : "falhou")})");
            Thread.Sleep(800);   // o Windows leva um instante para refletir a nova topologia
            Ui.Run(() => ShowCurrent(DisplayProjection.Current()));
            Bus.Notify();
        });
    }

    private void Back_Click(object sender, RoutedEventArgs e) => App.Panel?.GoBack();
}
