using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ControlCenter.Api.Infra;

/// <summary>Coloca o botão "Authorize" no Swagger e marca o cadeado só nos endpoints que exigem token.</summary>
public sealed class SegurancaOpenApi : IOpenApiDocumentTransformer, IOpenApiOperationTransformer
{
    private const string Esquema = "Bearer";

    public Task TransformAsync(OpenApiDocument doc, OpenApiDocumentTransformerContext ctx, CancellationToken ct)
    {
        doc.Info.Title = "ControlCenter API";
        doc.Info.Description = "Controle remoto do control-center: cenas, lâmpada, telas, aparência e DualSense.";
        doc.Components ??= new OpenApiComponents();
        doc.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        doc.Components.SecuritySchemes[Esquema] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "Token de POST /api/v1/auth/login",
        };
        return Task.CompletedTask;
    }

    public Task TransformAsync(OpenApiOperation op, OpenApiOperationTransformerContext ctx, CancellationToken ct)
    {
        var meta = ctx.Description.ActionDescriptor.EndpointMetadata;
        if (meta.OfType<IAuthorizeData>().Any() && !meta.OfType<IAllowAnonymous>().Any())
        {
            op.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(Esquema, ctx.Document)] = [] }];
            op.Responses ??= new OpenApiResponses();
            op.Responses.TryAdd("401", new OpenApiResponse { Description = "Token ausente, inválido ou vencido." });
        }
        return Task.CompletedTask;
    }
}
