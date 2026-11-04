using ControlCenter.Api.Autenticacao;
using ControlCenter.Api.Dados;
using ControlCenter.Api.Endpoints;
using ControlCenter.Api.Infra;
using ControlCenter.Api.Ponte;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

if (Setup.Executar(args) is { } codigo) return codigo;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService(o => o.ServiceName = "ControlCenterApi");
Setup.CarregarConfiguracaoLocal(builder.Configuration, builder.Configuration, builder.Environment.ContentRootPath);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<TratadorErros>();
builder.Services.AddValidatorsFromAssemblyContaining<Program>(includeInternalTypes: true);
builder.Services.AddLimites();

// ---------------- autenticação ----------------
builder.Services.AddOptions<OpcoesAutenticacao>()
    .BindConfiguration(OpcoesAutenticacao.Secao)
    .ValidateDataAnnotations()
    .ValidateOnStart();   // sem chave ou senha configurada, a API nem sobe: nada de rodar aberta por engano
builder.Services.AddSingleton<ServicoAutenticacao>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<OpcoesAutenticacao>>((jwt, auth) =>
    {
        var a = auth.Value;
        jwt.MapInboundClaims = false;   // mantém "sub" como "sub", sem traduzir para os nomes longos da Microsoft
        jwt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = a.Emissor,
            ValidAudience = a.Audiencia,
            IssuerSigningKey = a.ChaveAssinatura(),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });
builder.Services.AddAuthorization();

// ---------------- ponte até os aparelhos ----------------
builder.Services.AddOptions<OpcoesPonte>().BindConfiguration(OpcoesPonte.Secao);
builder.Services.AddSingleton<IPonte>(sp =>
    sp.GetRequiredService<IOptions<OpcoesPonte>>().Value.Modo.Equals("Simulada", StringComparison.OrdinalIgnoreCase)
        ? new PonteSimulada()
        : ActivatorUtilities.CreateInstance<PontePipe>(sp));

// ---------------- histórico ----------------
builder.Services.AddDbContext<HistoricoDb>((sp, o) =>
{
    string pasta = Setup.PastaDados(sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<IHostEnvironment>().ContentRootPath);
    Directory.CreateDirectory(pasta);
    o.UseSqlite($"Data Source={Path.Combine(pasta, "controlcenter.db")}");
});

builder.Services.AddHealthChecks()
    .AddDbContextCheck<HistoricoDb>("historico", tags: ["pronto"])
    .AddCheck<PonteHealthCheck>("ponte", tags: ["pronto"]);

builder.Services.AddOpenApi("v1", o =>
{
    var seguranca = new SegurancaOpenApi();
    o.AddDocumentTransformer(seguranca);
    o.AddOperationTransformer(seguranca);
});

var app = builder.Build();

// antes de atender: o histórico precisa existir no primeiro comando
using (var escopo = app.Services.CreateScope())
{
    await escopo.ServiceProvider.GetRequiredService<HistoricoDb>().Database.MigrateAsync();
}

app.UseExceptionHandler();
app.UseStatusCodePages();   // 401, 404 e 405 sem corpo também saem como ProblemDetails
app.UseAuthentication();
app.UseRateLimiter();       // depois da autenticação: o limite global é por usuário
app.UseAuthorization();

// /health só diz se o processo responde; /health/pronto também olha o banco e a ponte
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false }).DisableRateLimiting();
app.MapHealthChecks("/health/pronto", new HealthCheckOptions
{
    Predicate = c => c.Tags.Contains("pronto"),
    // "degradada" continua 200: a API está de pé, só os aparelhos é que não respondem agora
    ResultStatusCodes =
    {
        [HealthStatus.Healthy] = StatusCodes.Status200OK,
        [HealthStatus.Degraded] = StatusCodes.Status200OK,
        [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
    },
}).DisableRateLimiting();

if (app.Configuration.GetValue("Swagger:Ativo", true))
{
    app.MapOpenApi("/openapi/{documentName}.json");
    app.UseSwaggerUI(o =>
    {
        o.SwaggerEndpoint("/openapi/v1.json", "ControlCenter API v1");
        o.RoutePrefix = "swagger";
    });
}

app.MapAuth();
app.MapDispositivos();
app.MapHistorico();

await app.RunAsync();
return 0;

public partial class Program;
