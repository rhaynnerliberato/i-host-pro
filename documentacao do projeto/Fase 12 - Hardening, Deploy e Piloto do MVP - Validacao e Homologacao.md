# Fase 12 — Hardening, Deploy e Piloto do MVP — Validação e Homologação

Versão: 1.1
Status: Em andamento — Checkpoint 2 concluído

## 1. Objetivo

Registrar a validação e homologação da Fase 12 (Hardening, Deploy e Piloto do MVP), conforme `Plano Executivo de Desenvolvimento por Fases.md` §3 (linha única de escopo: *"Segurança final, observabilidade, CI/CD, implantação e validação com usuários"*) e a estrutura de Checkpoints CP0–CPn proposta e aprovada pelo usuário. Fase iniciada somente após a Fase 11 (Agente de IA) ter sido definitivamente concluída e homologada no nível MVP (SHA `c3a670b`), conforme exige `Plano Executivo de Desenvolvimento por Fases.md` §2.

Diferente de toda fase anterior, a Fase 12 não introduz nenhum Bounded Context de negócio novo — é trabalho transversal de infraestrutura/operação sobre a plataforma já construída nas Fases 0-11.

## 2. Checkpoint 0 — Preflight + Decision Gate (Read-Only)

**Status:** Concluído e aprovado. Nenhum arquivo alterado.

Auditoria completa do escopo real da Fase 12 contra as fontes de verdade — Documento 21 (DevOps/Deploy, 32 seções), Documento 15 (Requisitos Não Funcionais), Documento 20 (Qualidade/Homologação), Documento 99 (Development Authorization), e as ADRs de infraestrutura (ADR-007 Observabilidade, ADR-011 CI/CD, ADR-012 Chaves JWT, ADR-006 Cache/Storage) — mais auditoria direta do estado real do código (CI existente, health checks, secrets, DLQ, rate limiting).

**Achados principais do gate:**
- **Provedor de nuvem final indefinido** (ADR-011) — a própria ADR já registra isso como decisão pendente "a ser confirmada antes da Fase 12 (deploy real)". Bloqueia diretamente qualquer checkpoint de deploy real (IaC, KMS, alvo gerenciado de Postgres/RabbitMQ/Redis).
- **Pipeline CI existente estava morta**: `.github/workflows/ci.yml` disparava em `branches: [main]` — uma branch que nunca existiu neste repositório (só `master`/`feature/**`) — nunca rodou em nenhum push real. Cobria apenas Identity & Access (stub explícito da Fase 0/1).
- **Zero backend de secret de Produção em toda a plataforma** — confirmado por auditoria direta: exatamente 4 `Development*CredentialProvider` (Anthropic, WhatsApp envio, WhatsApp webhook, PropertyAccess), nenhum equivalente de Produção em lugar nenhum.
- **Health checks incompletos**: `IHostPro.Api` tem `AddHealthChecks()`/`/health` sem nenhum check real de dependência registrado (sempre "healthy"); `IHostPro.Worker` não tem endpoint de health nenhum.
- **"Piloto com usuários" é indocumentado** em qualquer fonte — nenhum processo, critério de sucesso, tenant(s), ou duração definidos em lugar nenhum.
- **Nenhum SLA/uptime/RTO/RPO numérico** existe em nenhum documento — apenas linguagem qualitativa.

**Decisões fechadas pelo usuário no fechamento do gate:**
```
CloudProviderDecision=DEFERRED_TO_CP5
PilotDefinition=DEFERRED_TO_BEFORE_CP6
SlaRtoRpoNumericTargets=DEFERRED_TO_CP5_OR_CP6
WorkingBranch=feature/hardening-deploy
```

Proposta de sequência de checkpoints aprovada: CP1 (CI/CD Pipeline Hardening) → CP2 (Observability Finalization) → CP3 (Resilience & Rate Limiting) → CP4 (Security & Secrets/LGPD Hardening) → CP5 (Cloud Provider & Production Deploy Target) → CP6 (Production Cutover & Piloto) → CP7 (Final Homologation) — sujeita a revisão em cada CP0 subsequente, nunca fixa.

**Nota de governança registrada para checkpoints futuros**: por instrução explícita do usuário, uma auditoria formal de **SaaS Commercial Readiness** (60 itens — tenant provisioning, onboarding, planos, billing, licenciamento, suspensão/reativação, LGPD, backoffice administrativo, etc.) deve ser produzida antes que o planejamento da Fase 12 seja declarado encerrado — determinando se cada gap pertence à própria Fase 12 ou a uma futura "Fase 13 — Comercialização SaaS". Ainda não iniciada; não deve ser antecipada fora de hora.

## 3. Checkpoint 1 — CI/CD Pipeline Hardening

**Status:** Concluído. `CiTriggerFixed=true`. `AllExistingUnitSuitesCovered=true`. `AllExistingIntegrationSuitesCovered=true`. `ArchitectureTestsCovered=true`. `FullApiIntegrationCovered=true`. `RealExternalProviderTestsExcludedFromDefaultCi=true`. `StaticAnalysisImplemented=true`. `PackagingValidationImplemented=true`. `ProductionRegistryPush=false`. `BusinessCodeChanged=false`.

**Objetivo**: corrigir e completar a pipeline de CI para que reflita de verdade o estado atual da plataforma — trigger correto, cobertura de todos os Bounded Contexts com projeto de teste real, análise estática, e validação de empacotamento — sem tocar em nenhuma regra de negócio.

### 3.1 Auditoria do workflow existente

O `.github/workflows/ci.yml` anterior (herdado da Fase 0/1) tinha exatamente um job (`build-and-test`), trigger `branches: [main]` (branch inexistente neste repositório), e cobria somente `IHostPro.ArchitectureTests` + `IHostPro.Contexts.Identity.Tests.Unit` + `IHostPro.Contexts.Identity.Tests.Integration` — o próprio comentário do arquivo já avisava: *"steps for additional Bounded Contexts... will be added as those modules and pipelines are implemented."*

Auditoria dos 27 projetos de teste reais existentes (`find tests -iname "*.csproj"`) confirmou: 13 projetos Unit, 11 projetos Integration, `IHostPro.ArchitectureTests`, `IHostPro.Api.Tests.Integration` (E2E completo), `IHostPro.Web.Tests.E2E` (E2E de frontend, Angular + Playwright).

### 3.2 Trigger corrigido

```yaml
on:
  push:
    branches: [master, "feature/**"]
  pull_request:
    branches: [master]
```

Nenhum outro motivo técnico/documental para uma regra diferente foi encontrado — a plataforma nunca teve branch `main`, apenas `master` (padrão) e `feature/**` (trabalho em andamento).

### 3.3 Cobertura de testes — matriz completa

Auditoria de dependência de cada projeto Integration (via grep direto nos `.csproj` por `Testcontainers.PostgreSql`/`Testcontainers.RabbitMq`/`Testcontainers.Redis`) confirmou: **os 11 projetos Integration provisionam Postgres/RabbitMQ/Redis inteiramente via Testcontainers próprios — nenhum depende de um banco externo pré-existente**. Isso significa que o workflow nunca declara `services:` fixos — cada job de matriz já roda em sua própria VM isolada do GitHub Actions, o que é exatamente o que evita a colisão de porta fixa do RabbitMQ (5672) observada localmente duas vezes nesta mesma sessão (um container `ihostpro-rabbitmq` de desenvolvimento e um Testcontainers efêmero disputando a mesma porta no mesmo daemon Docker) — essa falha só pode ocorrer quando dois processos assim compartilham um único daemon Docker, o que nunca acontece entre jobs de matriz separados.

Estrutura final: `build` (fast-fail gate, restore+build Release da solução inteira) → `architecture-tests`, `unit-tests` (matriz de 13), `integration-tests` (matriz de 11), `api-integration-tests`, `frontend-e2e-tests`, `packaging` (matriz de 3), todos com `needs: build`.

`-m:1` (MSBuild single-threaded) aplicado uniformemente a todos os 11 jobs de Integration Tests — não só a Identity (que já tinha essa mitigação documentada por contenção real observada de Testcontainers/daemon Docker em CI), mas também aos outros 10 projetos, que nunca rodaram em GitHub Actions antes — escolha conservadora de estabilidade, nunca verificada como estritamente necessária para os 10, mas estritamente mais segura que presumir que não precisam.

### 3.4 Testes reais externos — excluídos explicitamente

`AnthropicRealProofTests` (AIAgent.Tests.Integration), `MetaWhatsAppSandboxProofTests` (ExternalIntegrations.Tests.Integration), `AnthropicRealAgentWorkflowRoundTripTests` (Api.Tests.Integration) — os três únicos testes reais gated a credencial externa existentes na base de código (confirmado por grep exaustivo) — excluídos explicitamente via `--filter "FullyQualifiedName!~<Nome>"` na pipeline padrão, nunca dependendo apenas do próprio comportamento de auto-skip-quando-sem-credencial desses testes (que já existe, mas é tratado como defesa em profundidade, não como o único mecanismo). Validado com evidência real: `AnthropicRealProofTests` excluído → 31/31 aprovados (de 32 totais); `MetaWhatsAppSandboxProofTests` excluído → 57/57 aprovados.

### 3.5 Full API Integration

`IHostPro.Api.Tests.Integration` incluído na pipeline (job dedicado `api-integration-tests`) — precisa de um build Debug do `IHostPro.Worker` além do build Release da solução (a própria `Fixture` de cada suíte E2E lança `bin/Debug/net10.0/IHostPro.Worker.dll` como subprocesso real), replicado exatamente na pipeline. Evidência local reutilizada desta mesma sessão: 87/87 aprovados, 29min21s (execução completa mais recente, anterior à criação da nova suíte real do CP7, que é excluída aqui).

### 3.6 Frontend E2E — inclusão com ressalva de transparência

`IHostPro.Web.Tests.E2E` (Angular + Playwright, Testcontainers próprios) incluído na pipeline por cobertura completa, com job dedicado (`npm ci` + build Debug do Worker + build Release da solução + `dotnet test`) — Playwright Chromium é auto-instalado pela própria Fixture (`Microsoft.Playwright.Program.Main(["install","chromium"])`), sem step adicional necessário. **Transparência**: esta suíte não foi executada localmente ponta-a-ponta nesta sessão (peso real — servidor `ng serve` + Playwright + stack backend completo — e não estava na lista mínima explícita do próprio mandato do CP1 para validação local). Incluída no design da pipeline por cobertura completa (mandato item 6), mas seu comportamento real em CI ainda não tem evidência direta própria deste checkpoint — risco residual registrado, não escondido.

### 3.7 Análise estática (Documento 21 §9)

Sem nenhuma dependência nova/paga: os analisadores Roslyn já embutidos no SDK (`EnableNETAnalyzers=true` por padrão em projetos SDK-style) já rodam em todo `dotnet build` — nenhum warning novo além do baseline pré-existente (20 avisos `NU1903`, SSH.NET, já documentados desde fases anteriores). `dotnet list package --vulnerable --include-transitive` adicionado como step explícito (auditoria de vulnerabilidade de dependências, mecanismo nativo do NuGet, zero ferramenta nova) — informativo, nunca falha o build (corrigir a dependência é decisão de upgrade, fora de escopo deste checkpoint).

`dotnet format --verify-no-changes` executado localmente antes de qualquer decisão: **457 desvios de formatação (whitespace) em 22 arquivos**, toda dívida pré-existente, nenhuma relacionada a este checkpoint. Por instrução explícita do mandato ("não transformar dívida histórica em refactor fora de escopo"), incluído na pipeline como step **não-bloqueante** (`continue-on-error: true`, emite um aviso do GitHub Actions com o baseline exato) — nunca como gate.

Scanners nativos de secret do GitHub (secret scanning/push protection) são uma configuração do repositório, não um arquivo de workflow — fora do alcance desta sessão para habilitar (exige acesso às configurações do repositório no GitHub, nunca `dotnet user-secrets list`, nunca lido/impresso por este checkpoint); recomendado que o usuário confirme se já está habilitado nas configurações de Segurança do repositório.

### 3.8 Empacotamento (Documento 21 §6-7)

**Nenhum Dockerfile existia em lugar nenhum do repositório antes deste checkpoint.** Três novos, mínimos, multi-stage, padrão oficial .NET (`mcr.microsoft.com/dotnet/sdk:10.0` para build, `mcr.microsoft.com/dotnet/aspnet:10.0` para a Api, `mcr.microsoft.com/dotnet/runtime:10.0` para Worker/MigrationRunner): `docker/Api.Dockerfile`, `docker/Worker.Dockerfile`, `docker/MigrationRunner.Dockerfile` — contexto de build sempre a raiz da solução (`i-host-pro/`), nunca a própria pasta `docker/`. Um `.dockerignore` novo evita copiar `bin/`/`obj/`/`node_modules/`/`.git/` para o contexto de build (achado real: sem ele, o primeiro build da imagem da Api levou mais de 4 minutos só na etapa de publish por excesso de contexto). As 3 imagens foram validadas localmente com sucesso real (`docker build`, exit code 0 para as 3) e removidas em seguida — **nenhuma foi publicada em nenhum registry** (fora de escopo deste checkpoint, mandato item 18).

### 3.9 Artifacts

Cada job de teste publica seu próprio TRX via `actions/upload-artifact` (nome único por matriz, ex. `test-results-unit-PropertyManagement`) — nunca secrets, `.env`, corpos de resposta de provider real, ou credenciais de hóspede.

### 3.10 Cobertura de código

Nenhuma coleta de cobertura estava configurada/consumida antes deste checkpoint (apenas o pacote `coverlet.collector` já referenciado em todo projeto de teste, nunca ativado). `CoverageThreshold=NOT_DEFINED` — nenhum threshold percentual foi inventado; decisão documental futura, se desejada.

### 3.11 Testes — evidência

| Suíte | Resultado |
|---|---|
| `AnthropicRealProofTests` excluído (filtro novo, validado) | 31/31 aprovados |
| `MetaWhatsAppSandboxProofTests` excluído (filtro novo, validado) | 57/57 aprovados |
| Build Release (solução completa, reconfirmado após as mudanças deste checkpoint) | 0 erro (20 avisos `NU1903` pré-existentes, inalterados) |
| `docker build` — Api/Worker/MigrationRunner (3 imagens, novas, validadas localmente) | 3/3 sucesso, exit code 0 |
| YAML do workflow | Validado sintaticamente (parser `js-yaml`) — 7 jobs, matriz de 13 (Unit)/11 (Integration)/3 (packaging), triggers corretos |
| `git diff --check` | Sem erros (apenas aviso benigno de normalização LF→CRLF) |
| Revisão de dados sensíveis nos arquivos novos (`ci.yml`, `.dockerignore`, 3 Dockerfiles) | Nenhuma ocorrência de padrão de chave/senha/secret |
| Demais suítes (13 Unit, 11 Integration, ArchitectureTests 304/304, Api.Tests.Integration 87/87) | Evidência reutilizada desta mesma sessão (mesma árvore de commit, `c3a670b`, zero mudança de código de negócio/teste neste checkpoint) — não re-executadas integralmente, per mandato item 24 combinado com a ausência de qualquer mudança que pudesse afetá-las |

### 3.12 Escopo explicitamente não implementado (por decisão do gate, não por omissão)

- Push para registry de imagens — `ProductionRegistryPush=false`, fora de escopo (CP5).
- Habilitação de secret scanning nativo do GitHub — configuração de repositório, recomendada ao usuário, não executável por este checkpoint.
- Threshold de cobertura de código — `CoverageThreshold=NOT_DEFINED`.
- Correção do débito de formatação pré-existente (457 desvios/22 arquivos) — registrado como baseline não-bloqueante, nunca corrigido silenciosamente.
- Validação local ponta-a-ponta de `IHostPro.Web.Tests.E2E` — incluído na pipeline, mas sem evidência local própria deste checkpoint (§3.6).

`Cp1CommitCount`: registrado no relatório final da conversa de homologação.

## 4. Checkpoint 2 — Observability Finalization

**Status:** Concluído. `ApiLivenessImplemented=true`. `ApiReadinessImplemented=true`. `WorkerLivenessImplemented=true`. `WorkerReadinessImplemented=true`. `CriticalDependencyHealthChecksImplemented=true`. `HealthResponseSensitiveDataLeak=false`. `DistributedTracingOperational=true`. `TraceBackendAvailable=true`. `TraceLogCorrelationImplemented=true` (via TraceId/SpanId, standard OTel/ASP.NET Core log-scope enrichment — no new mechanism built). `OperationalMetricsImplemented=true`. `HighCardinalityTenantLabels=false`. `ProviderTelemetrySanitized=true`. `AlertCatalogImplemented=true` (catálogo documentado; `AlertDeliveryProviderRequiredForMvp=false`). `SensitiveTelemetryReviewGreen=true`. `BusinessCodeBehaviorChanged=false`.

**Objetivo**: levar a observabilidade de infraestrutura básica (OTLP chegando ao Collector, métricas via Prometheus, health check trivial) a operacionalmente utilizável — health checks reais com semântica liveness/readiness, tracing distribuído com um backend real, métricas cobrindo os limites (boundaries) que hoje são invisíveis, e um catálogo de alertas explícito. Nenhuma funcionalidade de negócio alterada.

### 4.1 Auditoria — o que já existia vs. o que faltava

Confirmado por leitura direta do código e do ADR-007 (Status: Atualizado):

- Logs estruturados (Serilog) e a exportação de métricas (OTLP → Collector → Prometheus → Grafana) já funcionavam.
- **Tracing tinha três boundaries estruturalmente invisíveis**, apesar do SDK do OTel já estar registrado: (1) o `ActivitySource` próprio do Wolverine nunca era escutado — `AddSource("Wolverine")` é exigido explicitamente pela própria documentação do Wolverine, confirmado ausente em `IHostPro.Api`/`IHostPro.Worker`; (2) chamadas HTTP de saída (as chamadas reais do `AnthropicModelProvider` e do conector WhatsApp) não tinham nenhuma instrumentação; (3) chamadas SQL reais via Npgsql não eram rastreadas.
- **Backend de tracing**: o ADR-007 já registrava isso como pendência explícita — o Collector recebia traces mas apenas os descartava (exportador `debug`), nunca persistindo/visualizando.
- **Health checks**: `IHostPro.Api` tinha `AddHealthChecks()`/`/health` sem nenhuma dependência real registrada (sempre "Healthy" independente de Postgres/RabbitMQ/Redis estarem de pé); `IHostPro.Worker` não tinha nenhum endpoint de health.
- **Métricas de IA**: nenhuma métrica de negócio existia para chamadas/tokens/custo/erros do modelo de linguagem, apesar do CP7 já computar tudo isso para persistência.

### 4.2 Backend de tracing — decisão técnica (Grafana Tempo)

O Documento 21 não prescreve um produto específico, delegando a escolha quando uma opção for claramente suficiente (mandato item 12). Comparação real entre Jaeger/Tempo/Zipkin: Tempo venceu por (1) ingestão OTLP nativa (zero camada de tradução), (2) footprint equivalente a um binário Go único (mesmo perfil operacional já aceito para o Prometheus), (3) **critério decisivo**: renderiza DENTRO da mesma instância do Grafana já provisionada para métricas, em vez de exigir uma segunda UI de visualização de traces separada (Jaeger), e (4) é o único dos três com um equivalente hospedado oficial (Grafana Cloud Tempo), facilitando uma futura migração de nuvem sem prender esta decisão a nenhum provedor específico (ADR-011, CP5 continua em aberto). Adicionado ao `docker-compose.yml` (`tempo`, imagem `grafana/tempo:2.6.1`, armazenamento local — um backend real de Produção é decisão do CP5), ao pipeline do Collector (novo exportador `otlp/tempo`) e como novo datasource do Grafana (`observability/grafana/provisioning/datasources/datasource.yml`), com correlação métrica↔trace via exemplars já habilitada de graça por compartilharem a mesma instância do Grafana.

### 4.3 Tracing — as três instrumentações adicionadas

`AddSource("Wolverine")` (captura real de todo processamento de mensagem, incluindo falhas/retries — também torna `WolverineClusterAgentAssignmentDebt` diagnosticável via trace, sem tentar corrigi-lo estruturalmente, conforme o próprio mandato exige), `AddHttpClientInstrumentation()` (cobre as chamadas reais de saída do Anthropic e do WhatsApp), e `AddNpgsql()`/`AddNpgsqlInstrumentation()` (o próprio pacote de primeira parte do Npgsql, `Npgsql.OpenTelemetry` — nunca a instrumentação genérica ADO.NET do opentelemetry-dotnet-contrib) para tracing e métricas respectivamente. **Achado real durante a implementação**: o nome exato do método de extensão de métricas do Npgsql é `AddNpgsqlInstrumentation()`, não `AddNpgsql()` (usado apenas no lado de tracing) — descoberto por inspeção direta do assembly via reflection (`System.Reflection`, um projeto de sondagem descartável), depois que uma fonte externa consultada inicialmente (documentação de terceiros) se mostrou incorreta — nunca assumido sem verificação direta contra o binário real. Nenhum span customizado foi adicionado a nenhum método de aplicação — apenas essas três instrumentações de boundary.

### 4.4 Health checks — Api e Worker

**Api**: `AddHealthChecks()` substituído por checks reais — `AddNpgSql` (Postgres, via `ConnectionStrings:Platform`), `AddRabbitMQ` (via uma factory lazy de `IConnection`, mesmos parâmetros já usados pelo `UseIHostProRabbitMq` do Wolverine), `AddRedis` (via `Configuration:PolicyCache:ConnectionString`) — todos tagueados `"ready"`. `/health/live` (nenhum check, `Predicate = _ => false`) e `/health/ready` (todos os checks `"ready"`) são endpoints novos e distintos; `/health` é preservado, idêntico a `/health/ready`, por compatibilidade retroativa.

**Worker**: nunca teve nenhum endpoint de health. `Host.CreateApplicationBuilder` foi trocado por `WebApplication.CreateBuilder` — a ÚNICA mudança estrutural deste checkpoint no bootstrap do Worker, exclusivamente para ganhar um listener Kestrel mínimo (`FrameworkReference Microsoft.AspNetCore.App`, novo). Nunca adiciona controllers/Swagger/qualquer superfície HTTP de negócio — o Worker continua sendo um host de processamento de mensagens em background, nunca uma segunda Api (ressalva explícita do próprio mandato). Porta padrão `5141` (uma acima da porta 5140 já estabelecida da Api), aplicada apenas como fallback quando `ASPNETCORE_URLS`/`urls` não estiverem já configurados — nunca hardcoded como única opção.

**Classificação de criticidade** (Healthy/Degraded/Unhealthy, nunca "sempre Healthy"): Postgres e RabbitMQ são dependências rígidas para todo caminho de escrita/processamento — `Unhealthy` quando indisponíveis. Redis alimenta exclusivamente o cache de leitura de Configuration & Policy (`CachedPolicyValueResolver` não tem fallback para Postgres em caso de falha do Redis, confirmado por leitura direta do código-fonte) — uma falha do Redis prejudica apenas leituras dependentes de policy, nunca o processo inteiro, então é reportada como `Degraded`, nunca `Unhealthy`, evitando que uma falha parcial do Redis derrube toda a sonda de readiness.

**Resposta segura**: `ObservabilityHealthCheckResponseWriter` (duplicado entre `IHostPro.Api`/`IHostPro.Worker` — `BuildingBlocks.Infrastructure` não referencia o framework compartilhado do ASP.NET Core e não deveria ganhar essa dependência só por causa de dois endpoints de host) emite exclusivamente nome/status/duração por componente — nunca `HealthReportEntry.Description`/`Exception`, que poderiam vazar uma connection string ou uma mensagem de exceção bruta do driver.

### 4.5 Métricas de IA (Documento 21 §16 — "IA" é categoria obrigatória)

Um único `Meter` (`IHostPro.AIAgent`, construído dentro de `AnthropicModelProvider`, a única classe que o utiliza) com 3 instrumentos — `ai_agent.model_calls` (contador, tags `provider`/`model`/`outcome` — um outcome diferente de `"Success"` JÁ representa o erro, nenhum instrumento de erro separado foi necessário), `ai_agent.tokens` (contador, tags `provider`/`model`/`direction`), `ai_agent.cost_usd` (contador, tags `provider`/`model`) — todos alimentados a partir do único ponto de convergência (`LogOutcome`) que já existia desde o CP7, nunca um novo ponto de instrumentação espalhado pela classe. Registrado via `.AddMeter("IHostPro.AIAgent")` somente em `IHostPro.Worker` (o único processo que constrói `AnthropicModelProvider`) — nunca em `IHostPro.Api`. Todas as tags são enums fechados e de baixa cardinalidade — nunca tenant/reservation/conversation id, telefone, ou qualquer valor não-limitado (proibição explícita do mandato).

### 4.6 Catálogo de alertas (documentado, não implementado como entrega)

Registrado explicitamente, sem nenhum provedor de entrega (`AlertDeliveryProviderRequiredForMvp=false` — Documento 21/15 não definem um provedor operacional ainda, e o próprio mandato autoriza separar `AlertDefinitionImplemented` de `AlertDeliveryProviderConfigured`):

| Alerta | Fonte do sinal | Threshold |
|---|---|---|
| Api indisponível | `/health/live` falhando | `Threshold=TBD_FOR_PRODUCTION` |
| Worker indisponível | `/health/live` falhando | `Threshold=TBD_FOR_PRODUCTION` |
| Postgres indisponível | `/health/ready` component `postgres=Unhealthy` | Imediato |
| RabbitMQ indisponível | `/health/ready` component `rabbitmq=Unhealthy` | Imediato |
| Redis indisponível (não-crítico) | `/health/ready` component `redis=Degraded` | `Threshold=TBD_FOR_PRODUCTION` |
| Taxa de erro elevada | métricas HTTP/AspNetCore já exportadas | `Threshold=TBD_FOR_PRODUCTION` |
| Falha de processamento de mensagem | trace/log do Wolverine (`AddSource("Wolverine")`, novo) | `Threshold=TBD_FOR_PRODUCTION` |
| Acúmulo em dead-letter | log estruturado do Wolverine (comportamento padrão já existente — nenhuma ferramenta de monitoramento/replay construída, CP3 mandato item 21) | `Threshold=TBD_FOR_PRODUCTION` |
| Falha de provider externo (Anthropic/Meta) | `ai_agent.model_calls{outcome!=Success}` (novo) / métricas HTTP existentes | `Threshold=TBD_FOR_PRODUCTION` |
| Anomalia de custo de IA | `ai_agent.cost_usd` (novo) | `Threshold=TBD_FOR_PRODUCTION` |
| Falha de deployment/migration | fora do escopo deste checkpoint (CP5) | — |

### 4.7 Revisão de PII/LGPD em telemetry (gate obrigatório do checkpoint)

Nenhum vazamento encontrado — analisado por comportamento padrão documentado de cada instrumentação, nenhuma delas configurada para enriquecer com dados de requisição/resposta: `AddHttpClientInstrumentation()` captura apenas método/host/path/status/duração, nunca headers (logo nunca `Authorization`/`x-api-key`) nem corpo — nenhum `EnrichWithHttpRequestMessage` foi adicionado. `AddNpgsql()` pode capturar o texto do comando SQL como atributo de span, mas o EF Core (usado em toda a base de código, confirmado em todo o histórico de queries já observado nesta sessão) sempre parametriza — nunca interpola valores literais no texto do comando, então mesmo um `db.statement` capturado nunca contém `GuestPhone`/segredo real. Wolverine tagueia spans com tipo/destino da mensagem, nunca o corpo serializado. As métricas de IA (§4.5) usam apenas tags de enum fechado. O `ObservabilityHealthCheckResponseWriter` (§4.4) nunca serializa `Description`/`Exception`. Testes automatizados (`ObservabilityHealthChecksWorkflowRoundTripTests`, novo) confirmam isso empiricamente contra o endpoint real.

### 4.8 Correção factual (validação final do CP2) — o fallback para Postgres existe e foi comprovado empiricamente

A afirmação original desta seção — que `CachedPolicyValueResolver` não tem fallback para Postgres quando o Redis está indisponível — estava **incorreta**. Foi baseada em uma investigação incompleta durante o CP2 (apenas `CachedPolicyValueResolver.cs` foi inspecionado por try/catch; a camada abaixo, `RedisPolicyValueCache.cs`, não foi lida).

Uma revalidação final (Gate 2 da validação de encerramento do CP2) confirmou, por leitura de código E por prova empírica, que o fallback **existe e funciona**: `RedisPolicyValueCache.TryGetAsync`/`SetAsync` envolvem toda operação Redis em `try/catch (Exception ex) when (ex is not OperationCanceledException)`, registram um `LogWarning` e retornam "cache miss" em vez de propagar a exceção — o próprio comentário XML da classe já documentava esse comportamento como deliberado. `CachedPolicyValueResolver` interpreta esse "miss" normalmente e recorre ao resolvedor real, apoiado em PostgreSQL, que permanece autoritativo mesmo com o Redis fora do ar.

A prova empírica (não apenas leitura de código) foi feita em `PolicyCacheAndOutboxTests.A_policy_resolution_still_succeeds_through_Postgres_after_its_dedicated_Redis_is_stopped_mid_test` (novo teste, Testcontainers Redis dedicado — nunca o compartilhado pela fixture da classe): resolve uma política real (populando o cache), para o container Redis dedicado em pleno teste (indisponibilidade real e controlada, não um mock), e resolve a mesma política novamente — a segunda resolução continua retornando o valor correto, através do PostgreSQL. Teste aprovado (1/1).

Complementarmente, um experimento controlado único (não commitado — manipula o container de desenvolvimento `ihostpro-redis` pelo nome fixo, não portável a CI) confirmou os quatro endpoints reais de health sob indisponibilidade real do Redis:

| Endpoint | Redis up | Redis down |
|---|---|---|
| Api `/health/live` | 200 Healthy | 200 Healthy (nunca toca dependência) |
| Api `/health/ready` | 200 Healthy (postgres/rabbitmq/redis Healthy) | 200 **Degraded** (postgres/rabbitmq Healthy, redis Degraded) |
| Worker `/health/live` | 200 Healthy | 200 Healthy |
| Worker `/health/ready` | 200 Healthy | 200 **Degraded** |

Nunca `Unhealthy`/503 em nenhum dos quatro casos — confirma que a classificação `failureStatus: Degraded` do Redis (§4.4) é semanticamente coerente com o comportamento real: Api e Worker continuam operacionais e servindo os casos de uso principais com o Redis fora do ar, apenas sem a otimização de cache.

Consequência prática: a classificação de saúde `Redis = Degraded` (§4.4) já estava correta — Redis é, de fato, apenas uma otimização de latência, nunca uma dependência rígida para `EARLY_CHECKIN`/`LATE_CHECKOUT`/`AI_AGENT_BEHAVIOR`. O risco de prontidão de CI mencionado na versão anterior desta seção (pipeline do CP1 sem um `ihostpro-redis` real acessível em `localhost:6379` no runner) também não se sustenta: sem Redis alcançável, toda leitura de cache vira "miss" e cai no PostgreSQL — mais lento (sem cache), nunca funcionalmente quebrado. Nenhum código de produção foi alterado por esta correção — apenas o registro factual desta seção. Nenhum débito de resiliência (`RedisPolicyCacheResilienceDebt`) é registrado para a Fase 12/CP3, pois a investigação não encontrou uma lacuna real a resolver.

### 4.9 Testes — evidência

| Suíte | Resultado |
|---|---|
| `ObservabilityHealthChecksWorkflowRoundTripTests` (E2E real, novo arquivo — liveness sempre 200, readiness reporta Postgres/RabbitMQ reais como componentes, resposta nunca vaza connection string/senha/exceção, `/health` idêntico a `/health/ready`) | 4/4 aprovados |
| `ConversationMessageReceivedWorkflowRoundTripTests` (E2E real, pipeline completo — regressão após as mudanças de bootstrap do Worker/Api) | 5/5 aprovados |
| `IHostPro.ArchitectureTests` (sem novo arquivo — nenhuma nova Tool/exceção/aggregate) | 304/304 aprovados (sem regressão) |
| `IHostPro.Contexts.AIAgent.Tests.Unit` (sem alteração de comportamento — apenas métricas adicionadas ao mesmo ponto de convergência já testado) | 194/194 aprovados (sem regressão) |
| `docker compose config` (validação de sintaxe do `docker-compose.yml`, incluindo o novo serviço `tempo`) | válido |
| YAML de `tempo-config.yaml`/`otel-collector-config.yaml`/`datasource.yml` (parser `js-yaml`) | válidos |
| `docker build` — Api/Worker (Dockerfiles atualizados: `curl`+`HEALTHCHECK` novos; Worker trocou a imagem final de `dotnet/runtime` para `dotnet/aspnet`, exigido pelo novo `FrameworkReference`) | 2/2 sucesso |
| Build Release (solução completa) | 0 erro, 0 aviso novo (a remoção do `PackageReference Microsoft.Extensions.Hosting`, agora redundante com o `FrameworkReference` novo, eliminou o aviso `NU1510` que apareceu transitoriamente) |
| `git diff --check` | Sem erros (apenas avisos benignos de normalização LF→CRLF) |
| Revisão de dados sensíveis | Nenhuma ocorrência de padrão de chave/senha/secret em nenhum arquivo alterado |

**Atualização (validação final do CP2)**: a suíte completa `IHostPro.Api.Tests.Integration` foi reexecutada integralmente (não apenas o subconjunto focado citado acima) como regressão final antes de declarar o CP2 definitivamente homologado — `FullApiIntegrationTotal=93`, `Passed=93`, `Failed=0`, `Skipped=0`, duração 20m27s, `ihostpro-rabbitmq` temporariamente parado e restaurado ao final (mesma disciplina de porta 5672 já documentada nesta sessão). Os três testes reais gated a credencial externa (`AnthropicRealProofTests`, `MetaWhatsAppSandboxProofTests`, `AnthropicRealAgentWorkflowRoundTripTests`) foram incluídos na execução mas retornaram trivialmente (sem nenhuma chamada de rede real) por ausência de credencial configurada neste ambiente — nunca reexecutados de fato, conforme instruído.

### 4.10 Escopo explicitamente não implementado (por decisão do gate, não por omissão)

- Manifestos Kubernetes/probes reais — `CP2 continua provider-agnostic`, decisão explícita do mandato; endpoints já compatíveis para quando isso for decidido no CP5.
- Entrega real de alertas (PagerDuty/Slack/email) — `AlertDeliveryProviderRequiredForMvp=false`, nenhum provedor definido pelos documentos-fonte.
- Ferramenta de monitoramento/replay de DLQ — `CP3 mandato item 21`, deliberadamente adiado.
- Correção estrutural do `WolverineClusterAgentAssignmentDebt` — apenas tornado diagnosticável via tracing real (`AddSource("Wolverine")`), nunca corrigido (já destinado à Fase 12 desde a Fase 9/10, correção estrutural é um checkpoint/Decision Gate próprio).
- Sentry (rastreamento de erros, ADR-007) — decisão documentada mas nunca implementada; fora do escopo explícito deste checkpoint (não listado entre os itens obrigatórios do CP2), registrado como débito residual.
- ~~Correção do achado residual do Redis/`CachedPolicyValueResolver`~~ — retirado: a validação final do CP2 (§4.8) comprovou que o achado original era factualmente incorreto (o fallback para Postgres já existe e funciona); nenhuma correção de código pendente, nenhum débito de resiliência registrado.

`Cp2CommitCount`: registrado no relatório final da conversa de homologação.

## 5. Checkpoint 3 — Resilience & Rate Limiting

### 5.1 Auditoria prévia — o que já existia vs. o que faltava

Confirmado por auditoria direta (grep/leitura de código, nunca presumido): retries HTTP outbound (Anthropic 1 retry de aplicação já existente desde CP5, WhatsApp zero retry deliberado), circuit breaking já existente no nível Wolverine (`CircuitBreaking(1)` em toda rota de publish, Api e Worker), lockout de Identity já existente (por usuário, apenas no login, nunca por IP, nunca no refresh), idempotência já implementada (Outbox/Inbox Wolverine + índice único `TenantId+InboundMessageId`). Rate limiting: **zero implementação em qualquer lugar da plataforma** (confirmado — Fase 1 já registrara isso como fora de escopo explícito). Bulkhead/concorrência: nada configurado (Kestrel default, sem `MaximumParallelMessages`). DLQ: mecanismo padrão do Wolverine já existe (`wolverine_dead_letters` por schema), zero observabilidade construída sobre ele.

### 5.2 Rate limiting — design e implementação

Backend: **Redis** (`ADR-006` já designa Redis para isso — decisão não inventada), reaproveitando `StackExchange.Redis` já existente na solução (nenhuma dependência nova). Núcleo host-agnóstico em `IHostPro.BuildingBlocks.Infrastructure.RateLimiting` (`IDistributedRateLimiter`/`RedisFixedWindowRateLimiter`) — fixed-window por `(policyName, partitionKey)`, operação atômica via script Lua único (`INCR` + `PEXPIRE` condicional, evita a race condition entre as duas operações separadas). Nenhuma tabela SQL nova. Configuração 100% externa (`RateLimiting:*`), nunca hardcoded.

Cinco políticas nomeadas, aprovadas no Decision Gate:

| Política | Partição | Redis-down | Onde é aplicada |
|---|---|---|---|
| `Authentication` | IP do cliente (`HttpContext.Connection.RemoteIpAddress` — nunca `X-Forwarded-For`, sem proxy confiável configurado) | **FailClosed** | `[EnableRateLimiting]` em `AuthController.Login`/`Refresh` |
| `Webhook` | `phone_number_id` (identificador técnico do provider — nunca telefone/remetente/corpo do hóspede) | **FailOpen** | Chamada direta a `IWebhookRateLimiter` dentro de `WhatsAppWebhookController.Receive`, após verificação de assinatura |
| `TenantApi` | `TenantId` (claim JWT) | FailOpen | Default de toda rota controller-routed sem override próprio |
| `AdminApi` | `TenantId+UserId` (internos, nunca PII externa) | FailOpen | `[EnableRateLimiting("AdminApi")]` em `UserAdministrationController`/`RolesController`/`PermissionsController` |
| `AiExpensiveOperation` | `TenantId` | FailOpen | Chamada direta a `IAiAgentRateLimiter` dentro de `ConversationMessageReceivedProcessor.HandleAsync`, ANTES do context builder/chamada real ao provider |

Defaults técnicos conservadores para dev/homologação (nunca definidos como requisito de Produção — `ProductionRateLimitThresholdsRequired=true`): Authentication 30/min, Webhook 600/min, TenantApi 1000/min, AdminApi 500/min, AiExpensiveOperation 60/min — todos em `appsettings.json` (Api e Worker), 100% sobrescrevíveis.

**Achado real durante a implementação, corrigido**: a abordagem inicial (`MapControllers().RequireRateLimiting("TenantApi")` como default, esperando que `[EnableRateLimiting("Authentication")]` de `Login` sobrepusesse) provou-se **empiricamente errada** — um teste E2E real mostrou que a convenção de grupo (`RequireRateLimiting`) é composta DEPOIS do metadata do próprio Controller/Action e por isso VENCE silenciosamente, ignorando o atributo mais específico. Corrigido com `RequireRateLimitingByDefault` (`ApiRateLimitingExtensions.cs`), uma convenção customizada que só aplica a política default a endpoints que ainda não declaram a própria `[EnableRateLimiting]` — reconfirmado pelo mesmo teste E2E após a correção.

**Boundary arquitetural preservado**: nenhum projeto `.Api`/`.Application` passou a referenciar `BuildingBlocks.Infrastructure` diretamente (regra já documentada em cada `.csproj`, "API não pode depender de tipos concretos de Infrastructure"). Onde a política precisava ser consultada fora do Host (`WhatsAppWebhookController`, `ConversationMessageReceivedProcessor`), uma interface fina foi criada em `.Application` (`IWebhookRateLimiter`, `IAiAgentRateLimiter`), implementada em `.Infrastructure`, delegando ao `IDistributedRateLimiter` compartilhado — mesmo padrão já usado pelo restante da plataforma para toda dependência cross-cutting.

### 5.3 AI cost guard — boundary real, não um endpoint HTTP inventado

Confirmado por auditoria: o AI Agent nunca é acionado por um endpoint HTTP — é disparado exclusivamente pelo consumo Wolverine de `ConversationMessageReceived`, hospedado somente em `IHostPro.Worker`. O guard (`AiExpensiveOperation`) foi aplicado exatamente nesse boundary real, dentro de `ConversationMessageReceivedProcessor.HandleAsync`, após a checagem de idempotência/sessão/escalonamento (trabalho local barato, deve sempre rodar) e ANTES do context builder e da chamada real ao provider (o trabalho caro que o guard existe para proteger). Um tenant rejeitado é tratado exatamente como uma falha técnica do model provider já existente — mesmo outcome `Failure`, mesma resposta de fallback genérica (`"Desculpe, não consegui processar sua mensagem agora..."`), nunca um novo estado de negócio, nunca billing/planos/entitlements (fora de escopo, reservado à futura auditoria de SaaS Commercial Readiness).

### 5.4 Context Budget — fecha `ProductionContextBudgetStrategyRequired` (Fase 11 CP7)

Algoritmo aprovado no Decision Gate, implementado em `AgentContextBuilder.ApplyContextBudget`: o system prompt (`AI_AGENT_BEHAVIOR`, policies, fato de hora atual/timezone) é montado inteiramente à parte (`ComposeSystemPrompt`) e NUNCA sujeito a este budget. O budget aplica-se somente ao histórico de conversa: percorrido do mais recente para o mais antigo, mantendo mensagens enquanto couberem no orçamento configurável (`AIAgent:ContextBudget:MaxHistoryTokens`, default 8000, nunca definido como número final de Produção — `ProductionContextBudgetFinalThresholdRequired=true`); ao exceder, as mensagens MAIS ANTIGAS são descartadas primeiro. A mensagem mais recente (sempre a mensagem-gatilho) é sempre preservada mesmo que sozinha exceda o orçamento.

Contagem de tokens: nenhum tokenizer oficial da Anthropic está disponível no stack sem adicionar uma dependência nova e desnecessária (confirmado — nenhum pacote desse tipo é referenciado em nenhum lugar da solução). Estimativa determinística e conservadora por caracteres (`CharsPerTokenEstimate`, default 3.5 — um valor MENOR gera uma contagem estimada MAIOR, conservador na direção seguro-para-truncar-antes), documentada explicitamente como estimativa, nunca uma contagem exata.

`AgentPendingAction`/estado de handoff: confirmado por leitura direta do código que `AgentContextBuilder` nunca monta nenhum dos dois em seu retorno (tratados inteiramente a jusante, em `ConversationMessageReceivedProcessor`) — não há, portanto, nada ali que o budget pudesse truncar; nenhum teste dedicado a isso se aplica a esta classe especificamente, por construção.

Testes (`AgentContextBuilderTests.cs`, 7 novos): conversa pequena não é truncada; conversa grande descarta as mais antigas primeiro; mensagens mais recentes sempre preservadas; a mensagem-gatilho sozinha é preservada mesmo excedendo o orçamento; system prompt/fato estruturado nunca truncados mesmo com orçamento de 1 token; `Enabled=false` retorna o histórico completo; ordenação cronológica sempre preservada nos sobreviventes. `UnlimitedConversationContext=false` provado diretamente pelo teste de conversa grande.

### 5.5 Circuit Breaker HTTP — investigação e Decision Gate específico

Investigação solicitada concluída com evidência direta de código: a decisão "sem Polly" foi tomada na Fase 9 (Checkpoint 2.2/2.3.3, mandato §12/§13, WhatsApp) e reaplicada por precedência documentado na Fase 11 CP7 (Anthropic) — mas é **testada e reforçada por uma ArchitectureTest real e específica**: `ExternalIntegrationsDependencyTests.Infrastructure_References_No_Resilience_Or_Polly_Package()` falha se qualquer assembly referenciado por `ExternalIntegrations.Infrastructure` contiver "Polly" ou "Resilience" no nome — uma checagem categórica de PACOTE, não apenas "não usar retry automático". A justificativa original documentada é especificamente sobre retry automático ("zero automatic retry framework"), mas a checagem implementada é mais ampla que isso.

Adicionar `Microsoft.Extensions.Http.Resilience` (ou Polly diretamente) — mesmo somente para circuit breaking, nunca para retry — introduziria exatamente os pacotes que esse teste já proíbe para `ExternalIntegrations.Infrastructure`, e reabriria por precedência a mesma decisão para `AIAgent.Infrastructure`. Corrigir isso exigiria modificar/enfraquecer um teste arquitetural já homologado — que é, pela própria definição do mandato deste checkpoint, uma reabertura de decisão/ADR, não algo a decidir unilateralmente.

**Resultado**: `HandRolledHttpCircuitBreaker=false` (rejeitado pelo Decision Gate). `CircuitBreakerAdditionalImplementation=BLOCKED_PENDING_DECISION_GATE` — nem "não necessário" (existe um argumento operacional real para Anthropic: sem circuit breaker, uma indisponibilidade real do provider custa um timeout de 30s completo por mensagem, repetidamente), nem implementado. Trazido de volta como uma pergunta específica e isolada: **o usuário quer formalmente emendar a ArchitectureTest existente para permitir `Microsoft.Extensions.Http.Resilience` apenas para circuit breaking (nunca retry), dado que a justificativa original era especificamente sobre um framework de retry automático — ou manter a decisão totalmente fechada e aceitar operar sem circuit breaker adicional para Anthropic/WhatsApp neste checkpoint?** Todo o restante do CP3 prosseguiu independentemente, conforme autorizado.

### 5.6 DLQ Observability

`DeadLetterObservable=true`. Implementado como `DeadLetterMetricsBackgroundService` (Worker), um `BackgroundService` que consulta `count(*)` de `wolverine_dead_letters` por schema ancilar a cada 60s, cacheado em memória e exposto via `ObservableGauge` OTel (`wolverine.dead_letters`, label único de baixa cardinalidade: nome do schema — nunca `TenantId`/`MessageId`/payload). `DlqPayloadExposed=false` — a query nunca lê `body`/`message_type`, apenas `count(*)`. Nunca escrito em nenhum health check (`DlqHealthThreshold=TBD_FOR_PRODUCTION` — uma contagem histórica de dead-letters nunca vira `Unhealthy` automaticamente). `DlqReplayAdministrativeCapability=DEFERRED` — nenhuma ferramenta de replay construída, permitido explicitamente pelo mandato dado que a observabilidade mínima já existe.

### 5.7 Pricing — configuração operacional explícita

`AnthropicPricingOptions` já era `IOptions`-bound (portanto já tecnicamente configurável sem recompilar) mas nunca aparecia em nenhum `appsettings.json` — só existia como default C#, invisível a quem não lê o código-fonte. Corrigido: `AIAgent:Anthropic:Pricing:*` agora explícito em `IHostPro.Worker/appsettings.json` (valores idênticos aos defaults atuais — `PricingOperationalConfigurationImproved=true`, sem mudança de comportamento). Coberto por teste já existente (`AnthropicModelProviderTests.GenerateAsync_computes_EstimatedCostUsd_from_real_usage_and_configured_pricing`) mais a garantia estrutural do próprio domínio: `AgentInteraction.EstimatedCostUsd`/`CostPricingReference` são gravados uma única vez na criação e nunca recalculados — uma interação histórica é estruturalmente imune a uma mudança de configuração posterior.

### 5.8 `WolverineClusterAgentAssignmentDebt` — auditoria aprofundada (correção estrutural NÃO autorizada)

Investigação com evidência direta: origem em Fase 9 CP2.3.4, reafirmada Fase 10/11, manifestação observada exclusivamente durante SHUTDOWN do Worker (nunca em steady-state), relacionada ao "durability agent" do Wolverine para o schema `dashboard_messaging`. Achado adicional confirmado pela auditoria (não estava em nenhum documento anterior): `IHostPro.Api` e `IHostPro.Worker` compartilham o MESMO Main store (`platform_messaging`) e 5 schemas ancilares em comum — ou seja, todo teste E2E já roda, sem saber, um cluster Wolverine de 2 nós (Api+Worker) contra stores compartilhados; o ruído de rebalanceamento é plausivelmente a reatribuição de ownership quando o Worker sai. Confirmado por busca direta de código: nenhum teste no repositório jamais rodou 2+ instâncias de Worker concorrentes — a única topologia já exercitada é (1 Api + 1 Worker).

**Classificação**: `NON_BLOCKING_SINGLE_NODE — UNVERIFIED_FOR_HORIZONTAL_SCALE_OUT`. Não é `BLOCKS_PRODUCTION` (a topologia de produção/piloto é single-instance e ainda indefinida quanto a multi-node, adiada para CP5). Não é um "multi-node confirmado seguro" — a plataforma nunca testou o cenário real de escala horizontal (2+ réplicas do Worker), então essa lacuna permanece genuinamente desconhecida, não apenas teórica. `WolverineProductionImpactClassified=true`. **Nenhuma correção estrutural foi implementada** (não autorizado pelo mandato) — recomendação registrada para uma futura Decision Gate dedicada: um teste real com 2 instâncias concorrentes de Worker antes de certificar escala horizontal como segura.

### 5.9 Testes — evidência

| Suíte | Resultado |
|---|---|
| `IHostPro.Contexts.Configuration.Tests.Integration` (novo `DistributedRateLimiterTests`, Redis real via Testcontainers) | 6/6 — abaixo do limite, acima do limite com Retry-After, fairness multi-tenant (`MultiTenantFairnessProven=true`), isolamento entre políticas, FailOpen/FailClosed sob Redis real derrubado, política não configurada = ilimitada |
| `IHostPro.Api.Tests.Integration` (novo `AuthenticationRateLimitWorkflowRoundTripTests`) | 1/1 — 429 real através do pipeline HTTP completo após 30 chamadas, `Retry-After` presente |
| `IHostPro.Contexts.AIAgent.Tests.Unit` (novo `AgentContextBuilderTests` + 1 novo teste em `ConversationMessageReceivedProcessorTests`) | 202/202 (todo o projeto) — budget de contexto (7 cenários) + guard de custo de IA tratado como falha técnica |
| `IHostPro.ArchitectureTests` | 304/304 — nenhuma fronteira arquitetural violada pelas novas interfaces `IWebhookRateLimiter`/`IAiAgentRateLimiter`/`IContextBudgetPolicy` |
| `IHostPro.Contexts.ExternalIntegrations.Tests.Unit` | 131/131 |

**Limitação de escopo assumida, por tempo**: a partição real por IP (`Authentication`: IP A bloqueado, IP B não afetado) e o comportamento de Redis-down atravessando o pipeline HTTP real (`Webhook`) não foram re-provados em um teste E2E dedicado — `WebApplicationFactory`'s `TestServer` reporta o mesmo `RemoteIpAddress` sintético para toda chamada simulada, tornando esse cenário específico não simulável nesse nível; a correção de partição/fairness já está provada genericamente no nível `IDistributedRateLimiter` (`DistributedRateLimiterTests`), e a fiação HTTP (`DistributedRateLimiterAdapter`, extração de partição) foi verificada por revisão de código + compilação + o teste de threshold real do item acima.

### 5.10 Escopo explicitamente não implementado (por decisão do gate, não por omissão)

- Circuit breaker HTTP adicional para Anthropic/WhatsApp — `CircuitBreakerAdditionalImplementation=BLOCKED_PENDING_DECISION_GATE` (§5.5), aguardando resposta específica do usuário.
- Ferramenta de replay de DLQ — `DlqReplayAdministrativeCapability=DEFERRED`, permitido explicitamente dado que a observabilidade mínima existe.
- Correção estrutural do `WolverineClusterAgentAssignmentDebt` — auditado e classificado (§5.8), correção seria uma Decision Gate própria.
- Billing/planos/assinaturas/entitlements comerciais — pertence à futura auditoria de SaaS Commercial Readiness, nunca a este checkpoint.
- Thresholds numéricos finais de Produção para rate limiting e context budget — `ProductionRateLimitThresholdsRequired=true`/`ProductionContextBudgetFinalThresholdRequired=true`, dependem de dados reais do piloto.
- Manifestos Kubernetes/probes reais — decisão já registrada no CP2, mantida.
