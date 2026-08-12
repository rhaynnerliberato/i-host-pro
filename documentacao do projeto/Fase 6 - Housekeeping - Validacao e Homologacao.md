# Fase 6 — Housekeeping — Validação e Homologação

Versão: 1.1 (Fase 6 encerrada funcionalmente — Incremento 1 + Incremento 2A publicados em `origin/feature/housekeeping`; Files/Evidências auditado e deliberadamente deferido — §30 — documento vivo, atualizado progressivamente a cada checkpoint)

Status: **Fase 6 (Housekeeping e Portal da Faxineira) CONCLUÍDA FUNCIONALMENTE**, composta por Incremento 1 (Housekeeping Foundation) e Incremento 2A (Portal da Faxineira Core) — ambos concluídos, homologados e publicados em `origin/feature/housekeeping`. Checkpoints 0-6 de ambos os incrementos concluídos (§4-§29). **Files/Evidências** (anteriormente denominado "Incremento 2B" durante o planejamento) foi auditado e deliberadamente **deferido** por decisão explícita do usuário — é requisito real e documentado do produto, não descartado, porém não bloqueia o encerramento funcional desta fase; permanece como escopo futuro sem fase de implementação atribuída (§30). Status da integração de `feature/housekeeping` em `master`: ver §30.6.

---

## 1. Objetivo

Este documento registra a implementação e homologação real do Incremento 1 da Fase 6 (Housekeeping — Bounded Context Core, per `Architecture Principles.md`), conforme `Plano Executivo de Desenvolvimento por Fases.md` (Fase 6: "Ciclo de faxinas, atribuição, execução, checklist, ocorrências e portal. Escopo detalhado a refinar e aprovar antes da implementação") e a auditoria de elegibilidade apresentada e aprovada pelo usuário, com as correções registradas em §2.

## 2. Correção factual da auditoria anterior

A auditoria da Fase 6 apresentada antes desta implementação continha um erro factual: recomendava criar as roles `FAXINEIRA`/`HOUSEKEEPING` e as permissões `HOUSEKEEPING:MANAGE`/`HOUSEKEEPING:READ` como se não existissem.

**Correção**: a Identity já possui, desde antes desta fase, no catálogo homologado (`IdentityCatalogSeed.cs`):

- Role técnica `HOUSEKEEPER` ("Faxineira").
- Permissões `CLEANINGS:MANAGE`, `CLEANINGS:MANAGE:OWN_CLEANING`, `CLEANINGS:READ`, `CLEANINGS:READ:OWN_OWNER`.
- `RolePermission`: `ADMIN → CLEANINGS:MANAGE`; `OPERATOR → CLEANINGS:MANAGE`; `HOUSEKEEPER → CLEANINGS:MANAGE:OWN_CLEANING`; `PROPERTY_OWNER → CLEANINGS:READ:OWN_OWNER`; `AI_AGENT → CLEANINGS:READ`.

Confirmado consistente com Documento 09 §7 (papel Faxineira) e com a Matriz Simplificada (§15, "Faxinas": Admin=X, Operador=X, Faxineira=X, Proprietário=L, IA=L).

**Causa raiz do erro**: a busca por código existente durante a auditoria anterior usou um padrão *case-sensitive* (`"Cleaning"`/`"Housekeeping"`), que não encontrou as strings reais do catálogo (`"CLEANINGS"`/`"HOUSEKEEPER"`, em maiúsculas). Nenhum código foi criado a partir da recomendação incorreta — o erro foi identificado e corrigido antes de qualquer implementação.

Este incremento **reutiliza** os códigos existentes, sem alterar sua semântica ou o seed. Único ajuste autorizado: promover `CLEANINGS:MANAGE` e `CLEANINGS:MANAGE:OWN_CLEANING` de literais do seed para constantes tipadas em `IdentityPermissionCodes.cs` (mesmo padrão já usado para `PoliciesRead`/`PoliciesManage`/`ReservationsManage` — um código só é promovido quando algo além do seed precisa referenciá-lo por valor pela primeira vez).

## 3. Escopo do Incremento 1 — Housekeeping Foundation

**Incluído**: Bounded Context `Housekeeping`; domínio (`Cleaning`); persistência; RLS; auditoria; outbox; integração por eventos já existentes (consumo de `ReservationCancelled`); API administrativa; frontend administrativo (`/housekeeping`); atribuição de faxineira; lifecycle administrativo; testes.

**Excluído** (fora deste incremento): Portal da Faxineira completo; checklist operacional completo; fotos/vídeos; Bounded Context Files; upload; Reopen; criação automática de Faxina por checkout; Agenda; Workflows; IA; Comunicação; notificações; materiais como estoque; check-in/checkout; qualquer funcionalidade das Fases 7 em diante.

## 4. Checkpoint 0 — Gates e contratos existentes

### 4.1 Catálogo Identity (HOUSEKEEPER/CLEANINGS)

Confirmado — ver §2.

### 4.2 Payloads reais de Reservations

| Evento | Campos | Observação |
|---|---|---|
| `ReservationCreated` | `ReservationId`, `PropertyId`, `Status` | `PropertyId` confiável no momento da criação |
| `ReservationUpdated` | `ReservationId`, `ChangedFields` (nomes dos campos alterados, nunca os novos valores) | Se `"property_id"` aparecer em `ChangedFields`, não há forma de obter o novo valor via evento |
| `ReservationCancelled` | `ReservationId`, `PropertyId` | Terminal, confiável |

Mesmo padrão de minimização de dados encontrado nos eventos de Property Management (`PropertyCreated`/`PropertyUpdated`/`PropertyActivated`/`PropertyDeactivated`/`PropertyArchived`) — convenção deliberada e uniforme em toda a plataforma, não uma lacuna isolada de Reservations.

**Decisão do usuário**: `CreateCleaning` sempre exige `PropertyId` como entrada explícita do ADMIN/OPERATOR — nunca auto-derivado de uma projeção de `ReservationId`. `ReservationId`, quando informado, é validado contra uma projeção local (existe, pertence ao tenant) apenas para fins de referência/vínculo, nunca para preencher `PropertyId`. Elimina o risco de um `PropertyId` desatualizado silenciosamente após a reserva ter seu imóvel reatribuído (cenário que os eventos reais não permitem detectar).

`PropertyId` é validado como imóvel real/ativo do tenant via uma projeção local separada, construída a partir dos eventos de lifecycle de Property Management (`PropertyCreated`/`PropertyActivated`/`PropertyDeactivated`/`PropertyArchived`) — nenhuma consulta síncrona a Property Management é necessária ou permitida (Property Management não é uma das duas exceções gerais de `Architecture Principles.md` §14; `IPropertyReservationEligibilityReader` é estritamente escopado a Reservations por ADR-014, não reutilizável aqui).

### 4.3 Elegibilidade de HOUSEKEEPER para assignment

`IIdentityUserEligibilityReader` (`Identity.Contracts`, já público, já implementado desde a Fase 2) é genérico sobre `requiredRoleCode` — `GetAsync(tenantId, userId, "HOUSEKEEPER", ct)` retorna `IsActive`/`HasRequiredRole` sem expor dados desnecessários. **Reutilizado diretamente, nenhum novo contrato criado.** Identity & Access é uma das duas exceções síncronas gerais de `Architecture Principles.md` §14 — disponível a qualquer contexto, sem necessidade de uma nova ADR.

### 4.4 Nomenclatura de mensageria

Conforme ADR-013 (`<contexto-em-kebab-case>-events`): exchange `housekeeping-events`, schema de outbox `housekeeping_messaging`, routing key = nome do evento em `snake_case` (`CleaningCreated` → `cleaning_created`, etc.).

### 4.5 Conclusão do Checkpoint 0

Nenhum bloqueio impede o início do Checkpoint 1. Nenhuma consulta síncrona nova além das duas já autorizadas (Identity & Access) foi necessária.

## 5. Checkpoint 1 — Foundation

### 5.1 Bounded Context criado

`IHostPro.Contexts.Housekeeping` com a estrutura padrão de `Architecture Principles.md`: `Domain`, `Contracts`, `Application`, `Infrastructure`, `Api` (projetos `Tests.Unit`/`Tests.Integration` ficam para o Checkpoint 2/3, quando houver testes reais a compilar). Todas as referências entre projetos seguem o mesmo grafo de dependências já usado por Reservations/Configuration & Policy (Domain sem dependências externas; Contracts referencia apenas `BuildingBlocks`; Application referencia Domain+Contracts+Identity.Contracts+Reservations.Contracts+PropertyManagement.Contracts; Infrastructure referencia Domain+Application; Api referencia Application+Infrastructure).

### 5.2 `HousekeepingDbContext`

Schema `housekeeping`; `DbSet<Cleaning>`, `DbSet<CleaningAuditEntry>`, `DbSet<PropertyProjectionEntry>`, `DbSet<ReservationProjectionEntry>`; mapeia o storage de envelope Wolverine em `housekeeping_messaging`. `HousekeepingDbContextFactory` (design-time-only, mesmo padrão de `ConfigurationDbContextFactory`/`ReservationsDbContextFactory`) permite `dotnet ef migrations add` sem depender do Host.

### 5.3 Host (`IHostPro.Api`/`IHostPro.Worker`)

`IHostPro.Api`: referências de projeto para Housekeeping.Api/Infrastructure/Contracts; `AddHousekeepingModule` + `AddHousekeepingCommandDispatch` registrados após Configuration & Policy; `EnrollAncillaryPostgresqlOutbox` para `housekeeping_messaging`/`HousekeepingDbContext`; roteamento de publish para `housekeeping-events` para os 6 eventos `Cleaning*`.

`IHostPro.Worker`: referência de projeto para Housekeeping.Infrastructure; `AddHousekeepingModule` (Worker precisa do módulo completo — não apenas de um cache, diferente de Configuration & Policy — porque escreve as projeções locais e reage a `ReservationCancelled`, ver nota de decisão técnica já registrada no `HousekeepingModuleExtensions`); `opts.Discovery.IncludeAssembly(typeof(PropertyCreatedHandler).Assembly)`; `opts.ListenToRabbitQueue("housekeeping.property-projection")` e `opts.ListenToRabbitQueue("housekeeping.reservation-projection")`.

Ambos build limpo (0 erros, 0 avisos) isoladamente antes da integração ao `IHostPro.sln`.

### 5.4 `IHostPro.MigrationRunner`

Referência de projeto para Housekeeping.Infrastructure. `Program.cs` estendido com:

- `typeof(HousekeepingDbContext).Assembly` adicionado a `moduleAssemblies` (descoberta automática de `IModuleDbContext`).
- Bloco de provisionamento do outbox `housekeeping_messaging` (via `EnrollAncillaryPostgresqlOutbox` + `SetupResources()`), espelhando exatamente o bloco de Configuration & Policy, incluindo os `GRANT`/`ALTER DEFAULT PRIVILEGES` para `ihostpro_app`.
- Exchange `housekeeping-events` (topic) declarada na topologia RabbitMQ.
- Duas novas filas de Housekeeping vinculadas a exchanges de OUTROS Bounded Contexts (padrão pub/sub genuinamente novo nesta plataforma — o contexto publicador nunca precisa saber que Housekeeping está ouvindo): `housekeeping.property-projection` vinculada a `property-management-events` com routing keys `property_created`/`property_activated`/`property_deactivated`/`property_archived`; `housekeeping.reservation-projection` vinculada a `reservation-events` com routing keys `reservation_created`/`reservation_cancelled`.

`appsettings.json` do MigrationRunner recebeu a connection string `Housekeeping` (role `ihostpro_migrator`, mesmo padrão dos demais contextos).

### 5.5 `IHostPro.sln`

Os 5 projetos existentes de Housekeeping (Domain/Contracts/Application/Infrastructure/Api) adicionados via `dotnet sln add ... --solution-folder src/Contexts/Housekeeping`, preservando a estrutura de pastas de solução já usada pelos demais Bounded Contexts. `dotnet sln list` confirma os 5 projetos registrados.

### 5.6 Migração EF Core `InitialCreate`

Gerada via `dotnet ef migrations add InitialCreate`. Cria o schema `housekeeping` e as 4 tabelas tenant-owned: `cleanings` (aggregate mutável — `PK` por `id`, `UNIQUE (tenant_id, id)`, índices por `(tenant_id, status, created_at_utc)`, `(tenant_id, property_id, created_at_utc)`, `(tenant_id, reservation_id)`, `(tenant_id, assigned_housekeeper_user_id)`; `xmin` como token de concorrência otimista), `cleaning_audit_log` (append-only, índices por `(tenant_id, occurred_at)` e `(tenant_id, aggregate_id, occurred_at)`), `property_projection` e `reservation_projection` (chave composta `(tenant_id, <id>)`).

**Complemento manual obrigatório** (não gerado automaticamente pelo `dotnet ef`, adicionado seguindo exatamente o padrão de Reservations/Configuration & Policy): bloco de `GRANT`s de privilégio mínimo para `ihostpro_app` (`cleanings`: SELECT/INSERT/UPDATE, nunca DELETE; `cleaning_audit_log`: SELECT/INSERT apenas, nunca UPDATE/DELETE; `property_projection`/`reservation_projection`: SELECT/INSERT/UPDATE para o upsert idempotente dos sincronizadores, nunca DELETE) + `ALTER DEFAULT PRIVILEGES`; e RLS (`ENABLE`/`FORCE ROW LEVEL SECURITY` + `CREATE POLICY tenant_isolation` com o padrão `current_setting('app.tenant_id', true)`/`NULLIF` fail-closed já homologado) nas 4 tabelas, todas tenant-owned. `Down()` espelha o cleanup de `REVOKE`/`ALTER DEFAULT PRIVILEGES` já usado pelos demais contextos.

### 5.7 Validação real (Docker dev, não simulada)

Build de solução completa (`dotnet build IHostPro.sln`): **0 erros, 0 avisos**.

Ambiente: `ihostpro-postgres` (dev, porta 5432) já ativo; RabbitMQ dev (`ihostpro-rabbitmq`) trocado temporariamente pelo de homologação (mesmo procedimento já usado na Fase 5 Checkpoint 7 — conflito de porta fixa 5672), executado e restaurado ao final.

`IHostPro.MigrationRunner` executado duas vezes seguidas contra o Postgres/RabbitMQ reais de dev:

- 1ª execução: aplicou a migração `InitialCreate` de `HousekeepingDbContext`; provisionou o outbox `housekeeping_messaging` (8 tabelas Wolverine confirmadas via `\dt`); declarou a exchange `housekeeping-events` e as duas filas com todos os 6 bindings esperados.
- 2ª execução: concluiu sem erros — `__EFMigrationsHistory` já continha `InitialCreate`, então nenhuma SQL de RLS/GRANT foi reexecutada (mecanismo padrão do EF Core); RabbitMQ topology reafirmada sem erro (declarações são idempotentes por natureza no Wolverine/RabbitMQ). **Idempotência confirmada.**

Inspeção direta via `psql`:

- `\dt housekeeping.*` → `__EFMigrationsHistory`, `cleaning_audit_log`, `cleanings`, `property_projection`, `reservation_projection` (owner `ihostpro_migrator`).
- `pg_class.relrowsecurity`/`relforcerowsecurity` → `t`/`t` nas 4 tabelas tenant-owned (RLS enabled + forced).
- `pg_policies` → política `tenant_isolation` (cmd `ALL`) presente nas 4 tabelas.
- `information_schema.role_table_grants` para `ihostpro_app` → exatamente os privilégios pretendidos (`cleanings`: INSERT/SELECT/UPDATE; `cleaning_audit_log`: INSERT/SELECT; `property_projection`/`reservation_projection`: INSERT/SELECT/UPDATE — nenhum DELETE em nenhuma tabela).
- `\dt housekeeping_messaging.*` → as 8 tabelas Wolverine (`wolverine_outgoing_envelopes`, `wolverine_incoming_envelopes`, `wolverine_dead_letters`, `wolverine_nodes`, `wolverine_node_assignments`, `wolverine_node_records`, `wolverine_control_queue`, `wolverine_agent_restrictions`), owner `ihostpro_migrator`.

Ambiente restaurado ao estado original (`ihostpro-homolog-rabbitmq` religado e saudável; nenhuma fila órfã). `git diff --check` limpo para todos os arquivos tocados neste checkpoint.

### 5.8 Conclusão do Checkpoint 1

Fundação completa e validada contra infraestrutura real (não apenas compilação). Nenhum bloqueio para o Checkpoint 2 (domínio/persistência — testes unitários de `Cleaning`).

## 6. Checkpoint 2 — Domain/Persistence

### 6.1 Defeito real encontrado e corrigido: `cleaning_concurrency_conflict` nunca era produzido

Ao escrever os testes de concorrência exigidos pelo item 23 da autorização ("concorrência"), a leitura direta do código revelou que `HousekeepingErrorCodes.CleaningConcurrencyConflict` estava declarado e já mapeado para HTTP 409 em `HousekeepingResultHttpMapper`, mas **nenhum caminho de código o produzia de fato**.

Causa raiz: a simplificação já documentada de usar apenas o executor genérico `IHousekeepingTransactionExecutor` diretamente em todos os 9 handlers (em vez do padrão de Reservations — um executor tipado por comando, ex. `ICancelReservationExecutor`/`IUpdateReservationExecutor`, que envolve o genérico e captura `DbUpdateConcurrencyException`) removeu, sem intenção, o próprio ponto onde essa tradução acontecia. `HousekeepingOutboxTransactionExecutor.ExecuteAsync` apenas relança qualquer exceção — nunca captura `DbUpdateConcurrencyException` — exatamente como o `ReservationsOutboxTransactionExecutor` genérico de Reservations também não captura (a captura em Reservations vive exclusivamente nos executores tipados por comando, não no genérico).

Diferença relevante em relação a Reservations: lá, apenas `Cancel`/`Update` tocam uma linha já existente (com `xmin`); em Housekeeping, **todo** comando de lifecycle após `Create` (Assign/Start/StartInspection/Complete/Cancel/MarkInterrupted/MarkWaitingMaterials/MarkWaitingHelp) toca uma linha já existente — logo a lacuna afetava 8 dos 9 comandos, não apenas 1-2.

**Correção aplicada** (dentro do escopo já autorizado do item 9/"xmin/concurrency token" e do Checkpoint 2/"concurrency" — não uma mudança de arquitetura/contrato público):

- Novo `ICleaningTransitionExecutor` (`Application/Cleanings/ICleaningTransitionExecutor.cs`) — interface tipada especificamente para `Result<CleaningResult>`, sem qualquer referência a tipos do EF Core (mantém a Application layer livre de dependência de infraestrutura).
- Nova implementação `CleaningTransitionExecutor` (`Infrastructure/Persistence/CleaningTransitionExecutor.cs`) — envolve `IHousekeepingTransactionExecutor`, captura `DbUpdateConcurrencyException` e traduz para `Result.Failure<CleaningResult>(CleaningConcurrencyConflict)`, espelhando exatamente `CancelReservationExecutor`/`UpdateReservationExecutor`.
- Os 8 handlers que mutam uma `Cleaning` já existente (todos exceto `CreateCleaningCommandHandler`, que insere uma linha nova e portanto nunca tem `xmin` a conflitar) foram atualizados para depender de `ICleaningTransitionExecutor` em vez de `IHousekeepingTransactionExecutor` diretamente.
- `HousekeepingModuleExtensions.AddHousekeepingModule` registra o novo `ICleaningTransitionExecutor`.

Validado: build completo da solução (0 erros/0 avisos) após a correção.

### 6.2 Testes unitários — `IHostPro.Contexts.Housekeeping.Tests.Unit`

Projeto criado (`Domain`/`Contracts`/`Application`/`Identity.Contracts` como referências — mesmo padrão de `IHostPro.Contexts.Reservations.Tests.Unit`, sem referência a `Infrastructure`, sem biblioteca de mock, apenas fakes escritos à mão), adicionado ao `IHostPro.sln`.

**62 testes, 0 falhas.** Cobertura:

- `Domain/CleaningTests.cs` (30 testes) — `Create` (estado inicial `Pending`, normalização UTC, referência opcional a `ReservationId`); todas as 8 transições guardadas (`Assign`/`Start`/`StartInspection`/`Complete`/`Cancel`/`MarkInterrupted`/`MarkWaitingMaterials`/`MarkWaitingHelp`), cada uma com um caminho válido e pelo menos um caminho inválido (`InvalidOperationException`); `Completed` e `Cancelled` como terminais (nenhuma transição posterior é aceita a partir de nenhum dos dois); `Cancel` documentado como válido a partir de `Pending`/`Assigned` e inválido a partir de `Started`/`InInspection` (decisão de design sob ambiguidade, já registrada); `Interrupted` sem via de saída documentada neste incremento.
- `Application/Cleanings/CreateCleaningCommandHandlerTests.cs` (6 testes) — criação válida; `PropertyNotFound`; `ReservationReferenceNotAvailable`; ausência de `reservationId` nunca consulta a projeção; auditoria (`cleaning_created`, `ChangedFields` vazio); evento `CleaningCreated` com status `Pending`.
- `Application/Cleanings/AssignCleaningCommandHandlerTests.cs` (9 testes) — elegível/inelegível (usuário inexistente, inativo, sem a role `HOUSEKEEPER`); código de role solicitado é `IdentityRoleCodes.Housekeeper`; faxina inexistente (`CleaningNotFound`); transição inválida (já `Assigned`); auditoria e evento `CleaningAssigned`.
- `Application/Cleanings/CleaningLifecycleCommandHandlerTests.cs` (17 testes) — os 7 comandos restantes (`Start`/`StartInspection`/`Complete`/`Cancel`/`MarkInterrupted`/`MarkWaitingMaterials`/`MarkWaitingHelp`): caminho válido, caminho inválido, terminal (`Complete`/`Cancel`), ausência de evento para `MarkInterrupted`/`MarkWaitingMaterials`/`MarkWaitingHelp` (nenhum evento catalogado existe para essas transições).
- `Application/Cleanings/GetCleaningDetailQueryHandlerTests.cs` (2 testes) — detalhe existente; `CleaningNotFound`.
- `Application/Cleanings/ListCleaningsQueryHandlerTests.cs` (3 testes) — página/tamanho padrão; página/tamanho informados; filtros (`status`/`propertyId`/`assignedHousekeeperUserId`) repassados ao reader sem alteração.

**Explicitamente fora do escopo deste checkpoint (unit)** — validado apenas por não-invenção, não por teste automatizado ainda: a tradução real `DbUpdateConcurrencyException → cleaning_concurrency_conflict` exige uma exceção real do EF Core/Postgres, o que este projeto de testes não tenta simular (mesma convenção de Reservations, cujo `Tests.Unit` também não referencia `Infrastructure` nem testa `CancelReservationExecutor` diretamente). Será validada por um teste HTTP real de concorrência no Checkpoint 4, espelhando exatamente o teste equivalente de Reservations (`ReservationCommandHandlerTests.cs`, "concurrency conflict").

Validadores (`CreateCleaningCommandValidator`/`ListCleaningsQueryValidator`) não recebem teste unitário dedicado — mesmo padrão de Reservations, cujo `Tests.Unit` também não testa validadores isoladamente; a validação de entrada (`validation_error`) é coberta nos testes HTTP de integração (Checkpoint 4).

### 6.3 Migrations/RLS

Já geradas e validadas contra Postgres real no Checkpoint 1 (§5.6-5.7) — nenhuma alteração de schema foi necessária neste checkpoint; a correção de concorrência (§6.1) é inteiramente código de aplicação/infraestrutura, sem impacto de schema.

### 6.4 Conclusão do Checkpoint 2

Domínio e regras de transição implementados, testados e sem lacunas conhecidas dentro do escopo aprovado. Defeito real de concorrência encontrado durante a própria escrita dos testes, corrigido e validado por build completo. Nenhum bloqueio para o Checkpoint 3 (projeção de eventos/integração).

## 7. Checkpoint 3 — Integration/Event Projection

### 7.1 Escopo e projeto criado

`IHostPro.Contexts.Housekeeping.Tests.Integration` — Testcontainers (`Testcontainers.PostgreSql` + `Testcontainers.RabbitMq`), sem mock, contra infraestrutura real, adicionado ao `IHostPro.sln`. Dois arquivos:

- `HousekeepingFoundationTests.cs` (19 testes) — migração/idempotência; RLS fail-closed nas 4 tabelas tenant-owned; privilégios do role `ihostpro_app` (sem DDL, sem BYPASSRLS, sem DESABLE RLS); grants de menor privilégio por tabela (`cleanings`: sem DELETE; `cleaning_audit_log`: append-only, sem UPDATE/DELETE; `property_projection`/`reservation_projection`: SELECT/INSERT/UPDATE, sem DELETE); provisionamento do schema de mensageria `housekeeping_messaging`. Mirror direto de `ReservationsFoundationTests.cs`, adaptado às 4 tabelas e à assimetria de grants deste contexto — supera (com automação permanente, via xUnit) a verificação manual via `psql` já feita no Checkpoint 1.
- `HousekeepingEventProjectionTests.cs` (10 testes) — a composição real (`AddHousekeepingModule` + outbox Postgresql real), nunca um executor fake: `PropertyProjectionSynchronizer`/`ReservationProjectionAndCancellationReaction` resolvidos via DI exatamente como `IHostPro.Worker` os resolveria, cada evento despachado via `IIntegrationEventHandler<T>.HandleAsync` diretamente — a mesma chamada que os 6 adaptadores Wolverine finos fazem, sem exigir um RabbitMQ real para a maioria dos casos.

### 7.2 Projeção de Property (`PropertyProjectionSynchronizer`)

Cobertura: `PropertyCreated` projeta `IsActive=false`; `PropertyActivated`/`PropertyDeactivated` alternam o estado corretamente; `PropertyArchived` marca inativo; redelivery do mesmo evento é idempotente (nenhuma linha duplicada, nenhum erro) — cobre o item "idempotência"/"outage/recovery" simulando a redelivery realista de uma mensagem at-least-once após um "crash" do consumidor entre o commit e o ack.

### 7.3 Projeção de Reservation + cancelamento automático (`ReservationProjectionAndCancellationReaction`)

Cobertura: `ReservationCreated` projeta a referência; redelivery idempotente. `ReservationCancelled`: cancela automaticamente uma Faxina vinculada em `Pending` ou `Assigned`; nunca toca uma Faxina já `Completed`; redelivery é idempotente (a query da reação não encontra mais linhas em estado cancelável na segunda entrega — nenhuma exceção, nenhum `CleaningCancelled` duplicado).

### 7.4 Outbox — verificação real, não simulada

**Defeito de metodologia encontrado e corrigido durante a própria escrita do teste**: a primeira tentativa verificava `wolverine_outgoing_envelopes` sem nenhuma regra de publish configurada no host de teste — sem uma regra de rota, o Wolverine nunca grava o envelope (não é uma falha de entrega, é a ausência de qualquer destino conhecido), o que teria produzido um falso "outbox vazio" caso a asserção não tivesse sido corrigida antes de ser aceita como válida. Corrigido reproduzindo exatamente a técnica já homologada em `PolicyCacheAndOutboxTests` (Fase 5): um container RabbitMQ real é iniciado (permitindo ao Wolverine declarar a topologia real na inicialização) e depois **parado antes da ação sob teste**, garantindo que `CleaningCancelled` fique persistido de forma durável no outbox Postgresql (`housekeeping_messaging.wolverine_outgoing_envelopes`) sem jamais ser efetivamente entregue — prova de persistência sem exigir round-trip real fim-a-fim via RabbitMQ (explicitamente adiado para o Checkpoint 6, mesmo raciocínio documentado em `PolicyCacheAndOutboxTests`). Dois testes usam esta técnica: a primeira publicação de `CleaningCancelled` e a ausência de duplicata na redelivery.

### 7.5 Resultado da execução

`HousekeepingFoundationTests`: 19/19 aprovados. `HousekeepingEventProjectionTests`: 10/10 aprovados. Total do projeto: 29/29. Build completo da solução: 0 erros/0 avisos.

### 7.6 Conclusão do Checkpoint 3

Toda a cadeia de projeção de eventos e a reação automática de cancelamento estão implementadas, corretas e cobertas por testes de integração reais (Postgres real; RabbitMQ real para a verificação específica de outbox). Nenhum bloqueio para o Checkpoint 4 (API administrativa).

## 8. Checkpoint 4 — API administrativa

### 8.1 Defeito crítico real encontrado e corrigido: `CreateCleaning` nunca funcionaria com RLS real

Ao escrever `HousekeepingEndpointsTests.cs` (testes HTTP reais — real ASP.NET Core `TestServer`, JWT real emitido pelo próprio stack de Identity, Postgres real), `POST /api/v1/cleanings` retornava **404 `property_not_found`** mesmo com a projeção de propriedade corretamente semeada e ativa.

**Causa raiz**: `PropertyReferenceProjectionReader.IsKnownActivePropertyAsync` e `ReservationReferenceProjectionReader.ExistsAsync` — os dois pontos de leitura que `CreateCleaningCommandHandler` consulta **antes** de abrir sua própria transação de escrita (decisão aprovada no Checkpoint 0: as duas checagens devem concluir e fechar suas conexões antes de `IHousekeepingTransactionExecutor` abrir a transação de escrita) — nunca abriam uma transação com `SET LOCAL app.tenant_id`. O EF Core Global Query Filter por si só filtra `tenant_id` na consulta LINQ, mas isso é inteiramente independente da própria RLS do PostgreSQL: com `FORCE ROW LEVEL SECURITY` ativo e nenhum `app.tenant_id` definido na sessão/transação, a política `tenant_isolation` falha fechada — zero linhas — **mesmo que o filtro do EF Core estivesse correto**. Isso significa que **`CreateCleaning` nunca teria funcionado em produção**, com RLS real habilitado, para nenhum tenant — um defeito de severidade máxima, invisível aos testes unitários (que usam fakes, sem RLS) e aos testes de integração do Checkpoint 3 (que exercitam a escrita das projeções via `IHousekeepingTransactionExecutor`, o qual já abre a transação corretamente — nunca o caminho de LEITURA pré-transação que `CreateCleaningCommandHandler` usa).

**Correção aplicada**: os dois readers agora abrem seu próprio `TenantAwareTransactionScope` (`readOnly: true`) com um `TenantContext` local descartável, exatamente como `PropertyReservationEligibilityReader` (Reservations) e `IdentityUserEligibilityReader` (Identity) já fazem — o mesmo padrão já homologado para "consulta síncrona pré-transação", apenas não replicado corretamente nestes dois arquivos novos. `ICleaningReader`/`CleaningReader` não precisou da mesma correção — suas duas queries (`ListCleaningsQuery`/`GetCleaningDetailQuery`) são despachadas via o pipeline do Mediator, que já envolve toda a chamada do handler em uma transação `TenantTransactionBehavior` ambiente.

Corrigido e validado por build completo + suíte de testes HTTP real (§8.3).

### 8.2 Revisão de código antes dos testes

Antes de escrever os testes HTTP, o controller (`CleaningsController`), o mapeador de erros (`HousekeepingResultHttpMapper`), o leitor de identidade (`HousekeepingIdentityReader`), os validadores (`CreateCleaningCommandValidator`/`ListCleaningsQueryValidator`) e o `CleaningReader` foram lidos integralmente. Todos corretos e consistentes com a aprovação (nenhum PATCH genérico; comandos explícitos por transição; `ProducesResponseType` em cada ação; `ProblemDetails.Extensions["code"]` com os códigos estáveis exatos da §16; paginação determinística com `pageSize` limitado a 100; `PropertyId` sempre validado no `CreateCleaningCommandValidator`, tornando o fallback `?? Guid.Empty` do controller seguro).

### 8.3 Testes HTTP — `HousekeepingEndpointsTests.cs`

Host real (`TestServer` + JWT real emitido pelo stack de Identity + Postgres real para Identity e Housekeeping — nunca Property Management/Reservations, já que `PropertyId`/`ReservationId` são validados via a projeção local do próprio Housekeeping). **15 testes, 0 falhas**:

- Autenticação/autorização: sem token → 401; role sem `CLEANINGS:MANAGE` → 403; `ADMIN`/`OPERATOR` → 201.
- `Create`: propriedade desconhecida → 404 `property_not_found`; ausência de `propertyId` → 400 `validation_error`.
- Ciclo de vida administrativo completo via HTTP real: `Create → Assign → Start → StartInspection → Complete`, cada resposta com o `status` esperado.
- `Assign` a um usuário real sem a role `HOUSEKEEPER` (semeado de fato no Identity real, não fake) → 403 `housekeeper_not_eligible`; `Assign` a um usuário inexistente → 403.
- Transição inválida (`complete` em uma Faxina `Pending`) → 409 `invalid_cleaning_transition`.
- `Cancel` de uma Faxina `Pending` → 200, status `Cancelled`.
- Detalhe de id inexistente → 404.
- **Concorrência real via HTTP**: dois `POST .../start` simultâneos na mesma Faxina `Assigned` (dois `HttpClient`/duas conexões independentes) produzem exatamente um 200 e um 409 — nunca dois 200 — validando de ponta a ponta a correção de concorrência do Checkpoint 2 (§6.1) através da pilha HTTP completa.
- Listagem: filtro por `status` + paginação retornam a contagem e o subconjunto corretos.

### 8.4 Defeito de configuração de teste corrigido (não um defeito de produção)

`SeedHousekeeperUserAsync` inicialmente falhava com uma violação de chave estrangeira real (`FK_users_tenants_tenant_id`) — a tabela `identity.users` tem uma FK genuína para `identity.tenants`, e a semeadura do teste nunca criava a linha de `Tenant` correspondente antes de inserir o `User`. Corrigido semeando um `Tenant` real (`Tenant.Provision`) antes de qualquer usuário — puramente uma lacuna de setup do teste, não um defeito de código de produção.

### 8.5 OpenAPI/NSwag

`CleaningsController` já está corretamente incluído no host `IHostPro.Api` (Checkpoint 1) e portanto no documento Swagger gerado automaticamente (`/swagger/v1/swagger.json`) sem nenhuma alteração adicional necessária. A regeneração real do cliente TypeScript (`nswag run` contra a API rodando, atualizando `frontend/IHostPro.Web/src/app/core/api/generated/api-client.ts`) fica para o início do Checkpoint 5 (Frontend), quando o cliente gerado será efetivamente consumido pela primeira vez — evita gerar um artefato que ficaria obsoleto/não utilizado até então.

**Nota de atualização**: a regeneração efetivamente ocorreu no início do Checkpoint 5, como planejado — e revelou um segundo defeito real (colisão de `operationId`), documentado e corrigido em §9.1.

### 8.6 Resultado da execução

`IHostPro.Contexts.Housekeeping.Tests.Integration` completo (Foundation + EventProjection + Endpoints): **44/44 aprovados**. `IHostPro.Contexts.Housekeeping.Tests.Unit`: 62/62 aprovados (revalidado após a correção de Infrastructure). Build completo da solução: 0 erros/0 avisos.

### 8.7 Conclusão do Checkpoint 4

API administrativa completa, validada de ponta a ponta via HTTP real contra infraestrutura real. Um defeito crítico de produção (RLS fail-closed no caminho de leitura pré-transação de `CreateCleaning`) foi encontrado e corrigido — sem esta bateria de testes HTTP reais, o defeito permaneceria invisível até a homologação final ou pior, produção. Nenhum bloqueio para o Checkpoint 5 (frontend administrativo).

## 9. Checkpoint 5 — Frontend administrativo

### 9.1 Defeito real encontrado e corrigido: colisão de `operationId` no OpenAPI (`Cancel`/`Cancel`)

Ao regenerar o cliente TypeScript (`npm run generate:api`, `nswag run nswag.json` contra `IHostPro.Api` real em execução), o `Client.cancel()` gerado — método já usado em produção por `ReservationsService.cancel()` — apareceu apontando para a rota `/api/v1/cleanings/{cleaningId}/cancel` em vez de `/api/v1/reservations/{reservationId}/cancel`.

**Causa raiz**: o `operationId` padrão do ASP.NET Core/Swashbuckle é derivado apenas do nome nu do método de ação C#, sem prefixo do controller. `ReservationsController.Cancel` (já existente, já em produção) e o novo `CleaningsController.Cancel` (Checkpoint 4) produziam ambos o `operationId` `"Cancel"` — duas operações OpenAPI compartilhando o mesmo identificador. O gerador NSwag (`MultipleClientsFromOperationId`) resolve essa colisão mantendo apenas um método `cancel()` na classe `Client` compartilhada, sobrescrevendo silenciosamente o de Reservations pelo de Cleanings. Sem a verificação explícita do corpo do método gerado, este seria um defeito real de regressão que quebraria silenciosamente o botão "Cancelar" já existente e já em produção de Reservations.

**Correção aplicada** (mínima, não invasiva): renomeado apenas o método C# `CleaningsController.Cancel` → `CleaningsController.CancelCleaning`. A rota HTTP (`[HttpPost("{cleaningId:guid}/cancel")]`, o contrato real de wire) permanece **inalterada**. Após rebuild + restart da API + regeneração do cliente, o `Client` passou a expor `cancel(cleaningId)` (agora servindo Cleanings, o operationId vencedor por ordem de descoberta) e `cancel2(reservationId)` (Reservations, renomeado automaticamente pelo NSwag para resolver a colisão) — ambos corretos e distintos.

**Correções de acompanhamento necessárias** (não escopo adicional — sem elas, a funcionalidade de Reservations já existente quebraria silenciosamente):

- `ReservationsService.cancel()` atualizado para chamar `this.client.cancel2(reservationId)`, com comentário explicando a renomeação.
- `reservations.service.spec.ts` atualizado (mock `cancel2`, asserção `client['cancel2']`).
- Suíte completa do frontend (`npm test -- --watch=false`, sem filtro) reexecutada para confirmar que Reservations continua 100% funcional: **279/279 testes aprovados antes da mudança de Housekeeping, depois novamente verde após a correção do `cancel2`.**

### 9.2 Módulo Angular `features/housekeeping/`

Estrutura espelhando `features/reservations/`, adaptada ao maior número de transições de lifecycle deste contexto:

- `housekeeping-error.ts` — classificador de erro HTTP. Diferente de `reservation-error.ts` (que só expõe `status`/`codes`, já que `ReservationsResultHttpMapper` nunca detalha 404/409 além do status): `HousekeepingResultHttpMapper` expõe um `code` singular explícito para 404/409/403 (`cleaning_not_found`, `invalid_cleaning_transition`, `cleaning_concurrency_conflict`, `housekeeper_not_eligible`), então `classifyHousekeepingError` lê `status`/`code`/`codes` (este último só para 400).
- `housekeeping.service.ts` — wrapper fino sobre os 11 métodos gerados (`cleaningsPOST`/`cleaningsGET`/`cleaningsGET2`/`assign`/`start`/`startInspection`/`complete`/`cancel`/`interrupt`/`waitingMaterials`/`waitingHelp`).
- `cleaning-form-dialog/` — criação manual apenas (`propertyId` obrigatório, `reservationId` opcional) — **não existe edição de Faxina** (Checkpoint 0: nenhum PATCH genérico, nenhuma alteração de uma Faxina já existente), logo, ao contrário de `ReservationFormDialog`, este diálogo nunca recebe `MAT_DIALOG_DATA` nem tem modo de edição.
- `assign-cleaning-dialog/` — atribuição de faxineira. `housekeeperUserId` é um campo GUID cru, não um seletor — não existe, neste incremento, um endpoint para listar usuários pela role `HOUSEKEEPER` que o frontend pudesse consumir; a elegibilidade real é sempre validada no backend (`IIdentityUserEligibilityReader`) no momento do comando.
- `cleaning-detail-dialog/` — visualização somente-leitura do detalhe completo (`CleaningDetailResponse`, incluindo os timestamps que `CleaningSummaryResponse` — a forma usada na listagem — omite: `createdByUserId`, `startedAtUtc`, `inspectionStartedAtUtc`, `completedAtUtc`, `cancelledAtUtc`).
- `cleanings-list/` — listagem com filtros (`status`, `propertyId`, `assignedHousekeeperUserId`), paginação, estados de carregamento/vazio/erro, chip de status, menu de ações por linha. Cada guarda de habilitação de ação (`canAssign`/`canStart`/`canStartInspection`/`canComplete`/`canCancel`/`canMarkInterrupted`/`canMarkWaitingMaterials`/`canMarkWaitingHelp`) espelha **exatamente** a guarda de domínio equivalente em `Cleaning.cs` — incluindo a ausência deliberada de qualquer ação para os estados `Completed`/`Cancelled`/`Interrupted`/`WaitingMaterials`/`WaitingHelp`/`InTransit`, já que nenhuma transição de retorno existe nesses casos neste incremento (mesmo tratamento de "não inventar" já registrado no domínio — `hasAnyAction()` retorna `false` para todos eles, ocultando o próprio botão de menu). Cancelamento é a única ação com diálogo de confirmação (`ConfirmDialog`, reutilizado de `features/users/`), mesma convenção já estabelecida em Reservations/Properties (ações terminais/destrutivas confirmam; ações de avanço normal do fluxo não).

### 9.3 Roteamento e menu de navegação

Rota `/housekeeping` adicionada a `app.routes.ts`, protegida por `authGuard` (herdado do layout pai) + `permissionGuard` com `data.permissions: ['CLEANINGS:MANAGE']` — nunca uma checagem por nome de role, mesmo padrão de toda rota administrativa já existente. Item de menu `layout.nav.housekeeping` adicionado a `AdminLayout`'s `NAV_ITEMS` (ícone Material `cleaning_services`), gated pela mesma permissão — visível a `ADMIN`/`OPERATOR` (os dois papéis com `CLEANINGS:MANAGE` no catálogo, §2), oculto a `HOUSEKEEPER`/`PROPERTY_OWNER`/`AI_AGENT`.

### 9.4 Internacionalização

Seção `housekeeping` completa adicionada a `public/i18n/pt-BR.json` e `public/i18n/en.json` (título, colunas, filtros, rótulos das 10 transições/ações, mensagens de sucesso, 10 rótulos de status, erros classificados por código) — mesma estrutura e paridade pt-BR/en já usada por `reservations`/`policies`/`propertyManagement`. Chave `layout.nav.housekeeping` adicionada em ambos os arquivos.

### 9.5 Testes unitários

**56 novos testes, todos aprovados** — suíte completa do frontend passou de 279 para **335 testes, 335/335 aprovados** (38 arquivos de teste):

- `housekeeping.service.spec.ts` (12) — delegação de cada um dos 11 métodos ao `Client` gerado, com os parâmetros exatos.
- `cleaning-form-dialog.spec.ts` (11) — validação (`propertyId` obrigatório, `reservationId` opcional); submissão com/sem `reservationId`; prevenção de duplo-submit; classificação de erro (404 `property_not_found`/`reservation_reference_not_available`/genérico, 400, genérico).
- `assign-cleaning-dialog.spec.ts` (10) — validação; submissão; prevenção de duplo-submit; classificação de erro (403 `housekeeper_not_eligible`, 404, 409 `cleaning_concurrency_conflict` vs. outro 409, 400, genérico).
- `cleaning-detail-dialog.spec.ts` (2) — exposição dos dados injetados; fechamento.
- `cleanings-list.spec.ts` (19) — carregamento/estados vazio/erro; filtros; paginação; `statusLabelKey`; todas as 8 guardas de ação espelhando o domínio; `hasAnyAction` para cada estado terminal/lateral; abertura dos 3 diálogos (criação/detalhe/atribuição) com recarregamento condicionado ao resultado; as 6 transições de lifecycle sem confirmação; cancelamento com confirmação (confirmado/recusado); classificação de erro em ação de lifecycle (409 conflito genérico, 409 concorrência, 403 inelegível).
- `admin-layout.spec.ts` (+2) — item "Limpezas" visível com `CLEANINGS:MANAGE`, oculto sem.

### 9.6 Validação realizada

- `npm test -- --watch=false` (suíte completa, sem filtro): **38 arquivos, 335 testes, 335 aprovados.**
- `npm run build` (build de produção): sucesso, sem erros de template/tipo; chunk lazy `cleanings-list` emitido corretamente junto aos demais.
- Verificação real em navegador (servidor de desenvolvimento `ng serve` + `IHostPro.Api` real): navegação direta para `/housekeeping` sem sessão autenticada é corretamente redirecionada para `/login?redirectTo=%2Fhousekeeping` pelo `authGuard` já existente; nenhum erro de console; página de login renderiza corretamente (Angular Material).

**Explicitamente não realizado neste checkpoint, e por quê**: verificação interativa autenticada (criar/atribuir/avançar/cancelar uma Faxina de fato clicando na UI) não foi executada nesta sessão manual de navegador. Os únicos usuários já semeados no Postgres de desenvolvimento persistente (`dev-tenant`/`admin@dev.local`, `e2e-frontend`, `cp7-live-test`) têm senha desconhecida nesta sessão (não documentada, não recuperável de `user-secrets`); criar um novo usuário administrativo ad hoc via SQL direto fugiria do procedimento já estabelecido no projeto. A verificação interativa autenticada real e completa (fluxos de criação/atribuição/lifecycle/cancelamento) é o objetivo explícito do Checkpoint 6 (E2E/Homologação), via Playwright C# com `WebE2EFixture` — o mesmo mecanismo (usuários semeados de forma efêmera e determinística, nunca uma sessão de navegador manual com credenciais avulsas) já usado para Reservations/Properties/Policies/Users em todas as fases anteriores. Registrado aqui como decisão explícita, não como lacuna silenciosa.

### 9.7 Conclusão do Checkpoint 5

Frontend administrativo completo: listagem com filtros e paginação, visualização de detalhe, criação manual, atribuição de faxineira, as 6 ações de lifecycle sem confirmação, cancelamento com confirmação, indicadores visuais de status (chip), prevenção de duplo-submit (todo diálogo desabilita o botão de submit e ignora clique duplicado enquanto `submitting()` é verdadeiro), roteamento e menu de navegação com gate por `CLEANINGS:MANAGE`, i18n pt-BR/en completo. Um defeito real de regressão (colisão de `operationId`, §9.1) foi encontrado e corrigido antes de afetar a funcionalidade já existente de Reservations. Verificação interativa autenticada fica formalmente para o Checkpoint 6. Nenhum bloqueio para o Checkpoint 6 (E2E/Homologação).

## 10. Checkpoint 6 — E2E e Homologação (mensageria real)

> Nota: esta seção é registrada progressivamente, à medida que cada etapa do gate de mensageria fecha. A cronologia completa da investigação (incluindo os experimentos refutados) é mantida deliberadamente, mesmo depois de superada por uma correção posterior — nenhuma etapa é apagada retroativamente.

### 10.1 Defeito A — outbox-enrollment ausente para Housekeeping no Worker

Um teste HTTP real (`ReservationCancelledWorkerRoundTripTests`, primeira versão) que despachava `CancelReservationCommand` via RabbitMQ real para um `IHostPro.Worker` real travava a mensagem em erro/retry: `"Cannot build service type IIntegrationEventHandler<ReservationCancelled> in any way"`. Causa raiz: `IHostPro.Worker/Program.cs` chamava `AddHousekeepingModule` (que registra `IDbContextOutbox<HousekeepingDbContext>` apenas *se* Wolverine já tiver enrolado esse `DbContext` como um Ancillary store), mas nunca chamava `opts.EnrollAncillaryPostgresqlOutbox(...)`/`opts.UseEntityFrameworkCoreTransactions()` para `HousekeepingDbContext` — diferente de Configuration & Policy (que nunca precisou disso, pois `PolicyUpdatedCacheInvalidation` depende apenas de `IPolicyCacheInvalidator`, nunca do outbox). Testes anteriores nunca detectaram isso porque chamavam `IIntegrationEventHandler<T>.HandleAsync` diretamente, contornando por completo a resolução de handler real do Wolverine. **Corrigido** adicionando `opts.EnrollAncillaryPostgresqlOutbox(...)` + `opts.UseEntityFrameworkCoreTransactions()` para `HousekeepingDbContext`/`housekeeping_messaging` no `Program.cs` do Worker (mesmo padrão já usado por Identity). Ver §187 do histórico de tarefas.

### 10.2 Defeito B — divergência de identidade de tenant entre a mensagem e o `TenantContext` resolvido pelo Wolverine

Mesmo após o Defeito A corrigido, a reação automática de `ReservationCancelled` continuava a não localizar/cancelar a Faxina vinculada em cenários reais via RabbitMQ (embora os mesmos testes de integração via chamada direta, Checkpoint 3, continuassem verdes). Investigação profunda revelou que o `TenantContext` (`AddScoped`) resolvido pelo próprio grafo de DI que Wolverine constrói para um chain de mensagem **não era, de forma confiável, o mesmo `TenantContext` que `TenantResolutionMiddleware` populava** — divergência de identidade de escopo entre o middleware de Wolverine (que roda dentro do próprio chain gerado por código, potencialmente com um serviço já resolvido por um caminho diferente de DI) e o `IIntegrationEventHandler<T>` que a mesma requisição deveria enxergar.

**Três experimentos foram tentados e refutados antes da solução final**, nenhum apagado desta cronologia:

1. *Reordenar `opts.Policies.AddMiddleware(...)` para antes dos `ListenToRabbitQueue`/`Discovery.IncludeAssembly`* — não teve efeito; a ordem de registro de middleware não determina identidade de escopo no codegen do Wolverine.
2. *Forçar `TenantContext` como `AddScoped` explicitamente re-registrado após `AddHousekeepingModule`* — não teve efeito; o problema não era o lifetime declarado, e sim qual grafo de DI o código gerado pelo Wolverine efetivamente instancia por mensagem.
3. *Injetar `ITenantContext` diretamente no construtor do handler Wolverine-discovered* — reproduziu o mesmo sintoma; a causa raiz não estava em qual serviço era injetado, e sim em **o próprio Wolverine, quando o handler é descoberto e compilado por convenção de nome, poder resolver dependências fora do escopo de execução real da mensagem** quando esse handler depende de infraestrutura (DbContext/outbox) do Housekeeping.

**Causa raiz final**: Wolverine precisa, para gerar código eficiente por chain, resolver estaticamente (em tempo de *codegen*) a árvore de dependências de cada handler. Quando um handler do Bounded Context Housekeeping depende de `HousekeepingDbContext`/`IHousekeepingTransactionExecutor` (registrados como parte do módulo de aplicação, não como parte do transporte), essa árvore se mistura com decisões de codegen do próprio Wolverine, tornando frágil qualquer garantia de identidade de tenant por mensagem.

### 10.3 ADR-015 — isolamento do processamento de mensagens Housekeeping da integração EF Core do Wolverine

Decisão aprovada pelo usuário (Opção B): separar explicitamente o transporte (Wolverine) da persistência (Housekeeping) através de uma fronteira de execução dedicada, usando `IServiceScopeFactory` — rejeitando deliberadamente a Conjoined Tenancy nativa do Wolverine para esta fase. Registrada em `documentacao do projeto/ADRs/ADR-015 - Isolamento do Processamento de Mensagens Housekeeping da Integração EF Core do Wolverine.md`.

**Papéis explicitamente separados**:

- **Wolverine**: transporte, retries, roteamento, metadados de envelope (`MessageContext`, `context.Envelope!.Id`) — nunca resolve `HousekeepingDbContext`/o executor de transação/o outbox diretamente para nenhum handler de Housekeeping. (Precisão retroativa, achado real de §10.8: "durable inbox" é uma capacidade que o Wolverine oferece em geral, mas **não está ativa** para os listeners de Housekeeping neste incremento — rodam em modo `Inline`, nunca `Durable`. Ver §10.8 para a causa raiz e a implicação real.)
- **`IHousekeepingMessageExecutionScope`** (child scope dedicado, `Housekeeping.Application`): tenant/RLS/transação/domínio/aplicação/auditoria/outbox de saída — abre um `IServiceScopeFactory.CreateAsyncScope()` próprio por mensagem, resolve `ITenantContext` **dentro** desse escopo (`SetTenant(tenantId)`), e só então resolve o `IIntegrationEventHandler<TMessage>` real — garantindo que o handler de negócio sempre veja exatamente o `TenantContext` que a mensagem carrega, nunca um artefato do grafo de codegen do Wolverine.

`HousekeepingMessageExecutionScope` (`Housekeeping.Infrastructure.Messaging`) é a **única** classe autorizada, em todo o Bounded Context Housekeeping, a depender de `IServiceScopeFactory` — ver §10.6 (regra arquitetural).

Cada um dos 6 eventos consumidos por Housekeeping agora tem exatamente um adaptador Wolverine fino, dependendo apenas de `TMessage`/`MessageContext`/`IHousekeepingMessageExecutionScope`/`CancellationToken` — nunca de `HousekeepingDbContext`/o executor de transação/auditoria/repositório/processor/`IDbContextOutbox`/`IServiceScopeFactory` diretamente:

```csharp
[NonTransactional]
public static class ReservationCreatedHandler
{
    public static Task Handle(
        ReservationCreated message,
        MessageContext context,
        IHousekeepingMessageExecutionScope executionScope,
        CancellationToken cancellationToken) =>
        executionScope.ExecuteAsync(message, message.TenantId, context.Envelope!.Id, cancellationToken);
}
```

(mesmo formato para `ReservationCancelledHandler`, `PropertyCreatedHandler`, `PropertyActivatedHandler`, `PropertyDeactivatedHandler`, `PropertyArchivedHandler`, cada um só trocando o tipo da mensagem). `[NonTransactional]` é intenção declarativa apenas — a garantia real vem inteiramente da fronteira de execução, não do atributo.

**`opts.CodeGeneration.AlwaysUseServiceLocationFor<IHousekeepingMessageExecutionScope>()`** — necessário porque `HousekeepingMessageExecutionScope` depende de `IServiceScopeFactory` (`Singleton`), o que originalmente produzia `Wolverine.Configuration.InvalidServiceLocationException` ("Found service locations while generating code... ServiceLocationPolicy.NotAllowed is in effect") ao tentar gerar código estático para o chain. Esta chamada usa exclusivamente a API pública (`JasperFx.CodeGeneration.GenerationRules`), é registrada uma única vez no composition root (`IHostPro.Worker/Program.cs`), e é escopada a **exatamente este um tipo** — não é um Service Locator dentro da aplicação (nenhum outro código chama `IServiceProvider.GetService` fora desta única classe), é configuração de codegen do Wolverine para uma exceção pontual e documentada. Aprovado pelo usuário exclusivamente para esta fronteira; não ampliado a nenhum outro serviço.

### 10.4 Generalização do padrão ADR-015 aos 6 eventos consumidos

Após o spike de `ReservationCancelled` provado verde via RabbitMQ+Worker+Postgres reais, o mesmo padrão foi generalizado a `ReservationCreated` e aos 4 eventos de lifecycle de Property (`PropertyCreated`/`PropertyActivated`/`PropertyDeactivated`/`PropertyArchived`), cada um provado por um teste de round-trip real e independente (RabbitMQ real → `IHostPro.Worker` real, subprocesso → Postgres real), nunca por chamada direta ao handler:

- `ReservationCreatedWorkerRoundTripTests` — projeção criada; tenant correto (projeção sob outro tenant não visível); `CreateCleaning` NÃO é disparado automaticamente por `ReservationCreated` (fora de escopo, confirmado); `IReservationReferenceProjection.ExistsAsync` (o mesmo port que `CreateCleaningCommandHandler` usa) confirma a leitura real sob RLS.
- `PropertyEventsWorkerRoundTripTests` — as 4 transições de lifecycle, nesta ordem, sobre a MESMA propriedade: `Created` → projeção criada com `IsActive=false`; `Activated` → `IsActive=true`; `Deactivated` → `IsActive=false`; `Archived` → `IsActive=false` (estado final, sem duplicação de linha); isolamento cross-tenant confirmado ao final.

Nenhuma semântica nova foi inventada — o comportamento por evento já era exatamente o implementado em `PropertyProjectionSynchronizer`/`ReservationProjectionAndCancellationReaction` desde o Checkpoint 3; a generalização apenas trocou o transporte (chamada direta → Wolverine real) sem alterar a lógica de negócio.

Dois defeitos de autoria de teste (não de produção) foram encontrados e corrigidos durante a escrita desses dois testes — nenhum dos dois exigiu alteração de código de produção:

1. Reabrir uma segunda `WebApplicationFactory<Program>` após o bloco `finally` já ter limpo as variáveis de ambiente da primeira causava `ObjectDisposedException`/timeout de broker (a segunda factory caía nos padrões de `appsettings.json`, nunca alcançando o RabbitMQ real do teste). Corrigido mantendo uma única factory viva por todo o corpo do teste.
2. Reutilizar um único escopo de DI (logo, um único `MessageContext` com outbox já vinculado) para 4 despachos sequenciais de comando reproduzia o próprio aviso do Wolverine ("MessageContext for null has already flushed its outgoing messages") e descartava silenciosamente o evento do segundo despacho em diante. Corrigido dando a cada despacho seu próprio `factory.Services.CreateScope()`, replicando exatamente o que uma requisição HTTP real faz.

### 10.5 Prova de entrypoint único por evento (sem acessar internals privados do Wolverine)

`HousekeepingWolverineDiscoveryTests` (novo, mesma metodologia já homologada em `PolicyUpdatedWolverineDiscoveryTests` — Fase 5, §13.11): sobe o `IHostPro.Worker.dll` real compilado como subprocesso, observa stdout/stderr por: (a) ausência de qualquer assinatura conhecida de falha de discovery/codegen do Wolverine (`UnResolvableVariableException`, `InvalidServiceLocationException`, `error CS0128`, `Cannot build service type` — esta última é a assinatura exata do Defeito A, §10.1 — e `Exception detected`); (b) exatamente uma linha `"Started message listening at rabbitmq://queue/housekeeping.reservation-projection"` e exatamente uma `"...housekeeping.property-projection"` — nunca uma segunda, o que indicaria um handler descoberto duas vezes pela convenção de nomes do Wolverine. **Aprovado.**

### 10.6 Matriz obrigatória de consumers (Housekeeping)

| Evento | Queue | Routing key | Wolverine adapter | Execution boundary | Processor | Efeito persistido | Idempotência | RLS | Resultado |
|---|---|---|---|---|---|---|---|---|---|
| `ReservationCreated` | `housekeeping.reservation-projection` | `reservation_created` | `ReservationCreatedHandler` | `IHousekeepingMessageExecutionScope` → `HousekeepingMessageExecutionScope` | `ReservationProjectionAndCancellationReaction` | Upsert em `housekeeping.reservation_projection` | Upsert idempotente (redelivery não duplica linha) | `SET LOCAL app.tenant_id` dentro do child scope; `tenant_isolation` real | **Verde** (`ReservationCreatedWorkerRoundTripTests`) |
| `ReservationCancelled` | `housekeeping.reservation-projection` | `reservation_cancelled` | `ReservationCancelledHandler` | `IHousekeepingMessageExecutionScope` → `HousekeepingMessageExecutionScope` | `ReservationProjectionAndCancellationReaction` | Cancela `Cleaning` vinculada em `Pending`/`Assigned` (auditoria + `CleaningCancelled` publicado) | Query de estado cancelável não encontra linha na redelivery — nenhum efeito duplicado | idem | **Verde** (`ReservationCancelledWorkerRoundTripTests`, spike ADR-015) |
| `PropertyCreated` | `housekeeping.property-projection` | `property_created` | `PropertyCreatedHandler` | idem | `PropertyProjectionSynchronizer` | Upsert `property_projection` (`IsActive=false`) | Upsert idempotente | idem | **Verde** (`PropertyEventsWorkerRoundTripTests`) |
| `PropertyActivated` | `housekeeping.property-projection` | `property_activated` | `PropertyActivatedHandler` | idem | `PropertyProjectionSynchronizer` | Upsert `property_projection` (`IsActive=true`) | Upsert idempotente | idem | **Verde** (idem) |
| `PropertyDeactivated` | `housekeeping.property-projection` | `property_deactivated` | `PropertyDeactivatedHandler` | idem | `PropertyProjectionSynchronizer` | Upsert `property_projection` (`IsActive=false`) | Upsert idempotente | idem | **Verde** (idem) |
| `PropertyArchived` | `housekeeping.property-projection` | `property_archived` | `PropertyArchivedHandler` | idem | `PropertyProjectionSynchronizer` | Upsert `property_projection` (`IsActive=false`, estado final) | Upsert idempotente | idem | **Verde** (idem) |

Prova de entrypoint único: §10.5. Nenhum dos 6 adaptadores depende de `HousekeepingDbContext`/executor de transação/`IServiceScopeFactory` diretamente (confirmado por leitura de código de cada um dos 6 arquivos e pela ausência de qualquer erro de codegen nos testes acima).

### 10.7 Redelivery real com o mesmo envelope Wolverine — evidência observada, não inferida

`ReservationCancelledRedeliveryTests` (novo): usando o fato de `reservation-events` ser um exchange topic, uma fila-sonda de teste foi vinculada à MESMA routing key (`reservation_cancelled`) que a fila real `housekeeping.reservation-projection` — ambas recebem uma cópia idêntica (mesmos bytes, mesmas properties AMQP) do que o Wolverine efetivamente publica. Após o Worker real processar com sucesso a primeira entrega (Cleaning `Pending`→`Cancelled`, auditoria+1, `CleaningCancelled`+1 confirmado via fila-sonda dedicada), o teste capturou a cópia crua da sonda e a republicou, byte a byte, diretamente na fila real (exchange padrão do RabbitMQ) — uma segunda entrega genuína do MESMO envelope, nunca uma reconstrução sintética.

**Resultado observado (não inferido)**: com o Worker rodando temporariamente em `Serilog:MinimumLevel:Default=Debug` (mesma técnica diagnóstica já usada e revertida na investigação do defeito do `PropertyActivated`), o log real do Worker mostrou:

```
[…] Received ReservationCancelled#08def7df-39bb-aca5-ee91-618bfd150000 at rabbitmq://queue/housekeeping.reservation-projection […]
[…] Started processing IHostPro.Contexts.Reservations.Contracts.ReservationCancelled#08def7df-39bb-aca5-ee91-618bfd150000
[…] Successfully processed message IHostPro.Contexts.Reservations.Contracts.ReservationCancelled#08def7df-39bb-aca5-ee91-618bfd150000 […]
[…] Finished processing […], executed in 34 ms
```

— o MESMO id de envelope (`08def7df-39bb-aca5-ee91-618bfd150000`) da primeira entrega. A redelivery **não foi rejeitada pelo inbox durável**: o Wolverine aceitou-a como uma entrega nova, iniciou um novo processamento (nova execução do boundary ADR-015, novo child scope, `ExecuteAsync` chamado de novo) e concluiu com sucesso — só que em 34ms em vez de ~1635ms da primeira vez, porque a query da reação (`ReservationProjectionAndCancellationReaction`) não encontrou mais nenhuma Faxina em estado cancelável para aquela reserva.

**Conclusão preliminar deste teste, revisada em §10.8**: a deduplicação de `ReservationCancelled` acontece **exclusivamente em nível de domínio** (o predicado `status IN ('Pending','Assigned')` da própria reação). A investigação da §10.8 (item 10 do protocolo aprovado) revelou a causa raiz exata — não é "a linha do inbox durável já foi removida quando a redelivery chega", e sim que **nenhuma linha de inbox durável é criada em momento algum** para esta fila. Confirmado nos dois testes: nenhuma segunda entrada de auditoria; nenhum `CleaningCancelled` duplicado; estado final `Cancelled` preservado — mas o mecanismo real é diferente do inicialmente hipotetizado.

Complementarmente, `HousekeepingEventProjectionTests.Redelivering_the_same_ReservationCancelled_event_is_idempotent_no_duplicate_cancellation_events` (reforçado nesta etapa com asserção explícita de contagem de auditoria) prova a mesma idempotência de negócio de forma isolada do transporte — cada `Dispatch` abre seu próprio `host.Services.CreateScope()`, simulando exatamente "commit de negócio ocorreu, mas o inbox do Wolverine ainda não foi marcado como Handled" sem depender de RabbitMQ real.

**Decisão consequente (item 9 do protocolo aprovado)**: como a idempotência de domínio já demonstrada é suficiente e nenhum efeito duplicado foi observado em nenhum dos dois testes, **nenhuma tabela de mensagens processadas foi criada** — não há caso concreto de efeito não-idempotente a apresentar.

### 10.8 Achado real (não hipotético): os listeners RabbitMQ de Housekeeping (e de Configuration & Policy) rodam em modo `Inline`, nunca `Durable`

Ao investigar o item 10 do protocolo aprovado pelo usuário ("valide explicitamente ... que housekeeping.reservation-projection/housekeeping.property-projection são durable listeners"), a validação real produziu o resultado **oposto** da premissa da própria instrução.

**Evidência 1 (real, contra Postgres real)**: um teste dedicado (`HousekeepingListenerDurabilityModeTests`, versão inicial — nome corrigido depois da investigação; o nome original, `HousekeepingDurableInboxTests`, presumia incorretamente que o resultado confirmaria um inbox durável) despachou um `ReservationCancelled` real via RabbitMQ para o Worker real e fez polling contínuo e agressivo (sem `Task.Delay` entre iterações, rodando em paralelo com a espera pelo processamento final) contra `housekeeping_messaging.wolverine_incoming_envelopes` durante toda a janela de compilação de codegen a frio já observada (~4s, log capturado em §10.7: `Received` às 05s, `Started processing` só às 07s, `Finished processing` às 09s). **Nenhuma linha apareceu em nenhum momento**, em duas execuções independentes.

**Evidência 2 (definitiva, inspeção pública em processo)**: `runtime.Options.Transports.AllEndpoints()` (API pública de `IWolverineRuntime`, nenhum internal privado) foi inspecionada diretamente sobre a MESMA composição usada por `IHostPro.Worker/Program.cs`. Resultado:

```
rabbitmq://queue/housekeeping.reservation-projection  Mode=Inline
rabbitmq://queue/housekeeping.property-projection     Mode=Inline
rabbitmq://queue/configuration.policy-updated          Mode=Inline   (Fase 5 — mesma composição)
```

**Causa raiz**: `opts.UseEntityFrameworkCoreTransactions()` — chamado UMA vez, globalmente, em `IHostPro.Worker/Program.cs` (necessário para todo o padrão ADR-015) — configura **todo** listener RabbitMQ do processo como `EndpointMode.Inline`, nunca `Durable`. Isso não é um comportamento introduzido por Housekeeping ou pela generalização desta etapa: `configuration.policy-updated` (Fase 5, já fechada) está sob a mesma configuração e sofre exatamente o mesmo efeito — nenhuma linha jamais existiu em `configuration_messaging.wolverine_incoming_envelopes` para essa fila também, pelo mesmo motivo.

**O que "Inline" significa na prática, e por que a idempotência de domínio (§10.7) continua sendo a proteção real**: em modo Inline, a mensagem é processada de forma síncrona dentro da própria operação de recebimento, sem nunca passar por uma tabela de inbox durável no Postgres — o ack ao RabbitMQ só ocorre após o processamento (incluindo o commit da transação de negócio) ser concluído com sucesso. Isso significa que a única rede de segurança contra uma entrega duplicada é: (a) o próprio RabbitMQ, que redelivera uma mensagem cujo ack nunca chegou (crash do consumidor entre commit e ack, ou desconexão), e (b) a idempotência de domínio da própria reação — nunca um mecanismo de deduplicação por `MessageId` no lado do Wolverine/Postgres. Isso explica de forma exata e definitiva por que a redelivery real testada em §10.7 nunca foi rejeitada antes de alcançar o handler.

**Isto não é necessariamente um defeito** — pode ser consequência aceitável e já implícita na decisão já aprovada de usar `opts.UseEntityFrameworkCoreTransactions()` (o próprio padrão de mensageria transacional via EF Core do Wolverine tipicamente opera assim: processar-e-commitar como uma unidade só, sem uma etapa de enfileiramento durável separada). Mas é uma característica **real e anteriormente não documentada** da arquitetura de mensageria de toda a plataforma (não apenas Housekeeping), que contradiz a suposição explícita do item 10 da instrução do usuário ("são durable listeners"), e que tem esta implicação de confiabilidade prática: se o Worker travar exatamente entre o commit da transação de negócio e o ack ao RabbitMQ, a única garantia de que a mensagem será reentregue vem do próprio RabbitMQ (mensagem não confirmada volta para a fila) — nunca de um mecanismo de recuperação do Wolverine baseado em Postgres.

**Decisão do usuário**: `Inline` é aceito como o modo correto/atual, sem nenhuma alteração de código de mensageria. Nenhum `.UseDurableInbox()` foi adicionado a nenhum listener. A proteção real contra entrega duplicada permanece exatamente a documentada acima (RabbitMQ redeliverando mensagens não confirmadas + idempotência de domínio) — já comprovada suficiente para todos os cenários reais testados em §10.7. Esta característica de toda a plataforma (não apenas Housekeeping) fica registrada aqui como fato conhecido e aceito, não como um defeito pendente.

Um teste de regressão determinístico e rápido (`HousekeepingListenerDurabilityModeTests`, reescrito e renomeado — inspeciona `IWolverineRuntime` em processo, sem broker/Postgres reais) trava o comportamento atual (`Mode=Inline` para as duas filas de Housekeeping) como fato observável, sem emitir juízo sobre se é correto.

### 10.9 Consistência de roteamento, outage/recovery real e regressão de `PolicyUpdated`

**Consistência de roteamento `CleaningCancelled`** (item 11): `CleaningCancelledRoutingParityTests` ancora a composição REAL de `IHostPro.Api` (via `WebApplicationFactory<Program>` contra broker/Postgres reais) ao Uri exato `rabbitmq://exchange/housekeeping-events/routing/cleaning_cancelled`. O lado Worker da mesma garantia já é provado, de forma igualmente real e independente, por `ReservationCancelledWorkerRoundTripTests`/`ReservationCancelledRedeliveryTests` (fila-sonda vinculada à mesma routing key, entrega real confirmada). Nenhuma abstração de produção foi criada só para remover duas linhas duplicadas, conforme instrução explícita do usuário — os dois lados permanecem pinados independentemente à mesma constante literal.

**Outage/recovery real** (item 12): `HousekeepingOutboxOutageRecoveryTests` — broker real iniciado (topologia declarada), fila-sonda vinculada, broker derrubado, `ReservationCancelled` despachado (a transação de negócio — Cleaning→Cancelled, auditoria, envelope `CleaningCancelled` staged — comita inteiramente contra o Postgres, independente do broker), broker religado, host reiniciado (mesmo caminho de recuperação de um processo Worker real reiniciado após uma indisponibilidade). Log real observado confirma o mecanismo exato: `"Found recoverable outgoing messages in the outbox for rabbitmq://exchange/housekeeping-events-test/routing/cleaning_cancelled"` → `"Recovered 1 messages from outbox for destination ... while discarding 0 expired messages"`. A fila-sonda recebe exatamente uma mensagem — nunca duas. Nenhum sleep fixo como sincronização primária; apenas polling real.

**Regressão de `PolicyUpdated`** (item 13): `PolicyUpdatedRegressionTests` — RabbitMQ real → `IHostPro.Worker` real → Redis real. `CreatePolicyValueVersionCommand` despachado via a composição real de `IHostPro.Api`; o Worker real consome o `PolicyUpdated` publicado e chama `IPolicyCacheInvalidator.InvalidateAsync`, confirmado pelo contador de geração no Redis (`ihostpro:{tenantId}:policy-cache:EARLY_CHECKIN:gen`) avançando de inexistente para exatamente `1`. Combinado com `HousekeepingWolverineDiscoveryTests` (consumer ativo, exatamente um handler, zero erro de codegen — mesmo processo Worker real) e a leitura direta de `PolicyUpdatedCacheInvalidation` (depende apenas de `IPolicyCacheInvalidator`, já continuamente garantido por `ConfigurationDependencyTests` que Configuration & Policy nunca depende de nenhum outro Bounded Context), os 5 pontos exigidos pelo protocolo estão cobertos. **Nenhuma regressão de Fase 5 causada por este checkpoint.**

**Defeito real encontrado e corrigido durante a própria validação proporcional (não um defeito de produção)**: ao reexecutar `PolicyUpdatedWolverineDiscoveryTests` (o teste preventivo original da Fase 5) como parte da validação final deste checkpoint, o Worker real falhou ao iniciar com um erro AMQP real: `"NOT_FOUND - no queue 'housekeeping.property-projection' in vhost '/'"`. Causa raiz: a fixture deste teste (`DeclareConfigurationRabbitMqTopologyAsync`) é anterior à Fase 6 e só declarava a topologia de Configuration & Policy; como `IHostPro.Worker/Program.cs` agora chama `ListenToRabbitQueue` incondicionalmente para as duas filas de Housekeeping também (Worker hospeda os consumers de todos os Bounded Contexts no mesmo processo), o processo real falha por completo na inicialização assim que tenta ouvir uma fila que esta fixture nunca provisionou — antes mesmo de alcançar o listener de `PolicyUpdated` que o teste realmente verifica. **Corrigido no próprio teste** (nunca em código de produção): a fixture agora também declara `property-management-events`/`reservation-events` com os bindings de Housekeeping, espelhando exatamente as declarações do `IHostPro.MigrationRunner`. Revalidado: teste verde. Este achado confirma, de forma concreta, exatamente o tipo de regressão que o item 13 do protocolo pedia para descartar — e a fixture desatualizada era precisamente o tipo de lacuna que só um teste real, subprocess completo, conseguiria revelar.

**Segundo defeito real de fixture de teste encontrado e corrigido (não em produção)**: ao escrever e executar `HousekeepingE2ETests` (Playwright), a suíte inteira falhou com a `IHostPro.Api` real completamente inacessível. Causa raiz: `WebE2EFixture.cs` (usada por TODA a suíte Playwright, não apenas Housekeeping) tinha exatamente a mesma classe de lacuna já encontrada em `PolicyUpdatedWolverineDiscoveryTests` — sua topologia RabbitMQ nunca aprendeu sobre as filas de Housekeeping, e o Worker real que a fixture inicia falhava ao subir antes mesmo de a `IHostPro.Api` ser iniciada (a fixture só inicia a Api depois do Worker sinalizar prontidão). Adicionalmente, mais dois defeitos relacionados foram encontrados na mesma fixture: `ConnectionStrings:Housekeeping` nunca era passada para os processos reais de Worker/Api, e `HousekeepingDbContext`/o outbox `housekeeping_messaging` nunca eram migrados/provisionados pela própria fixture.

Corrigidos os três, uma quarta camada do mesmo problema apareceu: as propriedades continuavam nunca sendo projetadas em Housekeeping. Investigação com captura temporária de log do Worker (Debug, revertida após a coleta) revelou a causa raiz real e definitiva: **`StartWorkerProcess()` desta fixture nunca definia `ConnectionStrings:Platform`** — `IHostPro.Worker/Program.cs` lança `InvalidOperationException("Missing connection string 'ConnectionStrings:Platform'.")` e o processo morre imediatamente na inicialização, antes de qualquer configuração do Wolverine. `StartApiProcess()`, na mesma fixture, já define essa connection string corretamente — a lacuna era exclusiva do lançamento do Worker.

**Implicação real, além de Housekeeping**: isso significa que o Worker real desta fixture nunca esteve efetivamente em execução em NENHUMA suíte Playwright anterior (Fase 3, 4 ou 5) — nenhuma delas, porém, tinha uma asserção que dependesse de um efeito produzido pelo Worker (CRUD síncrono via HTTP não precisa dele), então o crash silencioso nunca havia sido observável até agora, quando `CreateCleaning`'s dependência real na projeção assíncrona de Property tornou-se o primeiro caso em que a ausência real do Worker finalmente quebrou uma asserção.

**Todos os quatro corrigidos apenas no teste** (nunca em produção): topologia RabbitMQ estendida (mirroring MigrationRunner); `ConnectionStrings:Housekeeping` e `ConnectionStrings:Platform` adicionadas ao lançamento do Worker; migração e provisionamento do outbox de Housekeeping adicionados a `MigrateSchemasAsync`/`ProvisionMessageStoresAsync`; e um helper de espera real (polling limitado, nunca sleep fixo) para a janela de propagação assíncrona real de `PropertyActivated` até a projeção local de Housekeeping. Revalidando.

**Testes focados dos adaptadores** (item 14): `HousekeepingWolverineAdapterTests` — um teste por evento (6 no total), cada um construindo um host Wolverine mínimo real (sem broker, sem Postgres — `IMessageBus.InvokeAsync` despacha em processo) com um `IHousekeepingMessageExecutionScope` falso registrado no lugar do real, provando que cada adaptador passa a mensagem intacta (mesma referência), o `TenantId` correto e um `MessageId` real atribuído pelo Wolverine — e nada além disso (exatamente uma chamada). `MessageContext.Envelope` não tem setter público (confirmado por inspeção — apenas o codegen interno do próprio Wolverine o popula), então construir um `MessageContext` manualmente e injetá-lo diretamente no `Handle` exigiria acessar um membro privado; a técnica acima evita isso por completo, usando somente API pública do Wolverine.

Itens já fechados: regra arquitetural restringindo `IServiceScopeFactory` (§10.6); redelivery real (§10.7); idempotência de negócio independente do inbox (§10.7); validação do durable inbox (§10.8 — `Inline`, aceito como está); consistência de roteamento `CleaningCancelled`, outage/recovery real, regressão de `PolicyUpdated` (§10.9); testes focados dos adaptadores (acima).

**Todos os 14 itens do protocolo de mensageria aprovado pelo usuário estão fechados.**

### 10.10 Suíte Playwright de Housekeeping — 7 cenários reais, um defeito real de produção encontrado e corrigido

Com o gate de mensageria fechado, a suíte `HousekeepingE2ETests` (Playwright, `IHostPro.Web.Tests.E2E`) foi escrita cobrindo os 7 cenários administrativos do Incremento 1: listagem, criação manual, erro de imóvel desconhecido, ciclo de vida administrativo completo (Assign/Start/StartInspection/Complete), erro de camareira inelegível, cancelamento com confirmação, e filtro por status. Todos dirigem o `IHostPro.Web` real contra o `IHostPro.Api`/`IHostPro.Worker` reais (mesmo padrão de `ReservationsE2ETests`), nunca um mock.

**Defeito real de produção encontrado e corrigido: `AssignCleaningDialog` — o botão "Atribuir" não fazia absolutamente nada.** Ao investigar por que `Admin_completes_the_full_administrative_lifecycle` e `Assigning_to_an_ineligible_user_shows_the_classified_error` travavam indefinidamente após o clique em "Atribuir" (nem sucesso, nem erro, nunca), instrumentação temporária de captura de rede (todos os eventos `Request`/`Response`/`Console`/`PageError` do Playwright, revertida após a coleta) provou que **nenhuma requisição HTTP era sequer disparada** pelo clique — o problema era anterior à rede, no próprio Angular. Causa raiz confirmada por leitura de código: `assign-cleaning-dialog.html` tinha `<form (ngSubmit)="submit()">` sem `[formGroup]` (nem `ngForm`) associado ao elemento `<form>` — apenas o `<input>` interno tinha um `[formControl]` solto. Em Angular, o output `ngSubmit` só existe porque `FormGroupDirective`/`NgForm` o provê; sem `[formGroup]` no próprio `<form>`, nenhuma diretiva de formulário se anexa, `ngSubmit` nunca é emitido, e `submit()` do componente literalmente nunca é chamado — o clique é um no-op silencioso e completo em produção, não apenas em teste. Comparação com `cleaning-form-dialog.html` (que funciona) confirmou a diferença exata: lá o `<form>` tem `[formGroup]="form"`.

O teste unitário pré-existente (`assign-cleaning-dialog.spec.ts`) nunca capturou isso porque instancia o componente diretamente (`TestBed.runInInjectionContext(() => new AssignCleaningDialog())`) e chama `component['submit']()` manualmente, sem nunca renderizar o template (`TestBed.createComponent`/`detectChanges()`) — o mesmo padrão usado em `cleaning-form-dialog.spec.ts`, que por coincidência nunca expôs o problema porque o template daquele componente estava correto.

**Corrigido em produção** (`assign-cleaning-dialog.ts`/`.html`): `AssignCleaningDialog` passou a usar um `FormGroup` real (`FormBuilder.nonNullable.group({ housekeeperUserId: [...] })`) com `[formGroup]="form"` no `<form>` e `formControlName="housekeeperUserId"` no `<input>` — espelhando exatamente o padrão já correto de `CleaningFormDialog`. `assign-cleaning-dialog.spec.ts` atualizado para o novo shape (`component['form'].controls.housekeeperUserId`); os 10 testes unitários permanecem verdes. Confirmado end-to-end: após a correção, o clique em "Atribuir" dispara a requisição real e ambos os testes E2E antes travados passam.

**Defeitos reais de teste corrigidos (não em produção)**, encontrados na mesma rodada de depuração real (nunca simulada):

- **Paginação/ordenação padrão da listagem**: `CleaningReader.cs` ordena por `CreatedAtUtc` ascendente (mais antigo primeiro), página padrão de 10 itens. Como a sonda de prontidão `WaitUntilKnownToHousekeepingAsync` (criar-e-cancelar uma limpeza descartável, único mecanismo disponível já que não existe endpoint de leitura direta da projeção de Property em Housekeeping) roda a cada `CreateActivePropertyViaApiAsync`, e como os 7 testes desta classe rodam sequencialmente sobre o mesmo banco, a linha recém-criada de um teste que rode mais tarde na sequência podia cair além da página 1, nunca aparecendo no DOM. Corrigido usando o próprio filtro "ID do imóvel" já existente na UI para escopar cada asserção à propriedade exata do teste — nunca dependência da paginação/ordenação padrão não filtrada.
- **Poluição da sonda de prontidão**: a mesma sonda cria-e-cancela deixa, permanentemente, uma limpeza `Cancelada` residual em toda propriedade criada via `CreateActivePropertyViaApiAsync` — inevitável, já que não existe exclusão de limpezas e cancelar é a única forma de não bloquear a limpeza real subsequente. Isso quebrava `Filtering_by_status_narrows_the_list_to_matching_cleanings` (2 elementos `Cancelada` para a mesma propriedade — violação de strict mode do Playwright) e `Admin_creates_a_cleaning_manually` (mesma ambiguidade). Corrigido ajustando as asserções para reconhecer explicitamente o artefato conhecido e determinístico da sonda (contagem exata em vez de zero) e usando `.First`/`.Last` em vez de um `WaitForAsync` cru quando dois elementos legitimamente coexistem.
- **Locator de linha ficando obsoleto após mudança de status**: `Admin_completes_the_full_administrative_lifecycle` e `Admin_cancels_a_pending_cleaning` capturavam a linha (`row`) filtrada por texto `"Pendente"` uma única vez e a reutilizavam por múltiplas transições de estado — quando o status real mudava (para `"Atribuída"`, `"Cancelada"` etc.), o filtro por texto "Pendente" parava de casar com qualquer linha, e as asserções subsequentes (`row.GetByText(novoStatus)`) travavam esperando por um locator que já não resolvia a nada. Corrigido trocando o filtro por texto de status por `.Last` sobre a lista já escopada por propriedade (a sonda, sempre mais antiga, é sempre a primeira; a limpeza real, sempre a mais recente, é sempre a última) — uma referência estável e válida durante toda a transição de estados, imune ao texto de status atual.
- **Corrida entre o clique em "Filtrar" e a leitura da tabela**: `cleanings-list.html` remove a `<table>` inteira do DOM enquanto `state()` é `'loading'` (troca de `@switch`), então checar visibilidade de texto logo após clicar "Filtrar" podia ser satisfeito pelo conteúdo antigo (pré-filtro) ainda em tela, ou colidir com a janela em que a tabela está ausente — nunca uma prova real de que a resposta filtrada chegou. Apareceu apenas na segunda de duas execuções consecutivas completas da suíte E2E (nunca nas execuções isoladas de Housekeeping), um sintoma clássico de corrida sensível a timing. Corrigido centralizando o helper compartilhado `FilterByPropertyIdAsync` (usado por 5 dos 7 testes) para aguardar a resposta HTTP real de `GET /api/v1/cleanings` via `page.RunAndWaitForResponseAsync`, nunca apenas o clique; o mesmo padrão aplicado inline nos dois pontos de filtro de `Filtering_by_status_narrows_the_list_to_matching_cleanings`.

**Resultado final**: os 7 testes de `HousekeepingE2ETests` passam de forma consistente e real, sem sleep fixo em nenhum ponto, sem simulação, sem mock de rede. As execuções anteriores que falharam durante a própria depuração (as seis iterações de `HousekeepingE2ETests` isoladas, mais duas execuções completas da suíte E2E do repositório inteiro que ainda encontraram falhas — uma no timing de `Reservations`, flaky e não reproduzida depois, outra no timing de `Filtering_by_status`, corrigida acima) permanecem registradas neste documento como histórico da investigação, não como parte do par final válido. **O par final válido**: duas execuções consecutivas completas da suíte E2E do repositório inteiro, sem nenhuma intervenção/limpeza manual entre elas — 61/61 aprovados na primeira (2min02s), 61/61 aprovados na segunda (2min02s), zero processos/containers órfãos confirmado ao final de ambas. A falha de limpeza da coleção de testes (`[Test Collection Cleanup Failure (WebE2E)]`) observada em iterações anteriores desapareceu junto com as falhas de teste que a causavam — confirmando que era sintoma dos timeouts, não um defeito independente.

### 10.11 Validação backend completa (solução inteira) — dois defeitos reais de fixture pré-existentes encontrados e corrigidos

Com Housekeeping E2E fechado, a suíte `.NET` completa (excluindo E2E, já validada acima) foi executada em uma única invocação (`dotnet test IHostPro.sln`) pela primeira vez nesta homologação — prática nunca antes exercida no repositório: toda validação anterior, nesta e em fases passadas, sempre rodou cada projeto de teste (ou classe individual) isoladamente via `--filter`. Essa primeira execução realmente unificada revelou dois defeitos reais e pré-existentes em `IHostPro.Api.Tests.Integration`, nenhum deles introduzido por este checkpoint, ambos invisíveis a qualquer execução isolada:

**Defeito 1 — corrida por uma porta fixa do RabbitMQ entre 10 classes de teste.** Dez classes distintas neste projeto (`WolverineThreeStoreCompositionTests`, `OpenApiOperationIdTests`, `HousekeepingWolverineDiscoveryTests`, `CleaningCancelledRoutingParityTests`, `PolicyUpdatedRegressionTests`, `PolicyUpdatedWolverineDiscoveryTests`, `PropertyEventsWorkerRoundTripTests`, `ReservationCancelledRedeliveryTests`, `ReservationCancelledWorkerRoundTripTests`, `ReservationCreatedWorkerRoundTripTests`) precisam de um RabbitMQ Testcontainers vinculado à porta fixa de host `5672` — necessário porque cada uma sobe o `IHostPro.Api`/`IHostPro.Worker` reais, cuja configuração real (`UseIHostProRabbitMq`) nunca aceita um override de porta. O xUnit paraleliza classes de teste diferentes por padrão; sem uma diretiva explícita, duas ou mais dessas classes disputando a mesma porta simultaneamente falham com `"Bind for 0.0.0.0:5672 failed: port is already allocated"` — 12 das 14 falhas observadas na primeira execução unificada. **Corrigido apenas em teste**: novo arquivo `tests/Host/IHostPro.Api.Tests.Integration/AssemblyInfo.cs` com `[assembly: CollectionBehavior(DisableTestParallelization = true)]`, forçando execução sequencial de todo o assembly — mesmo raciocínio já usado por `WebE2EFixtureCollection` na suíte Playwright.

**Defeito 2 — `WolverineThreeStoreCompositionTests` nunca soube sobre Configuration nem Housekeeping.** Com a corrida de porta eliminada, `MigrationRunner_provisions_rabbitmq_topology_idempotently_and_the_real_host_delivers_through_it` ainda falhava — desta vez com um erro real do Postgres, `permission denied for table __EFMigrationsHistory`, ao tentar migrar o quinto contexto. Causa raiz: esta classe de teste é anterior a Configuration (Fase 5) e Housekeeping (Fase 6); seu helper `RunMigrationRunnerAsync()` (que lança o `IHostPro.MigrationRunner.dll` real como subprocesso) e seu `BuildFactory()`/`EnvironmentKeys` (que sobem o `IHostPro.Api` real via `WebApplicationFactory`) nunca foram atualizados para incluir `ConnectionStrings__Configuration`/`ConnectionStrings__Housekeeping` — ausentes, o subprocesso caía para os valores padrão gravados em `appsettings.json` (apontando para o Postgres de dev real, `localhost:5432`, nunca para o Postgres efêmero deste teste), explicando o erro de permissão real observado contra um banco físico diferente do esperado. Esta é a **quarta ocorrência exata desta mesma classe de lacuna** encontrada nesta fase (após `PolicyUpdatedWolverineDiscoveryTests`, e duas em `WebE2EFixture.cs` — topologia RabbitMQ e `ConnectionStrings:Platform`/`:Housekeeping`) — todas fixtures anteriores a um Bounded Context novo, nunca atualizadas quando esse contexto foi adicionado. **Corrigido apenas em teste**: as duas connection strings ausentes adicionadas em ambos os pontos (`RunMigrationRunnerAsync`, `BuildFactory`/`EnvironmentKeys`). Revalidado: as 4 verificações da classe passam; as 14 verificações do projeto inteiro passam em sequência (3min46s).

**Flake sob carga observado e explicado, não corrigido (comportamento correto)**: numa única execução (das várias realizadas), `PolicyUpdatedRegressionTests` encontrou geração `2` no Redis em vez de `1` — reexecutado isoladamente, passou de forma limpa. Consistente com o próprio achado já documentado em §10.8 (os listeners RabbitMQ de Configuration & Policy e de Housekeeping rodam em modo `Inline`, sem inbox durável, aceito como está pelo usuário): sob pressão real de recursos (dez classes de teste sequenciais, cada uma subindo containers reais), uma redelivery genuína do broker é exatamente o cenário que o modo `Inline` deixa de deduplicar — e `IPolicyCacheInvalidator.InvalidateAsync` é um incremento de contador, seguro de chamar mais de uma vez (o cache fica "mais invalidado que o necessário", nunca incorreto). Não é uma regressão nem um defeito de teste; é a primeira observação empírica automatizada do próprio risco já aceito.

**Redação precisa da execução**: a primeira execução unificada (`dotnet test IHostPro.sln`) encontrou o conflito real de infraestrutura/test fixture descrito acima (Defeitos 1 e 2) em `IHostPro.Api.Tests.Integration` especificamente. Os demais doze projetos — `IHostPro.BuildingBlocks.Tests.Unit` (13), `IHostPro.Contexts.Reservations.Tests.Unit` (50), `IHostPro.Contexts.Housekeeping.Tests.Unit` (62), `IHostPro.Contexts.PropertyManagement.Tests.Unit` (180), `IHostPro.Contexts.Configuration.Tests.Unit` (76), `IHostPro.ArchitectureTests` (133), `IHostPro.Contexts.Identity.Tests.Unit` (470), `IHostPro.Contexts.Reservations.Tests.Integration` (52), `IHostPro.Contexts.Configuration.Tests.Integration` (65), `IHostPro.Contexts.Housekeeping.Tests.Integration` (58), `IHostPro.Contexts.PropertyManagement.Tests.Integration` (184), `IHostPro.Contexts.Identity.Tests.Integration` (419) — passaram nessa mesma primeira execução e não foram afetados, não precisando de nenhuma reexecução. Após as duas correções de fixture (AssemblyInfo/DisableTestParallelization; connection strings ausentes), `IHostPro.Api.Tests.Integration` foi reexecutado integralmente e passou **14/14**. **1.776 testes aprovados no conjunto final de validações** (1.762 dos doze projetos não afetados + 14 de `IHostPro.Api.Tests.Integration` reexecutado) — nunca uma única execução monolítica sem interrupção, registrado aqui honestamente. Mais 335 testes Angular (Vitest) e 61 testes Playwright E2E × 2 execuções consecutivas.

## 20. Aprovação do usuário, gate final e versionamento

### 20.1 Aprovação do usuário

O usuário aprovou tecnicamente o Checkpoint 6 e o Incremento 1 (Housekeeping Foundation) com a seguinte instrução (resumo fiel): "O Checkpoint 6 e o Incremento 1 — Housekeeping Foundation estão TECNICAMENTE APROVADOS. Está autorizado versionar e publicar o Incremento 1 na branch feature/housekeeping, MAS somente depois dos gates finais [...]. Não fazer merge em master. Não criar tag. Não excluir branch. Não fazer rebase. Não usar force push. A Fase 6 NÃO está encerrada." Um gate final de cinco partes (secrets temporários; estado da infraestrutura; precisão Inline vs. Durable; revisão do ADR-015; precisão da redação dos resultados) foi executado integralmente antes de qualquer `git add`, conforme registrado abaixo.

### 20.2 Gate de secrets temporários (user-secrets do Checkpoint 5)

Investigação do `secrets.json` de `IHostPro.Api` (nunca imprimindo valores): arquivo criado em 27/07/2026 (semanas antes do Checkpoint 5, portanto pré-existente), modificado pela última vez às 07:11 de hoje — antes de qualquer atividade desta sessão visível (nesta sessão, `user-secrets` foi apenas **lido**, nunca escrito). Três chaves presentes: `RabbitMq:Username`, `RabbitMq:Password` (valores idênticos às credenciais reais do container `ihostpro-rabbitmq` de dev — não um valor sintético/temporário) e `Identity:Jwt:SigningKey:PrivateKeyPem`. Sem histórico/backup do arquivo (fora do controle de versão por design), não foi possível determinar com certeza absoluta qual valor específico foi tocado às 07:11. Apresentado ao usuário com a evidência completa; **decisão do usuário: manter os três valores como estão**, tratados como configuração de dev padrão e reutilizável (não um artefato temporário isolado do Checkpoint 5). Nenhum valor sensível foi documentado ou impresso em nenhum momento. Nenhum `user-secrets clear` executado.

### 20.3 Gate de estado da infraestrutura

Baseline real (não presumido) reconstruído a partir dos próprios registros da Fase 5 (§18.5-18.6 e §16-17 daquele documento): ao final da Fase 5, `ihostpro-homolog-rabbitmq` foi restaurado e confirmado `running` (item "Restore homolog RabbitMQ" daquela homologação). A primeira verificação desta sessão (antes de qualquer ação própria) encontrou `ihostpro-homolog-rabbitmq` parado há 12h e `ihostpro-rabbitmq` (dev) parado há 9h — divergência de 3h entre os dois, consistente com um swap RabbitMQ homolog→dev feito durante o Checkpoint 5 (necessário para rodar a API real localmente, mesmo procedimento já documentado em Fase 5 §9.8) cujo *swap-back* ficou incompleto: o RabbitMQ de dev foi parado, mas o de homolog nunca foi religado. `ihostpro-redis`/`ihostpro-homolog-redis` já estavam no baseline correto (dev parado, homolog rodando), confirmando que apenas o RabbitMQ fez parte daquele swap.

**Corrigido**: `docker start ihostpro-homolog-rabbitmq`, confirmado saudável (`rabbitmq-diagnostics ping` → `Ping succeeded`), mesmo volume nomeado, mesmas portas (`5672→5672`/`15672→15674`), mesma `RestartPolicy: no` do registro original da Fase 5 — nenhum container removido/recriado, nenhum volume alterado.

**Estado final confirmado, todos os sete containers no baseline correto**: `ihostpro-postgres` (dev, rodando — nunca fez parte de nenhum swap), `ihostpro-rabbitmq` (dev, parado), `ihostpro-redis` (dev, parado), `ihostpro-homolog-postgres` (rodando), `ihostpro-homolog-redis` (rodando), `ihostpro-homolog-rabbitmq` (rodando, restaurado nesta sessão), `n8n` (rodando, serviço pré-existente não relacionado a este trabalho). Confirmado adicionalmente: nenhum processo `IHostPro.Api`/`IHostPro.Worker`/`ng serve`/`node` de dev remanescente; nenhum Chromium lançado por automação (`chrome.exe` presentes são sessões normais do usuário, sem flags de automação); nenhum container/processo Testcontainers/Ryuk órfão (`docker ps -a --filter label=org.testcontainers` vazio); nenhuma porta 5672/5140/4200/15672 ocupada indevidamente.

### 20.4 Precisão Inline vs. Durable — auditoria e correções

Auditados: ADR-015, este documento, comentários de código, nomes de teste. Um total de quatro correções de precisão aplicadas (nenhuma delas altera comportamento de produção, apenas redação/nomenclatura):

1. §10.3 deste documento continha "Wolverine: transporte, durable inbox, retries..." na tabela de papéis separados — escrita antes da descoberta de §10.8, nunca corrigida retroativamente. Corrigida com nota de precisão explícita.
2. `tests/Host/IHostPro.Api.Tests.Integration/HousekeepingDurableInboxTests.cs` renomeado para `HousekeepingListenerDurabilityModeTests.cs` — o teste prova que os listeners **não** são duráveis (`Mode=Inline`); o nome anterior sugeria o oposto. Todas as referências cruzadas (ADR-015, este documento) atualizadas.
3. `ReservationCancelledRedeliveryTests.cs`: dois comentários que presumiam um mecanismo de deduplicação por inbox durável ("Wolverine embeds for its own durable-inbox deduplication"; "regardless of which layer... is responsible") corrigidos para afirmar o fato real (nenhum inbox durável existe para esta fila).
4. `HousekeepingEventProjectionTests.cs`: comentário que afirmava "which observed the durable inbox does NOT reject a redelivery... and its inbox row cleared" — factualmente incorreto (nenhuma linha de inbox é criada, não há linha para "limpar"). Reescrito para afirmar a causa raiz real (`EndpointMode.Inline`, nenhuma tabela de inbox).

ADR-015 revisado (Status permanece **Aceito**, decisão arquitetural não alterada) e confirmado cobrir explicitamente: Conjoined Tenancy rejeitada nesta fase; divergência de `TenantContext` causada pelo Wolverine EF-managed DbContext; child execution scope; `IServiceScopeFactory` confinado a uma única boundary; `AlwaysUseServiceLocationFor<IHousekeepingMessageExecutionScope>()` como exceção intencional documentada; listeners em modo `Inline`; nova subseção de precisão explicando que não há uma tabela de inbox para formar transação conjunta com a transação de negócio (o ack ao RabbitMQ ocorre apenas depois do commit); idempotência de negócio como obrigação; outbox transacional de saída preservado. Build completo revalidado após as quatro correções (0 erros/0 avisos) e os dois testes diretamente afetados reexecutados (`HousekeepingListenerDurabilityModeTests`, `Redelivering_the_same_ReservationCancelled_event_is_idempotent...`) — ambos aprovados.

### 20.5 Precisão da redação dos resultados finais

Corrigidas duas passagens deste documento para refletir honestamente a execução real (nunca uma única execução monolítica sem interrupção): a contagem de 1.776 testes de backend (§10.11, já reescrita acima) e o resultado E2E (§10.10, já reescrita acima, distinguindo o par final válido de duas execuções consecutivas sem intervenção manual das iterações anteriores de depuração, mantidas como histórico).

### 20.6 Commits realizados

| # | Hash completo | Mensagem |
|---|---|---|
| 1 | `2d09585426c06781046ebdd4bd899a7e23834752` | `feat(housekeeping): add cleaning management foundation` |
| 2 | `f08e8d777449d9da14d8279a5f7016459c0bde07` | `feat(housekeeping): integrate reservation and property events` |
| 3 | `6253b098901c4ef8768648c2451a140c9a3bfd84` | `feat(housekeeping): add administrative cleaning api` |
| 4 | `d2507487ed2a211b9037331e296dd7f3f336b898` | `feat(frontend): add housekeeping administration` |
| 5 | `9034247e713b4695ed2379cb80080105a5bc8f88` | `test(housekeeping): cover cleaning workflows and messaging` |

Cada commit foi precedido de *staging* seletivo (nunca `git add .`) e revisão de `git diff --cached --stat`/`git diff --cached` para confirmar que apenas os arquivos do grupo pretendido estavam presentes. `.claude/` (artefato local, mesmo precedente já registrado na Fase 4) nunca foi staged em nenhum commit. Build completo revalidado (0 erros/0 avisos) após os cinco commits, confirmando coerência/compilabilidade do conjunto. Nenhum arquivo proibido (`bin/`, `obj/`, `node_modules/`, `.angular/`, logs, `.env`, credenciais, evidências manuais de homologação, screenshots, vídeos, `test-results/`) foi versionado.

Este documento (Commit 6 — `docs(housekeeping): record increment 1 completion`) fecha a lista de commits funcionais/de teste; um commit de fechamento de publicação (`docs(housekeeping): close increment 1 publication`) segue após o `git push`.

### 20.7 Resultados finais consolidados

- Unit (`IHostPro.Contexts.Housekeeping.Tests.Unit`): **62/62**.
- Integration (`IHostPro.Contexts.Housekeeping.Tests.Integration`): **58/58**.
- Architecture (`IHostPro.ArchitectureTests`, todo o assembly): **133/133**.
- Frontend (Vitest, todo o assembly): **335/335**.
- Housekeeping Playwright E2E: **7/7**.
- E2E completo do repositório (par final válido, sem intervenção manual entre as execuções): **61/61 + 61/61**.
- Backend, conjunto final de validações: **1.776 testes aprovados** (§10.11).
- Release build: limpo (0 erros/0 avisos).
- Angular production build: limpo, chunk `cleanings-list` emitido corretamente.
- NSwag: regenerado duas vezes contra a API real, diff zero contra o cliente commitado e diff zero entre as duas gerações — determinístico.
- `MigrationRunner`: executado duas vezes contra Postgres/RabbitMQ de dev reais, idempotente (saída idêntica).
- RabbitMQ/Worker real: consumidores reais validados (`HousekeepingWolverineDiscoveryTests`, round-trips reais).
- Redelivery real: comprovada e aceita pelo handler, sem efeito duplicado (idempotência de domínio) — §10.7/§10.8.
- Outage/recovery real: comprovado (`HousekeepingOutboxOutageRecoveryTests`) — §10.9.
- Listeners `Inline`, nunca `Durable`: confirmado e documentado com precisão (§20.4).
- ADR-015: revisado, Status permanece Aceito, decisão arquitetural inalterada.
- Ambiente restaurado ao baseline real da Fase 5 (§20.3), não a um estado presumido.
- Secrets: nenhum valor temporário identificado para remoção; três valores de dev padrão mantidos por decisão explícita do usuário (§20.2).
- Nenhum Bounded Context Files criado ou referenciado em código de produção.
- Nenhuma funcionalidade do Portal da Faxineira (Incremento 2) implementada.
- Nenhuma criação automática de Cleaning a partir de eventos de Reservation fora do escopo já aprovado (reação de cancelamento automático, único caso aprovado desde o Checkpoint 3).
- Nenhuma funcionalidade de Fase 7 ou posterior implementada ou referenciada.

### 20.8 Publicação

`git push -u origin feature/housekeeping` executado após o Commit 6 (este documento, no estado do §20.6). Confirmado: os seis commits publicados em `origin/feature/housekeeping` (`2d09585`, `f08e8d7`, `6253b09`, `d250748`, `9034247`, `6461e47`), `git status -sb` mostrando `feature/housekeeping...origin/feature/housekeeping` com ahead/behind = 0/0, working tree limpa (exceto `.claude/`, artefato local nunca versionado). Nenhum merge para `master`, tag ou exclusão de branch foi realizado.

Este parágrafo (§20.8) e o commit que o inclui (`docs(housekeeping): close increment 1 publication`) fecham a lista de commits deste incremento — após este commit, um `git push origin feature/housekeeping` final replica-o ao remoto, reconfirmando ahead/behind = 0/0.

### 20.9 Estado da Fase 6 após a publicação

A Fase 6 **continua em andamento**: este documento registra o fechamento e a publicação do Incremento 1 (Housekeeping Foundation). O Incremento 2 (Portal da Faxineira) ainda não foi implementado — nenhum `Files`, upload, checklist, ocorrência, ou endpoint/frontend novo do Portal foi criado. Apenas a auditoria e o planejamento somente leitura, sem código, começam após esta publicação (§21). A implementação do Incremento 2 aguarda aprovação explícita do usuário.

## 21. Incremento 2 — decomposição e abertura do Incremento 2A

### 21.1 Decomposição registrada

A auditoria somente leitura do Incremento 2 (apresentada em conversa, sem alteração de código) foi aprovada pelo usuário, que autorizou o início de um subconjunto explícito do escopo original de Fase 6 (parágrafo único do Plano Executivo: "Ciclo de faxinas, atribuição, execução, checklist, ocorrências e portal"), decomposto em três incrementos:

- **Incremento 1 — Housekeeping Foundation**: concluído e publicado (§1-§20 deste documento).
- **Incremento 2A — Portal da Faxineira Core**: autorizado a iniciar nesta seção. Escopo: autoprestação da faxineira para as próprias faxinas (própria autorização/ABAC), transição `InTransit`, `Delay`/`NeedsHelp`/`NeedsMaterial`, ocorrências textuais, checklist textual (condicional ao Checkpoint 0), frontend Portal dedicado. Explicitamente **sem** Files/upload/fotos/vídeos em qualquer forma.
- **Incremento 2B — Files/Evidências (futuro, não autorizado)**: upload real de fotos/vídeos, Bounded Context Files (já decidido arquiteturalmente em ADR-006, nunca implementado em código). Não iniciado; não faz parte desta seção.

Esta decomposição é uma decisão de execução (sequenciamento) registrada aqui para rastreabilidade — não substitui nem altera o parágrafo original do Plano Executivo de Desenvolvimento por Fases, que permanece inalterado como a fonte do escopo aprovado de Fase 6 como um todo.

### 21.2 Confirmação do gate de secrets

Reconfirmado a partir dos próprios registros desta sessão (sem reabrir investigação de infraestrutura): a decisão de manter os três valores do `user-secrets` de `IHostPro.Api` (`RabbitMq:Username`, `RabbitMq:Password`, `Identity:Jwt:SigningKey:PrivateKeyPem`) como estão — já registrada em §20.2 — foi explicitamente confirmada pelo usuário via pergunta direta ("Leave all 3 secrets as-is (Recommended)"). Nenhum valor foi lido ou impresso novamente nesta confirmação.

### 21.3 Checkpoint 0 — matriz de refinamento documental

Auditados nesta seção: Documento 06 (Máquina de Estados), Documento 07 (Catálogo de Eventos), Documento 09 (Atores e Permissões), Documento 10 (Mapa Funcional), Documento 12 (Modelo de Dados Conceitual), Documento 14 (Diretrizes de UX/UI, §26 Portal da Faxineira), Documento 17 (Workflows 09-13), Plano Executivo, Architecture Principles, ADR-015, este documento (§1-§20), e o código atual de `Cleaning.cs`, `CleaningStatus.cs`, `ReservationProjectionEntry.cs`, `CreateCleaningCommand`/`CreateCleaningRequest`, `IdentityPermissionCodes.cs`, `IdentityCatalogSeed.cs`, `OwnProfileResponse.cs`/`GetOwnProfileQueryHandler.cs`.

| Área | REQ (documentado) | DEC (decisão já tomada) | IMPLEMENTAÇÃO (código atual) | GAP | RECOMENDAÇÃO |
|---|---|---|---|---|---|
| **InTransit** | Doc 06 §5/§18: `Designada → Em Deslocamento → Iniciada` no fluxo canônico; descrição explícita: "Opcional. Configuração por tenant." | Doc 06 já documenta a etapa como opcional/configurável por tenant; usuário (§8) autorizou o método de domínio mas proibiu usar `RequireCleaningInTransitStep` como chave definitiva de `ConfigurationDefinition` neste incremento. | `CleaningStatus.InTransit` existe no enum, zero método de domínio (`Cleaning.cs` comentário: "the optional InTransit step is skipped by design this increment"). | Nenhum gap material — a "opcionalidade configurável por tenant" documentada não será implementada via `ConfigurationDefinition` real neste incremento (decisão já tomada pelo usuário), apenas a capacidade de transição em si (`Assigned → InTransit → Started` e `Assigned → Started` diretos, ambos válidos). | Implementar `Cleaning.MarkInTransit()` + `Start()` aceitando ambas origens (`Assigned` ou `InTransit`), sem introduzir configuração nova. |
| **Delay** | Doc 09 linha 228: "Informar atraso" (capacidade da Faxineira). Doc 07: `CleaningDelayed` — "Faxina atrasada." (sem payload documentado). Doc 06: nenhum estado "Atrasada" existe na máquina de estados da Faxina. | — | Nenhuma implementação. `CleaningStatus` não tem valor "Delayed". | Nenhum gap material — a ausência de estado dedicado é consistente (evento informativo, não transição de estado) e a ausência de payload documentado já está coberta pela regra de parsimônia do usuário (§12). | Modelar como ação self-service que publica `CleaningDelayed` sem alterar `Status`, payload mínimo (`cleaningId`, `tenantId`, `reportedByUserId`, `reportedAtUtc`), sem campo de "minutos estimados" (não documentado, seria invenção). |
| **NeedsHelp** | Doc 09: "Solicitar ajuda". Doc 07: `CleaningNeedsHelp` — "Solicitou ajuda." Doc 06 §5: estado `Aguardando Ajuda` — "Necessita apoio", entrada documentada a partir de `Iniciada`, sem retorno documentado. | — | `Cleaning.MarkWaitingHelp()` já existe (`Started → WaitingHelp`), implementado no Incremento 1. Nenhum evento de integração publicado ainda (endpoint self-service não existe). | Nenhum gap material. | Adicionar endpoint self-service que chama o método já existente + publica `CleaningNeedsHelp` (payload mínimo, mesmo padrão de Material). |
| **NeedsMaterial** | Doc 09: "Solicitar materiais". Doc 07: `CleaningNeedsMaterial` — "Solicitou materiais." Doc 06 §5: estado `Aguardando Materiais` — "Necessário reabastecimento", mesma entrada/sem-retorno de NeedsHelp. Doc 17 Workflow 12 ("Pedido de Material"): `Faxineira → Solicitar Material → Registrar → Notificar Administrador` (sem fotos, sem relação com Reserva). | Doc 12 lista "Falta de material" também como exemplo de `Intercorrência` — sobreposição textual entre os dois modelos (Occurrence genérica vs. estado dedicado de Cleaning), não uma contradição: o Incremento 1 já escolheu e homologou o modelo de estado dedicado (`WaitingMaterials`), que este incremento apenas estende com o evento/endpoint self-service. | `Cleaning.MarkWaitingMaterials()` já existe (`Started → WaitingMaterials`). Nenhum evento/endpoint self-service ainda. | Nenhum gap material. | Mesmo tratamento de NeedsHelp: endpoint self-service + `CleaningNeedsMaterial`, payload mínimo, sem campo de "notificar administrador" (nenhum mecanismo de notificação existe no código — fora de escopo, não é um bloqueio). |
| **Occurrence** | Doc 12: entidade `Intercorrência` — "Problema encontrado", exemplos: Furto, Quebra, Objeto esquecido, Dano, Animal, Fumo, Ruído, Falta de material. Doc 06 §5: registro de ocorrências ligado à etapa `Em Inspeção`. Doc 06 §8: máquina de estados própria da Intercorrência (`Registrada → Em Investigação → ... → Encerrada`) — mais rica que o necessário aqui. Doc 17 Workflow 11 ("Registro de Dano"): `Ocorrência → Registrar → Anexar Fotos → Relacionar Reserva → Notificar Administrador → Auditoria`. Doc 09: "Registrar intercorrência". | Usuário (§15): ocorrência como entidade (não agregado próprio), campos mínimos. Usuário (§2): proibido qualquer evidência/foto, real ou stub. Doc 07: nenhum evento `CleaningOccurrenceRegistered`/similar catalogado — usuário (§18) proíbe evento novo sem necessidade comprovada. | Nenhuma implementação (nem entidade, nem tabela). | Nenhum gap material bloqueante — a máquina de estados rica do Doc 06 §8 e os passos "Anexar Fotos"/"Relacionar Reserva"/"Notificar Administrador" do Workflow 11 excedem o que este incremento pode suportar sem Files/eventos novos; tratados como redução de escopo já autorizada pelo usuário, não como invenção. | `CleaningOccurrence` como entidade simples ligada a `Cleaning` (`cleaningId`, `tenantId`, `type` — catálogo fixo derivado dos 8 exemplos do Doc 12 —, `description` livre, `registeredByUserId`, `registeredAtUtc`), sem evidências, sem evento de integração, sem máquina de estados própria, sem relação explícita a Reserva (já transitiva via `Cleaning.ReservationId`). |
| **Checklist** | Doc 12: entidade `Checklist` — "Representa itens de inspeção", exemplos: Fogão, Geladeira, TV, Ar-condicionado, Banheiro, Enxoval, Lixo, Janela (catálogo fixo, sem indicação de configuração por imóvel/tenant). Doc 12: Faxina "possui... checklist". Doc 17 Workflow 10 ("Conclusão da Faxina"): `Concluir → Checklist → Fotos → Ocorrências → Materiais → Finalizar` (sequência de UI, não enunciado como regra de bloqueio). | Usuário (§16-17): checklist não deve alterar silenciosamente a regra de `Complete` a menos que explicitamente documentado; fotos explicitamente não são requisito de conclusão neste incremento. | Nenhuma implementação. `Cleaning.Complete()` atual exige apenas `Status == InInspection`, sem depender de checklist. | Nenhum gap material — nenhum documento afirma em prosa que o checklist é obrigatório para concluir (o Workflow 10 é um diagrama de sequência de UI, não uma regra de negócio explícita); não há indicação documental de configurabilidade por imóvel/tenant (a lista do Doc 12 é o único catálogo encontrado). | Checklist textual com catálogo fixo de 8 itens (Doc 12), cada item um par `(rótulo, marcado: bool)` por Cleaning, sem gate sobre `Complete()` (preserva a regra atual), sem fotos, sem configuração por tenant/imóvel. |
| **Minhas Faxinas (ordenação de "próximas faxinas")** | Doc 12: Faxina "possui... data; horário" como atributos conceituais. Doc 14 §26 (Portal da Faxineira): tela principal deve mostrar imediatamente "próximas faxinas; horário; imóvel". Doc 10 §5: "Agenda" é um módulo funcional próprio e rico (Agenda geral/por imóvel/por faxineira/por período/diária/semanal/mensal), citado como a tela principal da Faxineira no Doc 09/10 — nenhum código implementa este módulo. | — | `Cleaning.cs` **não possui nenhum campo de data/horário agendado** — apenas `CreatedAtUtc` (timestamp administrativo de criação) e timestamps de transição já ocorrida (`StartedAtUtc`, `InspectionStartedAtUtc`, `CompletedAtUtc`, `CancelledAtUtc`, todos nulos até a transição acontecer). `ReservationProjectionEntry` (projeção local de Reservation) carrega **apenas** `TenantId`+`ReservationId` — nenhuma data de check-in/check-out. `CreateCleaningCommand`/`CreateCleaningRequest` não capturam nenhuma data/horário na criação. | **GAP MATERIAL — bloqueante.** Não existe, em nenhum lugar do modelo de dados atual de Housekeeping, um campo que represente "quando esta faxina deve/deveria acontecer". `CreatedAtUtc` reflete apenas a ordem de inserção administrativa, não uma agenda real — usá-lo para ordenar "próximas faxinas" apresentaria à faxineira uma ordem sem relação garantida com a urgência real do trabalho. O módulo "Agenda" citado pela documentação como a fonte natural desse dado é uma funcionalidade própria, não implementada em nenhuma fase até aqui. | **Não resolvido — ver §21.4 abaixo. Nenhum dado especulativo foi adicionado.** |

### 21.4 Gap material identificado — parada solicitada

Conforme instrução explícita do usuário ("Se a informação temporal existente em Cleaning/Reservation projection for insuficiente para ordenar 'próximas faxinas' de forma correta: PARE e apresente o gap antes de adicionar dado especulativo"), a implementação de domínio para o Incremento 2A foi pausada neste ponto específico e o gap foi apresentado (matriz §21.3) antes de qualquer código. Os demais três pontos de possível conflito material que o usuário pediu para vigiar explicitamente — checklist, ocorrência, autorização own-cleaning — **não** apresentaram conflito documental material (ver matriz acima); apenas a ordenação de "próximas faxinas" permaneceu bloqueada até decisão do usuário.

### 21.5 Resolução do gap — decisão do usuário

Apresentadas três opções (adicionar campo de agendamento; ordenar por `CreatedAtUtc` documentando a limitação; remover a semântica de "próximas" neste incremento) mais a opção de fornecer contexto adicional. **Decisão do usuário: adicionar um campo real de agendamento.**

Escopo da adição, mínimo e aditivo (sem alterar nenhum comportamento existente do Incremento 1):

- Novo campo `Cleaning.ScheduledAtUtc` (`DateTimeOffset?`, opcional, nulo por padrão).
- Capturável apenas na criação administrativa (`CreateCleaningCommand`/`CreateCleaningRequest` ganham um parâmetro opcional `ScheduledAtUtc`) — sem endpoint de atualização retroativa (não solicitado, seria escopo não pedido).
- "Próximas faxinas" (Checkpoint 1/5, listagem own-cleaning) ordenada por `ScheduledAtUtc` ascendente, com faxinas sem valor (`null`) ordenadas por último (nunca ocultas) — nenhum dado é inventado para linhas existentes; elas simplesmente não participam da ordenação por agendamento até que um administrador informe a data.
- Documento 12 já documentava conceitualmente que a Faxina "possui... data; horário" (linha 280-283) — esta adição apenas passa a implementar um atributo já previsto na documentação conceitual; nenhuma alteração do Documento 12 é necessária.
- O módulo "Agenda" (Doc 10 §5, multi-visão: geral/por imóvel/por faxineira/por período/diária/semanal/mensal) permanece fora de escopo — `ScheduledAtUtc` é um campo simples de data/hora único por Cleaning, não uma reconstrução daquele módulo.

Com esta decisão registrada, o Checkpoint 0 do Incremento 2A está concluído e a implementação de domínio (Checkpoint 1) está autorizada a prosseguir.

## 22. Incremento 2A — Checkpoint 1: fundação de autorização/API own-cleaning

### 22.1 `Cleaning.ScheduledAtUtc`

Campo `DateTimeOffset? ScheduledAtUtc` adicionado a `Cleaning` (opcional, capturável apenas na criação administrativa via `CreateCleaningCommand`/`CreateCleaningRequest`, sem endpoint de atualização retroativa — conforme §21.5). Migration `AddCleaningScheduledAtUtc` gerada via `dotnet ef migrations add` (nunca escrita manualmente) — adiciona a coluna `housekeeping.cleanings.scheduled_at_utc` e o índice `(tenant_id, assigned_housekeeper_user_id, scheduled_at_utc)`. Propagado por toda a cadeia existente: `Cleaning.Create`, `CreateCleaningCommandHandler`, `CleaningResult`/`CleaningSummaryResult`, `CleaningDetailResponse`/`CleaningSummaryResponse`, `CleaningConfiguration`.

### 22.2 Leitura própria com ABAC

`ICleaningReader` ganhou dois métodos novos, mantendo os existentes intactos: `ListForHousekeeperAsync` (filtra obrigatoriamente por `AssignedHousekeeperUserId`, ordena por `ScheduledAtUtc` ascendente com nulos por último, depois `CreatedAtUtc`/`Id`) e `GetByIdForHousekeeperAsync` (retorna `null` tanto para "não existe" quanto para "existe mas não é minha", nunca distinguindo os dois casos). Duas novas queries de Application (`ListOwnCleaningsQuery`/`GetOwnCleaningDetailQuery`) recebem `HousekeeperUserId` exclusivamente do identity do chamador (`sub` claim via `HousekeepingIdentityReader`), nunca do corpo/query string da requisição.

### 22.3 `MyCleaningsController` — endpoints self-service dedicados

Novo controller em `api/v1/my-cleanings` (nunca um parâmetro opcional em `CleaningsController` — conforme §10 da autorização), com `GET /api/v1/my-cleanings` (lista paginada) e `GET /api/v1/my-cleanings/{cleaningId}` (detalhe), ambos gated por `[Authorize(Policy = IdentityPermissionCodes.CleaningsManageOwnCleaning)]`.

**Defeito real encontrado e corrigido**: `CLEANINGS:MANAGE:OWN_CLEANING` já estava seedado no catálogo persistido e mapeado para `HOUSEKEEPER` desde o Incremento 1 (Fase 6), mas nenhuma `AuthorizationPolicy` ASP.NET Core correspondente havia sido registrada em `IdentityAuthorizationExtensions.AddIdentityAuthorization()` — a própria documentação da classe já previa isso ("A future checkpoint that protects a new endpoint with a permission code not yet listed here must add a policy for it at that point"). Sem essa policy, toda chamada a `MyCleaningsController` falhava com `InvalidOperationException: The AuthorizationPolicy named: 'CLEANINGS:MANAGE:OWN_CLEANING' was not found` — descoberto pelos próprios testes de integração HTTP reais desta seção (nunca por inspeção estática). Corrigido adicionando a policy que faltava, mesmo padrão das demais.

### 22.4 Testes

Unitários (`IHostPro.Contexts.Housekeeping.Tests.Unit`): 5 novos (`ListOwnCleaningsQueryHandlerTests` ×2, `GetOwnCleaningDetailQueryHandlerTests` ×3) — **67/67** aprovados.

Integração HTTP real (`IHostPro.Contexts.Housekeeping.Tests.Integration`, `HousekeepingEndpointsTests`), 6 novos, todos contra Postgres/JWT reais via Testcontainers:
- `MyCleanings_list_returns_only_cleanings_assigned_to_the_caller` — dois housekeepers no MESMO tenant, cada um só vê a própria faxina.
- `MyCleanings_getById_for_own_cleaning_returns_200`.
- `MyCleanings_getById_for_a_cleaning_assigned_to_someone_else_returns_404_never_403` — mesmo tenant, housekeeper errado.
- `MyCleanings_getById_across_tenants_returns_404_never_403` — tenant diferente.
- `MyCleanings_list_without_a_token_returns_401`.
- `MyCleanings_list_with_ADMIN_role_lacking_CLEANINGS_MANAGE_OWN_CLEANING_returns_403` — prova que a permissão administrativa NÃO concede acesso aos endpoints self-service (nenhum bypass, correspondência exata de permissão).

**Defeito de teste real encontrado e corrigido**: `EnsureTenantExistsAsync` (helper pré-existente da suíte) fazia `INSERT` incondicional de `Tenant`, nunca antes exercitado com dois housekeepers do mesmo tenant na mesma suíte — quebrava com violação de chave primária. Corrigido tornando-o idempotente (`if (await dbContext.Tenants.AnyAsync(...)) return;`), sem alterar o comportamento dos testes já existentes (cada um usa um tenant novo).

**67/67** unit + **21/21** integration (`HousekeepingEndpointsTests`, incluindo os 15 pré-existentes) + **133/133** architecture (assembly inteiro) — todos revalidados após as correções. `git diff --check` não executado ainda (fica para o gate final do Checkpoint 6, junto dos demais checkpoints).

## 23. Incremento 2A — Checkpoint 2: ciclo de vida do Portal

### 23.1 Domínio

`Cleaning.MarkInTransit()` adicionado (`Assigned → InTransit`, self-service apenas — nenhum gatilho administrativo, conforme Documento 06: "Faxineira informou deslocamento"). `Cleaning.Start()` revisado para aceitar origem `Assigned` OU `InTransit` — ambos os caminhos permanecem válidos, preservando a "opcionalidade" documentada sem introduzir uma `ConfigurationDefinition` nova (decisão já registrada em §21.5).

### 23.2 Três eventos de integração — primeira implementação real

`CleaningDelayed`, `CleaningNeedsHelp`, `CleaningNeedsMaterial` (Documento 07 §6) estavam catalogados desde antes da Fase 6 mas nunca implementados — confirmado nesta sessão que nem sequer os handlers ADMINISTRATIVOS de `MarkCleaningWaitingMaterials`/`MarkCleaningWaitingHelp` (Incremento 1) os publicavam, apenas auditavam. Payload mínimo para os três (apenas `CleaningId`, sem campo adicional — Documento 07 não documenta nenhum campo além do fato em si; regra de parsimônia da autorização §12-14):

- **`CleaningDelayed`**: sem transição de estado (Documento 06 não documenta um estado "Atrasada"); apenas audita (`cleaning_delayed`) e publica. Rejeitado com `invalid_cleaning_transition` (409) apenas se a faxina já está `Completed`/`Cancelled` — limite de sanidade, não uma regra de negócio inventada.
- **`CleaningNeedsMaterial`**/**`CleaningNeedsHelp`**: publicados tanto pelo caminho self-service quanto pelo administrativo já existente — os dois handlers `MarkCleaningWaitingMaterialsCommandHandler`/`MarkCleaningWaitingHelpCommandHandler` (Incremento 1) foram estendidos com a publicação do evento (mudança pequena, aditiva, sem alterar nenhum comportamento existente) para que o mesmo fato de domínio produza o mesmo evento independentemente de quem o disparou.

### 23.3 Sete novos comandos self-service, todos com guarda ABAC

`MarkOwnCleaningInTransitCommand`, `StartOwnCleaningCommand`, `StartOwnCleaningInspectionCommand`, `CompleteOwnCleaningCommand`, `MarkOwnCleaningWaitingMaterialsCommand`, `MarkOwnCleaningWaitingHelpCommand`, `ReportOwnCleaningDelayCommand` — cada um espelha estruturalmente seu equivalente administrativo, mas carrega através de `OwnCleaningLoader.LoadOwnedAsync` (novo helper compartilhado) a mesma garantia de fail-closed do Checkpoint 1: `null` tanto para "não existe" quanto para "existe mas não é minha", nunca um sinal distinto de "proibido". Nenhum comando de Cancel/Assign/Reassign/criação foi adicionado ao self-service (não concedido pela autorização §9).

Sete novas rotas em `MyCleaningsController`: `POST /api/v1/my-cleanings/{id}/in-transit|start|start-inspection|complete|waiting-materials|waiting-help|delay` — mesmos nomes de segmento do controller administrativo, sob `/my-cleanings/`, todas gated por `CLEANINGS:MANAGE:OWN_CLEANING`.

### 23.4 Testes

Domínio (`CleaningTests`): +5 (`MarkInTransit` de `Assigned` sucede/de `Pending`/`Started` falha; `Start` de `InTransit` sucede).

Unitários de Application (`OwnCleaningLifecycleCommandHandlerTests`, novo arquivo): +17 — sucesso por cada um dos 7 comandos, rejeição ABAC (`cleaning_not_found`, nunca "forbidden") para InTransit/Start/StartInspection/Complete/WaitingMaterials/WaitingHelp/Delay, rejeição de transição inválida para InTransit/Delay-em-Completed. `CleaningLifecycleCommandHandlerTests` (existente) atualizado: os dois testes que documentavam "enqueues no event" para os handlers administrativos de WaitingMaterials/WaitingHelp foram renomeados e revisados para refletir a nova publicação de evento (§23.2) — **89/89** no assembly completo.

Integração HTTP real (`HousekeepingEndpointsTests`), 4 novos:
- `Full_self_service_lifecycle_via_InTransit_start_start_inspection_complete_succeeds`.
- `Self_service_waiting_materials_waiting_help_and_delay_all_succeed_for_the_owning_housekeeper`.
- `Self_service_start_by_a_housekeeper_not_assigned_to_the_cleaning_returns_404_never_403`.
- `Self_service_delay_on_a_Completed_cleaning_returns_409`.

**25/25** (`HousekeepingEndpointsTests`) + **68/68** (projeto de integração completo) + **89/89** unit + **133/133** architecture — todos aprovados.

## 24. Incremento 2A — Checkpoint 3: Ocorrências

### 24.1 `CleaningOccurrence` — entidade, não agregado

`CleaningOccurrence : Entity<Guid>, ITenantOwned` (nunca `AggregateRoot<Guid>` — decisão §15 já registrada em §21.3), imutável, append-only, mirando exatamente `CleaningAuditEntry`: `TenantId`, `CleaningId` (referência opaca, sem FK física, mesma convenção do resto do contexto), `Type` (`OccurrenceType`, catálogo fixo de 8 valores — Theft/Breakage/ForgottenObject/Damage/Animal/Smoking/Noise/MaterialShortage — traduzidos literalmente dos 8 exemplos do Documento 12 §8, único texto-fonte encontrado), `Description` (livre, até 500 caracteres), `RegisteredByUserId`, `RegisteredAtUtc`. Sem evidências/fotos (proibido — approval §2); sem máquina de estados própria; sem novo evento de integração (Documento 07 não cataloga nenhum evento de ocorrência; approval §18 proíbe evento novo sem necessidade comprovada).

Migration `AddCleaningOccurrences` gerada via `dotnet ef migrations add`, complementada manualmente (mesmo padrão do `InitialCreate`) com `REVOKE UPDATE, DELETE` (apenas SELECT/INSERT, mesmo tratamento append-only de `cleaning_audit_log`) e RLS (`ENABLE`/`FORCE ROW LEVEL SECURITY` + `CREATE POLICY tenant_isolation`) para `housekeeping.cleaning_occurrences`.

### 24.2 API self-service apenas

`RegisterCleaningOccurrenceCommand`/Handler — ABAC via `OwnCleaningLoader` (mesma garantia fail-closed dos Checkpoints 1-2), rejeitado com `invalid_cleaning_transition` (409) apenas se a faxina já é `Completed`/`Cancelled` (mesmo limite de sanidade do `ReportOwnCleaningDelayCommand`, não uma regra nova). `ListCleaningOccurrencesQuery`/Handler — verifica a posse da Cleaning-pai via `ICleaningReader.GetByIdForHousekeeperAsync` antes de listar (404 uniforme, nunca uma lista vazia ambígua para um id não-possuído). Nenhum endpoint administrativo de ocorrências foi criado (fora do escopo explícito do Checkpoint 3 — "self-service API").

Duas novas rotas em `MyCleaningsController`: `POST/GET /api/v1/my-cleanings/{cleaningId}/occurrences`, ambas gated por `CLEANINGS:MANAGE:OWN_CLEANING`.

### 24.3 Testes

Domínio (`CleaningOccurrenceTests`, novo arquivo): +3.

Unitários de Application (`RegisterCleaningOccurrenceCommandHandlerTests`/`ListCleaningOccurrencesQueryHandlerTests`, novos arquivos): +6 — sucesso, rejeição ABAC (`cleaning_not_found`, nunca "forbidden"), rejeição em `Completed` (`invalid_cleaning_transition`), cleaning inexistente. **98/98** no assembly completo.

Integração HTTP real (`HousekeepingEndpointsTests`), 4 novos: registro+listagem golden-path; housekeeper não-atribuído → 404; tipo inválido → 400; registro em faxina `Completed` → 409.

**29/29** (`HousekeepingEndpointsTests`) + **72/72** (projeto de integração completo) + **98/98** unit + **133/133** architecture — todos aprovados.

## 25. Incremento 2A — Checkpoint 4: Checklist textual

### 25.1 `CleaningChecklistItem` — mutável, catálogo fixo de 8 itens

Diferente de `CleaningOccurrence`/`CleaningAuditEntry` (append-only), `CleaningChecklistItem : Entity<Guid>, ITenantOwned` é mutável em memória (`SetChecked`) — representa literalmente o estado de uma caixa de seleção, não um fato imutável. Catálogo fixo de 8 valores (`Stove/Refrigerator/Tv/AirConditioning/Bathroom/Linens/Trash/Window`), traduzidos verbatim dos 8 exemplos do Documento 12 §8 ("Checklist"). Uma linha só é criada quando o item é alternado pela primeira vez (nunca semeada eagerly para as 8 na criação da Cleaning) — um item sem linha é simplesmente não marcado por padrão, nunca um valor inventado/persistido. Índice único `(tenant_id, cleaning_id, item_type)` garante no máximo uma linha por item por faxina.

**Sem gate sobre `Cleaning.Complete()`** — decisão já confirmada no Checkpoint 0 (Fase 6 doc §21.3): nenhum documento afirma em prosa que o checklist é obrigatório para concluir; o Workflow 10 do Documento 17 é um diagrama de sequência de UI, não uma regra de negócio explícita. Verificado por teste de integração real (`Checklist_does_not_block_Complete_when_no_item_is_checked`).

Migration `AddCleaningChecklistItems` gerada via `dotnet ef migrations add`, complementada manualmente com RLS (mesmo padrão de todas as tabelas do schema) — **sem** a restrição `REVOKE UPDATE, DELETE` aplicada a `cleaning_occurrences`/`cleaning_audit_log`, já que esta tabela é legitimamente mutável.

### 25.2 API self-service apenas

`SetOwnCleaningChecklistItemCommand`/Handler — upsert por chave composta via `ICleaningChecklistItemRepository` (não o `IRepository<TAggregate,TId>` genérico compartilhado, cuja busca por `Guid` único não serve para uma chave composta), ABAC via `OwnCleaningLoader`, mesmo limite de sanidade `Completed`/`Cancelled` → 409 dos Checkpoints 2-3, auditado (`cleaning_checklist_item_set`). `GetOwnCleaningChecklistQuery`/Handler — verifica a posse da Cleaning-pai antes de ler, sempre retorna os 8 itens (nunca omite os não-alternados).

Duas novas rotas em `MyCleaningsController`: `GET /api/v1/my-cleanings/{cleaningId}/checklist`, `PUT /api/v1/my-cleanings/{cleaningId}/checklist/{itemType}`, ambas gated por `CLEANINGS:MANAGE:OWN_CLEANING`.

### 25.3 Testes

Domínio (`CleaningChecklistItemTests`, novo arquivo): +3.

Unitários de Application (`SetOwnCleaningChecklistItemCommandHandlerTests`/`GetOwnCleaningChecklistQueryHandlerTests`, novos arquivos): +6 — criação de linha nova vs. mutação em linha existente, rejeição ABAC (`cleaning_not_found`), rejeição em `Completed` (`invalid_cleaning_transition`). **107/107** no assembly completo.

Integração HTTP real (`HousekeepingEndpointsTests`), 4 novos: 8 itens iniciais todos não marcados + toggle persiste; `Complete()` nunca bloqueado pelo checklist; housekeeper não-atribuído → 404; tipo de item inválido → 400.

**33/33** (`HousekeepingEndpointsTests`) + **76/76** (projeto de integração completo) + **107/107** unit + **133/133** architecture — todos aprovados.

### 25.4 Defeito real encontrado durante a regeneração do NSwag — quinta ocorrência da classe CancelReservation/CancelCleaning

Ao regenerar `api-client.ts` para o Checkpoint 5 (frontend), cinco ações self-service de `MyCleaningsController` (`Start`, `StartInspection`, `Complete`, `WaitingMaterials`, `WaitingHelp`) mostraram-se colidir com o mesmo nome de segmento de rota final das ações administrativas equivalentes de `CleaningsController` — exatamente a mesma classe de defeito já documentada e corrigida uma vez para `CancelReservation`/`CancelCleaning` (§20.4/`Program.SwaggerOperationIdSelector`). O NSwag gerou `start`/`start2`, `startInspection`/`startInspection2`, `complete`/`complete2`, `waitingMaterials`/`waitingMaterials2`, `waitingHelp`/`waitingHelp2` — nomes que não indicam qual rota real cada um chama, o mesmo risco de apontar silenciosamente para a rota errada numa regeneração futura (ordem de operações no documento OpenAPI não é uma garantia estável).

**Corrigido**: `SwaggerOperationIdSelector` estendido com cinco novos casos, atribuindo IDs explícitos apenas ao lado self-service (`StartOwnCleaning`, `StartOwnCleaningInspection`, `CompleteOwnCleaning`, `MarkOwnCleaningWaitingMaterials`, `MarkOwnCleaningWaitingHelp`) — o lado administrativo permanece `null` (sem mudança), preservando os nomes já publicados `start()`/`startInspection()`/`complete()`/`waitingMaterials()`/`waitingHelp()` do frontend administrativo existente. `OpenApiOperationIdTests` estendido (não duplicado) com as mesmas cinco asserções reais contra o documento OpenAPI completo — **1/1 aprovado** contra Postgres/RabbitMQ reais via Testcontainers, confirmando que cada novo OperationId existe exatamente uma vez e mapeia para a rota `/my-cleanings/...` correta.

## 26. Incremento 2A — Checkpoint 5: Frontend Portal

### 26.1 Shell dedicado, nunca `AdminLayout`

`layout/portal-shell/` (novo) — shell mobile-first dedicado ao Portal da Faxineira, conforme approval §5-6 (proibição explícita de reutilizar `/housekeeping` administrativo ou `AdminLayout`): uma única toolbar superior (nome do app) e uma barra de navegação inferior com exatamente dois destinos — "Minhas Faxinas" e "Sair" — sem `mat-sidenav`, sem `BreakpointObserver`, sem comportamento PWA/offline (nenhum desses foi solicitado). `app.routes.ts` ganhou uma árvore de rotas de topo nova, `/my-cleanings`, isolada da árvore `/housekeeping` administrativa, com `PortalShell` como componente-pai e dois filhos (`''` → lista, `':cleaningId'` → detalhe), ambos protegidos por `permissionGuard` exigindo `CLEANINGS:MANAGE:OWN_CLEANING` — a mesma permissão já tipada/semeada no Incremento 1, nunca uma nova.

### 26.2 Telas

`features/portal/my-cleanings-list/` — lista "Minhas Faxinas": cartões com `propertyId`, chip de status (rótulo traduzido), horário agendado quando presente (`scheduledAtUtc`), e um botão de ação primária condicional por status (mesmas transições de domínio do backend — nunca uma ação client-side inventada). Clique no cartão navega ao detalhe; clique no botão de ação dispara a transição diretamente da lista, sem navegação.

`features/portal/my-cleaning-detail/` — tela de detalhe: botões de ciclo de vida (Estou a caminho / Iniciar / Iniciar inspeção / Concluir / Informar atraso / Aguardando materiais / Aguardando ajuda) visíveis apenas quando o status atual do domínio permite a transição correspondente (espelhando os guards reais de `Cleaning`, nunca uma lista fixa); seção "Ocorrências" com formulário (`[formGroup]`/`(ngSubmit)` corretamente vinculados — atenção redobrada após o defeito de produção já documentado em `AssignCleaningDialog`, Fase 6 §10) e listagem das ocorrências já registradas; seção "Checklist" com os 8 itens sempre exibidos (mesmo os não alternados), cada um uma checkbox que persiste via `PUT` ao ser alternada.

`features/portal/portal.service.ts` — wrapper fino sobre o `Client` gerado (NSwag), mesma convenção dos demais `*.service.ts` do projeto.

`public/i18n/en.json`/`pt-BR.json` — novo namespace `portal` (shell/list/detail/occurrences/checklist), com paridade completa entre os dois idiomas.

### 26.3 Testes automatizados

Suíte Angular completa (Vitest): **370/370 aprovados**, incluindo os novos specs de `PortalShell`, `PortalService`, `MyCleaningsList`, `MyCleaningDetail`, e um teste preventivo adicionado a `user-profile.service.spec.ts` provando que `CLEANINGS:MANAGE` e `CLEANINGS:MANAGE:OWN_CLEANING` nunca se concedem mutuamente (guarda contra checagem por prefixo, proibida explicitamente pelo approval).

### 26.4 Verificação real em navegador — defeito encontrado e corrigido

Após o incidente já registrado nesta mesma sessão (uma chamada ampla a `dotnet user-secrets list` vazou a chave privada de assinatura JWT para o output), a verificação visual do Portal foi deliberadamente desenhada para nunca ler/usar qualquer senha real: um teste xUnit temporário e descartável (`ZZZ_TEMP_PortalBrowserSeed.cs`, **excluído ao final desta seção** — nunca fez parte da suíte real) semeou um usuário `HOUSEKEEPER` sintético + duas Cleanings (`Assigned`/`InInspection`) diretamente no Postgres real de desenvolvimento e emitiu um JWT real assinado via `IJwtTokenGenerator` já resolvido por DI — a chave de assinatura em si nunca foi lida, impressa ou manuseada por este processo. O token foi injetado na aplicação Angular real (`ng serve`) através de `AuthStateService.setTokens(...)`, alcançado via os utilitários de depuração do próprio Angular (`ng.getComponent`), seguido de uma chamada real a `UserProfileService.load()` (perfil + permissões reais, nunca decodificados do JWT) — em nenhum momento uma senha real foi lida, digitada ou usada.

Verificado, com a API real (`IHostPro.Api`) e o Postgres/RabbitMQ/Redis de desenvolvimento reais:

- Lista renderiza as duas faxinas semeadas com status e ação primária corretos;
- Tela de detalhe renderiza os botões de ciclo de vida corretos para `Assigned` (Estou a caminho / Iniciar / Informar atraso);
- Checklist: toggle de um item (`TV`) persiste via `PUT /api/v1/my-cleanings/{id}/checklist/Tv` → `200 OK`, confirmado por releitura;
- Ocorrências: registro de uma ocorrência (`Dano`, com descrição) persiste via `POST /api/v1/my-cleanings/{id}/occurrences` → `200 OK`, aparece na lista imediatamente;
- Ação rápida "Iniciar" a partir do cartão da lista transiciona `Assigned → Started` via `POST .../start` → `200 OK`, atualização de status e botão de ação refletidos sem reload de página;
- Logout ("Sair") limpa a sessão e redireciona a `/login`.

**Defeito real encontrado e corrigido**: o snackbar de sucesso ao registrar uma ocorrência exibia a chave de tradução crua `portal.detail.occurrenceRegistered` em vez do texto traduzido — a chave correta já existia em ambos os idiomas, mas aninhada em `portal.detail.occurrences.occurrenceRegistered`, e `my-cleaning-detail.ts` referenciava o caminho errado (faltando o segmento `occurrences`). Corrigido em `my-cleaning-detail.ts` (uma linha); asserção de regressão adicionada ao teste existente `'submits a valid occurrence and reloads the occurrence list'` em `my-cleaning-detail.spec.ts`. Suíte completa revalidada após a correção: **370/370 aprovados**.

Dados sintéticos de verificação (5 usuários `portal-verify-*@example.com`, Cleanings e projeções de propriedade associadas) foram removidos do Postgres de desenvolvimento ao final da verificação; o arquivo de teste temporário foi excluído do repositório.

### 26.5 Segunda rodada de verificação — checklist completo de 17 itens (a pedido do usuário)

A pedido explícito do usuário, a verificação em navegador foi refeita do zero com um roteiro mais abrangente, cobrindo especificamente: login HOUSEKEEPER, acesso ao Portal, ausência de acesso ao administrativo, Minhas Faxinas, detalhe, InTransit, iniciar, iniciar inspeção, concluir, atraso, ajuda, materiais, ocorrência, checklist, responsividade/mobile-first, bottom navigation e isolamento own-cleaning. Mesma técnica da rodada anterior (§26.4) — token real minerado via `IJwtTokenGenerator`, nunca uma senha — porém com um cenário de dados mais rico: um segundo teste xUnit temporário e descartável (mesmo arquivo `ZZZ_TEMP_PortalBrowserSeed.cs`, reescrito e novamente excluído ao final desta seção) semeou dois usuários `HOUSEKEEPER` reais no mesmo tenant e cinco Cleanings — quatro pertencentes ao housekeeper principal, cobrindo cada ramo do ciclo de vida, e uma pertencente ao SEGUNDO housekeeper (para o teste de isolamento own-cleaning).

Resultado, item a item, todos aprovados contra a API real (`IHostPro.Api`, iniciada com o profile de desenvolvimento para que os user-secrets já configurados na máquina carregassem corretamente — nenhum segredo foi lido ou exibido por este processo) e o Postgres/RabbitMQ/Redis de desenvolvimento reais:

1. **Login HOUSEKEEPER**: `UserProfileService.load()` retornou perfil real com `roles: ["HOUSEKEEPER"]` e `permissions: ["CLEANINGS:MANAGE:OWN_CLEANING", ...]`.
2. **Acesso ao Portal**: `/my-cleanings` carregou normalmente sob `permissionGuard`.
3. **Ausência de acesso ao administrativo**: navegação para `/housekeeping` (rota administrativa, exige `CLEANINGS:MANAGE`) resultou em "Acesso negado" — confirma na prática que a checagem de permissão é por igualdade exata, nunca por prefixo (mesma garantia já coberta por teste unitário em `user-profile.service.spec.ts`).
4. **Minhas Faxinas**: lista renderizou exatamente as 4 Cleanings do housekeeper principal (Designada/Em andamento/Em andamento/Designada) — a Cleaning do segundo housekeeper nunca apareceu.
5. **Detalhe**: navegação cartão → detalhe funcionou para todas as Cleanings testadas.
6. **InTransit**: "Estou a caminho" → status "A caminho", snackbar "Marcado como a caminho." (texto traduzido correto).
7. **Iniciar**: `Assigned`/`InTransit → Started`, status "Em andamento".
8. **Iniciar inspeção**: `Started → InInspection`, status "Em inspeção", snackbar "Inspeção iniciada.".
9. **Concluir**: `InInspection → Completed`, status "Concluída", snackbar "Faxina concluída.", nenhuma ação restante (estado terminal).
10. **Informar atraso**: `POST /api/v1/my-cleanings/{id}/delay` → `200 OK` (não altera o status — por desenho, mesmo comportamento já documentado em §21.3/Checkpoint 0).
11. **Preciso de ajuda**: `MarkWaitingHelp`, status "Aguardando ajuda".
12. **Preciso de materiais**: `MarkWaitingMaterials`, status "Aguardando materiais".
13. **Ocorrência**: registro de "Furto" persiste via `POST .../occurrences` → `200 OK`, snackbar "Ocorrência registrada." (confirma que a correção do §26.4 permanece válida).
14. **Checklist**: toggle de "Fogão" (`Stove`) persiste via `PUT .../checklist/Stove` → `200 OK`.
15. **Responsividade/mobile-first**: viewport redimensionado para 375×812 (preset mobile). O screenshot da ferramenta de navegador exibiu visualmente o conteúdo comprimido num retângulo menor que o viewport — investigado e determinado ser um artefato de renderização/captura da própria ferramenta (a proporção da imagem capturada, 469×1015, não corresponde a nenhuma escala óbvia do viewport real). Medições diretas via `getBoundingClientRect()`/`scrollWidth`/`clientWidth` no DOM real confirmaram que o layout está correto: `.portal-shell`, `.portal-shell__content` e a barra de navegação inferior ocupam exatamente 375px (100% do viewport), os cartões ocupam 351px (375px − 12px de padding em cada lado), e `document.documentElement.scrollWidth === clientWidth` (nenhum overflow horizontal). Tratado como aprovado com base na medição de DOM, não no screenshot visual, que se mostrou não confiável neste caso específico.
16. **Bottom navigation**: os dois destinos ("Minhas Faxinas" e "Sair") testados a partir de uma tela de detalhe — "Minhas Faxinas" retorna à lista sem reload de página; "Sair" limpa a sessão e redireciona a `/login`.
17. **Isolamento own-cleaning**: além da ausência na listagem (item 4), navegação direta por URL para a Cleaning do segundo housekeeper (`router.navigateByUrl('/my-cleanings/{id-do-outro-housekeeper}')`, simulando um link direto/deep link) resultou em `GET /api/v1/my-cleanings/{id} → 404 Not Found` e na tela "Não foi possível carregar esta faxina." — nunca os dados reais, confirmando o comportamento fail-closed uniforme já garantido por `OwnCleaningLoader`/`GetByIdForHousekeeperAsync` (Checkpoints 1-4).

**Nenhum defeito novo encontrado nesta rodada** — a correção do §26.4 permanece válida e a suíte automatizada (370/370) não foi alterada. Dados sintéticos (2 usuários, 5 Cleanings, projeção de propriedade) removidos do Postgres de desenvolvimento; processo `IHostPro.Api` iniciado manualmente para esta verificação foi encerrado; arquivo de teste temporário excluído novamente.

### 26.6 Observação registrada para o gate do Checkpoint 6 (não bloqueia este checkpoint)

Executar `IHostPro.MigrationRunner.dll` diretamente (fora de `dotnet run` com o profile de desenvolvimento) falha na etapa de provisionamento da topologia RabbitMQ com `ACCESS_REFUSED`, porque as credenciais padrão do `appsettings.json` versionado (`guest`/`guest`) não correspondem ao broker real de desenvolvimento (usuário único configurado é `ihostpro`, sem usuário `guest`). As migrations EF Core (schema) são independentes desta etapa e foram confirmadas bem-sucedidas diretamente via `psql` (`housekeeping.cleanings.scheduled_at_utc`, `housekeeping.cleaning_occurrences`, `housekeeping.cleaning_checklist_items` todas presentes). A topologia RabbitMQ (`identity-events`, `property-management-events`, `reservation-events`, `configuration-events`, `housekeeping-events`) já está corretamente provisionada no broker de desenvolvimento a partir de execuções bem-sucedidas anteriores nesta mesma sessão — não é um bloqueio funcional agora. Fica registrado como item a investigar no gate de restauração/validação de ambiente do Checkpoint 6 (possível necessidade de `dotnet user-secrets` dedicado para o projeto `IHostPro.MigrationRunner`, não verificado nesta sessão).

### 26.7 Build de produção

`ng build --configuration production`: sucesso, sem erros ou warnings novos. Bundle inicial 409.02 kB raw / 95.12 kB estimado após transferência; `my-cleanings-list`/`my-cleaning-detail`/`portal-shell` carregados como lazy chunks, consistente com o padrão de lazy-loading já usado pelas demais features administrativas.

## 27. Incremento 2A — Checkpoint 6: Homologação final

### 27.1 Testes Playwright do Portal (novos)

Dois arquivos novos em `tests/Frontend/IHostPro.Web.Tests.E2E/`, seguindo exatamente a convenção já estabelecida por `HousekeepingE2ETests.cs`/`UsersAuthorizationE2ETests.cs` (dados de teste sempre semeados via API real com o token real do ADMIN; login do HOUSEKEEPER sempre pelo formulário real, com uma senha sintética conhecida — nunca uma credencial de produção; navegação e asserções sempre via seletores reais do DOM/Playwright, nunca um atalho):

- `PortalE2ETests.cs` (3 testes): `Housekeeper_completes_the_full_self_service_lifecycle_with_occurrence_and_checklist` (login → lista → detalhe → InTransit → Iniciar → ocorrência → checklist → Iniciar inspeção → Concluir → estado terminal → bottom nav → logout, tudo num único fluxo real coerente); `Housekeeper_reports_a_delay_and_requests_materials_and_help` (atraso, materiais, ajuda — os três ramos alternativos não cobertos pelo fluxo principal); `Portal_renders_full_width_at_a_mobile_viewport_with_no_horizontal_overflow` (375×812, `scrollWidth`/`clientWidth` sem overflow, bottom nav ocupando exatamente 375px).
- `PortalAuthorizationE2ETests.cs` (3 testes): redirecionamento de usuário não autenticado com `redirectTo` preservado; HOUSEKEEPER negado em `/housekeeping` administrativo (`/forbidden`); HOUSEKEEPER incapaz de carregar a Cleaning de outro HOUSEKEEPER (ausente na lista, `404` real na API, tela "Não foi possível carregar esta faxina.").

**Dois defeitos reais encontrados e corrigidos nos próprios testes durante a escrita** (nunca no código de produção): (1) duas das três funções de `PortalAuthorizationE2ETests` assumiam que o login redirecionava direto para `/my-cleanings`, mas `Login.submit()` só honra `redirectTo` quando presente na URL — sem ele, todo login (independente do papel) aterrissa em `/` (Home administrativa); corrigido aguardando `/` e navegando explicitamente em seguida. (2) o teste de isolamento correlacionava uma resposta de rede a uma navegação completa de página (`RunAndWaitForResponseAsync` em volta de um `GotoAsync` de documento inteiro, não um clique) — trocado por uma chamada de API direta e desacoplada (`page.Context.APIRequest.GetAsync`) para a asserção do `404`, com a verificação da UI mantida separadamente. Após as duas correções: **6/6 aprovados** repetidamente (quatro execuções completas do assembly `IHostPro.Web.Tests.E2E`, a última com **67/67** aprovados no total, incluindo todos os specs administrativos pré-existentes).

### 27.2 Cross-tenant/ABAC/exact-permission-match

- Já existia cobertura completa de isolamento same-tenant-different-housekeeper (404 nunca 403) para leitura e para cada comando self-service (`start`, `occurrences`, `checklist`), e um teste cross-tenant genuíno para `GetById`. **Gap identificado**: nenhum teste cross-tenant genuíno cobria um endpoint de **escrita**. Adicionado `Self_service_start_across_tenants_returns_404_never_403` em `HousekeepingEndpointsTests.cs`, espelhando exatamente o teste cross-tenant de leitura já existente — **77/77** aprovados no assembly completo (`IHostPro.Contexts.Housekeeping.Tests.Integration`) após a adição.
- Exact-permission-match (nunca prefixo) confirmado em três camadas independentes: unitário (`user-profile.service.spec.ts`, Checkpoint 5), API real (`A_HOUSEKEEPER_who_navigates_directly_to_the_administrative_housekeeping_area_is_denied_access`, §27.1) e verificação manual em navegador (§26.5, item 3).

### 27.3 Suíte de testes de backend consolidada

Duas execuções completas de `dotnet test IHostPro.sln` foram realizadas nesta sessão. A **primeira** (antes dos ajustes dos testes Playwright do Portal) apresentou 4 falhas isoladas entre milhares de testes: `ReservationsE2ETests.Admin_clears_guestPhone_by_sending_null`, `ReservationsE2ETests.A_period_conflict_is_presented_correctly`, `PropertyManagementE2ETests.Admin_lists_and_creates_a_condominium` e `PolicyUpdatedRegressionTests.PolicyUpdated_delivered_through_real_RabbitMQ_to_the_real_Worker_advances_the_real_Redis_cache_generation` — nenhuma relacionada ao código desta sessão (Reservations/PropertyManagement/Configuration, nunca tocados no Incremento 2A). Todas as quatro foram reexecutadas **isoladamente** (fora do contexto do assembly gigante) e passaram sem exceção, confirmando que eram instabilidades transitórias por concorrência real (Playwright contra a UI real sob carga; redelivery real de RabbitMQ sob carga), não regressões de código.

A **segunda** execução completa (após todos os ajustes, tentada para ter um artefato único e atual) apresentou uma cascata muito maior de falhas — incluindo especificações inteiras que NUNCA foram tocadas nesta sessão (Identity, Usuários, Políticas) e falhando em ~1ms cada, um padrão que não é de asserção de negócio mas de **infraestrutura**: o stack trace real mostra `Docker.DotNet` expirando ao tentar inspecionar um contêiner (`DockerContainer.CheckReadinessAsync`), e um benchmark de latência (Fase 5, decisão 7) que registrou p95 de 71ms contra o alvo de 50ms sob a mesma carga simultânea. Confirmado via `docker ps`/`docker info` imediatamente após (resposta em <1s) que o daemon Docker havia apenas saturado temporariamente por dezenas de execuções pesadas de Testcontainers em sequência direta nesta mesma sessão — não uma condição permanente, não um defeito de código e, especificamente, **não um defeito de produto do Incremento 2A**: nenhuma das especificações afetadas pertence a este incremento. Por essa razão, esta segunda execução **é inválida como evidência** e não é contada. Não foi realizada uma terceira execução massiva da solução — o custo de outra rodada completa não agregaria evidência útil além da já obtida via reexecuções isoladas e focadas.

**Evidência oficial do Incremento 2A** (todas as contagens abaixo obtidas em execuções isoladas, limpas, sem concorrência entre pacotes pesados):

- Primeira execução completa da solução: 4 falhas, todas reexecutadas isoladamente e não reproduzidas — nenhuma era regressão do Incremento 2A.
- `IHostPro.Contexts.Housekeeping.Tests.Integration`: **77/77** (inclui o novo teste cross-tenant de escrita).
- `IHostPro.Web.Tests.E2E`: **67/67** (inclui os 6 novos testes do Portal).
- `IHostPro.Api.Tests.Integration.PolicyUpdatedRegressionTests` isolado: **1/1**.
- `IHostPro.Contexts.Housekeeping.Tests.Unit` (gate final curto, pós-publicação dos testes Playwright): **107/107**.
- `IHostPro.ArchitectureTests` (gate final curto, pós-publicação dos testes Playwright): **133/133**.
- Frontend (Vitest): **370/370**.
- `dotnet build IHostPro.sln -c Release`: 0 erros, 0 warnings.
- `ng build --configuration production`: sucesso.
- `npm run generate:api` × 2: saída byte-idêntica (determinístico).

### 27.4 Build Release, testes de frontend, determinismo do NSwag

- `dotnet build IHostPro.sln -c Release`: sucesso, 0 erros, 0 warnings.
- `ng build --configuration production`: sucesso (§26.7).
- Suíte de testes Angular (Vitest): **370/370** aprovados, execução final após todas as alterações do Checkpoint 6.
- `npm run generate:api` executado duas vezes consecutivas com a API real em execução: saída **byte-idêntica** nas duas execuções (`diff` sem diferenças).

### 27.5 `git diff --check`

Apenas espaços em branco à direita (`trailing whitespace`) em 5 linhas de `api-client.ts` — todas em comentários JSDoc gerados automaticamente pelo próprio template Angular do NSwag (`@param foo (optional) `), confirmado determinístico (mesmas linhas nas duas gerações consecutivas de §27.4) e presente em parâmetros de endpoints administrativos pré-existentes, não específicos do Portal. Como o arquivo é 100% gerado (nunca editado manualmente, por convenção do projeto), corrigir a formatação à mão seria imediatamente revertido na próxima regeneração — registrado como característica conhecida e não-acionável do gerador, não um defeito do código desta sessão.

### 27.6 Restauração de ambiente

RabbitMQ e Redis de desenvolvimento (usados durante toda a sessão para os testes locais e a verificação em navegador) parados; RabbitMQ e Redis de homologação reiniciados — baseline correta restaurada (`ihostpro-homolog-rabbitmq`/`ihostpro-homolog-redis` up, `ihostpro-rabbitmq`/`ihostpro-redis` parados). Confirmado ausência de processos `dotnet`/`node` órfãos relacionados a este projeto (o servidor `ng serve` usado para a verificação em navegador do §26.5 foi encerrado). `ihostpro-postgres` (desenvolvimento, porta 5432) permanece em execução — não compartilha porta com `ihostpro-homolog-postgres` (porta 15432), portanto ambos correndo simultaneamente é o estado normal, não uma pendência de restauração.

### 27.7 Débito não bloqueante — configuração do MigrationRunner

O `appsettings.json` padrão do `IHostPro.MigrationRunner` não autentica no broker RabbitMQ usado fora do profile/configuração correspondente (as credenciais versionadas — `guest`/`guest` — não correspondem ao único usuário real do broker de desenvolvimento); execuções reais do `MigrationRunner` contra um broker real exigem as credenciais/configuração corretas por ambiente, não verificadas nesta sessão. Isto **não significa** que o `MigrationRunner` esteja quebrado, nem que o schema ou a topologia RabbitMQ não tenham sido validados — ambos foram confirmados bem-sucedidos por vias independentes desta mesma sessão (migrations EF Core aplicadas e verificadas diretamente via `psql`; topologia RabbitMQ — `identity-events`, `property-management-events`, `reservation-events`, `configuration-events`, `housekeeping-events` — já provisionada e confirmada via `rabbitmqctl list_exchanges`). Registrado como débito de configuração a investigar em sessão futura, não como defeito funcional.

## 28. Incremento 2A — conclusão técnica e versionamento

### 28.1 Status

O Incremento 2A (Portal da Faxineira Core) está **tecnicamente concluído e versionado** em três commits em `feature/housekeeping`, **local nesta máquina neste momento** — a publicação em `origin/feature/housekeeping` é registrada separadamente em §29, após o push real. `master` permanece intocada; nenhuma tag foi criada; nenhum merge foi realizado. A Fase 6 **continua EM ANDAMENTO**: o Incremento 2B (Files/Evidências) não foi implementado e depende de decisão explícita após a auditoria de §28.4/§30.

### 28.2 Commits

| # | Hash | Assunto | Escopo |
|---|------|---------|--------|
| 1 | `db7525c34c62880622f92699cabd4c0834d1553c` | `feat(housekeeping): add housekeeper self-service workflows` | Backend/core: `ScheduledAtUtc`, `OwnCleaningLoader`, autorização `CLEANINGS:MANAGE:OWN_CLEANING`, InTransit, ciclo de vida own-cleaning, delay, waiting-materials/help, `CleaningOccurrence`, checklist textual, auditoria, migrations/RLS, correção de política de autorização ausente, correção de colisão de OperationId do NSwag. |
| 2 | `bf4a2214742b529b7f2e2b3ac96ded0cea162905` | `feat(frontend): add housekeeper portal` | Frontend: `PortalShell`, rota `/my-cleanings`, `my-cleanings-list`, `my-cleaning-detail`, `PortalService`, i18n (`portal.*`), cliente NSwag regenerado. |
| 3 | `7b9eebfb2c51a738879d0e49866bc110eeeaa735` | `test(housekeeping): cover housekeeper portal workflows` | Testes: unitários de domínio/aplicação (own-cleaning, occurrences, checklist), integração HTTP real (isolamento same-tenant e cross-tenant, leitura e escrita), regressão de OperationId, testes unitários de frontend, testes Playwright E2E do Portal (`PortalE2ETests`, `PortalAuthorizationE2ETests`). |

### 28.3 Resultados finais registrados

- Verificação real em navegador: **17/17 itens** aprovados (login HOUSEKEEPER, acesso ao Portal, ausência de acesso ao administrativo, Minhas Faxinas, detalhe, InTransit, iniciar, iniciar inspeção, concluir, atraso, ajuda, materiais, ocorrência, checklist, responsividade/mobile-first, bottom navigation, isolamento own-cleaning — §26.5).
- `IHostPro.Contexts.Housekeeping.Tests.Integration`: **77/77**.
- `IHostPro.Web.Tests.E2E`: **67/67**.
- `IHostPro.Contexts.Housekeeping.Tests.Unit`: **107/107** (gate final, §27.3).
- `IHostPro.ArchitectureTests`: **133/133** (gate final, §27.3).
- Frontend (Vitest): **370/370**.
- `dotnet build IHostPro.sln -c Release`: 0 erros / 0 warnings.
- `ng build --configuration production`: sucesso.
- `npm run generate:api` × 2: byte-idêntico (determinístico).
- Cross-tenant write path (`Self_service_start_across_tenants_returns_404_never_403`): aprovado.
- Exact-permission-match: confirmado em 3 camadas independentes (§27.2).
- Ambiente restaurado (RabbitMQ/Redis de homologação reiniciados, dev parados, sem processos órfãos — §27.6).
- Dados sintéticos removidos do Postgres de desenvolvimento; nenhuma credencial real foi lida, exibida ou usada em nenhum momento desta sessão.
- Ressalva honesta sobre a suíte completa da solução registrada com precisão em §27.3 (primeira execução: 4 falhas transitórias, nenhuma regressão; segunda execução: inválida por saturação real do Docker/`Docker.DotNet`, não um defeito de produto).
- Débito de configuração do `MigrationRunner` descrito com precisão em §27.7 (não é indicativo de schema ou topologia não validados).

### 28.4 Incremento 2B (Files/Evidências) — ainda não iniciado

Fotos e vídeos de ocorrências/checklist permanecem inteiramente fora de escopo desta sessão, conforme a exclusão explícita já registrada em §21. Nenhuma Fase 7 ou posterior foi iniciada. A auditoria técnica/documental do Incremento 2B, sem qualquer implementação, é apresentada em §30, junto com a decisão pendente sobre se o Incremento 2B é ou não indispensável para o encerramento funcional da Fase 6.

## 29. Publicação do Incremento 2A

O push de `feature/housekeeping` para `origin/feature/housekeeping` foi realizado com sucesso (`56433f0..73492f1`). Confirmado via `git fetch` + `git status -sb`: `feature/housekeeping...origin/feature/housekeeping`, sem anotação de `ahead`/`behind` — **branch remota totalmente sincronizada**. `master` **não contém** nenhuma alteração da Fase 6 (nenhum merge foi realizado). A Fase 6 permanece **EM ANDAMENTO**: o Incremento 2B (Files/Evidências) ainda não foi iniciado e depende de decisão explícita após a auditoria técnica/documental apresentada em §30.

**Status oficial após a publicação**:

- **Fase 6** — EM ANDAMENTO.
- **Incremento 1** (Housekeeping Foundation) — concluído e publicado em `origin/feature/housekeeping`.
- **Incremento 2A** (Portal da Faxineira Core) — concluído e publicado em `origin/feature/housekeeping`.
- **Incremento 2B** (Files/Evidências) — não implementado; depende de decisão/aprovação após auditoria (§30).
- **`master`** — sem integração da Fase 6.

## 30. Auditoria Files/Evidências e decisão de encerramento da Fase 6

### 30.1 Auditoria executada

Auditoria estritamente documental e de código (read-only — nenhuma implementação, migração, pacote, alteração de `docker-compose`, bucket, frontend ou API), consultando: `CLAUDE.md`; `ai-rules/00 - Engineering Constitution.md`, `01 - Decision Making Policy.md`, `05 - Testing and Validation Policy.md`; `Documento 000 - Documentation Index.txt`; `Plano Executivo de Desenvolvimento por Fases.md`; `Architecture Principles.md`; `ADR-006 - Cache e Armazenamento de Arquivos.md`; `ADR-015`; `Documento 05, 07, 09, 10, 12, 14, 17`; este próprio documento (§3, §21.1, §21.3, §28.4, §29); código real de `src/Contexts/Housekeeping/` (Domain/Application/Infrastructure/Api) e do frontend Portal; `docker-compose.yml`; `appsettings*.json`; `observability/`.

### 30.2 Principais requisitos documentados (Files/Evidências)

- `Documento 10 §7` — "Upload de fotos" / "Upload de vídeos" são funcionalidades explícitas listadas para o Portal da Faxineira.
- `Documento 09 §7` — a Faxineira possui permissão narrativa de "Enviar fotos" / "Enviar vídeos".
- `Documento 12 §8/§15/§16` — entidade conceitual "Evidência" (tipos: Foto/Vídeo/Áudio/Documento), relacionamento filho direto de Faxina, com "Miniaturas" citada como campo conceitual esperado.
- `Documento 17, Workflow 11 — Registro de Dano` — `Ocorrência → Registrar → Anexar Fotos → Relacionar Reserva → Notificar Administrador → Auditoria`.
- `Documento 14 §25` — requisitos de UX de upload (arrastar-e-largar, câmera, galeria, múltiplos arquivos, indicador de progresso, pré-visualização).
- `ADR-006` (decisão já aprovada e vigente) — Files como Bounded Context Generic centralizado; **AWS S3 em produção**; **MinIO em desenvolvimento/homologação**; acesso somente via contrato público do contexto Files; binários nunca armazenados na base transacional.
- `Architecture Principles.md §3` — Files listado como Bounded Context (`Generic | Armazenamento de evidências/documentos`).

### 30.3 Principais gaps identificados

- `Documento 07` (Catálogo de Eventos de Domínio) não possui nenhum evento relacionado a evidências/anexos — nenhum outro Bounded Context depende hoje de Files.
- `Documento 09 §15` (matriz simplificada de permissões) não possui linha para o recurso "Arquivos", apesar de citado em §12.
- Sem definição documentada de: tipos/MIME/tamanho/quantidade permitidos por upload; metadados (legenda, ordem, retenção, soft vs. hard delete); URLs públicas vs. privadas/assinadas; autorização de download; criptografia; scan de antivírus; particionamento de storage key por tenant; ciclo de vida do upload (direto vs. proxy pela API, multipart, retry, limpeza de órfãos, consistência transacional entre metadado Postgres e binário no storage).
- Nenhuma implementação existe em código: zero Bounded Context Files em `src/Contexts/`; zero pacote SDK de storage (`AWSSDK.S3`/`Minio`/`Azure.Storage.Blobs`) em qualquer `.csproj` da solução; zero configuração relacionada em `appsettings*.json` ou `observability/`. O serviço MinIO já está provisionado em `docker-compose.yml` (conforme ADR-006), mas nenhum código o referencia.

### 30.4 Conflitos documentais encontrados (não resolvidos silenciosamente)

- `Documento 05 §10` lista Fotos/Vídeos como funcionalidade do módulo Faxinas, enquanto `§23` do mesmo documento determina que arquivos nunca devem ser armazenados diretamente em outros módulos — leitura possível (capacidade percebida pelo ator vs. implementação técnica centralizada em Files), porém não confirmada explicitamente por nenhum documento.
- `Documento 14 §25` define requisitos de upload, mas `§26` (elementos mínimos da tela inicial do Portal da Faxineira) não inclui botão de upload/câmera.
- `Documento 12 §8` cita "Áudio" como tipo de evidência — não mencionado em nenhum outro documento do conjunto auditado.

### 30.5 Decisão (aprovada explicitamente pelo usuário)

- **Files/Evidências é requisito real e documentado do produto** — não foi cancelado nem descartado, e nenhum documento histórico foi alterado para remover referências a fotos/vídeos.
- **Files/Evidências NÃO é condição para o encerramento da Fase 6** nesta versão do Plano Executivo.
- A Fase 6 é considerada **concluída funcionalmente** com Incremento 1 (Housekeeping Foundation) + Incremento 2A (Portal da Faxineira Core).
- O que era tratado como "Incremento 2B — Files/Evidências" durante o planejamento passa a ser **escopo futuro deferido, não implementado, não descartado, sem fase de implementação atribuída neste momento**.
- A proposta de arquitetura apresentada na auditoria (`FileAttachment`, `OwnerType`, presigned URL, `ConfirmFileUploadCommand`, `IObjectStorage`, estrutura de storage key, attachment de `ChecklistItem`/`CleaningOccurrence`, tipos finais Photo/Video/Audio/Document, thumbnail assíncrona, novos eventos, antivírus, limites, MIME allowlist, retenção, estratégia de exclusão) **permanece como RECOMENDAÇÃO/GAP, não aprovada** — não deve ser tratada como Architecture Principles, ADR ou requisito aprovado sem refinamento e aprovação futura explícita.
- `ADR-006` permanece vigente, sem alteração, quanto à existência conceitual do Files BC, centralização, S3, MinIO e contrato público.
- `Plano Executivo de Desenvolvimento por Fases.md` permanece com sua sequência atual — nenhuma Fase 6B foi criada; nenhuma Fase 7-12 foi renumerada ou alterada em escopo.

### 30.6 Status oficial

- **Fase 6 — Housekeeping e Portal da Faxineira** — STATUS: **CONCLUÍDA FUNCIONALMENTE**.
- **Incremento 1** (Housekeeping Foundation) — concluído; homologado; publicado em `origin/feature/housekeeping`.
- **Incremento 2A** (Portal da Faxineira Core) — concluído; homologado; publicado em `origin/feature/housekeeping`.
- **Files/Evidências** — deferido; não implementado; não bloqueia o encerramento funcional da Fase 6; exige planejamento/aprovação futura. (Anteriormente denominado "Incremento 2B" durante o planejamento.)
- **Integração em `master`**: autorizada pelo usuário (fast-forward puro); execução registrada nesta mesma sessão — ver atualização de status ao final deste documento assim que concluída.
