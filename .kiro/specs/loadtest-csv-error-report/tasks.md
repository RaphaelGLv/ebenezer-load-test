# Implementation Plan: loadtest-csv-error-report

## Overview

Implementação em C# no projeto `EbenezerBackend.LoadTests`. A estratégia separa lógica pura
(serialização CSV RFC 4180 e agregação de erros) dos efeitos de E/S, permitindo cobrir a maior
parte do comportamento com testes baseados em propriedades (FsCheck.Xunit, já referenciado) e a
gravação/integração com testes de exemplo (xunit). Cada passo evolui de componentes puros para a
camada de E/S e termina na integração em `Program.cs` e na padronização do cabeçalho do
`ResourceSampler`, sem deixar código órfão.

Os componentes vivem em um novo namespace `EbenezerBackend.LoadTests.Reporting`
(pasta `Reporting/`); os testes vivem em `Tests/`. As bibliotecas FsCheck 3.x, FsCheck.Xunit e
xunit já estão no `.csproj`.

## Tasks

- [x] 1. Criar estrutura do módulo de relatórios e modelos de dados compartilhados
  - Criar a pasta `Reporting/` e o namespace `EbenezerBackend.LoadTests.Reporting`
  - Definir o record `StepFailure(string StepName, string? Message, string? StatusCode, long Count)`
  - Definir o record `ErrorRow(string Endpoint, string ErrorMessage, long Count)`
  - _Requirements: 2.5, 3.1_

- [x] 2. Implementar o `CsvWriter` (serialização RFC 4180)
  - [x] 2.1 Implementar `CsvWriter.EscapeField` e as constantes `FieldSeparator`/`RecordTerminator`
    - Criar `Reporting/CsvWriter.cs` com classe estática
    - `EscapeField`: `null`/vazio → string vazia; valores com `,`, `"`, CR ou LF → envolver em aspas duplas e duplicar aspas internas; caso contrário, valor inalterado
    - _Requirements: 4.1, 4.4_

  - [x]* 2.2 Escrever teste de propriedade para o escaping de campo
    - **Property 5: Escaping de campo conforme RFC 4180**
    - **Validates: Requirements 4.1, 4.4**
    - Usar gerador de strings "maliciosas" (`,`, `"`, `\r`, `\n`, `\r\n`, vazias, nulas); `[Property(MaxTest = 100)]`

  - [x] 2.3 Implementar `CsvWriter.WriteRecord` e `CsvWriter.Write` com validação de cabeçalho
    - `WriteRecord`: serializa um registro de campos terminado por CRLF
    - `Write`: valida cabeçalho não vazio, sem nomes vazios e sem duplicados; valida que cada linha tem exatamente `header.Count` campos (lança `ArgumentException` caso contrário); emite cabeçalho como primeira linha física; quando `rows` vazio, retorna apenas o cabeçalho + um único CRLF; cada registro termina em CRLF
    - _Requirements: 1.1, 1.2, 1.3, 1.4, 2.2, 2.7, 4.2, 4.5_

  - [x]* 2.4 Escrever teste de propriedade de round-trip RFC 4180
    - **Property 1: Round-trip RFC 4180**
    - **Validates: Requirements 2.6, 4.1, 4.3, 4.4**
    - Implementar um parser RFC 4180 mínimo e independente em `Tests/` (sem reutilizar o `CsvWriter`) para evitar viés; ler o documento produzido e comparar campo a campo (tratando `null` como vazio)

  - [x]* 2.5 Escrever teste de propriedade da estrutura do documento
    - **Property 2: Estrutura do documento (contagem de campos e terminador CRLF)**
    - **Validates: Requirements 1.3, 4.2, 4.5**

  - [x]* 2.6 Escrever teste de propriedade de validação de cabeçalho
    - **Property 3: Validação do cabeçalho**
    - **Validates: Requirements 1.2, 4.2**
    - Gerar cabeçalhos válidos e inválidos (vazios/duplicados) e linhas com contagem divergente

  - [x]* 2.7 Escrever teste de propriedade de cabeçalho-primeiro e caso sem dados
    - **Property 4: Cabeçalho como primeira linha e caso sem dados**
    - **Validates: Requirements 1.1, 1.4, 2.2, 2.7**

- [x] 3. Checkpoint - Garantir que os testes do `CsvWriter` passam
  - Ensure all tests pass, ask the user if questions arise.

- [x] 4. Implementar o `ErrorReportBuilder` (agregação pura)
  - [x] 4.1 Implementar `ResolveMessage`, `ResolveEndpoint`, `Header` e constantes `Unknown*`
    - Criar `Reporting/ErrorReportBuilder.cs`
    - `ResolveMessage`: `Message` não-branco → `Message`; senão `StatusCode` não-branco → texto do status; senão → `unknown`
    - `ResolveEndpoint`: `knownEndpoints` fornecido e não contém `StepName` → `unknown` (preservar o `StepName` original); caso contrário → `StepName`
    - `Header` fixo: `["endpoint", "error_message", "count"]`
    - _Requirements: 2.2, 3.1, 3.4, 3.5, 3.6_

  - [x]* 4.2 Escrever teste de propriedade de resolução de endpoint
    - **Property 9: Resolução do endpoint**
    - **Validates: Requirements 3.1, 3.6**

  - [x]* 4.3 Escrever teste de propriedade de resolução de mensagem
    - **Property 10: Resolução da mensagem de erro**
    - **Validates: Requirements 3.4, 3.5**

  - [x] 4.4 Implementar `ErrorReportBuilder.Build` (agrupamento, soma e ordenação determinística)
    - Resolver `(endpoint, error_message)` por falha, agrupar somando `Count`
    - Quando `endpoint` cair em `unknown` por `StepName` desconhecido, anexar o nome original na mensagem no formato `"<mensagem> (step: <stepName>)"`
    - Ordenar por `endpoint` (ordinal asc) e desempatar por `error_message` (ordinal asc)
    - _Requirements: 2.3, 2.4, 2.5, 3.2, 3.3, 3.6, 6.3_

  - [x]* 4.5 Escrever teste de propriedade de agrupamento e cobertura completa
    - **Property 6: Agrupamento por chave composta e cobertura completa**
    - **Validates: Requirements 2.3, 3.2, 3.3, 6.3**

  - [x]* 4.6 Escrever teste de propriedade de ordenação determinística
    - **Property 7: Ordenação determinística**
    - **Validates: Requirements 2.4**
    - Verificar que qualquer permutação da entrada produz a mesma sequência de saída

  - [x]* 4.7 Escrever teste de propriedade de correção da contagem
    - **Property 8: Correção da contagem**
    - **Validates: Requirements 2.5**

- [x] 5. Implementar o `ErrorReportWriter` (camada de E/S robusta)
  - [x] 5.1 Implementar `ErrorReportWriter.TryWrite`
    - Criar `Reporting/ErrorReportWriter.cs` com `FileName = "errors-by-endpoint.csv"`
    - `Directory.CreateDirectory(reportFolder)`; gravar via `CsvWriter.Write(ErrorReportBuilder.Header, ...)` usando `File.WriteAllText` (sobrescreve) com UTF-8 sem BOM
    - Capturar qualquer exceção, escrever aviso no `Console`, retornar `false` sem propagar; nunca remover artefatos já gravados
    - _Requirements: 2.1, 5.1, 5.2, 5.3, 5.4, 5.5, 6.1, 6.2, 6.4_

  - [x]* 5.2 Escrever teste de propriedade de robustez da gravação
    - **Property 11: Robustez da gravação (nunca lança)**
    - **Validates: Requirements 5.5, 6.1, 6.4**
    - Gerar pastas de destino (incluindo caminhos inválidos/inacessíveis) e coleções de falhas

  - [x]* 5.3 Escrever testes de exemplo de E/S do `ErrorReportWriter`
    - Cabeçalho fixo `endpoint,error_message,count` (Req 2.2)
    - Grava `errors-by-endpoint.csv` na pasta informada (Req 5.1, 5.2)
    - Cria subpasta inexistente antes de gravar (Req 5.3)
    - Sobrescreve arquivo pré-existente (Req 5.4)
    - Falha de E/S retorna `false`, emite aviso e preserva artefato pré-existente (Req 5.5, 6.2)
    - Gravação típica conclui bem abaixo de 5s (Req 2.1)
    - _Requirements: 2.1, 2.2, 5.1, 5.2, 5.3, 5.4, 5.5, 6.2_

- [x] 6. Implementar o adaptador `NodeStats` → `StepFailure`
  - [x] 6.1 Implementar `ExtractFailures(NodeStats)`
    - Criar `Reporting/ErrorReportExtensions.cs`
    - Iterar `ScenarioStats[].StepStats[].Fail.StatusCodes[]`, emitindo `StepFailure(StepName, Message, StatusCode, Count)` quando `Count > 0`
    - _Requirements: 2.1, 3.1_

  - [x]* 6.2 Escrever teste de exemplo para `ExtractFailures`
    - Verificar projeção correta de `StepName/Message/StatusCode/Count` a partir de um `NodeStats` montado
    - _Requirements: 2.1, 3.1_

- [x] 7. Checkpoint - Garantir que os testes de agregação e gravação passam
  - Ensure all tests pass, ask the user if questions arise.

- [x] 8. Integrar no fluxo de execução e padronizar o cabeçalho do `ResourceSampler`
  - [x] 8.1 Integrar a gravação do relatório de erros em `Program.cs`
    - Capturar o `NodeStats` retornado por `NBomberRunner...Run()`
    - Chamar `ErrorReportWriter.TryWrite(reportFolder, ErrorReportExtensions.ExtractFailures(stats))` no bloco protegido após o `Run()`, preservando a liberação de `sampler`/`http`
    - _Requirements: 2.1, 5.1, 6.4_

  - [x] 8.2 Padronizar o cabeçalho e as linhas do `ResourceSampler` via `CsvWriter.WriteRecord`
    - Emitir o cabeçalho `timestamp,container,cpu_percent,memory_mb` e as linhas de dados via `CsvWriter.WriteRecord` para garantir contagem de campos consistente
    - Se possível, o cabeçalho cpu_percent deve trazer a porcentagem máxima no cabeçalho
    - _Requirements: 1.1, 1.2, 1.3_

  - [x]* 8.3 Escrever teste de exemplo para o cabeçalho do `resource-usage.csv`
    - Verificar que a primeira linha física é o cabeçalho e que cada linha de dados tem a mesma contagem de campos
    - _Requirements: 1.1, 1.2_

- [x] 9. Checkpoint final - Garantir que todos os testes passam
  - Ensure all tests pass, ask the user if questions arise.

## Notes

- Tarefas marcadas com `*` são opcionais (testes) e podem ser puladas para um MVP mais rápido.
- Cada tarefa referencia requisitos específicos para rastreabilidade.
- Os checkpoints garantem validação incremental.
- Os testes de propriedade (Properties 1–11) validam as propriedades universais de correção;
  cada propriedade é coberta por exatamente um teste baseado em propriedade.
- Os testes de exemplo cobrem E/S, localização/nome e integração não adequados a PBT.

## Task Dependency Graph

```json
{
  "waves": [
    { "id": 0, "tasks": ["1.1"] },
    { "id": 1, "tasks": ["2.1"] },
    { "id": 2, "tasks": ["2.3", "4.1"] },
    { "id": 3, "tasks": ["2.2", "2.4", "2.5", "2.6", "2.7", "4.4", "6.1"] },
    { "id": 4, "tasks": ["4.2", "4.3", "4.5", "4.6", "4.7", "5.1", "6.2"] },
    { "id": 5, "tasks": ["5.2", "5.3", "8.1", "8.2"] },
    { "id": 6, "tasks": ["8.3"] }
  ]
}
```
