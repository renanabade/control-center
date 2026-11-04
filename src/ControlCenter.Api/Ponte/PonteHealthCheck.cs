using ControlCenter.Contracts;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ControlCenter.Api.Ponte;

/// <summary>App da bandeja fechado deixa a API "degradada", não fora do ar: login e histórico continuam funcionando.</summary>
public sealed class PonteHealthCheck(IPonte ponte) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await ponte.PedirAsync<bool>(ComandosPonte.Ping, null, ct);
            return HealthCheckResult.Healthy();
        }
        catch (PonteException e)
        {
            return HealthCheckResult.Degraded(e.Message);
        }
    }
}
