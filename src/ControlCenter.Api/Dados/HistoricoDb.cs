using Microsoft.EntityFrameworkCore;

namespace ControlCenter.Api.Dados;

public sealed class RegistroComando
{
    public long Id { get; set; }
    public DateTime QuandoUtc { get; set; }
    public required string Usuario { get; set; }
    public required string Acao { get; set; }
    public required string Alvo { get; set; }
    public string? Dados { get; set; }
    public int Status { get; set; }
    public bool Sucesso { get; set; }
    public string? Erro { get; set; }
    public long DuracaoMs { get; set; }
}

public sealed class HistoricoDb(DbContextOptions<HistoricoDb> options) : DbContext(options)
{
    public DbSet<RegistroComando> Comandos => Set<RegistroComando>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        var e = model.Entity<RegistroComando>();
        e.ToTable("Comandos");
        e.Property(x => x.Usuario).HasMaxLength(100);
        e.Property(x => x.Acao).HasMaxLength(100);
        e.Property(x => x.Alvo).HasMaxLength(300);
        e.Property(x => x.Dados).HasMaxLength(2000);
        e.Property(x => x.Erro).HasMaxLength(500);
        // o SQLite devolve a data sem fuso: sem isto ela sairia no JSON sem o "Z" e o cliente a leria como hora local
        e.Property(x => x.QuandoUtc).HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        e.HasIndex(x => x.QuandoUtc);
        e.HasIndex(x => x.Acao);
    }
}
