# Fase 10 — Check-in, Checkout e Operações do Hóspede — Validação e Homologação

Versão: 1.1
Status: Em andamento — Checkpoint 1 e Checkpoint 2 concluídos

## 1. Objetivo

Registrar a validação e homologação da Fase 10 (Check-in, Checkout e Operações do Hóspede), conforme `Plano Executivo de Desenvolvimento por Fases.md` e a estrutura de Checkpoints CP0–CP6 adotada oficialmente pelo usuário. Este documento é criado agora, no fechamento do Checkpoint 1 — o Checkpoint 0 (design-only) não produziu documentação própria, apenas o relatório de decisão entregue e aprovado em conversa; suas decisões relevantes são referenciadas aqui e formalizadas em `ADR-024`.

## 2. Checkpoint 0 — Architecture & Product Decision Gate (Read-Only)

**Status:** Concluído e aprovado. Nenhum arquivo alterado.

Decisões de arquitetura aprovadas neste gate, todas confirmadas e implementadas no Checkpoint 1:

- Novo Bounded Context **Guest Operations**, dono exclusivo do ciclo de vida operacional do hóspede — distinto do ciclo de vida de reserva (`Reservation`).
- Terceiro estado de `Reservation.Status`: `Closed`, alcançável apenas de `Confirmed`, representando o checkout real do hóspede.
- O encerramento da Reservation nunca é chamado diretamente por Guest Operations — obrigatoriamente via um novo comando cross-context (`CloseReservation`), enviado por um novo orquestrador de Workflow, mesma arquitetura já estabelecida pela ADR-018.
- Early Check-in/Late Checkout permanecem entidades separadas (não um campo de `GuestStayOperation`), Portaria/PIX/Payment permanecem inteiramente fora de escopo, e as duas novas exceções síncronas necessárias no futuro (Guest Operations → Reservations para calendário; Guest Operations → Housekeeping para prontidão de limpeza) ficam explicitamente deferidas ao Checkpoint 3.

## 3. Checkpoint 1 — Guest Operations Foundation

### 3.1 Escopo aprovado

Fundação determinística mínima: o novo Bounded Context Guest Operations (agregado `GuestStayOperation`, sem comportamento de check-in real), o terceiro estado `ReservationStatus.Closed`, o comando cross-context `CloseReservation`, o evento `GuestCheckedOut`, e o segundo orquestrador de Workflow (`GuestCheckedOutCloseReservationOrchestrator`) — provados deterministicamente por testes unitários e por um E2E real via RabbitMQ + Worker + Postgres. Zero endpoints HTTP públicos. Nenhum formulário de check-in, credencial de acesso, Early/Late, Portaria, PIX/Payment, ou leitor síncrono novo.

### 3.2 Decisão bloqueante resolvida — semântica de `CloseReservation`

O único ponto de ambiguidade genuína deste checkpoint: o que fazer quando `CloseReservation` chega para uma Reservation já `Cancelled`. Dois precedentes reais e conflitantes existiam no codebase (permanent no-op do Airbnb vs. exceção do fluxo de Housekeeping) — o usuário foi consultado explicitamente (`AskUserQuestion`) e escolheu a rejeição de domínio com exceção específica. Decisão completa, motivo e as três semânticas de transição (`Confirmed`→`Closed`, `Closed`→no-op, `Cancelled`→exceção) estão registrados em `ADR-024`.

### 3.3 Estrutura implementada

- **`GuestOperations.Domain`**: `GuestStayOperation` (agregado, tenant-owned), `GuestStayOperationStatus` (`Active`/`CheckedOut`).
- **`GuestOperations.Contracts`**: `GuestCheckedOut` (Integration Event, payload mínimo — apenas `ReservationId`).
- **`GuestOperations.Application`**: `RecordGuestCheckedOutCommand`/`IRecordGuestCheckedOutHandler`/`RecordGuestCheckedOutCommandHandler` (resolvido diretamente, sem Mediator/HTTP — nenhum endpoint existe), `IGuestStayOperationReader`, `IGuestOperationsTransactionExecutor`, `IIntegrationEventCollector` (cópia própria, mesma convenção de todo Bounded Context).
- **`GuestOperations.Infrastructure`**: `GuestOperationsDbContext` (schema `guest_operations`), `GuestStayOperationConfiguration` (RLS, índice único `(tenant_id, reservation_id)`), `GuestStayOperationRepository`/`GuestStayOperationReader`/`GuestOperationsOutboxTransactionExecutor`, `GuestOperationsModuleExtensions`. **Sem** projeto `.Api` (ver `ADR-024` Seção 1).
- **Reservations**: `ReservationStatus.Closed`, `Reservation.Close(now)`, `ReservationStatusCodeMapper.ToCode` atualizado, `ReservationCancelledCannotBeClosedException`, `CloseReservation`/`ReservationClosed` (Contracts), `ICloseReservationHandler`/`CloseReservationCommandHandler`, extensão de `IReservationsMessageExecutionScope` (`ExecuteCloseReservationAsync`), adapter Wolverine `CloseReservationHandler`, `AddReservationsCloseReservationCommand` (novo método de composição, registrado em `IHostPro.Api` e `IHostPro.Worker`).
- **Workflow**: `IWorkflowCommandDispatcher.DispatchCloseReservationAsync` (nova assinatura), `GuestCheckedOutCloseReservationOrchestrator` (Workflow.Application, segundo caso de uso), `WolverineWorkflowCommandDispatcher` estendido, adapter `GuestCheckedOutHandler` (Workflow.Infrastructure.Messaging).
- **Identity**: migração `AddGuestOperationsPermissions` — seed de `GUEST_OPERATIONS:MANAGE`/`GUEST_OPERATIONS:READ` (Permission + RolePermission ADMIN), deliberadamente **sem** promoção a `IdentityPermissionCodes` e **sem** `AddPolicy` (ver `ADR-024` Seção 7 e Seção 6 abaixo).
- **Topologia RabbitMQ** (provisionada exclusivamente por `IHostPro.MigrationRunner`): nova exchange `guest-operations-events` (Topic) → fila `workflow.guest-checked-out-trigger`; nova fila `reservations.workflow-commands`, vinculada à exchange já existente `workflow-orchestration-commands` (routing key `close_reservation`).
- **Migração EF**: `GuestOperationsDbContext` InitialCreate (schema `guest_operations`, tabela `guest_stay_operations`, RLS `FORCE`, grants mínimos).

### 3.4 Permissões — seed sem wiring

`IdentityAuthorizationExtensions`'s própria regra documentada ("apenas políticas realmente consumidas por um endpoint existente são registradas") foi identificada como um conflito real com o pedido original de registrar `AddPolicy` já neste checkpoint — resolvido explicitamente com o usuário antes de qualquer implementação (`AskUserQuestion`). Decisão: seed apenas, mirroring o precedente já existente de `SETTINGS:MANAGE`/`SETTINGS:READ`. Verificado por SQL direto contra um Postgres descartável: `identity.permissions`/`identity.role_permissions` contêm exatamente as linhas esperadas, ADMIN grantado para ambos os códigos.

### 3.5 MigrationRunner — Run #1 e Run #2 executados de verdade

Mirroring o precedente corretivo da Fase 9, Checkpoint 3.2.1 — nunca substituído pela garantia inerente do EF Core. Executado contra um Postgres descartável real (papéis `ihostpro_migrator`/`ihostpro_app` criados manualmente):

- **Run #1**: todos os 9 DbContexts migrados (incluindo `GuestOperationsDbContext`, pela primeira vez), 4 bootstrap steps de projeção executados, outbox Wolverine de 8 Bounded Contexts (incluindo `guest_operations_messaging`, pela primeira vez) provisionado, topologia RabbitMQ completa declarada (incluindo `guest-operations-events` e a nova fila `reservations.workflow-commands`). Exit code 0.
- **Run #2**: reexecutado contra o MESMO banco já migrado. Exit code 0, zero erro, zero drift.
- Verificado por SQL direto: `guest_operations.__EFMigrationsHistory` contém exatamente 1 linha (`InitialCreate`); `identity.__EFMigrationsHistory` contém exatamente 1 linha para `AddGuestOperationsPermissions`; RLS `ENABLE`+`FORCE` confirmados em `guest_operations.guest_stay_operations`; os 3 índices esperados existem; grants de `ihostpro_app` restritos a `SELECT`/`INSERT`/`UPDATE` (sem `DELETE`, mirroring o precedente de `whatsapp_integrations` — nenhuma capacidade de exclusão existe neste checkpoint).

### 3.6 Prova real de transporte (RabbitMQ + Worker + Postgres reais)

`GuestCheckedOutCloseReservationWorkerRoundTripTests` (`tests/Host/IHostPro.Api.Tests.Integration`) — nunca chama nenhum handler diretamente para o fluxo principal: semeia um Tenant + Property real + Reservation `Confirmed` real (via `IReservationsRequestDispatcher`) + `GuestStayOperation` `Active` (seed direto via EF, mirroring como CP3.2 semeou `AirbnbListingMapping`); invoca `IRecordGuestCheckedOutHandler` diretamente (único ponto de entrada, sem HTTP); confirma a cadeia completa — outbox de Guest Operations → RabbitMQ real → `IHostPro.Worker.dll` real e não-modificado → `GuestCheckedOutCloseReservationOrchestrator` → `CloseReservation` → RabbitMQ real → `CloseReservationCommandHandler` → `Reservation.Closed` real no Postgres. Também prova: isolamento entre tenants (a mesma `ReservationId`, sob RLS de outro tenant, nunca resolve) e idempotência sobre transporte real (uma segunda entrega de `CloseReservation`, invocada diretamente após o fechamento, nunca lança e nunca altera o status).

### 3.7 Testes — contagens exatas

| Suíte | Resultado |
|---|---|
| `IHostPro.Contexts.GuestOperations.Tests.Unit` (novo) | 8 aprovados |
| `IHostPro.Contexts.Reservations.Tests.Unit` | 90 aprovados |
| `IHostPro.Contexts.Workflow.Tests.Unit` | 11 aprovados |
| `IHostPro.ArchitectureTests` | 233 aprovados |
| `IHostPro.Contexts.Identity.Tests.Unit` | 470 aprovados |
| `IHostPro.Contexts.Identity.Tests.Integration` | 420 aprovados |
| `IHostPro.Contexts.Reservations.Tests.Integration` | 86 aprovados |
| `IHostPro.Api.Tests.Integration` (suíte completa, uma única execução) | 36 aprovados |
| Build Release (solução completa) | 0 erro |
| Build Debug (solução completa) | 0 erro |

### 3.8 Defeitos reais encontrados e corrigidos durante o fechamento deste Checkpoint

**Defeito 1 — contagens fixas do catálogo de permissões.** `IdentityRowLevelSecurityTests.Migration_applies_cleanly_and_seeds_the_platform_catalog` fixava `Permissions.CountAsync() == 33`/`RolePermissions.CountAsync() == 42`. As duas novas linhas de `GUEST_OPERATIONS:MANAGE`/`GUEST_OPERATIONS:READ` (mais seus dois grants ADMIN) quebravam essa asserção — corrigido para 35/42, mesmo padrão de atualização já aplicado quando `INTEGRATIONS:MANAGE` foi adicionado (Fase 9).

**Defeito 2 — `IHostPro.MigrationRunner` sem `ConnectionStrings:GuestOperations` em todo teste E2E pré-existente.** Ao adicionar `GuestOperationsDbContext` à lista de módulos migrados por `IHostPro.MigrationRunner`, todo teste E2E pré-existente que já invocava esse executável como subprocesso (22 arquivos em `tests/Host/IHostPro.Api.Tests.Integration`) passou a falhar deterministicamente: sem a variável de ambiente `ConnectionStrings__GuestOperations`, o subprocesso caía no valor padrão de `appsettings.json` — o banco de desenvolvimento real (`Host=localhost;Port=5432;Database=ihostpro`) — e falhava com `permission denied for database` ao tentar criar o schema `guest_operations` ali. Corrigido adicionando a mesma variável (apontando para o mesmo Postgres descartável já usado por cada teste) a todos os 22 arquivos — mecânica idêntica à que toda introdução anterior de um novo Bounded Context já exigiu (ex.: `ConnectionStrings__ExternalIntegrations` teve de ser adicionada aos mesmos arquivos quando External Integrations foi criado, Fase 9). Confirmado corrigido rodando a suíte completa (`IHostPro.Api.Tests.Integration`, 36 testes) em uma única execução — nenhuma falha.

Nenhum dos dois defeitos está relacionado à lógica de negócio deste Checkpoint (fechamento de Reserva, orquestração de Workflow, agregado Guest Operations) — ambos são efeitos colaterais mecânicos, já esperados, de introduzir um novo Bounded Context neste codebase.

### 3.9 Regressão final

Todas as suítes relevantes executadas com sucesso, incluindo a suíte completa de `IHostPro.Api.Tests.Integration` (36/36) em uma única invocação — nenhuma falha remanescente. `git diff --check` sem erros de espaço em branco (apenas avisos benignos de normalização de fim de linha LF→CRLF).

### 3.10 Escopo explicitamente NÃO implementado neste checkpoint

Formulário de check-in, credencial de acesso, Early Check-in, Late Checkout, Portaria, PIX/Payment, qualquer endpoint HTTP público, leitores síncronos novos (`IReservationScheduleReader`/`ICleaningReadinessReader` — deferidos ao Checkpoint 3), granularidade completa de `CheckInStatus`, promoção de `GUEST_OPERATIONS:MANAGE`/`READ` a `IdentityPermissionCodes` e seu `AddPolicy`.

### 3.11 Status do Checkpoint 1

**Concluído e homologado.** Regressão final (§3.9) sem pendências.

## 4. Checkpoint 2 — Check-in/Checkout Core

### 4.1 Escopo aprovado

Primeiro comportamento de check-in real e os dois primeiros endpoints HTTP públicos deste Bounded Context: `GuestStayOperationStatus.CheckedIn` (novo estado intermediário `Active → CheckedIn → CheckedOut`), `RecordGuestCheckedInCommand`/`RecordGuestCheckedOutCommand` (despachados via Mediator, mesma convenção universal do codebase), o evento `GuestCheckedIn`, o novo projeto `GuestOperations.Api` (`GuestStayOperationsController`, dois endpoints: check-in e checkout), o gatilho de criação resolvido do `GuestStayOperation` (coreografia reagindo a `ReservationCreated`), e a estratégia de upgrade para Reservations preexistentes. Decisões completas registradas no amendment do `ADR-024` (2026-08-27).

### 4.2 Correção de governança — numeração de ADR

Durante a implementação, um ADR foi criado incorretamente com o número `ADR-025` (reservado desde o Checkpoint 0 exclusivamente para o Boundary de PIX Payment, Checkpoint 5). Corrigido antes de qualquer commit: o arquivo incorreto foi removido, e as decisões do Checkpoint 2 foram incorporadas como amendment ao `ADR-024` existente (nunca um novo número). `ADR-025` permanece reservada e vazia para o Checkpoint 5.

### 4.3 Defeitos reais encontrados e corrigidos durante a implementação

**Defeito 1 — despacho HTTP divergente da convenção do codebase.** O desenho inicial resolvia `RecordGuestCheckedInCommand`/`RecordGuestCheckedOutCommand` por interfaces de handler customizadas, herdadas do Checkpoint 1 (correto então, sem HTTP). Uma auditoria de todo Bounded Context com endpoint HTTP real (Reservations/Housekeeping/Dashboard/Configuration/PropertyManagement/Identity/ExternalIntegrations) confirmou que 100% deles despacham via Mediator (`ICommand`/`ICommandHandler`/`I<Contexto>RequestDispatcher`). Corrigido antes de qualquer commit — ver `ADR-024` amendment §A5.

**Defeito 2 — `IHostPro.Worker` sem `IIntegrationEventCollector` registrado para o novo consumidor.** `AddGuestOperationsReservationCreatedConsumer` registrava `IGuestOperationsTransactionExecutor` (que depende de `IIntegrationEventCollector`) mas não o próprio `IIntegrationEventCollector` — a validação de DI do ASP.NET Core falhava ao construir o Worker, derrubando TODO o processo na inicialização (não apenas o consumidor de Guest Operations) e fazendo 25 dos 36 testes da suíte `IHostPro.Api.Tests.Integration` falharem por "Worker never reported listening to X", em Bounded Contexts totalmente não relacionados (Housekeeping, Communication, WhatsApp). Corrigido adicionando o registro faltante.

**Defeito 3 — reuso indevido de escopo de DI no teste E2E reescrito.** O teste `GuestCheckedOutCloseReservationWorkerRoundTripTests` originalmente despachava `RecordGuestCheckedInCommand` e `RecordGuestCheckedOutCommand` no MESMO escopo de DI — um atalho artificial que nenhum cliente HTTP real jamais produziria (cada requisição HTTP real recebe seu próprio escopo novo do ASP.NET Core). Isso reutilizava a mesma instância Scoped de `IDbContextOutbox<GuestOperationsDbContext>` entre dois flushes sequenciais, e `GuestCheckedOut` nunca chegava de fato ao broker (a cadeia parava silenciosamente antes de `Workflow02_GuestCheckedOut`). Corrigido separando check-in e checkout em dois escopos de DI distintos — mesmo padrão que uma segunda chamada HTTP real teria.

Nenhum dos três defeitos está relacionado à lógica de negócio deste Checkpoint (check-in, checkout, coreografia de criação) — todos são efeitos colaterais mecânicos da introdução do despacho via Mediator e do primeiro consumidor real em `IHostPro.Worker` deste Bounded Context.

### 4.4 Gatilho de criação e fan-out de `ReservationCreated`

`ReservationCreatedGuestStayInitializer` (coreografia, nunca Workflow Orchestration) confirmado funcionando de ponta a ponta via transporte real: `ReservationCreated` publicado por `CreateReservationCommand` → RabbitMQ real → fila `guestoperations.reservation-created-trigger` → `GuestStayOperation` `Active` criado — log real observado: `"GuestStayOperation created for tenant ... reservationId ..."`.

`ReservationCreatedConsumerCount=5` (Housekeeping, Dashboard, Workflow, Communication, Guest Operations) — cada um sticky-bound à sua própria fila (ADR-020), sem competing-consumer behavior. Prova estrutural (não apenas efeito colateral em banco) via `WolverineHandlerChainIsolationBaselineTests`, estendido para incluir Guest Operations como quarto consumidor estruturalmente verificado no host mínimo (Housekeeping/Dashboard/Workflow/Guest Operations — Communication permanece fora deste host mínimo por ter grafo de dependências maior, mas seu próprio isolamento já está provado de verdade pela suíte completa de `IHostPro.Api.Tests.Integration`, que mostra as cinco filas recebendo e processando `ReservationCreated` de forma independente, sem nenhum handler de outro Bounded Context executando por engano). Os quatro consumidores pré-existentes continuam recebendo o evento normalmente — nenhuma regressão.

### 4.5 Existing Reservation Upgrade Strategy — auditoria e backfill

Auditoria do banco de desenvolvimento real (`ihostpro-postgres`/`ihostpro`), executada antes de qualquer decisão de backfill (condição explícita de parada do usuário): `ExistingReservationsCount=2` (`ConfirmedReservationsCount=1`, `CancelledReservationsCount=1`, mesmo tenant/property), `ExistingGuestStayOperationsCount=0` (schema `guest_operations` ainda não existia nesse banco — `MigrationRunner` nunca havia sido executado lá), logo `MissingGuestStayOperationsCount=2`.

Decisão do usuário: backfill versionado, nunca um script manual fora do versionamento. `GuestStayOperationBackfillBootstrapStep` (ADR-017, `tools/IHostPro.MigrationRunner`) — mirroring exatamente `DashboardReservationProjectionBootstrapStep`'s própria mecânica (iteração por tenant, `set_config('app.tenant_id', ...)`, nunca `BYPASSRLS`/superuser): para cada Reservation `Confirmed` sem `GuestStayOperation` correspondente, cria uma `Active`; para `Cancelled`, não cria nenhuma (terminal, nunca pode fazer check-in); idempotente via `ON CONFLICT (tenant_id, reservation_id) DO NOTHING`; nenhum replay de `ReservationCreated`, nenhum side effect em outro Bounded Context.

Provado por 5 testes reais e determinísticos (`GuestStayOperationBackfillBootstrapStepTests`, novo projeto `IHostPro.Contexts.GuestOperations.Tests.Integration`): Confirmed sem operação → Active criada; Cancelled sem operação → nenhuma criada; Confirmed com operação já existente (CheckedIn) → no-op, nunca regride; múltiplos tenants → isolamento total via RLS; segunda execução → zero linhas novas.

**Aplicação real ao banco de desenvolvimento — autorizada e executada.** `IHostPro.MigrationRunner` (Release) executado de verdade contra `ihostpro-postgres`/`ihostpro`, usando a configuração oficial já commitada (`appsettings.json` do próprio MigrationRunner — nenhuma credencial de superusuário, nenhum `BYPASSRLS`, nenhuma alteração de RLS).

- **Run #1**: exit code 0. Todos os 9 DbContexts migrados (schema `guest_operations` criado pela primeira vez nesse banco). Backfill: `3 tenant(s) checked, 1 row(s) inserted` — exatamente a Reservation `Confirmed` pré-existente, agora com `GuestStayOperation` `Active` (`CheckedInAtUtc`/`CheckedOutAtUtc` nulos). A Reservation `Cancelled` permanece sem `GuestStayOperation`, como esperado.
- **Verificação pós-Run#1** (read-only): `ExistingReservationsCount=2`, `ConfirmedReservationsCount=1`, `CancelledReservationsCount=1`, `GuestStayOperationsCount=1`, `BackfilledGuestStayOperationsCount=1`, `ConfirmedReservationCovered=true`, `CancelledReservationSkipped=true`, `MissingEligibleGuestStayOperationsCount=0`.
- **Run #2** (mesmo banco, imediatamente em seguida): exit code 0. Backfill: `3 tenant(s) checked, 0 row(s) inserted` — zero drift, zero linha nova, idempotência real confirmada.
- **Saúde pós-Run#2** (read-only): `guest_operations.guest_stay_operations` com RLS `ENABLE`+`FORCE` intactos; os 3 índices esperados presentes (`PK` em `id`, alternate key única `(tenant_id, id)`, índice único `(tenant_id, reservation_id)`); grants de `ihostpro_app` restritos a `SELECT`/`INSERT`/`UPDATE` (sem `DELETE`); schema `guest_operations_messaging` (durable outbox) existente. `GuestStayOperationsCount` permanece `1`.

`DevDatabaseMigrationApplied=true`, `DevDatabaseRun1Exit=0`, `DevDatabaseRun2Exit=0`, `ConfirmedReservationBackfilled=true`, `CancelledReservationSkipped=true`, `MissingEligibleGuestStayOperationsCount=0`, `DevDatabaseGuestStayOperationsCount=1`. Nenhum replay de `ReservationCreated`; nenhum side effect em Housekeeping/Dashboard/Workflow/Communication (seus próprios bootstrap steps rodaram normalmente, `0 row(s) inserted` cada, já totalmente aplicados desde fases anteriores).

### 4.6 Autorização — primeiro consumidor real, bundle completo

`GuestOperationsManageConstantExists=true` (`IdentityPermissionCodes.GuestOperationsManage`), `GuestOperationsManageSeedExists=true` (já seedado desde o Checkpoint 1), `GuestOperationsManageGrantedToAdmin=true` (já concedido desde o Checkpoint 1), `GuestOperationsManagePolicyRegistered=true` (novo — `IdentityAuthorizationExtensions.AddIdentityAuthorization`). `GUEST_OPERATIONS:READ` permanece não promovido/não registrado — nenhum endpoint somente-leitura existe.

Pipeline HTTP real provado por `GuestStayOperationsControllerAuthorizationTests` (host real ASP.NET Core + JWT real emitido pelo stack real da Identity): ADMIN → 200 (check-in e checkout reais, transição de estado real observada); autenticado sem `GUEST_OPERATIONS:MANAGE` → 403 (nunca 500); anônimo → 401. Teste de consistência por reflexão (`IdentityAuthorizationCatalogConsistencyTests.Every_controller_required_policy_is_registered`) estendido para incluir o assembly de `GuestOperations.Api` — descoberta automática, sem necessidade de lista manual.

Também provado: duplicidade (check-in chamado duas vezes via HTTP real → ambas 200, sem duplicar `GuestCheckedIn`) e isolamento de tenant (check-in para uma Reservation de outro tenant → 404 real, nunca vaza existência).

### 4.7 Credencial de Acesso

Reafirmado, sem alteração: **DEFERRED PENDING SECURE DELIVERY BOUNDARY** — nenhuma implementação neste Checkpoint. Um sub-gate específico precisa ser aberto e resolvido antes da homologação final da Fase 10 como um todo. Não é um blocker externo/de produção nem uma lacuna esquecida.

### 4.8 Testes — contagens exatas (regressão final)

| Suíte | Resultado |
|---|---|
| `IHostPro.Contexts.GuestOperations.Tests.Unit` | 17 aprovados |
| `IHostPro.Contexts.GuestOperations.Tests.Integration` (novo) | 10 aprovados |
| `IHostPro.Contexts.Reservations.Tests.Unit` | 90 aprovados |
| `IHostPro.Contexts.Workflow.Tests.Unit` | 11 aprovados |
| `IHostPro.ArchitectureTests` | 234 aprovados |
| `IHostPro.Contexts.Identity.Tests.Unit` | 470 aprovados |
| `IHostPro.Contexts.Identity.Tests.Integration` | 420 aprovados |
| `IHostPro.Contexts.Reservations.Tests.Integration` | 86 aprovados |
| `IHostPro.Api.Tests.Integration` (suíte completa, uma única execução) | 36 aprovados |
| MigrationRunner Run #1 (Postgres descartável real) | Exit code 0, todos os 9 DbContexts + 6 bootstrap steps |
| MigrationRunner Run #2 (mesmo banco) | Exit code 0, zero drift, backfill 0 linhas em ambas |
| NSwag (geração #1 e #2) | Diff zero entre as duas gerações |
| Angular build (`ng build`) | Sucesso, nenhuma UI nova |
| Build Release (solução completa) | 0 erro |
| Build Debug (solução completa) | 0 erro |
| `git diff --check` | Sem erros (apenas avisos benignos de normalização LF→CRLF) |

### 4.9 Escopo explicitamente NÃO implementado neste checkpoint

Formulário de check-in, Credencial de Acesso (deferida — ver §4.7), Early Check-in, Late Checkout, Portaria/Front Desk, PIX/Payment, qualquer novo consumidor de `GuestCheckedIn` em Communication, granularidade completa de `CheckInStatus` além de `CheckedIn`.

### 4.10 Status do Checkpoint 2

**Concluído e homologado.** Regressão final (§4.8) e aplicação real do backfill ao banco de desenvolvimento (§4.5) sem pendências.

## 5. Próximo Checkpoint Recomendado

Checkpoint 3, conforme a estrutura CP0–CP6 já adotada — escopo a refinar e aprovar antes do início, seguindo o mesmo processo já aplicado aos dois Checkpoints anteriores.
