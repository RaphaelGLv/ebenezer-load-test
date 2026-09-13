namespace EbenezerBackend.LoadTests;

/// <summary>
/// Forma estática de um cenário: quantos requests HTTP uma iteração completa dispara.
/// É a contagem de <c>Step.Run(...)</c> declarados no cenário — o teto por iteração,
/// já que steps podem ser pulados quando um step anterior falha.
/// </summary>
public sealed record ScenarioShape(string Key, string Name, int RequestsPerIteration);

/// <summary>
/// Taxa de injeção resolvida para um cenário e o throughput teórico que ela produz.
/// </summary>
public sealed record ScenarioPlan(string ScenarioName, int RequestsPerIteration, int InjectionRatePerSecond)
{
    /// <summary>Requests HTTP/s teóricos deste cenário (iterações/s × requests por iteração).</summary>
    public int TheoreticalRequestsPerSecond => RequestsPerIteration * InjectionRatePerSecond;
}

/// <summary>
/// Plano de carga da execução: alvo de requests HTTP/s, taxa de injeção resolvida por
/// cenário e o throughput teórico resultante.
/// </summary>
public sealed record ThroughputPlan(
    string LoadLevel,
    int TargetRequestsPerSecond,
    int DurationSeconds,
    IReadOnlyList<ScenarioPlan> Scenarios)
{
    /// <summary>Soma dos requests HTTP/s teóricos de todos os cenários ativos.</summary>
    public int TheoreticalRequestsPerSecond => Scenarios.Sum(s => s.TheoreticalRequestsPerSecond);

    /// <summary>Iterações/s injetadas somando todos os cenários ativos.</summary>
    public int IterationsPerSecond => Scenarios.Sum(s => s.InjectionRatePerSecond);

    /// <summary>Volume teórico de requests na duração completa (sem contar o warm-up).</summary>
    public long TheoreticalTotalRequests => (long)TheoreticalRequestsPerSecond * DurationSeconds;

    /// <summary>Desvio do teórico em relação ao alvo, em pontos percentuais.</summary>
    public double DeviationFromTargetPercent => TargetRequestsPerSecond == 0
        ? 0
        : (TheoreticalRequestsPerSecond - (double)TargetRequestsPerSecond) / TargetRequestsPerSecond * 100.0;
}

/// <summary>
/// Resolve as taxas de injeção por cenário a partir de um alvo de requests HTTP/s.
///
/// O <c>Simulation.Inject</c> do NBomber recebe iterações/s por cenário, e cada iteração
/// dispara N requests sequenciais. O alvo em requests/s, portanto, precisa ser
/// distribuído entre os cenários ativos.
///
/// Duas restrições guiam a distribuição:
///
/// 1) A taxa é um inteiro ≥ 1. Taxa 0 desligaria o cenário e mudaria silenciosamente o
///    mix de carga sob teste.
/// 2) O mix deve ser preservado. Todos os cenários recebem a mesma taxa base e, no
///    máximo, +1 iteração/s para fechar o resto da divisão — as taxas nunca diferem
///    entre si por mais de 1. Sem isso, uma busca que só minimizasse o erro em relação
///    ao alvo concentraria a carga no cenário com mais steps, distorcendo o benchmark.
///
/// A busca é exaustiva sobre (taxa base) × (subconjunto que recebe +1), o que é trivial
/// para até 5 cenários, e escolhe o teórico mais próximo do alvo, com empate resolvido
/// pelo menor número de incrementos.
///
/// Resultado com <c>"scenario": "all"</c> (19 + 11 + 14 + 8 + 15 = 67 requests por rodada):
///
///   nível     alvo    taxa por cenário                      teórico
///   ------   -----    ----------------------------------    -------
///   low        100    write 2, graph 2, demais 1            100 req/s
///   medium     200    todos 3                               201 req/s
///   high       350    mixed 6, demais 5                     350 req/s
///   higher     500    write 8, read 8, demais 7             499 req/s
/// </summary>
public static class ThroughputPlanner
{
    /// <summary>Limite de cenários para a busca exaustiva de subconjuntos (2^n).</summary>
    private const int MaxScenariosForExhaustiveSearch = 16;

    public static ThroughputPlan Plan(LoadProfile profile, IReadOnlyList<ScenarioShape> activeScenarios)
    {
        if (activeScenarios.Count == 0)
            throw new ArgumentException("Nenhum cenário ativo para planejar.", nameof(activeScenarios));

        var target = profile.TargetRequestsPerSecond;
        var requestsPerRound = activeScenarios.Sum(s => s.RequestsPerIteration);
        var rates = Resolve(activeScenarios, requestsPerRound, target);

        var plans = activeScenarios
            .Select((s, i) => new ScenarioPlan(s.Name, s.RequestsPerIteration, rates[i]))
            .ToList();

        return new ThroughputPlan(profile.Name, target, profile.DurationSeconds, plans);
    }

    private static int[] Resolve(IReadOnlyList<ScenarioShape> scenarios, int requestsPerRound, int target)
    {
        var count = scenarios.Count;

        // Taxa base candidata: piso e teto da divisão do alvo pela rodada completa.
        var floorRate = Math.Max(1, target / requestsPerRound);
        var baseCandidates = new[] { floorRate, floorRate + 1 };

        if (count > MaxScenariosForExhaustiveSearch)
        {
            // Salvaguarda: sem busca de subconjuntos, fica só a taxa base mais próxima.
            var best = baseCandidates
                .OrderBy(r => Math.Abs(r * requestsPerRound - target))
                .ThenBy(r => r)
                .First();
            var uniform = new int[count];
            Array.Fill(uniform, best);
            return uniform;
        }

        int[]? bestRates = null;
        var bestDistance = int.MaxValue;
        var bestIncrements = int.MaxValue;

        foreach (var baseRate in baseCandidates)
        {
            var baseTotal = baseRate * requestsPerRound;

            for (var mask = 0; mask < 1 << count; mask++)
            {
                var total = baseTotal;
                var increments = 0;

                for (var i = 0; i < count; i++)
                {
                    if ((mask & (1 << i)) == 0) continue;
                    total += scenarios[i].RequestsPerIteration;
                    increments++;
                }

                var distance = Math.Abs(total - target);
                if (distance > bestDistance || (distance == bestDistance && increments >= bestIncrements))
                    continue;

                bestDistance = distance;
                bestIncrements = increments;
                bestRates = new int[count];
                for (var i = 0; i < count; i++)
                    bestRates[i] = baseRate + ((mask & (1 << i)) != 0 ? 1 : 0);
            }
        }

        return bestRates!;
    }
}
