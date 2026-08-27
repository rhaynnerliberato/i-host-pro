# ADR-024 — Guest Operations Boundary and Checkout Orchestration

Status: Aceito
Data: 2026-08-26

## Contexto

A Fase 10, Checkpoint 0 (Architecture & Product Decision Gate) aprovou a criação de um novo Bounded Context, **Guest Operations**, como o dono exclusivo do ciclo de vida operacional do hóspede (check-in, checkout, early check-in, late checkout, Portaria) — um conceito distinto do ciclo de vida de reserva que `Reservation` já possui. O Checkpoint 0 também aprovou, em princípio, que o checkout real do hóspede deveria eventualmente encerrar a Reservation correspondente (`ReservationStatus.Closed`), e que — por ADR-018 já proibir qualquer Bounded Context além de Workflow Orchestration de enviar comandos cross-context — esse encerramento precisaria necessariamente passar por um novo orquestrador de Workflow, nunca por uma chamada direta de Guest Operations a Reservations.

Este ADR registra as decisões concretas do Checkpoint 1 ("Guest Operations Foundation"): o desenho mínimo do novo Bounded Context, o mecanismo exato de encerramento da Reservation, e a semântica de idempotência/violação de invariante para o comando `CloseReservation` — a primeira decisão que exigiu parar e perguntar ao usuário, por existirem dois precedentes reais conflitantes no próprio codebase (ver Seção "Alternativas Consideradas").

## Decisão

### 1. Guest Operations como novo Bounded Context, com footprint mínimo

Está aprovada a criação de `GuestOperations.Domain/Contracts/Application/Infrastructure` — deliberadamente **sem** projeto `.Api` neste checkpoint: zero endpoints HTTP existem (nenhum fluxo de check-in/checkout via UI é implementado ainda), mirroring a mesma decisão que `Workflow.Infrastructure` tomou na Fase 8, Checkpoint 1 ("no Domain/Contracts/Api project, only Application/Infrastructure... since CP1 has no aggregates, publishes nothing of its own, and exposes no endpoint") pela mesma razão estrutural ("exposes no endpoint"), ainda que Guest Operations, ao contrário de Workflow, já possua um agregado real e publique um evento real.

### 2. `GuestStayOperation` — agregado mínimo

`GuestStayOperation` (tenant-owned, RLS, schema `guest_operations`) carrega apenas: `Id`, `TenantId`, `ReservationId`, `PropertyId`, `Status` (`GuestStayOperationStatus.Active`/`CheckedOut` — apenas dois valores; a granularidade completa de check-in que o Documento 10 descreve — formulário pendente, acesso entregue, instruções enviadas, portaria notificada, entrada concedida — é deliberadamente **não** modelada agora, já que este checkpoint não implementa nenhum comportamento de check-in, apenas a fundação do agregado e o gatilho de checkout), `CheckedInAtUtc`/`CheckedOutAtUtc` (nullable), `CreatedAtUtc`/`UpdatedAtUtc`. Sem nome/telefone do hóspede, sem credencial de acesso, sem dados de Early/Late, sem Portaria, sem pagamento — todos deferidos a checkpoints futuros.

`ReservationId`/`PropertyId` carregam identidade opaca (sem FK física), mirroring `Reservation.PropertyId`'s próprio precedente através da fronteira Reservations/Property Management. Restrição única de banco `(TenantId, ReservationId)` garante exatamente um `GuestStayOperation` ativo por Reservation.

### 3. `ReservationStatus.Closed` — terceiro estado, alcançável apenas de `Confirmed`

`Reservation.Close(now)` mirra `Cancel(now)` exatamente: guarda única (`Status != Confirmed` → `InvalidOperationException`), terminal, sem restauração. Este guarda é defesa em profundidade — a tradução de "já Closed" em no-op silencioso, e de "Cancelled" em exceção específica, é responsabilidade do handler que chama `Close()`, nunca do próprio método de domínio (ver Seção 5).

`ReservationStatusCodeMapper.ToCode` ganha `Closed => "closed"` — necessário para que os endpoints GET/LIST já existentes nunca quebrem ao encontrarem uma Reservation Closed real. `FromCode` permanece sem o caso inverso: nenhum endpoint expõe `status=closed` como filtro de consulta ainda (fora de escopo, nenhum consumidor real existe).

### 4. `CloseReservation` — segundo comando cross-context, mesmo mecanismo do ADR-018

`Reservations.Contracts.CloseReservation` (`TenantId`, `ReservationId`, `CorrelationId`, `CausationId?` — payload mínimo, mirroring `CreateCleaningForReservation` exatamente, sem `PropertyId`: Reservations já o possui) é enviado exclusivamente por Workflow Orchestration, via `IMessageBus.SendAsync` (nunca `Publish`), na mesma exchange dedicada `workflow-orchestration-commands` já criada pelo ADR-018 (uma segunda routing key, `close_reservation`, nunca uma segunda exchange). `ICloseReservationHandler`/`CloseReservationCommandHandler` (Reservations.Application) e o adapter Wolverine fino `CloseReservationHandler` (Reservations.Infrastructure.Messaging) mirram exatamente `ICreateCleaningForReservationHandler`/`CreateCleaningForReservationCommandHandler`/`CreateCleaningForReservationHandler` de Housekeeping — incluindo a extensão estritamente aditiva de `IReservationsMessageExecutionScope` (`ExecuteCloseReservationAsync`), a mesma e única classe já autorizada a deter `IServiceScopeFactory` em Reservations (ADR-016).

### 5. Semântica de fechamento — decisão explícita do usuário

O ponto de decisão genuinamente bloqueante deste checkpoint: o que acontece quando `CloseReservation` chega para uma Reservation já `Cancelled`? Dois precedentes reais e conflitantes existiam no codebase (ver Seção "Alternativas Consideradas"). O usuário escolheu explicitamente:

- **`Confirmed` → `Closed`**: publica `ReservationClosed` exatamente uma vez.
- **`Closed` → no-op idempotente silencioso**: não republica `ReservationClosed`, não lança exceção. Verificado pelo handler ANTES de chamar `Reservation.Close()`.
- **`Cancelled` → violação de invariante**: lança `ReservationCancelledCannotBeClosedException` (Reservations.Application) — um tipo próprio, nunca o `InvalidOperationException` genérico do guarda de domínio, para permanecer distinguível/investigável. **Sem** política de retry customizada — depende exclusivamente do comportamento padrão do Wolverine (uma tentativa, depois dead-letter). Nunca restaura a Reservation, nunca republica `ReservationClosed`.
- Uma Reservation não encontrada (`GetByIdAsync` retorna `null`) é uma anomalia distinta e genérica — `InvalidOperationException` simples, mesma razão de design de `CreateCleaningForReservationCommandHandler`'s própria checagem de Property.

Motivo do usuário: `CloseReservation` é produzido por um fluxo interno controlado e bem-ordenado (`GuestOperations → GuestCheckedOut → Workflow → CloseReservation → Reservations`) — diferente do Airbnb, que trata eventos externos com ordenação incerta. Uma Reservation `Cancelled` recebendo `CloseReservation` representa um bug de orquestração ou violação de invariante interna, e deve permanecer visível, nunca absorvida silenciosamente.

### 6. `GuestCheckedOut` e o segundo orquestrador de Workflow

`GuestOperations.Contracts.GuestCheckedOut` (`ReservationId` apenas — sem `PropertyId`, nenhum consumidor real o exige ainda) é publicado por `RecordGuestCheckedOutCommandHandler` (GuestOperations.Application) ao transicionar um `GuestStayOperation` para `CheckedOut`. `GuestCheckedOutCloseReservationOrchestrator` (Workflow.Application) — o segundo caso de uso deste contexto, mirroring `ReservationCreatedCleaningOrchestrator` exatamente, incluindo o mesmo registro de auditoria estruturado do Documento 17 §28 — reage a ele enviando `CloseReservation` via `IWorkflowCommandDispatcher.DispatchCloseReservationAsync` (nova assinatura, mesma interface, mesmo padrão do ADR-018 item 3 de "nenhum command bus genérico").

`RecordGuestCheckedOutCommand`/`IRecordGuestCheckedOutHandler` (GuestOperations.Application) são deliberadamente resolvidos diretamente — nunca via Mediator/HTTP — já que este checkpoint não tem nenhum endpoint. `IHostPro.Api` é o único processo que os invoca (registrado via `AddGuestOperationsModule`/`AddReservationsCloseReservationCommand`), mirroring como o teste E2E de ADR-018 já resolvia `ICreateCleaningForReservationHandler` diretamente para sua própria checagem de idempotência.

### 7. Catálogo de permissões: seed sem promoção nem wiring

`GUEST_OPERATIONS:MANAGE`/`GUEST_OPERATIONS:READ` são, como `INTEGRATIONS:MANAGE` (ADR-021), entradas genuinamente novas no catálogo de permissões (não uma promoção de um código já seedado) — mas, ao contrário de `INTEGRATIONS:MANAGE` (que já tinha um controller real consumindo-o), **nenhum endpoint existe ainda para consumi-las**. `IdentityAuthorizationExtensions`'s próprio comentário documenta a regra "apenas políticas realmente consumidas por um endpoint existente são registradas aqui". Decisão: seed (migração `AddGuestOperationsPermissions`, `Permission`+`RolePermission` ADMIN) sem promoção a `IdentityPermissionCodes` e sem `AddPolicy` — mirroring exatamente o precedente já existente de `SETTINGS:MANAGE`/`SETTINGS:READ` (seedados desde a migração inicial, sem consumidor por múltiplas fases). A promoção/wiring fica para o checkpoint que introduzir o primeiro endpoint real.

## Idempotência

Dupla camada, mesmo padrão do ADR-018: verificação na Application (`CloseReservationCommandHandler` checa `Status` antes de chamar `Close()`; `RecordGuestCheckedOutCommandHandler` checa `Status` antes de chamar `CheckOut()`) mais o guarda de domínio como defesa em profundidade (nunca a única garantia). Provado deterministicamente por testes unitários (`CloseReservationCommandHandlerTests`, `RecordGuestCheckedOutCommandHandlerTests`) e por um E2E real via RabbitMQ + Worker + Postgres (`GuestCheckedOutCloseReservationWorkerRoundTripTests`), incluindo uma segunda entrega redelivered de `CloseReservation` sobre transporte real.

## Alternativas Consideradas

- **Cancelled + CloseReservation → no-op permanente** (mirroring `AirbnbReservationCancelledProcessor`, Fase 9 CP3.2): rejeitada explicitamente pelo usuário — esse precedente existe para uma fonte de eventos EXTERNA e sem garantia de ordenação (Airbnb); `CloseReservation` é interno e bem-ordenado, e um no-op silencioso esconderia um bug real de orquestração.
- **Cancelled + CloseReservation → exceção genérica `InvalidOperationException`** (mirroring apenas parcialmente `CreateCleaningForReservationCommandHandler`): rejeitada — o usuário exigiu um tipo de exceção específico e não-genérico, para nunca ser confundido com outras falhas estruturais (ex.: Reservation não encontrada) e para permitir escopo de retry customizado futuro exclusivo a este caso, se um dia necessário (hoje, explicitamente, nenhum).
- **`GuestOperations.Api` criado por simetria estrutural com as demais Bounded Contexts**: rejeitada — mirroring a rejeição de `IWorkflowMessageExecutionScope` "por simetria" no ADR-018; nenhum endpoint existe para justificar o projeto agora.
- **Granularidade completa de `CheckInStatus` (6 estados do Documento 10) materializada desde já**: rejeitada — nenhum comportamento deste checkpoint usa os estados intermediários; antecipar estados sem comportamento correspondente violaria o princípio de não inventar regras de negócio ausentes.
- **Promover `GUEST_OPERATIONS:MANAGE`/`READ` e registrar `AddPolicy` já neste checkpoint**: rejeitada — violaria a regra já documentada e estabelecida em `IdentityAuthorizationExtensions`; mirroring o precedente real de `SETTINGS:MANAGE`/`READ` em vez disso.

## Consequências

### Positivas
- Reaproveita integralmente o mecanismo de comando cross-context do ADR-018 (segunda instância, zero infraestrutura nova) e o boundary de execução tenant-safe do ADR-016 (extensão aditiva, nunca duplicação).
- `ReservationStatus.Closed` é aditivo — nenhum comportamento existente de `Confirmed`/`Cancelled` muda.
- A decisão de idempotência/violação de invariante fecha definitivamente o único ponto de ambiguidade genuína deste checkpoint, com precedente e motivo registrados, nunca decidida silenciosamente.

### Riscos Aceitos
- Nenhuma política de retry customizada para `ReservationCancelledCannotBeClosedException` — uma falha genuína de orquestração vai para a dead-letter queue do Wolverine, exigindo investigação manual (decisão deliberada do usuário, não uma lacuna).
- `GUEST_OPERATIONS:MANAGE`/`READ` seedados sem nenhum consumidor real ainda — mirroring `SETTINGS:MANAGE`/`READ`, um padrão já aceito neste codebase, não um risco novo.
- Qualquer futuro terceiro comando cross-context (de Workflow para um outro BC, ou de outro BC's own orchestrator) exige seu próprio ADR — esta decisão não generaliza automaticamente, mesma cláusula já registrada pelo ADR-018.
