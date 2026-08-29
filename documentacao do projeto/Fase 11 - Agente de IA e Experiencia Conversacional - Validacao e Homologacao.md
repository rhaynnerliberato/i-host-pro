# Fase 11 — Agente de IA e Experiência Conversacional — Validação e Homologação

Versão: 1.1
Status: Em andamento — Checkpoint 2 concluído

## 1. Objetivo

Registrar a validação e homologação da Fase 11 (Agente de IA e Experiência Conversacional), conforme `Plano Executivo de Desenvolvimento por Fases.md` e a estrutura de Checkpoints CP0–CP8 proposta e aprovada pelo usuário. Este documento é criado agora, no fechamento do Checkpoint 1 — o Checkpoint 0 (architecture & product decision gate, read-only) não produziu documentação própria (relatório entregue e aprovado em conversa); suas decisões relevantes são referenciadas aqui.

## 2. Checkpoint 0 — Architecture & Product Decision Gate (Read-Only)

**Status:** Concluído e aprovado. Nenhum arquivo alterado.

Auditoria completa do escopo literal da Fase 11 contra Documento 16 (fonte primária), ADR-009, Architecture Principles §3/§14, Documento 13 §30, Documento 19 §12, Documento 09 §9, Documento 12 §9/§10, Documento 06 §6/§14, Documento 07 §8, Documento 17 Workflows 13/14, Documento 20/15/05. Achados e decisões principais:

- **Agent ownership**: já ratificado (não uma decisão nova) — Bounded Context dedicado "AI Agent" (Core), já listado em `Architecture Principles.md` §3 e ADR-009 (Status: Aceito), contendo AI Gateway/Context Builder/Tools.
- **Provedor inicial**: Anthropic Claude, via `IModelProvider` (ADR-009) — chamada REST direta, sem SDK de terceiros.
- **Regra de comunicação do AI Agent**: já pré-aprovada como Exceção 3 de `Architecture Principles.md` §14 — Tools são adapters finos que invocam o Application Service público do contexto correspondente, nunca domínio/infraestrutura diretamente (mesmo mecanismo já usado por Workflow Orchestration, Exceção 2).
- **Achado crítico**: nenhuma mensagem inbound de convidado era processada até este ponto (ADR-022, item 19) — fundação zero, não construída em nenhuma fase anterior. Esse é o motivo do Checkpoint 1 focar exclusivamente em recepção/persistência, sem nenhuma lógica de IA.
- **Conversation model**: Documento 12 §9/§10 distingue `Conversa` (owned por Communication) de `Sessão IA` (owned pelo futuro AI Agent BC) — dois conceitos distintos, nunca fundidos.
- **Human handoff**: já especificado literalmente em Documento 06 §6 (`Atendimento Automático → Escalado para Humano`) e Documento 17 Workflow 14 — não uma decisão em aberto, apenas não implementada ainda (Checkpoint 6).
- **Achado de permissões**: o Role `AI_AGENT` já existe, seedado desde a migração inicial da Fase 1 (`IdentityCatalogSeed`), mas sem nenhum mecanismo de emissão de token/principal — scaffold legado, não tocado neste checkpoint (decisão CP0: `NOT MVP`).

Classificação final do gate: aprovado, com uma lista de decisões `B — USER DECISION REQUIRED` resolvidas explicitamente pelo usuário antes da autorização do Checkpoint 1 (ver §3 abaixo).

## 3. Decisões Oficiais do CP0 (aplicadas a partir do CP1)

- `AgentBoundedContextRequired=true` (já ratificado, não uma decisão nova).
- `AgentToolExecutionModel=IN_PROCESS_APPLICATION_SERVICES` — Tools chamam Commands/Queries in-process, nunca via HTTP/JWT.
- `AI_AGENT` JWT/service-account: `NOT MVP` — o Role pré-existente é tratado como scaffold legado/futuro, não promovido a policies/endpoints agora.
- `LLMProviderSelected=true` (Anthropic Claude, ADR-009) — implementação real ainda `NOT MVP` (Checkpoint 7).
- Fake `IModelProvider`: foundation first (Checkpoint 2).
- `VectorDatabase=NOT MVP`.
- `PromptVersioning=NOT MVP`.
- `RAG=STRUCTURED RETRIEVAL THROUGH TOOLS/READERS` — nunca busca semântica sobre texto não-estruturado neste MVP.
- `PixPaymentQueryCapability`: MUST na Fase 11, implementação deferida ao Checkpoint 3 (reabre a capability que a Fase 10 deixou `DEFERRED_NO_CURRENT_MVP_USE_CASE`, como requisito novo da Fase 11, não uma correção retroativa da Fase 10).
- `WriteConfirmation=REQUIRED` para ações state-changing/financeiras (aplicação em Checkpoint 4).
- `HumanHandoffResume=MANUAL ONLY` — nenhuma retomada automática após "Suspender IA".

## 4. Checkpoints Oficiais da Fase 11

CP0 (concluído) — Architecture & Product Decision Gate
CP1 (concluído) — Inbound Conversation Foundation
CP2 (concluído, este documento) — AI Agent Foundation
CP3 — Read Tools & Context Builder
CP4 — Write Tools & Response Delivery
CP5 — Policies, Workflow & Conversational Orchestration
CP6 — Human Handoff, Safety & Audit
CP7 — Anthropic Claude Real Proof
CP8 — Final Homologation

## 5. Checkpoint 1 — Inbound Conversation Foundation

**Status:** Concluído e homologado. `AIImplemented=false`. `LLMCalls=0`. `ExternalLLMNetworkCalls=0`.

**Objetivo**: construir somente a fundação necessária para que uma mensagem inbound real de hóspede possa fluir — normalização Meta → resolução de tenant → resolução de guest/reservation → Conversation → persistência da mensagem inbound — sem nenhuma lógica de IA.

### 5.1 Governança prévia — duas decisões resolvidas antes da implementação

Auditoria prévia (mandato item 14/17) identificou dois pontos de parada obrigatórios, resolvidos explicitamente pelo usuário antes de qualquer código:

1. **Nova exceção síncrona #13** (Communication → Reservations, resolução de reserva por telefone) — confirmada como NÃO sendo uma extensão da Exceção 5 (ADR-019, propósito inverso). Aprovada e formalizada em `ADR-029`.
2. **Regra de elegibilidade**: apenas `ReservationStatus.Confirmed` — `Cancelled`/`Closed` nunca elegíveis, sem janela temporal.

### 5.2 ADR-022 — extensão cronológica, não reabertura

O webhook do WhatsApp (Fase 9, ADR-022) passa a processar `messages[]` além de `statuses[]` — mesma verificação de assinatura, mesmo routing directory `PhoneNumberId → TenantId`, mesma disciplina PII-safe. Nenhuma das 18 decisões originais de ADR-022 foi reaberta ou enfraquecida (amendment cronológico registrado na própria ADR). External Integrations permanece o único owner do payload/protocolo Meta — `MetaWebhookMessage`/`MetaWebhookMessageText` confinados a `Infrastructure.Meta`, nunca vazando para Contracts/Domain/Application/Api (provado por `ArchitectureTests`).

### 5.3 Evento provider-neutro

`InboundGuestMessageReceived` (`ExternalIntegrations.Contracts`) — o segundo Integration Event que External Integrations publica (o primeiro foi `WhatsAppMessageStatusChanged`, Fase 9 CP2.3.3). Payload: `TenantId`, `ProviderMessageId`, `Channel`, `SenderPhoneNormalized` (dígitos-somente), `MessageType` (`Text`/`Unsupported`), `Text` (apenas para `Text`), `OccurredAtUtc`. CP1 é **TEXT ONLY** — qualquer outro tipo de mensagem é classificado `Unsupported`, nunca baixado/modelado (Documento 16 §31).

### 5.4 Exceção síncrona #13 — `IReservationByGuestPhoneReader`

Formalizada em `ADR-029`. `Reservations.Contracts.IReservationByGuestPhoneReader.FindEligibleByGuestPhoneAsync(tenantId, guestPhoneNormalized, ct)` → lista de `ReservationCandidate(ReservationId, PropertyId, CheckInAt, CheckOutAt)`. Implementada exclusivamente em `Reservations.Infrastructure` (`ReservationByGuestPhoneReader`), reduzindo `GuestPhone` a dígitos-somente para comparação — mesma regra que `MetaWebhookMessageProcessor` aplica no lado de External Integrations (nenhum utilitário compartilhado criado; duplicação mínima deliberadamente aceita, documentada nos dois pontos). Nova migration aditiva `AddGuestPhoneIndex` (índice `(tenant_id, guest_phone)`, sem a coluna `status`).

### 5.5 Resolução 0/1/N — decisão oficial

- **1 candidato**: resolve automaticamente — cria/reutiliza a `Conversation`, persiste o `Message` inbound.
- **0 candidatos**: nenhuma `Conversation` criada, nenhuma ação, nenhuma resposta — outcome `NoReservationResolved`, apenas logado.
- **N candidatos**: nunca escolhido automaticamente — nenhuma `Conversation` criada, outcome `ReservationResolutionRequired`, apenas logado. Desambiguação conversacional fica para um checkpoint futuro (quando o Agente de IA existir).

### 5.6 `Conversation` (novo agregado, Communication)

`Communication.Domain.Conversation` — `Id, TenantId, ReservationId, Channel, Status (Active), CreatedAtUtc, UpdatedAtUtc, LastMessageAtUtc`. Origem sempre `ReservationId` (Documento 12 §17 — "nenhuma conversa existe sem uma origem"); nunca criada para 0/N candidatos. Cardinalidade: uma `Conversation` ativa por `(TenantId, ReservationId, Channel)`, garantida por índice único real — nenhuma semântica de archive/reopen (Checkpoint 6, Human Handoff, é o dono futuro dessas regras). Deliberadamente sem nenhum estado de IA (intent/confiança/modelo/prompt) — isso pertence à futura `AISession` do AI Agent BC (Checkpoint 2), um conceito distinto, referenciado apenas por id opaco.

### 5.7 `Message` — extensão mínima, sem quebra de compatibilidade

`Message` ganha `ConversationId` (obrigatório, todas as direções) e `Direction` (`Outbound`/`Inbound`). Toda mensagem pré-existente (outbound) foi backfillada deterministicamente — nenhum invariante pré-existente foi alterado (`TemplateKey`/`RenderedContent`/`IdempotencyKey`/`ReservationId`/`Status` permanecem `NOT NULL` exatamente como antes). Novo valor de `MessageStatus.Received`, exclusivo de mensagens inbound, sem transições adicionais neste checkpoint. `Message.CreateInbound` nunca persiste texto de tipo `Unsupported` como `null` (usa o marcador `"[UNSUPPORTED MESSAGE TYPE]"`, preservando o invariante `RenderedContent` NOT NULL).

### 5.8 Backfill — auditoria e execução

Auditoria de dev (antes de qualquer migration): 3 `communication.messages` pré-existentes, todos com `ReservationId` preenchido, cada um distinto, todos artefatos órfãos do teste real de sandbox WhatsApp da Fase 9 (nenhum deles correspondendo a uma `Reservation` ainda existente — sem FK cross-schema, por desenho). Backfill determinístico via `IHostPro.MigrationRunner.ConversationBackfillBootstrapStep` (mesmo mecanismo ADR-017 já usado pelo backfill de `GuestStayOperation`, necessário aqui porque `communication.messages`/`conversations` têm `FORCE ROW LEVEL SECURITY` — um `INSERT`/`UPDATE` cross-tenant dentro da própria migration EF veria zero linhas sem `app.tenant_id` setado por tenant). Resultado esperado e confirmado: 3 `Conversation`s criadas (um grupo por `reservation_id` distinto), 3 `Message`s backfillados.

### 5.9 Idempotência

Deduplicação por `TenantId`/`Channel`/`ProviderMessageId`, reaproveitando o mecanismo de `IdempotencyKey` já existente (lookup-before-create + índice único, mesmo padrão de todo processador outbound). Nenhuma nova tabela de deduplicação criada.

### 5.10 Sem resposta, sem IA

Confirmado pelo desenho e pelos testes: nenhuma mensagem é enviada de volta ao hóspede neste checkpoint, nenhuma chamada de modelo de linguagem ocorre. `SendAgentResponseCommand`/`IModelProvider`/`AISession`/qualquer artefato do AI Agent BC permanecem inexistentes (provado por `ArchitectureTests` dedicados).

### 5.11 Achado corrigido durante a implementação

A primeira versão do E2E real revelou que `IHostPro.Api/Program.cs` não declarava a regra de publicação Wolverine (`PublishMessage<InboundGuestMessageReceived>().ToRabbitRoutingKey(...)`) para o novo evento — o evento era enfileirado no outbox local mas nunca roteado para a exchange real. Corrigido antes da homologação final (mesmo padrão já usado por `WhatsAppMessageStatusChanged`/eventos do Airbnb). Sem esse teste E2E real, esta lacuna não teria sido descoberta por nenhum teste unitário/de integração com fakes.

### 5.12 Testes — contagens exatas

| Suíte | Resultado |
|---|---|
| `IHostPro.Contexts.Reservations.Tests.Unit` | 90 aprovados |
| `IHostPro.Contexts.Reservations.Tests.Integration` (inclui os 8 novos testes de `IReservationByGuestPhoneReader`) | 105 aprovados |
| `IHostPro.Contexts.ExternalIntegrations.Tests.Unit` | 131 aprovados |
| `IHostPro.Contexts.ExternalIntegrations.Tests.Integration` | 52 aprovados, 1 falha pré-existente e não relacionada (`MetaWhatsAppSandboxProofTests` — credencial real de sandbox Meta expirada/rotacionada, blocker de Production já documentado, não uma regressão deste checkpoint) |
| `IHostPro.Contexts.Communication.Tests.Unit` (inclui os 5 novos testes de `InboundGuestMessageProcessor`) | 101 aprovados |
| `IHostPro.Contexts.Communication.Tests.Integration` | 12 aprovados |
| `IHostPro.ArchitectureTests` (267, de 262 — 5 novos testes deste checkpoint, mais 4 testes pré-existentes de contagem/lista atualizados para refletir os novos tipos legítimos) | 267 aprovados |
| `InboundGuestMessageWorkflowRoundTripTests` (E2E real — Postgres/RabbitMQ/Worker/Api reais) — cenário único, zero-reservation, N-reservation, duplicidade | 4 aprovados |
| `IHostPro.Api.Tests.Integration` (suíte completa) | ver §5.13 |
| MigrationRunner Run #1/#2 | ver §5.13 |
| Build Release | ver §5.13 |

### 5.13 Regressão completa e evidência final

| Suíte | Resultado |
|---|---|
| `IHostPro.Api.Tests.Integration` (suíte completa, uma única execução — inclui os 4 novos testes E2E deste checkpoint, e reconfirma sem regressão `WhatsAppMessageStatusChangedWorkerRoundTripTests`/`GuestAccessDeliveryWorkflowRoundTripTests`/demais suítes E2E pré-existentes) | 59 aprovados |
| MigrationRunner Run #1 (Postgres/RabbitMQ descartáveis reais) | Exit code 0 — todos os 9 DbContexts migrados, incluindo `AddGuestPhoneIndex`/`AddConversationAndInboundSupport`; novo bootstrap step `communication.messages.conversation_id` executado (0 tenants/0 linhas — banco descartável vazio, resultado esperado); nova topologia RabbitMQ provisionada (`communication.inbound-guest-message-trigger` declarada e vinculada a `external-integrations-events`/`inbound_guest_message_received`) |
| MigrationRunner Run #2 (mesmo banco, imediatamente em seguida) | Exit code 0 — zero drift, zero linha nova em qualquer backfill (incluindo o novo `Communication conversation backfill: 0/0`) |
| Verificação de schema pós-Run (read-only, SQL direto) | `communication.conversations`: RLS `ENABLE`+`FORCE` intactos, policy `tenant_isolation` fail-closed, índice único `(tenant_id, reservation_id, channel)`, grants `ihostpro_app`=SELECT/INSERT/UPDATE (sem DELETE); `communication.messages.conversation_id`: `NOT NULL` sem default residual; `communication.messages.direction`: `NOT NULL DEFAULT 'Outbound'`; `reservations.reservations`: índice `(tenant_id, guest_phone)` presente |
| Build Release (solução completa) | 0 erro |
| Build Debug (solução completa) | 0 erro |
| `git diff --check` | Sem erros (apenas avisos benignos de normalização LF→CRLF) |

**Achado corrigido durante a implementação, registrado por transparência (§5.11)**: a primeira execução do E2E real revelou que `IHostPro.Api/Program.cs` não declarava a regra de publicação Wolverine para `InboundGuestMessageReceived` — o evento era enfileirado no outbox local mas nunca roteado para a exchange `external-integrations-events`. Corrigido (`RouteExternalIntegrationsEvent<InboundGuestMessageReceived>("inbound_guest_message_received")`, mesmo padrão já usado para `WhatsAppMessageStatusChanged`/eventos do Airbnb) antes de qualquer commit. Nenhum teste unitário/de integração com fakes/spies teria revelado esta lacuna — apenas o round-trip real via RabbitMQ a expôs, confirmando o valor do gate E2E exigido pelo mandato.

`Cp1CommitCount`: registrado no relatório final da conversa de homologação (§6 do relatório de fechamento).

## 6. Checkpoint 2 — AI Agent Foundation

**Status:** Concluído e homologado. `AIImplemented=true`. `BusinessToolsImplemented=false`. `AnthropicIntegrated=false`. `ExternalLLMNetworkCalls=0`. `ConfidenceScale=Normalized_0_to_1_Nullable`. `ResponseTextPersistence=false`.

**Objetivo**: criar o novo Bounded Context AI Agent (Core), torná-lo consumidor real de `ConversationMessageReceived` (CP1), e construir a fundação completa do fluxo de sessão — resolução/criação de `AgentSession`, leitura sanitizada de histórico, Context Builder mínimo, `FakeModelProvider` determinístico, persistência de `AgentInteraction` — sem nenhuma Tool de negócio, sem Anthropic real, sem nenhuma resposta enviada ao hóspede.

### 6.1 Governança prévia — dois pontos de parada resolvidos antes de continuar a implementação

Auditoria prévia (mandato do CP2, itens 10/33/55) identificou dois pontos de parada obrigatórios, resolvidos explicitamente pelo usuário antes de qualquer código dependente:

1. **Nova exceção síncrona #14** (AI Agent → Communication, leitura de histórico sanitizado de conversa) — auditada a `Architecture Principles.md` §14 linha por linha (treze exceções nomeadas até então); confirmada como NÃO sendo uma extensão informal da Exceção 3 (que cobre Tools executando capabilities via Application Service, nunca uma leitura dedicada). Aprovada e formalizada em `ADR-030`.
2. **Escala de confidence**: normalizada para `decimal?`, `0..1` inclusive quando não nulo, `null` = provider não forneceu. Nenhum threshold de negócio decidido (fica para CP6/Policy). Migração de `double?` → `decimal?` aplicada em `AgentSession`/`AgentInteraction`/`ModelResult` antes de qualquer migration definitiva.

### 6.2 ADR-030 — Exceção Síncrona #14

`Communication.Contracts.IConversationHistoryReader` (implementado exclusivamente em `Communication.Infrastructure`) — entrada `TenantId`/`ConversationId`, retorna `IReadOnlyList<ConversationHistoryMessage(MessageId, Direction, Content, OccurredAtUtc)>` em ordem cronológica determinística (`CreatedAtUtc` + `MessageId` como tie-breaker). AI Agent nunca referencia `Communication.Domain`/`Application`/`Infrastructure`/`CommunicationDbContext` diretamente — provado por `ArchitectureTests` dedicados (`AIAgentFoundationArchitectureTests.No_Other_Context_Assembly_References_IConversationHistoryReader_Except_AIAgent` e a suíte de dependência geral).

**Achado de segurança de conteúdo, descoberto durante a implementação**: conteúdo já marcado `"[SENSITIVE CONTENT REDACTED]"` na escrita (entrega de credencial de acesso, ADR-028) é retornado como está — nenhuma reconstrução possível, o conteúdo real nunca foi persistido. Uma mensagem de entrega de cobrança PIX (`TemplateKey = "LATE_CHECKOUT_PIX_PAYMENT"`) é DIFERENTE: por decisão já homologada da Fase 10 (ADR-025/027), o payload real do QR/copia-e-cola é renderizado diretamente em `Message.RenderedContent` (esse é o destino final pretendido — o hóspede precisa lê-lo/escaneá-lo). Como isso nunca pode alcançar o AI Agent, `ConversationHistoryReader` — o lado de LEITURA, nunca a persistência já homologada da Fase 10 — substitui esse conteúdo pelo mesmo marcador fixo antes de retornar. Provado por teste de integração dedicado contra Postgres real (`GetHistoryAsync_redacts_a_PIX_delivery_messages_real_QR_content_never_leaking_it`).

### 6.3 AgentSession / AgentInteraction (novos agregados, schema `ai_agent`)

`AgentSession` — `Id, TenantId, ConversationId, ReservationId, Status (Active/Completed), Language?, Intent?, Confidence?, ModelProvider?, ModelName?, StartedAtUtc, UpdatedAtUtc, LastInteractionAtUtc?, EndedAtUtc?`. Cardinalidade: uma sessão `Active` por `(TenantId, ConversationId)` — nenhuma regra explícita em Documento 12 §10, preferência MVP aplicada e aprovada; enforced por índice único parcial (`WHERE status = 'Active'`), nunca no Domain. Múltiplas sessões `Completed` históricas são permitidas.

`AgentInteraction` — `Id, TenantId, AgentSessionId, InboundMessageId, Intent?, Language?, Confidence?, ModelProvider, ModelName, InputTokens, OutputTokens, StartedAtUtc, CompletedAtUtc?, Outcome (InProgress/Success/Failure)`. Idempotência por `(TenantId, InboundMessageId)` (unique index, defesa em profundidade atrás do lookup-before-create do consumer). Deliberadamente sem `ResponseText`/`PromptText`/`ToolResult` — decisão oficial: Documento 16 §24 audita "resposta enviada"; este checkpoint nunca envia nada ao hóspede, então a saída do modelo ainda não é uma "resposta enviada" (CP4, quando existir entrega real, decidirá a referência a `Communication.Message`, nunca duplicando o corpo).

`Confidence` (ambos agregados) — `decimal?`, `0..1` inclusive quando não nulo, nunca clampado — fora da faixa é `ArgumentOutOfRangeException` (`ConfidenceGuard`, compartilhado internamente ao Domain).

### 6.4 IModelProvider / FakeModelProvider

`IModelProvider` (Application) — `ProviderName`, `ModelName`, `GenerateAsync(ModelRequest) → ModelResult`. `ModelRequest(SystemPrompt?, Messages)` — deliberadamente mínimo (mandato item 14); `SystemPrompt` nunca populado com prompt de negócio hardcoded (Documento 16 §22). `ModelResult(Text, DetectedLanguage?, Intent?, Confidence?, InputTokens, OutputTokens, ModelName, FinishReason?)`.

`FakeModelProvider` (Infrastructure) — única implementação deste checkpoint, determinística, zero rede. Resposta/tokens são função pura do conteúdo de `ModelRequest.Messages`. Dois marcadores determinísticos permitem fixtures previsíveis sem reconfiguração de DI: `FailureTriggerMarker` (`"[FAKE_MODEL_FAILURE]"`, lança `ModelProviderException`) e `ConfidenceMarkerPrefix` (`"[FAKE_MODEL_CONFIDENCE:0.90]"`, retorna exatamente o valor). `DetectedLanguage` fixo `"pt-BR"`; `Intent`/`Confidence` nulos por padrão (nenhum catálogo de intents, nenhuma suposição de escala além do que os marcadores provam explicitamente).

### 6.5 Context Builder

`AgentContextBuilder` (Application) — único acoplamento cross-context: `IConversationHistoryReader`. Constrói `ModelRequest` a partir do histórico sanitizado; não consulta Reservations/GuestOperations/Payments/Housekeeping/PropertyManagement/Policies via Tools (isso é CP3). Nenhum Tool de negócio, nenhum executor genérico (`IAgentTool`/`AgentToolDescriptor` não foram criados — decisão de não construir por ausência de qualquer consumidor real neste checkpoint, mesmo precedente de ADR-021).

### 6.6 Fluxo real — `ConversationMessageReceivedProcessor`

`ConversationMessageReceived → AI Agent Worker (fila própria aiagent.conversation-message-trigger, nunca compartilhada com Communication) → idempotência por (TenantId, InboundMessageId) → resolve/cria AgentSession ativa → IConversationHistoryReader → AgentContextBuilder → FakeModelProvider → AgentInteraction persistida`. Sucesso atualiza `AgentSession.RecordInteraction` (Language/Intent/Confidence/ModelProvider/ModelName); falha controlada (`ModelProviderException`) persiste `AgentInteraction` com `Outcome=Failure` e **deixa a sessão intocada** — nenhuma metadata confirmada existe para registrar a partir de uma chamada que falhou. Nenhuma ação outbound de Communication em nenhum caminho.

Mirrors ADR-016 exatamente: `AIAgentMessageExecutionScope` (novo, único autorizado a segurar `IServiceScopeFactory` em AI Agent) + `ConversationMessageReceivedHandler` (adapter fino Wolverine) — provado por `AIAgentMessageExecutionScopeArchitectureTests` dedicados.

### 6.7 Outbox / RLS / MigrationRunner

`AIAgentDbContext` (schema `ai_agent`) — `agent_sessions`/`agent_interactions`, RLS `ENABLE`+`FORCE`, policy `tenant_isolation` fail-closed idêntica a todo outro Bounded Context. Outbox próprio `ai_agent_messaging` — AI Agent não publica nenhum evento próprio ainda; a infraestrutura existe apenas para que `IDbContextOutbox<AIAgentDbContext>` resolva dentro do consumer Wolverine (mesmo requisito empírico de todo Bounded Context com escrita transacional), nunca para justificar um evento artificial (mandato item 29).

`IHostPro.MigrationRunner` — `AIAgentDbContext` descoberto automaticamente pelo mecanismo de reflexão já existente (`IModuleDbContext`); outbox provisionado explicitamente (mesmo padrão de todo outro Bounded Context); nova exchange `communication-events` ganha o binding `aiagent.conversation-message-trigger` → `conversation_message_received`.

### 6.8 Achados corrigidos durante a implementação

- **`ServiceLocationPolicy.NotAllowed`**: o primeiro E2E real revelou `Wolverine.Configuration.InvalidServiceLocationException` ao gerar código para `ConversationMessageReceivedHandler` — faltava `opts.CodeGeneration.AlwaysUseServiceLocationFor<IAIAgentMessageExecutionScope>()` em `IHostPro.Worker/Program.cs` (sétima aplicação do mesmo padrão ADR-016 já usado por Housekeeping/Reservations/Dashboard/Communication/Guest Operations/Payments). Corrigido antes da homologação final.
- **Redação de QR PIX no reader** (ver §6.2) — descoberta e corrigida no boundary de leitura do AI Agent, sem alterar a persistência já homologada da Fase 10.
- **Erratum documental em Documento 07 §47**: a tabela final repetia por engano a linha de `GuestAccessDeliveryRequested` (§46) em vez de `InboundGuestMessageReceived`; corrigido nesta revisão, sem nenhuma mudança de comportamento.

### 6.9 Testes — contagens exatas

| Suíte | Resultado |
|---|---|
| `IHostPro.Contexts.AIAgent.Tests.Unit` (novo projeto) | 40 aprovados |
| `IHostPro.Contexts.AIAgent.Tests.Integration` (novo projeto, inclui teste dedicado de round-trip de `Confidence` decimal) | 10 aprovados |
| `IHostPro.Contexts.Communication.Tests.Unit` | 101 aprovados (sem regressão) |
| `IHostPro.Contexts.Communication.Tests.Integration` (inclui os 8 novos testes de `ConversationHistoryReader`, mais a migração dos dois arquivos existentes para um host Wolverine real — outbox ativo pela primeira vez) | 20 aprovados |
| `IHostPro.ArchitectureTests` (dois novos arquivos: `AIAgentFoundationArchitectureTests`, `AIAgentMessageExecutionScopeArchitectureTests`; dois testes CP1-only retirados por obsolescência legítima) | 278 aprovados |
| `ConversationMessageReceivedWorkflowRoundTripTests` (E2E real — Postgres/RabbitMQ/Worker/Api reais) — principal, duplicidade, multi-mensagem, histórico sensível, falha controlada | 5 aprovados |
| `IHostPro.Api.Tests.Integration` (suíte completa) | 64 aprovados |
| MigrationRunner Run #1/#2 (dev real) | ver §6.10 |

### 6.10 Regressão completa e evidência final

| Suíte | Resultado |
|---|---|
| MigrationRunner Run #1 (Postgres/RabbitMQ de desenvolvimento reais) | Exit code 0 — `AIAgentDbContext` migrado (descoberta automática via `IModuleDbContext`); outbox `ai_agent_messaging` provisionado; nova topologia RabbitMQ provisionada (`aiagent.conversation-message-trigger` declarada e vinculada a `communication-events`/`conversation_message_received`); `communication_messaging` (outbox de Communication, ativado neste checkpoint) provisionado com sucesso |
| MigrationRunner Run #2 (mesmo banco, imediatamente em seguida) | Exit code 0 — zero drift |
| `IHostPro.Api.Tests.Integration` (suíte completa, uma única execução — inclui os 5 novos testes E2E deste checkpoint, e reconfirma sem regressão as 59 suítes/cenários pré-existentes da Fase 11 CP1 e de fases anteriores) | 64 aprovados |
| Build Release (solução completa) | 0 erro |
| Build Debug (solução completa) | 0 erro |

**Nota de transparência sobre a execução da suíte completa**: a primeira tentativa de rodar a suíte completa colidiu com um erro operacional desta sessão (duplo backgrounding acidental de um comando, deixando processos `dotnet` e containers Testcontainers órfãos concorrendo pela porta fixa 5672 do RabbitMQ) — não uma regressão de código. Processos órfãos e containers de teste foram limpos; a suíte foi reexecutada uma única vez, de forma limpa e rastreada.

`Cp2CommitCount`: registrado no relatório final da conversa de homologação.
