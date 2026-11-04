using System.Security.Cryptography;
using System.Text.Json;
using ControlCenter.Api.Autenticacao;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;

namespace ControlCenter.Api.Infra;

/// <summary>Comandos de terminal. Eles existem para o projeto nunca precisar de uma senha ou chave padrão no código.</summary>
public static class Setup
{
    public const string ArquivoLocal = "api.settings.json";

    public static string PastaPadrao() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ControlCenter");

    public static string PastaDados(IConfiguration config, string raiz) =>
        config["Dados:Pasta"] is { Length: > 0 } pasta ? Path.GetFullPath(pasta, raiz) : PastaPadrao();

    /// <summary>
    /// A senha (em hash) e a chave do JWT ficam fora do repositório, na pasta de dados. O arquivo entra logo depois dos
    /// appsettings: variáveis de ambiente e argumentos continuam podendo sobrescrever.
    /// </summary>
    public static void CarregarConfiguracaoLocal(IConfigurationBuilder config, IConfiguration atual, string raiz)
    {
        string pasta = PastaDados(atual, raiz);
        if (!File.Exists(Path.Combine(pasta, ArquivoLocal))) return;
        var fonte = new JsonConfigurationSource { Path = ArquivoLocal, FileProvider = new PhysicalFileProvider(pasta) };
        int ultimoJson = config.Sources.ToList().FindLastIndex(s => s is JsonConfigurationSource);
        config.Sources.Insert(ultimoJson + 1, fonte);
    }

    /// <summary>Devolve o código de saída, ou null se os argumentos não são um comando daqui e a API deve subir.</summary>
    public static int? Executar(string[] args)
    {
        switch (args.FirstOrDefault())
        {
            case "hash-password" when args.Length == 2:
                Console.WriteLine(ServicoAutenticacao.GerarHash(args[1]));
                return 0;
            case "setup":
                return CriarConfiguracao(args);
            default:
                return null;
        }
    }

    private static string? Opcao(string[] args, string nome)
    {
        int i = Array.IndexOf(args, nome);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int CriarConfiguracao(string[] args)
    {
        string pasta = Opcao(args, "--pasta") ?? PastaPadrao();
        string usuario = Opcao(args, "--usuario") ?? "admin";
        string arquivo = Path.Combine(pasta, ArquivoLocal);
        if (File.Exists(arquivo) && !args.Contains("--substituir"))
        {
            Console.Error.WriteLine($"{arquivo} já existe. Use --substituir para gerar outra senha e outra chave.");
            return 1;
        }

        string senha = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');
        var config = new
        {
            Autenticacao = new
            {
                Usuario = usuario,
                SenhaHash = ServicoAutenticacao.GerarHash(senha),
                ChaveJwt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)),
            },
        };
        Directory.CreateDirectory(pasta);
        File.WriteAllText(arquivo, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine($"Configuração gravada em {arquivo}");
        Console.WriteLine($"Usuário: {usuario}");
        Console.WriteLine($"Senha:   {senha}");
        Console.WriteLine("Anote a senha agora: só o hash dela fica guardado.");
        return 0;
    }
}
