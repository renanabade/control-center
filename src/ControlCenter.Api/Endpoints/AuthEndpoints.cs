using ControlCenter.Api.Autenticacao;
using ControlCenter.Api.Infra;

namespace ControlCenter.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/auth/login", Login)
            .WithTags("Autenticação")
            .WithName("Login")
            .WithSummary("Troca usuário e senha por um token JWT.")
            .AllowAnonymous()
            .RequireRateLimiting(Limites.Login)
            .AddEndpointFilter<FiltroValidacao<PedidoLogin>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
    }

    private static IResult Login(PedidoLogin pedido, ServicoAutenticacao auth, ILogger<ServicoAutenticacao> log, HttpContext http)
    {
        if (!auth.Validar(pedido.Usuario!, pedido.Senha!))
        {
            log.LogWarning("Login recusado vindo de {Ip}", http.Connection.RemoteIpAddress);
            return Results.Problem("Usuário ou senha incorretos.", statusCode: StatusCodes.Status401Unauthorized, title: "Não autorizado");
        }
        return Results.Ok(auth.EmitirToken(pedido.Usuario!));
    }
}
