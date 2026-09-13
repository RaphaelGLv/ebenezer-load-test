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
///   nível     alvo     duração
///   ------   -----     -------
///   low        100        180 s
///   medium     200        300 s
///   high       350        480 s
///   higher     500        600 s
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
/// completas/s por cenário. Os alvos acima foram dimensionados para ficar dentro do que
/// a API consegue sustentar.
/// </summary>
public sealed record LoadProfile(string Name, int TargetRequestsPerSecond, int DurationSeconds)
{
    public static LoadProfile For(string level) => (level ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        // Durações calibradas para o tempo de resposta real da API (10–50 s/request).
        // Um ciclo write_heavy completo tem 19 steps sequenciais. Com ~15 s médios cada,
        // precisa de pelo menos 165 s para garantir que as iterações consigam completar.
        //
        // 2º parâmetro = ALVO de requests HTTP/s somando os cenários ativos.
        //                                  alvo   dur
        "low"    => new LoadProfile("low",    100,  180),
        "medium" => new LoadProfile("medium", 200,  300),
        "high"   => new LoadProfile("high",   350,  480),
        "higher" => new LoadProfile("higher", 500,  600),
        _ => throw new ArgumentException(
            $"Nível de carga desconhecido: '{level}'. Use: low | medium | high | higher.")
    };
}
