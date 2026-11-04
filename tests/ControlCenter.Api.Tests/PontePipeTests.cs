using System.IO.Pipes;
using System.Text.Json;
using ControlCenter.Api.Ponte;
using ControlCenter.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace ControlCenter.Api.Tests;

/// <summary>O cliente de verdade contra um pipe de verdade, com um servidor de teste fazendo o papel do app da bandeja.</summary>
public sealed class PontePipeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PontePipe Cliente(string nome, int comandoMs = 5000) => new(Options.Create(new OpcoesPonte
    {
        NomePipe = nome,
        TimeoutConexaoMs = 300,
        TimeoutComandoMs = comandoMs,
    }));

    private static string NomeUnico() => "cc-teste-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Ida_e_volta_preserva_comando_dados_e_acentos()
    {
        string nome = NomeUnico();
        await using var servidor = new NamedPipeServerStream(nome, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var atendimento = Task.Run(async () =>
        {
            await servidor.WaitForConnectionAsync(Ct);
            var pedido = await ProtocoloPonte.LerAsync<PedidoPonte>(servidor, Ct);
            var args = pedido.Dados!.Value.Deserialize<ArgsCena>(ProtocoloPonte.Json)!;
            var dados = JsonSerializer.SerializeToElement(new Cena(args.Nome, $"via {pedido.Comando}", true), ProtocoloPonte.Json);
            await ProtocoloPonte.EscreverAsync(servidor, new RespostaPonte(true, dados), Ct);
        }, Ct);

        var cena = await Cliente(nome).PedirAsync<Cena>(ComandosPonte.AplicarCena, new ArgsCena("Manhã de sábado"), Ct);
        await atendimento;
        Assert.Equal(new Cena("Manhã de sábado", "via cena.aplicar", true), cena);
    }

    [Fact]
    public async Task Erro_do_app_vira_excecao_com_o_status_certo()
    {
        string nome = NomeUnico();
        await using var servidor = new NamedPipeServerStream(nome, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var atendimento = Task.Run(async () =>
        {
            await servidor.WaitForConnectionAsync(Ct);
            await ProtocoloPonte.LerAsync<PedidoPonte>(servidor, Ct);
            await ProtocoloPonte.EscreverAsync(servidor, new RespostaPonte(false, Erro: ErrosPonte.Conflito, Mensagem: "ocupada"), Ct);
        }, Ct);

        var e = await Assert.ThrowsAsync<PonteException>(() => Cliente(nome).PedirAsync<bool>(ComandosPonte.Ping, null, Ct));
        await atendimento;
        Assert.Equal(409, e.StatusHttp);
        Assert.Equal("ocupada", e.Message);
    }

    [Fact]
    public async Task Sem_o_app_rodando_a_falha_e_indisponivel()
    {
        var e = await Assert.ThrowsAsync<PonteException>(() => Cliente(NomeUnico()).PedirAsync<bool>(ComandosPonte.Ping, null, Ct));
        Assert.Equal(503, e.StatusHttp);
    }

    [Fact]
    public async Task App_que_cai_no_meio_do_comando_vira_indisponivel()
    {
        string nome = NomeUnico();
        var servidor = new NamedPipeServerStream(nome, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var atendimento = Task.Run(async () =>
        {
            await servidor.WaitForConnectionAsync(Ct);
            await ProtocoloPonte.LerAsync<PedidoPonte>(servidor, Ct);
            await servidor.DisposeAsync();
        }, Ct);

        var e = await Assert.ThrowsAsync<PonteException>(() => Cliente(nome).PedirAsync<bool>(ComandosPonte.Ping, null, Ct));
        await atendimento;
        Assert.Equal(503, e.StatusHttp);
    }

    [Fact]
    public async Task App_travado_nao_prende_a_api_para_sempre()
    {
        string nome = NomeUnico();
        await using var servidor = new NamedPipeServerStream(nome, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var conexao = servidor.WaitForConnectionAsync(Ct);

        var e = await Assert.ThrowsAsync<PonteException>(() => Cliente(nome, comandoMs: 200).PedirAsync<bool>(ComandosPonte.Ping, null, Ct));
        await conexao;
        Assert.Equal(503, e.StatusHttp);
        Assert.Contains("demorou", e.Message);
    }

    [Fact]
    public async Task Tamanho_absurdo_na_mensagem_e_recusado_antes_de_alocar()
    {
        using var stream = new MemoryStream(BitConverter.GetBytes(int.MaxValue));
        await Assert.ThrowsAsync<InvalidDataException>(() => ProtocoloPonte.LerAsync<PedidoPonte>(stream, Ct));
    }
}
