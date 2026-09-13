# Requirements Document

## Introduction

Este recurso amplia os relatórios CSV gerados pelo projeto `EbenezerBackend.LoadTests`
em duas frentes:

1. **Coluna `scenario` no relatório de erros** (`errors-by-endpoint.csv`): hoje o arquivo
   possui as colunas `endpoint,error_message,count`. A ausência do nome do cenário força
   o engenheiro a cruzar o nome do step com o código-fonte para saber de qual cenário veio
   o erro. Adicionar a coluna `scenario` resolve isso.

2. **Relatório de steps pulados** (`skipped-steps.csv`): quando uma variável de controle de
   fluxo está vazia ou um step anterior falhou, o programa não executa os steps seguintes
   (`if (!string.IsNullOrEmpty(prayerId)) { ... }`). Atualmente, esses steps simplesmente
   desaparecem das estatísticas — não há falha, não há sucesso. O engenheiro não consegue
   distinguir "não foi testado" de "não foi executado". Este relatório registra cada step que
   não chegou a rodar e por quê.

## Glossary

Os termos do spec anterior (`loadtest-csv-error-report`) são herdados. Os novos abaixo:

- **Coluna_Scenario**: coluna `scenario` adicionada ao `errors-by-endpoint.csv`, contendo o
  nome do cenário NBomber (`write_heavy`, `read_heavy`, etc.) de onde a falha originou.
- **Step_Pulado**: step de um cenário que não foi executado porque uma pré-condição de controle
  de fluxo (`if`) não foi satisfeita, identificado pelo nome do cenário, nome do step e
  motivo (variável ou condição que impediu a execução).
- **Relatorio_De_Steps_Pulados**: novo arquivo CSV `skipped-steps.csv`, gravado na mesma
  `Pasta_De_Execucao` do relatório de erros, que lista todos os steps pulados da execução.
- **Motivo_De_Pulo**: texto descritivo do porquê um step foi pulado (ex.:
  `"prayerId is empty"`, `"s03.IsError = true"`).
- **Tracker_De_Pulos**: componente responsável por acumular registros de Steps_Pulados durante
  a execução dos cenários e expô-los para gravação ao final.

## Requirements

### Requirement 7: Coluna `scenario` no relatório de erros

**User Story:** Como engenheiro de testes de carga, quero saber de qual cenário veio cada
erro no `errors-by-endpoint.csv`, para que eu não precise cruzar nomes de steps com o
código-fonte para identificar a origem.

#### Acceptance Criteria

1. THE `errors-by-endpoint.csv` SHALL conter as colunas `scenario`, `endpoint`,
   `error_message` e `count`, nesta ordem, como primeira linha do arquivo.
2. WHEN o relatório é gerado, THE Gerador_De_Relatorios SHALL preencher a coluna `scenario`
   com o nome exato do cenário NBomber (`ScenarioStats.ScenarioName`) de onde a falha
   originou.
3. THE Gerador_De_Relatorios SHALL tratar `scenario` como parte da chave de agrupamento: falhas
   do mesmo step e com a mesma mensagem provenientes de cenários distintos SHALL produzir
   linhas separadas no relatório.
4. THE Gerador_De_Relatorios SHALL incluir `scenario` na ordenação primária do relatório
   (crescente, ordinal), seguido de `endpoint` (crescente, ordinal) e `error_message`
   (crescente, ordinal) como critérios de desempate subsequentes.
5. IF a coluna `scenario` estiver ausente ou em branco nas estatísticas do NBomber, THEN THE
   Gerador_De_Relatorios SHALL registrar o valor `unknown` na coluna `scenario` para as
   linhas afetadas.

### Requirement 8: Relatório de steps pulados

**User Story:** Como engenheiro de testes de carga, quero um arquivo `skipped-steps.csv` que
liste cada step que não foi executado e o motivo, para que eu saiba o que foi efetivamente
testado e o que não entrou no fluxo.

#### Acceptance Criteria

1. WHEN uma execução de teste de carga é concluída, THE Gerador_De_Relatorios SHALL gravar
   um `skipped-steps.csv` na `Pasta_De_Execucao` em até 5 segundos após o término da
   execução.
2. THE `skipped-steps.csv` SHALL conter as colunas `scenario`, `step_name` e `reason`,
   nesta ordem, como primeira linha do arquivo.
3. WHEN um bloco de steps não é executado porque uma pré-condição de controle de fluxo não
   foi satisfeita, THE Gerador_De_Relatorios SHALL registrar uma linha em `skipped-steps.csv`
   para cada step não executado, com o nome do cenário, o nome do step e o motivo legível
   do pulo.
4. THE Motivo_De_Pulo SHALL descrever a condição que não foi satisfeita de forma legível
   (ex.: `"prayerId is empty"`, `"categoryId is empty"`, `"s03.IsError = true – profile B
   not found"`, `"s04.IsError = true – friend request failed"`).
5. WHEN nenhum step é pulado durante a execução, THE Gerador_De_Relatorios SHALL gravar o
   `skipped-steps.csv` contendo apenas a linha de cabeçalho e nenhuma linha de dados.
6. THE `skipped-steps.csv` SHALL ser ordenado por `scenario` (crescente, ordinal), depois
   por `step_name` (crescente, ordinal) para saída determinística.
7. IF a gravação do `skipped-steps.csv` falhar, THE Gerador_De_Relatorios SHALL emitir
   aviso no console sem interromper a execução nem remover artefatos já gravados.
8. THE `skipped-steps.csv` SHALL ser um CSV válido em conformidade com RFC 4180,
   reutilizando o `CsvWriter` existente para escaping e estrutura de campos.

### Requirement 9: Cobertura de steps condicionais em todos os cenários

**User Story:** Como engenheiro de testes de carga, quero que todos os steps condicionais de
todos os cinco cenários sejam rastreados no `skipped-steps.csv`, para que nenhum bloco de
lógica condicional fique invisível nos relatórios.

#### Acceptance Criteria

1. THE rastreamento de steps pulados SHALL cobrir todos os blocos condicionais (`if`) nos
   cinco cenários do `Program.cs`: `write_heavy`, `read_heavy`, `graph_heavy`,
   `document_heavy` e `mixed_graph_documents`.
2. WHEN o step de registro falha (retorno `s01.IsError = true`) e o cenário é abortado via
   `return Response.Fail()`, THE Gerador_De_Relatorios SHALL registrar todos os steps
   subsequentes que foram pulados como consequência, com o motivo `"s01.IsError = true –
   registration failed"` ou equivalente.
3. WHEN uma variável de ID fica vazia (ex.: `categoryId`, `prayerId`, `userIdB`, `reqId`)
   porque o step que a produzia falhou ou retornou dados incompletos, THE Gerador_De_
   Relatorios SHALL registrar cada step que dependia dessa variável com o motivo
   correspondente (ex.: `"prayerId is empty"`, `"userIdB is empty"`).
4. WHEN um step de resultado intermediário falha (ex.: `s04.IsError = true`) e bloqueia
   steps subsequentes, THE Gerador_De_Relatorios SHALL registrar esses steps com o motivo
   indicando o step que falhou (ex.: `"s04.IsError = true – friend request failed"`).
