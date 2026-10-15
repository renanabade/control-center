using System.Text;
using WinControl.Services;

namespace WinControl;

/// <summary>Teste sem interface (--selftest): confere cada serviço contra o hardware real e grava selftest.txt.</summary>
public static class SelfTest
{
    public static void Run(bool write)
    {
        var sb = new StringBuilder();
        void P(string s) => sb.AppendLine(s);
        try
        {
            Lamp.Load();
            bool ok = Lamp.RefreshState();
            var c = Lamp.Cur;
            P($"LAMPADA leitura: ok={ok} online={c.Online} ligada={c.On} brilho={c.B}% temp={c.T} modo={c.Mode} h={c.H} s={c.S} timer={c.Timer}s");

            Screens.Start();
            Thread.Sleep(3500);
            foreach (var m in Screens.Monitors)
                P($"TELA {m.Name} ({m.Kind}) x={m.X} ok={m.Ok} brilho={m.Value}");

            P($"TEMA escuro={Theme.IsDark()} luzNoturna={Theme.NightLightOn()}");
            P($"PROJECAO atual={DisplayProjection.Label(DisplayProjection.Current())}");
            var (rok, rdesc) = Rotation.Toggle(dryRun: true);
            P($"ROTACAO teste: ok={rok} {rdesc}");

            Conforto.Load();
            P($"CONFORTO cenas={string.Join(",", Conforto.Cenas.Select(x => x?["nome"]))} agendaAtiva={Conforto.Flag("agenda_ativa")}");
            var (item, day) = Conforto.CurrentSlot();
            P($"AGENDA em vigor={item?["hora"]} ({Conforto.Describe(item!)}) desde {day}");

            if (Environment.GetCommandLineArgs().Contains("--selftest-theme"))
            {
                bool before = Theme.IsDark();
                Theme.SetDark(!before);
                P($"TEMA trocado: antes escuro={before} -> agora escuro={Theme.IsDark()}");
                Thread.Sleep(2500);
                Theme.SetDark(before);
                P($"TEMA restaurado: escuro={Theme.IsDark()}");
            }

            if (write)
            {
                P("--- escrita na lampada (volta ao estado anterior) ---");
                bool wasOn = c.On;
                P($"liga 1%/quente: {Lamp.PowerOn(1, 0)}");
                Thread.Sleep(800);
                Lamp.RefreshState();
                P($"  leitura apos ligar: ligada={c.On} brilho={c.B}% temp={c.T}");
                Lamp.ApplyWhite(3, 10);
                Lamp.RefreshState();
                P($"  apos ApplyWhite(3,10): brilho={c.B}% temp={c.T}");
                Lamp.ApplyColour(240, 1000, 3);
                Lamp.RefreshState();
                P($"  apos ApplyColour(240,1000): modo={c.Mode} h={c.H} s={c.S} brilho={c.B}%");
                Lamp.SetTimer(15);
                Lamp.RefreshState();
                P($"  apos SetTimer(15): timer={c.Timer}s");
                Lamp.SetTimer(0);
                if (!wasOn) { P($"desliga: {Lamp.PowerOff()}"); Lamp.RefreshState(); P($"  leitura final: ligada={c.On}"); }
            }
        }
        catch (Exception e) { P($"ERRO: {e}"); }
        File.WriteAllText(Path.Combine(AppPaths.Dir, "selftest.txt"), sb.ToString());
        Environment.Exit(0);
    }
}
