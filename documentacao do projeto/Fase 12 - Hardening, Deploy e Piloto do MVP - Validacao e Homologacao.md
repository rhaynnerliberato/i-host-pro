# Fase 12 — Hardening, Deploy e Piloto do MVP — Validação e Homologação

Versão: 1.0
Status: Em andamento — Checkpoint 1 concluído

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
