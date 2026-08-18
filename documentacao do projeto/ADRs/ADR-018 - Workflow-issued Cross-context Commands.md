# ADR-018 — Workflow-issued Cross-context Commands

Status: Aceito
Data: 2026-08-18

## Contexto

`Architecture Principles.md` §3 já classifica **Workflow Orchestration** como Bounded Context Core ("Motor de Sagas, coordenação de processos multi-etapa"), e §9/§14 já autorizam esse contexto — e somente ele — a enviar comandos (não apenas eventos) a outros contextos, sempre através dos contratos públicos de Application desses contextos, transportados pelo Event Bus. Essa autorização é anterior a esta ADR e não é criada por ela.

A auditoria de Checkpoint 0 da Fase 8 (Workflow Orchestration) confirmou, por leitura direta de todo `*.Contracts` existente, que **nenhum mecanismo concreto de comando cross-context existe hoje** — todo contrato cross-BC é ou um Integration Event no passado (`XCreated`, `XCancelled`...), ou uma das exceções síncronas de leitura já nomeadas (Identity, Configuration, Property Management via ADR-014). Esta ADR define o **primeiro mecanismo concreto** desse tipo, para o único caso aprovado no Checkpoint 1: Workflow Orchestration solicitando a Housekeeping a criação da Cleaning correspondente a uma Reservation recém-criada.

## Decisão

Está aprovado que **Workflow Orchestration pode enviar um comando explícito e público a um Bounded Context alvo**, quando esse BC-alvo expõe um contrato de comando dedicado em seu próprio `*.Contracts`. O primeiro e único caso implementado por esta ADR: `Housekeeping.Contracts.CreateCleaningForReservation`.

A decisão obedece obrigatoriamente a:

1. **Integration Event ≠ Command.** Um Integration Event representa um fato que já aconteceu (passado, imutável, `IntegrationEvent`). Um Cross-context Command representa uma solicitação explícita para o BC-alvo executar uma capacidade sua (intenção, nunca um fato consumado). `CreateCleaningForReservation` **não** herda de `IntegrationEvent` — é um tipo próprio, deliberadamente distinto, transportado pelo mesmo Event Bus (RabbitMQ/Wolverine) mas nunca modelado ou nomeado como um evento passado (ex.: nunca `CleaningCreationRequestedEvent`).
2. **O contrato de comando vive exclusivamente em `<BC-alvo>.Contracts`** — nunca em um projeto compartilhado, nunca em `Workflow.Contracts` (Workflow não publica nada próprio nesta ADR). `Housekeeping.Contracts.CreateCleaningForReservation` é o único caso.
3. **Nenhum command bus genérico, nenhum catálogo genérico de actions.** Não existe `ICommand<T>`/`GenericCommand`/`WorkflowActionCommand<T>`/`CommandDefinition`/`CommandJson` compartilhado. Cada comando cross-context futuro exige seu próprio tipo nomeado, sua própria ADR (mesmo precedente de escopo estrito já usado pela ADR-014 para consultas síncronas).
4. **Ownership permanece exclusivamente do BC-alvo.** Housekeeping continua sendo o único dono de: criação de Cleaning, validação de Property, vínculo Reservation↔Cleaning, status, idempotência de criação, regras de cancelamento. Workflow nunca chama `Cleaning.Create(...)` nem qualquer repositório/DbContext de Housekeeping — apenas envia o contrato público, e a validação de negócio completa roda inteiramente dentro do handler de Housekeeping.
5. **Payload mínimo, nunca PII.** `CreateCleaningForReservation` carrega apenas `TenantId`, `ReservationId`, `PropertyId`, `CorrelationId`, `CausationId` — nenhum nome/telefone de hóspede, nenhum dado financeiro, nenhum payload JSON arbitrário.
6. **Transporte `Send`, nunca `Publish`.** Existe exatamente um destinatário (Housekeeping) — o comando é roteado via `IMessageBus.SendAsync(...)`, nunca `PublishAsync`, e a topologia RabbitMQ usa uma exchange/fila dedicada e nomeada (`workflow-orchestration-commands` → `housekeeping.workflow-commands`), nunca uma exchange genérica de "comandos" compartilhada entre BCs.
7. **Idempotência é responsabilidade do BC-alvo.** Entrega é at-least-once (Inline, sem durable inbox) — Housekeeping garante que a redelivery do mesmo comando nunca cria uma segunda Cleaning (ver Seção "Idempotência" abaixo).
8. **Nenhuma alteração ao boundary de execução tenant-safe existente (ADR-015/016).** Housekeeping precisou de um novo método (`IHousekeepingMessageExecutionScope.ExecuteCommandAsync`), estritamente aditivo, na MESMA e única classe já autorizada a deter `IServiceScopeFactory` — nunca uma segunda classe, nunca uma abstração compartilhada entre contextos. O consumer de Workflow para `ReservationCreated` não precisou de nenhum boundary equivalente (`IWorkflowMessageExecutionScope`), por não tocar nenhum `DbContext` — criar essa classe apenas por simetria foi explicitamente rejeitado (ver Seção "Alternativas Consideradas").
9. **Keyed DI desde o primeiro commit.** `ReservationCreated` já tem consumidores (Housekeeping, Dashboard) no mesmo processo `IHostPro.Worker`. O novo consumidor de Workflow usa `AddKeyedScoped<IIntegrationEventHandler<ReservationCreated>, ...>("workflow")` — nunca registro não-keyed — desde o início, sem esperar por uma regressão real como aconteceu para Dashboard (Fase 7, Checkpoint 1).
10. **`ScheduledAtUtc` nunca é derivado do checkout.** Confirmado por comentário already-existente em `CreateCleaningCommand.cs` (Fase 6): esse gatilho pertence à Fase 10. A Cleaning criada por este fluxo nasce sem horário agendado (`ScheduledAtUtc = null`) — decisão do usuário, Checkpoint 1.
11. **A janela de corrida entre criação e cancelamento é aceita e documentada, não eliminada.** Ver Seção "Riscos Aceitos".

## Idempotência

`housekeeping.cleanings` permite, por desenho já existente (índice não-único documentado em `CleaningConfiguration.cs`), mais de uma Cleaning por Reservation. Este fluxo não pode, portanto, usar "já existe uma Cleaning para este ReservationId" como chave de idempotência sem quebrar esse invariante para criações manuais legítimas.

Decisão do usuário: a chave de idempotência é **"já existe uma Cleaning para este ReservationId com `CreatedByUserId == null`"** (ou seja, já criada por este mesmo fluxo automático) — nunca "qualquer Cleaning para este ReservationId". Reaproveita o campo `Cleaning.CreatedByUserId`, agora nullable (ver Seção "Ator do sistema"), sem exigir nenhuma coluna nova dedicada a proveniência.

Proteção em duas camadas (nunca apenas `AnyAsync` → `Insert`, per decisão do usuário): verificação na Application antes de inserir, mais um índice único parcial no banco — `UNIQUE (tenant_id, reservation_id) WHERE created_by_user_id IS NULL` — que nunca conflita com o índice não-único geral já existente, e nunca impede múltiplas Cleanings manuais para o mesmo Reservation.

## Ator do sistema

`Cleaning.CreatedByUserId` (domínio), `CleaningResult.CreatedByUserId` (Application) e `CleaningDetailResponse.CreatedByUserId` (contrato HTTP público) tornam-se `Guid?`. `CleaningAuditEntry.ActorUserId` (interno, nunca exposto via HTTP) também se torna `Guid?`, para manter consistência com o mesmo princípio. Não existe nenhum "usuário sistema" seedado no Identity — decisão do usuário: em vez de inventar essa identidade, o criador é `null`, seguindo o mesmo precedente já estabelecido para eventos automáticos (`ActorType = "System", ActorId = null`, já usado por `ReservationProjectionAndCancellationReaction` ao publicar `CleaningCancelled` por reação automática).

Mudança aditiva/mecânica — nunca altera o comportamento do fluxo HTTP autenticado existente, que sempre continua populando um `Guid` real.

## Alternativas Consideradas

- **Modelar o comando como um Integration Event** (`CleaningCreationRequestedEvent` ou similar): rejeitada explicitamente pelo usuário (Seção 4 do mandato do Checkpoint 1) — confundiria semanticamente "fato que já aconteceu" com "solicitação de ação", e tornaria o mecanismo indistinguível de qualquer outra reação event-driven já existente no codebase, escondendo a natureza de comando privilegiado que só Workflow Orchestration pode exercer.
- **Command bus genérico / `ICommand<T>` compartilhado entre contextos**: rejeitada — geraria exatamente o tipo de motor de automação genérico que o próprio Documento 17 §34 explicitamente não quer ("o iHostPro não deverá tornar-se uma plataforma BPM genérica").
- **`IWorkflowMessageExecutionScope` criado por simetria com Housekeeping/Reservations/Dashboard**: rejeitada — o consumer de Workflow para `ReservationCreated` não resolve nenhum `DbContext` tenant-aware (é um orquestrador stateless que só lê campos do evento e envia um comando), então o mecanismo ADR-015/016 (cuja única finalidade é isolar a resolução de `ITenantContext` de um `DbContext` reachable do grafo de codegen do Wolverine) simplesmente não se aplica. Criar a classe mesmo assim adicionaria uma segunda classe autorizada a deter `IServiceScopeFactory` em outro contexto sem necessidade real — inconsistente com a própria razão de ser do padrão.
- **Extrair uma abstração de execution-scope compartilhada entre os quatro contextos agora** (Housekeeping/Reservations/Dashboard/Workflow): reavaliada no Checkpoint 0 da Fase 8 e mantida como rejeitada — ADR-016 já previa reavaliar "se um terceiro contexto apresentar a mesma necessidade"; o terceiro (Dashboard) já apareceu e a decisão foi reafirmada como duplicação, não extração, para reduzir blast radius. Sem motivo novo para reverter agora, e de qualquer forma Workflow não precisa desse boundary neste Checkpoint (ver item anterior).
- **Leitura síncrona de Housekeeping para Reservations, para confirmar cancelamento em tempo real**: rejeitada — violaria a lista fechada de exceções síncronas já nomeada em `Architecture Principles.md` §14; qualquer nova exceção desse tipo exigiria sua própria ADR, e o usuário optou pelo guard local best-effort em vez disso.

## Consequências

### Positivas
- Define o primeiro precedente real e testável de comando cross-context, mantendo a superfície mínima e nomeada (um único par Workflow→Housekeeping, um único tipo de comando).
- Reaproveita integralmente a infraestrutura de mensageria (Wolverine/RabbitMQ), o boundary de execução tenant-safe já existente (apenas estendido, nunca duplicado sem necessidade) e o padrão de keyed DI já corrigido na Fase 7.
- Não introduz nenhuma dependência nova de runtime entre Workflow e o schema/DbContext de Housekeeping — a fronteira permanece exclusivamente o contrato público.

### Riscos Aceitos
- **Janela de corrida entre criação e cancelamento**: se uma Reservation for cancelada exatamente entre a publicação de `ReservationCreated` e o processamento do comando por Housekeeping, o guard local best-effort (projeção `reservation_projection.is_cancelled`, atualizada de forma assíncrona e independente pela própria fila de projeção de Housekeeping) pode estar desatualizado no momento da verificação — infraestrutura atual (filas independentes, sem ordenação cross-queue, sem redelivery sob demanda) não permite eliminar essa janela deterministicamente sem uma nova decisão material de ordering/durability (fora do escopo deste Checkpoint). Risco aceito explicitamente pelo usuário, mesma classe de risco de consistência eventual já aceita em ADR-014 (janela de TOCTOU).
- **Nenhum mecanismo de retry/dead-letter dedicado** para o novo comando — usa o comportamento padrão do Wolverine, sem política customizada, mesma decisão já registrada para todo o resto do sistema.
- Qualquer futuro segundo comando cross-context (de Workflow para outro BC, ou de outro BC para um terceiro) exige sua própria ADR — esta decisão não generaliza automaticamente.

## Referências
- `documentacao do projeto/Architecture Principles.md`, Seções 3, 9 e 14 (autorização arquitetural pré-existente para Workflow Orchestration enviar comandos)
- ADR-014 (precedente de exceção estrita e nomeada, nunca genérica)
- ADR-015 (Isolamento do Processamento de Mensagens Housekeeping — mecanismo de execution-scope original)
- ADR-016 (Tenant-safe Execution Boundary — generalização do mecanismo, decisão de manter duplicação por contexto)
- `Fase 8 - Workflow Orchestration - Validacao e Homologacao.md`, Checkpoint 0 (auditoria completa) e Checkpoint 1 (implementação desta ADR)
- `CreateCleaningForReservation.cs`, `CreateCleaningForReservationCommandHandler.cs`, `HousekeepingMessageExecutionScope.cs`
