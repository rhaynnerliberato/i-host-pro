# ADR-024 — Guest Operations Boundary and Checkout Orchestration

Status: **Atualizado** (Checkpoint 2 amendment abaixo; decisões originais do Checkpoint 1 preservadas)
Data original: 2026-08-26
Data desta revisão: 2026-08-27

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

## Amendment — Fase 10, Checkpoint 2 (Check-in/Checkout Core, 2026-08-27)

As decisões do Checkpoint 1 acima permanecem inalteradas. Este amendment registra as decisões arquiteturais do Checkpoint 2, que dá o primeiro comportamento de check-in real e os dois primeiros endpoints HTTP públicos deste Bounded Context. Numeração de ADR: por decisão do Checkpoint 0, `ADR-025` está reservada exclusivamente para o Boundary de PIX Payment (Checkpoint 5) — as decisões abaixo são registradas como emenda a este ADR, nunca como um novo número.

### A1. Gatilho de criação de `GuestStayOperation` — coreografia reagindo a `ReservationCreated`

Confirmado por auditoria real (nenhuma ocorrência de `GuestStayOperation.Create(...)` em código de produção antes deste Checkpoint) que nenhum gatilho de criação existia até aqui. O usuário escolheu explicitamente, entre três alternativas apresentadas (ver "Alternativas Consideradas (Checkpoint 2)" abaixo), a **auto-criação via coreografia**: `ReservationCreatedGuestStayInitializer` (GuestOperations.Application) implementa `IIntegrationEventHandler<ReservationCreated>` e cria um `GuestStayOperation` `Active` na primeira vez que vê uma Reservation — mirroring exatamente `Communication.Application.ReservationCreatedCommunicationProcessor`/`Workflow.Application.ReservationCreatedCleaningOrchestrator`. Nunca um novo comando cross-context, nunca acionado por Workflow Orchestration.

Fluxo: `ReservationCreated` → Guest Operations (`ReservationCreatedGuestStayInitializer`) → `GuestStayOperation` `Active`. Idempotência por busca-antes-de-criar (`IGuestStayOperationReader.GetIdByReservationIdAsync` antes de `GuestStayOperation.Create`), com a restrição única de banco `(TenantId, ReservationId)` (já existente desde o Checkpoint 1) como defesa em profundidade — exatamente um `GuestStayOperation` por Reservation, nunca a única garantia.

`RecordGuestCheckedInCommandHandler` NUNCA cria um `GuestStayOperation` — um check-in para uma Reservation sem `GuestStayOperation` correspondente é `GuestOperationsErrorCodes.GuestStayOperationNotFound`, nunca um auto-create implícito dentro do handler de check-in. A criação é responsabilidade exclusiva da coreografia acima.

Auditoria do banco de desenvolvimento real, executada antes da implementação (condição explícita de parada do usuário): `reservations.reservations` continha 0 linhas e o schema `guest_operations` ainda não existia — nenhum gap de backfill a resolver nesse momento. Reconfirmado na homologação final deste Checkpoint (ver o documento de homologação da Fase 10 para os valores exatos de `ExistingReservationsCount`/`ExistingGuestStayOperationsCount`/`MissingGuestStayOperationsCount`).

### A2. `ReservationCreated` — quinto consumidor em processo, isolado por ADR-020

`ReservationCreated` já tinha quatro consumidores em processo dentro de `IHostPro.Worker` (Housekeeping, Dashboard, Workflow, Communication). Guest Operations é o quinto — registrado com `AddStickyHandler` (ADR-020) desde o primeiro commit, keyed via `GuestOperationsMessageExecutionScope.HandlerKey = "guestoperations"`, nunca resolução não-keyed. Fila própria `guestoperations.reservation-created-trigger`, vinculada à exchange já existente `reservation-events` (mesma routing key `reservation_created`, nenhuma exchange nova) — provisionada exclusivamente por `IHostPro.MigrationRunner`. Os quatro consumidores pré-existentes continuam recebendo o evento sem alteração — cada um sticky-bound à sua própria fila, sem competing-consumer entre Bounded Contexts (mesma garantia que ADR-020 já estabelece para os quatro anteriores). Registrado sem gate de ambiente (ao contrário do consumidor equivalente de Communication): este consumidor não tem distinção fake/real de conector — auto-criar um `GuestStayOperation` local é sempre correto, em qualquer ambiente.

### A3. `GuestStayOperationStatus.CheckedIn` — novo estado intermediário

`Active → CheckedIn → CheckedOut`. `GuestStayOperation.CheckIn(now)` mirra `CheckOut(now)` exatamente: guarda única (`Status != Active` → `InvalidOperationException`), defesa em profundidade — a tradução de "já CheckedIn" em no-op silencioso, e de "CheckedOut" em violação de invariante, é responsabilidade do handler, nunca do método de domínio. `CheckOut(now)`'s própria guarda muda de `Status != Active` para `Status != CheckedIn`: checkout agora exige check-in prévio (decisão do usuário). Um checkout partindo de `Active` (nunca checked in) é `GuestOperationsErrorCodes.GuestStayOperationNotCheckedIn`, uma violação operacional explícita, nunca um silent skip — `Active → CheckedOut` direto é uma transição inválida.

### A4. `GuestCheckedIn` — segundo evento deste Bounded Context

`GuestOperations.Contracts.GuestCheckedIn` (`ReservationId`, `CheckedInAtUtc`) é publicado por `RecordGuestCheckedInCommandHandler` ao transicionar um `GuestStayOperation` para `CheckedIn` — mesma exchange de `GuestCheckedOut` (`guest-operations-events`), uma segunda routing key (`guest_checked_in`), nunca uma segunda exchange. Deliberadamente sem consumidor real ainda: Front Desk (notificação de portaria) permanece na Fase 10, Checkpoint 4; Communication segue o padrão-default explícito de não adicionar nenhum novo consumidor apenas para provar que um existe (decisão do usuário). Ver Documento 07 §31 para payload/roteamento completo.

### A5. Despacho HTTP — convenção Mediator, correção antes de qualquer commit

O desenho inicial deste Checkpoint (antes de qualquer commit) resolvia `RecordGuestCheckedInCommand`/`RecordGuestCheckedOutCommand` por interfaces de handler customizadas (`IRecordGuestCheckedInHandler`/`IRecordGuestCheckedOutHandler`), herdando a forma do Checkpoint 1 — correta então, porque nenhum HTTP existia. Uma auditoria da convenção real do codebase (Reservations/Housekeeping/Dashboard/Configuration/PropertyManagement/Identity/ExternalIntegrations — todo Bounded Context com endpoint HTTP) confirmou que **100% deles** despacham via Mediator (`Mediator.SourceGenerator`, ADR-002): `ICommand<TResponse>`/`ICommandHandler<TCommand,TResponse>` (BuildingBlocks.Application) mais um dispatcher próprio por contexto (`I<Contexto>RequestDispatcher`, necessário porque `Mediator.SourceGenerator` gera um tipo `Mediator.Mediator` distinto por assembly, e os tipos compartilhados `ISender`/`IMediator` ficam ambíguos quando todo Bounded Context se registra no mesmo container, `IHostPro.Api`).

As interfaces de handler customizadas foram removidas antes de qualquer commit; `RecordGuestCheckedInCommand`/`RecordGuestCheckedOutCommand` agora implementam `ICommand<GuestStayOperationResult>`, os handlers implementam `ICommandHandler<TCommand, GuestStayOperationResult>` (método `Handle`, `ValueTask<Result<TValue>>`), e `IGuestOperationsRequestDispatcher`/`GuestOperationsRequestDispatcher` mirram `IReservationsRequestDispatcher`/`ReservationsRequestDispatcher` exatamente. `GuestOperationsCommandDispatchExtensions.AddGuestOperationsCommandDispatch` (Infrastructure, Api-only) mirra `ReservationsCommandDispatchExtensions` exatamente — nenhum `IPipelineBehavior` adicional: cada handler abre sua própria transação via `IGuestOperationsTransactionExecutor` diretamente, mesmo padrão de `CreateReservationCommand`. `GuestStayOperationsController` (novo projeto `GuestOperations.Api`) expõe exatamente dois endpoints: `POST /api/v1/guest-operations/reservations/{reservationId}/check-in` e `.../checkout`.

### A6. Catálogo de permissões — primeiro consumidor real, bundle completo aplicado

`GUEST_OPERATIONS:MANAGE` (não `READ` — nenhum endpoint somente-leitura existe) é promovido a `IdentityPermissionCodes` e registrado em `IdentityAuthorizationExtensions.AddIdentityAuthorization` — o primeiro consumidor real desde que foi seedado no Checkpoint 1 (Seção 7 acima). Aplicado o bundle completo exigido pela regra permanente registrada pelo usuário após o incidente `INTEGRATIONS:MANAGE` (Fase 9): constante + seed (já existente) + grant ADMIN (já existente) + `AddPolicy` (novo) + teste de consistência (novo — `IdentityAuthorizationCatalogConsistencyTests.ControllerAssemblies` estendido com o assembly de `GuestOperations.Api`, descoberto automaticamente pelo mecanismo de reflexão já existente desde a correção da Fase 9).

### A7. Credencial de Acesso — permanece deferida, dentro da Fase 10

Reafirmado sem alteração: `AccessCredentialSecretReference`/`IGuestAccessCredentialProvider`/qualquer entrega de senha/PIN de acesso continuam **DEFERRED PENDING SECURE DELIVERY BOUNDARY** — um sub-gate específico precisa ser aberto e resolvido antes da homologação final da Fase 10 como um todo. Não é um blocker externo/de produção nem uma lacuna esquecida.

### A8. Escopo explicitamente fora deste Checkpoint (reafirmado)

Formulário de check-in (`CheckInFormRequired=false`), Early Check-in/Late Checkout (permanece Checkpoint 3), Portaria/Front Desk (permanece Checkpoint 4, incluindo a notificação de `GuestCheckedIn`), PIX/Payment (permanece Checkpoint 5, e sua própria ADR será `ADR-025` quando aquele Checkpoint iniciar). Communication não ganha nenhum novo consumidor de `GuestCheckedIn`.

### Alternativas Consideradas (Checkpoint 2)

- **Gatilho de criação: auto-criar dentro do próprio handler de check-in**: rejeitada explicitamente pelo usuário — misturaria "registrar um check-in" com "iniciar o ciclo de vida operacional do hóspede", e tornaria a criação dependente de um ator HTTP em vez de um evento de domínio real.
- **Gatilho de criação: comando cross-context de Reservations para Guest Operations**: rejeitada — Reservations nunca deveria precisar saber que Guest Operations existe; coreografia é o padrão já estabelecido por Communication/Workflow/Housekeeping/Dashboard para o mesmo evento.
- **Despacho HTTP: manter as interfaces de handler customizadas do Checkpoint 1**: rejeitada — corrigida antes de qualquer commit assim que a auditoria da convenção real confirmou divergência de 100% do precedente HTTP-exposto do codebase.
- **`GUEST_OPERATIONS:READ` promovido junto com `MANAGE`**: rejeitada — nenhum endpoint somente-leitura existe.
- **Implementar Credencial de Acesso já neste Checkpoint**: rejeitada explicitamente pelo usuário — o modelo de entrega segura ainda não foi decidido.

### Consequências (Checkpoint 2)

**Positivas**: o gatilho de criação fecha definitivamente a lacuna estrutural sinalizada desde o Checkpoint 1 — `GuestStayOperation` agora nasce de um evento de domínio real, nunca de seed manual; a correção do despacho HTTP alinha Guest Operations a 100% do precedente do codebase antes de qualquer commit.

**Riscos aceitos**: Credencial de Acesso permanece um ponto de decisão de segurança em aberto — deve ser resolvido antes da homologação final da Fase 10; `GuestCheckedIn` é publicado sem nenhum consumidor real ainda (Front Desk, Checkpoint 4, é o consumidor esperado).
