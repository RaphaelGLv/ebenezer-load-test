using NBomber.Contracts.Stats;

namespace EbenezerBackend.LoadTests.Reporting;

/// <summary>
/// Escreve o <c>throughput.csv</c> na pasta da execução: o plano de carga (alvo,
/// taxa de injeção e requests HTTP/s teóricos por cenário) confrontado com o
/// throughput real medido pelo NBomber.
///
/// O objetivo é deixar explícito no relatório quanto de carga foi PEDIDO e quanto a
/// API efetivamente ENTREGOU — sem isso, um teste saturado parece apenas "lento".
///
/// Teórico e real usam a MESMA janela (a duração configurada do nível de carga), então
/// <c>attainment_percent</c> é comparável direto: 100% significa que a API absorveu toda
/// a carga injetada; valores bem abaixo indicam saturação.
/// </summary>
public static class ThroughputReportWriter
{
    /// <summary>Nome fixo do arquivo de throughput.</summary>
    public const string FileName = "throughput.csv";

    public static readonly IReadOnlyList<string> Header =
    [
        "scenario",
        "requests_per_iteration",
        "injection_rate_iterations_per_second",
        "theoretical_requests_per_second",
        "actual_requests_per_second",
        "attainment_percent",
        "duration_seconds",
        "theoretical_total_requests",
        "actual_total_requests"
    ];

    /// <summary>
    /// Extrai o total de requests HTTP concluídos por cenário a partir do
    /// <see cref="NodeStats"/>. Soma os steps (cada step = 1 request HTTP), não as
    /// iterações — o número de iterações subestimaria a carga por um fator de 8 a 19.
    /// </summary>
    public static Dictionary<string, long> ExtractRequestCounts(NodeStats stats)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var scenario in stats.ScenarioStats)
        {
            long requests = 0;
            foreach (var step in scenario.StepStats)
                requests += step.Ok.Request.Count + step.Fail.Request.Count;

            result[scenario.ScenarioName] = requests;
        }

        return result;
    }

    /// <summary>
    /// Serializa o relatório. Função pura, para permitir teste sem tocar em disco.
    /// </summary>
    /// <param name="plan">Plano resolvido antes da execução.</param>
    /// <param name="requestCounts">
    /// Requests HTTP concluídos por nome de cenário. Cenários ausentes ficam com as
    /// colunas de valor real vazias, em vez de zero, para não confundir "não medido"
    /// com "nenhum request".
    /// </param>
    public static string Build(ThroughputPlan plan, IReadOnlyDictionary<string, long> requestCounts)
    {
        var rows = new List<IReadOnlyList<string?>>();

        foreach (var scenario in plan.Scenarios)
        {
            long? actual = requestCounts.TryGetValue(scenario.ScenarioName, out var c) ? c : null;
            rows.Add(Row(
                scenario.ScenarioName,
                scenario.RequestsPerIteration.ToString(),
                scenario.InjectionRatePerSecond.ToString(),
                scenario.TheoreticalRequestsPerSecond,
                actual,
                plan.DurationSeconds));
        }

        // Linha agregada: é o número que responde "o nível de carga entregou o alvo?".
        long? totalActual = requestCounts.Count == 0 ? null : requestCounts.Values.Sum();

        rows.Add(Row(
            "TOTAL",
            string.Empty,
            plan.IterationsPerSecond.ToString(),
            plan.TheoreticalRequestsPerSecond,
            totalActual,
            plan.DurationSeconds));

        return CsvWriter.Write(Header, rows);
    }

    /// <summary>Requests HTTP/s reais: requests concluídos sobre a janela de medição.</summary>
    public static double RequestsPerSecond(long requestCount, int durationSeconds) =>
        durationSeconds > 0 ? requestCount / (double)durationSeconds : 0;

    private static IReadOnlyList<string?> Row(
        string scenario,
        string requestsPerIteration,
        string injectionRate,
        int theoreticalRps,
        long? actualRequests,
        int durationSeconds)
    {
        double? actualRps = actualRequests is null
            ? null
            : RequestsPerSecond(actualRequests.Value, durationSeconds);

        double? attainment = actualRps is not null && theoreticalRps > 0
            ? actualRps.Value / theoreticalRps * 100.0
            : null;

        return new string?[]
        {
            scenario,
            requestsPerIteration,
            injectionRate,
            theoreticalRps.ToString(),
            Format(actualRps),
            Format(attainment),
            durationSeconds.ToString(),
            ((long)theoreticalRps * durationSeconds).ToString(),
            actualRequests?.ToString() ?? string.Empty
        };
    }

    private static string Format(double? value) =>
        value is null
            ? string.Empty
            : value.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Grava o relatório. Nunca lança: em caso de erro, avisa no console e devolve <c>false</c>.
    /// </summary>
    public static bool TryWrite(string reportFolder, ThroughputPlan plan, IReadOnlyDictionary<string, long> requestCounts)
    {
        try
        {
            Directory.CreateDirectory(reportFolder);
            var path = Path.Combine(reportFolder, FileName);
            File.WriteAllText(path, Build(plan, requestCounts), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Warning] Failed to write throughput report '{FileName}': {ex.Message}");
            return false;
        }
    }
}
