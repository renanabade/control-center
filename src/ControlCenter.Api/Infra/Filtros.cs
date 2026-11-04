using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using ControlCenter.Api.Dados;
using ControlCenter.Api.Ponte;
using ControlCenter.Contracts;
using FluentValidation;

namespace ControlCenter.Api.Infra;

/// <summary>Roda o validador do FluentValidation do corpo do pedido e devolve 400 no formato ValidationProblemDetails.</summary>
public sealed class FiltroValidacao<T>(IValidator<T> validador) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var corpo = ctx.Arguments.OfType<T>().FirstOrDefault();
        if (corpo == null) return Results.Problem("Corpo do pedido ausente.", statusCode: StatusCodes.Status400BadRequest);
        var r = await validador.ValidateAsync(corpo, ctx.HttpContext.RequestAborted);
        return r.IsValid ? await next(ctx) : Results.ValidationProblem(r.ToDictionary());
    }
}

/// <summary>Grava no histórico todo comando que chegou aos aparelhos, inclusive os que falharam.</summary>
public sealed class FiltroHistorico(HistoricoDb db, TimeProvider relogio, ILogger<FiltroHistorico> log) : IEndpointFilter
{
    private const int MaxDados = 2000, MaxErro = 500;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var inicio = Stopwatch.GetTimestamp();
        try
        {
            var resultado = await next(ctx);
            int status = (resultado as IStatusCodeHttpResult)?.StatusCode ?? StatusCodes.Status200OK;
            await Gravar(ctx, inicio, status, null);
            return resultado;
        }
        catch (PonteException e)
        {
            await Gravar(ctx, inicio, e.StatusHttp, e.Message);
            throw;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await Gravar(ctx, inicio, StatusCodes.Status500InternalServerError, "Erro interno.");
            throw;
        }
    }

    private async Task Gravar(EndpointFilterInvocationContext ctx, long inicio, int status, string? erro)
    {
        var http = ctx.HttpContext;
        try
        {
            db.Comandos.Add(new RegistroComando
            {
                QuandoUtc = relogio.GetUtcNow().UtcDateTime,
                Usuario = http.User.FindFirstValue("sub") ?? "?",
                Acao = http.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ?? "?",
                Alvo = Cortar(http.Request.Path.Value ?? "", 300)!,
                Dados = Cortar(Corpo(ctx), MaxDados),
                Status = status,
                Sucesso = status < 400,
                Erro = Cortar(erro, MaxErro),
                DuracaoMs = (long)Stopwatch.GetElapsedTime(inicio).TotalMilliseconds,
            });
            // sem o token do pedido: o comando já foi executado, mesmo que o cliente tenha desistido de esperar
            await db.SaveChangesAsync(CancellationToken.None);
        }
        // uma falha no histórico não pode transformar um comando que funcionou em erro para o cliente
        catch (Exception e) { log.LogError(e, "Não consegui gravar o comando no histórico"); }
    }

    /// <summary>Só os tipos do contrato entram no histórico: o resto dos argumentos são serviços injetados.</summary>
    private static string? Corpo(EndpointFilterInvocationContext ctx)
    {
        var corpo = ctx.Arguments.FirstOrDefault(a => a?.GetType().Assembly == typeof(AjusteLampada).Assembly);
        return corpo == null ? null : JsonSerializer.Serialize(corpo, corpo.GetType(), ProtocoloPonte.Json);
    }

    private static string? Cortar(string? s, int max) => s == null || s.Length <= max ? s : s[..max];
}
