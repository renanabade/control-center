using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace WinControl.Services;

/// <summary>
/// Em que lugar o PC está, para mostrar só as lâmpadas de lá. O lugar é o MAC do roteador: não muda entre 2,4 e 5 GHz nem
/// entre cabo e Wi-Fi da mesma casa, e dois roteadores com o mesmo nome de rede continuam sendo lugares diferentes.
/// </summary>
public static class Rede
{
    private static readonly object Gate = new();
    private static IReadOnlyList<string> _atuais = [];
    private static CancellationTokenSource? _espera;

    /// <summary>A rede mudou de verdade (o aviso do Windows chega várias vezes a cada troca).</summary>
    public static event Action? Mudou;

    public static IReadOnlyList<string> Atuais => _atuais;

    public static void Start()
    {
        _atuais = Ler();
        NetworkChange.NetworkAddressChanged += (_, _) => Agendar();
    }

    private static void Agendar()
    {
        CancellationToken t;
        lock (Gate)
        {
            _espera?.Cancel();
            _espera = new CancellationTokenSource();
            t = _espera.Token;
        }
        // espera assentar: logo depois da troca, o roteador novo ainda nem responde ao ARP
        Task.Delay(TimeSpan.FromSeconds(3), t).ContinueWith(d =>
        {
            if (d.IsCanceled) return;
            var novas = Ler();
            if (novas.SequenceEqual(_atuais)) return;
            _atuais = novas;
            Log.Write($"Rede mudou: {novas.Count} rede(s) local(is) ativa(s)");
            try { Mudou?.Invoke(); } catch (Exception e) { Log.Write($"erro ao trocar de rede: {e.Message}"); }
        }, TaskScheduler.Default);
    }

    private static List<string> Ler()
    {
        var ids = new List<string>();
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up || !EhFisica(n)) continue;
                var p = n.GetIPProperties();
                var gw = p.GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
                var ip = p.UnicastAddresses.Select(u => u.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (gw == null || ip == null) continue;
                ids.Add(MacDe(gw, ip) is { } mac ? $"gw:{mac}" : $"ip:{gw}");   // sem ARP, o IP do roteador ainda serve de pista
            }
        }
        catch (Exception e) { Log.Write($"erro ao ler as redes: {e.Message}"); }
        return ids.Distinct().Order().ToList();
    }

    /// <summary>Os outros endereços da rede local. Redes maiores que /22 ficam de fora: varrer tudo aquilo não seria mais leve.</summary>
    public static List<string> Vizinhos()
    {
        var ips = new List<string>();
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && EhFisica(n)))
            {
                var p = n.GetIPProperties();
                if (p.GatewayAddresses.Count == 0) continue;
                foreach (var u in p.UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork && u.PrefixLength is >= 22 and < 31))
                {
                    uint meu = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(u.Address.GetAddressBytes());
                    uint mascara = uint.MaxValue << (32 - u.PrefixLength);
                    for (uint h = 1; h < ~mascara; h++)
                    {
                        uint ip = (meu & mascara) | h;
                        if (ip == meu) continue;
                        var b = new byte[4];
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b, ip);
                        ips.Add(new IPAddress(b).ToString());
                    }
                }
            }
        }
        catch (Exception e) { Log.Write($"erro ao listar a rede local: {e.Message}"); }
        return ips;
    }

    // VPN não é lugar: com ela ligada, o PC continua na mesma casa
    private static bool EhFisica(NetworkInterface n)
    {
        if (n.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet)) return false;
        string d = (n.Description + " " + n.Name).ToLowerInvariant();
        return !new[] { "vpn", "tap-", "wintun", "wireguard", "tailscale", "virtual", "hyper-v", "vmware", "vethernet", "loopback" }.Any(d.Contains);
    }

    private static string? MacDe(IPAddress alvo, IPAddress origem)
    {
        var mac = new byte[6];
        uint tam = 6;
        int r = SendARP(BitConverter.ToUInt32(alvo.GetAddressBytes()), BitConverter.ToUInt32(origem.GetAddressBytes()), mac, ref tam);
        return r == 0 && tam == 6 ? Convert.ToHexString(mac) : null;
    }

    [DllImport("iphlpapi.dll")] private static extern int SendARP(uint destino, uint origem, byte[] mac, ref uint tamanho);
}
