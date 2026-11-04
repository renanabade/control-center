using ControlCenter.Api.Ponte;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace ControlCenter.Api.Infra;

/// <summary>Toda falha sai como ProblemDetails (RFC 9457), sem detalhes internos: o cliente só vê o que pode resolver.</summary>
public sealed class TratadorErros(IProblemDetailsService problemas, ILogger<TratadorErros> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception erro, CancellationToken ct)
    {
        var (status, titulo, detalhe) = erro switch
        {
            PonteException p => (p.StatusHttp, Titulo(p.StatusHttp), p.Message),
            BadHttpRequestException b => (b.StatusCode, "Pedido inválido", "O corpo do pedido não pôde ser lido."),
            _ => (StatusCodes.Status500InternalServerError, "Erro interno", "Ocorreu um erro inesperado."),
        };
        if (status >= 500 && erro is not PonteException) log.LogError(erro, "Erro não tratado em {Caminho}", http.Request.Path);

        http.Response.StatusCode = status;
        var problema = new ProblemDetails { Status = status, Title = titulo, Detail = detalhe };
        if (erro is PonteException e) problema.Extensions["codigo"] = e.Codigo;
        return await problemas.TryWriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = problema, Exception = erro });
    }

    private static string Titulo(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "Pedido inválido",
        StatusCodes.Status404NotFound => "Não encontrado",
        StatusCodes.Status409Conflict => "Conflito com o estado do aparelho",
        StatusCodes.Status503ServiceUnavailable => "Aparelho indisponível",
        _ => "Erro interno",
    };
}
