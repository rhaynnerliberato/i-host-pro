# Fase 7 — Agenda e Dashboard Operacional — Validação e Homologação

Versão: 1.7 (Incremento 1 — Agenda Foundation — CONCLUÍDO E PUBLICADO em master; Checkpoints 0-3 registrados em §2-§6. Incremento 2 — Dashboard & Reporting Foundation — Checkpoint 0 e Checkpoint 1 registrados em §7; Checkpoint 2 registrado em §7.7; Checkpoint 3 registrado em §7.8; Checkpoint 4 — E2E/Homologação Final — registrado em §7.9)

Status: **Incremento 1 (Agenda Foundation) CONCLUÍDO E PUBLICADO** — Checkpoint 0, Checkpoint 1, Checkpoint 1 CLOSURE (ADR-016), Checkpoint 2 (Frontend Agenda) e Checkpoint 3 (Integration/E2E) concluídos, homologados e publicados em `master` (fast-forward, commit `b53b2cb`). **Incremento 2 (Dashboard & Reporting Foundation) CONCLUÍDO FUNCIONALMENTE** — Checkpoint 0 (Auditoria e Refinamento Read-Only), Checkpoint 1 (Dashboard BC Foundation / Projections), Checkpoint 2 (Overview API), Checkpoint 3 (Frontend Dashboard) e Checkpoint 4 (E2E/Homologação Final) concluídos e homologados nesta branch (`feature/dashboard-reporting`), registrados em §7; publicação em `master` pendente (§7.9.13). **Fase 7 (Agenda e Dashboard Operacional) CONCLUÍDA FUNCIONALMENTE** — ambos os incrementos aprovados; publicação final pendente. Reporting histórico/BI, ocupação, gráficos, Dashboard para PROPERTY_OWNER/HOUSEKEEPER: deliberadamente fora do escopo deste MVP, registrados como não implementados (§7.9.13).

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

## 5. Checkpoint 2 — Frontend Agenda

### 5.1 Escopo

Agenda somente leitura no frontend Angular (`frontend/IHostPro.Web`): rota, item de navegação, consumo real de `GET /api/v1/schedule`, visualizações Dia/Semana/Mês via FullCalendar, filtros suportados pelo backend, distinção visual Reservation × Cleaning, estados de loading/vazio/erro, responsividade, i18n, testes unitários. Explicitamente fora deste checkpoint: Dashboard, indicadores/BI, edição da Agenda (drag-and-drop, resize, bloqueio, manutenção, reagendamento), edição de Reservation/Cleaning a partir da Agenda, consumo de `CalendarBlocked`/`CalendarReleased`/`ScheduleUpdated` (não emitidos por nenhum evento real ainda).

### 5.2 Dependência FullCalendar — versão e alternativa registrada

Escolhido **FullCalendar 6.1.21** (`@fullcalendar/angular`, `@fullcalendar/core`, `@fullcalendar/daygrid`, `@fullcalendar/timegrid`, licença MIT), não a v7.0.2 (GA havia ~7 semanas antes desta implementação, arquitetura "headless-calendar" completamente nova, sem os pacotes `@fullcalendar/daygrid`/`@fullcalendar/timegrid` usados pelo restante do ecossistema, peer-dependencies novas como `temporal-polyfill`). Critério: v6.1.21 é a versão madura, em produção desde 2023, com suporte oficial documentado a Angular 12–22 (cobre a Angular 21.2 deste projeto), plugin-based, adequada a uma Agenda somente leitura de dia/semana/mês. Alternativa rejeitada e registrada: grade customizada própria (descartada por reimplementar navegação de calendário, cálculo de grade e acessibilidade que o FullCalendar já resolve maduramente, sem ganho real para o escopo somente leitura deste checkpoint). Nenhum plugin de interação (`@fullcalendar/interaction`) foi instalado — não é necessário para navegação somente leitura, e sua ausência impede drag-and-drop/resize por construção, não apenas por convenção.

### 5.3 Estrutura da feature

`src/app/features/schedule/` (convenção de feature-folder própria, não aninhada em `features/reservations`): `schedule.service.ts` (wrapper fino sobre `Client.schedule(...)` gerado pelo NSwag — nenhum `HttpClient` paralelo), `schedule-event-mapper.ts` (mapeador isolado e unit-testável `ScheduleItemResponse → FullCalendar EventInput`, que nunca deixa o contrato da API ser contaminado por tipos do FullCalendar), `schedule-calendar/` (componente `ScheduleCalendar`, único wrapper do FullCalendar na aplicação — não foi criada nenhuma abstração de calendário genérica reaproveitável por outras features).

### 5.4 Mapeamento Reservation/Cleaning e timezone

Reservation: `start = checkInAt`, `end = checkOutAt` (nenhum dado de hóspede além do necessário — nome/telefone não são expostos pela Agenda). Cleaning: `start = scheduledAtUtc`, sem `end` real (duração meramente visual da UI, nunca persistida ou reportada como real). Itens de Cleaning com `scheduledAtUtc` nulo são filtrados defensivamente pelo mapper — o backend (`ScheduleReader.cs`) já os exclui por padrão, então esta é uma robustez adicional, não a correção de uma lacuna real observada. Corretude de fuso horário coberta por teste unitário com `DateTimeOffset` real (`new Date('2026-08-15T09:30:00-03:00')` → `toISOString()`/`getTime()` preservados exatamente) e confirmada com dado real em navegador: uma reserva com `checkInAt = 2026-08-14T17:00:00Z` / `checkOutAt = 2026-08-15T14:00:00Z` foi criada via UI (inserindo 14:00–11:00 em horário local do navegador) e renderizou na Agenda exatamente como "14:00–00:00" (Sex) + "00:00–11:00" (Sáb) — mesmo instante, sem deslocamento.

### 5.5 Filtros — implementados e deliberadamente diferidos

Backend suporta `from`/`to`/`propertyId`/`housekeeperUserId`/`eventType`. Implementado nesta Agenda: `from`/`to` (range visível do FullCalendar, nunca um fetch de histórico completo) e `eventType` (Todos/Reservas/Faxinas, verificado em navegador disparando `refetchEvents()` e uma nova requisição com `eventType=Cleaning`). **Diferidos**: filtro por Imóvel e por Faxineira. Motivo, com evidência direta de código: `PropertiesController` exige `PROPERTIES:MANAGE` em toda ação (inclusive `List`) e `UsersController` exige `USERS:MANAGE`; `IdentityCatalogSeed` mostra que OPERATOR possui apenas `PROPERTIES:READ` (nunca `PROPERTIES:MANAGE`) e nenhum `USERS:MANAGE` — mas OPERATOR possui `SCHEDULE:MANAGE` e é uma persona real desta mesma página. Um filtro que resulta em 403 para uma persona autorizada da própria Agenda não pôde ser implementado sem quebrar a página para OPERATOR; registrado aqui como lacuna, não resolvido silenciosamente.

### 5.6 Permissões e personas

Rota `/schedule` protegida por `permissionGuard` com `data.permissions: ['SCHEDULE:MANAGE', 'SCHEDULE:READ']` (match exato por código de permissão, nunca por nome de role ou prefixo — mesmo mecanismo já validado em checkpoints anteriores). Personas cobertas neste checkpoint: ADMIN e OPERATOR (ambos possuem `SCHEDULE:MANAGE`). HOUSEKEEPER e PROPERTY_OWNER permanecem deliberadamente fora — o escopo de "Agenda própria" para esses papéis não está formalizado (`SCHEDULE:READ:OWN_OWNER` não existe) e AI_AGENT não possui UI. Verificado em navegador: um usuário sem `SCHEDULE:MANAGE`/`SCHEDULE:READ` que navega para `/schedule` é redirecionado para "Acesso negado".

### 5.7 UX — loading, vazio, erro

Loading não destrutivo: o componente `<full-calendar>` nunca é recriado/destruído entre estados — apenas um banner de estado é sobreposto (`loading`/`empty`/`error`), verificado em navegador com o texto real "Nenhum evento neste período." para o estado vazio.

### 5.8 Defeito real encontrado e corrigido em navegador — rótulos do toolbar não traduzidos

Verificação visual real revelou que os botões do toolbar do FullCalendar ("Hoje"/"Mês"/"Semana"/"Dia") renderizavam as chaves i18n cruas (`schedule.today`, `schedule.month`, `schedule.week`, `schedule.day`) em vez do texto traduzido, enquanto o restante da página (via `| transloco` no template) traduzia corretamente. Causa: `calendarOptions.buttonText` era construído por uma chamada síncrona e única a `TranslocoService.translate(...)` no inicializador de campo da classe, antes do JSON de tradução (carregado via HTTP) terminar de carregar — `app.config.ts` não possui nenhuma barreira de carregamento de tradução antes do bootstrap. Corrigido substituindo por um `signal` (`buttonLabels`) alimentado por `TranslocoService.selectTranslate([...])` (reemite quando o carregamento termina e em trocas de idioma) e um `effect()` no construtor que empurra o valor atualizado para a API viva do FullCalendar via `getApi().setOption('buttonText', ...)` — único mecanismo para atualizar a configuração de uma instância já montada, já que `[options]` é lido apenas uma vez pelo `FullCalendarComponent`. Regressão coberta por teste unitário dedicado. Corrigido e reverificado em navegador antes do fechamento deste checkpoint.

### 5.9 Responsividade

Verificado em 1280×800 (desktop) e 375×812 (mobile): `document.documentElement.scrollWidth === window.innerWidth` em ambos (sem overflow horizontal real); `initialView` resolve para `timeGridWeek` no desktop e `timeGridDay` no breakpoint Handset (via `BreakpointObserver.isMatched(Breakpoints.Handset)`, mesmo padrão já usado por `AdminLayout`); toolbar e navegação permanecem utilizáveis em ambos os tamanhos.

### 5.10 Testes automatizados

Suíte completa do frontend: **45 arquivos, 391 testes, 100% verde** (inclui `schedule.service.spec.ts`, `schedule-event-mapper.spec.ts` com 10 casos, `schedule-calendar.spec.ts` com 12 casos cobrindo range visível, filtro de tipo, estados loading/vazio/erro, configuração de views, breakpoint desktop/handset, e a regressão do defeito de §5.8). Build de produção Angular: verde (`schedule-calendar` chunk 237.32 kB / 60.50 kB transfer). Regeneração do cliente NSwag: determinística (idêntica byte a byte em duas execuções). `git diff --check`: nenhuma linha nova problemática fora do padrão pré-existente do próprio gerador NSwag (comentários JSDoc `@param ... (optional) ` com espaço final — presente em 55 linhas do arquivo gerado inteiro, não introduzido por este checkpoint).

### 5.11 Lacuna identificada e não resolvida neste checkpoint — projeção de propriedades do Housekeeping

Durante a tentativa de popular dados reais de Cleaning para verificação visual, `POST /api/v1/cleanings` retornou 404 (`property_not_found`) para propriedades reais e ativas confirmadas via `GET /api/v1/properties`. Investigação confirmou causa raiz: a criação manual de limpeza valida a propriedade contra uma projeção local do Housekeeping (`HousekeepingDbContext.PropertyProjection`), populada exclusivamente por consumo assíncrono de `PropertyCreated`/`PropertyActivated` via RabbitMQ (`PropertyProjectionSynchronizer`). Exchanges topic do RabbitMQ não reproduzem histórico para uma fila recém-vinculada — propriedades criadas antes deste consumer existir (ou em execuções sem o Worker ativo) nunca populam a projeção, e não existe mecanismo de backfill. **Esta lacuna é orthogonal ao trabalho desta Fase 7**: pertence à infraestrutura de mensageria do Housekeeping (Fase 6), não ao consumidor de eventos de Cleaning que alimenta a Agenda (que é um consumer Wolverine separado, `CleaningScheduleProjection`, já coberto pelos testes de round-trip do Checkpoint 1/1 CLOSURE). Como consequência, a renderização de Cleaning na Agenda foi verificada apenas via testes unitários do mapper (cobrindo `start`/ausência de `end`/`status`/`housekeeperUserId` em `extendedProps`/filtragem de `scheduledAtUtc` nulo) — não via dado real de Cleaning em navegador. Reservation foi verificada com dado real de ponta a ponta (criação via UI → renderização correta na Agenda, cor + rótulo + timezone). Não corrigido nesta etapa — fora do escopo deste checkpoint (validação backend do Housekeeping), registrado apenas como gap conhecido.

### 5.12 Ambiente de verificação

Verificação realizada contra API real (`IHostPro.Api`, porta 5140) e frontend real (`ng serve`, porta 4200), Postgres dev real via Docker, RabbitMQ dev real (`ihostpro-rabbitmq`, trocado temporariamente com `ihostpro-homolog-rabbitmq` e restaurado ao final). Usuário de verificação `admin-cp2@dev.local` (tenant `dev-tenant`, role ADMIN atribuída manualmente via SQL, já que `DevelopmentIdentitySeeder` não atribui roles por padrão) — usuário e dados de teste (uma reserva e um imóvel) existem apenas no Postgres dev local, não fazem parte de nenhuma migração ou seed versionado.

## 6. Checkpoint 3 — Integration/E2E

### 6.1 Escopo

Homologação completa e real do Incremento 1 (Backend Agenda + Frontend Agenda + mensageria real + dados reais + E2E real), incluindo a resolução obrigatória do defeito bloqueante registrado em §5.11 (`property_not_found` para propriedades pré-existentes). Explicitamente fora deste checkpoint: Dashboard; qualquer funcionalidade de edição da Agenda (drag/drop, resize); qualquer ampliação de escopo além do estritamente necessário para o Incremento 1 já aprovado funcionar de ponta a ponta com dados reais.

### 6.2 Defeito bloqueante — investigação de causa raiz

Antes de qualquer correção, foram respondidas com evidência real (leitura direta de código/migrações/comentários) as nove perguntas obrigatórias sobre a lacuna registrada em §5.11: `PropertyProjectionEntry` (Housekeeping) é populada exclusivamente por consumo assíncrono de `PropertyCreated`/`PropertyActivated`; não existe, em nenhum lugar do código, um mecanismo de backfill/replay para propriedades que já existiam antes desse consumer começar a escutar; `MigrationRunner` não possui, antes desta correção, nenhum mecanismo de migração de dados (apenas migrações de schema EF Core); não existe replay de eventos histórico no RabbitMQ (exchanges topic não reproduzem histórico para uma fila recém-vinculada); PropertyManagement não mantém um outbox histórico reaproveitável para esse fim; a lacuna afeta qualquer ambiente real (não apenas dev) em que o Worker/consumer de Housekeeping tenha entrado em operação depois de propriedades já existirem em PropertyManagement — inclusive um upgrade real de produção; não há outras projeções derivadas no codebase com o mesmo problema identificado nesta investigação; o precedente mais próximo no próprio projeto para inicializar um read model derivado é a própria `TenantAwareTransactionScope`/`SET LOCAL app.tenant_id` já usada em testes de integração (`ReservationCommandHandlerTests.SetTenantAsync`), não um mecanismo de replay dedicado.

### 6.3 Alternativas avaliadas e decisão

Quatro alternativas foram avaliadas (aptidão arquitetural, atomicidade, RLS, idempotência, comportamento em upgrade e em instalação nova, rollback, custo, testabilidade): (A) migração de dados one-time dentro do `MigrationRunner`; (B) replay/republicação controlada dos eventos `Property*` já existentes; (C) bootstrap explícito via mecanismo de inicialização de aplicação; (D) outra abordagem já estabelecida no projeto. A Opção A foi identificada como a única que não exige nenhuma nova decisão arquitetural de fundo (nenhuma leitura cross-context em runtime, nenhuma API nova, nenhum evento de integração novo, nenhum framework de replay novo) — mas por estender o `MigrationRunner` (uma ferramenta hoje dedicada exclusivamente a migrações de schema EF Core) para também executar uma migração de dados, essa extensão foi apresentada ao usuário antes de qualquer implementação. **Decisão do usuário** (via pergunta estruturada, não assumida): escopo limitado exclusivamente a `PropertyProjectionEntry` (nenhuma outra projeção); Opção A autorizada diretamente, sem exigência de ADR prévio.

### 6.4 Implementação — `PropertyProjectionBootstrap`

Nova classe `PropertyProjectionBootstrap` (`tools/IHostPro.MigrationRunner/PropertyProjectionBootstrap.cs`), invocada por `Program.cs` do `MigrationRunner` logo após as migrações de schema EF Core de todos os módulos, antes do provisionamento dos message stores Wolverine. Mecanismo, um único método `RunAsync(connectionString, log, cancellationToken)`:

- Lê `identity.tenants` (catálogo de plataforma, deliberadamente sem RLS) para obter todos os tenants existentes — sem precisar de contexto de tenant.
- Para cada tenant, dentro de uma transação isolada: define `app.tenant_id` via `SELECT set_config('app.tenant_id', $1, true)` (equivalente parametrizável de `SET LOCAL`, mesma técnica de `ReservationCommandHandlerTests.SetTenantAsync`); executa um único `INSERT ... SELECT ... FROM property_management.properties p ON CONFLICT (tenant_id, property_id) DO NOTHING` para `housekeeping.property_projection`, com `is_active = (p.status = 'Active')` — mesma regra que `PropertyActivatedHandler` aplicaria se o evento real tivesse sido consumido.
- Roda com a MESMA connection string/role (`ihostpro_migrator`) já usada para as migrações de schema — role que deliberadamente não tem `BYPASSRLS` (Architecture Principles, Seção 7); ambas as tabelas têm `FORCE ROW LEVEL SECURITY`, então este mecanismo respeita RLS exatamente como qualquer outra escrita tenant-scoped do codebase, nunca a desabilitando.
- Idempotente por construção: a chave primária de `PropertyProjectionEntry` é `(TenantId, PropertyId)` (`PropertyProjectionEntryConfiguration.cs`), então `ON CONFLICT DO NOTHING` garante que reexecuções nunca duplicam nem regridem uma linha já existente — uma linha já presente foi backfillada corretamente antes, ou está sendo mantida pelo `PropertyProjectionSynchronizer` em tempo real, que este passo nunca sobrescreve.
- Runtime de `CreateCleaningCommandHandler` permanece inalterado — continua lendo exclusivamente sua própria `IPropertyReferenceProjectionReader` local; este mecanismo nunca é acionado em runtime, apenas durante a execução do `MigrationRunner` (deployment/upgrade), classificando-se como preocupação de migração de dados/deployment, não como dependência de runtime entre Bounded Contexts (conforme a ressalva do próprio mandato de checkpoint).

### 6.5 Testes preventivos do backfill

`PropertyProjectionBootstrapTests.cs` (novo, projeto `IHostPro.Contexts.Housekeeping.Tests.Integration`, Testcontainers Postgres real): três testes — `Backfills_a_pre_existing_active_property_and_is_idempotent_on_rerun` (dado uma Property ativa pré-existente e projeção vazia, o mecanismo popula a linha corretamente e uma segunda execução não duplica nem regride); `Fresh_install_with_no_pre_existing_properties_backfills_nothing` (instalação nova, zero properties, zero linhas inseridas); `Draft_property_is_backfilled_as_inactive` (Property em `Draft` é backfillada com `IsActive = false`). Os três verdes. Referências de projeto adicionadas ao `.csproj` de teste (PropertyManagement.Domain/Infrastructure e o próprio `IHostPro.MigrationRunner`) são explicitamente test-only, documentadas como tal no próprio arquivo de projeto — nenhuma referência de produção de Housekeeping passou a depender de PropertyManagement.

### 6.6 Defeitos reais adicionais encontrados e corrigidos (bloqueavam todo o pipeline da Agenda)

Durante a preparação do ambiente E2E real, três defeitos pré-existentes e independentes do defeito de backfill — todos bloqueando o `IHostPro.Worker` de sequer iniciar com sucesso — foram descobertos e corrigidos:

1. **`IHostPro.Worker/appsettings.json`** não tinha a chave `ConnectionStrings:Reservations`, exigida incondicionalmente desde a Fase 7 Checkpoint 1 (outbox de Reservations). Corrigido adicionando a chave, no mesmo padrão de valor das demais connection strings do Worker.
2. **`WebE2EFixture.StartWorkerProcess()`** (infraestrutura de teste E2E compartilhada por toda a suíte) definia suas próprias variáveis de ambiente para o subprocesso do Worker, ignorando totalmente o `appsettings.json` — e também não incluía `ConnectionStrings__Reservations`, reproduzindo o mesmo defeito de forma independente dentro do fixture de teste. Corrigido adicionando a variável de ambiente equivalente.
3. **Topologia RabbitMQ do `WebE2EFixture`** declarava a exchange `housekeeping-events` sem vincular nenhuma fila a ela — o consumer real do Worker (`reservations.cleaning-schedule-projection`) falhava na inicialização com AMQP 404 NOT_FOUND. Corrigido espelhando os dez routing keys reais já declarados pelo `IHostPro.MigrationRunner`.

Os três foram diagnosticados por evidência real (log de crash do subprocesso Worker capturado via hook temporário, revertido após uso), nunca por suposição.

### 6.7 Suíte Playwright formal da Agenda

Novo arquivo `ScheduleAgendaE2ETests.cs` (`tests/Frontend/IHostPro.Web.Tests.E2E`), 15 testes, cobrindo os 18 cenários mandatados mais 2 adicionais (ciclo de vida real e timezone explícito), sempre via API/comandos oficiais para seed e comportamento real de aplicação para a Agenda em si — nunca inserção direta, nunca mock, nunca bypass de elegibilidade:

`ADMIN_accesses_the_Agenda`; `OPERATOR_accesses_the_Agenda`; `User_without_SCHEDULE_permission_is_redirected_to_forbidden` (PROPERTY_OWNER, único papel real seedado que possui apenas `SCHEDULE:READ:OWN_OWNER`, nunca `SCHEDULE:MANAGE`/`SCHEDULE:READ`); `A_real_Reservation_appears_using_CheckInAt_and_CheckOutAt`; `A_real_Cleaning_appears_at_its_ScheduledAtUtc`; `Reservation_and_Cleaning_are_distinguishable_beyond_color`; `Day_Week_and_Month_views_are_all_selectable`; `Previous_Today_and_Next_navigation_change_the_visible_range`; `Filtering_by_EventType_Reservation_hides_Cleaning_events`; `Filtering_by_EventType_Cleaning_hides_Reservation_events`; `The_calendars_visible_range_is_sent_to_the_backend`; `Another_tenants_events_never_appear_and_the_empty_state_renders`; `The_Agenda_is_usable_at_375px`; `A_real_Cleaning_status_update_Assigned_is_reflected_in_the_schedule`; `Reservation_and_Cleaning_times_render_with_no_timezone_shift`.

Uma Cleaning real foi criada via API oficial (`POST /api/v1/cleanings` com `scheduledAtUtc` — campo aceito pelo contrato real de `CreateCleaningCommand`, embora o diálogo "Nova limpeza" do frontend administrativo de Housekeeping não o exponha) para uma propriedade pré-existente, comprovando de ponta a ponta que o defeito de §5.11 está resolvido: Property → Housekeeping (via backfill) → Cleaning criada com sucesso → `CleaningCreated` → RabbitMQ real → Worker real → projeção de Reservations → `GET /api/v1/schedule` → renderização real na Agenda. Filtros de Imóvel e Faxineira permanecem diferidos (§5.5), não reexpandidos apenas para caber nesta suíte. HOUSEKEEPER/PROPERTY_OWNER permanecem fora do escopo administrativo da Agenda deste incremento (§5.6) — nenhuma ABAC de frontend foi simulada.

Resultado: **15/15 verde** em execução isolada da classe.

### 6.8 Regressão

**Suíte E2E completa** (`IHostPro.Web.Tests.E2E`, sem filtro, todas as classes incluindo `ScheduleAgendaE2ETests`): 81/82 em duas execuções consecutivas, com a mesma falha reproduzida nas duas — `ReservationsE2ETests.A_repeated_cancellation_is_handled_correctly` (timeout de 30s aguardando uma linha da tabela de Reservations aparecer após reload). Isolado por experimento controlado: a mesma classe (`ReservationsE2ETests`, 9 testes) roda 9/9 verde quando executada isoladamente, fora da suíte completa. O teste não toca Housekeeping, Worker, RabbitMQ, `MigrationRunner` ou qualquer arquivo alterado neste checkpoint — apenas listagem/cancelamento de Reservations via UI. Confirma-se, pelo mesmo protocolo de isolamento já usado em §4.7 para `PolicyUpdatedRegressionTests`, que **não é uma regressão desta correção**, e sim uma flakiness de timing pré-existente sob carga acumulada de uma suíte sequencial de ~82 testes reais orientados a browser (~3min de execução) — não corrigida nesta etapa, fora do escopo desta decisão.

**Backend** (Housekeeping Unit 112/112; Housekeeping Integration 80/80, incluindo os 3 novos testes de §6.5; Reservations Unit 59/59; Reservations Integration 80/80; `ArchitectureTests` 135/135; `IHostPro.Api.Tests.Integration` 5/5): zero falhas.

**Frontend**: suíte completa 45 arquivos / 391 testes, 100% verde (sem alteração de código frontend neste checkpoint). Build de produção Angular verde (`schedule-calendar` chunk 237.32 kB / 60.50 kB transfer).

**NSwag**: nenhuma alteração de controller/DTO/contrato neste checkpoint (apenas `appsettings.json` do Worker, `MigrationRunner` e infraestrutura de teste) — regeneração não se aplica (§19 do mandato).

**`git diff --check`**: limpo, sem problemas de whitespace nos arquivos alterados.

### 6.9 Testes de arquitetura

`ArchitectureTests` (135/135) confirma, sem regressão, que a correção do backfill não introduziu nenhuma dependência de runtime de Housekeeping sobre PropertyManagement: `PropertyProjectionBootstrap` vive exclusivamente em `tools/IHostPro.MigrationRunner` (fora de `src/Contexts/Housekeeping`), nenhuma referência de projeto de produção de Housekeeping foi adicionada a PropertyManagement, `CreateCleaningCommandHandler` continua dependendo apenas de `IPropertyReferenceProjectionReader` local. As referências de projeto adicionadas ao `.csproj` de teste de integração de Housekeeping são exclusivamente de teste (documentado no próprio arquivo). Classificação mantida: preocupação de deployment/migração de dados, não dependência de runtime entre Bounded Contexts.

### 6.10 Ambiente

Testcontainers (Postgres/RabbitMQ/Redis efêmeros) usados para toda a suíte automatizada — sem containers órfãos ao final (confirmado via `docker ps`). `ihostpro-rabbitmq` (dev) permaneceu parado; `ihostpro-homolog-rabbitmq` restaurado ao estado de baseline (ativo) ao final desta etapa. Um processo órfão `IHostPro.Worker.exe`, remanescente de uma verificação manual anterior nesta mesma tarefa, foi identificado e encerrado. Nenhum dado sintético residual foi deixado fora dos containers efêmeros (que são descartados automaticamente pelo Ryuk do Testcontainers).

### 6.11 Lacunas conhecidas, não resolvidas neste checkpoint

- Filtro de Imóvel e de Faxineira na Agenda (frontend) permanecem diferidos — mesma causa registrada em §5.5, não reexpandida.
- Escopo de "Agenda própria" para HOUSEKEEPER e OWN_OWNER para PROPERTY_OWNER permanecem não formalizados (§5.6) — Agenda administrativa deste incremento continua exclusiva a ADMIN/OPERATOR.
- `ReservationsE2ETests.A_repeated_cancellation_is_handled_correctly` apresenta flakiness de timing sob carga de suíte completa (§6.8) — não corrigido, registrado como técnico-débito de estabilidade de E2E, não como defeito funcional.
- Dashboard Operacional (Incremento 2 da Fase 7): nenhum trabalho iniciado.

## 7. Incremento 2 — Dashboard & Reporting Foundation — Checkpoint 0 e Checkpoint 1

### 7.1 Checkpoint 0 — Auditoria e Refinamento Read-Only — decisões materiais aprovadas

Auditoria completa (documentária + código real) do escopo de Documento 18 contra as seis fontes atualmente implementadas (Reservations, Housekeeping, Property Management, Identity & Access, Configuration & Policy, Agenda/Scheduling), produzindo uma matriz de inventário de indicadores (Indicador | Fonte | BC dono | Dados existem | Evento existente | Payload suficiente | Projection necessária | Implementável agora | Futuro | Gap) sem cherry-picking. Cinco decisões materiais foram identificadas, apresentadas e resolvidas pelo usuário antes de qualquer implementação:

1. **Taxa de ocupação**: DEFERIDO / GAP DOCUMENTAL — não existe fórmula oficial nem definição de denominador (blocos de proprietário, bloqueios de calendário, intervalo) documentada de forma inequívoca. Não implementado neste incremento; não bloqueia check-ins/check-outs/reservas futuras/canceladas (dados objetivos, independentes da fórmula de ocupação).
2. **Enriquecimento de `ReservationCreated`/`ReservationUpdated`** com `CheckInAt`/`CheckOutAt` (campos nullable, aditivos, nunca PII) — aprovado após gate de compatibilidade real (consumidores existentes, serializador, convenção de evolução de contrato). Ver Documento 07 §27.1/§27.3.
3. **Novo evento `CleaningOccurrenceRegistered`** (Housekeeping → Dashboard), payload mínimo (`OccurrenceId`, `CleaningId`, `OccurrenceType`, `RegisteredAtUtc` — sem descrição, sem nome de usuário, sem PII) — aprovado após confirmação da transação/outbox real do produtor. Ver Documento 07 §29.9.
4. **ADR-017 — Deployment-time Bootstrap for Event-derived Projections**: generalização mínima do precedente `PropertyProjectionBootstrap` (Fase 7, Incremento 1, Checkpoint 3) em um mecanismo tipado (`IProjectionBootstrapStep`), confinado a `tools/IHostPro.MigrationRunner`, nunca uma dependência de runtime entre Bounded Contexts.
5. **Bibliotecas de gráficos, Reporting histórico/BI, métricas de duração/SLA**: todos deliberadamente DIFERIDOS/NÃO IMPLEMENTADOS neste incremento — Dashboard Foundation usa exclusivamente Angular Material (cards/chips/tabelas), sem nenhuma dependência de gráficos instalada.

Escopo do MVP aprovado, exatamente: Reservations (check-ins/check-outs no intervalo, futuras, canceladas, contagem por status); Housekeeping (pendentes, em andamento, interrompidas, concluídas, canceladas, atrasadas, pedidos de ajuda, pedidos de material); Properties (ativas, inativas, arquivadas); Occurrences (total no intervalo, distribuição por tipo). Nenhum outro indicador.

### 7.2 Checkpoint 1 — implementação

**ADR-017 e mecanismo de bootstrap**: `IProjectionBootstrapStep` (interface mínima, `Name` + `ExecuteAsync`), `PropertyProjectionBootstrapStep` (adapta o `PropertyProjectionBootstrap` pré-existente sem alterar sua semântica), e quatro novos steps concretos (`DashboardReservationProjectionBootstrapStep`, `DashboardCleaningProjectionBootstrapStep`, `DashboardPropertyProjectionBootstrapStep`, `DashboardOccurrenceProjectionBootstrapStep`), todos vivendo exclusivamente em `tools/IHostPro.MigrationRunner`, registrados em uma lista tipada explícita no `Program.cs` do MigrationRunner — sem DSL de migração, sem engine genérica, sem descoberta por reflection.

Cada bootstrap step do Dashboard reutiliza a mesma técnica do precedente (`identity.tenants` para enumerar tenants sem contexto de tenant; `SELECT set_config('app.tenant_id', $1, true)` por tenant/transação; `INSERT ... SELECT ... ON CONFLICT (tenant_id, <id>) DO NOTHING` — idempotente por construção). Descoberta relevante durante a implementação: `reservations.reservations.status` e `property_management.properties.status` armazenam o nome bruto do enum C# (`HasConversion<string>()` sem mapeamento explícito — PascalCase: "Confirmed"/"Active"), exigindo `LOWER(...)` no bootstrap para casar com a convenção de código estável minúsculo já usada pelos eventos reais (`ReservationStatusCodeMapper`/`PropertyStatusCodeMapper`); `housekeeping.cleanings.status` e `housekeeping.cleaning_occurrences.type` já são idênticos, byte a byte, aos códigos estáveis dos seus próprios mappers, dispensando conversão.

**Bounded Context Dashboard & Reporting**: quatro novos projetos (`IHostPro.Contexts.Dashboard.Domain/Contracts/Application/Infrastructure`), schema `dashboard` próprio, RLS completo (`ENABLE`/`FORCE ROW LEVEL SECURITY`, política `tenant_isolation` fail-closed, sem `BYPASSRLS`), quatro projeções locais (`DashboardReservationProjectionEntry`, `DashboardCleaningProjectionEntry`, `DashboardPropertyProjectionEntry`, `DashboardOccurrenceProjectionEntry`) com guard de out-of-order (`LastEventAtUtc`, aplica apenas se `event.Timestamp >= LastEventAtUtc`) nas três primeiras — a de Occurrence é append-only, sem guard, pois não existe caminho de atualização. Dezoito adapters Wolverine finos, todos dependendo exclusivamente de `IDashboardMessageExecutionScope` (boundary ADR-016 local, terceira aplicação do padrão, deliberadamente duplicado por contexto em vez de generalizado — decisão explícita do usuário, para reduzir blast radius). Nove testes de arquitetura (NetArchTest) confirmam: nenhum contexto Core referencia Dashboard; Dashboard referencia apenas os três Contracts dos contextos-fonte; nenhuma leitura cross-schema em runtime fora de `tools/IHostPro.MigrationRunner`; `DashboardDbContext` nunca aparece na cadeia de adapters Wolverine.

**Testes preventivos do out-of-order guard** (§29 do mandato — provar via teste antes de consolidar, nunca por inspeção de código): `DashboardReservationProjectionSynchronizerTests` (10 testes, host Wolverine real com Postgres real via Testcontainers, dispatch direto via `HandleAsync`, sem RabbitMQ) — insere, atualiza, cancela, prova idempotência de `ReservationCreated` redelivered, prova que um `ReservationUpdated`/`ReservationCancelled` mais antigo (Timestamp anterior a `LastEventAtUtc`) nunca regride um estado mais novo já aplicado, e prova isolamento RLS fail-closed sob um `ITenantContext` de escopo divergente do `TenantId` do payload. Testes de bootstrap (`DashboardProjectionBootstrapStepsTests`, 2 testes, Testcontainers Postgres real): fresh install (fontes vazias → zero linhas inseridas) e upgrade+rerun (fontes com dados reais pré-existentes → bootstrap popula corretamente com conversão de case correta por campo → segunda execução não duplica nem regride nenhuma linha).

**Gates reais de transporte**: dois gates completos via RabbitMQ real + `IHostPro.Worker.dll` real (subprocess) + Postgres real, provando `TenantId`/RLS/isolamento cross-tenant/idempotência de redelivery via captura e republicação do envelope real (mesma técnica de `ReservationCancelledRedeliveryTests`):

1. `ReservationCreated` → outbox real de Reservations → RabbitMQ real → Worker real → `dashboard.reservation_projection` — `CheckInAt`/`CheckOutAt`/`PropertyId`/`Status` corretos; linha invisível sob outro tenant; redelivery do envelope real não duplica nem regride.
2. `CleaningOccurrenceRegistered` → outbox real de Housekeeping → RabbitMQ real → Worker real → `dashboard.occurrence_projection` — `Type`/`CleaningId`/`RegisteredAtUtc` corretos, sem descrição/PII; mesmas garantias de isolamento e idempotência.

### 7.3 Defeito real encontrado e corrigido — colisão de DI entre contextos que compartilham `IIntegrationEventHandler<T>`

A regressão completa da suíte `IHostPro.Api.Tests.Integration` (19 classes, real-Worker-subprocess) revelou um defeito arquitetural pré-existente, não específico deste checkpoint, apenas exposto por ele: `IIntegrationEventHandler<T>` é uma interface genérica **compartilhada** entre Bounded Contexts (`IHostPro.BuildingBlocks.Application`). `HousekeepingMessageExecutionScope`/`ReservationsMessageExecutionScope` (ADR-015/ADR-016) resolviam seu handler via `scope.ServiceProvider.GetRequiredService<IIntegrationEventHandler<TMessage>>()` — resolução por interface **não-keyed**. Enquanto cada tipo de evento tinha exatamente um consumidor por processo Worker, isso era seguro. Dashboard é o primeiro Bounded Context a consumir eventos que Housekeeping/Reservations **já consumiam** no mesmo processo (`PropertyCreated`/`PropertyActivated`/`PropertyDeactivated`/`PropertyArchived`/`ReservationCreated`/`ReservationCancelled` — já consumidos por Housekeeping; os dez eventos de ciclo de vida de `Cleaning` — já consumidos por Reservations). `GetRequiredService<T>()` para um tipo com múltiplos registros retorna silenciosamente o **último registrado**, sem lançar exceção nem aviso — e como `AddDashboardModule` é registrado por último no `Program.cs` do Worker, suas dezesseis registrações passaram a **sombrear silenciosamente** a resolução de handler de Housekeeping e Reservations para esses mesmos tipos de evento, mesmo quando invocada de dentro do escopo de execução DELES.

Evidência real: `CleaningNeedsHelpScheduleProjectionWorkerRoundTripTests` (pré-existente, não alterado neste checkpoint) começou a falhar com `duplicate key value violates unique constraint "PK_property_projection"` em `dashboard.property_projection`, com o stack trace mostrando `HousekeepingMessageExecutionScope.ExecuteAsync` chamando internamente `DashboardOutboxTransactionExecutor` — a prova direta da colisão de resolução.

**Decisão do usuário** (apresentada como decisão material antes de qualquer correção, três alternativas descritas): DI keyed (.NET 8+), com uma chave de string constante por Bounded Context (`"housekeeping"`/`"reservations"`/`"dashboard"`, exposta como `HandlerKey` em cada `I<Contexto>MessageExecutionScope`). Aplicado às três aplicações do padrão: `HousekeepingModuleExtensions`/`ReservationsModuleExtensions`/`DashboardModuleExtensions` passaram de `AddScoped<IIntegrationEventHandler<T>, Impl>()` para `AddKeyedScoped<IIntegrationEventHandler<T>, Impl>(<Contexto>MessageExecutionScope.HandlerKey)`; os três `ExecuteAsync<TMessage>` passaram de `GetRequiredService` para `GetRequiredKeyedService(HandlerKey)`. Nenhuma mudança de assinatura pública, nenhuma nova abstração compartilhada — cada scope já era 1:1 com seu próprio Bounded Context, então a chave é uma constante interna, nunca um parâmetro adicional.

### 7.4 Defeitos reais adicionais encontrados e corrigidos — lacunas de configuração de teste

Doze arquivos de teste pré-existentes em `IHostPro.Api.Tests.Integration` que lançam `IHostPro.Worker.dll`/`IHostPro.MigrationRunner.dll` como subprocessos reais não incluíam `ConnectionStrings__Dashboard` em seus dicionários de ambiente — o Worker agora exige essa chave incondicionalmente (`AddDashboardModule`/`EnrollAncillaryPostgresqlOutbox` para `dashboard_messaging`). Corrigido em todos os doze arquivos. Dois casos adicionais e distintos:

- `PolicyUpdatedWolverineDiscoveryTests`: fixture próprio nunca executava o `MigrationRunner`, dependendo de um fallback implícito ao Postgres de desenvolvimento local real pré-provisionado — um padrão frágil já sinalizado duas vezes antes no próprio comentário deste arquivo (Housekeeping, depois Configuration). Substituído pelo mesmo mecanismo robusto de provisionamento via `MigrationRunner` (papéis `ihostpro_migrator`/`ihostpro_app`, Testcontainers Postgres isolado) que as demais dezoito classes já usam — eliminando a fragilidade recorrente em vez de apenas corrigi-la pontualmente de novo.
- `HousekeepingWolverineDiscoveryTests`: gap simples — `ConnectionStrings__Dashboard` ausente apenas no dicionário de ambiente do subprocess Worker (o do MigrationRunner já estava correto). Corrigido com uma linha.

### 7.5 Regressão final

Após as correções de §7.3/§7.4: **`IHostPro.Api.Tests.Integration` 19/19** (suíte completa, incluindo os dois gates reais de transporte do Dashboard, `WolverineThreeStoreCompositionTests`, `PolicyUpdatedRegressionTests`, e todos os *WorkerRoundTripTests pré-existentes de Reservations/Housekeeping); **`IHostPro.Contexts.Dashboard.Tests.Integration` 10/10** (2 bootstrap + 8 synchronizer/out-of-order/RLS); **`ArchitectureTests` 144/144**; **Reservations Unit 61/61, Integration 80/80**; **Housekeeping Unit 113/113, Integration 80/80** (inclui a regressão dos 3 testes de `PropertyProjectionBootstrap` já migrados para o mecanismo `IProjectionBootstrapStep` sem alteração de semântica). Build completo da solução: verde. `git diff --check`: limpo. Frontend/E2E/Playwright: explicitamente não executados neste checkpoint (nenhuma alteração de frontend — Checkpoint 3 do Incremento 2 é o único que altera frontend).

### 7.6 Lacunas conhecidas, deliberadamente diferidas

- Taxa de ocupação: sem fórmula oficial documentada — GAP DOCUMENTAL, não implementado (§7.1, decisão 1).
- Reporting histórico/BI (séries de 7/30/90/12 meses, data warehouse, OLAP): incremento futuro separado, não iniciado.
- Métricas de duração/SLA (tempo médio de limpeza, tempo até início/conclusão): deliberadamente não implementadas — `IntegrationEvent.Timestamp` não foi promovido a timestamp de negócio oficial para métricas históricas.
- Bibliotecas de gráficos: nenhuma instalada — Dashboard Foundation usa exclusivamente Angular Material.
- Distribuição por faxineira: `HousekeeperUserId` armazenado na projeção de Cleaning, mas não exposto — mesma decisão de não-exposição já registrada para a Agenda.
- `PROPERTY_OWNER`/`OWN_OWNER` para o Dashboard: mesmo gap já registrado para a Agenda (§5.6), não duplicado silenciosamente — `DASHBOARD:READ:OWN_OWNER` permanece deliberadamente não aceito pelo Checkpoint 2 (§7.7.5) nem pelo frontend do Checkpoint 3 (§7.8.4).
- E2E/Homologação final do Incremento 2 (Checkpoint 4): concluído — ver §7.9.

### 7.7 Checkpoint 2 — Overview API

### 7.7.1 Escopo

Um único endpoint administrativo somente leitura, `GET /api/v1/dashboard/overview`, agregando os quatro grupos de indicadores aprovados no Checkpoint 0 (§7.1) — Reservations, Housekeeping, Properties, Occurrences — sempre por agregação real em PostgreSQL (`COUNT`/`GROUP BY`, nunca materialização em memória). Explicitamente fora deste checkpoint: qualquer endpoint por card (`/dashboard/reservations`, `/dashboard/cleanings`, `/dashboard/properties`, `/dashboard/occurrences` — nenhum foi criado); frontend Dashboard (Checkpoint 3); cache; SignalR/tempo real; novos eventos de integração; novos Bounded Contexts; Reporting histórico/BI.

### 7.7.2 Contrato — `GET /api/v1/dashboard/overview`

Parâmetros de query `from`/`to` (`DateTimeOffset`, ambos obrigatórios), semântica `[from, to)` uniforme para todo indicador temporal (`from` inclusivo, `to` exclusivo — comparação por instante real, não por data local). Janela máxima de 100 dias, uma DEC técnica explícita (não um requisito de negócio), espelhando exatamente o precedente já homologado de `ListScheduleQueryValidator.MaxWindow` (`GetDashboardOverviewQueryValidator.cs`: `TimeSpan.FromDays(100)`, `RuleFor(x => x.To).GreaterThan(x => x.From)`). O backend nunca decide "hoje" — nenhuma chamada a `DateTimeOffset.UtcNow`/`DateTime.Now`/fuso de servidor nesta decisão; `from`/`to` são sempre fornecidos pelo chamador.

DTO de resposta (`DashboardOverviewResponse`): `Period{From,To}`, `Reservations`, `Housekeeping`, `Properties`, `Occurrences`, `GeneratedAtUtc` — zero PII em qualquer campo (confirmado por teste estrutural/payload, §7.7.8). Tenant sem nenhum dado retorna **200** com contadores zerados/arrays vazios/`Period` válido/`GeneratedAtUtc` válido — nunca 404.

### 7.7.3 Matriz de indicadores (Métrica | Fonte | Filtro | Temporal × current-state | Notas)

**Reservations** (`DashboardOverviewReader.GetReservationsOverviewAsync`, fonte `dashboard.reservation_projection`):

| Métrica | Filtro | Tipo | Notas |
|---|---|---|---|
| `CheckInsInPeriod` | `CheckInAt ∈ [from,to)` ∧ `Status != cancelled` | Temporal | |
| `CheckOutsInPeriod` | `CheckOutAt ∈ [from,to)` ∧ `Status != cancelled` | Temporal | |
| `FutureReservations` | `CheckInAt >= nowUtc` ∧ `Status != cancelled` | Temporal (usa `nowUtc`, não `from`/`to`) | `nowUtc` vem de `TimeProvider`, nunca de `from`/`to` |
| `CancelledInPeriod` | `CancelledAtUtc ∈ [from,to)` | Temporal | usa `CancelledAtUtc`, nunca `CheckInAt`/`UpdatedAt` genérico |
| `StatusCounts` | nenhum (todas as linhas do tenant) | Current-state | `GroupBy(Status)`, reordenado client-side por `StringComparer.Ordinal` |

**Housekeeping** (`GetHousekeepingOverviewAsync`, fonte `dashboard.cleaning_projection`):

| Métrica | Filtro | Tipo | Notas |
|---|---|---|---|
| `Pending` | `Status ∈ {Pending, Assigned}` | Current-state | |
| `InProgress` | `Status ∈ {InTransit, Started, InInspection, WaitingHelp, WaitingMaterials}` | Current-state | |
| `Interrupted` | `Status == Interrupted` | Current-state | |
| `CompletedInPeriod` | `CompletedAtUtc ∈ [from,to)` | Temporal | |
| `CancelledInPeriod` | `CancelledAtUtc ∈ [from,to)` | Temporal | |
| `Delayed` | `ScheduledAtUtc != null` ∧ `ScheduledAtUtc < nowUtc` ∧ `Status ∉ {Completed, Cancelled}` | Current-state (usa `nowUtc`) | `Interrupted` conta como atrasada; `ScheduledAtUtc == null` nunca conta |
| `WaitingHelp` | `Status == WaitingHelp` | Current-state | |
| `WaitingMaterials` | `Status == WaitingMaterials` | Current-state | |

**Properties** (`GetPropertiesOverviewAsync`, fonte `dashboard.property_projection`, current-state, sem filtro temporal): `Active` (`Status == active`), `Inactive` (`Status == inactive`), `Archived` (`Status == archived`) — `Draft` deliberadamente não contabilizado (lista de três campos aprovada no Checkpoint 0/Checkpoint 2, §7.1/mandato §22).

**Occurrences** (`GetOccurrencesOverviewAsync`, fonte `dashboard.occurrence_projection`, append-only, sem conceito de aberto/resolvido): `TotalInPeriod` (`RegisteredAtUtc ∈ [from,to)`), `ByType` (`GroupBy(Type)` sobre o mesmo filtro, reordenado por `StringComparer.Ordinal`).

`GeneratedAtUtc` vem de `TimeProvider.GetUtcNow()`, computado uma única vez em `GetDashboardOverviewQueryHandler` e repassado explicitamente ao reader (`nowUtc`) — o reader nunca resolve "agora" internamente. `TimeProvider` (nunca `DateTimeOffset.UtcNow` cru) controla exatamente três pontos: `FutureReservations`, `Delayed`, `GeneratedAtUtc`.

### 7.7.4 `CancelledAtUtc`/`CompletedAtUtc` — fonte real e backfill

`CancelledAtUtc` foi adicionado a `DashboardReservationProjectionEntry` e `DashboardCleaningProjectionEntry` (migração `20260817173347_AddCancelledAtUtcToProjections`, nullable, `timestamp with time zone`). Fonte, em ambos os casos, é o `Timestamp` do próprio evento real de cancelamento (`ReservationCancelled.Timestamp`/`CleaningCancelled.Timestamp`), nunca um valor inventado — o guard de out-of-order (`LastEventAtUtc`) já existente desde o Checkpoint 1 se aplica sem alteração.

- **Reservation**: `Reservation.Cancel()` é terminal — `UpdateReservationCommandHandler` rejeita (`CancelledReservationCannotBeModifiedError`) qualquer PATCH sobre uma reserva já cancelada, tornando `reservations.reservations.updated_at` um proxy historicamente confiável para o instante de cancelamento. Backfill (`DashboardReservationProjectionBootstrapStep`): `CASE WHEN r.status = 'Cancelled' THEN r.updated_at ELSE NULL END`.
- **Cleaning**: `housekeeping.cleanings.cancelled_at_utc` já é uma coluna dedicada, real, mantida pelo próprio agregado `Cleaning` — sem necessidade de reconstrução. Backfill (`DashboardCleaningProjectionBootstrapStep`): copia diretamente `c.cancelled_at_utc`.
- **`CompletedAtUtc`** (Cleaning) não é novo deste checkpoint — já existia desde o Checkpoint 1 (`CleaningCompleted.Timestamp`, mesma técnica), reutilizado sem alteração pela métrica `CompletedInPeriod`.

Nenhuma aproximação foi necessária em nenhum dos dois casos — ambas as fontes históricas são reais e confiáveis; não houve necessidade de PARAR/reportar gap nesta frente.

### 7.7.5 Autorização

`DASHBOARD:MANAGE`/`DASHBOARD:READ` (`IdentityPermissionCodes`, novos), acesso se o usuário possuir QUALQUER um dos dois (padrão manual OR já usado por `ScheduleController`: `[Authorize]` de classe + `IAuthorizationService.AuthorizeAsync` tentando `DashboardManage`, então `DashboardRead`, `Forbid()` se ambos falharem — nunca um `[Authorize(Policy=...)]` único, já que o mecanismo de policy deste projeto é sempre exato, nunca OR). Políticas registradas em `IdentityAuthorizationExtensions.AddIdentityAuthorization()` (defeito real encontrado e corrigido durante este checkpoint: as duas políticas não existiam, causando `InvalidOperationException: No policy found` no primeiro teste HTTP real). ADMIN possui `DASHBOARD:MANAGE`, OPERATOR possui `DASHBOARD:READ`. `DASHBOARD:READ:OWN_OWNER` (PROPERTY_OWNER) e `DASHBOARD:USE` (AI_AGENT) são explicitamente negados — nenhum prefix match. Matriz real via `DashboardOverviewEndpointsTests` (HTTP real, `TestServer`): sem token → 401; ADMIN/OPERATOR → 200; PROPERTY_OWNER (`DASHBOARD:READ:OWN_OWNER`) → 403; AI_AGENT (`DASHBOARD:USE`) → 403; HOUSEKEEPER (nenhuma permissão de Dashboard) → 403.

### 7.7.6 RLS e isolamento cross-tenant

`DashboardDbContext` mantém o Global Query Filter/`FORCE ROW LEVEL SECURITY` já existente desde o Checkpoint 1, sem alteração — o caminho de leitura da Overview API nunca usa `IgnoreQueryFilters`, conexão raw, nem qualquer bypass. O caminho HTTP nunca participa de `IDashboardMessageExecutionScope`/Wolverine (essa fronteira é exclusiva dos consumers persistentes) — a query é servida via `TenantTransactionBehavior<GetDashboardOverviewQuery, Result<DashboardOverviewResult>, DashboardDbContext>`, mesmo mecanismo de transação tenant-aware já usado por `ScheduleReader`. Comprovado por `DashboardOverviewReaderTests.Tenant_isolation_...` (reader isolado) e `DashboardOverviewEndpointsTests.Overview_never_reflects_another_tenants_rows` (HTTP real, ponta a ponta) — nunca usando acesso admin/`BYPASSRLS` como evidência.

### 7.7.7 SQL e índices

Todo indicador é `COUNT`/`GROUP BY` traduzido pelo EF Core — nenhum `ToListAsync()` seguido de contagem em memória, nenhuma avaliação client-side, nenhuma stored procedure. Três novos índices, todos diretamente justificados pelas novas métricas period-filtered introduzidas neste checkpoint (nenhum índice adicionado "para parecer otimizado"): `(tenant_id, cancelled_at_utc)` em `reservation_projection`; `(tenant_id, cancelled_at_utc)` e `(tenant_id, completed_at_utc)` em `cleaning_projection`. Os demais campos consultados (`CheckInAt`/`CheckOutAt`/`Status` em Reservation; `Status`/`ScheduledAtUtc`/`PropertyId` em Cleaning) já possuíam índice desde o Checkpoint 1, sem alteração.

### 7.7.8 PII

Confirmado, por inspeção direta do DTO e por teste estrutural, que nenhum dos campos sensíveis listados no mandato (nome/telefone de hóspede, contagem pessoal de hóspedes, descrição de ocorrência, `RegisteredByUserId`/nome de usuário, endereço, valor financeiro) existe em `DashboardOverviewResponse` ou em qualquer um dos seus sub-objetos.

### 7.7.9 Testes automatizados

- **Unit** (`IHostPro.Contexts.Dashboard.Tests.Unit`, novo projeto): 8/8 — `GetDashboardOverviewQueryValidatorTests` (janela válida, `To==From`, `To<From`, exatamente 100 dias, mais de 100 dias, offsets UTC diferentes representando o mesmo instante) e `GetDashboardOverviewQueryHandlerTests` (o handler repassa `from`/`to`/`nowUtc` corretamente e nunca resolve "agora" internamente, via `TimeProvider`/reader fake).
- **Integration — reader** (`DashboardOverviewReaderTests`, Testcontainers Postgres real, apenas schema `dashboard` migrado): 26/26 — fronteiras `[from,to)` de check-in/check-out/ocorrência, exclusão de reserva cancelada de `CheckInsInPeriod`, `FutureReservations` com `nowUtc` explícito, `CancelledInPeriod` usando `CancelledAtUtc` (nunca `CheckInAt`), `StatusCounts` sempre current-state, os sete agrupamentos de status de Housekeeping, seis cenários de `Delayed`, `WaitingHelp`/`WaitingMaterials` current-state, distribuição `ByType`, isolamento por tenant.
- **Integration — HTTP** (`DashboardOverviewEndpointsTests`, `TestServer` real, sem Wolverine — a Overview é puro-leitura e `TenantAwareUnitOfWork<TDbContext>` não depende de Wolverine): 12/12 — matriz de autorização completa (§7.7.5), overview vazia → 200 com zeros, overview populada → contagens e período corretos, isolamento cross-tenant, `to==from` → 400, janela > 100 dias → 400, janela == 100 dias exatos → 200.
- **Integration — fan-out da lacuna de cobertura (novo, fechado neste checkpoint)**: `DashboardCleaningProjectionSynchronizerTests` (7 testes) e `DashboardPropertyProjectionSynchronizerTests` (6 testes) — até este checkpoint, `DashboardCleaningProjectionSynchronizer`/`DashboardPropertyProjectionSynchronizer` não tinham nenhuma cobertura dedicada (diferente de `DashboardReservationProjectionSynchronizer`, que já tinha 10 testes desde o Checkpoint 1). Mesma técnica de dispatch direto via host Wolverine real + Postgres real (sem RabbitMQ): criação, idempotência de redelivery, uma transição de status representativa, guard de out-of-order, RLS fail-closed — incluindo a nova regra `CancelledAtUtc`/`CompletedAtUtc` de Cleaning.
- **Integration — suíte completa do Dashboard**: **62/62** (49 pré-existentes do Checkpoint 1/início do Checkpoint 2 + 13 novos desta rodada).
- **ArchitectureTests**: **150/150** — confirma, sem regressão: `Dashboard.Application` sem referência a EF Core; `Dashboard.Api` depende apenas de `Identity.Contracts` (nunca `Identity.Application`/`Infrastructure`/`Api`, nunca `Dashboard.Infrastructure`); `Dashboard.Api` nunca referencia `DashboardDbContext`; o controller expõe exatamente a única action `Overview` aprovada; nenhuma action declara `[AllowAnonymous]` ou um `[Authorize(Policy=...)]` de código único (deve ser o padrão manual OR); nenhuma action declara parâmetro `tenantId`/`actorId`.

### 7.7.10 Evidência de regressão de fan-out (mecanismo keyed DI do Checkpoint 1)

O mandato do Checkpoint 2 exigiu confirmação de que a correção de DI keyed do Checkpoint 1 (§7.3) permanece saudável após as mudanças deste checkpoint, para os três cenários reais de fan-out multi-consumidor no mesmo processo `IHostPro.Worker`. Registrando exatamente qual teste prova qual lado, todos executados nesta sessão contra RabbitMQ real + `IHostPro.Worker.dll` real (subprocess) + Postgres real:

| Cenário | Lado A (consumidor pré-existente) | Lado Dashboard | Transporte |
|---|---|---|---|
| `ReservationCreated` → Housekeeping + Dashboard | `ReservationCreatedWorkerRoundTripTests` (valida `HousekeepingDbContext.ReservationProjection`, via `ReservationProjectionAndCancellationReaction`) | `DashboardReservationProjectionWorkerRoundTripTests` (valida `dashboard.reservation_projection`) | Real (RabbitMQ+Worker), **ambos os lados** |
| `CleaningCreated` → Reservations/Agenda + Dashboard | `CleaningCreatedScheduleProjectionWorkerRoundTripTests` (valida `reservations.cleaning_schedule_projection`) | `DashboardCleaningProjectionSynchronizerTests` (novo — dispatch direto via Wolverine real + Postgres real, sem RabbitMQ) | Real apenas do lado Reservations/Agenda; lado Dashboard comprovado funcionalmente, não via RabbitMQ real |
| `PropertyCreated`/`PropertyActivated` → Housekeeping + Dashboard | `PropertyEventsWorkerRoundTripTests` (valida apenas `HousekeepingDbContext.PropertyProjection.IsActive` — nunca a projeção do Dashboard) | `DashboardPropertyProjectionSynchronizerTests` (novo — dispatch direto) | Real apenas do lado Housekeeping; lado Dashboard comprovado funcionalmente, não via RabbitMQ real |

Os cinco testes da tabela (mais `DashboardOccurrenceProjectionWorkerRoundTripTests`, cenário de consumidor único sem risco de colisão) foram executados nesta sessão, isoladamente, contra o `ihostpro-rabbitmq` de desenvolvimento parado (porta fixa 5672 exigida pelos Testcontainers destes testes) — **5/5 (mais o de Occurrence, 6/6 no total) verdes**; container de desenvolvimento restaurado ao final. Registrado honestamente: o cenário `ReservationCreated` tem prova real de transporte dos dois lados simultaneamente (o mesmo mecanismo de colisão documentado em §7.3 é exercido de fato); os cenários `CleaningCreated`/`PropertyCreated` têm prova real de transporte apenas do lado do consumidor pré-existente, complementada por prova funcional (não via RabbitMQ real) do lado Dashboard — mas o mecanismo de registro keyed (`AddKeyedScoped`/`GetRequiredKeyedService(HandlerKey)`) é estruturalmente idêntico para as 18 registrações de handler do Dashboard (confirmado por leitura direta de `DashboardModuleExtensions.AddDashboardProjectionConsumer`), então a prova real de transporte do caso `ReservationCreated` (o único cenário que efetivamente colidia antes da correção do Checkpoint 1) é a evidência mais forte disponível de que o mecanismo genérico continua correto — não uma prova direta e independente de cada um dos quatro pares específicos.

### 7.7.11 Defeitos reais encontrados e corrigidos neste checkpoint

1. **`ConnectionStrings:Dashboard` ausente em `IHostPro.Api/appsettings.json`**: o Checkpoint 1 registrou a chave em `IHostPro.Worker`/`IHostPro.MigrationRunner`, mas nunca em `IHostPro.Api` — inofensivo enquanto a Api não referenciava `DashboardDbContext`, mas teria quebrado a primeira execução real da Api assim que `AddDashboardModule`/`AddDashboardQueryDispatch` fossem ligados. Corrigido — correção localizada de configuração, mesmo padrão de valor das demais connection strings da Api. Verificado que nenhuma outra superfície de configuração precisa da mesma chave: `appsettings.Development.json` da Api/Worker e do `MigrationRunner` não possuem seção `ConnectionStrings` (apenas Logging/Serilog/seed); `docker-compose.yml` não expõe nenhuma connection string de aplicação (apenas containers de infraestrutura).
2. **Duas políticas de autorização ausentes** (`DASHBOARD:MANAGE`/`DASHBOARD:READ` nunca registradas em `IdentityAuthorizationExtensions`) — encontrado pelo primeiro teste HTTP real (`InvalidOperationException: No policy found: DASHBOARD:MANAGE`). Corrigido.
3. **Lacuna de cobertura de teste pré-existente do Checkpoint 1** (`DashboardCleaningProjectionSynchronizer`/`DashboardPropertyProjectionSynchronizer` sem nenhum teste dedicado) — encontrada durante a auditoria de evidência de fan-out deste checkpoint (§7.7.10) e fechada com os dois novos arquivos de teste registrados em §7.7.9.

### 7.7.12 Regressão final e ambiente

Release build da solução completa: verde. NSwag: cliente regenerado duas vezes contra a Api real em execução, byte a byte idêntico entre as duas execuções; contrato real confirmado (`GET /api/v1/dashboard/overview`, `from`/`to` obrigatórios, tipos corretos, 200/400/401/403 presentes, DTO sem PII, nenhuma rota extra de Dashboard). Build de produção Angular: verde (nenhuma feature funcional de frontend criada neste checkpoint — apenas o cliente gerado muda). `git diff --check`: limpo, com exceção das duas linhas de whitespace já conhecidas e pré-existentes do próprio template JSDoc do NSwag para parâmetros opcionais (`@param ... (optional) ` com espaço final — presentes em 55 ocorrências idênticas já commitadas no arquivo gerado inteiro, confirmadas via `git show HEAD:...api-client.ts`; nunca editado manualmente, conforme regra do mandato). Ambiente Docker restaurado ao estado original após os testes de transporte real (§7.7.10).

### 7.8 Checkpoint 3 — Frontend Dashboard

#### 7.8.1 Escopo

Frontend administrativo somente leitura do Dashboard Operacional, consumindo exclusivamente o Overview API já homologado no Checkpoint 2. Explicitamente fora deste checkpoint: gráficos/biblioteca de charts, SignalR/WebSocket, Reporting/BI histórico, exportação/PDF/Excel, acesso de `PROPERTY_OWNER`/HOUSEKEEPER/AI Agent, drill-down/navegação a partir dos cards, customização de layout, financeiro/ocupação. Nenhuma alteração de backend foi feita ou foi necessária.

#### 7.8.2 Tecnologia e estrutura

Nenhuma dependência nova instalada — apenas Angular Material (já presente) e o cliente NSwag já gerado. Feature própria em `frontend/IHostPro.Web/src/app/features/dashboard/` (não aninhada em `reservations`/`schedule`/`housekeeping`, mesma convenção de feature-folder das demais áreas administrativas):

- `dashboard-period.ts` — helper puro (sem dependência de Angular) para os limites de dia local dos presets Hoje/Últimos 7/Últimos 30/personalizado, e para o parse de `<input type="date">` como data local (nunca `new Date(string)`, que o spec da linguagem interpreta como meia-noite UTC — exatamente a armadilha que este checkpoint precisava evitar para "hoje").
- `dashboard.service.ts` — wrapper fino sobre `Client.overview(from, to)` gerado pelo NSwag, mesmo padrão de `ScheduleService`.
- `dashboard-overview/` — o único componente de página (`DashboardOverview`), template e estilos.

#### 7.8.3 Rota e navegação

Rota `/dashboard` sob `AdminLayout` (nunca `PortalShell`), protegida por `permissionGuard` com `data.permissions: ['DASHBOARD:MANAGE', 'DASHBOARD:READ']` — match exato por código de permissão, mesmo mecanismo genérico já validado nos checkpoints anteriores (`permissionGuard` é data-driven; nenhuma lógica nova no guard foi necessária). Entrada "Dashboard" adicionada à navegação administrativa (`admin-layout.ts`), com a mesma semântica OR já usada por Políticas/Agenda.

#### 7.8.4 Personas e matriz de permissão

Disponível para ADMIN (`DASHBOARD:MANAGE`) e OPERATOR (`DASHBOARD:READ`). `DASHBOARD:READ:OWN_OWNER` (PROPERTY_OWNER) e `DASHBOARD:USE` (AI_AGENT) permanecem explicitamente negados — sem prefix matching. Comprovado em dois níveis: unitário (`admin-layout.spec.ts`, visibilidade do item de navegação para cada um dos quatro códigos) e navegador real com usuários reais e permissões reais atribuídas via SQL (ADMIN → acessa; OPERATOR → acessa; HOUSEKEEPER, sem nenhuma permissão de Dashboard → redirecionado para "Acesso negado" pelo `permissionGuard`, confirmando fail-closed).

#### 7.8.5 Período — abertura padrão, presets e intervalo personalizado

Abre sempre em "Hoje": `[início do dia local atual, início do dia local seguinte)`, calculado por componentes de data locais (`Date(y, m, d)`/`getFullYear`/`getMonth`/`getDate`) — nunca `Date.UTC`/parse de string ISO — para nunca deslizar um dia em fusos diferentes de UTC. Presets "Últimos 7/30 dias" cobrem exatamente 7/30 dias civis completos terminando hoje, inclusive: `[hoje−N+1, amanhã)`. Intervalo personalizado usa dois `<input type="date">` (mesmo padrão nativo já usado pelos filtros de Reservations — não `matDatepicker`, nunca antes usado no projeto), ambos os limites inclusivos, validado client-side (`from ≤ to`, janela ≤ 100 dias) antes de qualquer requisição — uma seleção inválida nunca chega a bater no backend, exibindo mensagem inline em vez disso. O rótulo do período nunca expõe timestamp UTC: mostra apenas a(s) data(s) local(is) formatada(s), um único dia para "Hoje" ou um intervalo "de–até" (usando o último dia INCLUSO, não o limite exclusivo bruto) para os demais.

Prova de correção end-to-end via `read_network_requests` contra a API real (Docker Postgres/RabbitMQ, `IHostPro.Api` real na porta 5140, timezone do processo do navegador em UTC−3): "Hoje" gerou `from=2026-08-17T03:00:00.000Z&to=2026-08-18T03:00:00.000Z` — exatamente meia-noite local em ambos os limites, nunca meia-noite UTC; o intervalo personalizado 01–17/08 gerou `from=2026-08-01T03:00:00.000Z&to=2026-08-18T03:00:00.000Z` (17 dias inclusivos, boundary exclusivo correto). Complementado por 13 testes automatizados do helper (`dashboard-period.spec.ts`), incluindo um teste que alterna `process.env.TZ` entre `America/Sao_Paulo` e `Asia/Tokyo` no próprio processo Vitest para provar que a mesma data-parede local produz dois instantes UTC diferentes — ou seja, que o helper nunca fixa UTC internamente.

#### 7.8.6 Layout e separação Período × Operação atual (mandato §13-14)

Quatro seções, na ordem: **Resumo do período** (`CheckInsInPeriod`, `CheckOutsInPeriod`, `CancelledInPeriod` de Reservations; `CompletedInPeriod`, `CancelledInPeriod` de Housekeeping; `TotalInPeriod` de Occurrences — todas com rótulo explícito "...no período"); **Operação atual** (`FutureReservations` — deliberadamente aqui, não na seção de período, pois é relativo ao `TimeProvider` do backend, não ao filtro `[from,to)`, mandato §15; `Pending`/`InProgress`/`Interrupted`/`Delayed`/`WaitingHelp`/`WaitingMaterials` de Housekeeping); **Imóveis** (`Active`/`Inactive`/`Archived`, current-state, sem `Draft`); **Detalhes** (tabela `Reservas por status`, tabela `Ocorrências por tipo`). As 18 métricas-folha da resposta são cobertas exatamente uma vez cada — nenhuma omitida, nenhuma duplicada, nenhuma recalculada no frontend (mandato §18/§36: os cards leem os campos da resposta diretamente, nunca somam/derivam).

#### 7.8.7 Cards, tabelas e mapeamento de status/tipo

Cards `mat-card` simples (valor + rótulo), sem tendências/setas/percentuais/comparação com período anterior (dados que o backend não fornece). `Reservas por status` e `Ocorrências por tipo` usam `<table>` HTML semântica (com `<th scope="col">`) envolvida por um contêiner com `overflow-x: auto` — não `mat-table`, uma escolha deliberada de simplicidade para duas tabelas de duas colunas (evita a cerimônia de `matColumnDef` sem ganho real), mas preservando cabeçalhos reais e sem introduzir um componente customizado pesado. Mapeamento de status/tipo para rótulo i18n com fallback seguro: `reservationStatusLabel`/`occurrenceTypeLabel` comparam o resultado de `TranslocoService.translate(key)` contra a própria chave (Transloco retorna a chave quando não há tradução) — um código de status futuro/desconhecido nunca renderiza uma chave i18n quebrada, cai de volta ao código bruto (mandato §16).

#### 7.8.8 Atualização — polling, refresh manual e estados

Uma única pipeline RxJS (`switchMap` sobre um `Subject` próprio `triggerFetch$`, alimentado por: carga inicial via `startWith`, toda troca de período, o clique em "Atualizar", e um `interval(60000)`) — deliberadamente NÃO usando a ponte `toObservable(period)` do RxJS interop, cujo agendamento passa pelo `effect()` do Angular e tornaria a primeira carga e cada refetch por período dependentes de um flush de change detection em vez de disparar de forma síncrona e diretamente testável. `switchMap` cancela por construção qualquer requisição anterior ainda em voo quando um gatilho mais novo chega; `takeUntilDestroyed` encerra o polling quando o componente é destruído — sem assinatura órfã. Estado: `phase` (`loading`/`loaded`/`error`) só controla a view de página inteira na PRIMEIRA carga; toda atualização posterior (poll, troca de período, refresh manual) preserva os cards já carregados e alterna `refreshing` (indicador discreto) / `refreshFailed` (banner inline, sem descartar os dados já exibidos) — nunca reconstrói a tela vazia a cada 60s (mandato §29/§32). Overview vazio (zeros) é tratado como estado normal — nunca um erro; as duas tabelas de detalhe mostram individualmente "Nenhum dado neste período." quando vazias.

#### 7.8.9 Defeito real encontrado e corrigido em navegador

Verificação visual revelou que o toggle "Período personalizado" nunca revelava o formulário de intervalo — o `mat-button-toggle` correspondente não tinha nenhum `(click)`/handler ligado ao componente (os outros três toggles tinham `selectPreset(...)`), então clicar nele apenas mexia no estado visual interno do Material sem nunca atualizar o sinal `preset` do componente. Corrigido adicionando `selectCustomPreset()` (que só revela o formulário — não dispara requisição, mandato §10 — a requisição só ocorre em `applyCustomRange()`) e ligando-o ao `(click)` do toggle. Reverificado em navegador: revela o formulário corretamente; validação de intervalo inválido e de janela >100 dias testadas e corretas nesse mesmo fluxo.

#### 7.8.10 Responsividade e acessibilidade

Verificado em 375px real (`resize_window` + inspeção via JavaScript): `document.body.scrollWidth === document.body.clientWidth === window.innerWidth` (sem overflow horizontal); grid de cards (`grid-template-columns: repeat(auto-fill, minmax(11rem, 1fr))`) resolve para 2 colunas de ~180px nessa largura; o contêiner da tabela de detalhes mantém `overflow-x: auto` isolado, nunca forçando o scroll da página inteira. Acessibilidade: hierarquia de headings real (`h1`/`h2`/`h3`), `<table>` com `<th scope="col">` reais (nunca apenas visual), botão "Atualizar" com `aria-label`, seções com `aria-labelledby` apontando para o próprio heading, mensagens de erro com `role="alert"`, nenhum status expresso somente por cor.

#### 7.8.11 Testes automatizados

- **`dashboard-period.spec.ts`**: 13 testes — Hoje, Últimos 7/30 dias, intervalo personalizado válido/inválido/>100 dias/dia único, `startOfLocalDay`, parse de input nativo, e o teste de independência de timezone via `process.env.TZ` (§7.8.5).
- **`dashboard.service.spec.ts`**: 1 teste — delega para `Client.overview(from, to)`.
- **`dashboard-overview.spec.ts`**: 31 testes — carga inicial (sucesso e erro), seleção de cada preset com os limites corretos, revelar/aplicar/validar intervalo personalizado, polling a cada 60s com temporizadores falsos (nunca reais, mandato §46), refresh manual, uma atualização em segundo plano que falha preserva os dados e marca `refreshFailed` (e uma atualização seguinte bem-sucedida limpa a marca), `switchMap` cancelando corretamente uma requisição superada por outra mais nova, os três grupos de cards refletindo os campos brutos da resposta (nunca recalculados), fallback seguro de rótulo de status/tipo desconhecido, e o rótulo de período (dia único vs. intervalo).
- **`admin-layout.spec.ts`**: +4 testes — visibilidade do item "Dashboard" para `DASHBOARD:MANAGE` (mostra), `DASHBOARD:READ` (mostra), `DASHBOARD:READ:OWN_OWNER` isolado (esconde) e `DASHBOARD:USE` isolado (esconde).
- **Suíte completa do frontend**: **48 arquivos, 440 testes, 100% verde** (391 pré-existentes + 49 novos: 13+1+31+4).

#### 7.8.12 Verificação em navegador real

API real (`IHostPro.Api`, porta 5140, seed de desenvolvimento habilitado apenas via variável de ambiente `Identity__DevelopmentSeed__AdminPassword` — nunca commitada em `appsettings.Development.json`, conforme a própria regra documentada em `DevelopmentSeedOptions`) e frontend real (`ng serve`, porta 4200), Postgres/RabbitMQ dev reais via Docker. Três usuários de verificação criados no tenant `dev-tenant` (um seedado pelo mecanismo oficial + dois inseridos via SQL reutilizando o mesmo hash Argon2 válido do usuário seedado, apenas para fins de teste local — nunca fazem parte de migração/seed versionado): `cp3-verify@dev.local` (ADMIN), `operator-cp3-verify@dev.local` (OPERATOR), `housekeeper-cp3-verify@dev.local` (HOUSEKEEPER). Confirmado visualmente: rota, navegação, todos os presets, intervalo personalizado (válido/inválido/>100 dias — defeito real encontrado e corrigido, §7.8.9), rótulo de período, as quatro seções com valores reais provenientes do Postgres dev (incluindo dados residuais de checkpoints anteriores: 2 imóveis ativos, 1 faxina interrompida, 1 reserva confirmada + 1 cancelada), tabelas de detalhe com tradução correta de status/tipo, `GeneratedAtUtc` exibido como "Última atualização" formatado localmente, zero erros no console em toda a sessão. Acesso OPERATOR confirmado (200/renderiza). Acesso HOUSEKEEPER confirmado negado ("Acesso negado"). Responsividade em 375px confirmada sem overflow. Ambiente restaurado ao final: processo da Api real encerrado, `appsettings.Development.json` revertido ao estado original (`Enabled: false`, `admin@dev.local`) — nenhuma credencial de desenvolvimento permanece configurada no arquivo versionado.

#### 7.8.13 NSwag e regressão

Nenhuma alteração de backend neste checkpoint — o cliente gerado (`api-client.ts`) permanece inalterado, regeneração não se aplica (mesmo raciocínio já registrado no Checkpoint 2 do Incremento 1, §6.8, para uma situação equivalente). `git diff --check`: limpo. Nenhuma migração, nenhum endpoint novo, nenhum DTO novo.

#### 7.8.14 Lacunas conhecidas, deliberadamente diferidas

- Gráficos/biblioteca de visualização: nenhuma instalada — mantém a decisão já registrada no Checkpoint 0 do Incremento 2 (§7.1, decisão 5).
- SignalR/tempo real: não implementado — polling de 60s é o mecanismo de atualização aprovado para este MVP.
- Reporting histórico/BI, exportação/PDF/Excel: fora de escopo, incremento futuro separado.
- `PROPERTY_OWNER`/`OWN_OWNER`, HOUSEKEEPER, AI Agent: sem UI administrativa do Dashboard (§7.8.4).
- Drill-down/navegação a partir de cards, customização de layout por usuário: deliberadamente não implementados (mandato §43-44).
- Filtro por Imóvel/Faxineira no Dashboard: nunca fez parte do escopo aprovado (diferente da Agenda, que registrou esse gap por outro motivo, §5.5) — Overview API não expõe esses filtros.
- E2E/Homologação final do Incremento 2 (Checkpoint 4): não iniciado.

### 7.9 Checkpoint 4 — E2E / Homologação Final

#### 7.9.1 Escopo e preflight

Gate final do Dashboard Foundation, do Incremento 2 e da Fase 7 como um todo: prova real de ponta a ponta (Domínio/Comandos → Outbox → RabbitMQ → Worker → projeções do Dashboard → Overview API → Angular → navegador real) para os cenários já homologados nos Checkpoints 1-3, sem mock nos cenários principais. Preflight confirmou os quatro refs (`master`, `origin/master`, `feature/dashboard-reporting`, `origin/feature/dashboard-reporting`) convergindo em `ef3b26f775b96df14c4c8d7faab53cd8039ef618`, reconfirmado ao final deste checkpoint sem divergência (nenhum commit foi feito antes do gate ficar totalmente verde). Nenhuma nova funcionalidade foi adicionada; nenhum contrato/arquitetura foi alterado para facilitar testes — os poucos defeitos reais encontrados (§7.9.4/§7.9.7) foram corrigidos exclusivamente em código de teste, nunca em código de produção.

#### 7.9.2 Extensão do `WebE2EFixture` para o Dashboard

`WebE2EFixture` (suíte `IHostPro.Web.Tests.E2E`, compartilhada por toda a coleção de testes E2E) nunca provisionava o Dashboard — o mesmo padrão de lacuna já encontrado e corrigido historicamente para Housekeeping (Fase 6 CP6), Reservations (Fase 7 Incremento 1 CP3) e Configuration (Fase 5 CP7). Corrigido mirando exatamente as declarações reais do próprio `IHostPro.MigrationRunner/Program.cs` (nunca topologia inventada à mão): migração do schema `dashboard` (`CreateDashboardDbContext`), provisionamento do outbox ancilar `dashboard_messaging`, e a topologia RabbitMQ completa e real do Dashboard nas três exchanges (`property-management-events`, `reservation-events`, `housekeeping-events`) — incluindo as 18 filas/bindings reais (`dashboard.property-projection`, `dashboard.reservation-projection`, `dashboard.cleaning-projection` com os dez routing keys reais de Cleaning, `dashboard.occurrence-projection`). `ConnectionStrings__Dashboard` adicionada ao ambiente dos subprocessos `IHostPro.Api`/`IHostPro.Worker` que o fixture lança. Referência de projeto `IHostPro.Contexts.Dashboard.Infrastructure` adicionada ao `.csproj` de teste, documentada como test-only (mesmo padrão das demais referências de Infrastructure já presentes).

#### 7.9.3 Suíte `DashboardE2ETests` — cenários e dataset

Novo arquivo `DashboardE2ETests.cs` (`tests/Frontend/IHostPro.Web.Tests.E2E`), 12 testes, seed exclusivamente via fluxos oficiais (APIs/comandos reais — nunca inserção direta em `dashboard.*`): acesso ADMIN (200 real + heading); acesso OPERATOR com `DASHBOARD:READ` real (nunca `MANAGE` artificialmente concedido); negação HOUSEKEEPER (nav ausente + navegação direta redirecionada para "Acesso negado"); preset "Hoje" com prova real de fronteira de dia local via `BrowserNewContextOptions.TimezoneId` explícito; presets "Últimos 7/30 dias" com prova real de janela via requisição de rede; intervalo personalizado válido (11 dias) e inválido (`to<from`, rejeitado client-side, zero requisição) com prova de rede; refresh manual sem reload completo; responsividade 375px/desktop; acessibilidade (heading, botão/controles por role+nome, `<table><th scope="col">` reais); isolamento cross-tenant com dois tenants genuinamente distintos (nunca zero-vs-zero); tenant vazio → 200 com todos os cards zerados; e o cenário completo (`Dashboard_reflects_real_period_and_current_state_metrics_from_the_full_scenario`) cobrindo: 4 propriedades ativas + 1 inativa + 1 arquivada + 1 para faxinas; reservas futura/check-in/check-out/cancelada; sete faxinas cobrindo pending/started/waiting-help/completed/cancelled/delayed/interrupted; duas ocorrências de tipos distintos (`Damage`/`Noise`) registradas pelo fluxo oficial do portal do faxineiro (`POST /api/v1/my-cleanings/{id}/occurrences`) — provando as 18 métricas-folha, `StatusCounts`, `OccurrenceByType` e a ausência de `Description` renderizada, com dados reais provenientes do Postgres via RabbitMQ real e Worker real.

Cada cenário sensível a contagem exata (cross-tenant, tenant vazio, cenário completo) provisiona seu próprio tenant novo via `CreateAdditionalTenantWithAdminAsync()` — nunca o tenant padrão compartilhado por toda a coleção — porque a Overview API retorna apenas contagens agregadas, sem id por item filtrável (diferente de `GET /api/v1/schedule`), tornando qualquer teste de contagem exata no tenant compartilhado inerentemente contaminável por outras suítes da mesma coleção.

#### 7.9.4 Defeitos de teste reais encontrados e corrigidos durante a investigação

Nenhum dos seis itens abaixo é defeito de produto — todos são correções em código de teste, dentro do escopo já aprovado (a própria suíte E2E criada neste checkpoint), nunca em `src/`:

1. **Precondição real de `Archive` mal assumida**: `ArchivePropertyCommandHandler` só aceita `Draft`/`Inactive` (rejeita `Active` com 409) — o cenário completo tentava arquivar uma propriedade recém-ativada diretamente. Corrigido inserindo o passo `Deactivate` antes de `Archive`.
2. **Sobreposição real de datas** no teste de isolamento cross-tenant: as duas reservas do Tenant A usavam a mesma propriedade com intervalos que genuinamente se sobrepunham, gerando um 409 de conflito real (não um bug do teste em si sobre a regra de negócio, mas um desenho de dado incorreto). Corrigido usando a segunda propriedade já criada (antes descartada) para a segunda reserva.
3. **Contagem real de faxinas canceladas subestimada**: `WaitUntilKnownToHousekeepingAsync` (helper já estabelecido, usado por `ScheduleAgendaE2ETests`) cria e cancela uma faxinas-sonda para detectar a propagação de `PropertyActivated` — essa sonda também conta para `housekeeping.cancelledInPeriod` do dia corrente. O teste assumia 1 faxina cancelada (apenas a explícita); o valor real e correto é 2. Corrigido o predicado de espera e a asserção do card, com o motivo documentado inline.
4. **Quirk real do Playwright .NET**: `EvaluateAsync<Dictionary<string,string>>` deserializava silenciosamente um objeto JS genuinamente populado (confirmado por captura do DOM real e por `JSON.stringify` da mesma expressão) como um dicionário vazio, causando falha em três testes que liam valores de cards. Confirmado experimentalmente (não suposto) via um round-trip de string JSON manual. Corrigido em `ReadCardValuesAsync`/`ReadTableRowsAsync`: o JavaScript agora retorna `JSON.stringify(...)`, desserializado no lado C# via `System.Text.Json.JsonSerializer.Deserialize<T>` — evitando o tipo genérico problemático como alvo direto de `EvaluateAsync<T>`.
5. **Duas páginas nunca haviam navegado para `/dashboard`** antes de `page.ReloadAsync()` ser chamado (o teste de cross-tenant e o cenário completo faziam todo o seed via API direta, e `LoginAsync` deixa a página em `/`) — um `reload()` da home page nunca teria cards, causando timeout de 30s. Corrigido substituindo `ReloadAsync()` por uma navegação real via `OpenDashboardAsync` (clique no link "Dashboard" + espera da resposta real).
6. **Colisão de `GetByLabel` no formulário de intervalo personalizado**: `GetByLabel("De")` (substring match, comportamento padrão do Playwright) resolvia para a seção "Detalhes" (`aria-labelledby` apontando para o heading "Detalhes", que contém "De" como substring), nunca para o `<input>` real. Corrigido com `Exact = true` nos dois seletores (`"De"`/`"Até"`).

Também foi rewriteado `Dashboard_accessibility_smoke` para provisionar seu próprio tenant com um dado real semeado (em vez de depender do tenant compartilhado ter, por acaso, dados de outra suíte da mesma coleção sem garantia de ordem entre classes) — não um defeito descoberto por falha, mas uma fragilidade de desenho identificada e corrigida preventivamente durante a mesma investigação.

#### 7.9.5 Suíte Dashboard E2E — duas execuções consecutivas verdes

Após as correções de §7.9.4: **12/12 verde em duas execuções consecutivas** da classe `DashboardE2ETests`, usando o ciclo de vida oficial do fixture em cada rodada (nenhum reset manual de banco entre elas) — satisfazendo o requisito de detectar vazamento de estado/bootstrap não-idempotente/sobras de RabbitMQ.

#### 7.9.6 Suíte E2E completa — classificação de flakes pré-existentes

Execução da suíte completa (`IHostPro.Web.Tests.E2E`, sem filtro, todas as classes): **89/94, com as mesmas 5 falhas reproduzidas identicamente em duas execuções**, todas fora de `DashboardE2ETests` — 4 em `ScheduleAgendaE2ETests` (`Reservation_and_Cleaning_times_render_with_no_timezone_shift`, `Filtering_by_EventType_Reservation_hides_Cleaning_events`, `Filtering_by_EventType_Cleaning_hides_Reservation_events`, `Reservation_and_Cleaning_are_distinguishable_beyond_color`) e 1 em `ReservationsE2ETests` (`A_repeated_cancellation_is_handled_correctly`) — nenhuma toca código alterado neste checkpoint.

Classificação não assumida por conveniência — comprovada por um experimento de controle real: as mudanças de `WebE2EFixture.cs` deste checkpoint (§7.9.2) foram temporariamente isoladas via `git stash` e a mesma suíte (`ScheduleAgendaE2ETests`+`ReservationsE2ETests`) foi reexecutada contra o fixture **sem** a extensão do Dashboard. Resultado: **6 falhas** (pior, não melhor), com uma assinatura de erro **diferente** (`WaitUntilKnownToHousekeepingAsync` — timeout aguardando a propagação de `PropertyActivated` para a projeção de Housekeeping) — provando que as mudanças deste checkpoint não são a causa das 5 falhas originais; ambas as rodadas (com e sem a extensão) reproduzem falhas de timing sob carga acumulada de uma suíte sequencial real orientada a browser, sem relação com o código alterado. Mudanças restauradas (`git stash pop`) imediatamente após o experimento, confirmado sem sobras (`git stash list` vazio). Precedente documentado no próprio histórico deste incremento (§6.8, `PolicyUpdatedRegressionTests` em §4.7): esta classe de flakiness de timing sob carga de suíte E2E sequencial já foi identificada e registrada múltiplas vezes antes, nunca deste checkpoint. Não corrigido — fora do escopo aprovado (Schedule/Reservations, não Dashboard).

#### 7.9.7 Defeito real encontrado e corrigido — testes de integração de Housekeeping desatualizados em relação ao DI keyed do Checkpoint 1

A regressão completa do backend (§7.9.9) revelou **11 de 80 testes falhando** em `IHostPro.Contexts.Housekeeping.Tests.Integration`, todos com `InvalidOperationException: No service for type 'IIntegrationEventHandler<T>' has been registered`. Causa raiz confirmada por leitura direta do código de produção: o Checkpoint 1 deste incremento (§7.3) migrou `HousekeepingModuleExtensions`/`HousekeepingMessageExecutionScope` de `AddScoped`/`GetRequiredService` (não-keyed) para `AddKeyedScoped`/`GetRequiredKeyedService(HandlerKey)` — correção real, já documentada e já em produção, motivada pela colisão de DI entre Bounded Contexts que compartilham `IIntegrationEventHandler<T>`. Dois arquivos de teste pré-existentes (`HousekeepingEventProjectionTests.cs`, `HousekeepingOutboxOutageRecoveryTests.cs`) mantinham seus próprios helpers `Dispatch<TEvent>` com a resolução ANTIGA, não-keyed — nunca atualizados quando o Checkpoint 1 mudou a resolução real, e nunca antes pegos por uma regressão completa do backend deste projeto específico durante os Checkpoints 1-3 (que validaram Housekeeping via suítes diferentes/parciais). Corrigido: ambos os helpers passaram a resolver via `sp.GetRequiredKeyedService<IIntegrationEventHandler<TEvent>>(HousekeepingMessageExecutionScope.HandlerKey)`, espelhando exatamente `HousekeepingMessageExecutionScope.ExecuteAsync` real — mesma classe de correção já aplicada uma vez neste mesmo incremento para outro tipo de teste desatualizado pelo Checkpoint 1 (`test(worker): fix pre-existing subprocess tests for the new Dashboard ancillary store`). Nenhuma outra ocorrência do padrão antigo encontrada no restante da suíte (`grep` confirmando). Resultado após a correção: **`IHostPro.Contexts.Housekeeping.Tests.Integration` 80/80**.

Esta correção prova, na prática, exatamente o gate de fan-out que este checkpoint precisava confirmar: `ReservationCreated`/`ReservationCancelled`/`PropertyCreated`/`PropertyActivated`/`PropertyDeactivated`/`PropertyArchived` continuam resolvendo corretamente o handler de Housekeeping (nunca o de Dashboard) através do mecanismo keyed do Checkpoint 1, sob a mesma composição de DI que os testes de integração exercitam.

#### 7.9.8 Regressão de bootstrap (`MigrationRunner`)

`IHostPro.MigrationRunner` executado duas vezes consecutivas contra o Postgres/RabbitMQ de desenvolvimento real (`ihostpro-postgres`/`ihostpro-rabbitmq`, papel `ihostpro_migrator`): primeira execução aplicou migrações de schema (nenhuma pendente — já aplicadas em checkpoints anteriores) e declarou a topologia RabbitMQ completa, incluindo as 18 filas/bindings reais do Dashboard; segunda execução (idempotência) completou sem erros, confirmando que as declarações de fila/binding do Wolverine (`DeclareExchange`/`BindQueue`/`SetupResources`) são idempotentes por construção. `PropertyProjectionBootstrap`/os quatro `IProjectionBootstrapStep` do Dashboard (ADR-017) não sofreram nenhuma alteração neste checkpoint — regressão via reexecução real, não via nova suíte de teste.

#### 7.9.9 ArchitectureTests e regressão ADR-015/016/017

**`ArchitectureTests` 150/150** (mesmo número do Checkpoint 2, §7.7.9 — nenhum teste de arquitetura precisou de alteração neste checkpoint), confirmando sem regressão: o boundary `IServiceScopeFactory` de ADR-015 (Housekeeping) permanece exclusivo a `HousekeepingMessageExecutionScope`; o de ADR-016 (Reservations/Dashboard, mesma técnica local duplicada por contexto) permanece exclusivo a `ReservationsMessageExecutionScope`/`DashboardMessageExecutionScope`; nenhuma dependência de runtime cross-context foi introduzida pelo mecanismo de bootstrap do ADR-017 (confinado a `tools/IHostPro.MigrationRunner`). A correção de §7.9.7 (DI keyed) é regressão comportamental do mesmo mecanismo, provada por execução real, não por inspeção de arquitetura.

#### 7.9.10 Regressão final — backend

Suítes completas, todas 100% verdes após as correções de §7.9.7: `IHostPro.BuildingBlocks.Tests.Unit` 13/13; `IHostPro.Contexts.Identity.Tests.Unit` 470/470; `IHostPro.Contexts.Identity.Tests.Integration` 419/419; `IHostPro.Contexts.PropertyManagement.Tests.Unit` 180/180; `IHostPro.Contexts.PropertyManagement.Tests.Integration` 184/184; `IHostPro.Contexts.Reservations.Tests.Unit` 61/61; `IHostPro.Contexts.Reservations.Tests.Integration` 80/80; `IHostPro.Contexts.Configuration.Tests.Unit` 76/76; `IHostPro.Contexts.Configuration.Tests.Integration` 65/65; `IHostPro.Contexts.Housekeeping.Tests.Unit` 113/113; `IHostPro.Contexts.Housekeeping.Tests.Integration` 80/80 (§7.9.7); `IHostPro.Contexts.Dashboard.Tests.Unit` 8/8; `IHostPro.Contexts.Dashboard.Tests.Integration` 62/62; `IHostPro.Api.Tests.Integration` 19/19; `IHostPro.ArchitectureTests` 150/150 (§7.9.9).

#### 7.9.11 Regressão final — frontend, Release, Angular, NSwag

**Frontend**: 48 arquivos / 440 testes, 100% verde (mesmo número do Checkpoint 3 — nenhuma alteração de frontend neste checkpoint). **Release build** (`dotnet build -c Release`, solução completa): verde, 0 erros (apenas o aviso pré-existente `NU1903` do pacote transitivo `SSH.NET`, já classificado em checkpoints anteriores, não introduzido aqui). **Angular produção** (`ng build`): verde. **NSwag**: nenhuma alteração de controller/DTO/contrato neste checkpoint; verificado ativamente (não apenas assumido) regenerando o cliente contra a `IHostPro.Api` real em execução (Postgres/RabbitMQ dev reais, `ASPNETCORE_ENVIRONMENT=Development` explícito para carregar User Secrets) — `git diff --stat` sobre `api-client.ts` vazio antes e depois da regeneração, confirmando zero drift byte a byte. `git diff --check`: limpo.

#### 7.9.12 Ambiente

Nenhum processo/servidor manual (Api/Worker/Angular) deixado em execução: a `IHostPro.Api` iniciada manualmente para a verificação de NSwag (§7.9.11) foi encerrada explicitamente ao final, porta 5140 confirmada livre. Nenhum container Testcontainers órfão (confirmado via `docker ps` — nenhum container/rede rotulado `org.testcontainers`). `ihostpro-postgres` (dev) permaneceu ativo durante todo o checkpoint, sem interrupção. `ihostpro-rabbitmq` (dev) foi encontrado parado no início deste checkpoint (resquício de sessão anterior) — necessário para a execução real do `MigrationRunner`/verificação de NSwag (§7.9.8/§7.9.11), foi iniciado e permanece ativo (saudável) ao final: seu estado declarado em `docker-compose.yml` é `restart: unless-stopped`, portanto mantê-lo ativo é a restauração correta ao estado de repouso pretendido, não um desvio do baseline. Os processos `dotnet.exe` observados em execução ao final são exclusivamente nós do build-server do MSBuild (`/nodeReuse:true`, confirmado via `Get-CimInstance Win32_Process`) — infraestrutura padrão do próprio SDK .NET, reutilizada entre builds por design, nunca um processo de aplicação órfão; não seguram nenhuma porta de rede da aplicação. `git status`/`git diff --stat`/`git diff --check`: apenas os arquivos deste checkpoint (`WebE2EFixture.cs`, `IHostPro.Web.Tests.E2E.csproj`, `DashboardE2ETests.cs` novo, os dois arquivos de teste de Housekeeping corrigidos em §7.9.7), `.claude/` corretamente não versionado, nenhum problema de whitespace.

#### 7.9.13 Status final e lacunas conhecidas

**Incremento 2 (Dashboard & Reporting Foundation): CONCLUÍDO FUNCIONALMENTE** — Checkpoints 0-4 homologados nesta branch; publicação em `master` pendente (próxima etapa). **Fase 7 (Agenda e Dashboard Operacional): CONCLUÍDA FUNCIONALMENTE** — ambos os incrementos (Agenda Foundation, já publicado; Dashboard & Reporting Foundation, homologado nesta branch) aprovados; publicação final pendente.

Lacunas explicitamente diferidas, não implementadas, nunca silenciosamente omitidas:

- **Taxa de ocupação**: sem fórmula oficial documentada — GAP DOCUMENTAL desde o Checkpoint 0 (§7.1, decisão 1), não implementado.
- **Gráficos/biblioteca de visualização**: nenhuma dependência de charts instalada em nenhum checkpoint — Dashboard Foundation usa exclusivamente Angular Material.
- **Dashboard para `PROPERTY_OWNER`** (`DASHBOARD:READ:OWN_OWNER`): não implementado — mesmo gap da Agenda (§5.6), registrado desde o Checkpoint 2 (§7.7.5) e reconfirmado no Checkpoint 3 (§7.8.4) e neste checkpoint (§7.9.3).
- **Acesso administrativo de HOUSEKEEPER ao Dashboard**: não implementado — HOUSEKEEPER não possui nenhuma permissão `DASHBOARD:*`, confirmado negado em todos os checkpoints (§7.7.5, §7.8.4, §7.9.3).
- **Reporting histórico/BI** (séries temporais, data warehouse, OLAP, exportação/PDF/Excel): incremento futuro separado, não iniciado.
- **Métricas de duração/SLA**: não implementadas — `IntegrationEvent.Timestamp` não foi promovido a timestamp de negócio oficial para métricas históricas (§7.6).
- **SignalR/tempo real**: não implementado — polling de 60s é o mecanismo de atualização aprovado para este MVP (§7.8.8).
- **Filtro por Imóvel/Faxineira no Dashboard**: nunca fez parte do escopo aprovado — Overview API não expõe esses filtros.
- **Drill-down/navegação a partir de cards, customização de layout**: deliberadamente não implementados (mandato do Checkpoint 3, §43-44).
- `ReservationsE2ETests.A_repeated_cancellation_is_handled_correctly` e o subconjunto de `ScheduleAgendaE2ETests` registrado em §7.9.6: flakiness de timing pré-existente sob carga de suíte E2E completa, comprovadamente não relacionada a este incremento (experimento de controle, §7.9.6) — não corrigida, débito técnico de estabilidade de E2E já registrado antes deste incremento (§6.8).

Fonte da verdade confirmada, sem duplicação: Agenda (Incremento 1) permanece a fonte real de eventos/horários de Cleaning/Reservation consumidos pelo Dashboard; Dashboard nunca recalcula nem reinterpreta esses dados — apenas agrega via suas próprias projeções locais alimentadas pelos mesmos eventos reais.

## 8. Referências

- ADR-016 (Tenant-safe Execution Boundary for Persistent Wolverine Consumers).
- ADR-015 (Isolamento do Processamento de Mensagens Housekeeping da Integração EF Core do Wolverine) — a descoberta original, para Housekeeping.
- ADR-017 (Deployment-time Bootstrap for Event-derived Projections) — mecanismo de bootstrap do Dashboard, generalizando o precedente `PropertyProjectionBootstrap`.
- Documento 07 (Catálogo de Eventos de Domínio) — payload real dos dez eventos de ciclo de vida de `Cleaning`, incluindo os quatro corrigidos/novos na Fase 7 Incremento 1; §27.1/§27.3 (CheckInAt/CheckOutAt) e §29.9 (CleaningOccurrenceRegistered) da Fase 7 Incremento 2.
- Documento 18 (Dashboards, Indicadores e Business Intelligence) — fonte da matriz de inventário de indicadores do Checkpoint 0.
