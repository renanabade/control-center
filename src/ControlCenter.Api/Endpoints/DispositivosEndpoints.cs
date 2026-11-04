using ControlCenter.Api.Infra;
using ControlCenter.Api.Ponte;
using ControlCenter.Contracts;

namespace ControlCenter.Api.Endpoints;

public static class DispositivosEndpoints
{
    /// <summary>Comando sobre um aparelho: validação primeiro (pedido inválido não vira histórico), depois o registro.</summary>
    private static RouteHandlerBuilder Comando<T>(this RouteHandlerBuilder rota, string nome, string resumo) => rota
        .WithName(nome)
        .WithSummary(resumo)
        .AddEndpointFilter<FiltroValidacao<T>>()
        .AddEndpointFilter<FiltroHistorico>()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

    public static void MapDispositivos(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization();

        api.MapGet("/estado", async (IPonte p, CancellationToken ct) => await p.PedirAsync<EstadoGeral>(ComandosPonte.Estado, null, ct))
            .WithTags("Estado").WithName("ObterEstado").WithSummary("Tudo de uma vez: lâmpada, telas, aparência, DualSense e cena ativa.")
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        MapCenas(api.MapGroup("/cenas").WithTags("Cenas"));
        MapLampada(api.MapGroup("/dispositivos/lampada").WithTags("Lâmpada"));
        MapTelas(api.MapGroup("/dispositivos/telas").WithTags("Telas"));
        MapAparencia(api.MapGroup("/aparencia").WithTags("Aparência"));
        MapDualSense(api.MapGroup("/dispositivos/dualsense").WithTags("DualSense"));
    }

    private static void MapCenas(RouteGroupBuilder g)
    {
        g.MapGet("/", async (IPonte p, CancellationToken ct) => await p.PedirAsync<List<Cena>>(ComandosPonte.Cenas, null, ct))
            .WithName("ListarCenas").WithSummary("Lista as cenas configuradas.")
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        g.MapPost("/{nome}/aplicar", async (string nome, IPonte p, CancellationToken ct) =>
                await p.PedirAsync<ResultadoCena>(ComandosPonte.AplicarCena, new ArgsCena(nome), ct))
            .WithName("AplicarCena")
            .WithSummary("Aplica uma cena.")
            .WithDescription("Nunca acende uma lâmpada apagada: com ela apagada, o ajuste fica combinado para o próximo \"ligar\" (lampada.ajustePendente).")
            .AddEndpointFilter<FiltroHistorico>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    private static void MapLampada(RouteGroupBuilder g)
    {
        g.MapGet("/", async (IPonte p, CancellationToken ct) =>
                (await p.PedirAsync<EstadoGeral>(ComandosPonte.Estado, null, ct)).Lampada)
            .WithName("ObterLampada").WithSummary("Estado da lâmpada.")
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        g.MapPut("/energia", async (AjusteEnergia a, IPonte p, CancellationToken ct) =>
                await p.PedirAsync<EstadoLampada>(ComandosPonte.LampadaEnergia, a, ct))
            .Comando<AjusteEnergia>("LigarDesligarLampada", "Liga ou desliga a lâmpada. Ligar usa o ajuste combinado por uma cena, uma única vez.");

        g.MapPatch("/", async (AjusteLampada a, IPonte p, CancellationToken ct) =>
                await p.PedirAsync<EstadoLampada>(ComandosPonte.LampadaAjustar, a, ct))
            .Comando<AjusteLampada>("AjustarLampada", "Ajusta brilho, temperatura ou cor. Com a lâmpada apagada, responde 409 e não a acende.");

        g.MapPut("/timer", async (AjusteTimer a, IPonte p, CancellationToken ct) =>
                await p.PedirAsync<EstadoLampada>(ComandosPonte.LampadaTimer, a, ct))
            .Comando<AjusteTimer>("DefinirTimerLampada", "Desliga a lâmpada daqui a N minutos (0 cancela).");
    }

    private static void MapTelas(RouteGroupBuilder g)
    {
        g.MapGet("/", async (IPonte p, CancellationToken ct) =>
                (await p.PedirAsync<EstadoGeral>(ComandosPonte.Estado, null, ct)).Telas)
            .WithName("ListarTelas").WithSummary("Lista os monitores e o brilho de cada um.")
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // 202 porque o DDC/CI é lento e roda em fila no app: o valor foi agendado, ainda não chegou ao monitor
        g.MapPut("/brilho", async (AjusteBrilhoTela a, IPonte p, CancellationToken ct) =>
                Results.Accepted(value: await p.PedirAsync<List<EstadoTela>>(ComandosPonte.TelaBrilho, new ArgsBrilhoTela(null, a.Valor!.Value), ct)))
            .Comando<AjusteBrilhoTela>("DefinirBrilhoTelas", "Agenda o brilho de todas as telas.")
            .Produces<List<EstadoTela>>(StatusCodes.Status202Accepted);

        g.MapPut("/{nome}/brilho", async (string nome, AjusteBrilhoTela a, IPonte p, CancellationToken ct) =>
                Results.Accepted(value: await p.PedirAsync<List<EstadoTela>>(ComandosPonte.TelaBrilho, new ArgsBrilhoTela(nome, a.Valor!.Value), ct)))
            .Comando<AjusteBrilhoTela>("DefinirBrilhoTela", "Agenda o brilho de uma tela, pelo nome.")
            .Produces<List<EstadoTela>>(StatusCodes.Status202Accepted);
    }

    private static void MapAparencia(RouteGroupBuilder g)
    {
        g.MapGet("/", async (IPonte p, CancellationToken ct) =>
                (await p.PedirAsync<EstadoGeral>(ComandosPonte.Estado, null, ct)).Aparencia)
            .WithName("ObterAparencia").WithSummary("Tema do Windows e Luz Noturna.")
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        g.MapPut("/tema", async (AjusteTema a, IPonte p, CancellationToken ct) =>
                await p.PedirAsync<EstadoAparencia>(ComandosPonte.Tema, a, ct))
            .Comando<AjusteTema>("DefinirTema", "Troca o tema do Windows ('escuro' ou 'claro').");

        g.MapPut("/luz-noturna", async (AjusteLuzNoturna a, IPonte p, CancellationToken ct) =>
                await p.PedirAsync<EstadoAparencia>(ComandosPonte.LuzNoturna, a, ct))
            .Comando<AjusteLuzNoturna>("DefinirLuzNoturna", "Liga ou desliga a Luz Noturna.");
    }

    private static void MapDualSense(RouteGroupBuilder g)
    {
        g.MapGet("/", async (IPonte p, CancellationToken ct) =>
                (await p.PedirAsync<EstadoGeral>(ComandosPonte.Estado, null, ct)).DualSense)
            .WithName("ObterDualSense").WithSummary("Conexão, bateria, luz e modo do DualSense.")
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        g.MapPatch("/", async (AjusteDualSense a, IPonte p, CancellationToken ct) =>
                await p.PedirAsync<EstadoDualSense>(ComandosPonte.DualSense, a, ct))
            .Comando<AjusteDualSense>("AjustarDualSense", "Ajusta luz e modo de emulação (dualsense, xbox360, ps4).");
    }
}
