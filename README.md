# EbenezerBackend.LoadTests

Suíte de carga para comparação entre as duas abordagens de persistência do TCC:

- **multimodel** — ArangoDB (banco multimodelo único)
- **heterogeneous** — MongoDB (documentos) + Neo4j (grafo social)

A suíte mede latência, throughput e consumo de recursos (CPU/RAM dos containers de banco) sob cinco categorias de carga, produzindo um relatório HTML por execução e um CSV de uso de recursos. A mesma suíte roda sem modificação em ambas as branches; só a abordagem muda.

---

## Pré-requisitos

- .NET 10 SDK
- Docker em execução (a API e os bancos devem estar no ar via `docker-compose`)
- A API deve estar acessível na URL configurada em `benchmark.config.jsonc` (padrão `http://localhost:5000`)

---

## Como executar

A abordagem de persistência é informada diretamente na linha de comando — não no arquivo de configuração. Isso evita esquecer de editar o config antes de cada rodada e torna cada execução autoexplicativa.

```bash
# Branch ArangoDB
dotnet run -- multimodel

# Branch MongoDB + Neo4j
dotnet run -- heterogeneous
```

O arquivo de configuração é sempre `benchmark.config.jsonc`, localizado no diretório do projeto. Não é necessário especificá-lo — o programa o encontra automaticamente, independentemente do diretório a partir do qual você chama `dotnet run`.

Se omitir a abordagem, o programa imprime uma mensagem de uso e encerra com código 1.

### Aliases aceitos

| Que você digita | Interpretado como |
|---|---|
| `multimodel`, `multimodelo`, `arangodb` | multimodel (ArangoDB) |
| `heterogeneous`, `heterogeneo`, `mongodb-neo4j`, `mongo-neo4j` | heterogeneous (MongoDB + Neo4j) |

---

## Arquivo de saída

Cada execução cria uma pasta única dentro de `reports/`, nunca sobrescrevendo rodadas anteriores:

```
reports/
  multimodel/
    medium_all_2026-06-23_14-00-00/
      nbomber_report.html      ← relatório principal
      resource-usage.csv       ← CPU/RAM dos containers
      throughput.csv           ← req HTTP/s teóricos vs. reais, por cenário
      errors-by-endpoint.csv   ← falhas agregadas por endpoint
      skipped-steps.csv        ← steps não executados por falha anterior
  heterogeneous/
    medium_all_2026-06-23_15-00-00/
      nbomber_report.html
      resource-usage.csv
      throughput.csv
      errors-by-endpoint.csv
      skipped-steps.csv
```

O nível de carga e o cenário também entram no nome da pasta, então `high_write_...` e `medium_all_...` nunca colidem.

**Por que a abordagem fica na pasta e não no nome do arquivo?**
Para que comparar as duas branches seja simples: você abre `reports/multimodel/` e `reports/heterogeneous/` lado a lado, sem precisar interpretar nomes de arquivo.

---

## Configuração (`benchmark.config.jsonc`)

O arquivo aceita comentários (`//`) e vírgulas finais. Todos os parâmetros têm um padrão razoável.

```jsonc
{
  "baseUrl": "http://localhost:5000", // URL da API sob teste

  "loadLevel": "medium",  // low | medium | high | higher (ver tabela abaixo)

  "warmupSeconds": 5,     // segundos descartados antes da medição; 0 desabilita

  "scenario": "all",      // all | write | read | graph | document | mixed

  "resourceMonitoring": {
    "enabled": true,
    "sampleIntervalSeconds": 2
  }
}
```

### Por que a abordagem não está aqui?

Porque ela muda a cada rodada de comparação (você alterna entre as branches) e é fácil esquecer de editar um arquivo antes de rodar. Colocar na linha de comando força consciência: você vê o que está medindo antes de apertar Enter.

Os containers Docker monitorados são derivados automaticamente da abordagem:
- `multimodel` → monitora `arangodb`
- `heterogeneous` → monitora `ebenezer-mongodb` e `ebenezer-neo4j`

Assim o config nunca precisa mudar entre branches.

### Níveis de carga predefinidos

Cada nível é um **alvo de requests HTTP por segundo**, somando todos os cenários ativos:

| Nível | Alvo (req HTTP/s) | Duração | Teórico resolvido com `all` |
|---|---|---|---|
| `low` | 100 | 180 s | 100 req/s |
| `medium` | 200 | 300 s | 201 req/s |
| `high` | 350 | 480 s | 350 req/s |
| `higher` | 500 | 600 s | 499 req/s |

**Por que o alvo é em requests/s e não na taxa do `Simulation.Inject`?**
Porque o `Inject` recebe **iterações por segundo de um cenário**, e cada iteração dispara vários requests HTTP sequenciais (`write` 19, `read` 11, `graph` 14, `document` 8, `mixed` 15 — 67 por rodada com `all`). Configurar o Inject diretamente esconde duas multiplicações: o número de cenários em paralelo e o número de steps por iteração. Com `"scenario": "all"` e taxa 20, por exemplo, o que chega na API são ~1.340 req/s, não 20.

A conversão é feita em tempo de execução pelo `ThroughputPlanner`, sobre os cenários realmente selecionados. Assim o alvo em req/s vale igual para `all` e para um cenário isolado; muda apenas a taxa de injeção resolvida. Todos os cenários recebem a mesma taxa base, com no máximo +1 iteração/s para fechar o resto da divisão, de modo que o mix de carga é preservado.

**Por que predefinidos em vez de campos livres?**
Porque para comparar duas abordagens os números precisam ser idênticos. Campos livres abrem espaço para diferenças acidentais entre rodadas. Os quatro níveis cobrem desde uma carga leve (`low`) até estresse (`higher`), e o mesmo nível aplicado nas duas branches garante comparação justa.

A injeção é **open-loop**: mantém a taxa alvo independentemente da latência, sem esperar as iterações anteriores terminarem. Isso é desejável para comparação — se uma abordagem é mais lenta, ela acumula mais requisições em voo e isso aparece no relatório, em vez de a carga cair artificialmente. O contraponto é que o teórico é um **teto de injeção, não uma promessa de vazão**: acima da capacidade da API, o excedente vira fila de conexões e depois reset de socket (`SocketException` 10054/10053), não throughput. É exatamente por isso que o `throughput.csv` compara teórico com real.

### Rastreamento do throughput teórico

O plano de carga resolvido é impresso no console antes da execução e, no fim, gravado em `throughput.csv` na pasta do relatório:

| Coluna | Significado |
|---|---|
| `scenario` | nome do cenário (mais uma linha `TOTAL`) |
| `requests_per_iteration` | requests HTTP que uma iteração completa dispara |
| `injection_rate_iterations_per_second` | taxa resolvida e passada ao `Simulation.Inject` |
| `theoretical_requests_per_second` | `requests_per_iteration × injection_rate` |
| `actual_requests_per_second` | throughput real medido pelo NBomber (soma dos steps ok + fail) |
| `attainment_percent` | real ÷ teórico × 100 |
| `duration_seconds` | duração da fase de medição |
| `theoretical_total_requests` | volume teórico na duração completa |
| `actual_total_requests` | requests HTTP efetivamente concluídos |

`attainment_percent` bem abaixo de 100% indica saturação: a API não absorveu a carga pedida, e os percentis de latência do HTML devem ser lidos como métrica de fila, não de tempo de resposta em estado estável.

---

## Warm-up

`warmupSeconds` define um período inicial cuja duração é descartada das métricas pelo NBomber. O padrão é 5 segundos.

**Por que usar warm-up?**
Nas primeiras iterações o JIT ainda não compilou os hot paths, pools de conexão estão vazios, e caches de banco estão frios. Sem warm-up essas latências artificialmente altas contaminariam os percentis finais. Com warm-up, as métricas refletem o comportamento em estado estável — que é o que interessa para comparar abordagens de persistência.

---

## Cenários

Cada cenário é categorizado pela perspectiva do banco de dados que ele mais exercita. Todos os endpoints da API são cobertos por pelo menos um cenário.

### `write` — Escrita intensiva

Exercita o caminho de gravação puro: criação e atualização de todas as entidades documentais, incluindo o ciclo completo de comentários.

| Passo | Endpoint |
|---|---|
| 01 | `POST /auth/register` |
| 02 | `POST /profile/new` |
| 03 | `POST /categories/new` |
| 04 | `PUT /categories/{id}` |
| 05 | `POST /prayers/new` |
| 06 | `PUT /prayers/{id}` |
| 07 | `POST /comments/prayer/{id}` |
| 07b | `GET /comments/prayer/{id}?page=1&pageSize=10` |
| 07c | `PUT /comments/{commentId}` |
| 07d | `POST /comments/comment/{commentId}` (sub-comentário) |
| 07e | `GET /comments/comment/{commentId}?page=1&pageSize=10` |
| 07f | `DELETE /comments/{subCommentId}` |
| 07g | `POST /comments/{commentId}/support` |
| 07h | `POST /comments/{commentId}/remove-support` |
| 07i | `DELETE /comments/{commentId}` |
| 08 | `POST /prayers/{id}/support` |
| 09 | `POST /prayers/{id}/support-remove` |
| 10 | `DELETE /prayers/{id}` |
| 11 | `DELETE /categories/{id}` |

**Por que esse cenário existe?**
Escrever em ArangoDB (uma coleção de documentos + arestas em um único banco) tem um perfil de I/O diferente de escrever em MongoDB (documento) enquanto o Neo4j permanece inalterado. Isolar as escritas deixa claro qual abordagem tem menor latência e maior throughput de escrita. O ciclo completo de comentários (criar → listar → editar → sub-comentar → reagir → excluir) exercita intensamente as edge collections `CommentedOn` e `ReactedBy` do ArangoDB.

---

### `read` — Leitura intensiva

Setup mínimo de dados seguido exclusivamente de leituras — o padrão dominante de uma rede social em produção.

| Passo | Endpoint |
|---|---|
| 01–03 | Setup: register + categoria + oração |
| 04 | `GET /prayers/{id}` |
| 05 | `GET /prayers/timeline` |
| 06 | `GET /prayers/search?Text=...` |
| 07 | `GET /prayers/search?AuthorUserName=...` |
| 08 | `GET /categories/{id}` |
| 09 | `GET /categories/user/{username}` |
| 10 | `GET /categories/mine` |
| 11 | `GET /profile/{username}` |

**Por que esse cenário existe?**
Leituras em ArangoDB usam AQL sobre document collections. Leituras em MongoDB usam find/aggregate sobre collections separadas. Sob carga alta, o comportamento de índices e o custo de serialização divergem entre os dois. Este cenário isola esse custo.

---

### `graph` — Grafo social (Neo4j na abordagem heterogênea)

Ciclo completo de amizade entre dois usuários, cobrindo todos os estados possíveis de um relacionamento.

| Passo | Endpoint |
|---|---|
| 01–02 | Register A e B |
| 03 | `GET /profile/{username}` (obter ID de B) |
| 04 | `POST /friendships/request` |
| 05 | `GET /friendships/requests/to-me` |
| 06 | `POST /friendships/{id}/accept` |
| 07 | `GET /friendships/my-mutual-friends-with/{id}` |
| 08 | `GET /friendships/suggestions` |
| 09 | `GET /friendships` |
| 10 | `POST /friendships/{id}/remove` |
| 11–12 | Enviar e cancelar pedido |
| 13–14 | Enviar e recusar pedido |

**Por que esse cenário existe?**
É o cenário que mais diferencia as abordagens. Na abordagem heterogênea, toda operação de amizade cruza o Neo4j (traversal de grafo). Na abordagem multimodelo, ArangoDB usa edge collections. A diferença de latência e throughput aqui tende a ser a mais expressiva do TCC.

**Por que dois usuários por iteração em vez de reusar dados de um pool?**
Porque reusar usuários com estado de amizade acumulado criaria dependências entre iterações (usuário A já tem amizade com B, então `accept` falha na segunda vez). Criar novos usuários por iteração garante isolamento real — cada iteração é autocontida e os resultados não interferem uns nos outros.

---

### `document` — Documentos (MongoDB na abordagem heterogênea)

Foco nas operações CRUD sobre entidades documentais (categorias e orações) sem envolver o grafo.

| Passo | Endpoint |
|---|---|
| 01 | Register |
| 02 | `POST /categories/new` |
| 03 | `POST /prayers/new` |
| 04 | `GET /prayers/{id}` |
| 05 | `PUT /prayers/{id}` |
| 06 | `GET /prayers/search` |
| 07 | `GET /prayers/timeline` |
| 08 | `GET /categories/{id}` |

**Por que esse cenário existe separado do `write`?**
O `write` tem DELETE e cobre o ciclo completo (criar → destruir). Este cenário isola especificamente o throughput de leitura e escrita de documentos sem a pressão das exclusões, que têm comportamento diferente nos dois bancos.

---

### `mixed` — Grafo + Documentos (cross-store)

Jornada de usuário realista que cruza os dois stores: cria conteúdo (MongoDB/ArangoDB) e forma vínculos sociais (Neo4j/ArangoDB) na mesma iteração, incluindo retrospectiva e interações de comentário.

| Passo | Endpoint |
|---|---|
| 01 | Register A |
| 02 | `POST /auth/login` |
| 03 | Register B |
| 04 | `GET /profile/{username}` |
| 05 | `POST /prayers/new` |
| 06 | `POST /friendships/request` |
| 07 | `POST /friendships/{id}/accept` |
| 08 | `GET /prayers/timeline` |
| 09 | `GET /friendships/my-mutual-friends-with/{id}` |
| 10 | `GET /prayers/search` |
| 11 | `POST /comments/prayer/{prayerId}` |
| 11b | `GET /comments/prayer/{prayerId}?page=1&pageSize=10` |
| 11c | `POST /comments/{commentId}/support` |
| 11d | `POST /comments/{commentId}/remove-support` |
| 12 | `GET /retrospective/my-retrospective` |

**Por que esse cenário existe?**
Na abordagem heterogênea, operações cross-store têm custo de coordenação entre MongoDB e Neo4j dentro do mesmo request. ArangoDB executa tudo em um único banco, evitando esse round-trip. Esse cenário captura exatamente esse custo de coordenação — que pode ser a diferença mais relevante para o TCC.

**Por que o login está aqui?**
O `mixed` é o único cenário que testa o `POST /auth/login` separado do register, para cobrir o pipeline de validação JWT sem o custo de criação de conta. O cenário `write` já testa o register.

---

### `all` — Todos os cenários (padrão)

Executa os cinco cenários **em paralelo** dentro do mesmo runner do NBomber.

**Quando usar:** para uma visão rápida da suíte completa, ou quando o tempo é limitado.

**Quando não usar:** para atribuir consumo de CPU/RAM a uma categoria específica. Com os cinco cenários rodando ao mesmo tempo, o `resource-usage.csv` reflete o uso combinado de todos eles — você não consegue separar quanto do CPU do MongoDB veio das leituras versus das escritas. Rode um cenário por vez quando quiser essa granularidade.

---

## Como as métricas se complementam

### O que o NBomber mede

O NBomber coleta, por step e por cenário:

- **Latência** — p50, p95 e p99 das requisições concluídas durante a fase de medição (excluindo o warm-up)
- **Throughput** — requisições concluídas por segundo
- **Taxa de erro** — proporção de steps que retornaram falha

Esses dados aparecem no `nbomber_report.html` com gráficos de série temporal e tabelas de percentis.

O NBomber **não** mede CPU nem RAM do sistema sob teste. Ele coleta, internamente, o uso de CPU e memória da própria máquina que está executando o benchmark — isso serve apenas para confirmar que o gerador de carga não é o gargalo. Esse número não aparece no HTML e não é útil para comparar as abordagens de banco de dados.

### O que o ResourceSampler mede

O `ResourceSampler` (em `ResourceSampler.cs`) preenche essa lacuna consultando a **Docker Engine API** periodicamente durante toda a execução do benchmark.

A cada `sampleIntervalSeconds` (padrão: 2 segundos), para cada container monitorado, o sampler chama:

```
GET /containers/{nome}/stats?stream=false
```

O Docker retorna um snapshot de estatísticas do cgroup do container. A partir desse snapshot, duas métricas são calculadas:

**CPU%**

O Linux expõe o uso de CPU de um container como um contador cumulativo de nanossegundos (`cpu_usage.total_usage`). Para obter uma porcentagem instantânea, o Docker compara dois snapshots consecutivos:

```
cpu_delta    = total_usage[atual] − total_usage[anterior]
system_delta = system_cpu_usage[atual] − system_cpu_usage[anterior]
cpu%         = (cpu_delta / system_delta) × número_de_cpus × 100
```

O `system_cpu_usage` é o tempo de CPU total do host (todos os cores, todos os processos) no mesmo intervalo. Dividir pelo delta do sistema normaliza o percentual em relação à capacidade real disponível para o container. Multiplicar pelo número de CPUs online converte para o intervalo 0–100% por core (o mesmo que `docker stats` exibe).

**Memória (MB)**

Lida diretamente de `memory_stats.usage`, que é o consumo de memória residente atual do container em bytes, dividido por 1 024² para converter em megabytes. Não inclui swap.

### Onde os dados são gravados

```
reports/<abordagem>/<nivel>_<cenario>_<timestamp>/
  nbomber_report.html   ← latência, throughput e taxa de erro (NBomber)
  resource-usage.csv    ← CPU% e memória MB por container ao longo do tempo
```

Formato do CSV:

```
timestamp,container,cpu_percent,memory_mb
2026-06-23T14:00:02+00:00,ebenezer-mongodb,12.34,256.00
2026-06-23T14:00:04+00:00,ebenezer-mongodb,14.10,257.50
2026-06-23T14:00:02+00:00,ebenezer-neo4j,4.21,128.50
```

Cada linha é uma amostra de um container num instante específico. Para analisar os dados é possível filtrar por container, calcular médias e picos, e correlacionar os timestamps com os períodos de cada cenário visíveis no HTML.

### Por que amostrar em vez de medir de forma contínua?

A Docker Engine API só fornece estatísticas de forma síncrona (uma leitura por requisição HTTP, sem streaming neste modo). Fazer uma amostra a cada 2 segundos é um compromisso razoável entre granularidade e overhead: intervals muito curtos adicionam carga de I/O ao próprio benchmark; intervals muito longos perdem picos transitórios de CPU que ocorrem durante rajadas de requisições. Para os fins do TCC, 2 segundos é suficiente para capturar tendências e comparar comportamento médio e máximo entre as abordagens.

### Por que não confiar no CPU% reportado pelo NBomber?

Porque ele mede o processo .NET do gerador de carga, não o banco de dados. Sob cargas altas o gerador também consome CPU (serialização JSON, gestão de conexões HTTP, geração de dados com Bogus) — esse número misturaria o custo do benchmark com o custo da persistência, tornando a comparação inválida.

### Limitação importante: `all` vs. cenário único

Quando `scenario` é `all`, os cinco cenários rodam em paralelo. O CSV reflete o uso combinado de todos eles no mesmo container. Não é possível atribuir, por exemplo, quanto do CPU do MongoDB veio do cenário `write` versus o `read`. Para essa granularidade, rode um cenário por vez.

---

## Estrutura do projeto

```
EbenezerBackend.LoadTests/
  Program.cs                  ← ponto de entrada; define e executa os cenários
  LoadConfig.cs               ← leitura do config, predefinições de carga, PersistenceApproach
  ThroughputPlanner.cs        ← converte o alvo de req HTTP/s em iterações/s por cenário
  ResourceSampler.cs          ← coleta de CPU/RAM via Docker Engine API
  Reporting/                  ← writers de CSV (erros, steps pulados, throughput)
  benchmark.config.jsonc      ← parâmetros editáveis (URL, nível, cenário, recursos)
  reports/                    ← saída (criada na execução; não commitar)
    multimodel/
    heterogeneous/
```

### Por que um único `Program.cs` em vez de classes separadas por cenário?

Os cenários são closures que fecham sobre o `HttpClient` compartilhado, os helpers (`Call`, `Register`) e o perfil de carga (`profile`). Separá-los em arquivos exigiria passar essas dependências explicitamente, adicionando cerimônia sem benefício real dado o tamanho do projeto. Para um TCC com cinco cenários fixos, a legibilidade de tudo em um lugar supera a modularidade.

### Por que um único `HttpClient` compartilhado?

Criar um `HttpClient` por iteração (como fazia a versão anterior) esgota o pool de sockets do SO sob carga alta — um problema clássico em .NET chamado socket exhaustion. Um cliente compartilhado com `BaseAddress` configurado reutiliza conexões via HTTP keep-alive, o que é o comportamento correto para um benchmark: você mede o desempenho do banco, não do overhead de abertura de conexões TCP.

---

## Reprodutibilidade entre branches

Para que a comparação do TCC seja válida, as duas rodadas precisam ser idênticas exceto pela camada de persistência. Checklist:

- [ ] Mesmo `loadLevel` nas duas rodadas
- [ ] Mesmo `scenario` nas duas rodadas
- [ ] Mesmo `warmupSeconds`
- [ ] API na mesma versão em ambas as branches (mesmo contrato de endpoints)
- [ ] Docker com recursos equivalentes (mesmos limites de CPU/memória nos containers)
- [ ] Banco limpo (sem dados de rodadas anteriores) antes de cada execução

O único campo que muda entre branches é o argumento na linha de comando (`multimodel` vs `heterogeneous`). Todo o resto — config, código da suíte, endpoints testados — é idêntico.
