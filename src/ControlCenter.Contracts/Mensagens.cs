namespace ControlCenter.Contracts;

// O que a API e o app da bandeja trocam. Os mesmos tipos são o JSON público da API, então mudar um nome aqui muda os dois.

public sealed record EstadoLampada(
    bool Configurada,
    bool Online,
    bool Ligada,
    int Brilho,
    int Temperatura,
    string Modo,
    int Matiz,
    int Saturacao,
    int TimerSegundos,
    /// <summary>Uma cena deixou um ajuste combinado para o próximo "ligar".</summary>
    bool AjustePendente);

public sealed record EstadoTela(string Nome, string Tipo, bool Responde, int? Brilho);

public sealed record EstadoAparencia(string Tema, bool LuzNoturna);

public sealed record EstadoDualSense(
    bool Conectado,
    bool Bluetooth,
    int? Bateria,
    string Modo,
    string ModoEfetivo,
    bool Automatico,
    string Cor,
    int Intensidade,
    string? Problema);

public sealed record EstadoGeral(
    /// <summary>Todas as lâmpadas da rede em que o PC está, resumidas. Os comandos da lâmpada valem para todas elas.</summary>
    EstadoLampada Lampada,
    IReadOnlyList<EstadoTela> Telas,
    EstadoAparencia Aparencia,
    EstadoDualSense DualSense,
    string? CenaAtiva,
    /// <summary>Cada lâmpada da rede em que o PC está.</summary>
    IReadOnlyList<ResumoLampada> Lampadas);

public sealed record ResumoLampada(string Nome, bool Online, bool Ligada, int Brilho);

public sealed record Cena(string Nome, string Resumo, bool Ativa);

public sealed record ResultadoCena(string Nome, EstadoLampada Lampada);

// Pedidos. Tudo anulável de propósito: num bool comum, um campo esquecido no JSON viraria "false" e apagaria a lâmpada.

public sealed record AjusteEnergia(bool? Ligada);

public sealed record AjusteLampada(int? Brilho, int? Temperatura, int? Matiz, int? Saturacao);

public sealed record AjusteTimer(int? Minutos);

public sealed record AjusteBrilhoTela(int? Valor);

public sealed record AjusteTema(string? Tema);

public sealed record AjusteLuzNoturna(bool? Ligada);

public sealed record AjusteDualSense(string? Cor, int? Intensidade, string? Modo, bool? Automatico);

// Argumentos internos da ponte (na API, esses valores vêm da rota)

public sealed record ArgsCena(string Nome);

public sealed record ArgsBrilhoTela(string? Tela, int Valor);
