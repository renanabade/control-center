using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using ControlCenter.Contracts;

namespace WinControl.Services;

/// <summary>
/// Ponte para a ControlCenter.Api. O serviço do Windows roda na sessão 0, sem o HKCU do usuário nem acesso aos
/// monitores, então quem mexe nos aparelhos continua sendo este processo. Parado, o pipe só espera conexão: não
/// gasta CPU. Os comandos usam os mesmos métodos do painel, e as regras das cenas valem igual para a API.
/// </summary>
public static class ApiBridge
{
    private const int MaxConexoes = 4;

    private sealed class ErroPonte(string codigo, string mensagem) : Exception(mensagem)
    {
        public string Codigo { get; } = codigo;
    }

    private static readonly object Trava = new();
    private static CancellationTokenSource? _parar;

    public static void Start()
    {
        lock (Trava)
        {
            if (_parar != null) return;
            var parar = new CancellationTokenSource();
            _parar = parar;
            Task.Run(() => AcceptLoop(parar.Token));
        }
    }

    /// <summary>Fecha o pipe. Um pedido já em andamento termina sozinho.</summary>
    public static void Stop()
    {
        lock (Trava)
        {
            _parar?.Cancel();
            _parar = null;
        }
    }

    /// <summary>Só o usuário e o LocalService (a conta do serviço) entram, e nunca pela rede.</summary>
    private static PipeSecurity Seguranca()
    {
        var s = new PipeSecurity();
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        s.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        // CreateNewInstance porque o cliente abre com GENERIC_WRITE, que em pipes inclui esse direito; sem ele, acesso negado
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance | PipeAccessRights.Synchronize, AccessControlType.Allow));
        return s;
    }

    private static NamedPipeServerStream Criar(bool primeira) => NamedPipeServerStreamAcl.Create(
        ProtocoloPonte.NomePipe, PipeDirection.InOut, MaxConexoes, PipeTransmissionMode.Byte,
        // FirstPipeInstance: se outro processo já criou um pipe com este nome, falha em vez de dividir o nome com ele
        PipeOptions.Asynchronous | (primeira ? PipeOptions.FirstPipeInstance : PipeOptions.None),
        0, 0, Seguranca());

    private static async Task AcceptLoop(CancellationToken parar)
    {
        bool primeira = true;
        try
        {
            while (!parar.IsCancellationRequested)
            {
                NamedPipeServerStream pipe;
                try
                {
                    pipe = Criar(primeira);
                    primeira = false;
                }
                catch (Exception e)
                {
                    Log.Write($"API: nao consegui abrir o pipe ({e.Message})");
                    await Task.Delay(TimeSpan.FromSeconds(5), parar);   // também cai aqui com todas as conexões ocupadas: passa logo
                    continue;
                }
                try
                {
                    await pipe.WaitForConnectionAsync(parar);
                    _ = Task.Run(() => Atender(pipe));
                }
                catch (OperationCanceledException)
                {
                    await pipe.DisposeAsync();
                    return;
                }
                catch (Exception e)
                {
                    Log.Write($"API: erro ao aceitar conexao ({e.Message})");
                    await pipe.DisposeAsync();
                    await Task.Delay(1000, parar);
                }
            }
        }
        catch (OperationCanceledException) { /* desligada enquanto esperava para tentar de novo */ }
    }

    /// <summary>Uma conexão, um pedido. Um cliente que conecta e não fala não pode prender uma das instâncias.</summary>
    private static async Task Atender(NamedPipeServerStream pipe)
    {
        await using (pipe)
        {
            try
            {
                using var limite = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var pedido = await ProtocoloPonte.LerAsync<PedidoPonte>(pipe, limite.Token);
                var resposta = Executar(pedido);
                using var envio = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await ProtocoloPonte.EscreverAsync(pipe, resposta, envio.Token);
            }
            catch (Exception e) { Log.Write($"API: conexao interrompida ({e.Message})"); }
        }
    }

    private static RespostaPonte Executar(PedidoPonte pedido)
    {
        try
        {
            object? dados = Despachar(pedido);
            return new RespostaPonte(true, JsonSerializer.SerializeToElement(dados, ProtocoloPonte.Json));
        }
        catch (ErroPonte e) { return new RespostaPonte(false, Erro: e.Codigo, Mensagem: e.Message); }
        catch (Exception e)
        {
            Log.Write($"API: erro em {pedido.Comando}: {e}");
            return new RespostaPonte(false, Erro: ErrosPonte.Interno, Mensagem: "Erro interno no control-center. Veja o log.");
        }
    }

    private static T Args<T>(PedidoPonte p) =>
        (p.Dados is { } d ? d.Deserialize<T>(ProtocoloPonte.Json) : default)
        ?? throw new ErroPonte(ErrosPonte.Invalido, "Pedido sem os dados esperados.");

    private static object? Despachar(PedidoPonte p) => p.Comando switch
    {
        ComandosPonte.Ping => true,
        ComandosPonte.Estado => Estado(),
        ComandosPonte.Cenas => ListarCenas(),
        ComandosPonte.AplicarCena => AplicarCena(Args<ArgsCena>(p).Nome),
        ComandosPonte.LampadaEnergia => Energia(Args<AjusteEnergia>(p)),
        ComandosPonte.LampadaAjustar => AjustarLampada(Args<AjusteLampada>(p)),
        ComandosPonte.LampadaTimer => Timer(Args<AjusteTimer>(p)),
        ComandosPonte.TelaBrilho => BrilhoTela(Args<ArgsBrilhoTela>(p)),
        ComandosPonte.Tema => Tema(Args<AjusteTema>(p)),
        ComandosPonte.LuzNoturna => LuzNoturna(Args<AjusteLuzNoturna>(p)),
        ComandosPonte.DualSense => AjustarDualSense(Args<AjusteDualSense>(p)),
        _ => throw new ErroPonte(ErrosPonte.Invalido, $"Comando desconhecido: {p.Comando}"),
    };

    // ---------------- leituras ----------------
    private static EstadoLampada EstadoDaLampada()
    {
        var c = Lamp.Cur;
        return new EstadoLampada(Lamp.ConfigProblem == null, c.Online, c.On, c.B, c.T, c.Mode, c.H, c.S,
                                 Lamp.TimerLeft(), Conforto.PendingLamp != null);
    }

    private static List<EstadoTela> EstadoDasTelas() =>
        Screens.Monitors.Select(m => new EstadoTela(m.Name, m.Kind, m.Ok, m.Value)).ToList();

    private static EstadoAparencia EstadoDaAparencia() =>
        new(Theme.IsDark() ? "escuro" : "claro", Theme.NightLightOn());

    private static string NomeModo(DualSense.Modo m) => m switch
    {
        DualSense.Modo.Xbox360 => "xbox360",
        DualSense.Modo.Ps4 => "ps4",
        _ => "dualsense",
    };

    private static EstadoDualSense EstadoDoDualSense()
    {
        var (r, g, b) = DualSense.BaseColor();
        return new EstadoDualSense(DualSense.Conectado, DualSense.Bluetooth, DualSense.Bateria, NomeModo(DualSense.Cfg.Modo),
                                   NomeModo(DualSense.Efetivo), DualSense.Cfg.Automatico, $"#{r:X2}{g:X2}{b:X2}",
                                   DualSense.Cfg.Intensidade, DualSense.Problema);
    }

    private static EstadoGeral Estado() =>
        new(EstadoDaLampada(), EstadoDasTelas(), EstadoDaAparencia(), EstadoDoDualSense(), Conforto.Cena);

    private static List<Cena> ListarCenas() => Conforto.Cenas.OfType<System.Text.Json.Nodes.JsonObject>()
        .Where(c => c["nome"] != null)
        .Select(c => new Cena(c["nome"]!.ToString(), Conforto.Describe(c), Conforto.Cena == c["nome"]!.ToString()))
        .ToList();

    // ---------------- cenas ----------------
    private static ResultadoCena AplicarCena(string nome)
    {
        if (!Conforto.Cenas.OfType<System.Text.Json.Nodes.JsonObject>().Any(c => c["nome"]?.ToString() == nome))
            throw new ErroPonte(ErrosPonte.NaoEncontrado, $"A cena '{nome}' não existe.");
        Conforto.ApplyScene(nome);   // o mesmo caminho do painel: nunca acende lâmpada apagada
        return new ResultadoCena(nome, EstadoDaLampada());
    }

    // ---------------- lâmpada ----------------
    private static void ExigirConfigurada()
    {
        if (Lamp.ConfigProblem is { } p) throw new ErroPonte(ErrosPonte.Indisponivel, $"Lâmpada não configurada: {p}.");
    }

    private static EstadoLampada Energia(AjusteEnergia a)
    {
        ExigirConfigurada();
        bool ligar = a.Ligada ?? throw new ErroPonte(ErrosPonte.Invalido, "Informe 'ligada'.");
        bool? ok = null;
        // WithBusy ignora o pedido se já houver um em andamento: "ok" continua nulo e o cliente fica sabendo
        Lamp.WithBusy(() => ok = ligar ? Lamp.PowerOn() : Lamp.PowerOff());
        if (ok == null) throw new ErroPonte(ErrosPonte.Conflito, "A lâmpada está ocupada com outro comando.");
        if (ok == false) throw new ErroPonte(ErrosPonte.Indisponivel, "A lâmpada não respondeu.");
        Bus.Notify();
        return EstadoDaLampada();   // como no painel, ligar não tira o destaque da cena: ela é quem combinou este "ligar"
    }

    /// <summary>Lê a lâmpada de novo antes de mexer: o ligar/desligar pode ter vindo do app da Tuya.</summary>
    private static void ExigirLigada()
    {
        ExigirConfigurada();
        if (!Lamp.RefreshState()) throw new ErroPonte(ErrosPonte.Indisponivel, "A lâmpada não respondeu.");
        if (!Lamp.Cur.On) throw new ErroPonte(ErrosPonte.Conflito, "A lâmpada está desligada. Ligue-a antes de ajustar.");
    }

    private static EstadoLampada AjustarLampada(AjusteLampada a)
    {
        ExigirLigada();
        var c = Lamp.Cur;
        if (a.Matiz != null || a.Saturacao != null || (a.Temperatura == null && c.Mode == "colour"))
            Lamp.ApplyColour(a.Matiz ?? c.H, a.Saturacao ?? c.S, a.Brilho ?? c.B);
        else
            Lamp.ApplyWhite(a.Brilho ?? c.B, a.Temperatura ?? c.T);
        // ApplyWhite e ApplyColour não devolvem resultado: uma falha aparece no estado "Online"
        if (!c.Online) throw new ErroPonte(ErrosPonte.Indisponivel, "A lâmpada não respondeu.");
        Core.Touch();
        Bus.Notify();
        return EstadoDaLampada();
    }

    private static EstadoLampada Timer(AjusteTimer a)
    {
        ExigirLigada();
        Lamp.SetTimer(a.Minutos ?? throw new ErroPonte(ErrosPonte.Invalido, "Informe 'minutos'."));
        if (!Lamp.Cur.Online) throw new ErroPonte(ErrosPonte.Indisponivel, "A lâmpada não respondeu.");
        return EstadoDaLampada();
    }

    // ---------------- telas ----------------
    private static List<EstadoTela> BrilhoTela(ArgsBrilhoTela a)
    {
        if (a.Tela == null)
        {
            if (!Screens.Monitors.Any(m => m.Ok)) throw new ErroPonte(ErrosPonte.Indisponivel, "Nenhuma tela está respondendo.");
            Core.SetScreenBrightness(a.Valor);
            return EstadoDasTelas();
        }
        var tela = Screens.Monitors.FirstOrDefault(m => string.Equals(m.Name, a.Tela, StringComparison.OrdinalIgnoreCase))
            ?? throw new ErroPonte(ErrosPonte.NaoEncontrado, $"A tela '{a.Tela}' não existe.");
        if (!tela.Ok) throw new ErroPonte(ErrosPonte.Indisponivel, $"A tela '{tela.Name}' não responde ao DDC/CI.");
        Core.SetScreenBrightness(a.Valor, tela.Key);
        return EstadoDasTelas();
    }

    // ---------------- aparência ----------------
    private static EstadoAparencia Tema(AjusteTema a)
    {
        Theme.SetDark(a.Tema == "escuro");
        Conforto.Cena = null;
        Bus.Notify();
        return EstadoDaAparencia();
    }

    private static EstadoAparencia LuzNoturna(AjusteLuzNoturna a)
    {
        bool ligada = a.Ligada ?? throw new ErroPonte(ErrosPonte.Invalido, "Informe 'ligada'.");
        Theme.SetNightLight(ligada);
        // SetNightLight só registra a falha no log: confere pelo registro se a mudança valeu
        if (Theme.NightLightOn() != ligada) throw new ErroPonte(ErrosPonte.Indisponivel, "O Windows não aplicou a Luz Noturna.");
        Conforto.Cena = null;
        return EstadoDaAparencia();
    }

    // ---------------- DualSense ----------------
    private static EstadoDualSense AjustarDualSense(AjusteDualSense a)
    {
        if (a.Cor is { } cor)
        {
            var hex = cor.TrimStart('#');
            DualSense.SetColor(Convert.ToByte(hex[0..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16));
        }
        if (a.Intensidade is { } i) DualSense.SetIntensity(i);
        // nesta ordem: escolher um modo tira do automático, e pedir os dois juntos deve deixar o automático como pedido
        if (a.Modo is { } m)
            DualSense.SetModo(m switch { "xbox360" => DualSense.Modo.Xbox360, "ps4" => DualSense.Modo.Ps4, _ => DualSense.Modo.DualSense });
        if (a.Automatico is { } auto) DualSense.SetAutomatico(auto);
        return EstadoDoDualSense();
    }
}
