# Fase 7 — Agenda e Dashboard Operacional — Validação e Homologação

Versão: 1.1 (Incremento 1 — Agenda Foundation, Checkpoint 2 — Frontend Agenda — documento vivo; Checkpoint 1/Checkpoint 1 CLOSURE registrados em §2-§4)

Status: **Incremento 1 (Agenda Foundation) em andamento** — Checkpoint 0, Checkpoint 1, Checkpoint 1 CLOSURE (ADR-016) e Checkpoint 2 (Frontend Agenda) concluídos e homologados. Checkpoint 3 (Integration/E2E formal) ainda não iniciado. Dashboard Operacional permanece fora de escopo — nenhum trabalho iniciado (nenhum projeto, pasta, scaffold, migração, API, frontend, métrica, card ou relatório criado).

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

## 6. Referências

- ADR-016 (Tenant-safe Execution Boundary for Persistent Wolverine Consumers).
- ADR-015 (Isolamento do Processamento de Mensagens Housekeeping da Integração EF Core do Wolverine) — a descoberta original, para Housekeeping.
- Documento 07 (Catálogo de Eventos de Domínio) — payload real dos dez eventos de ciclo de vida de `Cleaning`, incluindo os quatro corrigidos/novos nesta fase.
