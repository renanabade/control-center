using ControlCenter.Api.Autenticacao;
using ControlCenter.Contracts;
using FluentValidation;

namespace ControlCenter.Api.Validacao;

public sealed class PedidoLoginValidator : AbstractValidator<PedidoLogin>
{
    public PedidoLoginValidator()
    {
        RuleFor(x => x.Usuario).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Senha).NotEmpty().MaximumLength(200);
    }
}

public sealed class AjusteEnergiaValidator : AbstractValidator<AjusteEnergia>
{
    public AjusteEnergiaValidator() => RuleFor(x => x.Ligada).NotNull();
}

public sealed class AjusteLampadaValidator : AbstractValidator<AjusteLampada>
{
    public AjusteLampadaValidator()
    {
        RuleFor(x => x.Brilho).InclusiveBetween(1, 100);
        RuleFor(x => x.Temperatura).InclusiveBetween(0, 100);
        RuleFor(x => x.Matiz).InclusiveBetween(0, 359);
        RuleFor(x => x.Saturacao).InclusiveBetween(0, 1000);
        RuleFor(x => x)
            .Must(x => x.Brilho != null || x.Temperatura != null || x.Matiz != null || x.Saturacao != null)
            .WithName("ajuste").WithMessage("Informe ao menos um entre brilho, temperatura, matiz e saturacao.");
        // a lâmpada está em modo branco ou em modo cor: pedir os dois junto não tem resposta certa
        RuleFor(x => x)
            .Must(x => x.Temperatura == null || (x.Matiz == null && x.Saturacao == null))
            .WithName("ajuste").WithMessage("Temperatura (branco) não combina com matiz ou saturação (cor).");
    }
}

public sealed class AjusteTimerValidator : AbstractValidator<AjusteTimer>
{
    public AjusteTimerValidator() => RuleFor(x => x.Minutos).NotNull().InclusiveBetween(0, 1440);
}

public sealed class AjusteBrilhoTelaValidator : AbstractValidator<AjusteBrilhoTela>
{
    public AjusteBrilhoTelaValidator() => RuleFor(x => x.Valor).NotNull().InclusiveBetween(0, 100);
}

public sealed class AjusteTemaValidator : AbstractValidator<AjusteTema>
{
    public AjusteTemaValidator() =>
        RuleFor(x => x.Tema).NotNull().Must(t => t is "escuro" or "claro").WithMessage("O tema deve ser 'escuro' ou 'claro'.");
}

public sealed class AjusteLuzNoturnaValidator : AbstractValidator<AjusteLuzNoturna>
{
    public AjusteLuzNoturnaValidator() => RuleFor(x => x.Ligada).NotNull();
}

public sealed class AjusteDualSenseValidator : AbstractValidator<AjusteDualSense>
{
    public AjusteDualSenseValidator()
    {
        RuleFor(x => x.Cor).Matches("^#[0-9a-fA-F]{6}$").WithMessage("A cor deve estar no formato #RRGGBB.");
        RuleFor(x => x.Intensidade).InclusiveBetween(0, 100);
        RuleFor(x => x.Modo).Must(m => m is null or "dualsense" or "xbox360" or "ps4")
            .WithMessage("O modo deve ser 'dualsense', 'xbox360' ou 'ps4'.");
        RuleFor(x => x)
            .Must(x => x.Cor != null || x.Intensidade != null || x.Modo != null || x.Automatico != null)
            .WithName("ajuste").WithMessage("Informe ao menos um entre cor, intensidade, modo e automatico.");
    }
}
