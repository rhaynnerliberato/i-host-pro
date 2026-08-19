# Fase 9 — Comunicação e Integrações do MVP — Validação e Homologação

Versão: 1.2 (Checkpoint 0 — Auditoria Read-Only — registrado em §2; Checkpoint 1 — Communication Foundation + Templates + Fake WhatsApp Connector — registrado em §3, incluindo a correção definitiva do gate de segurança do conector/topologia em Production — §3.15/§3.16/§3.18)

Status: **Checkpoint 1 — DEFINITIVAMENTE HOMOLOGADO E PUBLICADO** (correção definitiva do gate de Production publicada em `master`/`origin/master`/`feature/communication-integrations`/`origin/feature/communication-integrations`, todos convergindo em `8c2c38e`; sem rollback/rebase/force-push/squash/merge commit). O gate diagnóstico final sobre `PolicyUpdatedRegressionTests` (§3.21) foi resolvido por controle A/B rigoroso comparando baseline `ae78af5` vs. current `8c2c38e` — 4/10 falhas em ambos os lados, mesma assinatura, nenhum arquivo de produção relacionado ao PolicyUpdated tocado pela correção — confirmando dívida técnica preexistente, sem regressão introduzida. **Fase 9 — Comunicação e Integrações do MVP — EM ANDAMENTO.** Checkpoint 2 (WhatsApp real), Checkpoint 3 (Airbnb) e Checkpoint 4 (homologação final da Fase) permanecem pendentes — nenhum dos três foi iniciado. Ver §6 para o escopo exato ainda em aberto.

---

## 1. Objetivo

Este documento registra a implementação e homologação real da Fase 9 (Comunicação e Integrações do MVP, per `Plano Executivo de Desenvolvimento por Fases.md`), começando pelo Checkpoint 0 (auditoria read-only) e o Checkpoint 1 (Communication Foundation): o primeiro fluxo de comunicação automática ao hóspede — uma mensagem WhatsApp de boas-vindas disparada por `ReservationCreated`, através de um Bounded Context novo (Communication), um modelo de Template dentro de Configuration & Policy, e um conector WhatsApp deliberadamente falso (dev/test-only), pois nenhuma credencial ou integração real com um provedor WhatsApp foi contratada ou implementada nesta fase.

## 2. Checkpoint 0 — Auditoria e Refinamento Read-Only

Auditoria documentária e de código completa (sem nenhuma alteração de código), cobrindo: Documento 19 (Integrações Externas) na íntegra; as seções de Communication/Integração dos Documentos 05, 06, 07, 09, 10, 12 e 14; os ADRs aplicáveis (002, 014, 015, 016, 017, 018) e as homologações de fases anteriores; e uma auditoria real de código confirmando zero implementação prévia de Communication, Notifications, Airbnb, PIX ou qualquer `IntegrationCredential`. A síntese produziu uma matriz REQ/DEC/IMPLEMENTADO/GAP/FUTURO e a recomendação de MVP que fundamentou o Checkpoint 1: um fluxo único (WhatsApp de boas-vindas em `ReservationCreated`), um modelo mínimo de Template dentro de Configuration (nunca um BC Templates próprio), e um conector falso — decisões confirmadas pelo usuário antes do início da implementação.

## 3. Checkpoint 1 — Communication Foundation + Templates + Fake WhatsApp Connector

### 3.1 Escopo

**Incluído**: ADR-019 (leitura síncrona purpose-limited de contato do hóspede); o Bounded Context **Communication** (`Communication.Domain` + `Communication.Application` + `Communication.Infrastructure` — sem `Contracts` próprio, sem `Api` próprio, ver §3.4); a entidade `Template` dentro de **Configuration & Policy** (nunca um BC Templates independente, ver §3.3); o processador de aplicação que resolve o template ativo, lê o contato do hóspede via ADR-019, monta a mensagem, aplica idempotência e despacha via conector; o `FakeWhatsAppConnector` (produção, sempre bem-sucedido, explicitamente documentado como dev/test-only); a topologia RabbitMQ dedicada de Communication; o quarto consumidor de `ReservationCreated` (Housekeeping/Dashboard/Workflow já existentes — Communication é o quarto, ver §3.10 para a distinção de proteções necessárias para isso funcionar corretamente).

**Excluído** (fora deste checkpoint): qualquer credencial ou integração real com um provedor WhatsApp (Checkpoint 2); Airbnb (Checkpoint 3); PIX; qualquer BC de Notifications; API/UI administrativa própria de Communication (Templates tem sua própria API dentro de Configuration, ver §3.3); versionamento/rollback de Template; qualquer canal além de WhatsApp.

### 3.2 ADR-019 — Leitura síncrona purpose-limited de contato do hóspede

Ver `documentacao do projeto/ADRs/ADR-019 - Purpose-limited Reservation Guest Contact Read for Communication.md` para a decisão completa — não duplicada aqui. Resumo dos pontos obrigatórios, revalidados nesta rodada de fechamento contra o código real: quarta exceção síncrona cross-context nomeada (após as duas de ADR-002 e a de ADR-014); contrato `IReservationGuestContactReader`/`ReservationGuestContact` em `Reservations.Contracts`, implementação única em `Reservations.Infrastructure` (`ReservationGuestContactReader`); resposta mínima confirmada no código real — exatamente `ReservationId` e `GuestPhone` (`string?`), nenhum outro campo, `GuestName` deliberadamente ausente; leitura tenant-scoped via `TenantAwareTransactionScope.BeginAsync(readOnly: true)`, mesmo padrão de `PropertyReservationEligibilityReader`; retorna `null` para Reservation inexistente ou de outro tenant, indistinguíveis por desenho; toda leitura emite um registro estruturado PII-safe (`TenantId`, `ReservationId`, `Purpose = "communication_delivery"`, `Caller = "Communication"`, resultado `Found`/`NotFound` — nunca o valor do telefone) via `ILogger<T>`, sem persistência nova. Restrição de referência confirmada por `ArchitectureTests` dedicado: `IReservationGuestContactReader` é referenciado exclusivamente por Communication.

### 3.3 Templates — ownership, modelo real e defeito de autorização encontrado e corrigido

**Ownership confirmado**: `Template` vive dentro de **Configuration & Policy** (`IHostPro.Contexts.Configuration.Domain/Template.cs`), nunca um Bounded Context próprio — decisão do Checkpoint 0, reconfirmada nesta rodada por inspeção direta do código: `Configuration.Api` expõe `TemplatesController`; `Configuration.Application` contém o caso de uso em `Templates/`; `Configuration.Infrastructure` contém `TemplateRepository` e a configuração EF (`TemplateConfiguration.cs`, migração `20260818195807_AddTemplates`); `Configuration.Contracts` expõe `ITemplateReader`/`ActiveTemplate` para consumo cross-context (Communication é o único consumidor, mesmo padrão de restrição de referência do ADR-019).

**Escopo real do modelo — sem overclaiming**: o `Template` implementado nesta fase é deliberadamente mínimo — uma chave (`TemplateKey`), um conteúdo textual com placeholders simples, um canal (`WhatsApp`), e um estado ativo/inativo por tenant. **Não existe** versionamento, histórico de revisões, rollback, aprovação/workflow de publicação, nem múltiplos idiomas — qualquer relatório anterior que tenha sugerido essas capacidades estaria overclaiming; este documento registra o escopo real, não o aspiracional.

**Defeito real encontrado e corrigido — permissões `TEMPLATES:MANAGE`/`TEMPLATES:READ` seedadas mas nunca registradas como política**: `IdentityPermissionCodes.TemplatesManage`/`TemplatesRead` já existiam no catálogo persistido desde a migração original de `IdentityCatalogSeed` (Fase 1), mas `IdentityAuthorizationExtensions.cs` nunca registrava `.AddPolicy(...)` para elas — como nenhum controller anterior os referenciava, o gap ficou invisível até `TemplatesController` se tornar o primeiro consumidor real nesta fase. Sem a correção, toda requisição a `TemplatesController` teria falhado a autorização independentemente da permissão real do chamador. Corrigido adicionando as duas policies em `IdentityAuthorizationExtensions.cs`, mesmo padrão já usado por `DASHBOARD:MANAGE`/`DASHBOARD:READ`. Cobertura de regressão: testes HTTP dedicados provando 401 (sem token), 403 (token válido sem a permissão) e sucesso (token com a permissão correta), mais isolamento cross-tenant na leitura/escrita de Templates.

**Renderização**: testada via `TemplateReaderTests`/testes unitários do domínio — substituição de placeholder simples, nunca um motor de template genérico (Handlebars/Razor/etc.).

### 3.4 Estrutura real do Bounded Context Communication

Confirmada por inspeção direta do código, três projetos apenas — sem `Communication.Contracts` (nada fora do BC referencia tipos de Communication) e sem `Communication.Api` (nenhuma superfície HTTP própria nesta fase, fluxo inteiramente orientado a evento):

- **`Communication.Domain`**: o aggregate `Message`.
- **`Communication.Application`**: `ReservationCreatedCommunicationProcessor` — o caso de uso completo (resolve template ativo, lê contato via ADR-019, monta idempotência, monta variáveis do template, persiste e despacha).
- **`Communication.Infrastructure`**: `Messaging/` (o adapter Wolverine fino + `CommunicationMessageExecutionScope`, ver §3.9, + `FakeWhatsAppConnector`) e `Persistence/` (EF Core para `Message`).

### 3.5 Modelo `Message` — classificação PII por campo

| Campo | Classificação | Observação |
|---|---|---|
| `Id`, `TenantId`, `ReservationId`, `TemplateKey`, `Channel`, `Status`, `CreatedAtUtc`, timestamps de transição | Não-PII | Identificadores e metadados operacionais |
| `IdempotencyKey` | Não-PII | Formato `{tenantId:D}:{reservationId:D}:{templateKey}:{channel}` — nenhum dado de contato |
| `DestinationMasked` | Não-PII (mascarado por desenho) | Armazena exclusivamente os últimos 4 dígitos do telefone — nunca o número completo |
| `RenderedContent` | Não-PII neste Checkpoint, por construção do único template existente | O único template ativo nesta fase usa exclusivamente a variável `CheckInDate` (uma data) — nenhum nome, telefone ou endereço de hóspede é interpolado. Uma futura mudança de template que introduza uma variável pessoal exigiria reavaliar este campo — registrado aqui como um limite explícito, não uma garantia estrutural permanente |
| `FailureReason` (quando aplicável) | Não-PII | Motivo técnico/de negócio da falha, nunca eco do conteúdo |

**Confirmação de ausência de PII em `ReservationCreated`**: o evento que dispara este fluxo continua, sem alteração, sem `GuestPhone`/`GuestName` — o mesmo precedente testado desde a Fase 3 e reafirmado em toda fase subsequente (mais recentemente Fase 8, §5.13.3). Communication obtém o contato exclusivamente via a leitura síncrona do ADR-019, nunca do evento.

### 3.6 Máquina de estados de `Message` e boundary transacional

Estados reais implementados: `Created → Queued → Sending → Sent`; falha possível em `Queued → Failed` (sem contato disponível — nenhuma tentativa de envio) ou em `Sending → Failed` (conector rejeitou/lançou). Transições inválidas são rejeitadas pelo próprio aggregate (`EnsureStatus`).

**Boundary transacional em duas fases**, já correto por desenho — nenhuma refatoração foi necessária (o mandato desta rodada exigia um STOP explícito caso fosse necessária alteração material; não foi o caso):
1. Persistir `Message` em `Queued` numa transação curta e própria.
2. Chamar o conector **sem nenhuma transação aberta**.
3. Persistir o estado terminal (`Sent`/`Failed`) numa segunda transação curta e separada.

Este desenho evita manter uma transação de banco aberta durante uma chamada de I/O externo (mesmo com o conector falso atual) — já preparado para um conector HTTP real futuro sem necessidade de retrabalho estrutural.

### 3.7 Conector WhatsApp — dev/test-only, inequívoco

Duas implementações distintas e deliberadas, confirmadas no código:

- **`FakeWhatsAppConnector`** (`Communication.Infrastructure`, produção): sempre bem-sucedido, nenhuma chamada de rede real, comentário de documentação explícito marcando-o como dev/test-only — não deve ser confundido com um conector real. Log estruturado, PII-safe (apenas a chave de idempotência).
- **`FakeOutboundMessageConnector`** (arquivos `Fakes.cs`, apenas em projetos de teste): fábricas `Succeeding()`/`Rejecting(reason)`/`Throwing(exception)`, mais `ReceivedDispatches` para asserção — usado para provar os caminhos de sucesso/falha do processador sem depender do `FakeWhatsAppConnector` de produção.

Nenhuma credencial, chave de API, token de acesso, segredo de webhook ou classe `IntegrationCredential` existe em qualquer lugar do código — confirmado por busca textual no Checkpoint anterior a este fechamento (ver §5).

### 3.8 Auditoria de PII — completa

Toda leitura de contato via ADR-019 é logada PII-safe (§3.2). Todo despacho de mensagem é logado PII-safe (chave de idempotência, nunca o telefone). Os logs reais do Worker, capturados durante os gates de transporte real (§3.9), foram inspecionados e confirmam zero ocorrência de número de telefone/nome de hóspede em qualquer linha — apenas identificadores e a chave de idempotência.

### 3.9 Provas reais de transporte (RabbitMQ + Worker + Postgres reais)

- **Sucesso**: `ReservationCreated` publicado → Communication consome → template resolvido → contato lido via ADR-019 → `FakeWhatsAppConnector` chamado exatamente uma vez → `Message` persistida em `Sent`.
- **Falha (sem contato)**: Reservation sem `GuestPhone` → `Message` persistida diretamente em `Failed`, conector nunca chamado.
- **Redelivery**: a mesma mensagem `ReservationCreated` (mesmo `MessageId` do Wolverine) reprocessada não cria uma segunda `Message` — idempotência garantida pela chave `IdempotencyKey` (índice único).
- **Cross-tenant**: uma Reservation do Tenant A nunca produz uma `Message` visível/lida sob o Tenant B — confirmado por leitura RLS-scoped sob o outro tenant.

### 3.10 ADR-016 (execution-scope boundary) e keyed DI — duas proteções distintas, ambas necessárias

`CommunicationMessageExecutionScope` segue exatamente o padrão ADR-016 já generalizado em Housekeeping/Reservations/Dashboard: uma única classe autorizada detém `IServiceScopeFactory`, abre um escopo DI filho por mensagem, resolve `ITenantContext` desse escopo e chama `SetTenant(tenantId)` **antes** de resolver o processador de negócio. Registro keyed DI (`HandlerKey = "communication"`) resolve a ambiguidade de múltiplas implementações da mesma interface não-keyed **dentro** de um handler.

Estas são **duas proteções diferentes, ambas necessárias simultaneamente**, nunca confundidas: keyed DI resolve resolução não-determinística de DI dentro de um handler; o sticky-handler do ADR-020 (§3.11) resolve a composição da cadeia de handlers do próprio Wolverine para o mesmo tipo de mensagem entre múltiplos Bounded Contexts. Um problema não substitui o outro.

### 3.11 Correção transversal (ADR-020) — registrado aqui apenas como referência, NÃO uma funcionalidade ou defeito de Communication

**Communication nunca teve um defeito próprio de handler-chain.** Durante a implementação deste Checkpoint 1, adicionar o quarto consumidor de `ReservationCreated` (Communication, após Housekeeping/Dashboard/Workflow) expôs um defeito **pré-existente e transversal** no Wolverine (comportamento padrão `MultipleHandlerBehavior.Combined`, que combina handlers de múltiplos Bounded Contexts para o mesmo tipo de mensagem numa única cadeia de execução, independentemente do número de filas/listeners) — um defeito que já existia desde a Fase 6/7/8 (Housekeeping/Dashboard/Workflow já compartilhavam esse risco estrutural entre si, antes mesmo de Communication existir). A correção (`AddStickyHandler`, aplicada por fila em `IHostPro.Worker/Program.cs`) foi investigada, provada e publicada como uma correção arquitetural cross-phase independente, em branch própria (`fix/wolverine-fanout-handler-isolation`), fast-forwarded para `master` antes deste Checkpoint 1 ser retomado. Ver **ADR-020** (Isolamento de Handler Chains do Wolverine para Fan-out entre Bounded Contexts) para a investigação completa, a prova estrutural e de transporte real, e o escopo exato — não duplicado aqui.

O gate de 4 consumidores (Housekeeping/Dashboard/Workflow/Communication, exatamente 4 execuções lógicas por `ReservationCreated`, nunca 0/nunca N×M) foi reexecutado após o resync de Communication sobre a correção publicada e está registrado como evidência oficial deste Checkpoint 1 — sem repetição do detalhamento já registrado em ADR-020.

### 3.12 Defeitos reais encontrados e corrigidos durante o fechamento deste Checkpoint

1. **Autorização de Templates** (§3.3) — políticas `TEMPLATES:MANAGE`/`TEMPLATES:READ` seedadas mas nunca registradas; corrigido em `IdentityAuthorizationExtensions.cs`.
2. **`ConnectionStrings__Communication` ausente em `WolverineThreeStoreCompositionTests.cs`** — a fixture de composição do host real não repassava a connection string de Communication ao subprocesso `IHostPro.MigrationRunner`, encontrado durante a revalidação deste Checkpoint (antes do gate final da suíte completa). Corrigido adicionando a linha, mesmo padrão já usado para os demais contextos.
3. **`ConnectionStrings__Communication` ausente em `WolverineHandlerChainIsolationBaselineTests.cs`** — mesma classe de defeito, num segundo arquivo, criado durante a investigação do ADR-020 (branch corretiva) **antes** de Communication ter sido reincorporada — na época da criação daquele teste, Communication ainda não fazia parte do conjunto de contextos migrados pelo `IHostPro.MigrationRunner` usado pelo teste. Após o resync, o `MigrationRunner` passou a tentar migrar `CommunicationDbContext` também nesse teste, e a ausência da connection string fazia o subprocesso cair silenciosamente no valor padrão do próprio `appsettings.json` do MigrationRunner — que aponta para o Postgres real de desenvolvimento (`localhost:5432`), não para o Postgres efêmero (Testcontainers) do teste — resultando em `Npgsql.PostgresException: 42501: permission denied for table __EFMigrationsHistory` contra o banco real. Como essa exceção interrompia `InitializeAsync` **depois** do próprio container RabbitMQ efêmero do teste já ter sido iniciado, o container ficava associado à porta fixa 5672 sem ser descartado — bloqueando o RabbitMQ efêmero de toda classe de teste subsequente pelo resto de qualquer execução completa da suíte (`IHostPro.Api.Tests.Integration`). Esta foi a causa raiz confirmada das 14 e depois 21 falhas observadas nas duas primeiras tentativas de executar a suíte completa como gate final deste Checkpoint — isolada por um experimento controlado (as 17 classes que usam RabbitMQ efêmero executadas uma a uma, em processos separados: 16/18 passaram limpas isoladamente; apenas esta classe falhou por mérito próprio, e sua vizinha `WolverineThreeStoreCompositionTests` falhou apenas como consequência herdada da porta ainda ocupada). Corrigido adicionando a linha ausente; suíte completa reexecutada do zero após a correção: **26/26, 0 falhas** (ver §5). Afeta exclusivamente infraestrutura de teste — nenhum código de produção alterado por esta correção, e não é um defeito de negócio de Communication.

### 3.13 Dívida técnica pré-existente, explicitamente NÃO relacionada a este Checkpoint

Mantida separada, nunca misturada com ADR-020 ou com os defeitos de §3.12:

- **`PropertyProjectionSynchronizer`** (Housekeeping) — corrida read-then-write pré-existente, `task_6b2837d1` (Fase 8, §3.9). Não corrigida.
- **`ReservationProjectionSynchronizer`** (Dashboard) — mesma classe de corrida, `task_ba854be2` (Fase 8, §3.11). Não corrigida.
- **Gap de retry-safety em `LogoutExecutor`/`RevokeOwnSessionExecutor`** (Identity) — gap de limpeza pré-CP6, deliberadamente deferido em fase anterior. Não corrigida.

Nenhuma das três é bloqueante para o fechamento deste Checkpoint — todas são pré-existentes, fora do escopo de Communication, e permanecem registradas para correção futura.

### 3.14 Escopo confirmado como zero (sem implementação, sem placeholder)

Confirmado por busca textual no código real desta rodada de fechamento: zero classe/tabela `IntegrationCredential`; zero armazenamento de API key/access token/client secret/webhook secret em qualquer lugar do BC Communication; zero referência a Airbnb; zero referência a PIX; zero Bounded Context ou namespace "Notifications". Nada disso foi implementado, nem como placeholder.

### 3.15 Gate de segurança — cronologia completa (primeira tentativa insuficiente, corrigida a seguir)

Registrado cronologicamente, sem apagar o ocorrido — a primeira tentativa foi real, foi publicada em `ae78af5`, e foi corrigida FORWARD, nunca por rollback/rebase/force-push:

1. **Estado original**: `AddCommunicationReservationConsumer()` — que registra `services.AddScoped<IOutboundMessageConnector, FakeWhatsAppConnector>()`, a única implementação existente — era chamada incondicionalmente em `IHostPro.Worker/Program.cs`, sem nenhum `IsDevelopment()`/`IsProduction()`. Development, Test/E2E e Production resolviam exatamente a mesma implementação — um conector que sempre reporta sucesso sem nunca chamar um provedor real.
2. **Auditoria final do CP1** detectou esse risco: em Production, uma `ReservationCreated` real levaria uma `Message` a `Sent` sem nenhuma entrega de fato ocorrer — um falso positivo operacional silencioso.
3. **Primeira correção**: gate por ambiente usando `if (!builder.Environment.IsProduction())`, envolvendo tanto o registro DI (`AddCommunicationModule`/`AddCommunicationReservationConsumer`) quanto o listener Wolverine (`opts.ListenToRabbitQueue("communication.reservation-created-trigger")`) — publicada em `ae78af5`.
4. **Essa escolha foi prematura**: `!IsProduction()` é uma DENYLIST — deixaria o conector falso ativo em Staging/QA/UAT/qualquer nome de ambiente customizado, exatamente o mesmo risco de falso-positivo operacional que o gate existia para fechar, apenas realocado para outro ambiente não-Development.
5. **Homologação corretiva** (revisão pós-publicação) definiu a regra definitiva: uma ALLOWLIST — `builder.Environment.IsDevelopment()` — o fake automation só pode estar ativo quando o ambiente é literalmente `Development`, nunca por exclusão de `Production`. Aplicado às mesmas duas chamadas (registro DI + listener Wolverine), no mesmo commit lógico da correção.
6. **A topologia também foi corrigida** (nunca fazia parte da primeira tentativa — descoberta apenas na homologação corretiva, ver §3.18): sem esse segundo gate, uma `ReservationCreated` real em qualquer ambiente não-Development continuaria sendo roteada para uma fila sem consumidor, acumulando backlog.
7. **Checkpoint 2** substituirá este mecanismo por um provedor/configuração reais — este gate Development-only é estritamente uma medida de segurança do CP1, nunca a semântica final de produção.

Fora de Development, o módulo inteiro fica ausente — nenhuma `Message` é criada/enfileirada, nenhuma fila é escutada — até o Checkpoint 2 entregar um conector real.

### 3.16 Defeito real, pré-existente, descoberto ao provar o gate de §3.15: `ASPNETCORE_ENVIRONMENT` nunca funcionou para `IHostPro.Worker`

Provar o gate acima expôs um segundo defeito real, mais profundo e completamente independente do connector: `IHostPro.Worker/Program.cs` usa `Host.CreateApplicationBuilder(args)` — o Generic Host puro (`Microsoft.Extensions.Hosting`), não o Web Host. O Generic Host reconhece exclusivamente `DOTNET_ENVIRONMENT`; a variável `ASPNETCORE_ENVIRONMENT` só é reconhecida pela camada de hospedagem web do ASP.NET Core (`WebApplication.CreateBuilder`), que este processo nunca usa. As 18 fixtures de teste que lançam o subprocesso real `IHostPro.Worker.dll` (toda a suíte `WorkerRoundTrip`/`WolverineThreeStoreCompositionTests`/`WolverineHandlerChainIsolationBaselineTests`, mais `WebE2EFixture.cs` do frontend) sempre definiram `ASPNETCORE_ENVIRONMENT=Development` — uma intenção nunca honrada: `builder.Environment.EnvironmentName` sempre resolveu para `Production` (o padrão do Generic Host quando `DOTNET_ENVIRONMENT` não está definido), em toda execução de teste desde que este processo existe. Isso nunca teve efeito observável até agora porque nenhum outro código deste processo ramifica por ambiente, exceto o gate de Identity já existente (`AddIdentityModule(..., builder.Environment.IsDevelopment())`, linha 69) — cujo próprio seeder (`DevelopmentIdentitySeeder`) sempre no-opa por padrão (`DevelopmentSeedOptions.Enabled = false`), tornando o defeito completamente invisível até o gate de §3.15 introduzir o primeiro comportamento condicional que os testes de fato precisavam disparar corretamente.

Confirmado empiricamente: com o gate de §3.15 aplicado e sem correção desta variável, os dois testes de transporte real de Communication falhavam com `communicationReady` nunca `True` (o Worker nunca reportava escutar a fila de Communication). Corrigido adicionando `DOTNET_ENVIRONMENT=Development` ao lado de `ASPNETCORE_ENVIRONMENT=Development` (mantida, sem custo, para o caso deste processo migrar para um Web Host no futuro) nas 18 fixtures — nunca substituindo, apenas complementando. Escopo confirmado como exclusivamente de teste: `IHostPro.Api`'s próprio `WebApplicationFactory`-based test host usa `WebApplication.CreateBuilder` de fato (Web Host real), onde `ASPNETCORE_ENVIRONMENT` já funcionava corretamente — nenhuma alteração feita ali. Esta correção permanece válida e não foi revertida pela homologação corretiva de §3.15/§3.18 — `IHostPro.MigrationRunner` usa o mesmo Generic Host e o mesmo mecanismo nativo `IHostEnvironment`, sem exigir nenhuma abstração nova.

### 3.17 Blocker adicional encontrado na homologação corretiva: backlog de topologia em ambientes não-Development

A homologação corretiva (revisão pós-publicação de `ae78af5`) levantou uma pergunta que a primeira tentativa nunca respondeu: mesmo com o listener do Worker desligado fora de Development, `IHostPro.MigrationRunner` ainda provisionava a fila/binding `communication.reservation-created-trigger` → `reservation_created` na exchange compartilhada `reservation-events`, incondicionalmente, em qualquer ambiente. Confirmado por leitura direta do `Program.cs` do MigrationRunner: nenhuma ramificação por ambiente existia em nenhum lugar do arquivo antes desta correção. Risco real: uma `ReservationCreated` genuína em Production seria roteada para essa fila e simplesmente se acumularia, sem nenhum consumidor — quando o Checkpoint 2 ativasse um conector real, o backlog acumulado poderia ser processado e originar mensagens para reservas históricas, o que é proibido (ver §3.19).

**Correção**: o binding `communication.reservation-created-trigger` foi envolvido em `if (builder.Environment.IsDevelopment())` — mesma allowlist, mesmo mecanismo nativo `IHostEnvironment` que `Host.CreateApplicationBuilder` já expõe (nenhuma abstração/configuração nova). Fora de Development: nem a fila nem o binding são criados. A exchange `reservation-events` em si, e todo outro binding existente nela (Housekeeping/Dashboard/Workflow), permanece provisionado em todo ambiente, inalterado — apenas o binding específico de Communication é condicional. `CommunicationDbContext`, o schema `communication`, suas migrations e RLS continuam provisionados normalmente em qualquer ambiente — o gate é exclusivamente sobre o efeito colateral externo falso (fake connector) e o trigger/listener/topologia correspondente, nunca sobre a infraestrutura de dados de Communication, que permanece disponível para o Checkpoint 2. Templates/Configuration também permanecem disponíveis em todo ambiente, sem alteração.

Nenhuma fila pré-existente foi apagada/purgada automaticamente por esta correção — se algum ambiente real já possuir uma fila Communication de uma execução anterior à correção, esse fato deve ser tratado operacionalmente pelo Checkpoint 2 (rollout/migração), nunca por um `DeleteQueue`/`PurgeQueue` automático desta correção.

### 3.18 Provas da correção — Development/Staging/Production

Três testes de composição novos (`CommunicationEnvironmentGateTests`, real Postgres + RabbitMQ efêmeros + `IHostPro.MigrationRunner.dll` real + `IHostPro.Worker.dll` real, nunca um host reduzido manualmente montado), cada um provando AMBOS os gates (DI/listener do Worker E topologia do MigrationRunner) juntos, por ambiente:

- **Development**: MigrationRunner provisiona a fila/binding de Communication (confirmado via `QueueDeclarePassiveAsync` contra o broker real); o Worker registra e ativa o listener (`"Started message listening at rabbitmq://queue/communication.reservation-created-trigger"` aparece no log real); Housekeeping's own listener também ativo.
- **Staging**: nem a fila/binding é provisionado, nem o Worker ativa o listener de Communication — o cenário que especificamente teria falhado sob o `!IsProduction()` da primeira tentativa (§3.15, item 4). Housekeeping's own listener permanece ativo — o Worker inicia normalmente.
- **Production**: idêntico a Staging — nem fila/binding, nem listener de Communication — Housekeeping's own listener permanece ativo, Worker inicia normalmente, sem fail-fast do processo inteiro.

As três passaram, 3/3, verde (ver §4). `IHostPro.MigrationRunner` também foi executado 2× adicionais contra o Postgres/RabbitMQ reais de desenvolvimento, sob `DOTNET_ENVIRONMENT=Development` — idempotente, exit code 0 em ambas, com a fila/binding de Communication corretamente provisionada em ambas (ver §5).

### 3.18.1 Segundo defeito real de fixture, encontrado ao rodar a suíte completa após a correção de §3.17

A primeira execução da suíte completa após a correção de topologia (§3.17) regrediu severamente — 13, depois 15 falhas em 29, a maioria em testes historicamente estáveis (`ReservationCreatedWorkerRoundTripTests`, `DashboardReservationProjectionWorkerRoundTripTests`, `PropertyEventsWorkerRoundTripTests`, ambos os testes de `ReservationCreatedCommunicationWorkerRoundTripTests`, entre outros), incluindo um crash real do processo Worker (`NullReferenceException` dentro do próprio `Wolverine.RabbitMQ.Internal.RabbitMqListener.CreateAsync()`, ao tentar reconectar um listener para uma fila inexistente). Investigado por reprodução isolada (um único teste, log completo capturado) em vez de aceitar como contenção de ambiente: a causa raiz real era `RabbitMQ.Client.Exceptions.OperationInterruptedException: ... NOT_FOUND - no queue 'communication.reservation-created-trigger'`.

**Causa raiz confirmada**: cada uma das 18 fixtures que lançam `IHostPro.Worker.dll` também lança `IHostPro.MigrationRunner.dll`, através de seu próprio método `RunMigrationRunnerAsync()` — um helper que nunca precisou definir `DOTNET_ENVIRONMENT` porque, até a correção de §3.17, nada no `Program.cs` do MigrationRunner ramificava por ambiente. A correção de §3.15 já havia adicionado `DOTNET_ENVIRONMENT=Development` ao ambiente do subprocesso **Worker** de cada fixture, mas nunca ao subprocesso **MigrationRunner** da mesma fixture — um gap que permaneceu invisível até a correção de §3.17 introduzir a PRIMEIRA ramificação por ambiente no código do MigrationRunner. Resultado: o Worker de cada teste (corretamente em `Development`) tentava escutar a fila de Communication, enquanto o MigrationRunner do MESMO teste (implicitamente em `Production`, por omissão) nunca a criava — a mesma classe de defeito de §3.16, numa segunda superfície (o subprocesso do MigrationRunner, não do Worker). Confirmado por leitura direta: apenas dois arquivos já tratavam isso corretamente — `ReservationCreatedCommunicationWorkerRoundTripTests.cs`? não — na verdade nenhum dos 17 arquivos pré-existentes o fazia; somente o `CommunicationEnvironmentGateTests.cs`, escrito nesta própria correção, definia `DOTNET_ENVIRONMENT` para ambos os subprocessos desde o início.

**Corrigido** adicionando `DOTNET_ENVIRONMENT=Development` (mais `ASPNETCORE_ENVIRONMENT=Development`, por paridade) ao próprio `psi.Environment` de `RunMigrationRunnerAsync()` em todas as 18 fixtures — mesmo padrão, mesma variável, agora presente nos DOIS subprocessos que cada fixture lança. Reverificado: o mesmo lote de 5 testes que falhara (incluindo ambos os testes de Communication) voltou a passar 5/5; a suíte completa foi reexecutada do zero após a correção (ver §5).

### 3.19 Semântica prospectiva — o que o CP2 herda desta correção

Registrado explicitamente para orientar o Checkpoint 2: a automação outbound de Communication deste CP1 é Development-only, por desenho. Quando o Checkpoint 2 introduzir o conector real e ativar a topologia correspondente em Production, apenas `ReservationCreated` NOVOS, publicados APÓS a ativação da topologia real, devem originar uma mensagem. Nenhum reprocessamento retroativo de reservas históricas, nenhum bootstrap de backlog acumulado antes da ativação — essa é exatamente a garantia que o gate de topologia de §3.17 existe para preservar (nenhum backlog se acumula porque a fila nunca existiu fora de Development).

### 3.20 ADR-019 e ADR-020 — não reabertas por esta correção

Nem ADR-019 (leitura de contato do hóspede) nem ADR-020 (isolamento de handler chains) precisaram ser reabertas por este gate — nenhuma das duas trata de segurança de ambiente de deployment. A decisão de Production safety desta correção pertence exclusivamente a este documento (Fase 9); nenhuma ADR nova foi criada apenas para um gate temporário e específico ao CP1.

### 3.21 Falha intermitente observada — classificação honesta, causa raiz NÃO confirmada

Durante a revalidação do gate de §3.15/§3.16 (antes da homologação corretiva), `PolicyUpdatedRegressionTests.PolicyUpdated_delivered_through_real_RabbitMQ_to_the_real_Worker_advances_the_real_Redis_cache_generation` falhou duas vezes em três execuções isoladas consecutivas, sempre com o mesmo sintoma: o contador de geração no Redis chegava a `2` em vez de `1` ("exactly one InvalidateAsync (INCR) must have run for this single real PolicyUpdated, but found 2"). Investigação: (a) o mesmo sintoma idêntico já havia ocorrido numa rodada anterior à correção de §3.16, quando o código do gate de conector estava estruturalmente inerte (`IsProduction()` avaliava `true`, os dois blocos `if` nunca executavam) — descartando o gate de conector e a correção de ambiente como causa; (b) `IHostPro.Api` só publica eventos (`listen: false`, nunca consome — confirmado por leitura direta do `Program.cs` de Api), descartando um segundo consumidor concorrente da mesma fila; (c) `PolicyUpdatedCacheInvalidation` não implementa nenhuma deduplicação por mensagem — apenas incrementa o contador Redis a cada invocação. Uma terceira execução isolada, imediatamente em seguida, passou limpa (1/1).

**Classificação honesta, sem overclaiming**: falha intermitente real sob transporte real, confirmada NÃO relacionada às mudanças deste Checkpoint (mesmo sintoma ocorreu quando o código do gate estava estruturalmente inerte). A causa raiz NÃO foi confirmada — redelivery/ausência de deduplicação em `PolicyUpdatedCacheInvalidation` é uma HIPÓTESE plausível (mecanismo estruturalmente compatível com o sintoma observado), não um fato provado; nenhuma investigação adicional (captura de rede, log de redelivery do RabbitMQ, etc.) foi conduzida para confirmá-la. Não corrigida neste Checkpoint — investigação da causa raiz e eventual deduplicação ficam registradas como débito técnico/investigação futura, não como um fato resolvido.

**Recorrência na rodada definitiva pós-correção (§3.18.1)**: a suíte completa `IHostPro.Api.Tests.Integration`, executada do zero após a correção do gap de fixture de §3.18.1, terminou 28/29 verde — a única falha foi novamente `PolicyUpdatedRegressionTests`, com o sintoma idêntico ("expected 1L ... but found 2L"). Registrado honestamente como reforço da classificação acima (falha intermitente real, não uma regressão desta correção), não como um resultado "verde" fabricado por reexecução seletiva — a suíte não foi reexecutada por inteiro uma segunda vez apenas para obter um número totalmente limpo, em linha com a diretriz de não repetir testes pesados sem motivo técnico novo. Ver §4/§5 para os números exatos desta rodada.

**Controle A/B rigoroso (baseline `ae78af5` vs. current `8c2c38e`)**: por exigência explícita do usuário — a classificação acima como "dívida preexistente" não foi considerada suficiente sem comparação direta e controlada — o mesmo método `PolicyUpdated_delivered_through_real_RabbitMQ_to_the_real_Worker_advances_the_real_Redis_cache_generation`, sem nenhuma alteração de assertion/timeout/retry, foi executado 10 vezes isoladas e sequenciais em cada lado, em worktrees Git separados, mesma máquina, mesmo Docker, build Debug+Release fresca em cada worktree, RabbitMQ de dev parado durante toda a bateria (porta 5672 fixa dos Testcontainers), execuções nunca simultâneas:

- **Baseline `ae78af5`** (commit imediatamente anterior à correção Development-only desta rodada): **4 falhas em 10** (execuções 1, 5, 6, 8) — sintoma idêntico em todas ("expected 1L ... but found 2L").
- **Current `8c2c38e`** (após a correção Development-only + fix de fixture de §3.18.1): **4 falhas em 10** (execuções 3, 5, 7, 9) — sintoma idêntico em todas, nenhuma assinatura nova.
- **Diff de produção `ae78af5..8c2c38e`**: apenas `IHostPro.Worker/Program.cs` e `tools/IHostPro.MigrationRunner/Program.cs` foram alterados, ambos exclusivamente no gate de ambiente do conector/topologia de Communication — nenhuma linha toca `PolicyUpdatedCacheInvalidation`, o handler de evento de Configuration, a lógica de geração no Redis, o contrato `PolicyUpdated`, o handler Wolverine específico de PolicyUpdated, ou qualquer retry/dedup desse fluxo.
- **Conclusão**: mesma frequência (4/10 em ambos os lados), mesma assinatura de falha, nenhum arquivo de produção relacionado tocado — **dívida técnica preexistente confirmada por controle A/B, sem evidência de regressão introduzida por esta correção**. A hipótese de redelivery/ausência de deduplicação permanece exatamente isso — uma hipótese plausível, não confirmada; o log natural do teste (sem tracing AMQP habilitado) não mostrou nem confirmou nem refutou redelivery em nenhum dos lados. Não corrigido neste Checkpoint, por decisão do usuário e por não ser dentro do escopo da Fase 9.

### 3.22 Benchmark p95 (Fase 5) — evidência anterior, não reexecutado nesta correção

Distinção explícita entre evidência de rodadas anteriores e desta correção, para que o documento registre a evidência completa do CP1 sem ambiguidade: numa rodada anterior desta mesma sessão de fechamento (antes da correção de Production safety), o benchmark de resolução de política (`PolicyCacheAndOutboxTests`, Fase 5, decisão oficial 7 — alvo p95 = 50ms) falhou quando três suítes baseadas em Postgres real rodaram em paralelo (p95 medido = 52,12ms), e passou limpo quando reexecutado isoladamente — classificado, então, como contenção de recursos/timing ambiental, não uma regressão de código (nenhuma mudança de Fase 9 toca o código de resolução de política). Esta correção de Production safety (§3.15–§3.19) **não tocou nenhum código relacionado ao cache/resolução de política** e portanto **não foi reexecutado nesta rodada** — a evidência acima é da rodada anterior, registrada aqui por completude, nunca reapresentada como se fosse desta correção. O threshold de 50ms não foi alterado.

## 4. Testes — contagens exatas

As suítes de contexto (Configuration/Reservations/Communication/ArchitectureTests) abaixo refletem a rodada de fechamento original do CP1 (`ae78af5`) — nenhum código dessas camadas foi tocado pela correção de Production safety (§3.15–§3.18.1), que se limitou a `IHostPro.Worker/Program.cs`, `tools/IHostPro.MigrationRunner/Program.cs` e fixtures de teste de `IHostPro.Api.Tests.Integration`. A linha de `IHostPro.Api.Tests.Integration` foi **reexecutada por inteiro** nesta correção (código de fixture alterado) e reflete o número real e definitivo desta rodada, não o número anterior a `ae78af5`:

| Suíte | Resultado |
|---|---|
| `IHostPro.Contexts.Configuration.Tests.Unit` | 93/93, verde (rodada anterior a `ae78af5` — não tocado por esta correção) |
| `IHostPro.Contexts.Configuration.Tests.Integration` (inclui PolicyUpdated) | 80/80, verde (rodada anterior a `ae78af5` — não tocado por esta correção) |
| `IHostPro.Contexts.Reservations.Tests.Unit` | 61/61, verde (rodada anterior a `ae78af5` — não tocado por esta correção) |
| `IHostPro.Contexts.Reservations.Tests.Integration` (inclui `ReservationGuestContactReaderTests`) | 86/86, verde (rodada anterior a `ae78af5` — não tocado por esta correção) |
| `IHostPro.Contexts.Communication.Tests.Unit` | 35/35, verde (rodada anterior a `ae78af5` — não tocado por esta correção) |
| `IHostPro.Contexts.Communication.Tests.Integration` | 5/5, verde (rodada anterior a `ae78af5` — não tocado por esta correção) |
| `IHostPro.ArchitectureTests` (solução completa, inclui `CommunicationDependencyTests`/`CommunicationMessageExecutionScopeArchitectureTests`/restrição de referência ADR-019) | 173/173, verde — reconfirmado durante esta correção (§3.18.1 não altera regras de arquitetura) |
| `IHostPro.Api.Tests.Integration` (suíte completa pós-correção — inclui as 3 novas `CommunicationEnvironmentGateTests`, gates reais de transporte, fan-out de 4 consumidores, `WolverineThreeStoreCompositionTests`, `WolverineHandlerChainIsolationBaselineTests`) | **29 total, 28 verde, 1 falha** — a única falha é `PolicyUpdatedRegressionTests` (falha intermitente pré-existente, ver §3.21), não uma regressão desta correção |

## 5. Gate final de regressão

| Gate | Resultado |
|---|---|
| Release build (solução completa, após o gate de conector de §3.15/§3.16) | 0 erros |
| MigrationRunner (idempotência, execuções consecutivas contra Postgres de dev, inclui Communication + Templates, topologia agora Development-only — §3.17) | limpo, sem migration pendente, 2 execuções consecutivas exit code 0, fila/binding de Communication corretamente provisionada em ambas |
| NSwag (regeneração contra API real) | determinístico — diff contido exclusivamente a `CreateTemplateRequest`/`TemplateResponse`/`UpdateTemplateContentRequest` (Templates); zero superfície de API de Communication (nenhuma API própria existe); client nunca editado manualmente; não reexecutado nesta correção pois nenhum arquivo HTTP/frontend foi alterado |
| Angular (build de produção) | verde (rodada anterior a `ae78af5` — não tocado por esta correção) |
| `IHostPro.Api.Tests.Integration` (suíte completa, rodada definitiva pós-correção de §3.17/§3.18.1) | **29 total, 28 verde, 1 falha intermitente pré-existente conhecida** (`PolicyUpdatedRegressionTests`, ver §3.21) — nenhuma falha relacionada ao gate de ambiente ou à topologia |
| `CommunicationEnvironmentGateTests` (Development/Staging/Production — §3.18) | 3/3, verde |
| Suítes por contexto (Configuration/Reservations/Communication, Unit+Integration) | ver §4 — todas verdes (rodada anterior a `ae78af5`) |
| ArchitectureTests (solução completa) | 173/173, verde |
| `git diff --check` | a confirmar imediatamente antes do commit (§7) |
| Ambiente | RabbitMQ de dev parado durante os gates baseados em Testcontainers (porta fixa 5672); restauração ao estado de repouso a confirmar antes do commit (§7) |

## 6. Escopo pendente da Fase 9 — explicitamente NÃO iniciado

Registrado para que nenhuma leitura futura confunda o fechamento do Checkpoint 1 com o fechamento da Fase:

- **Checkpoint 2 — WhatsApp real**: nenhum provedor concreto escolhido; nenhuma credencial disponível; verificação de webhook não definida; estratégia de armazenamento de credencial não definida; encriptação em repouso não definida; política de resiliência HTTP (retry/circuit breaker/timeout) não definida; rate limits do provedor não investigados; semântica de idempotência do próprio provedor não investigada. Nenhuma dessas decisões foi tomada nesta fase — todas pendentes de definição explícita antes do início do Checkpoint 2.
- **Checkpoint 3 — Airbnb**: não iniciado, zero código.
- **Checkpoint 4 — Homologação final da Fase**: não iniciado, condicionado à conclusão dos Checkpoints 2 e 3.

## 7. Status do Checkpoint 1

Checkpoint 1 = **DEFINITIVAMENTE HOMOLOGADO E PUBLICADO** (após a sequência de publicação em `8c2c38e` e o controle A/B do gate diagnóstico de `PolicyUpdatedRegressionTests`, ambos registrados nos relatórios de fechamento).

## 8. Checkpoint 2.0 — Auditoria e Refinamento Read-Only do WhatsApp Real

Auditoria completa, read-only (nenhum código alterado), comparando Meta WhatsApp Cloud API direta e Twilio (BSP), cobrindo onboarding, credenciais, encryption-at-rest, outbound/webhook, templates, consent/LGPD, idempotência, lifecycle, resiliência, PII/auditoria, rollout, arquitetura (External Integrations ACL, boundary síncrono) e escopo do MVP recomendado. Recomendação: **Meta WhatsApp Cloud API direta** (menor dependência — Twilio não elimina a necessidade de conta/WABA Meta própria, apenas adiciona uma segunda camada de vendor e uma taxa recorrente). Relatório completo de 74 itens apresentado ao usuário; nenhuma decisão material foi tomada silenciosamente — as 12 decisões materiais (A–L) foram apresentadas para aprovação explícita antes de qualquer implementação.

## 9. Checkpoint 2.1 — External Integrations + Credential/Configuration Foundation

### 9.1 Escopo aprovado

Fundação apenas: Bounded Context **External Integrations** (Domain/Application/Infrastructure/Api/Contracts), configuração tenant-owned de integração WhatsApp (`WhatsAppIntegration`), permissão administrativa (`INTEGRATIONS:MANAGE`), abstração de credenciais (Development-only). Explicitamente **não** implementado neste checkpoint: chamada HTTP real à Meta, webhook, envio real de WhatsApp, `ProviderMessageId`/`Delivered`/`Read` em `Message`, provider template real, ativação de Production.

### 9.2 Conflito documental §13/§14/§17 — resolvido antes do scaffold (ADR-021)

Antes de criar qualquer projeto, a auditoria de pré-scaffold encontrou um conflito real entre `Architecture Principles.md` §17 (que pressupunha um projeto `ExternalIntegrations.Abstractions`) e §12/§13/§14 (que fecham `Contracts` como única superfície pública por-BC, e listam exatamente 4 exceções síncronas nomeadas, sem `Abstractions`). A implementação foi interrompida (`PARE`) e o conflito reportado ao usuário, sem resolução silenciosa.

**Decisão do usuário**: rejeitar `ExternalIntegrations.Abstractions`; publicar `IMessagingProvider` (e futuros Integration Events de status) em `ExternalIntegrations.Contracts`, a mesma e única superfície pública que todo outro Bounded Context já usa; registrar a chamada Communication → External Integrations como a sexta exceção síncrona do Architecture Principles §14 (Exceção 6 — a numeração literal do documento já chegava a "Exceção 5" com ADR-019, então a nova entrada foi numerada 6, não 5, para não colidir). **ADR-021 — External Integrations ACL and Synchronous Provider Boundary** registra a decisão completa; `Architecture Principles.md` §13/§14/§17 foram corrigidas em conformidade.

### 9.3 Scaffold implementado

`IHostPro.Contexts.ExternalIntegrations.{Contracts,Domain,Application,Infrastructure,Api}` + `Tests.Unit` + `Tests.Integration`, seguindo exatamente a convenção de projeto por-BC já estabelecida (Architecture Principles §16).

- **Contracts**: `IMessagingProvider.SendAsync(OutboundMessageRequest, CancellationToken) → OutboundMessageResult`, `ProviderFailureCategory` — deliberadamente provider-neutro (nenhum nome/tipo Meta/Twilio/Graph API aparece aqui; prova por `ArchitectureTests.Contracts_Assembly_Names_No_Real_Provider`). **Não consumido pela Communication ainda** — nenhum wiring artificial foi criado só para "usar" a interface; a ligação real pertence ao Checkpoint 2.2.
- **Domain**: `WhatsAppIntegration` (agregado tenant-owned) — `WabaId`, `PhoneNumberId`, `IsEnabled` (sempre `false`, sem `Enable()`/`Disable()` exposto neste checkpoint), três referências de secret opcionais (`AccessTokenSecretReference`, `AppSecretSecretReference`, `VerifyTokenSecretReference`) — nunca um valor de secret real.
- **Application**: `ConfigureWhatsAppIntegrationCommand`/`GetWhatsAppIntegrationQuery` (upsert + leitura, sem código de erro de negócio — ambas operações sempre têm sucesso), `IWhatsAppCredentialProvider` (porta, sem implementação de Production).
- **Infrastructure**: `ExternalIntegrationsDbContext` (schema `external_integrations`, sem `MapWolverineEnvelopeStorage` — nenhum Integration Event publicado ainda), `DevelopmentWhatsAppCredentialProvider` (lê de `IConfiguration`/User Secrets, registrado apenas quando `IsDevelopment()` — resolver a porta fora de Development falha alto, nunca cai silenciosamente para a implementação de Development).
- **Api**: `WhatsAppIntegrationController` (`GET`/`PUT` `/api/v1/integrations/whatsapp`, ambos exigindo `INTEGRATIONS:MANAGE`) — nunca aceita ou retorna um valor de secret, apenas booleanos `*Configured`.

### 9.4 Persistência — `external_integrations.whatsapp_integrations`

Schema/tabela novos via migration EF Core gerada por `dotnet ef migrations add` (nunca escrita à mão) — RLS `ENABLE`+`FORCE`, mesma política fail-closed (`current_setting('app.tenant_id', true)`) de todo outro Bounded Context, grants de menor privilégio idênticos ao padrão já estabelecido (`ihostpro_app`: SELECT/INSERT/UPDATE; sem CREATE/ALTER/DROP/BYPASSRLS), índice único em `tenant_id` (uma integração por tenant, CP2.0 Decisão E). Verificado diretamente contra Postgres real (`psql`) e por 13 testes de integração dedicados contra Testcontainers real (RLS fail-closed, isolamento entre tenants, índice único, referência de secret persistida literalmente sem transformação).

### 9.5 `INTEGRATIONS:MANAGE`

Primeira entrada genuinamente nova no catálogo de permissões desde o seed original da Fase 1 (todo outro código em `IdentityPermissionCodes` é uma "promoção" de um código já seedado, nunca uma entrada nova — ver o próprio comentário de `IdentityCatalogSeed`). Seedada via migration EF Core limpa (`InsertData`/`DeleteData`, nada além disso), ADMIN apenas, sem `INTEGRATIONS:READ`. Migration gerada por `dotnet ef migrations add` também corrigiu, como efeito colateral incidental e sem impacto de DDL real, uma divergência pré-existente do `IdentityDbContextModelSnapshot` (mapeamento Wolverine do outbox de Identity, presente desde o primeiro commit de `IdentityDbContext.cs`, nunca antes capturado no snapshot) — confirmado inofensivo por leitura direta da migration real gerada (contém exclusivamente as duas operações de dados da nova permissão) e por múltiplas execuções reais bem-sucedidas contra Postgres de dev.

### 9.6 Testes — contagens exatas desta rodada

| Suíte | Resultado |
|---|---|
| `IHostPro.ArchitectureTests` (solução completa) | **185/185, verde** (173 pré-existentes + 12 novos — nenhum `Abstractions`, Communication nunca referencia além de `Contracts`, sem dependência inversa, sem secret bruto, `WhatsAppIntegration` tenant-owned, etc.) |
| `ExternalIntegrations.Tests.Unit` | **10/10, verde** |
| `ExternalIntegrations.Tests.Integration` (Postgres real, RLS/isolamento/unicidade) | **13/13, verde** |
| `Identity.Tests.Unit` | **470/470, verde** |
| `Identity.Tests.Integration` | **419/419, verde** — 1 falha real encontrada e corrigida (`IdentityRowLevelSecurityTests`, contagem hardcoded de permissões 32→33/RolePermissions 39→40, consequência direta e esperada da nova permissão, não uma regressão) |
| MigrationRunner (Postgres/RabbitMQ reais de dev) | 3 execuções, exit code 0 em todas, schema `external_integrations` provisionado e idempotente |
| NSwag | determinístico, diff contido exclusivamente a `ConfigureWhatsAppIntegrationRequest`/`WhatsAppIntegrationResponse` |
| Angular (build de produção) | verde |
| Release build (solução completa) | 0 erros |
| `git diff --check` | limpo |

### 9.7 Gate bloqueado — `IHostPro.Api.Tests.Integration` (débito técnico pré-existente, não uma regressão do CP2.1)

A suíte completa (29 testes, ~20 fixtures) foi executada **4 vezes** (incluindo uma vez após um `wsl --shutdown` completo, com estado Docker genuinamente limpo confirmado por `docker ps -a`) — todas as 4 vezes reproduziram o mesmo padrão: exatamente uma fixture consegue vincular a porta fixa 5672 do RabbitMQ Testcontainers; toda fixture subsequente falha imediatamente com `Bind for 0.0.0.0:5672 failed: port is already allocated`. A investigação (timing dos eventos, teste manual de bind/release isolado bem-sucedido, estado limpo confirmado antes de cada tentativa) aponta para uma falha real de `IAsyncLifetime.DisposeAsync()` de exatamente uma fixture nunca liberar seu container — não um problema de ambiente acumulado, e não relacionado a nenhuma mudança do CP2.1 (nenhum arquivo de teste, nenhuma configuração do Testcontainers foi tocada nesta correção). Zero falhas de asserção de negócio em qualquer tentativa — 100% das falhas são o mesmo erro de infraestrutura Docker.

**Decisão do usuário**: aceitar a evidência já reunida por outros meios (ArchitectureTests, testes unitários/integração de ExternalIntegrations e Identity, MigrationRunner contra infraestrutura real, Release build — todos limpos) e prosseguir com a publicação do CP2.1, registrando este gate como bloqueado por um bug de infraestrutura de teste pré-existente, a ser investigado separadamente, nunca escondido. Ambiente restaurado (containers órfãos removidos, RabbitMQ de dev reiniciado e saudável) após cada tentativa.

### 9.8 Escopo explicitamente NÃO implementado neste checkpoint

Meta HTTP real; envio real de WhatsApp; webhook (`ExternalIntegrations.Api` não tem nenhum endpoint de webhook ainda); `ProviderMessageId`/`MessageStatus.Delivered`/`MessageStatus.Read` em `Communication`; mapeamento de provider template; ativação de Production (nenhum backend de secret de Production existe; `IsEnabled` permanece `false` sempre); pacote de resiliência HTTP (nenhum `HttpClient` real existe ainda); qualquer evento outbound com PII no RabbitMQ; migração do `FakeWhatsAppConnector` do CP1 (permanece exatamente como estava, Development-only).

### 9.9 Pré-requisitos para o Checkpoint 2.2

Antes de iniciar o CP2.2 (conector real Meta), o usuário precisará providenciar, fora deste chat: (1) conta de desenvolvimento Meta for Developers; (2) test Phone Number ID (auto-provisionado pela Meta ao completar o "Get Started"); (3) token de acesso temporário/de teste; (4) definição do Utility Template real a ser usado no sandbox; (5) configuração desses valores localmente via User Secrets/variáveis de ambiente (nunca colados no chat). Instruções de configuração local podem ser fornecidas quando o CP2.2 for autorizado.

## 10. Status final

Checkpoint 1 = **DEFINITIVAMENTE HOMOLOGADO E PUBLICADO**. Checkpoint 2.0 = **CONCLUÍDO** (auditoria read-only, decisões A–L aprovadas). Checkpoint 2.1 = **HOMOLOGADO E PUBLICADO** (foundation apenas — External Integrations BC, `WhatsAppIntegration`, `INTEGRATIONS:MANAGE`; gate `IHostPro.Api.Tests.Integration` bloqueado por débito técnico pré-existente de infraestrutura de teste, aceito pelo usuário, registrado para investigação futura separada). **Fase 9 — Comunicação e Integrações do MVP = EM ANDAMENTO** — não tratar como concluída até o fechamento do Checkpoint 4. Checkpoint 2.2 (conector real) **não foi iniciado**.
