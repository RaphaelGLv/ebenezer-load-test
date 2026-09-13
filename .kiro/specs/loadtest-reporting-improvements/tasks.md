# Implementation Plan: loadtest-reporting-improvements

## Overview

Implementação em C# no projeto `EbenezerBackend.LoadTests`. Estende o módulo de relatórios
existente em `Reporting/` com duas melhorias:

1. Adiciona a coluna `scenario` ao `errors-by-endpoint.csv`.
2. Adiciona o arquivo `skipped-steps.csv` com os steps que não foram executados.

Cada task referencia os requisitos correspondentes para rastreabilidade. Tasks com `*` são
opcionais (testes de propriedade) e podem ser puladas para um MVP mais rápido.

## Tasks

- [x] 1. Adicionar `ScenarioName` ao `StepFailure` e atualizar `ExtractFailures`
  - Adicionar o campo `ScenarioName` (primeiro parâmetro) ao record `StepFailure`
  - Atualizar `ErrorReportExtensions.ExtractFailures` para incluir `scenario.ScenarioName`
    ao construir cada `StepFailure`
  - _Requirements: 7.1, 7.2_

- [x] 2. Atualizar `ErrorRow` e `ErrorReportBuilder` para incluir `scenario`
  - Adicionar o campo `Scenario` (primeiro parâmetro) ao record `ErrorRow`
  - Atualizar `ErrorReportBuilder.Header` para `["scenario", "endpoint", "error_message", "count"]`
  - Adicionar `ResolveScenario(string? scenarioName)`: retorna o valor se não-nulo/não-branco, senão `"unknown"`
  - Atualizar `Build` para agrupar por `(scenario, endpoint, message)` e ordenar por
    `scenario` (ordinal asc) → `endpoint` (ordinal asc) → `error_message` (ordinal asc)
  - _Requirements: 7.1, 7.3, 7.4, 7.5_

- [x] 3. Atualizar `ErrorReportWriter` para projetar a nova coluna `scenario`
  - Na projeção de `ErrorRow` para `IReadOnlyList<string?>`, incluir `r.Scenario` como
    primeiro campo
  - O CSV resultante terá 4 campos por linha: `scenario,endpoint,error_message,count`
  - _Requirements: 7.1_

- [x]* 4. Escrever testes para as mudanças no relatório de erros
  - **Property 12**: gerar `StepFailure` com cenários distintos para o mesmo
    `(endpoint, message)` e verificar que `Build` produz linhas separadas por cenário
    — **Validates: Requirement 7.3**
  - **Property 13**: verificar ordenação com três chaves (`scenario`, `endpoint`,
    `error_message`) para permutações da entrada — **Validates: Requirement 7.4**
  - **Fact**: cabeçalho do `errors-by-endpoint.csv` agora é
    `scenario,endpoint,error_message,count`
  - **Fact**: `ExtractFailures` propaga `ScenarioName` corretamente para `StepFailure`

- [x] 5. Criar modelos de dados para steps pulados
  - Criar `Reporting/SkippedStep.cs` com o record
    `SkippedStep(string Scenario, string StepName, string Reason)`
  - _Requirements: 8.2, 8.3_

- [x] 6. Implementar `SkipTracker` (acúmulo thread-safe)
  - Criar `Reporting/SkipTracker.cs`
  - `Record(string scenario, string stepName, string reason)`: adiciona ao estado interno
    com `lock` para segurança em execuções paralelas do NBomber
  - `Snapshot()`: retorna `IReadOnlyList<SkippedStep>` imutável de todos os registros
  - _Requirements: 8.3, 9.1_

- [x]* 7. Escrever teste de propriedade de thread-safety do `SkipTracker`
  - **Property 14**: lançar N threads concorrentes chamando `Record` e verificar que
    `Snapshot()` contém exatamente todos os registros inseridos, sem perdas nem duplicatas
    — **Validates: Requirement 8.3**

- [x] 8. Implementar `SkippedStepsBuilder` (ordenação pura)
  - Criar `Reporting/SkippedStepsBuilder.cs`
  - `Header`: `["scenario", "step_name", "reason"]`
  - `Sort(IEnumerable<SkippedStep>)`: ordena por `scenario` (ordinal asc) → `step_name`
    (ordinal asc) e retorna lista imutável
  - _Requirements: 8.2, 8.6_

- [x]* 9. Escrever teste de propriedade de ordenação do `SkippedStepsBuilder`
  - **Property 15**: qualquer permutação da entrada produz a mesma sequência de saída
    — **Validates: Requirement 8.6**

- [x] 10. Implementar `SkippedStepsWriter` (camada de E/O defensiva)
  - Criar `Reporting/SkippedStepsWriter.cs` com `FileName = "skipped-steps.csv"`
  - `TryWrite(string reportFolder, IEnumerable<SkippedStep> records)`:
    - `Directory.CreateDirectory(reportFolder)`
    - `SkippedStepsBuilder.Sort(records)` → projetar para `IReadOnlyList<string?>`
    - `CsvWriter.Write(SkippedStepsBuilder.Header, rows)`
    - `File.WriteAllText(path, content, new UTF8Encoding(false))`
    - Capturar qualquer exceção, emitir console warning, retornar `false`
  - _Requirements: 8.1, 8.7, 8.8_

- [x]* 11. Escrever testes para o `SkippedStepsWriter`
  - **Property 16**: `TryWrite` nunca lança para qualquer pasta/coleção
    — **Validates: Requirement 8.7**
  - **Fact**: cabeçalho do `skipped-steps.csv` é `scenario,step_name,reason`
  - **Fact**: cria subpasta inexistente antes de gravar
  - **Fact**: sobrescreve arquivo pré-existente
  - **Fact**: coleção vazia produz CSV com apenas o cabeçalho — **Validates: Requirement 8.5**

- [x] 12. Checkpoint — garantir que os testes do módulo de relatórios passam
  - Rodar todos os testes existentes e os novos; confirmar zero regressões

- [x] 13. Instrumentar os cinco cenários em `Program.cs` com `SkipTracker`
  - Criar `var skipTracker = new SkipTracker();` antes de `NBomberRunner`
  - Para cada cenário (`write_heavy`, `read_heavy`, `graph_heavy`, `document_heavy`,
    `mixed_graph_documents`), instrumentar cada bloco condicional (`if`/`return`) com
    chamadas a `skipTracker.Record(...)` conforme o mapeamento do design:
    - Retornos antecipados por `s0x.IsError`: registrar todos os steps restantes do cenário
    - Blocos `if (!string.IsNullOrEmpty(variavel))`: registrar os steps internos com motivo
      `"<variavel> is empty"`
    - Blocos `if (!s0x.IsError && ...)`: registrar os steps internos com motivo
      `"s0x.IsError = true – <descrição>"`
  - _Requirements: 9.1, 9.2, 9.3, 9.4_

- [x] 14. Integrar `SkippedStepsWriter` no fluxo de execução de `Program.cs`
  - Após `ErrorReportWriter.TryWrite(...)`, adicionar:
    `SkippedStepsWriter.TryWrite(reportFolder, skipTracker.Snapshot());`
  - _Requirements: 8.1, 8.7_

- [x] 15. Checkpoint final — garantir que todos os testes passam e o projeto compila
  - `dotnet build` sem erros
  - Todos os testes passam

## Task Dependency Graph

```json
{
  "waves": [
    { "id": 0, "tasks": ["1", "5"] },
    { "id": 1, "tasks": ["2", "6"] },
    { "id": 2, "tasks": ["3", "7", "8"] },
    { "id": 3, "tasks": ["4", "9", "10"] },
    { "id": 4, "tasks": ["11"] },
    { "id": 5, "tasks": ["12"] },
    { "id": 6, "tasks": ["13"] },
    { "id": 7, "tasks": ["14"] },
    { "id": 8, "tasks": ["15"] }
  ]
}
```
