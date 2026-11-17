using System;
using System.IO;
using System.Text.Json;

namespace EbenezerBackend.LoadTests;

/// <summary>
/// Configuração de monitoramento de recursos (CPU/memória) dos containers Docker.
/// Os nomes dos containers são derivados da abordagem informada na linha de comando.
/// </summary>
public sealed record ResourceMonitoringConfig
{
    public bool Enabled { get; init; } = true;
    public int SampleIntervalSeconds { get; init; } = 2;
}

/// <summary>
/// Abordagem de persistência sob teste, informada na execução do script.
/// Define o rótulo do relatório e quais containers Docker monitorar.
/// </summary>
public sealed record PersistenceApproach(string Key, string Label, string[] Containers)
{
    public static PersistenceApproach Parse(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "multimodel" or "multimodelo" or "arangodb" =>
            new PersistenceApproach("multimodel", "multimodel", ["arangodb"]),

        "heterogeneous" or "heterogeneo" or "heterogêneo" or "mongodb-neo4j" or "mongo-neo4j" =>
            new PersistenceApproach("heterogeneous", "heterogeneous", ["ebenezer-mongodb", "ebenezer-neo4j"]),

        _ => throw new ArgumentException(
            $"Abordagem desconhecida: '{value}'. Use: multimodel | heterogeneous.")
    };
}

/// <summary>
/// Configuração simples carregada de benchmark.config.jsonc.
/// </summary>
public sealed record LoadConfig
{
    public string BaseUrl { get; init; } = "http://localhost:5000";

    public string LoadLevel { get; init; } = "medium";
    public int WarmupSeconds { get; init; } = 5;
    public string Scenario { get; init; } = "all";
    public ResourceMonitoringConfig ResourceMonitoring { get; init; } = new();

    /// <summary>Alvo de requests HTTP/s e duração predefinidos para o nível configurado.</summary>
    public LoadProfile Profile => LoadProfile.For(LoadLevel);

    public static LoadConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Arquivo de configuração não encontrado: {path}");

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        var config = JsonSerializer.Deserialize<LoadConfig>(File.ReadAllText(path), options)
                     ?? throw new InvalidOperationException("Configuração inválida ou vazia.");

        // Valida o nível de carga cedo, antes de qualquer execução.
        _ = config.Profile;
        return config;
    }
}

/// <summary>
/// Mapeia um nível de carga ("low"/"medium"/"high"/"higher") em um ALVO de requests
/// HTTP por segundo e uma duração.
///
/// <see cref="TargetRequestsPerSecond"/> é expresso na unidade que interessa ao
/// benchmark: requests HTTP/s somando todos os cenários ativos. Ele NÃO é a taxa que
/// vai para o <c>Simulation.Inject</c> — o Inject recebe iterações/s por cenário, e
/// cada iteração dispara vários requests sequenciais:
///
///       write_heavy            = 19 requests por iteração
///       read_heavy             = 11 requests por iteração
///       graph_heavy            = 14 requests por iteração
///       document_heavy         =  8 requests por iteração
///       mixed_graph_documents  = 15 requests por iteração
///       -----------------------------------------------
///       total com "scenario": "all" = 67 requests por rodada
///
/// A conversão alvo → iterações/s por cenário é feita em tempo de execução por
/// <see cref="ThroughputPlanner"/>, que considera exatamente os cenários selecionados.
/// Ou seja, o alvo vale tanto para <c>"all"</c> quanto para um cenário isolado.
///
/// Alvos (requests HTTP/s teóricos, somando os cenários ativos):
///
///   nível     alvo     duração     teórico com "all"
///   ------   -----     -------     -----------------
///   low        500       300 s            499 req/s
///   medium    1000       600 s            997 req/s
///   high      2000       900 s          1.999 req/s
///   higher    4000      1200 s          4.001 req/s
///
/// A escada começa em 500 req/s, que é o antigo nível "higher" e já foi sustentado pelas
/// duas abordagens, e dobra a cada nível. Os alvos equivalem, em ordem de grandeza, a
/// ~144 mil, ~288 mil, ~576 mil e ~1,15 milhão de usuários ativos por dia, assumindo ~100
/// requests por usuário/dia e pico de 3x a média (DAU ≈ pico req/s x 288). As durações
/// (5 a 20 min de platô) seguem a prática de testes de carga de média duração: tempo
/// suficiente para o platô estabilizar e para a base crescer de forma relevante, sem virar
/// teste de soak.
///
/// Relação com concorrência (Lei de Little): requests em voo = req/s x latência média.
/// A 4.000 req/s com 50 ms de latência são ~200 requests simultâneos; se a latência subir
/// para 5 s, são ~20.000, acima das ~16 mil portas efêmeras padrão do Windows.
///
/// O plano resolvido (taxa por cenário e teórico atingido) é impresso no console e
/// gravado em <c>throughput.csv</c> na pasta do relatório, junto do throughput real
/// medido pelo NBomber.
///
/// O teórico é um teto de injeção, não uma promessa de vazão. <c>Inject</c> é open-loop:
/// injeta independente de as iterações anteriores terminarem. Quando a API satura, o
/// excedente vira fila de conexões e depois reset de socket, não throughput. Referência:
/// na execução medium_all de 2026-07-29 (quando o nível medium injetava 20 iterações/s
/// por cenário, ~1.340 requests/s teóricos) o register mediu 24 s de latência média e
/// ~80% de falha com SocketException 10054/10053; a vazão real ficou em ~2 iterações
/// completas/s por cenário. O nível "low" fica dentro do que as duas abordagens já
/// sustentaram; os níveis acima dele podem saturar uma das abordagens, e é isso que o
/// <c>attainment_percent</c> de <c>throughput.csv</c> deve revelar.
/// </summary>
public sealed record LoadProfile(string Name, int TargetRequestsPerSecond, int DurationSeconds)
{
    public static LoadProfile For(string level) => (level ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        // 2º parâmetro = ALVO de requests HTTP/s somando os cenários ativos.
        // 3º parâmetro = duração da fase de medição, sem contar o warm-up.
        //
        // Com a API saudável (dezenas de ms por request), a iteração mais longa (write_heavy,
        // 19 steps) termina em poucos segundos, então as durações abaixo são dimensionadas
        // pelo volume e pelo crescimento da base, não pelo tempo de uma iteração.
        //                                  alvo   dur
        "low"    => new LoadProfile("low",     500,   300),
        "medium" => new LoadProfile("medium", 1000,   600),
        "high"   => new LoadProfile("high",   2000,   900),
        "higher" => new LoadProfile("higher", 4000,  1200),
        _ => throw new ArgumentException(
            $"Nível de carga desconhecido: '{level}'. Use: low | medium | high | higher.")
    };
}
