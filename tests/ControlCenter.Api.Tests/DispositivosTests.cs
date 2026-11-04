using System.Net;
using System.Net.Http.Json;
using ControlCenter.Contracts;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace ControlCenter.Api.Tests;

/// <summary>
/// Cada teste cria a própria API porque os aparelhos simulados guardam estado. As regras reais das cenas vivem no app
/// (Conforto.cs, --selftest-scenes); aqui o que se confere é o contrato HTTP: o status, o corpo e a validação.
/// </summary>
public sealed class DispositivosTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Lampada = "/api/v1/dispositivos/lampada";

    private static async Task<EstadoLampada> LerLampada(HttpResponseMessage r)
    {
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<EstadoLampada>(Ct))!;
    }

    [Fact]
    public async Task Cena_com_lampada_apagada_nao_acende_e_vale_para_um_ligar()
    {
        await using var api = new ApiFactory();
        var c = await api.ClienteLogadoAsync();

        var r = await c.PostAsync("/api/v1/cenas/Noite/aplicar", null, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var cena = await r.Content.ReadFromJsonAsync<ResultadoCena>(Ct);
        Assert.False(cena!.Lampada.Ligada);
        Assert.True(cena.Lampada.AjustePendente);

        var ligada = await LerLampada(await c.PutAsJsonAsync($"{Lampada}/energia", new AjusteEnergia(true), Ct));
        Assert.True(ligada.Ligada);
        Assert.Equal(1, ligada.Brilho);
        Assert.Equal(0, ligada.Temperatura);
        Assert.False(ligada.AjustePendente);

        // desligou e ligou de novo: sem cena pendente, mantém o último estado
        await c.PutAsJsonAsync($"{Lampada}/energia", new AjusteEnergia(false), Ct);
        await c.PatchAsJsonAsync(Lampada, new AjusteLampada(10, null, null, null), Ct);   // recusado, está apagada
        var religada = await LerLampada(await c.PutAsJsonAsync($"{Lampada}/energia", new AjusteEnergia(true), Ct));
        Assert.Equal(1, religada.Brilho);
    }

    [Fact]
    public async Task Ajustar_lampada_apagada_devolve_409_e_ela_continua_apagada()
    {
        await using var api = new ApiFactory();
        var c = await api.ClienteLogadoAsync();

        var r = await c.PatchAsJsonAsync(Lampada, new AjusteLampada(80, null, null, null), Ct);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        var p = await r.Content.ReadFromJsonAsync<ProblemDetails>(Ct);
        Assert.Equal(409, p!.Status);

        var estado = await c.GetFromJsonAsync<EstadoLampada>(Lampada, Ct);
        Assert.False(estado!.Ligada);
    }

    [Fact]
    public async Task Energia_sem_o_campo_ligada_nao_apaga_a_lampada()
    {
        await using var api = new ApiFactory();
        var c = await api.ClienteLogadoAsync();
        await c.PutAsJsonAsync($"{Lampada}/energia", new AjusteEnergia(true), Ct);

        var r = await c.PutAsJsonAsync($"{Lampada}/energia", new { }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.True((await c.GetFromJsonAsync<EstadoLampada>(Lampada, Ct))!.Ligada);
    }

    [Theory]
    [InlineData(0, null, null, null)]       // brilho abaixo do mínimo
    [InlineData(101, null, null, null)]     // brilho acima do máximo
    [InlineData(null, 101, null, null)]     // temperatura
    [InlineData(null, null, 360, null)]     // matiz
    [InlineData(null, null, null, 1001)]    // saturação
    [InlineData(null, null, null, null)]    // nada para ajustar
    [InlineData(null, 50, 10, null)]        // branco e cor ao mesmo tempo
    public async Task Ajuste_invalido_da_lampada_devolve_400(int? brilho, int? temperatura, int? matiz, int? saturacao)
    {
        await using var api = new ApiFactory();
        var c = await api.ClienteLogadoAsync();
        var r = await c.PatchAsJsonAsync(Lampada, new AjusteLampada(brilho, temperatura, matiz, saturacao), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Json_malformado_devolve_400_como_problem_details()
    {
        await using var api = new ApiFactory();
        var c = await api.ClienteLogadoAsync();
        var r = await c.PutAsync($"{Lampada}/energia", new StringContent("{ isto não é json", System.Text.Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Cena_inexistente_devolve_404()
    {
        await using var api = new ApiFactory();
        var c = await api.ClienteLogadoAsync();
        var r = await c.PostAsync("/api/v1/cenas/Nao%20Existe/aplicar", null, Ct);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Telas_aceitam_nome_com_espaco_e_respondem_202()
    {
        await using var api = new ApiFactory();
        var c = await api.ClienteLogadoAsync();
        var r = await c.PutAsJsonAsync("/api/v1/dispositivos/telas/Monitor%20Simulado/brilho", new AjusteBrilhoTela(33), Ct);
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        var telas = await r.Content.ReadFromJsonAsync<List<EstadoTela>>(Ct);
        Assert.Equal(33, telas!.Single(t => t.Nome == "Monitor Simulado").Brilho);
        Assert.Equal(50, telas!.Single(t => t.Nome == "Notebook Simulado").Brilho);

        Assert.Equal(HttpStatusCode.NotFound,
            (await c.PutAsJsonAsync("/api/v1/dispositivos/telas/Outra/brilho", new AjusteBrilhoTela(33), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await c.PutAsJsonAsync("/api/v1/dispositivos/telas/brilho", new AjusteBrilhoTela(101), Ct)).StatusCode);
    }

    [Fact]
    public async Task Aparencia_troca_tema_e_recusa_valor_desconhecido()
    {
        await using var api = new ApiFactory();
        var c = await api.ClienteLogadoAsync();
        var r = await c.PutAsJsonAsync("/api/v1/aparencia/tema", new AjusteTema("claro"), Ct);
        Assert.Equal("claro", (await r.Content.ReadFromJsonAsync<EstadoAparencia>(Ct))!.Tema);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await c.PutAsJsonAsync("/api/v1/aparencia/tema", new AjusteTema("azul"), Ct)).StatusCode);
    }

    [Fact]
    public async Task DualSense_ajusta_e_valida()
    {
        await using var api = new ApiFactory();
        var c = await api.ClienteLogadoAsync();
        const string rota = "/api/v1/dispositivos/dualsense";

        var r = await c.PatchAsJsonAsync(rota, new AjusteDualSense("#ff0000", 30, "xbox360", null), Ct);
        var d = await r.Content.ReadFromJsonAsync<EstadoDualSense>(Ct);
        Assert.Equal(("#FF0000", 30, "xbox360"), (d!.Cor, d.Intensidade, d.Modo));

        Assert.Equal(HttpStatusCode.BadRequest, (await c.PatchAsJsonAsync(rota, new AjusteDualSense("vermelho", null, null, null), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PatchAsJsonAsync(rota, new AjusteDualSense(null, null, "n64", null), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PatchAsJsonAsync(rota, new AjusteDualSense(null, null, null, null), Ct)).StatusCode);
    }

    [Fact]
    public async Task App_fechado_devolve_503_mas_a_api_continua_de_pe()
    {
        await using var api = new ApiFactory { Ponte = new PonteComFalha(ErrosPonte.Indisponivel, "O control-center não está rodando") };
        var c = await api.ClienteLogadoAsync();

        var r = await c.GetAsync("/api/v1/estado", Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        var p = await r.Content.ReadFromJsonAsync<ProblemDetails>(Ct);
        Assert.Contains("não está rodando", p!.Detail);

        var pronto = await c.GetAsync("/health/pronto", Ct);
        Assert.Equal(HttpStatusCode.OK, pronto.StatusCode);
        Assert.Equal("Degraded", await pronto.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Health_responde_sem_token()
    {
        await using var api = new ApiFactory();
        var c = api.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/health", Ct)).StatusCode);
        Assert.Equal("Healthy", await c.GetStringAsync("/health/pronto", Ct));
    }

    [Fact]
    public async Task OpenApi_descreve_os_endpoints_e_exige_token_onde_deve()
    {
        await using var api = new ApiFactory();
        var json = await api.CreateClient().GetStringAsync("/openapi/v1.json", Ct);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var raiz = doc.RootElement;
        Assert.Equal("http", raiz.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer").GetProperty("type").GetString());

        var caminhos = raiz.GetProperty("paths");
        Assert.True(caminhos.GetProperty("/api/v1/cenas/{nome}/aplicar").GetProperty("post").TryGetProperty("security", out _));
        Assert.False(caminhos.GetProperty("/api/v1/auth/login").GetProperty("post").TryGetProperty("security", out _));
    }
}
