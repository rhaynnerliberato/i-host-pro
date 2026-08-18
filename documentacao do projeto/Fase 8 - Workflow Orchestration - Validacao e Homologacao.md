# Fase 8 — Workflow Orchestration — Validação e Homologação

Versão: 1.0 (Checkpoint 0 — Auditoria e Refinamento Read-Only — registrado em §2; Checkpoint 1 — Minimal Workflow Foundation — registrado em §3)

Status: **Checkpoint 0 CONCLUÍDO** (auditoria completa, sem código, cinco decisões materiais resolvidas pelo usuário). **Checkpoint 1 CONCLUÍDO FUNCIONALMENTE** — o primeiro comando cross-context real do codebase (Workflow Orchestration → Housekeeping) implementado, testado (unitário, integração, arquitetura, E2E real via RabbitMQ/Worker/Postgres) e pronto para publicação em `master` pendente do gate final (§4).

---

## 1. Objetivo

Este documento registra a implementação e homologação real da Fase 8 (Workflow Orchestration, per `Plano Executivo de Desenvolvimento por Fases.md`, §3: "Coordenação de eventos e automações entre os contextos"), começando pelo Checkpoint 0 (auditoria read-only) e o Checkpoint 1 (Minimal Workflow Foundation): o primeiro workflow real do catálogo (Documento 17, Workflow 01 — Nova Reserva) implementado de ponta a ponta como um orquestrador stateless que reage a `ReservationCreated` enviando um comando cross-context a Housekeeping solicitando a criação da Cleaning correspondente.

## 2. Checkpoint 0 — Auditoria e Refinamento Read-Only

Auditoria documentária e de código completa (sem nenhuma alteração de código), cobrindo: Documento 17 (Catálogo de Workflows — 20 workflows documentados como diagramas de ação lineares, sem vocabulário formal de "definição"/"instância"/"etapa"; §34 rejeita explicitamente uma plataforma BPM genérica); Documentos 05/06/07/09/12/18 quanto a Workflow; ADRs 003/014/015/016/017; `Architecture Principles.md` §3 (Workflow Orchestration já classificado como BC Core — "Motor de Sagas, coordenação de processos multi-etapa" — decisão anterior a esta Fase, não criada por ela) e §9/§14 (Workflow Orchestration já autorizado arquiteturalmente como o único BC que pode enviar comandos, não apenas consumir eventos, a outros contextos); auditoria real de código confirmando zero padrões de workflow/automação já implementados e zero contratos de comando cross-context existentes em qualquer `*.Contracts`; auditoria da configuração real do Wolverine 6.22.0 (Inline por padrão, sem retry policy customizada, sem scheduling, sem dead-letter queue).

Cinco decisões materiais foram levantadas e resolvidas pelo usuário antes da autorização do Checkpoint 1:

1. Não construir um motor de workflow genérico (Definição/Instância/Etapa/Máquina de Estados) — MVP stateless, gatilho único, ação única.
2. Escopo do primeiro comando cross-context restrito a Workflow → Housekeeping (`CreateCleaningForReservation`), sem barramento genérico.
3. Não extrair uma abstração compartilhada de execution-scope entre os quatro contextos (Housekeeping/Reservations/Dashboard/Workflow) — manter a duplicação já decidida em ADR-016.
4. Nenhum Integration Event próprio de Workflow neste checkpoint.
5. Nenhum código de permissão novo (`WORKFLOW:*`) neste checkpoint — sem API/UI própria.

## 3. Checkpoint 1 — Minimal Workflow Foundation

### 3.1 Escopo

**Incluído**: ADR-018 (decisão arquitetural completa); o contrato `Housekeeping.Contracts.CreateCleaningForReservation` (comando cross-context, nunca um Integration Event); o Bounded Context Workflow Orchestration na sua forma mínima aprovada (um único projeto, `IHostPro.Contexts.Workflow.Infrastructure` — sem Domain/Application/Contracts/Api próprios, já que este checkpoint não possui agregados, não publica nada próprio e não expõe nenhum endpoint HTTP); o consumidor `ReservationCreated` de Workflow, keyed DI, sem `IWorkflowMessageExecutionScope` (justificado no ADR-018 — não há `DbContext` a proteger); o handler `CreateCleaningForReservationCommandHandler` em Housekeeping.Application, com dois guards que o fluxo HTTP não precisa (idempotência e cancelamento best-effort); o método aditivo `IHousekeepingMessageExecutionScope.ExecuteCreateCleaningForReservationAsync`; a topologia RabbitMQ dedicada (`workflow-orchestration-commands`, exchange Direct, fila `housekeeping.workflow-commands`) e a nova fila de gatilho de Workflow (`workflow.reservation-created-trigger`, ligada à exchange `reservation-events` já existente); o índice único parcial (`created_by_user_id IS NULL`) que garante idempotência ao nível do banco; a migração que torna `Cleaning.CreatedByUserId`/`CleaningAuditEntry.ActorUserId` nullable.

**Excluído** (fora deste checkpoint, per mandato do usuário): Workflow 02/03 do catálogo; qualquer API/UI/permissão própria de Workflow; `WorkflowDefinition`/`WorkflowInstance`/máquina de estados/scheduler; retry policy customizada; reação a `ReservationUpdated`/`ReservationCancelled` por parte de Workflow; qualquer segundo comando cross-context.

### 3.2 ADR-018

Ver `documentacao do projeto/ADRs/ADR-018 - Workflow-issued Cross-context Commands.md` para a decisão completa. Resumo dos pontos obrigatórios: um comando cross-context nunca herda `IntegrationEvent` nem é nomeado como um evento passado; o contrato vive exclusivamente em `<BC-alvo>.Contracts` (aqui, `Housekeeping.Contracts`); nenhum barramento de comando genérico; payload mínimo, nunca PII; transporte via `IMessageBus.SendAsync` (não `PublishAsync` — exatamente um destinatário), sobre uma exchange Direct dedicada e nomeada, nunca uma exchange genérica de comandos; idempotência é responsabilidade do BC-alvo, em duas camadas (checagem de aplicação + índice único parcial no banco); `ScheduledAtUtc` nunca é derivado do checkout (esse gatilho pertence à Fase 10); a janela de corrida entre criação e cancelamento é aceita e documentada, não eliminada (risco aceito, ver §3.6).

### 3.3 Idempotência

`housekeeping.cleanings` permite, por desenho já existente antes desta Fase, mais de uma Cleaning por Reservation (índice não único documentado em `CleaningConfiguration.cs`). A chave de idempotência escolhida — decisão do usuário — é "já existe uma Cleaning para este `ReservationId` com `CreatedByUserId == null`", nunca "qualquer Cleaning para este `ReservationId`", preservando o direito de criação manual múltipla. Duas camadas: uma checagem de aplicação (`ICleaningReader.ExistsAutomatedForReservationAsync`, com escopo de transação/RLS próprio) antes de abrir a transação de escrita, e um índice único parcial no banco (`ix_cleanings_tenant_id_reservation_id_automated_unique`, `UNIQUE (tenant_id, reservation_id) WHERE created_by_user_id IS NULL`) como garantia real contra uma corrida entre duas entregas concorrentes do mesmo comando.

### 3.4 Ator do sistema

`Cleaning.CreatedByUserId`, `CleaningResult.CreatedByUserId`, `CleaningDetailResponse.CreatedByUserId` (contrato HTTP público) e `CleaningAuditEntry.ActorUserId` (interno) tornam-se `Guid?`. Decisão do usuário: sem usuário-sistema seedado — o criador automático é `null`, mesmo precedente já usado por `ReservationProjectionAndCancellationReaction` (`ActorType = "System", ActorId = null`) para eventos automáticos. O fluxo HTTP autenticado existente continua sempre populando um `Guid` real — mudança puramente aditiva, sem alteração de comportamento observável para o fluxo manual.

### 3.5 Execution boundary — decisão de não criar `IWorkflowMessageExecutionScope`

O consumidor de `ReservationCreated` em Workflow não resolve nenhum `DbContext` tenant-aware (é um orquestrador stateless que só lê campos do próprio evento e envia um comando) — o mecanismo ADR-015/016 (cuja única finalidade é isolar a resolução de `ITenantContext` de um `DbContext` reachable do grafo de codegen do Wolverine) simplesmente não se aplica aqui. Criar essa classe mesmo assim foi explicitamente rejeitado (Decisão Material 3, Checkpoint 0) — adicionaria uma segunda classe autorizada a deter `IServiceScopeFactory` em outro contexto sem necessidade real.

**Defeito real encontrado e corrigido durante a implementação**: o desenho inicial do adapter Wolverine de Workflow injetava `IServiceProvider` diretamente como parâmetro do método `Handle`, resolvendo o serviço keyed manualmente dentro do método (`serviceProvider.GetRequiredKeyedService<...>(...)`). Um gate real contra Worker (RabbitMQ real) revelou `Wolverine.Configuration.InvalidServiceLocationException` ("Service System.IServiceProvider: Directly using scoped IServiceProvider") — o codegen estrito do Wolverine trata resolução manual de `IServiceProvider` dentro do método como service location não verificada, a mesma classe de restrição já documentada pelo ADR-015/016 para `IServiceScopeFactory`. Corrigido substituindo por injeção via CONSTRUTOR com o atributo padrão `[FromKeyedServices]` do .NET — resolução keyed tratada pelo codegen como um parâmetro de construtor ordinário, sem violar a política de codegen. `ReservationCreatedHandler` passou de `static class` para uma classe de instância comum.

### 3.6 Janela de corrida (cancelamento) — best-effort, aceito

`ReservationProjectionEntry.IsCancelled` (nova coluna) é marcada por `ReservationProjectionAndCancellationReaction` ao processar `ReservationCancelled`. `CreateCleaningForReservationCommandHandler` consulta essa flag (`IReservationReferenceProjection.IsCancelledAsync`) antes de criar a Cleaning. Best-effort, nunca uma garantia — filas RabbitMQ independentes e sem ordenação garantida entre si significam que a flag pode estar desatualizada no instante exato em que o comando é processado. Risco aceito explicitamente (Checkpoint 1, §15-17 do mandato do usuário), mesma classe de risco de consistência eventual já aceita em ADR-014.

### 3.7 Topologia RabbitMQ

- `reservation-events` (exchange Topic já existente, dono: Reservations): nova ligação, `workflow.reservation-created-trigger` ↔ `reservation_created` — terceira fila subscritora independente nesta exchange (Housekeeping e Dashboard já a usavam), Reservations nunca precisa saber que Workflow está ouvindo.
- `workflow-orchestration-commands` (exchange Direct, nova, dono lógico: o próprio comando/par Workflow-Housekeeping): única ligação, `housekeeping.workflow-commands` ↔ `create_cleaning_for_reservation` — nomeação deixa clara a propriedade de Housekeeping como destino, nunca uma exchange genérica de comandos.

Ambas provisionadas exclusivamente por `IHostPro.MigrationRunner`, mesmo padrão de autoridade única já usado por toda a topologia existente. `WebE2EFixture.cs` (fixture de testes E2E do frontend) foi atualizado com a mesma topologia, espelhando o MigrationRunner.

### 3.8 Testes

- **Unitários** (`IHostPro.Contexts.Housekeeping.Tests.Unit`, 6 testes novos): criação com ator/agenda nulos, evento `CleaningCreated` com `ActorType=System`/`ActorId=null`, auditoria sem ator, no-op de idempotência (camada 1), no-op do guard de cancelamento, e a propriedade desconhecida lançando exceção (contando com o redelivery padrão do Wolverine, sem política nova).
- **Integração — adapter Wolverine** (`HousekeepingWolverineAdapterTests`, 1 teste novo, dispatch real via `IMessageBus.InvokeAsync` sem broker): comprova que o sétimo adapter (`CreateCleaningForReservationHandler`) passa o comando intacto e um `MessageId` real, nada além disso.
- **Arquitetura** (`WorkflowOrchestrationArchitectureTests`, 5 testes novos, NetArchTest): `CreateCleaningForReservation` nunca é um `IntegrationEvent`; nenhum outro contexto (Reservations/Dashboard/PropertyManagement/Identity/Configuration) referencia o tipo do comando — testável consequência do ADR-018 de que só Workflow pode enviá-lo; o adapter de Workflow nunca depende de `IServiceScopeFactory`/`IDbContextOutbox`; a assembly de Workflow.Infrastructure não declara nenhum `DbContext`; exatamente dois tipos existem no namespace de mensageria de Workflow (o adapter fino e o handler que ele delega).
- **E2E real** (`CreateCleaningForReservationWorkflowRoundTripTests`, 1 teste, Postgres real + RabbitMQ real + subprocesso real `IHostPro.Worker.dll`, nunca chamando um handler diretamente): `ReservationCreated` publicado através do outbox real de Reservations → consumido pelo `ReservationCreatedHandler` real de Workflow → `CreateCleaningForReservation` enviado através do broker real, na exchange dedicada → consumido pelo `CreateCleaningForReservationHandler` real de Housekeeping → uma `Cleaning` real criada (`Pending`, `CreatedByUserId=null`, `ScheduledAtUtc=null`); isolamento cross-tenant confirmado por leitura RLS-scoped sob outro tenant; idempotência confirmada invocando o handler uma segunda vez, em processo, para a mesma Reservation — nenhuma segunda Cleaning criada.

### 3.9 Defeito pré-existente encontrado, fora de escopo, sinalizado separadamente

Durante a construção do gate E2E real, uma corrida genuína e pré-existente foi descoberta em `PropertyProjectionSynchronizer.UpsertAsync` (Housekeeping): o padrão ler-então-inserir-ou-atualizar não é seguro contra entrega concorrente do mesmo tipo de evento por múltiplas filas subscritoras (`housekeeping.property-projection` e `dashboard.property-projection`, ambas ligadas à mesma exchange `property-management-events`) — publicar `PropertyCreated` e, em seguida, quase imediatamente, `PropertyActivated`, com um Worker real consumindo ambas as filas, produziu `DbUpdateException: 23505: duplicate key value violates unique constraint "PK_property_projection"` de forma repetível. Este defeito é anterior a esta Fase, não foi introduzido por nenhuma mudança do Checkpoint 1, e está fora do escopo de Workflow Orchestration — não foi corrigido aqui. O teste E2E deste checkpoint evita o gatilho semeando a Property diretamente em ambos os armazenamentos (mesma técnica já usada por `ReservationCreatedWorkerRoundTripTests`), preservando o foco no que este checkpoint de fato construiu. Sinalizado como tarefa separada para correção futura.

### 3.10 Escopo de teste explicitamente não coberto neste checkpoint

Um teste dedicado de "cancelar antes de criar" via broker real (`ReservationCreated` seguido imediatamente por `ReservationCancelled`, ambos reais, provando o guard best-effort em ação sob condições de corrida genuínas) não foi implementado nesta rodada — a garantia é best-effort por desenho (§3.6) e a lógica do guard já está coberta por teste unitário; um gate E2E dedicado a essa corrida específica fica registrado como possível extensão futura, não bloqueante para o fechamento funcional deste checkpoint.

## 4. Gate final e publicação

Pendente: regressão completa da solução (build, testes unitários/integração/arquitetura de todos os contextos, `git diff --check`), build Release, build Angular, verificação de determinismo NSwag, e a sequência de publicação (push da feature, fast-forward para `master`, push de `master`, merge de volta na feature). Registrado nesta seção assim que executado.
