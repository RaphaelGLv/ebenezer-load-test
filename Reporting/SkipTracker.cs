namespace EbenezerBackend.LoadTests.Reporting;

/// <summary>
/// Acumula registros de steps pulados durante a execução dos cenários.
/// Thread-safe via lock simples (múltiplos cenários rodam em paralelo no NBomber).
/// </summary>
public sealed class SkipTracker
{
    private readonly List<SkippedStep> _records = new();
    private readonly object _lock = new();

    /// <summary>Registra um step pulado. Pode ser chamado de qualquer thread.</summary>
    public void Record(string scenario, string stepName, string reason)
    {
        lock (_lock)
        {
            _records.Add(new SkippedStep(scenario, stepName, reason));
        }
    }

    /// <summary>Retorna snapshot imutável de todos os registros acumulados.</summary>
    public IReadOnlyList<SkippedStep> Snapshot()
    {
        lock (_lock)
        {
            return _records.ToList().AsReadOnly();
        }
    }
}
