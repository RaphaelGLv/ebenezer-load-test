namespace EbenezerBackend.LoadTests.Reporting;

/// <summary>Um step que não foi executado por uma pré-condição não satisfeita.</summary>
public sealed record SkippedStep(
    string Scenario,   // nome do cenário (ex.: "write_heavy")
    string StepName,   // nome do step que teria sido executado (ex.: "04_update_category")
    string Reason);    // motivo legível (ex.: "categoryId is empty")
