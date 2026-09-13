# Requirements Document

## Introduction

Este recurso aprimora a geração de relatórios do projeto de testes de carga
(`EbenezerBackend.LoadTests`). Hoje, cada execução grava um relatório HTML do NBomber
e um CSV de uso de recursos (`resource-usage.csv`) em uma pasta exclusiva por execução
(`reports/{abordagem}/{nome_da_execucao}/`).

O objetivo é duplo:

1. Garantir que todo relatório gravado em arquivo CSV contenha uma linha de cabeçalho
   que identifique cada coluna.
2. Adicionar um novo relatório CSV que contabilize as mensagens de erro ocorridas
   durante a execução e as atribua ao endpoint (etapa do cenário) que as originou —
   por exemplo: `endpoint 1 - erro A`, `endpoint 1 - erro B`, `endpoint 1 - erro A`,
   `endpoint 2 - erro C`, com a respectiva contagem de ocorrências.

## Glossary

- **Gerador_De_Relatorios**: Componente do projeto `EbenezerBackend.LoadTests` responsável
  por orquestrar a execução do NBomber e gravar os artefatos de relatório (HTML e CSV)
  na pasta de execução.
- **Relatorio_CSV**: Qualquer artefato de relatório gravado em arquivo com extensão `.csv`
  pelo Gerador_De_Relatorios.
- **Relatorio_De_Erros**: Novo Relatorio_CSV que contabiliza mensagens de erro por endpoint.
- **Cabecalho_CSV**: Primeira linha de um Relatorio_CSV que nomeia cada coluna,
  separada por vírgulas.
- **Etapa**: Passo nomeado de um cenário NBomber (`Step.Run("01_register", ...)`),
  que mapeia para uma operação de endpoint da API sob teste.
- **Endpoint**: Operação da API exercitada por uma Etapa, identificada pelo nome da Etapa.
- **Cenario**: Cenário NBomber composto por uma ou mais Etapas (ex.: `write_heavy`).
- **Mensagem_De_Erro**: Texto descritivo associado a uma falha registrada por uma Etapa.
- **Pasta_De_Execucao**: Diretório exclusivo de cada execução
  (`reports/{abordagem}/{nome_da_execucao}/`) onde os relatórios são gravados.

## Requirements

### Requirement 1: Cabeçalho em relatórios CSV

**User Story:** Como engenheiro de testes de carga, quero que todo relatório CSV inclua
uma linha de cabeçalho, para que eu consiga interpretar cada coluna sem consultar o código.

#### Acceptance Criteria

1. WHEN o Gerador_De_Relatorios grava um Relatorio_CSV, THE Gerador_De_Relatorios SHALL escrever um Cabecalho_CSV como única primeira linha física do arquivo, antes de qualquer linha de dados.
2. THE Cabecalho_CSV SHALL conter exatamente um nome de coluna para cada coluna presente nas linhas de dados do Relatorio_CSV, na mesma ordem das colunas, sem nomes de coluna vazios e sem nomes de coluna duplicados.
3. THE Cabecalho_CSV SHALL utilizar o mesmo caractere separador de campos empregado nas linhas de dados do Relatorio_CSV.
4. WHEN um Relatorio_CSV não possui linhas de dados, THE Gerador_De_Relatorios SHALL gravar o arquivo contendo apenas o Cabecalho_CSV seguido de uma única quebra de linha.

### Requirement 2: Relatório CSV de contagem de erros por endpoint

**User Story:** Como engenheiro de testes de carga, quero um relatório CSV que contabilize
as mensagens de erro e as atribua ao endpoint que as originou, para que eu identifique
quais operações falham e com qual frequência.

#### Acceptance Criteria

1. WHEN uma execução de teste de carga é concluída, THE Gerador_De_Relatorios SHALL gravar um Relatorio_De_Erros na Pasta_De_Execucao em até 5 segundos após o término da execução.
2. THE Relatorio_De_Erros SHALL conter as colunas `endpoint`, `error_message` e `count`, nesta ordem, em uma única linha de Cabecalho_CSV na primeira linha do arquivo.
3. THE Gerador_De_Relatorios SHALL gravar uma linha de dados no Relatorio_De_Erros para cada combinação distinta de Endpoint e Mensagem_De_Erro observada na execução.
4. THE Gerador_De_Relatorios SHALL ordenar as linhas de dados do Relatorio_De_Erros de forma crescente primeiro pela coluna `endpoint` e, em caso de empate, de forma crescente pela coluna `error_message`, garantindo saída idêntica para execuções com os mesmos dados de erro.
5. THE Gerador_De_Relatorios SHALL definir o valor da coluna `count` como o número total de ocorrências da respectiva combinação de Endpoint e Mensagem_De_Erro durante a execução, sendo um inteiro maior ou igual a 1.
6. WHERE uma Mensagem_De_Erro ou um Endpoint contém o caractere separador de colunas, aspas duplas ou quebra de linha, THE Gerador_De_Relatorios SHALL gravar o valor de forma que a estrutura de colunas e de linhas do Relatorio_De_Erros seja preservada.
7. WHEN nenhuma falha ocorre durante a execução, THE Gerador_De_Relatorios SHALL gravar o Relatorio_De_Erros contendo apenas o Cabecalho_CSV e nenhuma linha de dados.
8. IF a gravação do Relatorio_De_Erros na Pasta_De_Execucao falha, THEN THE Gerador_De_Relatorios SHALL sinalizar uma indicação de erro informando que o relatório não foi gerado, sem interromper a disponibilização das demais estatísticas da execução.

### Requirement 3: Atribuição da mensagem de erro ao endpoint de origem

**User Story:** Como engenheiro de testes de carga, quero que cada mensagem de erro seja
associada à etapa/endpoint que a invocou, para que eu localize a origem exata de cada falha.

#### Acceptance Criteria

1. WHEN uma Etapa registra uma falha, THE Gerador_De_Relatorios SHALL atribuir a Mensagem_De_Erro ao Endpoint cujo identificador corresponde exatamente ao nome da Etapa que invocou a falha.
2. WHEN o relatório é gerado, THE Gerador_De_Relatorios SHALL contabilizar separadamente Mensagens_De_Erro idênticas originadas por Endpoints distintos, mantendo um contador independente para cada par (Endpoint, Mensagem_De_Erro).
3. WHEN o relatório é gerado, THE Gerador_De_Relatorios SHALL contabilizar separadamente Mensagens_De_Erro distintas originadas pelo mesmo Endpoint, mantendo um contador independente para cada par (Endpoint, Mensagem_De_Erro).
4. WHEN uma Etapa registra uma falha sem Mensagem_De_Erro descritiva mas com um código de status disponível, THE Gerador_De_Relatorios SHALL registrar a Mensagem_De_Erro como a representação textual do código de status retornado.
5. IF uma falha não possui Mensagem_De_Erro descritiva (texto ausente, vazio ou composto apenas por espaços em branco) nem código de status disponível, THEN THE Gerador_De_Relatorios SHALL registrar a Mensagem_De_Erro com o valor reservado `unknown`.
6. IF o nome da Etapa que registrou a falha não corresponde a nenhum Endpoint conhecido, THEN THE Gerador_De_Relatorios SHALL atribuir a Mensagem_De_Erro a um Endpoint identificado pelo valor reservado `unknown` e preservar o nome original da Etapa no registro da falha.

### Requirement 4: Formato CSV válido e à prova de delimitadores

**User Story:** Como engenheiro de testes de carga, quero que o Relatorio_De_Erros seja um
CSV válido mesmo quando as mensagens de erro contêm vírgulas ou aspas, para que ferramentas
de planilha leiam o arquivo corretamente.

#### Acceptance Criteria

1. IF um valor de campo contém vírgula, aspas duplas, CR, LF ou CRLF, THEN THE Gerador_De_Relatorios SHALL delimitar o valor inteiro com aspas duplas e escapar cada aspa dupla interna duplicando-a, conforme RFC 4180.
2. THE Gerador_De_Relatorios SHALL gravar em cada linha do Relatorio_De_Erros exatamente o mesmo número de campos que o número de colunas declarado no Cabecalho_CSV.
3. WHEN o Relatorio_De_Erros é lido por um leitor CSV em conformidade com RFC 4180, THE conteúdo lido SHALL corresponder, campo a campo, aos valores originais de Endpoint, Mensagem_De_Erro e contagem gravados, incluindo quaisquer vírgulas, aspas duplas e quebras de linha presentes nesses valores.
4. IF um valor de campo é vazio ou nulo, THEN THE Gerador_De_Relatorios SHALL gravar um campo vazio (nenhum caractere entre os separadores de campo) preservando a contagem de campos declarada no Cabecalho_CSV.
5. THE Gerador_De_Relatorios SHALL terminar cada registro do Relatorio_De_Erros, incluindo o Cabecalho_CSV, com a sequência CRLF.

### Requirement 5: Localização e nomeação do relatório de erros

**User Story:** Como engenheiro de testes de carga, quero o relatório de erros junto aos
demais artefatos da execução, para que cada execução mantenha seus relatórios agrupados.

#### Acceptance Criteria

1. THE Gerador_De_Relatorios SHALL gravar o Relatorio_De_Erros na mesma Pasta_De_Execucao (`reports/{approach.Label}/{runName}`) onde são gravados o relatório HTML e o `resource-usage.csv`.
2. THE Gerador_De_Relatorios SHALL nomear o arquivo do Relatorio_De_Erros exatamente como `errors-by-endpoint.csv`.
3. IF a Pasta_De_Execucao não existe no momento da gravação, THEN THE Gerador_De_Relatorios SHALL criar a Pasta_De_Execucao, incluindo todos os diretórios intermediários ausentes, antes de gravar o Relatorio_De_Erros.
4. IF já existe um arquivo `errors-by-endpoint.csv` na Pasta_De_Execucao no momento da gravação, THEN THE Gerador_De_Relatorios SHALL sobrescrever o arquivo existente com o conteúdo do Relatorio_De_Erros da execução atual.
5. IF a gravação do Relatorio_De_Erros falhar (por exemplo, erro de E/S ou permissão de escrita negada na Pasta_De_Execucao), THEN THE Gerador_De_Relatorios SHALL emitir uma indicação de erro identificando a falha de gravação do Relatorio_De_Erros e preservar os demais artefatos já gravados na Pasta_De_Execucao (relatório HTML e `resource-usage.csv`).

### Requirement 6: Robustez da geração de relatórios

**User Story:** Como engenheiro de testes de carga, quero que a geração do relatório de erros
não interrompa a execução nem a gravação dos demais relatórios, para que uma execução sempre
produza os artefatos disponíveis.

#### Acceptance Criteria

1. IF a gravação do Relatorio_De_Erros falha, THEN THE Gerador_De_Relatorios SHALL registrar no console uma mensagem de aviso indicando a falha da gravação, sem propagar exceção que interrompa a execução.
2. IF a gravação de qualquer relatório falha, THEN THE Gerador_De_Relatorios SHALL preservar em disco todos os demais relatórios já gravados, sem removê-los nem revertê-los.
3. WHEN a execução é concluída com falhas em uma ou mais Etapas, THE Gerador_De_Relatorios SHALL incluir no Relatorio_De_Erros cada Etapa com falha e o respectivo motivo da falha.
4. WHEN a fase de geração de relatórios é concluída, THE Gerador_De_Relatorios SHALL retornar o controle ao fluxo de execução, independentemente de ocorrerem falhas na gravação de um ou mais relatórios.
