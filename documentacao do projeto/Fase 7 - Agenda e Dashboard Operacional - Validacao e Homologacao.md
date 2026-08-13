# Fase 7 — Agenda e Dashboard Operacional — Validação e Homologação

Versão: 1.0 (Incremento 1 — Agenda Foundation, Checkpoint 1 e Checkpoint 1 CLOSURE — documento vivo, criado neste checkpoint para registrar a investigação de causa raiz e a correção arquitetural ADR-016; checkpoints anteriores registrados em §2-§3 a partir do histórico real de commits/código)

Status: **Incremento 1 (Agenda Foundation) em andamento** — Checkpoint 0 e Checkpoint 1 concluídos; Checkpoint 1 CLOSURE (correção do defeito de identidade de tenant, ADR-016) concluído e homologado nesta seção. Dashboard Operacional permanece fora de escopo — nenhum trabalho iniciado.

---

## 1. Objetivo

Este documento registra a implementação e homologação real do Incremento 1 da Fase 7 (Agenda e Dashboard Operacional, per `Plano Executivo de Desenvolvimento por Fases.md`, §Fase 7), começando pela Agenda Foundation: uma projeção local, somente leitura, de `Cleaning` (Housekeeping) dentro de Reservations, mantida em sincronia por Integration Events reais, mais a API `GET /api/v1/schedule`.

## 2. Escopo do Incremento 1 — Agenda Foundation

**Incluído**: `CleaningScheduleProjectionEntry` (projeção local em Reservations, schema `reservations`); consumo real dos dez eventos de ciclo de vida de `Cleaning` publicados por Housekeeping (`CleaningCreated`, `CleaningAssigned`, `CleaningInTransit`, `CleaningStarted`, `CleaningInspectionStarted`, `CleaningNeedsHelp`, `CleaningNeedsMaterial`, `CleaningInterrupted`, `CleaningCompleted`, `CleaningCancelled`); `GET /api/v1/schedule`; correção de rotas RabbitMQ pré-existentes que nunca publicavam `CleaningNeedsHelp`/`CleaningNeedsMaterial`; dois novos eventos (`CleaningInTransit`, `CleaningInterrupted`) para transições que antes não publicavam nada.

**Excluído** (fora deste incremento): frontend Agenda (FullCalendar); Dashboard Operacional (indicadores/métricas); qualquer funcionalidade das Fases 8 em diante.

## 3. Checkpoint 0 e Checkpoint 1 — resumo

Checkpoint 0 (gates e contratos existentes) e Checkpoint 1 (projeção, consumo Wolverine inicial, API `GET /api/v1/schedule`, testes de integração) foram concluídos e o primeiro gate real de transporte (`CleaningCreated` → Worker real → projeção) foi comprovado verde antes desta seção. O detalhamento cronológico completo desses checkpoints está registrado no histórico de tasks da sessão de implementação; este documento foca no Checkpoint 1 CLOSURE (abaixo), cuja investigação e correção são o objeto desta homologação.

## 4. Checkpoint 1 CLOSURE — status-coverage gap e investigação de causa raiz

### 4.1 Motivação

Uma revisão de cobertura identificou uma lacuna real: nem todo `Cleaning.Status` real tinha um evento correspondente (`CleaningNeedsHelp`/`CleaningNeedsMaterial` existiam desde a Fase 6 Incremento 2A mas nunca eram roteados por `IHostPro.Api`; `CleaningInTransit`/`CleaningInterrupted` não existiam). Corrigido: os quatro eventos foram generalizados no synchronizer/handlers Wolverine, o `MigrationRunner` passou a provisionar os bindings correspondentes, e um teste de paridade de roteamento real (`CleaningLifecycleRoutingFixParityTests`) comprovou os quatro eventos chegando à exchange `housekeeping-events` com as routing keys documentadas.

O fechamento exigia um segundo gate real de transporte — não apenas a criação original (`CleaningCreated`), mas uma atualização de status pós-criação genuína (`CleaningNeedsHelp` → `WaitingHelp`).

### 4.2 Defeito descoberto: CleaningAssigned nunca atualizava a projeção

O segundo gate real (`CleaningNeedsHelpScheduleProjectionWorkerRoundTripTests`: Create → Assign → Start → NeedsHelp, via RabbitMQ real, Worker real, Postgres real) falhou: `CleaningAssigned` nunca fazia a projeção avançar de `Pending` para `Assigned`, mesmo com o evento comprovadamente publicado e entregue (confirmado por uma probe RabbitMQ direta).

### 4.3 Investigação — hipóteses descartadas com evidência real

A investigação seguiu um protocolo de evidência obrigatória em múltiplas rodadas, rejeitando explicitamente qualquer correção antes de causalidade comprovada:

1. **"Commit-visibility race" (hipótese inicial)** — descartada. Uma política de retry nativa do Wolverine (`RetryWithCooldown`, experimento temporário e revertido) rodou 7 tentativas reais ao longo de ~7,6s sem nunca encontrar a linha — incompatível com uma race de visibilidade breve, que se resolveria em milissegundos. Instrumentação temporária confirmou também, empiricamente, que o commit da transação de `CleaningCreated` é genuinamente síncrono (`DbContext.Database.CurrentTransaction` nulo logo após `SaveChangesAndFlushMessagesAsync`).
2. **`OverrideStorage`/composição de storage Wolverine** — descartada. Decompilação real de `Wolverine.EntityFrameworkCore.dll`/`Wolverine.dll` 6.22.0 (via `ilspycmd`) confirmou que `MessageContext.OverrideStorage(store)` apenas reatribui `Storage` (não afeta transação/commit), e que `DbContextOutbox<T>.SaveChangesAndFlushMessagesAsync()` de fato comita a transação de forma síncrona antes de fazer flush das mensagens.
3. **Cadeia gerada divergente entre `CleaningCreatedHandler`/`CleaningAssignedHandler`** — descartada. A cadeia gerada real (capturada via reflection sobre `HandlerChain.SourceCode` durante uma execução real do Worker) é estruturalmente **idêntica**, byte a byte, para os dois handlers.

### 4.4 Causa raiz comprovada

Um harness de reprodução rápida in-process (`IMessageBus.InvokeAsync` contra a cadeia Wolverine real, sem RabbitMQ — mesma cadeia gerada confirmada em 4.3.3, ~8s por execução em vez de ~2min do round trip completo) reproduziu o defeito deterministicamente e capturou o SQL real:

```
-- CleaningCreated (AnyAsync de idempotência):
SELECT FALSE

-- CleaningAssigned (FirstOrDefaultAsync do UpdateAsync):
SELECT c.tenant_id, c.cleaning_id, ...
FROM reservations.cleaning_schedule_projection AS c
WHERE FALSE
```

A causa: dentro de cada invocação da cadeia gerada, existem **duas instâncias independentes de `ITenantContext`** — (A) a resolvida via DI dentro do `IServiceScope` que a própria cadeia cria (`_serviceScopeFactory.CreateAsyncScope()`), injetada no construtor de `ReservationsDbContext` e fechada pelo Global Query Filter tenant-aware (`BaseDbContext.BuildTenantFilter`), **nunca populada** (permanece `TenantId = null`, produzindo `WHERE FALSE` por design fail-closed); (B) um `new TenantContext()` manual, declarado separadamente pelo código gerado, populado por `TenantResolutionMiddleware.Before(...)` e usado **apenas** para o `SET LOCAL app.tenant_id` do RLS via `ReservationsOutboxTransactionExecutor`/`TenantAwareTransactionScope` — por isso o RLS sempre esteve correto, mascarando o problema até uma leitura filtrada (`FirstOrDefaultAsync`) expor a instância A nunca resolvida.

`CleaningCreated` mascarava o defeito porque `Add()`/`INSERT` não é afetado por Global Query Filters — mas seu próprio guard de idempotência (`AnyAsync`) sofria exatamente do mesmo `WHERE FALSE`, uma consequência colateral real (redelivery real teria lançado `DbUpdateException`, não o no-op idempotente documentado).

Esta é **a mesma classe mecânica de defeito que o ADR-015 já havia documentado e corrigido para Housekeeping** (Fase 6) — confirmado, não apenas por semelhança de sintoma, mas pela leitura direta do código gerado e do comentário do próprio `IHousekeepingMessageExecutionScope`, que descreve exatamente este mecanismo.

### 4.5 Correção — ADR-016

Ver `documentacao do projeto/ADRs/ADR-016 - Tenant-safe Execution Boundary for Persistent Wolverine Consumers.md` para a decisão completa. Resumo: `IReservationsMessageExecutionScope`/`ReservationsMessageExecutionScope`, mesmo desenho do boundary do ADR-015 — child DI scope por mensagem, `ITenantContext` resolvido desse scope e populado **antes** de resolver o processor de negócio, garantindo que `ReservationsDbContext`/o transaction executor observem a mesma instância. Os dez adapters Wolverine de Reservations foram migrados para depender exclusivamente de `IReservationsMessageExecutionScope`.

### 4.6 Prova de causalidade — spike isolado

O primeiro handler migrado, isoladamente, foi `CleaningAssignedHandler` (spike). Toggle real, observado via SQL:

- **Antes** (design original): `WHERE FALSE` — linha nunca encontrada.
- **Depois** (apenas `CleaningAssignedHandler` migrado): `WHERE c.tenant_id = @ef_filter__TenantId AND c.tenant_id = @tenantId AND c.cleaning_id = @cleaningId` — predicado real, tenant-aware; linha encontrada e atualizada (`status`, `assigned_housekeeper_user_id`) corretamente.
- **Controle positivo**: `CleaningCreated`, ainda não migrado nesse momento, continuou exibindo `SELECT FALSE` em seu próprio guard de idempotência — confirmando que o defeito é do mecanismo, não de `CleaningAssigned` especificamente.

Satisfaz os cinco pontos do critério de causalidade: defeito reproduzível (harness rápido, determinístico); mudança isolada remove o defeito (migração de um único handler); mecanismo interno observado explica o porquê (duas instâncias de `ITenantContext`, código-fonte + SQL); `CleaningCreated` como controle positivo consistente em todas as etapas.

### 4.7 Generalização e gates finais

Após o spike verde, os nove handlers restantes foram migrados para o mesmo boundary. Resultados:

- **`ReservationsMessageExecutionScopePipelineTests`** (3 testes, dispatch real via `IMessageBus.InvokeAsync`, sem RabbitMQ): `CleaningAssigned` atualiza a projeção; isolamento cross-tenant (`CleaningAssigned` de um tenant nunca afeta a linha de outro tenant com o mesmo `CleaningId`); `CleaningCreated` redelivered é um no-op real (sem exceção, sem segunda linha).
- **`CleaningScheduleProjectionSynchronizerTests`** (15 testes, construção direta via DI, sem passar pela cadeia Wolverine): matriz completa de status, idempotência, ordem fora de sequência, RLS fail-closed — sem regressão após a migração.
- **`ReservationsMessageExecutionScopeArchitectureTests`** (2 testes, NetArchTest): exatamente uma classe (`ReservationsMessageExecutionScope`) depende de `IServiceScopeFactory` em todo Reservations; os dez adapters nunca dependem de `ReservationsDbContext`/`IReservationsTransactionExecutor`/`CleaningScheduleProjectionSynchronizer`/`IServiceScopeFactory` diretamente.
- **`CleaningNeedsHelpScheduleProjectionWorkerRoundTripTests`** (gate real completo — RabbitMQ real, Worker real subprocess, Postgres real): Create → Assign → Start → NeedsHelp, todos os quatro eventos atualizando a MESMA linha da projeção; `PropertyId`/`ScheduledAtUtc` preservados; `AssignedHousekeeperUserId` preservado através da transição para `WaitingHelp`; exatamente uma linha por `Cleaning`; isolamento cross-tenant confirmado por leitura RLS-scoped sob outro tenant.
- **`CleaningLifecycleRoutingFixParityTests`**: paridade de roteamento real para os quatro eventos corrigidos/novos (`CleaningNeedsHelp`, `CleaningNeedsMaterial`, `CleaningInTransit`, `CleaningInterrupted`) — sem regressão.
- **Regressão Housekeeping** (`HousekeepingWolverineAdapterTests`, 6 testes) e **regressão Configuration & Policy** (`PolicyUpdatedRegressionTests`): sem regressão causada por esta correção. `PolicyUpdatedRegressionTests` mostrou-se intermitentemente flaky (geração do cache Redis avançando 2x em vez de 1x, ~2 de 3 execuções); isolado por experimento controlado (removendo/restaurando apenas a linha `AlwaysUseServiceLocationFor<IReservationsMessageExecutionScope>()` do Worker) — a falha reproduz-se identicamente com ou sem essa linha, confirmando que **não é uma regressão desta correção**, e sim o risco de redelivery real já documentado e aceito pelo próprio ADR-015 (`EndpointMode.Inline`, sem inbox durável). Não corrigido nesta etapa — fora do escopo desta decisão.

## 5. Referências

- ADR-016 (Tenant-safe Execution Boundary for Persistent Wolverine Consumers).
- ADR-015 (Isolamento do Processamento de Mensagens Housekeeping da Integração EF Core do Wolverine) — a descoberta original, para Housekeeping.
- Documento 07 (Catálogo de Eventos de Domínio) — payload real dos dez eventos de ciclo de vida de `Cleaning`, incluindo os quatro corrigidos/novos nesta fase.
