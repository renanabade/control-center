using System.Text.Json;
using ControlCenter.Contracts;

namespace ControlCenter.Api.Ponte;

/// <summary>Quem fala com os aparelhos. O envio é genérico porque cada comando é só um nome e um JSON.</summary>
public interface IPonte
{
    Task<RespostaPonte> EnviarAsync(PedidoPonte pedido, CancellationToken ct);
}

/// <summary>Falha vinda da ponte, com o status HTTP que o cliente deve receber.</summary>
public sealed class PonteException(string codigo, string mensagem) : Exception(mensagem)
{
    public string Codigo { get; } = codigo;

    public int StatusHttp => Codigo switch
    {
        ErrosPonte.NaoEncontrado => StatusCodes.Status404NotFound,
        ErrosPonte.Conflito => StatusCodes.Status409Conflict,
        ErrosPonte.Indisponivel => StatusCodes.Status503ServiceUnavailable,
        ErrosPonte.Invalido => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status500InternalServerError,
    };
}

public static class PonteExtensions
{
    public static async Task<T> PedirAsync<T>(this IPonte ponte, string comando, object? dados, CancellationToken ct)
    {
        JsonElement? json = dados == null ? null : JsonSerializer.SerializeToElement(dados, dados.GetType(), ProtocoloPonte.Json);
        var r = await ponte.EnviarAsync(new PedidoPonte(comando, json), ct);
        if (!r.Ok) throw new PonteException(r.Erro ?? ErrosPonte.Interno, r.Mensagem ?? "A ponte recusou o comando.");
        return (r.Dados is { } d ? d.Deserialize<T>(ProtocoloPonte.Json) : default)
            ?? throw new PonteException(ErrosPonte.Interno, "A ponte respondeu sem dados.");
    }
}
