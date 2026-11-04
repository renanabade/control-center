using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace ControlCenter.Api.Infra;

public sealed class OpcoesLimites
{
    public const string Secao = "Limites";

    public int LoginPorMinuto { get; set; } = 5;
    public int PedidosPorMinuto { get; set; } = 120;
}

public static class Limites
{
    public const string Login = "login";

    public static IServiceCollection AddLimites(this IServiceCollection services)
    {
        services.AddOptions<OpcoesLimites>().BindConfiguration(OpcoesLimites.Secao);
        return services.AddRateLimiter(r =>
        {
            r.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // apertado no login, por IP: é o único endpoint em que dá para tentar adivinhar uma senha
            r.AddPolicy(Login, http =>
            {
                int limite = http.RequestServices.GetRequiredService<IOptions<OpcoesLimites>>().Value.LoginPorMinuto;
                return RateLimitPartition.GetFixedWindowLimiter(
                    http.Connection.RemoteIpAddress?.ToString() ?? "?",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = limite, Window = TimeSpan.FromMinutes(1) });
            });

            // o resto é por usuário: protege a lâmpada e os monitores de um cliente em laço
            r.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
            {
                int limite = http.RequestServices.GetRequiredService<IOptions<OpcoesLimites>>().Value.PedidosPorMinuto;
                return RateLimitPartition.GetSlidingWindowLimiter(
                    http.User.FindFirstValue("sub") ?? http.Connection.RemoteIpAddress?.ToString() ?? "?",
                    _ => new SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = limite,
                        Window = TimeSpan.FromMinutes(1),
                        SegmentsPerWindow = 6,
                    });
            });

            r.OnRejected = async (ctx, ct) =>
            {
                var http = ctx.HttpContext;
                if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var espera))
                    http.Response.Headers.RetryAfter = ((int)espera.TotalSeconds).ToString();
                var problemas = http.RequestServices.GetRequiredService<IProblemDetailsService>();
                await problemas.TryWriteAsync(new ProblemDetailsContext
                {
                    HttpContext = http,
                    ProblemDetails = new ProblemDetails
                    {
                        Status = StatusCodes.Status429TooManyRequests,
                        Title = "Muitos pedidos",
                        Detail = "Espere um pouco antes de tentar de novo.",
                    },
                });
            };
        });
    }
}
