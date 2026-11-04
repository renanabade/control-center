using System.Net;
using System.Net.Http.Json;
using ControlCenter.Api.Endpoints;
using ControlCenter.Contracts;
using Xunit;

namespace ControlCenter.Api.Tests;

public sealed class HistoricoTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Pagina_do_mais_recente_para_o_mais_antigo_sem_repetir_itens()
    {
        await using var api = new ApiFactory();
        var c = await api.ClienteLogadoAsync();
        for (int i = 10; i < 15; i++)
            await c.PutAsJsonAsync("/api/v1/dispositivos/telas/brilho", new AjusteBrilhoTela(i), Ct);

        var p1 = await c.GetFromJsonAsync<PaginaHistorico>("/api/v1/historico?pagina=1&tamanhoPagina=2", Ct);
        var p3 = await c.GetFromJsonAsync<PaginaHistorico>("/api/v1/historico?pagina=3&tamanhoPagina=2", Ct);
        Assert.Equal((5, 3), (p1!.Total, p1.TotalPaginas));
        Assert.Equal(2, p1.Itens.Count);
        Assert.Single(p3!.Itens);
        Assert.Contains("14", p1.Itens[0].Dados);
        Assert.Contains("10", p3.Itens[0].Dados);
        Assert.True(p1.Itens[0].Id > p1.Itens[1].Id);
        Assert.Equal(DateTimeKind.Utc, p1.Itens[0].QuandoUtc.Kind);
    }

    [Fact]
    public async Task Registra_usuario_acao_e_tambem_as_falhas()
    {
        await using var api = new ApiFactory();
        var c = await api.ClienteLogadoAsync();
        await c.PostAsync("/api/v1/cenas/Filme/aplicar", null, Ct);
        await c.PostAsync("/api/v1/cenas/Nao%20Existe/aplicar", null, Ct);

        var h = await c.GetFromJsonAsync<PaginaHistorico>("/api/v1/historico", Ct);
        var falha = h!.Itens[0];
        var ok = h.Itens[1];
        Assert.Equal((ApiFactory.Usuario, "AplicarCena", true, 200), (ok.Usuario, ok.Acao, ok.Sucesso, ok.Status));
        Assert.Equal(("AplicarCena", false, 404), (falha.Acao, falha.Sucesso, falha.Status));
        Assert.Contains("não existe", falha.Erro);
    }

    [Fact]
    public async Task Pedido_invalido_e_leitura_nao_entram_no_historico()
    {
        await using var api = new ApiFactory();
        var c = await api.ClienteLogadoAsync();
        await c.PutAsJsonAsync("/api/v1/aparencia/tema", new AjusteTema("azul"), Ct);
        await c.GetAsync("/api/v1/estado", Ct);
        Assert.Equal(0, (await c.GetFromJsonAsync<PaginaHistorico>("/api/v1/historico", Ct))!.Total);
    }

    [Fact]
    public async Task Login_nunca_grava_a_senha()
    {
        await using var api = new ApiFactory();
        var c = await api.ClienteLogadoAsync();
        Assert.Equal(0, (await c.GetFromJsonAsync<PaginaHistorico>("/api/v1/historico", Ct))!.Total);
    }

    [Fact]
    public async Task Filtra_por_acao_e_sucesso()
    {
        await using var api = new ApiFactory();
        var c = await api.ClienteLogadoAsync();
        await c.PutAsJsonAsync("/api/v1/aparencia/tema", new AjusteTema("claro"), Ct);
        await c.PostAsync("/api/v1/cenas/Filme/aplicar", null, Ct);
        await c.PostAsync("/api/v1/cenas/Nao%20Existe/aplicar", null, Ct);

        var porAcao = await c.GetFromJsonAsync<PaginaHistorico>("/api/v1/historico?acao=DefinirTema", Ct);
        Assert.Equal("DefinirTema", Assert.Single(porAcao!.Itens).Acao);
        var falhas = await c.GetFromJsonAsync<PaginaHistorico>("/api/v1/historico?sucesso=false", Ct);
        Assert.False(Assert.Single(falhas!.Itens).Sucesso);
    }

    [Fact]
    public async Task Filtra_por_periodo()
    {
        await using var api = new ApiFactory();
        var c = await api.ClienteLogadoAsync();
        await c.PostAsync("/api/v1/cenas/Filme/aplicar", null, Ct);

        string Url(DateTime de) => $"/api/v1/historico?de={Uri.EscapeDataString(de.ToString("O"))}";
        Assert.Equal(1, (await c.GetFromJsonAsync<PaginaHistorico>(Url(DateTime.UtcNow.AddHours(-1)), Ct))!.Total);
        Assert.Equal(0, (await c.GetFromJsonAsync<PaginaHistorico>(Url(DateTime.UtcNow.AddHours(1)), Ct))!.Total);
    }

    [Theory]
    [InlineData("pagina=0")]
    [InlineData("tamanhoPagina=0")]
    [InlineData("tamanhoPagina=101")]
    [InlineData("de=2026-02-01&ate=2026-01-01")]
    public async Task Parametros_invalidos_devolvem_400(string consulta)
    {
        await using var api = new ApiFactory();
        var c = await api.ClienteLogadoAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync($"/api/v1/historico?{consulta}", Ct)).StatusCode);
    }
}
