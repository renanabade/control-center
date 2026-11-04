using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ControlCenter.Api.Autenticacao;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ControlCenter.Api.Tests;

public sealed class AutenticacaoTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _api;

    public AutenticacaoTests(ApiFactory api) => _api = api;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Login_correto_devolve_token_que_abre_a_api()
    {
        var cliente = _api.CreateClient();
        var r = await cliente.PostAsJsonAsync("/api/v1/auth/login", new PedidoLogin(ApiFactory.Usuario, ApiFactory.Senha), Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var login = await r.Content.ReadFromJsonAsync<RespostaLogin>(Ct);
        Assert.True(login!.ExpiraEmUtc > DateTime.UtcNow);

        cliente.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync("/api/v1/estado", Ct)).StatusCode);
    }

    [Theory]
    [InlineData(ApiFactory.Usuario, "senha-errada")]
    [InlineData("outro", ApiFactory.Senha)]
    public async Task Login_errado_devolve_401_como_problem_details(string usuario, string senha)
    {
        var r = await _api.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new PedidoLogin(usuario, senha), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Login_sem_campos_devolve_erros_de_validacao()
    {
        var r = await _api.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new PedidoLogin(null, ""), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var p = await r.Content.ReadFromJsonAsync<ValidationProblemDetails>(Ct);
        Assert.Contains("Usuario", p!.Errors.Keys);
        Assert.Contains("Senha", p.Errors.Keys);
    }

    [Theory]
    [InlineData("GET", "/api/v1/estado")]
    [InlineData("GET", "/api/v1/cenas")]
    [InlineData("POST", "/api/v1/cenas/Noite/aplicar")]
    [InlineData("PUT", "/api/v1/dispositivos/lampada/energia")]
    [InlineData("PATCH", "/api/v1/dispositivos/lampada")]
    [InlineData("PUT", "/api/v1/dispositivos/telas/brilho")]
    [InlineData("PUT", "/api/v1/aparencia/tema")]
    [InlineData("PATCH", "/api/v1/dispositivos/dualsense")]
    [InlineData("GET", "/api/v1/historico")]
    public async Task Sem_token_nada_passa(string metodo, string rota)
    {
        var r = await _api.CreateClient().SendAsync(new HttpRequestMessage(new HttpMethod(metodo), rota), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Token_assinado_com_outra_chave_e_recusado()
    {
        var cliente = _api.CreateClient();
        // cabeçalho e corpo válidos, assinatura de outra chave
        cliente.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJ0ZXN0ZSIsImlzcyI6ImNvbnRyb2wtY2VudGVyIiwiYXVkIjoiY29udHJvbC1jZW50ZXItYXBpIiwiZXhwIjo0MTAyNDQ0ODAwfQ.c2Fzc2luYXR1cmEtZmFsc2EtZGUtdGVzdGU");
        Assert.Equal(HttpStatusCode.Unauthorized, (await cliente.GetAsync("/api/v1/estado", Ct)).StatusCode);
    }

    [Fact]
    public async Task Tentativas_demais_de_login_devolvem_429()
    {
        await using var api = new ApiFactory { LoginPorMinuto = 2 };
        var cliente = api.CreateClient();
        var pedido = new PedidoLogin(ApiFactory.Usuario, "senha-errada");
        Assert.Equal(HttpStatusCode.Unauthorized, (await cliente.PostAsJsonAsync("/api/v1/auth/login", pedido, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await cliente.PostAsJsonAsync("/api/v1/auth/login", pedido, Ct)).StatusCode);

        var r = await cliente.PostAsJsonAsync("/api/v1/auth/login", pedido, Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, r.StatusCode);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Limite_global_devolve_429_e_nao_afeta_o_health()
    {
        await using var api = new ApiFactory { PedidosPorMinuto = 3 };
        var cliente = await api.ClienteLogadoAsync();
        HttpStatusCode ultimo = default;
        for (int i = 0; i < 5; i++) ultimo = (await cliente.GetAsync("/api/v1/cenas", Ct)).StatusCode;
        Assert.Equal(HttpStatusCode.TooManyRequests, ultimo);
        Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync("/health", Ct)).StatusCode);
    }
}
