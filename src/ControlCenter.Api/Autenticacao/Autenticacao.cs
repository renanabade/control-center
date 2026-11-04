using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace ControlCenter.Api.Autenticacao;

public sealed class OpcoesAutenticacao : IValidatableObject
{
    public const string Secao = "Autenticacao";

    [Required] public string Usuario { get; set; } = "";
    [Required] public string SenhaHash { get; set; } = "";
    [Required] public string ChaveJwt { get; set; } = "";
    public string Emissor { get; set; } = "control-center";
    public string Audiencia { get; set; } = "control-center-api";
    [Range(1, 720)] public int ValidadeHoras { get; set; } = 12;

    public SymmetricSecurityKey ChaveAssinatura() => new(Encoding.UTF8.GetBytes(ChaveJwt));

    public IEnumerable<ValidationResult> Validate(ValidationContext _)
    {
        if (Encoding.UTF8.GetByteCount(ChaveJwt) < 32)
            yield return new ValidationResult("ChaveJwt precisa de pelo menos 32 bytes. Rode 'ControlCenter.Api setup'.");
    }
}

public sealed record PedidoLogin(string? Usuario, string? Senha);

public sealed record RespostaLogin(string Token, DateTime ExpiraEmUtc);

public sealed class ServicoAutenticacao(IOptions<OpcoesAutenticacao> opcoes, TimeProvider relogio)
{
    private static readonly PasswordHasher<string> Hasher = new();

    /// <summary>
    /// Confere a senha mesmo com o usuário errado: responder mais rápido nesse caso revelaria quais usuários existem.
    /// </summary>
    public bool Validar(string usuario, string senha)
    {
        var o = opcoes.Value;
        bool usuarioOk = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(usuario), Encoding.UTF8.GetBytes(o.Usuario));
        bool senhaOk = Hasher.VerifyHashedPassword(o.Usuario, o.SenhaHash, senha) != PasswordVerificationResult.Failed;
        return usuarioOk && senhaOk;
    }

    public RespostaLogin EmitirToken(string usuario)
    {
        var o = opcoes.Value;
        var agora = relogio.GetUtcNow().UtcDateTime;
        var expira = agora.AddHours(o.ValidadeHoras);
        var token = new JwtSecurityToken(
            o.Emissor, o.Audiencia,
            [new Claim(JwtRegisteredClaimNames.Sub, usuario), new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))],
            agora, expira,
            new SigningCredentials(o.ChaveAssinatura(), SecurityAlgorithms.HmacSha256));
        return new RespostaLogin(new JwtSecurityTokenHandler().WriteToken(token), expira);
    }

    public static string GerarHash(string senha) => Hasher.HashPassword("", senha);
}
