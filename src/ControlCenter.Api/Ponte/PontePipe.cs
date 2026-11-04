using System.IO.Pipes;
using System.Security.Principal;
using ControlCenter.Contracts;
using Microsoft.Extensions.Options;

namespace ControlCenter.Api.Ponte;

public sealed class OpcoesPonte
{
    public const string Secao = "Ponte";

    /// <summary>"Pipe" fala com o app da bandeja; "Simulada" roda sem hardware (Docker, demonstração).</summary>
    public string Modo { get; set; } = "Pipe";
    public string NomePipe { get; set; } = ProtocoloPonte.NomePipe;
    public int TimeoutConexaoMs { get; set; } = 2000;
    /// <summary>Folgado porque acordar a lâmpada pelo Bluetooth e avisar as janelas da troca de tema demoram.</summary>
    public int TimeoutComandoMs { get; set; } = 30000;
}

/// <summary>Uma conexão por comando: o app pode abrir e fechar à vontade sem deixar conexão velha para trás.</summary>
public sealed class PontePipe(IOptions<OpcoesPonte> opcoes) : IPonte
{
    public async Task<RespostaPonte> EnviarAsync(PedidoPonte pedido, CancellationToken ct)
    {
        var o = opcoes.Value;
        // Identification: o outro lado pode saber quem conectou, mas não pode agir em nome da conta do serviço
        await using var pipe = new NamedPipeClientStream(".", o.NomePipe, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);

        using (var conexao = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            conexao.CancelAfter(o.TimeoutConexaoMs);
            try
            {
                await pipe.ConnectAsync(conexao.Token);
            }
            catch (Exception e) when (!ct.IsCancellationRequested && e is OperationCanceledException or TimeoutException or IOException or UnauthorizedAccessException)
            {
                throw new PonteException(ErrosPonte.Indisponivel,
                    "O control-center não está rodando na sessão do usuário. Abra o app da bandeja.");
            }
        }

        using var comando = CancellationTokenSource.CreateLinkedTokenSource(ct);
        comando.CancelAfter(o.TimeoutComandoMs);
        try
        {
            await ProtocoloPonte.EscreverAsync(pipe, pedido, comando.Token);
            return await ProtocoloPonte.LerAsync<RespostaPonte>(pipe, comando.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new PonteException(ErrosPonte.Indisponivel, "O control-center demorou demais para responder.");
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or InvalidDataException)
        {
            throw new PonteException(ErrosPonte.Indisponivel, "A conexão com o control-center caiu durante o comando.");
        }
    }
}
