using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinControl.Services;

/// <summary>
/// Leva a configuração para outro PC num arquivo só. Ele carrega as chaves das lâmpadas, então sai cifrado com uma senha:
/// pode passar por pen drive ou nuvem, mas nunca deve entrar no repositório, que é público.
/// </summary>
public static class Backup
{
    public const string Extensao = ".ccconfig";
    public const int SenhaMinima = 8;
    private const string Formato = "control-center-config";
    private const int Iteracoes = 600_000;

    // a senha da API e o log ficam de fora: são deste PC
    private static readonly string[] Arquivos = ["lamp_config.json", "conforto.json", "dualsense.json", "jogos.json", "perifericos.json"];

    public sealed class Erro(string mensagem) : Exception(mensagem);

    public static string Cifrar(IReadOnlyDictionary<string, string> arquivos, string senha)
    {
        var dados = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(arquivos));
        byte[] sal = RandomNumberGenerator.GetBytes(16), iv = RandomNumberGenerator.GetBytes(12), tag = new byte[16];
        var cifrado = new byte[dados.Length];
        using (var gcm = new AesGcm(Chave(senha, sal, Iteracoes), 16))
            gcm.Encrypt(iv, dados, cifrado, tag, Encoding.UTF8.GetBytes(Formato));
        return new JsonObject
        {
            ["formato"] = Formato,
            ["versao"] = 1,
            ["iteracoes"] = Iteracoes,
            ["sal"] = Convert.ToBase64String(sal),
            ["iv"] = Convert.ToBase64String(iv),
            ["tag"] = Convert.ToBase64String(tag),
            ["dados"] = Convert.ToBase64String(cifrado),
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static Dictionary<string, string> Decifrar(string conteudo, string senha)
    {
        JsonObject j;
        byte[] sal, iv, tag, cifrado;
        int iteracoes;
        try
        {
            j = JsonNode.Parse(conteudo) as JsonObject ?? throw new FormatException();
            if (j["formato"]?.ToString() != Formato) throw new FormatException();
            iteracoes = j["iteracoes"]!.GetValue<int>();
            sal = Convert.FromBase64String(j["sal"]!.ToString());
            iv = Convert.FromBase64String(j["iv"]!.ToString());
            tag = Convert.FromBase64String(j["tag"]!.ToString());
            cifrado = Convert.FromBase64String(j["dados"]!.ToString());
        }
        catch (Exception e) when (e is not Erro) { throw new Erro("Este arquivo não é uma configuração do Control Center."); }

        var dados = new byte[cifrado.Length];
        try
        {
            using var gcm = new AesGcm(Chave(senha, sal, iteracoes), 16);
            gcm.Decrypt(iv, cifrado, tag, dados, Encoding.UTF8.GetBytes(Formato));
        }
        catch (CryptographicException) { throw new Erro("Senha errada, ou o arquivo foi alterado."); }
        return JsonSerializer.Deserialize<Dictionary<string, string>>(dados) ?? throw new Erro("O arquivo está vazio.");
    }

    private static byte[] Chave(string senha, byte[] sal, int iteracoes) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(senha), sal, iteracoes, HashAlgorithmName.SHA256, 32);

    /// <summary>Grava na Área de Trabalho e devolve o caminho e quantas lâmpadas foram.</summary>
    public static (string caminho, int lampadas) Exportar(string senha)
    {
        var arquivos = new Dictionary<string, string>();
        foreach (var nome in Arquivos)
        {
            string caminho = Path.Combine(AppPaths.Dir, nome);
            if (File.Exists(caminho)) arquivos[nome] = File.ReadAllText(caminho);
        }
        string destino = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "control-center" + Extensao);
        File.WriteAllText(destino, Cifrar(arquivos, senha));
        Log.Write("Configuracao exportada");
        return (destino, Lamp.Todas.Count);
    }

    /// <summary>
    /// Só aceita os nomes conhecidos: um arquivo adulterado não pode escrever fora da pasta nem criar outra coisa nela.
    /// O que já existia fica ao lado, com o final .antes-da-importacao.
    /// </summary>
    public static int Importar(string caminho, string senha, string? pasta = null)
    {
        var arquivos = Decifrar(File.ReadAllText(caminho), senha);
        pasta ??= AppPaths.Dir;
        int gravados = 0;
        foreach (var nome in Arquivos)
        {
            if (!arquivos.TryGetValue(nome, out var conteudo)) continue;
            string destino = Path.Combine(pasta, nome);
            if (File.Exists(destino)) File.Copy(destino, destino + ".antes-da-importacao", overwrite: true);
            File.WriteAllText(destino, conteudo);
            gravados++;
        }
        Log.Write($"Configuracao importada: {gravados} arquivo(s)");
        return gravados;
    }
}
