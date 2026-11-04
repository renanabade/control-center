using ControlCenter.Api.Dados;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ControlCenter.Api.Endpoints;

public sealed record ItemHistorico(
    long Id, DateTime QuandoUtc, string Usuario, string Acao, string Alvo, string? Dados, int Status, bool Sucesso, string? Erro, long DuracaoMs);

public sealed record PaginaHistorico(IReadOnlyList<ItemHistorico> Itens, int Pagina, int TamanhoPagina, int Total, int TotalPaginas);

public sealed record FiltroConsultaHistorico(int Pagina, int TamanhoPagina, string? Acao, bool? Sucesso, DateTime? De, DateTime? Ate);

public sealed class FiltroConsultaHistoricoValidator : AbstractValidator<FiltroConsultaHistorico>
{
    public FiltroConsultaHistoricoValidator()
    {
        RuleFor(x => x.Pagina).GreaterThanOrEqualTo(1);
        RuleFor(x => x.TamanhoPagina).InclusiveBetween(1, 100);
        RuleFor(x => x.Acao).MaximumLength(100);
        RuleFor(x => x).Must(x => x.De == null || x.Ate == null || x.De <= x.Ate)
            .WithName("periodo").WithMessage("'de' deve ser anterior a 'ate'.");
    }
}

public static class HistoricoEndpoints
{
    public static void MapHistorico(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/historico", Listar)
            .RequireAuthorization()
            .WithTags("Histórico")
            .WithName("ListarHistorico")
            .WithSummary("Comandos executados, do mais recente para o mais antigo.")
            .ProducesValidationProblem();
    }

    private static async Task<IResult> Listar(
        HistoricoDb db,
        IValidator<FiltroConsultaHistorico> validador,
        CancellationToken ct,
        [FromQuery] int pagina = 1,
        [FromQuery] int tamanhoPagina = 20,
        [FromQuery] string? acao = null,
        [FromQuery] bool? sucesso = null,
        [FromQuery] DateTime? de = null,
        [FromQuery] DateTime? ate = null)
    {
        var filtro = new FiltroConsultaHistorico(pagina, tamanhoPagina, acao, sucesso, de?.ToUniversalTime(), ate?.ToUniversalTime());
        var validacao = await validador.ValidateAsync(filtro, ct);
        if (!validacao.IsValid) return Results.ValidationProblem(validacao.ToDictionary());

        var consulta = db.Comandos.AsNoTracking();
        if (filtro.Acao != null) consulta = consulta.Where(x => x.Acao == filtro.Acao);
        if (filtro.Sucesso != null) consulta = consulta.Where(x => x.Sucesso == filtro.Sucesso);
        if (filtro.De != null) consulta = consulta.Where(x => x.QuandoUtc >= filtro.De);
        if (filtro.Ate != null) consulta = consulta.Where(x => x.QuandoUtc <= filtro.Ate);

        int total = await consulta.CountAsync(ct);
        var itens = await consulta
            .OrderByDescending(x => x.Id)     // o Id cresce com o tempo e, diferente da data, nunca empata entre páginas
            .Skip((filtro.Pagina - 1) * filtro.TamanhoPagina)
            .Take(filtro.TamanhoPagina)
            .Select(x => new ItemHistorico(x.Id, x.QuandoUtc, x.Usuario, x.Acao, x.Alvo, x.Dados, x.Status, x.Sucesso, x.Erro, x.DuracaoMs))
            .ToListAsync(ct);

        int paginas = (int)Math.Ceiling(total / (double)filtro.TamanhoPagina);
        return Results.Ok(new PaginaHistorico(itens, filtro.Pagina, filtro.TamanhoPagina, total, paginas));
    }
}
