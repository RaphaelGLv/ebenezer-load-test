# Design Document

## Overview

Este documento descreve o design para aprimorar a geração de relatórios do projeto
`EbenezerBackend.LoadTests`. O recurso tem dois objetivos derivados dos requisitos:

1. **Cabeçalho universal em CSVs** (Requirement 1): garantir que todo `Relatorio_CSV`
   gravado pelo `Gerador_De_Relatorios` comece com uma única linha de `Cabecalho_CSV`
   nomeando cada coluna, inclusive o `resource-usage.csv` existente.
2. **Relatório de erros por endpoint** (Requirements 2–6): adicionar um novo relatório
   `errors-by-endpoint.csv` que contabiliza cada par (`Endpoint`, `Mensagem_De_Erro`)
   observado na execução, com a respectiva contagem, gravado na mesma `Pasta_De_Execucao`,
   em conformidade com RFC 4180 (Requirement 4), ordenado de forma determinística
   (Requirement 2.4) e robusto a falhas de gravação (Requirements 5 e 6).

A estratégia central é introduzir uma pequena camada de componentes **puros e testáveis**
(escritor CSV em conformidade com RFC 4180 e agregador de erros) que operam sobre as
estatísticas (`NodeStats`) retornadas pelo `NBomberRunner.Run()`, separando a lógica de
formatação/agregação dos efeitos colaterais de E/S. Essa separação permite testar a maior
parte do comportamento exigido com testes baseados em propriedades (PBT), enquanto a
gravação em disco e a integração com o NBomber são cobertas por testes de exemplo e a
robustez por tratamento defensivo de exceções.

### Fontes de pesquisa

- **RFC 4180 (Common Format and MIME Type for CSV Files)**: define as regras de
  quoting/escaping adotadas em Requirement 4 — campos contendo `,`, `"`, CR ou LF devem
  ser delimitados por aspas duplas, e aspas internas devem ser duplicadas; cada registro
  termina com CRLF. Referência: <https://www.rfc-editor.org/rfc/rfc4180>.
- **API do NBomber 6.x (`NodeStats`)**: `NBomberRunner.Run()` retorna um `NodeStats` que
  expõe `ScenarioStats[]`; cada `ScenarioStats` expõe `StepStats[]`; cada `StepStats` tem
  `StepName` e `Fail.StatusCodes` (`StatusCodeStats[]` com `StatusCode` (string),
  `IsError` (bool), `Message` (string) e `Count` (int)). Esses dados são a fonte das
  falhas a serem contabilizadas (Requirements 2.3, 3.x). Referência:
  <https://nbomber.com/docs/getting-started/hello-world>.

> Observação sobre as estatísticas do NBomber: as estatísticas agregam falhas por
> `StatusCodeStats` (uma entrada por código/mensagem distinta, já com `Count`). O design
> usa esse `Count` como fonte das ocorrências, em vez de eventos individuais, o que é
> consistente com Requirement 2.5 ("número total de ocorrências da respectiva combinação").

## Architecture

O fluxo permanece: configurar cenários → executar NBomber → gravar artefatos na
`Pasta_De_Execucao`. A mudança é capturar o `NodeStats` retornado por `Run()` e, na fase de
finalização, produzir o `errors-by-endpoint.csv` a partir dele, reutilizando um escritor CSV
compartilhado que também padroniza o cabeçalho do `resource-usage.csv`.

```mermaid
flowchart TD
    A[Program.cs: configura cenarios] --> B[NBomberRunner.Run]
    B --> C[NodeStats]
    C --> D[ErrorReportBuilder.Build\nagrega Endpoint x Mensagem -> count]
    D --> E[ErrorReportWriter.Write\nusa CsvWriter RFC 4180]
    E --> F[(reports/{label}/{run}/errors-by-endpoint.csv)]
    B --> G[Relatorio HTML NBomber]
    H[ResourceSampler] --> I[CsvWriter\ncabecalho padronizado]
    I --> J[(reports/{label}/{run}/resource-usage.csv)]
    E -. falha de E/S .-> K[Console.WriteLine aviso\nnao propaga excecao]
```

### Camadas

- **Domínio puro (sem E/S)**: `CsvWriter` (serialização RFC 4180) e `ErrorReportBuilder`
  (agregação de erros a partir do `NodeStats`). São funções determinísticas, ideais para PBT.
- **Camada de E/S**: `ErrorReportWriter` (cria diretório, grava/sobrescreve o arquivo, captura
  exceções) e a adaptação do `ResourceSampler` para emitir cabeçalho via componente comum.
- **Orquestração**: `Program.cs` captura o `NodeStats` e chama a gravação do relatório de erros
  dentro de um bloco protegido, após o `Run()`.

## Components and Interfaces

### 1. `CsvWriter` (utilitário compartilhado RFC 4180)

Componente puro responsável por transformar linhas lógicas (sequências de campos string) em
texto CSV válido. Centraliza o cumprimento dos Requirements 1 e 4 e é reutilizado tanto pelo
relatório de erros quanto pela padronização de cabeçalho dos demais CSVs.

```csharp
namespace EbenezerBackend.LoadTests.Reporting;

public static class CsvWriter
{
    public const string FieldSeparator = ",";
    public const string RecordTerminator = "\r\n"; // CRLF (Req 4.5)

    /// Serializa um documento CSV completo: cabeçalho + linhas de dados.
    /// header e cada linha de data devem ter o MESMO número de campos.
    /// Lança ArgumentException se a contagem de campos divergir (Req 1.2 / 4.2).
    public static string Write(IReadOnlyList<string> header,
                               IReadOnlyList<IReadOnlyList<string?>> rows);

    /// Escapa um único campo conforme RFC 4180 (Req 4.1, 4.4).
    public static string EscapeField(string? value);

    /// Serializa um único registro (linha) já com terminador CRLF.
    public static string WriteRecord(IReadOnlyList<string?> fields);
}
```

Regras de `EscapeField` (Requirement 4.1, 4.4):
- `null` ou `""` → campo vazio (string vazia), preservando contagem de campos.
- Se o valor contém `,`, `"`, CR (`\r`) ou LF (`\n`) → envolver em aspas duplas e duplicar
  cada `"` interno.
- Caso contrário → valor inalterado.

Regras de `Write` (Requirements 1.1–1.4, 4.2, 4.5):
- Valida que `header` é não vazio, sem nomes vazios e sem duplicados (Req 1.2).
- Valida que cada linha de dados tem exatamente `header.Count` campos (Req 4.2); caso
  contrário, lança `ArgumentException`.
- Emite o cabeçalho como primeira linha física, terminado por CRLF (Req 1.1, 4.5).
- Quando `rows` está vazio, retorna apenas o cabeçalho seguido de um único CRLF (Req 1.4, 2.7).
- Cada linha de dados é terminada por CRLF (Req 4.5).

### 2. `ErrorReportBuilder` (agregação pura)

Transforma o `NodeStats` do NBomber em uma lista ordenada e determinística de linhas do
relatório de erros. Não realiza E/S.

```csharp
namespace EbenezerBackend.LoadTests.Reporting;

public sealed record ErrorRow(string Endpoint, string ErrorMessage, long Count);

public static class ErrorReportBuilder
{
    public const string UnknownEndpoint = "unknown";
    public const string UnknownMessage = "unknown";

    public static IReadOnlyList<string> Header => new[] { "endpoint", "error_message", "count" };

    /// Agrega as falhas de todas as Scenario/StepStats em linhas (endpoint, mensagem, count),
    /// somando contagens por par e ordenando por endpoint asc, depois error_message asc.
    public static IReadOnlyList<ErrorRow> Build(IEnumerable<StepFailure> failures,
                                                ISet<string>? knownEndpoints = null);

    /// Normaliza a mensagem de erro de uma falha (Req 3.4, 3.5).
    public static string ResolveMessage(string? message, string? statusCode);

    /// Normaliza o endpoint de uma falha (Req 3.1, 3.6).
    public static string ResolveEndpoint(string stepName, ISet<string>? knownEndpoints);
}
```

`StepFailure` é uma projeção mínima e neutra das estatísticas do NBomber, para desacoplar a
lógica de agregação da API concreta e facilitar o PBT:

```csharp
public sealed record StepFailure(
    string StepName,    // nome da Etapa (Step.Run("01_register", ...))
    string? Message,    // StatusCodeStats.Message
    string? StatusCode, // StatusCodeStats.StatusCode (texto)
    long Count);        // StatusCodeStats.Count
```

Regras de agregação:
- `ResolveMessage` (Req 3.4, 3.5): se `Message` tem texto não-branco, usa-o; senão, se
  `StatusCode` tem texto não-branco, usa a representação textual do status; senão, `unknown`.
- `ResolveEndpoint` (Req 3.1, 3.6): se `knownEndpoints` é fornecido e não contém `StepName`,
  retorna `unknown`; caso contrário usa `StepName` (correspondência exata). O nome original
  da Etapa é preservado na `Mensagem_De_Erro`/observação quando cair em `unknown` apenas se
  necessário — ver nota abaixo.
- Agrupa por `(endpoint, error_message)` somando `Count` (Req 3.2, 3.3, 2.5).
- Ordena por `endpoint` (ordinal asc) e desempata por `error_message` (ordinal asc),
  produzindo saída idêntica para os mesmos dados (Req 2.4).

> Nota sobre Req 3.6 (preservar nome original da Etapa): quando o `StepName` não corresponde
> a nenhum `Endpoint` conhecido, o endpoint do relatório é `unknown` e o nome original é
> preservado anexando-o à `error_message` no formato `"<mensagem> (step: <stepName>)"`.
> Como, na prática, todo `StepName` do NBomber É o identificador do endpoint, `knownEndpoints`
> é tipicamente `null` (todos os steps são considerados conhecidos) e esse caminho só é
> exercido em configurações futuras que forneçam uma lista de endpoints conhecidos.

### 3. `ErrorReportWriter` (camada de E/S)

Orquestra a construção e gravação do `errors-by-endpoint.csv`, encapsulando toda a robustez
exigida (Requirements 5 e 6).

```csharp
namespace EbenezerBackend.LoadTests.Reporting;

public static class ErrorReportWriter
{
    public const string FileName = "errors-by-endpoint.csv";

    /// Constrói e grava o relatório de erros na pasta informada.
    /// Nunca lança: em caso de falha, escreve aviso no console e retorna false (Req 5.5, 6.1, 6.4).
    public static bool TryWrite(string reportFolder,
                                IEnumerable<StepFailure> failures,
                                ISet<string>? knownEndpoints = null);
}
```

Comportamento:
- Garante a `Pasta_De_Execucao` via `Directory.CreateDirectory` (Req 5.3).
- Caminho do arquivo: `Path.Combine(reportFolder, FileName)` (Req 5.1, 5.2).
- Serializa via `CsvWriter.Write(ErrorReportBuilder.Header, rows)` e grava com
  `File.WriteAllText(path, content, new UTF8Encoding(false))`, sobrescrevendo arquivo
  existente (Req 5.4). `WriteAllText` é sobrescrita por natureza.
- Em qualquer exceção (E/S, permissão), captura, escreve `Console.WriteLine` de aviso
  identificando a falha de gravação do relatório de erros e retorna `false`, sem propagar
  (Req 5.5, 6.1, 6.4). Demais artefatos já gravados permanecem intactos (Req 6.2).

### 4. Extração de falhas a partir do `NodeStats` (adaptador)

Função fina que projeta `NodeStats` → `IEnumerable<StepFailure>`, isolando a dependência da
API concreta do NBomber (não é alvo de PBT; é coberta por teste de exemplo):

```csharp
public static IEnumerable<StepFailure> ExtractFailures(NodeStats stats)
{
    foreach (var scenario in stats.ScenarioStats)
        foreach (var step in scenario.StepStats)
            foreach (var sc in step.Fail.StatusCodes)
                if (sc.Count > 0)
                    yield return new StepFailure(step.StepName, sc.Message, sc.StatusCode, sc.Count);
}
```

### 5. Integração em `Program.cs`

Captura o retorno do `Run()` e grava o relatório de erros no bloco de finalização, após o
HTML, sem alterar o comportamento atual de coleta de recursos:

```csharp
NodeStats stats = NBomberRunner
    .RegisterScenarios(configured)
    .WithReportFolder(reportFolder)
    .WithReportFormats(ReportFormat.Html)
    .Run();

// Relatório de erros por endpoint (Req 2, 5, 6). Nunca interrompe o fluxo.
ErrorReportWriter.TryWrite(reportFolder, ErrorReportExtensions.ExtractFailures(stats));
```

A chamada ocorre dentro de `try { ... } finally { ... }` já existente de modo que a liberação
do `sampler` e do `http` permaneça garantida.

### 6. Padronização de cabeçalho do `ResourceSampler` (Requirement 1)

O `ResourceSampler` já grava o cabeçalho `timestamp,container,cpu_percent,memory_mb`. Para
satisfazer o Requirement 1 de forma consistente entre todos os CSVs, o cabeçalho passa a ser
emitido via `CsvWriter.WriteRecord(new[] { "timestamp", "container", "cpu_percent", "memory_mb" })`,
garantindo a mesma convenção de campos/escaping. As linhas de dados subsequentes também passam
a usar `CsvWriter.WriteRecord` para garantir contagem de campos consistente com o cabeçalho.

> Decisão de design: o `resource-usage.csv` hoje usa terminador LF. O Requirement 4.5 (CRLF)
> é específico do `Relatorio_De_Erros`. O Requirement 1 exige apenas a presença e a estrutura
> do cabeçalho (não o terminador). Para reduzir risco de regressão em ferramentas que já
> consomem o `resource-usage.csv`, mantém-se a estratégia de E/S do sampler, padronizando
> apenas a montagem da linha de cabeçalho/dados via `CsvWriter.WriteRecord`. Caso se deseje
> CRLF universal no futuro, basta o sampler adotar `CsvWriter.RecordTerminator` — ponto único
> de mudança.

## Data Models

### `StepFailure`
Projeção neutra de uma falha agregada de uma Etapa.

| Campo       | Tipo     | Origem (NBomber)              | Observação                              |
|-------------|----------|-------------------------------|-----------------------------------------|
| StepName    | string   | `StepStats.StepName`          | Identificador da Etapa/Endpoint (Req 3.1)|
| Message     | string?  | `StatusCodeStats.Message`     | Pode ser nula/vazia (Req 3.5)           |
| StatusCode  | string?  | `StatusCodeStats.StatusCode`  | Usada como fallback (Req 3.4)           |
| Count       | long     | `StatusCodeStats.Count`       | Ocorrências (Req 2.5) ≥ 1               |

### `ErrorRow`
Linha final do relatório, pronta para serialização.

| Campo        | Tipo   | Coluna CSV      | Regra                                   |
|--------------|--------|-----------------|-----------------------------------------|
| Endpoint     | string | `endpoint`      | `StepName` ou `unknown` (Req 3.1, 3.6)  |
| ErrorMessage | string | `error_message` | resolvida (Req 3.4, 3.5)                |
| Count        | long   | `count`         | soma por par, ≥ 1 (Req 2.5)             |

### Documento CSV de erros
- Cabeçalho fixo: `endpoint,error_message,count` (Req 2.2).
- 0..N linhas de dados (Req 2.3, 2.7).
- Cada registro termina em CRLF (Req 4.5).
- Campos escapados por RFC 4180 (Req 4.1, 4.4).

## Correctness Properties

*Uma propriedade é uma característica ou comportamento que deve ser verdadeiro em todas as
execuções válidas de um sistema — essencialmente uma afirmação formal sobre o que o sistema
deve fazer. Propriedades servem como ponte entre especificações legíveis por humanos e
garantias de correção verificáveis por máquina.*


As propriedades a seguir resultam da análise de prework e da consolidação de critérios
redundantes. Elas cobrem os componentes puros (`CsvWriter`, `ErrorReportBuilder`) e a
robustez do `ErrorReportWriter`. Critérios de localização/E/S específicos (5.1–5.4, 6.2) são
cobertos por testes de exemplo/integração descritos na Testing Strategy, não por propriedades.

### Property 1: Round-trip RFC 4180

*Para todo* cabeçalho válido e *toda* lista de linhas de dados com contagem de campos
consistente (campos arbitrários, incluindo vírgulas, aspas duplas, CR, LF, CRLF, strings
vazias e nulas), ler com um leitor em conformidade com RFC 4180 o documento produzido por
`CsvWriter.Write` recupera, campo a campo, exatamente os valores originais (tratando `null`
como campo vazio).

**Validates: Requirements 2.6, 4.1, 4.3, 4.4**

### Property 2: Estrutura do documento (contagem de campos e terminador CRLF)

*Para todo* cabeçalho válido e *toda* lista de linhas consistente, no documento produzido por
`CsvWriter.Write` cada registro lógico (cabeçalho e linhas de dados) possui exatamente
`header.Count` campos, todos os registros são terminados por CRLF e não há LF ou CR isolado
fora de campos entre aspas.

**Validates: Requirements 1.3, 4.2, 4.5**

### Property 3: Validação do cabeçalho

*Para todo* cabeçalho que contenha pelo menos um nome vazio ou nomes duplicados, ou para toda
linha de dados cuja contagem de campos difira da do cabeçalho, `CsvWriter.Write` rejeita a
entrada lançando `ArgumentException`; e *para todo* cabeçalho sem vazios nem duplicados com
linhas consistentes, a escrita é bem-sucedida.

**Validates: Requirements 1.2, 4.2**

### Property 4: Cabeçalho como primeira linha e caso sem dados

*Para todo* cabeçalho válido e *toda* lista de linhas, o primeiro registro lógico do documento
produzido é exatamente o cabeçalho; e quando a lista de linhas é vazia, o documento é
exatamente o cabeçalho serializado seguido de um único CRLF (nenhum CRLF adicional).

**Validates: Requirements 1.1, 1.4, 2.2, 2.7**

### Property 5: Escaping de campo conforme RFC 4180

*Para todo* valor de campo, `CsvWriter.EscapeField` produz: a string vazia quando o valor é
`null` ou vazio; um valor delimitado por aspas duplas com cada aspa interna duplicada quando o
valor contém `,`, `"`, CR ou LF; e o valor inalterado caso contrário.

**Validates: Requirements 4.1, 4.4**

### Property 6: Agrupamento por chave composta e cobertura completa

*Para toda* coleção de falhas (`StepFailure`), o conjunto de chaves `(endpoint, error_message)`
das linhas produzidas por `ErrorReportBuilder.Build` é exatamente o conjunto distinto de pares
`(endpoint, error_message)` resolvidos a partir das falhas de entrada — cada par distinto gera
exatamente uma linha, sem omissões nem duplicatas.

**Validates: Requirements 2.3, 3.2, 3.3, 6.3**

### Property 7: Ordenação determinística

*Para toda* coleção de falhas, as linhas produzidas por `ErrorReportBuilder.Build` estão
ordenadas de forma crescente por `endpoint` e, em empate, de forma crescente por
`error_message`; além disso, qualquer permutação da coleção de entrada produz exatamente a
mesma sequência de linhas de saída.

**Validates: Requirements 2.4**

### Property 8: Correção da contagem

*Para toda* coleção de falhas, o `count` de cada linha produzida é igual à soma dos `Count`
de todas as falhas de entrada que resolvem para aquele par `(endpoint, error_message)`, e todo
`count` é um inteiro maior ou igual a 1; a soma de todos os `count` de saída é igual à soma de
todos os `Count` de entrada.

**Validates: Requirements 2.5**

### Property 9: Resolução do endpoint

*Para todo* `StepName`: quando nenhum conjunto de endpoints conhecidos é fornecido, ou quando
o conjunto contém o `StepName`, o endpoint resolvido é exatamente o `StepName`; quando um
conjunto de endpoints conhecidos é fornecido e não contém o `StepName`, o endpoint resolvido é
`unknown` e o `StepName` original é preservado na `error_message` da linha.

**Validates: Requirements 3.1, 3.6**

### Property 10: Resolução da mensagem de erro

*Para toda* falha: quando `Message` contém texto não composto apenas por espaços em branco, a
mensagem resolvida é `Message`; senão, quando `StatusCode` contém texto não-branco, a mensagem
resolvida é a representação textual do `StatusCode`; senão, a mensagem resolvida é `unknown`.

**Validates: Requirements 3.4, 3.5**

### Property 11: Robustez da gravação (nunca lança)

*Para toda* combinação de pasta de destino (incluindo caminhos inválidos ou inacessíveis) e
coleção de falhas, `ErrorReportWriter.TryWrite` retorna um booleano sem propagar exceção,
devolvendo o controle ao chamador em todos os casos.

**Validates: Requirements 5.5, 6.1, 6.4**

## Error Handling

| Cenário | Tratamento | Requisitos |
|---------|-----------|------------|
| Pasta de execução inexistente | `Directory.CreateDirectory(reportFolder)` antes de gravar (cria intermediárias) | 5.3 |
| Arquivo `errors-by-endpoint.csv` já existe | `File.WriteAllText` sobrescreve o conteúdo | 5.4 |
| Falha de E/S / permissão negada na gravação | Captura da exceção em `TryWrite`; `Console.WriteLine` com aviso identificando a falha de gravação do relatório de erros; retorna `false`; não propaga | 5.5, 6.1 |
| Demais artefatos (HTML, resource-usage.csv) já gravados | Nunca removidos nem revertidos; gravação do relatório de erros é isolada e posterior | 6.2 |
| Cabeçalho inválido ou contagem de campos divergente (erro de programação) | `CsvWriter.Write` lança `ArgumentException`; capturada por `TryWrite` como qualquer outra falha, sem interromper o fluxo | 1.2, 4.2, 6.1 |
| `Message` ausente/vazia/em branco | Fallback para `StatusCode`; senão `unknown` | 3.4, 3.5 |
| `StepName` sem endpoint conhecido | Endpoint `unknown`, preserva nome original na mensagem | 3.6 |
| Nenhuma falha na execução | Relatório só com cabeçalho + CRLF | 2.7 |
| Falha em qualquer relatório | Fase de relatórios retorna o controle ao fluxo normalmente | 6.4 |

Princípio geral: a lógica pura (`CsvWriter`, `ErrorReportBuilder`) valida invariantes e pode
lançar em uso incorreto; a fronteira de E/S (`ErrorReportWriter.TryWrite`) é o único ponto que
converte qualquer exceção em aviso de console + retorno `false`, garantindo que a geração do
relatório de erros nunca interrompa a execução nem os demais artefatos.

## Testing Strategy

O projeto já referencia **FsCheck 3.x + FsCheck.Xunit** e **xunit**, que serão usados para as
propriedades e para os testes de exemplo, respectivamente. A abordagem é dupla e complementar.

### Testes baseados em propriedades (PBT)

- Biblioteca: **FsCheck.Xunit** (`[Property]`), sem implementar PBT do zero.
- Mínimo de **100 iterações** por propriedade (`[Property(MaxTest = 100)]` ou superior).
- Cada teste referencia a propriedade de design correspondente via comentário no formato:
  `// Feature: loadtest-csv-error-report, Property {número}: {texto da propriedade}`.
- Cada uma das Properties 1–11 é implementada por **um único** teste baseado em propriedade.
- Geradores customizados:
  - Strings "maliciosas" contendo `,`, `"`, `\r`, `\n`, `\r\n`, vazias e nulas (Properties 1, 5).
  - Cabeçalhos válidos (sem vazios/duplicados) e inválidos (com vazios/duplicados) (Property 3).
  - Documentos header+rows com contagem de campos consistente (Properties 1, 2, 4).
  - Coleções de `StepFailure` com endpoints/mensagens repetidos e `Count` ≥ 1 (Properties 6, 7, 8).
- Para a Property 1 (round-trip), o leitor de verificação deve ser um parser RFC 4180
  independente do `CsvWriter` (parser mínimo no projeto de teste) para evitar viés de
  implementação compartilhada.

### Testes de exemplo (xunit `[Fact]`/`[Theory]`)

Cobrem critérios de E/S, configuração e integração não adequados a PBT:

- **Cabeçalho fixo** do relatório de erros é `endpoint,error_message,count` (Req 2.2).
- **Localização e nome**: `TryWrite` grava `errors-by-endpoint.csv` na pasta informada (Req 5.1, 5.2).
- **Criação de diretório**: apontar para subpasta inexistente e verificar criação + arquivo (Req 5.3).
- **Sobrescrita**: pré-criar arquivo com conteúdo distinto, chamar `TryWrite`, verificar substituição (Req 5.4).
- **Robustez de E/S**: simular falha de gravação (ex.: caminho inválido/arquivo bloqueado) e verificar
  retorno `false`, mensagem de aviso e preservação de artefato pré-existente (Req 5.5, 6.2).
- **Desempenho**: gravação de relatório típico conclui bem abaixo de 5s (Req 2.1).
- **Adaptador NodeStats**: teste de exemplo (com `NodeStats` simulado/montado, se viável, ou
  via execução curta do NBomber) que `ExtractFailures` projeta corretamente
  `StepName/Message/StatusCode/Count` (Req 2.1, 3.1).

### Equilíbrio

Os testes de exemplo focam em pontos de integração e bordas de E/S; os testes de propriedade
cobrem o espaço de entrada amplo da formatação CSV e da agregação. Evita-se multiplicar
testes de exemplo para casos já garantidos pelas propriedades (escaping, ordenação, contagem).

## Review and Approval

O documento `requirements.md` existe (workflow requirements-first) e foi usado como fonte de
verdade; cada propriedade e decisão de design referencia os números de requisito
correspondentes. Caso sejam identificadas lacunas nos requisitos durante a implementação
(por exemplo, decisão sobre CRLF universal no `resource-usage.csv`), recomenda-se retornar à
fase de requisitos para esclarecimento antes de prosseguir.
