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

            if (Environment.GetCommandLineArgs().Contains("--selftest-scenes"))
            {
                // só a parte da lâmpada das cenas (não mexe em tema nem brilho das telas)
                var noite = (System.Text.Json.Nodes.JsonObject)Conforto.Cenas.OfType<System.Text.Json.Nodes.JsonObject>()
                    .First(x => x["nome"]?.ToString() == "Noite")["lampada"]!;
                var trabalho = (System.Text.Json.Nodes.JsonObject)Conforto.Cenas.OfType<System.Text.Json.Nodes.JsonObject>()
                    .First(x => x["nome"]?.ToString() == "Trabalho")["lampada"]!;
                Lamp.RefreshState();
                P($"CENAS inicio: ligada={c.On}");
                Conforto.ApplyLampPart(noite, auto: false);
                Lamp.RefreshState();
                P($"  cena Noite com lampada DESLIGADA: ligada={c.On} (esperado False), combinado={Conforto.PendingLamp}");
                Conforto.ApplyLampPart(trabalho, auto: true);
                Lamp.RefreshState();
                P($"  agenda 'Trabalho' com lampada DESLIGADA: ligada={c.On} (esperado False), combinado={Conforto.PendingLamp}");
                Conforto.ApplyLampPart(noite, auto: false);
                Lamp.PowerOn();
                Lamp.RefreshState();
                P($"  ligar apos Noite: ligada={c.On} brilho={c.B}% temp={c.T} (esperado True 1% 0)");
                Conforto.ApplyLampPart(trabalho, auto: true);
                Lamp.RefreshState();
                P($"  agenda 'Trabalho' com lampada LIGADA: ligada={c.On} brilho={c.B}% temp={c.T} (esperado True 50% 50)");
                Lamp.PowerOff();
                Conforto.PendingLamp = null;
                Lamp.RefreshState();
                P($"  fim: ligada={c.On}");
                Lamp.ApplyWhite(1, 0);   // sem efeito com ela desligada; o estado guardado é restaurado abaixo
            }

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
