using System.Net.Http.Headers;
using System.Net.Http.Json;
using ControlCenter.Api.Autenticacao;
using ControlCenter.Api.Ponte;
using ControlCenter.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ControlCenter.Api.Tests;

/// <summary>A API inteira em memória, com a ponte simulada e um SQLite próprio em pasta temporária.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string Usuario = "teste";
    public const string Senha = "senha-de-teste";

    private static readonly string Hash = ServicoAutenticacao.GerarHash(Senha);
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), "cc-testes-" + Guid.NewGuid().ToString("N"));

    public int LoginPorMinuto { get; init; } = 1000;
    public int PedidosPorMinuto { get; init; } = 100_000;
    /// <summary>Troca a ponte, para simular o app fechado ou um defeito.</summary>
    public IPonte? Ponte { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Dados:Pasta"] = _pasta,
            ["Ponte:Modo"] = "Simulada",
            ["Autenticacao:Usuario"] = Usuario,
            ["Autenticacao:SenhaHash"] = Hash,
            ["Autenticacao:ChaveJwt"] = "chave-so-para-os-testes-com-mais-de-32-bytes",
            ["Limites:LoginPorMinuto"] = LoginPorMinuto.ToString(),
            ["Limites:PedidosPorMinuto"] = PedidosPorMinuto.ToString(),
        }));
        builder.ConfigureTestServices(services =>
        {
            if (Ponte != null) services.AddSingleton(Ponte);
        });
    }

    public async Task<HttpClient> ClienteLogadoAsync()
    {
        var cliente = CreateClient();
        var r = await cliente.PostAsJsonAsync("/api/v1/auth/login", new PedidoLogin(Usuario, Senha));
        r.EnsureSuccessStatusCode();
        var login = await r.Content.ReadFromJsonAsync<RespostaLogin>();
        cliente.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.Token);
        return cliente;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        SqliteConnection.ClearAllPools();   // senão o Windows não deixa apagar o arquivo do banco
        try { Directory.Delete(_pasta, recursive: true); } catch (IOException) { }
    }
}

/// <summary>Ponte que sempre falha do mesmo jeito.</summary>
public sealed class PonteComFalha(string codigo, string mensagem) : IPonte
{
    public Task<RespostaPonte> EnviarAsync(PedidoPonte pedido, CancellationToken ct) =>
        Task.FromResult(new RespostaPonte(false, Erro: codigo, Mensagem: mensagem));
}
