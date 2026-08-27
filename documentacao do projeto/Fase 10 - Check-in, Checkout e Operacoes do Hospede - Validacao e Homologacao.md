# Fase 10 — Check-in, Checkout e Operações do Hóspede — Validação e Homologação

Versão: 1.0
Status: Em andamento — Checkpoint 1 concluído

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

## 4. Próximo Checkpoint Recomendado

Checkpoint 2, conforme a estrutura CP0–CP6 já adotada — escopo a refinar e aprovar antes do início, seguindo o mesmo processo já aplicado a este Checkpoint.
