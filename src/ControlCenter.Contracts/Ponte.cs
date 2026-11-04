using System.Buffers.Binary;
using System.Text.Json;

namespace ControlCenter.Contracts;

public static class ComandosPonte
{
    public const string Ping = "ping";
    public const string Estado = "estado";
    public const string Cenas = "cenas";
    public const string AplicarCena = "cena.aplicar";
    public const string LampadaEnergia = "lampada.energia";
    public const string LampadaAjustar = "lampada.ajustar";
    public const string LampadaTimer = "lampada.timer";
    public const string TelaBrilho = "tela.brilho";
    public const string Tema = "aparencia.tema";
    public const string LuzNoturna = "aparencia.luz_noturna";
    public const string DualSense = "dualsense.ajustar";
}

/// <summary>O motivo da falha, para a API escolher o status HTTP sem depender do texto da mensagem.</summary>
public static class ErrosPonte
{
    public const string NaoEncontrado = "nao_encontrado";
    public const string Conflito = "conflito";
    public const string Indisponivel = "indisponivel";
    public const string Invalido = "invalido";
    public const string Interno = "interno";
}

public sealed record PedidoPonte(string Comando, JsonElement? Dados = null);

public sealed record RespostaPonte(bool Ok, JsonElement? Dados = null, string? Erro = null, string? Mensagem = null);

/// <summary>
/// Formato no pipe: 4 bytes com o tamanho e depois o JSON. Com o tamanho na frente, uma mensagem nunca é lida pela
/// metade, e o limite impede que um cliente com defeito faça o outro lado alocar memória à vontade.
/// </summary>
public static class ProtocoloPonte
{
    public const string NomePipe = "ControlCenter.Ponte.v1";
    private const int TamanhoMaximo = 1024 * 1024;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task EscreverAsync<T>(Stream stream, T mensagem, CancellationToken ct = default)
    {
        byte[] corpo = JsonSerializer.SerializeToUtf8Bytes(mensagem, Json);
        if (corpo.Length > TamanhoMaximo) throw new InvalidDataException("mensagem grande demais para a ponte");
        byte[] cabecalho = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(cabecalho, corpo.Length);
        await stream.WriteAsync(cabecalho, ct);
        await stream.WriteAsync(corpo, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<T> LerAsync<T>(Stream stream, CancellationToken ct = default)
    {
        byte[] cabecalho = new byte[4];
        await stream.ReadExactlyAsync(cabecalho, ct);
        int tamanho = BinaryPrimitives.ReadInt32LittleEndian(cabecalho);
        if (tamanho is <= 0 or > TamanhoMaximo) throw new InvalidDataException($"tamanho inválido na ponte: {tamanho}");
        byte[] corpo = new byte[tamanho];
        await stream.ReadExactlyAsync(corpo, ct);
        return JsonSerializer.Deserialize<T>(corpo, Json) ?? throw new InvalidDataException("mensagem vazia na ponte");
    }
}
