# Fase 7 — Agenda e Dashboard Operacional — Validação e Homologação

Versão: 1.3 (Incremento 1 — Agenda Foundation — CONCLUÍDO E PUBLICADO em master; Checkpoints 0-3 registrados em §2-§6)

Status: **Incremento 1 (Agenda Foundation) CONCLUÍDO E PUBLICADO** — Checkpoint 0, Checkpoint 1, Checkpoint 1 CLOSURE (ADR-016), Checkpoint 2 (Frontend Agenda) e Checkpoint 3 (Integration/E2E) concluídos, homologados e publicados em `master` (fast-forward, commit `b53b2cb`). **Incremento 2 (Dashboard Operacional) — NÃO INICIADO** — nenhum trabalho iniciado (nenhum projeto, pasta, scaffold, migração, API, frontend, métrica, card ou relatório criado).

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

## 7. Referências

- ADR-016 (Tenant-safe Execution Boundary for Persistent Wolverine Consumers).
- ADR-015 (Isolamento do Processamento de Mensagens Housekeeping da Integração EF Core do Wolverine) — a descoberta original, para Housekeeping.
- Documento 07 (Catálogo de Eventos de Domínio) — payload real dos dez eventos de ciclo de vida de `Cleaning`, incluindo os quatro corrigidos/novos nesta fase.
