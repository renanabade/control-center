using System.Text.Json;
using ControlCenter.Contracts;

namespace ControlCenter.Api.Ponte;

/// <summary>
/// Aparelhos de mentira, para rodar a API sem Windows nem hardware (Docker, testes, demonstração). Repete de propósito
/// as regras do app: cena nunca acende a lâmpada, o ajuste da cena vale para um "ligar" e lâmpada apagada não aceita ajuste.
/// </summary>
public sealed class PonteSimulada : IPonte
{
    private readonly Lock _trava = new();

    private bool _ligada;
    private int _brilho = 50, _temperatura = 50, _matiz = 30, _saturacao = 1000, _timer;
    private string _modoLampada = "white";
    private (int brilho, int temperatura)? _pendente;
    private string? _cenaAtiva;
    private string _tema = "escuro";
    private bool _luzNoturna;
    private readonly Dictionary<string, int> _telas = new() { ["Monitor Simulado"] = 70, ["Notebook Simulado"] = 50 };
    private EstadoDualSense _dualSense = new(true, false, 80, "dualsense", "dualsense", false, "#0050FF", 60, null);

    private sealed record CenaSimulada(string Nome, bool? Ligada, int? Brilho, int? Temperatura, int? Telas, string? Tema, bool? LuzNoturna);

    private static readonly CenaSimulada[] Cenas =
    [
        new("Trabalho", true, 50, 50, 85, "claro", false),
        new("Noite", true, 1, 0, 0, "escuro", true),
        new("Filme", false, null, null, 40, "escuro", null),
    ];

    public Task<RespostaPonte> EnviarAsync(PedidoPonte pedido, CancellationToken ct)
    {
        lock (_trava)
        {
            try
            {
                return Task.FromResult(new RespostaPonte(true, JsonSerializer.SerializeToElement(Executar(pedido), ProtocoloPonte.Json)));
            }
            catch (PonteException e)
            {
                return Task.FromResult(new RespostaPonte(false, Erro: e.Codigo, Mensagem: e.Message));
            }
        }
    }

    private static T Args<T>(PedidoPonte p) => p.Dados!.Value.Deserialize<T>(ProtocoloPonte.Json)!;

    private object? Executar(PedidoPonte p) => p.Comando switch
    {
        ComandosPonte.Ping => true,
        ComandosPonte.Estado => Estado(),
        ComandosPonte.Cenas => Cenas.Select(c => new Cena(c.Nome, Resumo(c), c.Nome == _cenaAtiva)).ToList(),
        ComandosPonte.AplicarCena => AplicarCena(Args<ArgsCena>(p).Nome),
        ComandosPonte.LampadaEnergia => Energia(Args<AjusteEnergia>(p).Ligada!.Value),
        ComandosPonte.LampadaAjustar => Ajustar(Args<AjusteLampada>(p)),
        ComandosPonte.LampadaTimer => Timer(Args<AjusteTimer>(p).Minutos!.Value),
        ComandosPonte.TelaBrilho => BrilhoTela(Args<ArgsBrilhoTela>(p)),
        ComandosPonte.Tema => DefinirTema(Args<AjusteTema>(p).Tema!),
        ComandosPonte.LuzNoturna => DefinirLuzNoturna(Args<AjusteLuzNoturna>(p).Ligada!.Value),
        ComandosPonte.DualSense => AjustarDualSense(Args<AjusteDualSense>(p)),
        _ => throw new PonteException(ErrosPonte.Invalido, $"Comando desconhecido: {p.Comando}"),
    };

    private static string Resumo(CenaSimulada c) =>
        string.Join(" · ", new[]
        {
            c.Ligada == false ? "luz off" : c.Brilho is { } b ? $"luz {b}%" : null,
            c.Telas is { } t ? $"telas {t}%" : null,
            c.Tema,
        }.Where(x => x != null));

    private EstadoLampada Lampada() =>
        new(true, true, _ligada, _brilho, _temperatura, _modoLampada, _matiz, _saturacao, _timer, _pendente != null);

    private List<EstadoTela> Telas() =>
        _telas.Select(t => new EstadoTela(t.Key, "simulada", true, t.Value)).ToList();

    private EstadoGeral Estado() => new(Lampada(), Telas(), new EstadoAparencia(_tema, _luzNoturna), _dualSense, _cenaAtiva,
        [new ResumoLampada("Lâmpada Simulada", true, _ligada, _brilho)]);

    private ResultadoCena AplicarCena(string nome)
    {
        var c = Cenas.FirstOrDefault(x => x.Nome == nome)
            ?? throw new PonteException(ErrosPonte.NaoEncontrado, $"A cena '{nome}' não existe.");
        if (c.Tema != null) _tema = c.Tema;
        if (c.LuzNoturna is { } luz) _luzNoturna = luz;
        if (c.Telas is { } valor) foreach (var k in _telas.Keys) _telas[k] = valor;
        if (c.Ligada == false)
        {
            _ligada = false;
            _pendente = null;
        }
        else if (c.Brilho is { } b)
        {
            int t = c.Temperatura ?? _temperatura;
            _pendente = (b, t);
            if (_ligada) (_brilho, _temperatura, _modoLampada) = (b, t, "white");
        }
        _cenaAtiva = nome;
        return new ResultadoCena(nome, Lampada());
    }

    private EstadoLampada Energia(bool ligar)
    {
        if (ligar && !_ligada && _pendente is var (b, t))
            (_brilho, _temperatura, _modoLampada) = (b, t, "white");
        if (ligar) _pendente = null;     // a cena vale para um "ligar", não para sempre
        _ligada = ligar;
        _timer = 0;
        return Lampada();
    }

    private void ExigirLigada()
    {
        if (!_ligada) throw new PonteException(ErrosPonte.Conflito, "A lâmpada está desligada. Ligue-a antes de ajustar.");
    }

    private EstadoLampada Ajustar(AjusteLampada a)
    {
        ExigirLigada();
        if (a.Brilho is { } b) _brilho = b;
        if (a.Temperatura is { } t) { _temperatura = t; _modoLampada = "white"; }
        if (a.Matiz is { } h) { _matiz = h; _modoLampada = "colour"; }
        if (a.Saturacao is { } s) { _saturacao = s; _modoLampada = "colour"; }
        _cenaAtiva = null;
        return Lampada();
    }

    private EstadoLampada Timer(int minutos)
    {
        ExigirLigada();
        _timer = minutos * 60;
        return Lampada();
    }

    private List<EstadoTela> BrilhoTela(ArgsBrilhoTela a)
    {
        if (a.Tela == null)
        {
            foreach (var k in _telas.Keys) _telas[k] = a.Valor;
            return Telas();
        }
        var nome = _telas.Keys.FirstOrDefault(k => string.Equals(k, a.Tela, StringComparison.OrdinalIgnoreCase))
            ?? throw new PonteException(ErrosPonte.NaoEncontrado, $"A tela '{a.Tela}' não existe.");
        _telas[nome] = a.Valor;
        return Telas();
    }

    private EstadoAparencia DefinirTema(string tema)
    {
        _tema = tema;
        _cenaAtiva = null;
        return new EstadoAparencia(_tema, _luzNoturna);
    }

    private EstadoAparencia DefinirLuzNoturna(bool ligada)
    {
        _luzNoturna = ligada;
        _cenaAtiva = null;
        return new EstadoAparencia(_tema, _luzNoturna);
    }

    private EstadoDualSense AjustarDualSense(AjusteDualSense a)
    {
        var d = _dualSense;
        if (a.Cor != null) d = d with { Cor = a.Cor.ToUpperInvariant() };
        if (a.Intensidade is { } i) d = d with { Intensidade = i };
        if (a.Modo != null) d = d with { Modo = a.Modo, ModoEfetivo = a.Modo, Automatico = false };
        if (a.Automatico is { } auto) d = d with { Automatico = auto };
        return _dualSense = d;
    }
}
