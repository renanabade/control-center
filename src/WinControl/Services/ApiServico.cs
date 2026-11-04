using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WinControl.Services;

/// <summary>
/// A API é opcional: desligada, o app não abre nem o pipe e funciona como se ela não existisse. Mexer no serviço exige
/// administrador, então cada mudança passa pelo UAC. Dar ao usuário o direito de reconfigurar um serviço de sistema
/// sairia mais caro: quem troca o executável dele vira LocalService.
/// </summary>
public static class ApiServico
{
    public const string Nome = "ControlCenterApi";
    public const int PortaPadrao = 5080;
    private const string ChaveServico = @"SYSTEM\CurrentControlSet\Services\" + Nome;
    private static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };

    public sealed record Estado(bool Instalado, bool Rodando, bool SempreLigada, int Porta, string? Exe);

    /// <summary>Sem a opção salva, vale o serviço instalado: em PC novo a API fica desligada, e quem já a instalou não a perde.</summary>
    public static bool Ativa => Conforto.Conf["api_ativa"]?.GetValue<bool>() ?? Ler().Instalado;

    public static string Usuario => Conforto.Conf["api_usuario"]?.ToString() is { Length: > 0 } u ? u : "admin";

    public static Estado Ler()
    {
        using var k = Registry.LocalMachine.OpenSubKey(ChaveServico);
        if (k == null) return new(false, false, false, PortaPadrao, null);
        int porta = PortaPadrao;
        if (k.GetValue("Environment") is string[] env)
            foreach (var linha in env)
                if (Regex.Match(linha, @"^Urls=.*:(\d+)/?$") is { Success: true } m) porta = int.Parse(m.Groups[1].Value);
        bool rodando = false;
        try
        {
            using var sc = new ServiceController(Nome);
            rodando = sc.Status == ServiceControllerStatus.Running;
        }
        catch (Exception e) { Log.Write($"API: nao consegui ler o servico ({e.Message})"); }
        return new(true, rodando, k.GetValue("Start") is int s && s == 2, porta, (k.GetValue("ImagePath") as string)?.Trim().Trim('"'));
    }

    /// <summary>"Healthy", "Degraded" ou null se a porta não responde.</summary>
    public static async Task<string?> Saude(int porta)
    {
        try { return await Http.GetStringAsync($"http://127.0.0.1:{porta}/health/pronto"); }
        catch { return null; }
    }

    /// <summary>O firewall só abre a porta em redes particulares: nas públicas, os outros aparelhos não conectam.</summary>
    public static List<string> RedesPublicas()
    {
        var nomes = new List<string>();
        try
        {
            using var busca = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\StandardCimv2"),
                new ObjectQuery("SELECT InterfaceAlias, NetworkCategory FROM MSFT_NetConnectionProfile"));
            foreach (var o in busca.Get())
                if (Convert.ToInt32(o["NetworkCategory"]) == 0) nomes.Add(o["InterfaceAlias"]?.ToString() ?? "?");
        }
        catch (Exception e) { Log.Write($"API: nao consegui ler as redes ({e.Message})"); }
        return nomes;
    }

    public static List<string> Enderecos(int porta) =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.GetIPProperties().GatewayAddresses.Count > 0)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => $"http://{a.Address}:{porta}/swagger")
            .ToList();

    /// <summary>Procura subindo a partir do exe: os scripts vivem no repositório, e o app roda de dentro de bin\.</summary>
    public static string? Script(string nome)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            string caminho = Path.Combine(d.FullName, "tools", nome);
            if (File.Exists(caminho)) return caminho;
        }
        return null;
    }

    public static string? ValidarInstalacao(double porta, string usuario)
    {
        if (double.IsNaN(porta) || porta != Math.Floor(porta) || porta is < 1024 or > 65535) return "A porta precisa ser um número de 1024 a 65535.";
        if (!Regex.IsMatch(usuario, @"^[A-Za-z0-9._-]{1,64}$")) return "O usuário só pode ter letras, números, ponto, hífen e sublinhado.";
        return null;
    }

    // ---------------- ligar e desligar ----------------
    private static void Ativar()
    {
        Conforto.SetOption("api_ativa", true);
        ApiBridge.Start();
    }

    private static void Desativar()
    {
        ApiBridge.Stop();
        Conforto.SetOption("api_ativa", false);
    }

    /// <summary>Todos devolvem a mensagem de erro, ou null se deu certo.</summary>
    public static async Task<string?> Ligar()
    {
        Ativar();
        if (Ler().Rodando) return null;
        var erro = await Elevar($"Start-Service {Nome}");
        if (erro != null) Desativar();
        return erro;
    }

    /// <summary>Desligar também tira do início automático: desligada, ela não pode voltar sozinha no próximo boot.</summary>
    public static async Task<string?> Desligar()
    {
        if (Ler().Instalado)
        {
            var erro = await Elevar($"Set-Service {Nome} -StartupType Manual; Stop-Service {Nome} -Force");
            if (erro != null) return erro;
        }
        Desativar();
        return null;
    }

    public static async Task<string?> DefinirSempreLigada(bool sempre)
    {
        if (!sempre) return await Elevar($"Set-Service {Nome} -StartupType Manual");
        Ativar();
        return await Elevar($"Set-Service {Nome} -StartupType Automatic; Start-Service {Nome}");
    }

    /// <summary>Em janela própria: é nela que a senha aparece, uma única vez, sem passar pelo app.</summary>
    public static string? Instalar(int porta, string usuario)
    {
        if (Script("instalar-servico.ps1") is not { } script) return "Não achei tools\\instalar-servico.ps1 perto do programa.";
        Conforto.Conf["api_usuario"] = usuario;
        Ativar();     // antes do script: ele confere o /health/pronto, que depende da ponte
        var (p, erro) = Abrir($"& {Aspas(script)} -Porta {porta} -Usuario {Aspas(usuario)}", visivel: true);
        if (p == null) { Desativar(); return erro; }
        _ = Task.Run(async () =>
        {
            using (p) await p.WaitForExitAsync();
            // desistiu ou falhou antes de criar o serviço: a ponte não fica aberta à toa
            if (!Ler().Instalado) Ui.Run(Desativar);
        });
        return null;
    }

    /// <summary>A chave do JWT é trocada junto, então os tokens já emitidos deixam de valer.</summary>
    public static string? TrocarSenha(string usuario)
    {
        if (Ler().Exe is not { } exe) return "O serviço não está instalado.";
        var (p, erro) = Abrir($"& {Aspas(exe)} setup --substituir --usuario {Aspas(usuario)}; if ($LASTEXITCODE -eq 0) {{ Restart-Service {Nome} }}", visivel: true);
        p?.Dispose();
        if (p != null) { Conforto.Conf["api_usuario"] = usuario; Conforto.Save(); }
        return erro;
    }

    public static async Task<string?> Remover()
    {
        if (Script("remover-servico.ps1") is not { } script) return "Não achei tools\\remover-servico.ps1 perto do programa.";
        var erro = await Elevar($"& {Aspas(script)}");
        if (erro == null) Desativar();
        return erro;
    }

    private static string Aspas(string s) => "'" + s.Replace("'", "''") + "'";

    private static async Task<string?> Elevar(string comando)
    {
        var (p, erro) = Abrir("$ErrorActionPreference = 'Stop'; " + comando, visivel: false);
        if (p == null) return erro;
        using (p)
        {
            await p.WaitForExitAsync();
            return p.ExitCode == 0 ? null : "O Windows não conseguiu concluir o comando no serviço.";
        }
    }

    /// <summary>EncodedCommand para não depender de nenhuma regra de aspas entre o app, o UAC e o PowerShell.</summary>
    private static (Process? processo, string? erro) Abrir(string comando, bool visivel)
    {
        try
        {
            string b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(comando));
            return (Process.Start(new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass {(visivel ? "-NoExit " : "")}-EncodedCommand {b64}",
                WindowStyle = visivel ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden,
            }), null);
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { return (null, "Cancelado na tela do administrador. Nada mudou."); }
        catch (Exception e) { return (null, e.Message); }
    }
}
