# Fase 10 — Check-in, Checkout e Operações do Hóspede — Validação e Homologação

Versão: 1.3
Status: Em andamento — Checkpoint 1, Checkpoint 2, Checkpoint 3 e Checkpoint 4 concluídos

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

## 5. Checkpoint 3 — Early Check-in / Late Checkout Core

### 5.1 Escopo aprovado

Os dois primeiros fluxos completos de decisão automática deste Bounded Context: `EarlyCheckInRequest`/`LateCheckoutRequest` (agregados novos, `guest_operations`), as duas primeiras exceções síncronas cross-context que Guest Operations consome (`IReservationScheduleReader` — Reservations; `ICleaningReadinessReader` — primeira exceção síncrona que Housekeeping concede), os dois novos endpoints HTTP (`POST .../early-check-in`, `POST .../late-checkout`), o reagendamento real da Reservation via dois novos orquestradores de Workflow (`RescheduleReservationForEarlyCheckIn`/`...LateCheckout`), quatro novos Integration Events, e a reação real (auditoria, nunca mutação de agenda inventada) de Housekeeping a `LateCheckoutApproved`. Decisões completas registradas no amendment "Checkpoint 3" do `ADR-024` (2026-08-27) e no Architecture Principles §14 (Exceções 7 e 8).

### 5.2 Correção de governança — numeração de ADR (reafirmação)

`ADR-025` permanece reservada, vazia, exclusivamente para o Boundary de PIX Payment (Checkpoint 5) — nenhum arquivo com esse número foi criado neste Checkpoint. Todas as decisões do Checkpoint 3 foram registradas como um segundo amendment ao `ADR-024` existente, mesma disciplina já aplicada ao Checkpoint 2.

### 5.3 Exceções síncronas #7/#8 — real, nunca fake, em nenhum teste

`IReservationScheduleReader` (Reservations.Infrastructure) e `ICleaningReadinessReader` (Housekeeping.Infrastructure, primeira concessão deste tipo por Housekeeping) — ambas implementadas via `TenantAwareTransactionScope`, mirroring `IReservationGuestContactReader` (ADR-019). Confirmado por auditoria de código e pelos próprios testes: nenhum dos seis E2E reais deste Checkpoint (§5.5–§5.7) nem os dois testes de autorização (§5.10) substituem qualquer um dos dois leitores — ou o leitor/leitores de política — por um fake. `ScheduleReaderReal=true`, `CleaningReadinessReaderReal=true`, `PolicyReaderReal=true`. Validado estruturalmente por `ArchitectureTests` (`GuestOperationsDependencyTests`, três novos testes: um positivo de referência exclusiva, dois de exclusividade de consumidor).

### 5.4 Agregados, cardinalidade e RLS

`EarlyCheckInRequest`/`LateCheckoutRequest` (RLS `ENABLE`+`FORCE`, schema `guest_operations`, migração `AddEarlyCheckInLateCheckoutRequests`) nascem `Pending` e são decididos sincronamente na mesma transação — nunca existe aprovação manual. Cardinalidade garantida por índice único parcial: `WHERE status = 'Pending'` (Early), `WHERE status IN ('Pending', 'PendingPayment')` (Late — `PendingPayment` conta como ativo). Grants de `ihostpro_app` restritos a `SELECT`/`INSERT`/`UPDATE` (sem `DELETE`), mesma convenção de `guest_stay_operations`. Ver `ADR-024` amendment §B3 para o desenho completo.

### 5.5 Early Check-in — E2E real (Approved e Denied)

**Approved** (`EarlyCheckIn_Approved_flows_through_the_real_broker_chain_and_reschedules_the_real_Reservation`): requisição HTTP real (JWT real, `GUEST_OPERATIONS:MANAGE`) contra `IHostPro.Api` real → política `EARLY_CHECKIN` lida de verdade de Configuration & Policy (seedada explicitamente para o teste via `CreatePolicyValueVersionCommand`, nunca um default global de produto) → `IReservationScheduleReader` real → `EarlyCheckInRequest.Status=Approved` → `EarlyCheckinApproved` publicado → RabbitMQ real → `IHostPro.Worker` real consome → orquestrador de Workflow envia `RescheduleReservationForEarlyCheckIn` → Reservations executa o reagendamento real → `Reservation.CheckInAt` atualizado para o horário solicitado, `Reservation.CheckOutAt` inalterado. `EarlyApprovedRealE2E=true`, `EarlyReservationRescheduled=true`.

**Denied** (`EarlyCheckIn_Denied_for_CleaningNotReady_never_dispatches_a_reschedule`): mesma cadeia, política exigindo limpeza concluída, Cleaning real (auto-criada pela coreografia já existente desde a Fase 8) ainda `Pending` → `EarlyCheckInRequest.Status=Denied`, `DenialReason=CleaningNotReady` → `EarlyCheckinDenied` publicado exatamente uma vez → nenhum reagendamento, nenhum comando de Workflow, `Reservation.CheckInAt` inalterado. `EarlyDeniedRealE2E=true`.

### 5.6 Late Checkout — E2E real (Approved sem PIX, PendingPayment, Percentage)

**Approved sem PIX** (`LateCheckout_Approved_without_Pix_reschedules_the_Reservation_and_Housekeeping_observes_it_without_mutating_the_schedule`): mesma cadeia real de ponta a ponta, política `LATE_CHECKOUT` com `chargeType=none`, `requiresPix=false`, `updatesCleaning=true` → `LateCheckoutRequest.Status=Approved` → `LateCheckoutApproved` publicado → Workflow reagenda a Reservation real (`Reservation.CheckOutAt` atualizado, `CheckInAt` inalterado) **e**, no mesmo publish, Housekeeping reage de forma independente (§5.8). `LateApprovedNoPixRealE2E=true`, `LateReservationRescheduled=true`.

**PendingPayment — gate crítico** (`LateCheckout_requiring_Pix_settles_at_PendingPayment_and_triggers_absolutely_no_downstream_effect`): política com `requiresPix=true` → `LateCheckoutRequest.Status=PendingPayment` (nunca `Approved`) → provado, de forma real e não apenas por ausência de asserção: zero reagendamento de Reservation, zero publicação de `LateCheckoutApproved`, zero reação de Housekeeping (zero entradas de auditoria), zero chamada a provedor de PIX, zero `ExternalPaymentId` (nenhuma coluna/tabela desse tipo existe em lugar nenhum do schema). `LatePendingPaymentRealE2E=true`, `PendingPaymentReservationUnchanged=true`.

**Percentage — gap de pricing confirmado** (`LateCheckout_with_a_Percentage_policy_fails_explicitly_before_persisting_any_row`): política com `chargeType=percentage` → falha funcional explícita (HTTP 409, `late_checkout_charge_type_percentage_unsupported`) ANTES de qualquer persistência. `PercentageUnsupportedVerified=true`: `RequestPersisted=false`, `ApprovalEventPublished=false`, `DenialEventPublished=false`, `ReservationChanged=false`. Nenhuma base de cálculo foi inventada — confirma o gap já registrado no Decision Gate deste Checkpoint (nenhum campo monetário existe em `Reservation`/`Property`/`AirbnbReservationImported`).

**Cardinalidade Late** (`LateCheckout_second_request_while_the_first_is_PendingPayment_is_rejected_as_already_active`): uma segunda requisição enquanto a primeira permanece `PendingPayment` (ativa) é rejeitada (HTTP 409), nenhuma segunda linha criada. Early não possui um cenário sequencial equivalente reproduzível por construção — a decisão síncrona sempre resolve para um status terminal antes da resposta HTTP retornar (ver `ADR-024` amendment §B3).

### 5.7 Reação de Housekeeping — auditoria real, sem mutação de agenda inventada

`LateCheckoutApprovedCleaningReactor` observado reagindo de verdade, no mesmo teste do §5.6 (Approved sem PIX): exatamente uma `CleaningAuditEntry` (`action_code=late_checkout_approved`) registrada contra a Cleaning automatizada existente, nunca duplicada. `LateHousekeepingReactionObserved=true`, `CleaningScheduleMutationImplemented=false`, `CleaningScheduleMutationReason=NoDocumentedSchedulingRule` — `Cleaning.ScheduledAtUtc` permanece `null`, nunca mutado, porque o Documento 10 não define uma regra de offset a partir de um checkout tardio. Isso não é um blocker do Core deste Checkpoint — é um gap de produto genuíno e explicitamente registrado, não um gap técnico escondido.

### 5.8 Fan-out real — ADR-020

`EarlyCheckinApprovedConsumerCount=1` (Workflow, único). `LateCheckoutApprovedConsumerCount=2` (Workflow — sempre; Housekeeping — quando `UpdatesCleaning=true`), cada um em fila própria (`workflow.late-checkout-approved-trigger`/`housekeeping.late-checkout-approved-trigger`), `ADR020IsolationVerified=true` — provado empiricamente pelo mesmo E2E do §5.6 (uma entrada de auditoria, nenhuma duplicação, nenhum consumidor rouba a entrega do outro), nunca apenas por inspeção estática. `EarlyCheckinDeniedConsumerCount=0`, `LateCheckoutDeniedConsumerCount=0` — nenhum consumidor artificial foi criado para evitar essa contagem.

### 5.9 Autorização/RLS — dois novos endpoints

Oito testes reais (`EarlyCheckInLateCheckoutAuthorizationTests`, host ASP.NET Core real + JWT real, módulos Reservations/Housekeeping/Configuration reais — nenhum leitor cross-context fake): para cada endpoint, anônimo → 401; autenticado sem `GUEST_OPERATIONS:MANAGE` → 403 (nunca 500); ADMIN + `GUEST_OPERATIONS:MANAGE` → 200 real (avaliação real, `policy_not_configured` — nenhuma política foi seedada nesses testes especificamente, cenário de sucesso deliberadamente simples); tenant errado → 404, nunca vaza existência. Nenhuma política nova — reaproveita `GUEST_OPERATIONS:MANAGE`, já promovida no Checkpoint 2.

### 5.10 MigrationRunner — Run #1/#2 (ambiente descartável real)

`Run1Exit=0`, `Run2Exit=0`, `Run2MigrationsApplied=0` (idempotência confirmada — exatamente as duas migrações esperadas em `guest_operations.__EFMigrationsHistory`, nenhuma duplicata). `RlsIntact=true`, `ForceRlsIntact=true` (`early_check_in_requests`/`late_checkout_requests`, `relrowsecurity`/`relforcerowsecurity` ambos `t` após as duas execuções). `IndexesIntact=true` (6 índices confirmados: PK + alternate key + índice único parcial, para cada uma das duas tabelas). `GrantsIntact=true` (`ihostpro_app`: `SELECT`/`INSERT`/`UPDATE` apenas, idênticos após Run #1 e Run #2). Topologia RabbitMQ confirmada nos logs reais: as três novas filas (`workflow.early-checkin-approved-trigger`, `workflow.late-checkout-approved-trigger`, `housekeeping.late-checkout-approved-trigger`) e as duas novas routing keys de `reschedule_for_early_check_in`/`reschedule_for_late_checkout` na exchange `workflow-orchestration-commands` já existente.

### 5.11 NSwag e Angular

Geração #1: ambos os novos endpoints (`earlyCheckIn`/`lateCheckout`) e seus quatro DTOs presentes no client gerado. Geração #2: diff zero contra a geração #1 (determinístico). Nenhuma edição manual do client gerado. `ng build`: sucesso, nenhuma UI nova (apenas o client TypeScript regenerado).

### 5.12 Investigação de teste order-dependent — descoberta durante a regressão completa

A primeira execução completa de `IHostPro.Api.Tests.Integration` (42 testes, incluindo os seis E2E deste Checkpoint) revelou uma falha: `WhatsAppMessageStatusRetryPolicyScopingTests.The_specific_exception_retries_while_an_unrelated_one_does_not` — `System.InvalidOperationException: Cannot resolve scoped service 'Wolverine.IMessageBus' from root provider`. Investigada por bisecção real (nunca aceita como flake conhecida sem prova):

- **Reprodução isolada:** o teste passa sozinho (1/1) — não é um defeito determinístico no próprio teste.
- **Sequência mínima identificada por trace cronológico do log real:** `OpenApiOperationIdTests` (teste pré-existente, não tocado por este Checkpoint) executando imediatamente antes, na mesma suíte.
- **Root cause confirmada por leitura direta do código** (nunca assumida por ser "provável"): `OpenApiOperationIdTests.EnvironmentKeys` — a lista que seu próprio bloco `finally` usa para limpar variáveis de ambiente do processo — havia saído de sincronia com o dicionário `values` que realmente as define, faltando `DOTNET_ENVIRONMENT` (e, adicionalmente, quatro chaves de connection string: `Communication`/`GuestOperations`/`ExternalIntegrations`/`Dashboard`, cada uma adicionada a `values` por commits anteriores sem atualizar essa lista). `WhatsAppMessageStatusRetryPolicyScopingTests` é o único teste da suíte que usa `Host.CreateDefaultBuilder()` puro — que habilita `ValidateScopes=true` automaticamente sob `DOTNET_ENVIRONMENT=Development`, exatamente a falha observada.
- **Classificação:** `PreExisting=true` (`git diff --stat` do arquivo estava vazio antes da correção; o último commit real a tocá-lo, `ae4339c`, parte do trabalho do Checkpoint 2 anterior, só adicionou uma entrada de connection string a `values`, nunca tocou `EnvironmentKeys`), `IntroducedByCP3=false`, `ExposedByCP3=false` (18+ outras classes de teste executaram normalmente entre a própria suíte deste Checkpoint e a falha).
- **Correção:** estritamente um test isolation fix — `EnvironmentKeys` sincronizada para incluir as cinco chaves faltantes. Nenhum código de produção alterado.
- **Prova:** teste isolado → PASS; sequência mínima anteriormente falha (`OpenApiOperationIdTests` → `WhatsApp...`) → PASS, repetida duas vezes limpas; **segunda execução completa da suíte, limpa: 42/42 aprovados** — esta é a execução válida para o gate deste Checkpoint.

### 5.13 Testes — contagens exatas (regressão final)

| Suíte | Resultado |
|---|---|
| `IHostPro.Contexts.GuestOperations.Tests.Unit` | 60 aprovados |
| `IHostPro.Contexts.GuestOperations.Tests.Integration` | 18 aprovados |
| `IHostPro.Contexts.Reservations.Tests.Unit` | 90 aprovados |
| `IHostPro.Contexts.Reservations.Tests.Integration` | 97 aprovados |
| `IHostPro.Contexts.Housekeeping.Tests.Unit` | 120 aprovados |
| `IHostPro.Contexts.Housekeeping.Tests.Integration` | 97 aprovados |
| `IHostPro.Contexts.Configuration.Tests.Unit` | 93 aprovados |
| `IHostPro.Contexts.Configuration.Tests.Integration` | 80 aprovados |
| `IHostPro.Contexts.Workflow.Tests.Unit` | 11 aprovados |
| `IHostPro.Contexts.Identity.Tests.Unit` | 470 aprovados |
| `IHostPro.Contexts.Identity.Tests.Integration` | 420 aprovados |
| `IHostPro.BuildingBlocks.Tests.Unit` | 13 aprovados |
| `IHostPro.ArchitectureTests` | 237 aprovados |
| `IHostPro.Api.Tests.Integration` (suíte completa, execução final limpa) | **42/42 aprovados** |
| MigrationRunner Run #1 (Postgres+RabbitMQ descartáveis reais) | Exit code 0 |
| MigrationRunner Run #2 (mesmo ambiente) | Exit code 0, zero migração nova, RLS/índices/grants intactos |
| NSwag (geração #1 e #2) | Diff zero entre as duas gerações |
| Angular build (`ng build`) | Sucesso, nenhuma UI nova |
| Build Release (solução completa) | 0 erro |
| `git diff --check` | Sem erros novos — apenas ruído pré-existente de NSwag (2 linhas, já presentes 23x no arquivo commitado) e avisos benignos de normalização LF→CRLF |

### 5.14 Escopo explicitamente NÃO implementado neste checkpoint

PIX/Payment real (Checkpoint 5, `ADR-025` reservada), Portaria/Front Desk (Checkpoint 4), qualquer cálculo de `Percentage`/domínio de pricing/moeda, qualquer offset de agenda de limpeza inventado, qualquer UI nova além do client TypeScript regenerado, qualquer nova exceção síncrona além das duas aprovadas (#7/#8), aprovação manual de request.

### 5.15 Status do Checkpoint 3

**Concluído e homologado.** Todos os gates obrigatórios fechados nesta ordem: seis E2E reais contra broker real (§5.5–§5.7), fan-out real (§5.8), autorização/RLS real (§5.9), MigrationRunner Run#1/#2 (§5.10), NSwag/Angular (§5.11), investigação e correção completa de um teste order-dependent pré-existente não relacionado (§5.12), regressão final limpa em todas as suítes relevantes (§5.13).

## 6. Checkpoint 4 — Portaria Notification Foundation

### 6.1 Escopo e Decision Gate

Um Architecture & Product Decision Gate read-only (zero alteração de arquivo) precedeu a implementação, auditando Documento 10, Documento 07, GuestOperations/Communication/Identity/PropertyManagement/Reservations atuais, Architecture Principles e ADR-024. Achado prévio relevante: "Documento 10 — Portaria" não existe como documento dedicado — a Portaria é especificada de forma rasa dentro de `Documento 10 - Mapa Funcional do Sistema (Feature Map).txt` (§15, quatro itens sem elaboração, mais menções esparsas em §3/§22/§11/§13/§25). O gate produziu um relatório de 30 itens (recipient model, ownership, cardinalidade, PII, etc.), classificando decisões em SAFE TO APPROVE / USER DECISION REQUIRED / DEFERRED / NOT MVP. As decisões abaixo foram confirmadas explicitamente pelo usuário antes de qualquer implementação.

Decisões oficiais do gate: Portaria **não** é usuário Identity — sem login, sem role dedicada, sem portal próprio, sem acknowledgement, sem capacidade de edição. É um **external/passive operational recipient** (destinatário externo e passivo).

### 6.2 Ownership

O cadastro estrutural da Portaria pertence a **Property Management** — `Architecture Principles.md` §3 já registrava "Portarias" como dado desta Bounded Context, antes mesmo deste checkpoint. Guest Operations não possui (nem nunca possuiu) cadastro de Portaria. Communication permanece o único dono da entrega de mensagem (rendering, recipient delivery, provider interaction, Message lifecycle) — Guest Operations e Property Management nunca chamam o provider.

### 6.3 Cardinalidade

Decisão: **um `FrontDeskContact` ativo por Condomínio** (`Condominium → 0..1 active FrontDeskContact`). Múltiplas portarias/guaritas por Condomínio permanecem **DEFERRED** — não implementado. O schema existente de `Condominium`/`Property` não impôs nenhum impedimento a essa relação (nenhum STOP necessário).

### 6.4 Entidade `FrontDeskContact`

Criada em `PropertyManagement.Domain`: `Id, TenantId, CondominiumId, DisplayName, PhoneNumber, IsActive, CreatedAtUtc, UpdatedAtUtc`. Deliberadamente sem guest data, sem `AccessCredential`, sem `ProviderMessageId`/identificador provider-specific — `PhoneNumber` é dado de contato operacional puro. Atualizada em local (`UpdateContact`) — nunca soft-deletada e recriada, portanto nenhuma linha histórica; `IsActive` é o único toggle liga/desliga.

### 6.5 RLS e cardinalidade no banco

Migração `20260827202539_AddFrontDeskContact` cria `property_management.front_desk_contacts` com FK composta (`tenant_id, condominium_id`) → `condominiums(tenant_id, id)`, `ENABLE`/`FORCE ROW LEVEL SECURITY`, política `tenant_isolation` (mesmo padrão fail-closed de todas as tabelas tenant-owned), e `REVOKE DELETE` (never explicitamente concedido — a tabela é atualizada em local, nunca apagada). Cardinalidade "no máximo um contato por Condomínio" enforced por **unique constraint simples** em `(tenant_id, condominium_id)` — não parcial/filtrado, já que não há histórico a excluir.

### 6.6 Exceção Síncrona #9 (ADR-026)

`Communication → PropertyManagement.Contracts`, via `IFrontDeskContactReader.GetActiveByPropertyIdAsync(TenantId, PropertyId)`, retornando `FrontDeskContactReadResult(ContactId, DisplayName?, PhoneNumber)`. Resolução interna Property → Condomínio → contato ativo é responsabilidade exclusiva de Property Management — Communication nunca conhece `CondominiumId`. Três casos colapsam no mesmo `null` (Property sem Condomínio; Condomínio sem contato; contato inativo) — tratados pelo chamador como a mesma situação ordinária ("nada a notificar"), nunca uma falha. Implementação em `PropertyManagement.Infrastructure`, mesmo padrão `TenantAwareTransactionScope` de `PropertyReservationEligibilityReader`/`ReservationGuestContactReader`. Registrada em **ADR-026** (nova, dedicada — não uma amendment de ADR-024, já que o par de contextos não envolve Guest Operations). `ADR-025` permanece reservada para PIX/CP5 — não criada, não tocada.

### 6.7 Extensão factual de ADR-019 (`GuestName`)

`ReservationGuestContact` (Communication → Reservations, Exceção #4/ADR-019) foi estendido com um terceiro campo, `GuestName` (string, não-nulo), para permitir que os processadores de notificação de Portaria rendericem o nome do hóspede. O próprio ADR-019 (item 4) já previa essa decisão explícita como necessária antes de qualquer extensão — registrada como amendment na própria ADR-019, não como nova exceção síncrona (o boundary permanece exatamente o mesmo). `GuestPhone` nunca atravessa para o caso de uso de Portaria.

### 6.8 Eventos e correção de payload (`PropertyId`)

Auditoria revelou que a premissa inicial do mandato ("Communication já possui `PropertyId` vindo do evento") estava factualmente incorreta — `GuestCheckedIn`/`EarlyCheckinApproved`/`LateCheckoutApproved` não carregavam `PropertyId`. Decisão confirmada pelo usuário: adicionar `PropertyId` (Guid, `required`) aos três eventos — extensão aditiva, mesmo precedente de `ReservationCreated` ganhar `CheckInAt`/`CheckOutAt` na Fase 7. Todos os três producers (`RecordGuestCheckedInCommandHandler`, `RequestEarlyCheckInCommandHandler`, `RequestLateCheckoutCommandHandler`) já possuíam `PropertyId` naturalmente disponível (via `GuestStayOperation.PropertyId`) — nenhum novo synchronous reader foi necessário para preenchê-lo. `GuestName`/`GuestPhone`/`CondominiumId`/`AccessCredential` NUNCA foram adicionados a nenhum evento.

Nenhum evento novo foi criado. `FrontDeskContactCreated`/`Updated` foram deliberadamente NÃO criados — resolução é sempre síncrona, nenhum consumidor real precisaria deles.

### 6.9 Processadores de Communication

Três processadores, um por business intent (nunca um `FrontDeskEventProcessor` genérico): `GuestCheckedInFrontDeskNotificationProcessor`, `EarlyCheckinApprovedFrontDeskNotificationProcessor`, `LateCheckoutApprovedFrontDeskNotificationProcessor`. Cada um: verifica idempotência (mesmo padrão `IdempotencyKey` de `ReservationCreatedCommunicationProcessor`) → resolve `IFrontDeskContactReader` → se `null`, no-op deliberado (log `FrontDeskContactNotConfigured`, zero `Message` criada, zero retry) → resolve Template (`FRONT_DESK_GUEST_CHECKED_IN`/`FRONT_DESK_EARLY_CHECKIN_APPROVED`/`FRONT_DESK_LATE_CHECKOUT_APPROVED`) → resolve `GuestName` via `IReservationGuestContactReader` → renderiza → cria/envia `Message` via `IOutboundMessageConnector` (mesmo `FakeWhatsAppConnector`, nenhum provider real). `LateCheckoutApprovedFrontDeskNotificationProcessor` nunca lê `UpdatesCleaning` — esse campo é gate exclusivo da reação de Housekeeping, não relacionado à notificação de Portaria. Nenhum conteúdo de mensagem real foi criado — apenas texto determinístico de teste/dev.

### 6.10 PII

`PropertyId`: permitido em Integration Event (provider-neutral, não-PII). `GuestName`: nunca no evento — resolvido via ADR-019 dentro do processamento de Communication. `GuestPhone`/`AccessCredential`/CPF/RG/email/documento: nunca em nenhum DTO/evento deste checkpoint — provado por testes de arquitetura dedicados (`Front_Desk_Trigger_Events_Never_Declare_A_Forbidden_PII_Property`, `FrontDeskContactReadResult_Never_Carries_Guest_Data_Or_Access_Credential`, `FrontDeskContact_Never_Declares_Guest_Data_Access_Credential_Or_Provider_Specific_Fields`). `PhoneNumber` da Portaria: mascarado em `Message.DestinationMasked` (últimos 4 dígitos, mesmo algoritmo de `ReservationCreatedCommunicationProcessor`), nunca logado por inteiro.

### 6.11 Fan-out (ADR-020)

`GuestCheckedIn`: 0 → 1 consumidor (Communication, primeiro consumidor real desde a Fase 10 CP2). `EarlyCheckinApproved`: 1 → 2 consumidores (Workflow + Communication). `LateCheckoutApproved`: 2 → 3 consumidores (Workflow + Housekeeping + Communication). Todos sticky-bound, filas próprias (`communication.guest-checked-in-trigger`, `communication.early-checkin-approved-trigger`, `communication.late-checkout-approved-trigger`), gated a `Development` (mesmo padrão de `communication.reservation-created-trigger` desde a Fase 9 CP1 — `FakeWhatsAppConnector` é o único connector). Isolamento provado por E2E real: ao adicionar Communication como consumidor adicional, Workflow continua reagendando a Reservation e Housekeeping continua registrando exatamente 1 audit entry — nenhum consumidor rouba ou duplica a entrega de outro.

### 6.12 No-contact / contato inativo / Property sem Condomínio

Todos os três casos resultam em `IFrontDeskContactReader` retornando `null`, tratado pelos processadores como no-op deliberado: zero `Message` criada, zero retry infinito, log estruturado com `FrontDeskContactNotConfigured`. Provado por E2E real (`GuestCheckedIn_without_a_front_desk_contact_is_a_deliberate_no_op`) e por testes de integração dedicados do reader (contato ativo encontrado; sem contato; contato inativo tratado como não configurado; Property sem Condomínio; isolamento de tenant; forma mínima da resposta, nunca o agregado).

### 6.13 API administrativa

`GET`/`PUT /api/v1/condominiums/{condominiumId}/front-desk-contact` — reaproveita `PROPERTIES:MANAGE` (nenhuma permissão nova criada). `GET` distingue Condomínio inexistente (404, `CondominiumNotFound`) de Condomínio sem contato configurado ainda (404, `FrontDeskContactNotFound`) — dois códigos de erro distintos. `PUT` faz upsert (cria se não existir, atualiza em local se existir, idempotente quando nenhum campo muda). Nenhum endpoint de leitura operacional (lista diária de chegadas/saídas) foi criado — explicitamente DEFERRED (§6.16).

### 6.14 Autorização/RLS real

Provado por teste HTTP real (`FrontDeskContactsEndpointsTests`, Postgres real + JWT real + catálogo de permissões real): sem token → 401; role sem `PROPERTIES:MANAGE` → 403; ADMIN → sucesso (`PUT` seguido de `GET` confirma o mesmo `Id`); Condomínio inexistente → 404; Condomínio de outro tenant → 404 (indistinguível de inexistente, RLS).

### 6.15 E2E real (broker/Worker/Postgres reais)

Quatro cenários, todos contra Postgres real, RabbitMQ real, `IHostPro.Worker.dll` real (subprocess não modificado) e HTTP real de `IHostPro.Api` com JWT real (`FrontDeskNotificationWorkflowRoundTripTests`):

1. `GuestCheckedIn` com contato configurado → `Message` real criada, `DestinationMasked` termina nos últimos 4 dígitos do telefone da PORTARIA (nunca do hóspede), status `Sent` via `FakeWhatsAppConnector`.
2. `GuestCheckedIn` sem contato configurado → zero `Message`, log `FrontDeskContactNotConfigured`, pipeline permanece verde.
3. `EarlyCheckinApproved` com contato configurado → Workflow reagenda a Reservation real E Communication cria a `Message` real (fan-out 2 consumidores).
4. `LateCheckoutApproved` com contato configurado → Workflow reagenda, Housekeeping registra exatamente 1 audit entry, Communication cria a `Message` real (fan-out 3 consumidores, nenhuma duplicação).

`IFrontDeskContactReader` nunca foi mockado nestes testes — toda resolução roda a implementação real de Infrastructure contra dados reais semeados no Postgres.

### 6.16 Escopo explicitamente NÃO implementado neste Checkpoint

Portal de Portaria (nenhuma menção em Documento 10, nem mesmo como funcionalidade futura); lista diária de chegadas/saídas (read model operacional); "Histórico" (audit subsystem novo); acknowledgement/confirmação de recebimento; role `FRONT_DESK`/`PORTARIA`; `GuestPhone` na notificação de Portaria; `AccessCredential`/senha/PIN entregue à Portaria (permanece `DEFERRED PENDING SECURE DELIVERY BOUNDARY`, ADR-024 §A7, sem qualquer relação com este checkpoint); múltiplas portarias por Condomínio; qualquer nova exceção síncrona além da #9; UI nova (frontend permanece backend/API only — nenhuma tela Angular criada); conteúdo textual real de mensagem (apenas texto determinístico de teste/dev).

### 6.17 MigrationRunner (Run #1/#2) e regressão completa

MigrationRunner executado duas vezes contra um ambiente Postgres+RabbitMQ descartável (containers manuais, não Testcontainers, nunca o banco de desenvolvimento compartilhado): Run#1 aplicou `20260827202539_AddFrontDeskContact` com exit code 0 e provisionou as três novas filas de Communication (`communication.guest-checked-in-trigger`, `communication.early-checkin-approved-trigger`, `communication.late-checkout-approved-trigger`); Run#2 (exit code 0, nenhuma exceção) confirmou idempotência — nenhuma migração pendente, nenhuma tentativa de recriar a tabela. Verificado diretamente contra o Postgres descartável após os dois runs: `front_desk_contacts` com `ENABLE`/`FORCE ROW LEVEL SECURITY` intactos, política `tenant_isolation` presente, três índices corretos (chave primária, chave alternada `(tenant_id, id)`, e o índice único de cardinalidade `(tenant_id, condominium_id)`), e grants de `ihostpro_app` limitados a `SELECT`/`INSERT`/`UPDATE` (sem `DELETE`, confirmando o `REVOKE DELETE` da migração).

Regressão completa executada nesta ordem, todas verdes: ArchitectureTests 243/243; PropertyManagement.Tests.Unit 192/192; PropertyManagement.Tests.Integration 200/200 (Postgres real); Communication.Tests.Unit 82/82; Communication.Tests.Integration 12/12 (Postgres real); Reservations.Tests.Unit 90/90; Reservations.Tests.Integration (leitor com `GuestName`) 6/6 (Postgres real); Workflow.Tests.Unit 11/11; GuestOperations.Tests.Unit 60/60; Housekeeping.Tests.Unit 120/120; full `IHostPro.Api.Tests.Integration` — **46/46**, 0 falhas, execução única e limpa (30min5s, broker/Worker/Postgres reais para toda a suíte, incluindo os quatro cenários novos deste Checkpoint e os 42 já homologados no Checkpoint 3, provando que nenhuma regressão foi introduzida). `dotnet build IHostPro.sln -c Release`: 0 erros. `npx tsc --noEmit` (Angular): 0 erros, nenhuma UI nova a compilar. `git diff` revisado integralmente: nenhum conteúdo proibido encontrado (sem `ADR-025`, sem `FRONT_DESK:MANAGE`, sem role/controller literal de "Portaria", sem `AccessCredential`, sem `Currency`/`Price`).

O container de desenvolvimento `ihostpro-rabbitmq` foi parado e restaurado ao redor de cada execução de E2E de porta fixa (5672) — nunca `ihostpro-postgres`/`ihostpro-redis` — sem operações concorrentes de Docker durante qualquer suíte em execução.

### 6.18 Status do Checkpoint 4

**Concluído e homologado.** Gates fechados: Decision Gate read-only prévio (§6.1), ownership/cardinalidade/entidade/RLS confirmados (§6.2–§6.5), Exceção Síncrona #9 registrada em ADR-026 nova e dedicada (§6.6), extensão factual de ADR-019 registrada (§6.7), correção de premissa sobre `PropertyId` confirmada e implementada (§6.8), três processadores de Communication provados por testes unitários e E2E real (§6.9, §6.15), PII provada por testes de arquitetura dedicados (§6.10), fan-out isolado provado por E2E real (§6.11), comportamento no-contact/inativo provado (§6.12), API administrativa e autorização real provadas (§6.13–§6.14), MigrationRunner Run#1/#2 e regressão completa limpos em todas as suítes relevantes, incluindo full `IHostPro.Api.Tests.Integration` 46/46 (§6.17).

## 7. Checkpoint 5 — PIX/Payment Deterministic Foundation

### 7.1 Decision Gate read-only prévio

Realizado antes de qualquer implementação, conforme o mesmo processo dos Checkpoints anteriores: auditoria de fonte de verdade (Documento 07 §9, Documento 10 §14, Documento 13 §9, Documento 19 §13), confirmação de que `LateCheckoutRequest.PendingPayment` era um estado puro sem coluna financeira, pesquisa oficial de providers PIX (Asaas/Pagar.me/OpenPix — nenhum escolhido), e um relatório de 49 itens classificando cada decisão pendente (SAFE TO APPROVE / USER DECISION REQUIRED / DEFERRED / EXTERNAL BLOCKER / NOT MVP). Duas decisões adicionais foram resolvidas via `AskUserQuestion` já durante a implementação (persistência do QR — coluna comum, não criptografada; e o mecanismo de disparo da confirmação E2E — mensagem provider-neutra real via Wolverine/RabbitMQ, nunca endpoint HTTP test-only).

### 7.2 Novo Bounded Context: Payments

Criado como Supporting Bounded Context, seguindo o padrão dos demais: `IHostPro.Contexts.Payments.Contracts`/`.Domain`/`.Application`/`.Infrastructure`. **Sem projeto `.Api`** — nenhum endpoint público foi criado (mandato explícito: "zero Payments public API neste CP5 foundation").

### 7.3 Ownership

Guest Operations continua dona de `LateCheckoutRequest` e da decisão final de aprovação/negação — nunca do ciclo de vida financeiro. Payments é dona exclusiva de `PixCharge`. External Integrations é dona de `IPixProvider`/ACL do provider real futuro. Communication é dona da entrega do QR ao hóspede.

### 7.4 Boundary assíncrono Guest Operations ↔ Payments

Decisão confirmada: **sem chamada síncrona**. `LateCheckoutRequest` em `PendingPayment` publica `LateCheckoutPaymentRequired` (`GuestOperations.Contracts`); Payments consome e cria `PixCharge`. Payments confirma via `PixChargeConfirmed` (`Payments.Contracts`); Guest Operations consome e chama `LateCheckoutRequest.Approve()` — reaproveitando integralmente o fluxo já homologado no Checkpoint 3. `Approve()` foi estendido para aceitar `PendingPayment` como estado de origem (além de `Pending`), exatamente a "transição em diante" que o próprio doc comment de `PendingPayment` já antecipava desde o Checkpoint 3.

### 7.5 `PixCharge`

Campos: `Id`, `TenantId`, `LateCheckoutRequestId`, `ReservationId`, `Amount`, `CurrencyCode`, `Status`, `ProviderChargeId?`, `QrCodePayload?`, `IdempotencyKey` (gerada internamente), `ExpiresAtUtc?`, `ConfirmedAtUtc?`, `FailedAtUtc?`, `CreatedAtUtc`, `UpdatedAtUtc`. `Amount`/`CurrencyCode` são snapshot único de `LateCheckoutPaymentRequired` — nunca recalculados. `CurrencyCode` é **BRL-only** (`Create` rejeita qualquer outro valor). `Percentage` continua oficialmente não suportado (decisão do Checkpoint 3, não reaberta).

`QrCodePayload` é persistido em coluna comum (decisão explícita deste checkpoint, via `AskUserQuestion`) — protegido por RLS/tenant isolation como qualquer outra coluna tenant-owned, nunca por criptografia de coluna (nenhum padrão desse tipo existe nesta base hoje). Classificado como dado operacional de pagamento sensível, nunca um segredo (nunca roteado por `*SecretReference`). Nunca aparece em log, Integration Event, query string, ou mensagem de exceção. Revisão de proteção em repouso registrada como follow-up de Production hardening, não como bloqueador deste checkpoint.

### 7.6 `PixChargeStatus` e matriz de transição

`Pending, Confirmed, Failed, Expired, Cancelled` — sem `Created` separado. `Confirm()` aplica a matriz aprovada: `Pending/Failed/Expired → Confirmed` (avanço, inclusive fora de ordem — dinheiro confirmado tem precedência); `Confirmed → Confirmed` (no-op idempotente); `Confirmed → Failed`/`Expired` (regressão, no-op); `Cancelled → Confirmed` **não decidida** — lança `PixChargeCancelledConfirmationConflictException` em vez de decidir silenciosamente (nada neste checkpoint jamais define `Cancelled`, então este ramo é inalcançável hoje, provado por teste unitário via reflection). `Fail()` aplicado quando o provider rejeita/falha tecnicamente na criação. `PaymentFailed`/`Expired` nunca denegam nem cancelam o `LateCheckoutRequest` — permanece `PendingPayment`, sem `LateCheckoutDenied`; uma nova tentativa é uma operação futura explícita, fora de escopo.

### 7.7 Idempotência e cardinalidade

No máximo uma `PixCharge` ATIVA (`Pending`) por `(TenantId, LateCheckoutRequestId)` — índice único parcial, provado por teste de integração real (Postgres) rejeitando uma segunda `Pending` e permitindo uma nova após `Failed`. O handler de `LateCheckoutPaymentRequired` verifica essa mesma condição antes de criar.

### 7.8 Exceção Síncrona #10 — ADR-025

Payments → External Integrations, `IPixProvider` (`ExternalIntegrations.Contracts`), execução síncrona de criação de cobrança — mesma natureza da Exceção 6 (ADR-021). `FakePixProvider` é a única implementação: sempre aceita, determinística, sem rede, sem dinheiro real, registrada incondicionalmente (nenhum provider real existe para conflitar). Escolha de provider real permanece `DEFERRED`.

### 7.9 Exceção Síncrona #11 — ADR-027

Communication → Payments, `IPixChargeDeliveryReader` (`Payments.Contracts`), leitura síncrona purpose-limited do payload de entrega — nunca regenera via novo `IPixProvider.CreateChargeAsync`, sempre lê o `QrCodePayload` já persistido. `PixChargeCreated` nunca carrega o payload financeiro — apenas `TenantId`/`LateCheckoutRequestId`/`ReservationId`/identidade da charge.

### 7.10 Seam de confirmação provider-neutro

`PixChargeConfirmationReceived` (`Payments.Contracts`, mensagem cross-context, não um `IntegrationEvent` — mirroring `CreateCleaningForReservation`/`CloseReservation`) representa o fato "uma cobrança foi confirmada", código de produção legítimo, o seam que uma futura normalização de webhook em External Integrations produziria sem mudança de domínio. Único publicador hoje: o harness de teste E2E, via envio real Wolverine/RabbitMQ (exchange `payments-commands`, Direct, routing key `pix_charge_confirmation_received`) — nunca endpoint HTTP test-only, nunca lógica de teste embutida no domínio (decisão confirmada via `AskUserQuestion`).

### 7.11 Entrega segura ao hóspede

`PixChargeCreatedDeliveryProcessor` (Communication) consome `PixChargeCreated`, resolve `IReservationGuestContactReader` (ADR-019, telefone do HÓSPEDE, nunca da Portaria) e `IPixChargeDeliveryReader` (ADR-027, QR), renderiza e envia via `IOutboundMessageConnector` (`FakeWhatsAppConnector`, mesmo padrão de todo consumidor de Communication deste projeto). O QR é renderizado no CONTEÚDO da mensagem — seu destino final legítimo, nunca um vazamento.

### 7.12 Provas E2E reais

Três cenários, todos contra Postgres real, RabbitMQ real, `IHostPro.Worker.dll` real (subprocess não modificado) e HTTP real de `IHostPro.Api` com JWT real (`PixPaymentWorkflowRoundTripTests`):

1. Late Checkout com PIX exigido → `LateCheckoutPaymentRequired` real → `PixCharge` criada com `ProviderChargeId`/`QrCodePayload` persistidos → `PixChargeCreated` real → Communication entrega ao HÓSPEDE (nunca à Portaria), `Message.RenderedContent` contém o QR persistido.
2. `PixChargeConfirmationReceived` real → `PixCharge` Confirmed → `PixChargeConfirmed` real → Guest Operations aprova → `LateCheckoutApproved` → Workflow reagenda a Reservation real, Housekeeping registra exatamente 1 audit entry, Communication notifica a Portaria (fan-out completo disparado pelo caminho PIX, nunca testado antes por este caminho específico).
3. Confirmação duplicada (mesma `PixChargeId`, publicada duas vezes) → exatamente um reagendamento, exatamente um audit entry, zero efeito duplicado.

### 7.13 Escopo explicitamente NÃO implementado neste Checkpoint

Provider PIX real (Asaas/Pagar.me/OpenPix — nenhum escolhido, `ProductionProviderSelected=false`); webhook real (nenhum verificador de assinatura, nenhuma tabela de roteamento por tenant, nenhum DTO de provider); retry/nova tentativa de cobrança (nenhum endpoint criado); CPF/CNPJ/dado de pagador; `Percentage` (decisão do Checkpoint 3, não reaberta); Refunds; criptografia de coluna para o QR (registrada como follow-up); qualquer API pública de Payments; qualquer nova permission; UI nova.

### 7.14 MigrationRunner (Run #1/#2) e regressão completa

MigrationRunner executado duas vezes contra um ambiente Postgres+RabbitMQ descartável (containers manuais, nunca o banco de desenvolvimento compartilhado): Run#1 aplicou `20260828000204_InitialCreate` (schema `payments`) com exit code 0 e provisionou as exchanges `payments-events`/`payments-commands` com todos os bindings; Run#2 (exit code 0, nenhuma exceção) confirmou idempotência. Regressão completa: ArchitectureTests 256/256; Payments.Tests.Unit 25/25; Payments.Tests.Integration 11/11 (Postgres real, RLS/cardinalidade/reader provados); Communication.Tests.Unit 87/87; GuestOperations.Tests.Unit 64/64; full `IHostPro.Api.Tests.Integration` — **todas as suítes verdes após correção de uma lacuna sistêmica pré-existente** (25 fixtures de E2E não tinham `ConnectionStrings__Payments`, exigido incondicionalmente por `IHostPro.MigrationRunner`/`IHostPro.Worker` desde que `PaymentsDbContext` foi registrado; corrigido em todos os arquivos afetados, incluindo uma lacuna independente e pré-existente em `PolicyUpdatedWolverineDiscoveryTests` que já omitia `ConnectionStrings__GuestOperations`/`ExternalIntegrations` antes deste checkpoint). `dotnet build IHostPro.sln -c Debug`: 0 erros.

O container de desenvolvimento `ihostpro-rabbitmq` foi parado e restaurado ao redor de cada execução de E2E de porta fixa (5672) — nunca `ihostpro-postgres`/`ihostpro-redis`.

### 7.15 Status do Checkpoint 5

**Concluído, com uma lacuna de evidência identificada na revisão final e fechada pelo Checkpoint 5.1 (§8).** Gates fechados: Decision Gate read-only prévio (§7.1), novo Bounded Context Payments sem API pública (§7.2), ownership e boundary assíncrono confirmados (§7.3–§7.4), `PixCharge`/`PixChargeStatus` com matriz de transição completa e idempotência provados por teste unitário e de integração real (§7.5–§7.7), Exceções Síncronas #10/#11 registradas em ADR-025/ADR-027 novas e dedicadas (§7.8–§7.9), seam de confirmação provider-neutro e entrega segura ao hóspede provados por teste unitário e E2E real (§7.10–§7.12), MigrationRunner Run#1/#2 e regressão completa limpos em todas as suítes relevantes, incluindo full `IHostPro.Api.Tests.Integration` (§7.14). `RealMoneyTransactions=0`. `ExternalPixNetworkCalls=0`. `ProductionProviderSelected=false`.

O relatório final do Checkpoint 5 registrou honestamente `Failed/Expired E2E = NÃO PROVADO` (item 41 do relatório de 69 itens) — o mandato exigia prova E2E real das transições `Failed`/`Expired`, e nada no CP5 as disparava fora de teste unitário (`FakePixProvider` sempre aceita). A revisão do relatório considerou essa lacuna objetivamente bloqueante para homologação definitiva; o Checkpoint 5.1 (§8) fecha exclusivamente essa lacuna, sem reverter nenhuma arquitetura já publicada do CP5.

## 8. Checkpoint 5.1 — Payment Failure/Expiration Evidence Corrective Gate

### 8.1 Motivo e escopo

Gate corretivo, não um novo checkpoint funcional — fecha exclusivamente a lacuna de evidência `Failed/Expired E2E = NÃO PROVADO` apontada na revisão do relatório final do Checkpoint 5 (§7.15). Reaproveita integralmente a arquitetura já publicada em `7c96b01` (SHA base) — nenhuma reversão, nenhum novo Bounded Context, nenhuma nova exceção síncrona.

### 8.2 `PixChargeFailureReceived` / `PixChargeExpirationReceived`

Duas novas mensagens provider-neutras (`Payments.Contracts`, mesma natureza de `PixChargeConfirmationReceived` — não `IntegrationEvent`s), no MESMO seam já aprovado: mesma exchange Direct `payments-commands`, duas novas routing keys (`pix_charge_failure_received`/`pix_charge_expiration_received`), duas novas filas (`payments.failure-received`/`payments.expiration-received`). Payload mínimo provider-neutro: `TenantId`, `PixChargeId`, `OccurredAtUtc`/`ExpiredAtUtc`, `CorrelationId`, `CausationId?`. `PixChargeFailureReceived` aceita adicionalmente `FailureCode?`, usado apenas para diagnóstico/log — nunca persistido (mesmo tratamento que `PixChargeCreationResult.FailureCode` já recebia desde o CP5). Nenhum QR, nenhuma PII de pagador, nenhum dado de provider, nenhum secret. Único publicador: o harness de teste E2E, via envio real Wolverine/RabbitMQ — nunca um endpoint HTTP test-only (ver ADR-025).

### 8.3 `PixCharge.Expire()` e a nova coluna `ExpiredAtUtc`

`PixCharge.Expire()` é um método de domínio novo, mirroring `Fail()` exatamente: `Pending → Expired`, no-op idempotente se já `Confirmed`/`Failed`/`Expired` (nenhuma transição aprovada entre os dois estados terminais negativos). Persiste `ExpiredAtUtc` (nova coluna nullable, migração `AddPixChargeExpiredAtUtc`, sem mudança de RLS/grants — ambos já se aplicam à tabela inteira). Os dois novos handlers (`PixChargeFailureReceivedCommandHandler`/`PixChargeExpirationReceivedCommandHandler`) chamam `Fail()`/`Expire()` e **não publicam nenhum evento downstream** — nenhum consumidor real precisa reagir a `Failed`/`Expired`; `LateCheckoutRequest` permanece deliberadamente `PendingPayment`, sem `LateCheckoutDenied`.

### 8.4 Provas E2E reais

Três novos cenários adicionados a `PixPaymentWorkflowRoundTripTests` (mesma infraestrutura real — Postgres, RabbitMQ, `IHostPro.Worker.dll` subprocess, HTTP real de `IHostPro.Api`):

1. `PixChargeFailureReceived` real → `PixCharge` Failed → `LateCheckoutRequest` permanece `PendingPayment`, Reservation inalterada, zero audit entry de Housekeeping, zero mensagem de Portaria.
2. `PixChargeExpirationReceived` real → `PixCharge` Expired → mesmas quatro asserções de ausência de efeito.
3. Fora de ordem: `PixChargeFailureReceived` real primeiro → `PixCharge` Failed → depois `PixChargeConfirmationReceived` real → `PixCharge` Confirmed → `PixChargeConfirmed` real → Guest Operations aprova → `LateCheckoutApproved` → Reservation reagendada, Housekeeping reage, Communication notifica a Portaria — prova real de que uma confirmação genuína sempre vence um sinal negativo fora de ordem.

O caminho simétrico (`Expired → Confirmed`) permanece coberto exclusivamente por teste unitário de domínio (`PixChargeTests.Confirm_from_Expired_forwards_to_Confirmed`, agora usando o `Expire()` real em vez do hack de reflection do CP5) — proporcional, mesma máquina de estados já provada real pelo cenário 3.

### 8.5 Regressão

Payments.Tests.Unit, Payments.Tests.Integration (Postgres real), ArchitectureTests, e full `IHostPro.Api.Tests.Integration` — números exatos no relatório final do Checkpoint 5.1 (ver conversa de homologação). `dotnet build IHostPro.sln -c Release`: 0 erros.

### 8.6 Status do Checkpoint 5.1

**Concluído.** `FailedE2E=true`. `ExpiredE2E=true`. `RealMoneyTransactions=0`. `ExternalPixNetworkCalls=0`. `ProductionProviderSelected=false`. `RealProviderWebhookImplemented=false`. Fecha definitivamente a lacuna de evidência do Checkpoint 5 — Fase 10, Checkpoint 5, agora **DEFINITIVAMENTE HOMOLOGADO E PUBLICADO** em conjunto com este gate corretivo.

## 9. Próximo Checkpoint Recomendado

Checkpoint 6, conforme a estrutura CP0–CP6 já adotada — escopo a refinar e aprovar antes do início, seguindo o mesmo processo já aplicado aos Checkpoints anteriores. Não iniciado.
