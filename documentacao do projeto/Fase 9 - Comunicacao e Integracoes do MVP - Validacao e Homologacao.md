# Fase 9 — Comunicação e Integrações do MVP — Validação e Homologação

Versão: 1.1 (Checkpoint 0 — Auditoria Read-Only — registrado em §2; Checkpoint 1 — Communication Foundation + Templates + Fake WhatsApp Connector — registrado em §3, incluindo o gate de segurança do conector em Production — §3.15/§3.16)

Status: **Checkpoint 1 — HOMOLOGADO E PUBLICADO.** **Fase 9 — Comunicação e Integrações do MVP — EM ANDAMENTO.** Checkpoint 2 (WhatsApp real), Checkpoint 3 (Airbnb) e Checkpoint 4 (homologação final da Fase) permanecem pendentes — nenhum dos três foi iniciado. Ver §6 para o escopo exato ainda em aberto.

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

### 3.15 Gate de segurança — `FakeWhatsAppConnector` nunca pode ser resolvido em Production

Revisão pós-implementação (imediatamente antes da documentação/versionamento final) encontrou um blocker real: `AddCommunicationReservationConsumer()` — que registra `services.AddScoped<IOutboundMessageConnector, FakeWhatsAppConnector>()`, a única implementação existente — era chamada incondicionalmente em `IHostPro.Worker/Program.cs`, sem nenhum `IsDevelopment()`/`IsProduction()`. Não existe `appsettings.Production.json` para `IHostPro.Worker`, nem qualquer feature flag. Comportamento confirmado por leitura direta do código, não suposição: Development, Test/E2E e Production resolviam exatamente a mesma implementação — um conector que sempre reporta sucesso sem nunca chamar um provedor real. Em Production, uma `ReservationCreated` real levaria uma `Message` a `Sent` sem nenhuma entrega de fato ocorrer — um falso positivo operacional silencioso.

Quatro alternativas mínimas foram levantadas (gate por ambiente; conector que falha explicitamente em Production; startup fail-fast; feature flag `Communication:Enabled`) e apresentadas ao usuário como uma decisão material — nenhuma foi escolhida silenciosamente. **Decisão do usuário: gate por ambiente.** Implementado envolvendo as duas chamadas relevantes (`AddCommunicationModule`/`AddCommunicationReservationConsumer`, e o listener Wolverine `opts.ListenToRabbitQueue("communication.reservation-created-trigger")` — ambas, nunca apenas o registro DI isoladamente, para nunca deixar um listener ativo em Production sem handler registrado, o que produziria uma falha de resolução por mensagem em vez de uma ausência limpa) em `if (!builder.Environment.IsProduction())`. Fora de Production, Communication funciona exatamente como antes. Em Production (ambiente não explicitamente definido, ou explicitamente `Production`), o módulo inteiro fica ausente — nenhuma `Message` é criada/enfileirada, nenhuma fila é escutada — até o Checkpoint 2 entregar um conector real.

### 3.16 Defeito real, pré-existente, descoberto ao provar o gate de §3.15: `ASPNETCORE_ENVIRONMENT` nunca funcionou para `IHostPro.Worker`

Provar o gate acima expôs um segundo defeito real, mais profundo e completamente independente do connector: `IHostPro.Worker/Program.cs` usa `Host.CreateApplicationBuilder(args)` — o Generic Host puro (`Microsoft.Extensions.Hosting`), não o Web Host. O Generic Host reconhece exclusivamente `DOTNET_ENVIRONMENT`; a variável `ASPNETCORE_ENVIRONMENT` só é reconhecida pela camada de hospedagem web do ASP.NET Core (`WebApplication.CreateBuilder`), que este processo nunca usa. As 18 fixtures de teste que lançam o subprocesso real `IHostPro.Worker.dll` (toda a suíte `WorkerRoundTrip`/`WolverineThreeStoreCompositionTests`/`WolverineHandlerChainIsolationBaselineTests`, mais `WebE2EFixture.cs` do frontend) sempre definiram `ASPNETCORE_ENVIRONMENT=Development` — uma intenção nunca honrada: `builder.Environment.EnvironmentName` sempre resolveu para `Production` (o padrão do Generic Host quando `DOTNET_ENVIRONMENT` não está definido), em toda execução de teste desde que este processo existe. Isso nunca teve efeito observável até agora porque nenhum outro código deste processo ramifica por ambiente, exceto o gate de Identity já existente (`AddIdentityModule(..., builder.Environment.IsDevelopment())`, linha 69) — cujo próprio seeder (`DevelopmentIdentitySeeder`) sempre no-opa por padrão (`DevelopmentSeedOptions.Enabled = false`), tornando o defeito completamente invisível até o gate de §3.15 introduzir o primeiro comportamento condicional que os testes de fato precisavam disparar corretamente.

Confirmado empiricamente: com o gate de §3.15 aplicado e sem correção desta variável, os dois testes de transporte real de Communication falhavam com `communicationReady` nunca `True` (o Worker nunca reportava escutar a fila de Communication). Corrigido adicionando `DOTNET_ENVIRONMENT=Development` ao lado de `ASPNETCORE_ENVIRONMENT=Development` (mantida, sem custo, para o caso deste processo migrar para um Web Host no futuro) nas 18 fixtures — nunca substituindo, apenas complementando. Escopo confirmado como exclusivamente de teste: `IHostPro.Api`'s próprio `WebApplicationFactory`-based test host usa `WebApplication.CreateBuilder` de fato (Web Host real), onde `ASPNETCORE_ENVIRONMENT` já funcionava corretamente — nenhuma alteração feita ali. Após a correção, o gate de §3.15 foi reverificado com sucesso: os dois testes de transporte real de Communication voltaram a verde, e a suíte completa `IHostPro.Api.Tests.Integration` fechou em **26/26, 0 falhas** (rodada final, ver §5).

### 3.17 Falha intermitente observada, classificada como pré-existente e não relacionada a este Checkpoint

Durante a revalidação do gate de §3.15/§3.16, `PolicyUpdatedRegressionTests.PolicyUpdated_delivered_through_real_RabbitMQ_to_the_real_Worker_advances_the_real_Redis_cache_generation` falhou duas vezes em três execuções isoladas consecutivas, sempre com o mesmo sintoma: o contador de geração no Redis chegava a `2` em vez de `1` ("exactly one InvalidateAsync (INCR) must have run for this single real PolicyUpdated, but found 2"). Investigação: (a) o mesmo sintoma idêntico já havia ocorrido numa rodada anterior à correção de §3.16, quando o código do gate de conector estava estruturalmente inerte (`IsProduction()` avaliava `true`, os dois blocos `if` nunca executavam) — descartando o gate de conector e a correção de ambiente como causa; (b) `IHostPro.Api` só publica eventos (`listen: false`, nunca consome — confirmado por leitura direta do `Program.cs` de Api), descartando um segundo consumidor concorrente da mesma fila; (c) `PolicyUpdatedCacheInvalidation` não implementa nenhuma deduplicação por mensagem — apenas incrementa o contador Redis a cada invocação — tornando-o estruturalmente vulnerável a uma redelivery genuína do RabbitMQ sob variação real de timing, exatamente a mesma categoria de causa (contenção/timing sob transporte real) já registrada para o benchmark p95 de Fase 5. Uma terceira execução isolada, imediatamente em seguida, passou limpa (1/1). **Classificação**: falha intermitente pré-existente, de causa mais provável uma redelivery real sob transporte, não introduzida por nenhuma mudança deste Checkpoint — não corrigida aqui (adicionar deduplicação a `PolicyUpdatedCacheInvalidation` seria uma decisão de design nova, fora do mandato desta rodada de fechamento). Registrada aqui de forma transparente, não descartada silenciosamente; a rodada final da suíte completa (§5) passou 26/26 com este teste verde.

## 4. Testes — contagens exatas

Todas as suítes a seguir foram executadas nesta rodada de fechamento (não reaproveitadas de memória), contra o código já com as correções de §3.3 e §3.12 aplicadas:

| Suíte | Resultado |
|---|---|
| `IHostPro.Contexts.Configuration.Tests.Unit` | 93/93, verde |
| `IHostPro.Contexts.Configuration.Tests.Integration` (inclui PolicyUpdated) | 80/80, verde |
| `IHostPro.Contexts.Reservations.Tests.Unit` | 61/61, verde |
| `IHostPro.Contexts.Reservations.Tests.Integration` (inclui `ReservationGuestContactReaderTests`) | 86/86, verde |
| `IHostPro.Contexts.Communication.Tests.Unit` | 35/35, verde |
| `IHostPro.Contexts.Communication.Tests.Integration` | 5/5, verde |
| `IHostPro.ArchitectureTests` (solução completa, inclui `CommunicationDependencyTests`/`CommunicationMessageExecutionScopeArchitectureTests`/restrição de referência ADR-019) | 173/173, verde |
| `IHostPro.Api.Tests.Integration` (suíte completa — gates reais de transporte, fan-out de 4 consumidores, `WolverineThreeStoreCompositionTests`, `WolverineHandlerChainIsolationBaselineTests`) | 26/26, verde, 0 falhas |

## 5. Gate final de regressão

| Gate | Resultado |
|---|---|
| Release build (solução completa, após o gate de conector de §3.15/§3.16) | 0 erros |
| MigrationRunner (idempotência, execuções consecutivas contra Postgres de dev, inclui Communication + Templates) | limpo, sem migration pendente, topologia RabbitMQ de Communication reafirmada |
| NSwag (regeneração contra API real) | determinístico — diff contido exclusivamente a `CreateTemplateRequest`/`TemplateResponse`/`UpdateTemplateContentRequest` (Templates); zero superfície de API de Communication (nenhuma API própria existe); client nunca editado manualmente |
| Angular (build de produção) | verde |
| `IHostPro.Api.Tests.Integration` (suíte completa, rodada final pós §3.15/§3.16) | 26/26, verde, 0 falhas |
| Suítes por contexto (Configuration/Reservations/Communication, Unit+Integration) | ver §4 — todas verdes |
| ArchitectureTests (solução completa) | 173/173, verde |
| `git diff --check` | a confirmar imediatamente antes do commit (§7) |
| Ambiente | RabbitMQ de dev parado durante os gates baseados em Testcontainers (porta fixa 5672); nenhum container órfão remanescente após a correção de §3.12, item 3 |

## 6. Escopo pendente da Fase 9 — explicitamente NÃO iniciado

Registrado para que nenhuma leitura futura confunda o fechamento do Checkpoint 1 com o fechamento da Fase:

- **Checkpoint 2 — WhatsApp real**: nenhum provedor concreto escolhido; nenhuma credencial disponível; verificação de webhook não definida; estratégia de armazenamento de credencial não definida; encriptação em repouso não definida; política de resiliência HTTP (retry/circuit breaker/timeout) não definida; rate limits do provedor não investigados; semântica de idempotência do próprio provedor não investigada. Nenhuma dessas decisões foi tomada nesta fase — todas pendentes de definição explícita antes do início do Checkpoint 2.
- **Checkpoint 3 — Airbnb**: não iniciado, zero código.
- **Checkpoint 4 — Homologação final da Fase**: não iniciado, condicionado à conclusão dos Checkpoints 2 e 3.

## 7. Status final

Checkpoint 1 = **HOMOLOGADO E PUBLICADO** (após a sequência de publicação registrada no relatório de fechamento). **Fase 9 — Comunicação e Integrações do MVP = EM ANDAMENTO** — não tratar como concluída até o fechamento do Checkpoint 4.
