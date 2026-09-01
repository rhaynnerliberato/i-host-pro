# Fase 11 — Agente de IA e Experiência Conversacional — Validação e Homologação

Versão: 1.4
Status: Em andamento — Checkpoint 5 concluído

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
CP2 (concluído) — AI Agent Foundation
CP3 (concluído) — Read Tools & Context Builder
CP4 (concluído) — Write Tools & Response Delivery
CP5 (concluído, este documento) — Policies, Workflow & Conversational Orchestration
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

## 7. Checkpoint 3 — Read Tools & Context Builder

**Status:** Concluído e homologado. `ReadToolsImplemented=true`. `BusinessWriteToolsImplemented=false`. `RagMode=StructuredRetrieval`. `VectorDatabase=false`. `Embeddings=false`. `PixPaymentQueryCapability=IMPLEMENTED_IN_PHASE11_CP3`. `AnthropicIntegrated=false`. `ExternalLLMNetworkCalls=0`.

**Objetivo**: construir as 8 Read Tools aprovadas (Documento 16, CP0), cada uma um adapter fino que invoca a Query de Application já existente do Bounded Context correspondente (Exceção 3, nunca uma nova exceção síncrona), acopladas ao loop de tool-calling do `FakeModelProvider`; persistir a auditoria de cada execução (`AgentToolExecution`); reabrir e implementar `PixPaymentQueryCapability`, deixada `DEFERRED_NO_CURRENT_MVP_USE_CASE` pela Fase 10 (Documento 13 §9) — reabertura como requisito novo da Fase 11, nunca uma correção retroativa. Nenhuma Tool de escrita, nenhum RAG semântico sobre texto não-estruturado, nenhuma integração real com Anthropic.

### 7.1 Governança prévia — ponto de parada resolvido antes de continuar a implementação

A primeira tentativa de promover os Query Mediators dos 5 Bounded Contexts consumidos (Reservations/PropertyManagement/Housekeeping/Configuration/Payments) para seus próprios módulos DI compartilhados (`Add<Context>Module`, decisão "Opção A" abaixo) quebrou a inicialização real do `IHostPro.Worker`: `Host.CreateApplicationBuilder`'s `ValidateOnBuild=true` (padrão) falhou porque `Mediator.SourceGenerator`'s `AddMediator()` é tudo-ou-nada por assembly — registra incondicionalmente TODOS os handlers `IRequestHandler<,>` descobertos na assembly, incluindo handlers de Command de escrita cujas dependências (readers/repositórios de escrita) são deliberadamente ausentes da composição do Worker.

Resolvido pelo usuário como "Opção 3": manter `ValidateOnBuild=true` no host geral do Worker, investigar o mecanismo real de registro do Mediator antes de qualquer solução, nunca registrar dependências de escrita apenas para satisfazer a validação, nunca desabilitar `ValidateOnBuild` globalmente. Investigação do código gerado real (`Mediator.g.cs`, via `-p:EmitCompilerGeneratedFiles=true`) confirmou: `Mediator.Mediator` (a classe concreta gerada por assembly) e seu `RequestHandlerWrapper<,>` nunca falham `ValidateOnBuild` (resolvem o handler concreto preguiçosamente, em tempo de chamada, nunca no construtor) — apenas os descritores do handler concreto em si (`ImplementationType` = a classe do handler, com dependências reais) podem falhar.

Solução implementada: `MediatorHandlerAllowlistExtensions.KeepOnlyMediatorHandlers` (novo, `IHostPro.BuildingBlocks.Infrastructure.Messaging`) — um filtro de DI baseado em reflexão que remove, de uma assembly-alvo específica, todo registro de handler `IRequestHandler<,>` que não esteja na allowlist explícita, deixando `Mediator.Mediator`/`RequestHandlerWrapper<,>` intactos. Chamado exclusivamente pelo `IHostPro.Worker/Program.cs`, uma vez por Bounded Context promovido, imediatamente após cada `AddXModule(...)` — nunca de dentro do próprio módulo compartilhado (uma primeira tentativa de colocar a chamada dentro do módulo quebrou silenciosamente os endpoints HTTP de escrita reais do `IHostPro.Api`, já que Api também consome `AddXModule()`). Um bug real de "a última chamada vence" (escopo de remoção não limitado à assembly-alvo, cada chamada sequencial apagando os handlers já aprovados por chamadas anteriores) foi encontrado e corrigido durante a implementação — coberto por teste de arquitetura dedicado (`Worker_Composition_Resolves_The_Five_Query_Dispatchers_But_Registers_No_Write_Command_Surface`) que prova que os 7 handlers de leitura aprovados sobrevivem simultaneamente ao final das 5 chamadas.

Prova exigida pelo mandato — confirmada: `ValidateOnBuild=true` mantido; os 5 dispatchers de leitura resolvem; nenhum handler de Command de escrita, validador ou repositório de escrita é resolvível na composição do Worker (`Worker_Composition_Never_Registers_A_Write_Command_Handler_Class`). Nenhuma nova exceção síncrona foi criada — a solução opera inteiramente dentro da Exceção 3 já aprovada.

### 7.2 Decisão "Opção A" — Query Mediator promovido ao módulo compartilhado, Commands permanecem Api-only

Cada um dos 5 Bounded Contexts consumidos por uma Tool teve sua Query Mediator (`AddXApplicationMediator()`) promovida de `AddXCommandDispatch()` (Api-only) para `AddXModule()` (compartilhado entre Api e Worker) — Commands de escrita, seus validadores e pipeline behaviors permanecem exclusivamente em `AddXCommandDispatch()`, nunca alcançando o Worker. Payments (sem projeto Api, sem `CommandDispatch` próprio) ganhou sua primeira superfície de Mediator/Query já diretamente em `AddPaymentsModule`. O Worker consome as Queries in-process através do próprio `I<Context>RequestDispatcher` de cada Bounded Context (nunca a interface compartilhada `Mediator.ISender`/`IMediator`/`IPublisher`, que se torna genuinely ambígua no momento em que dois ou mais `AddMediator()` de contexts diferentes são compostos no mesmo `IServiceCollection` — a primeira chamada ganha a corrida `TryAdd`).

Essa ambiguidade causou uma regressão real, pré-existente, descoberta durante a regressão completa: `ReservationCommandHandlerTests.cs` (Reservations, Integration) compunha `AddPropertyManagementModule` + `AddReservationsModule`/`AddReservationsCommandDispatch` no mesmo host de teste e despachava via `ISender` bruto — com a promoção do Mediator de PropertyManagement ao módulo compartilhado, `PropertyManagement`'s `AddMediator()` (que roda primeiro na ordem de composição) passou a vencer a corrida `TryAdd` para `IMediator`/`ISender`/`IPublisher`, quebrando as 105 chamadas do arquivo com `Mediator.MissingMessageHandlerException`. Corrigido trocando o despacho para `IReservationsRequestDispatcher` (o padrão que já existe exatamente para este propósito); nenhum outro arquivo de teste na base estava em risco (busca completa por `GetRequiredService<ISender>()`/`IMediator`/`IPublisher` confirmou apenas este único ponto afetado).

### 7.3 `AgentToolExecution` (novo agregado, schema `ai_agent`)

`AgentToolExecution` — `Id, TenantId, AgentInteractionId, ToolName, StartedAtUtc, CompletedAtUtc?, Outcome (InProgress/Success/Failure), DurationMs?, FailureCode?`. Referenciado por `AgentInteraction` através de uma foreign key real de banco de dados (`fk_agent_tool_executions_agent_interactions`, `ON DELETE RESTRICT`) — diferente do precedente opaco-por-id de `AgentInteraction → AgentSession`, autorizado explicitamente pelo mandato deste checkpoint por ambas as tabelas viverem no mesmo schema/Bounded Context. Migração `AddAgentToolExecution` segue o template padrão de RLS/grants (`ENABLE`+`FORCE ROW LEVEL SECURITY`, policy `tenant_isolation` fail-closed, grants `SELECT/INSERT/UPDATE` sem `DELETE`) — verificado diretamente contra o Postgres real de desenvolvimento. Deliberadamente nunca persiste o texto bruto de entrada/saída da Tool, PII do hóspede, credencial/secret-reference, payload de QR/pagamento, ou o prompt completo do modelo — apenas metadados de auditoria (qual Tool rodou, quando, quanto tempo levou, como terminou). `FailureCode` é sempre um código curto e sanitizado (nome do tipo da exceção, ou o `Error.Code` de negócio do `Result<T>` retornado pela Query) — nunca uma mensagem de exceção bruta ou stack trace.

### 7.4 As 8 Read Tools aprovadas

| Tool | Bounded Context consumido | Query reutilizada | Exclusões deliberadas |
|---|---|---|---|
| `GetReservationSummary` | Reservations | `GetReservationDetailQuery` | `GuestName`/`GuestPhone`/`GuestCount`, timestamps de auditoria — apenas Status/CheckInAt/CheckOutAt/PropertyId |
| `GetSchedule` | Reservations | `ListScheduleQuery` | Argumento opcional `days` (padrão 7, min 1, máx 30) — sempre a propriedade da própria reserva, nunca multi-propriedade |
| `GetAvailability` | Reservations | `ListScheduleQuery` (mesma janela) | Apenas fato de calendário (conflito/livre) — nunca conclusão de elegibilidade de early check-in/late checkout |
| `GetPropertyInformation` | PropertyManagement | `GetPropertyDetailQuery` | Nada de `PropertyAccessConfiguration`, nenhum detalhe administrativo de Condomínio/Portaria — apenas Name/EffectiveAddress/Capacity |
| `GetAccessInstructions` | PropertyManagement | `GetPropertyAccessConfigurationQuery` | Retorna exclusivamente `AccessInstructions` — nunca `AccessCredentialSecretReference`, nunca resolve o valor real da credencial; Wi-Fi/estacionamento/regras permanecem DEFERRED (texto livre do administrador, verbatim) |
| `GetCleaningStatus` | Housekeeping | `GetCleaningStatusByReservationQuery` (nova) | Apenas fato real persistido — nunca ETA/conclusão inventada |
| `GetPaymentStatus` | Payments | `GetPaymentStatusByReservationQuery` (nova, primeira superfície Mediator de Payments) | `QrCodePayload`/`ProviderChargeId`/`IdempotencyKey`/dados do pagador/detalhe de falha do provedor — apenas Status/Amount/CurrencyCode/ExpiresAtUtc |
| `GetRelevantPolicies` | Configuration | `GetEffectivePolicyQuery` | Argumento opcional `policyCode` (allowlist `EARLY_CHECKIN`/`LATE_CHECKOUT`); `EffectivePolicyResult.Value` sempre convertido ao seu tipo concreto tipado antes do resumo — nunca repassado como `object?` bruto; apenas fatos, nunca lógica de decisão/elegibilidade (que permanece em Guest Operations) |

`AgentToolNames` fixa o conjunto fechado das 8 constantes — adicionar uma nona Tool (ou uma Tool de escrita) exige um novo mandato, provado por teste de arquitetura dedicado (`Exactly_The_Eight_Approved_Read_Tools_Exist_No_More_No_Less`).

### 7.5 Regra de desempate do pagamento — `GetPaymentStatus`

`IPixChargeReader.GetStatusByReservationIdAsync` resolve sempre a cobrança PIX mais recente por `CreatedAtUtc DESC, Id DESC` (tie-break determinístico) — nunca por status (um `Confirmed` mais antigo nunca vence sobre um `Failed`/`Pending` mais recente). Provado contra Postgres real por `GetPaymentStatusByReservationReaderTests` e reconfirmado pelo cenário E2E `Payment_E2E_picks_the_most_recent_PixCharge_by_CreatedAtUtc_never_by_status`. `GetCleaningStatusByReservationQuery`/`ICleaningReader.GetStatusByReservationIdAsync` (Housekeeping) segue o mesmo padrão de desempate.

### 7.6 Loop de tool-calling — `ConversationMessageReceivedProcessor`

Estendido do fluxo linear do CP2 para: `Call#1 (IModelProvider.GenerateAsync) → se ModelResult.ToolCallRequest → AgentInteraction persistida (InProgress) → Tool executada no máximo uma vez → AgentToolExecution persistida (Success/Failure) → se sucesso, Call#2 com o resultado sanitizado da Tool anexado como mensagem de papel Tool → AgentInteraction completada`. Sem multi-hop — o modelo nunca tem uma segunda chance de pedir outra Tool na mesma interação (decisão de escopo deste checkpoint). Uma falha de Tool (falha de negócio, nome de Tool desconhecido, ou exceção inesperada) falha a interação inteira exatamente como uma `ModelProviderException` — a sessão permanece intocada, nenhuma segunda chamada ao modelo ocorre. Exceções de Tool são logadas para diagnóstico operacional (nunca persistidas) — `AgentToolExecution.FailureCode` armazena apenas o nome do tipo da exceção.

`ModelRequest` ganhou `AvailableTools` (opcional, lista de `AgentToolDescriptor`); `ModelMessageRole` ganhou o caso `Tool` (efêmero, nunca persistido); `ModelResult` ganhou `ToolCallRequest` (`ModelToolCallRequest(ToolName, Arguments)`). `FakeModelProvider` ganhou o marcador determinístico `ToolCallTriggerPrefix` (`"[FAKE_MODEL_TOOL_CALL:"` + nome da Tool + `"]"`) — dispara exatamente uma rodada de tool-calling por mensagem, nunca re-dispara quando a última mensagem já é de papel `Tool`.

### 7.7 Achados corrigidos durante a implementação

- **Crash real de inicialização do Worker** (`ValidateOnBuild=true` vs. auto-descoberta tudo-ou-nada do Mediator) — coberto em detalhe no §7.1.
- **Primeira tentativa de correção quebrou o caminho de escrita real do Api** — `KeepOnlyMediatorHandlers` chamado de dentro do módulo compartilhado stripava também os handlers de Command de escrita que o Api precisa; revertido, chamada movida exclusivamente para o Worker.
- **Bug "última chamada vence" no próprio filtro** — escopo de remoção não limitado à assembly-alvo; corrigido e coberto por teste de arquitetura permanente.
- **Regressão real pré-existente em `ReservationCommandHandlerTests.cs`** (ambiguidade de `ISender`/`IMediator` entre contexts compostos juntos) — coberta em detalhe no §7.2.
- **Corrida de polling em E2E próprio** — `WaitForInteractionAsync` inicialmente aceitava uma `AgentInteraction` ainda `InProgress` (inserida propositalmente antes da Tool rodar, para a FK ter um pai válido); corrigido exigindo `Outcome != InProgress`.
- **Mismatch de tenant em E2E próprio** — a nova classe de teste inicialmente gerava seu próprio `GlobalTenantId`, mas a Fixture reutilizada do CP2 referencia o campo estático da classe original via acesso de classe aninhada; corrigido reutilizando o campo original (`private` → `internal`) em vez de gerar um novo.

Nenhum desses achados exigiu mudança de escopo, nova exceção síncrona, ou decisão de produto — todos corrigidos e a implementação continuada, conforme autorização explícita do mandato.

### 7.8 Testes — contagens exatas

| Suíte | Resultado |
|---|---|
| `IHostPro.Contexts.AIAgent.Tests.Unit` | 70 aprovados |
| `IHostPro.Contexts.AIAgent.Tests.Integration` | 15 aprovados |
| `IHostPro.Contexts.Reservations.Tests.Unit` | 90 aprovados (sem regressão) |
| `IHostPro.Contexts.Reservations.Tests.Integration` (inclui a correção de `ReservationCommandHandlerTests.cs`, §7.2) | 105 aprovados |
| `IHostPro.Contexts.PropertyManagement.Tests.Unit` | 202 aprovados (sem regressão) |
| `IHostPro.Contexts.PropertyManagement.Tests.Integration` | 207 aprovados (sem regressão) |
| `IHostPro.Contexts.Housekeeping.Tests.Unit` | 120 aprovados (sem regressão) |
| `IHostPro.Contexts.Housekeeping.Tests.Integration` (inclui os 4 novos testes de `GetCleaningStatusByReservationReaderTests`) | 101 aprovados |
| `IHostPro.Contexts.Configuration.Tests.Unit` | 93 aprovados (sem regressão) |
| `IHostPro.Contexts.Configuration.Tests.Integration` | 80 aprovados (sem regressão) |
| `IHostPro.Contexts.Payments.Tests.Unit` | 39 aprovados (sem regressão) |
| `IHostPro.Contexts.Payments.Tests.Integration` (inclui os 4 novos testes de `GetPaymentStatusByReservationReaderTests`, primeira superfície de Mediator/Query de Payments) | 15 aprovados |
| `IHostPro.Contexts.Communication.Tests.Unit` | 101 aprovados (sem regressão) |
| `IHostPro.Contexts.Communication.Tests.Integration` | 20 aprovados (sem regressão) |
| `IHostPro.ArchitectureTests` (novo arquivo `AIAgentReadToolsArchitectureTests`; 284 no total, de 278) | 284 aprovados |
| `AIAgentReadToolsWorkflowRoundTripTests` (E2E real — Postgres/RabbitMQ/Worker/Api reais) — principal (GetReservationSummary), pagamento (desempate real), faxina (fato real persistido), propriedade (informação + instruções de acesso, duas interações), disponibilidade (livre + conflito real), política (hierarquia real PROPERTY→TENANT→GLOBAL via projeção tipada), idempotência (mesmo MessageId duas vezes) | 7 aprovados (parte da suíte completa abaixo) |
| `IHostPro.Api.Tests.Integration` (suíte completa) | 71 aprovados |
| MigrationRunner Run #1/#2 | ver §7.9 |
| Build Release | ver §7.9 |

### 7.9 Regressão completa e evidência final

| Suíte | Resultado |
|---|---|
| MigrationRunner Run #1 (Postgres/RabbitMQ de desenvolvimento reais) | Exit code 0 — todos os 11 DbContexts migrados, incluindo `AddAgentToolExecution`; RLS `ENABLE`+`FORCE` e policy `tenant_isolation` confirmados por leitura direta de schema (`\d ai_agent.agent_tool_executions`); nova topologia RabbitMQ provisionada sem alteração (nenhuma nova exchange/binding própria da AI Agent neste checkpoint — as Tools consomem Queries in-process, nunca mensageria) |
| MigrationRunner Run #2 (mesmo banco, imediatamente em seguida) | Exit code 0 — zero drift, zero linha nova em qualquer backfill |
| Verificação de schema pós-Run (read-only, SQL direto) | `ai_agent.agent_tool_executions`: FK real `fk_agent_tool_executions_agent_interactions` (`ON DELETE RESTRICT`) para `ai_agent.agent_interactions(id)`; índices `IX_agent_tool_executions_agent_interaction_id` e `ix_agent_tool_executions_tenant_id_agent_interaction_id` presentes; RLS `ENABLE`+`FORCE` intactos, policy `tenant_isolation` fail-closed |
| `IHostPro.Api.Tests.Integration` (suíte completa, execução limpa e isolada — inclui os 7 novos testes E2E deste checkpoint, e reconfirma sem regressão as suítes E2E pré-existentes) | 71 aprovados, 0 com falha (22 min 38 s) |
| Build Release (solução completa) | 0 erro (20 avisos `NU1903` pré-existentes, SSH.NET, não relacionados a este checkpoint) |
| `git diff --check` | Sem erros (apenas avisos benignos de normalização LF→CRLF) |
| Revisão manual completa do diff | Nenhum vazamento de secret/QR/`AccessCredentialSecretReference`/`GuestPhone`/payload bruto do provedor/tipo específico de Anthropic/Claude; nenhuma persistência de resultado bruto de Tool — confirmado por leitura direta de cada Tool, do `AgentToolExecution`, e do `ConversationMessageReceivedProcessor` |

**Nota de transparência sobre a suíte `IHostPro.Api.Tests.Integration`**: a primeira execução completa (71 testes, ~24 min) apresentou uma única falha isolada em `Property_E2E_GetPropertyInformation_and_GetAccessInstructions_each_succeed_in_their_own_interaction`. Investigada antes de qualquer conclusão: o mesmo teste, executado isoladamente (mesma suíte, mesmo commit, sem nenhuma alteração de código), passou em 10s. A suíte completa foi então reexecutada do zero, de forma limpa — resultado final 71 aprovados, 0 com falha, confirmando que a falha original foi um artefato de contenção de recursos (Testcontainers/Docker sob carga prolongada), não uma regressão real deste checkpoint — mesmo padrão já observado e documentado durante a regressão das 7 Bounded Contexts nesta mesma sessão de fechamento.

**Nota de transparência sobre o `MigrationRunner`**: a primeira tentativa de executá-lo nesta sessão de fechamento falhou por dois motivos operacionais — (1) `dotnet run --project` a partir do diretório raiz do repositório não resolveu `appsettings.json` (corrigido executando a partir do próprio diretório do projeto); (2) sem `DOTNET_ENVIRONMENT=Development`, o provisionamento da topologia RabbitMQ usou as credenciais base (`guest`) em vez do override de desenvolvimento (`ihostpro`/`ihostpro_dev`) que o container real exige, falhando com `ACCESS_REFUSED` após 20 tentativas — corrigido definindo a variável de ambiente explicitamente. Nenhum dos dois é uma regressão de código deste checkpoint; ambos são particularidades operacionais do ambiente local, registradas aqui por transparência (mandato: nunca esconder dúvidas/limitações relevantes).

`Cp3CommitCount`: registrado no relatório final da conversa de homologação.

## 8. Checkpoint 4 — Write Tools & Response Delivery

**Status:** Concluído e homologado. `WriteToolsImplemented=true`. `WriteConfirmationImplemented=true`. `BusinessWriteToolsImplemented=true`. `PendingActionModel=AgentPendingAction`. `PendingActionTtl=false`. `MaxPendingActionsPerSession=1`. `ResponseDeliveryImplemented=true`. `AgentResponsePersistence=CommunicationMessage`. `AgentInteractionResponseTextPersistence=false`. `AnthropicIntegrated=false`. `ExternalLLMNetworkCalls=0`.

**Objetivo**: construir as 3 Write Tools de negócio aprovadas (`RequestEarlyCheckIn`, `RequestLateCheckout`, `RequestGuestAccessDelivery`), cada uma um adapter fino que invoca o Application Command já existente do Bounded Context correspondente (Exceção 3, nunca uma nova exceção síncrona); introduzir um modelo de confirmação server-side para ações state-changing/financeiras (`AgentPendingAction`), com a regra explícita de que o modelo de linguagem nunca decide se uma Tool exige confirmação; e implementar a primeira entrega real de resposta ao hóspede (`SendAgentResponseCommand`, o primeiro Application Command síncrono de Communication), fazendo com que toda interação bem-sucedida — leitura ou escrita — finalmente produza uma resposta real. Nenhuma outra Write Tool além das 3 aprovadas, nenhum RAG semântico, nenhuma integração real com Anthropic.

### 8.1 Governança prévia — CP4 Decision/Contract Gate (read-only)

Antes de qualquer código, o usuário exigiu um Decision/Contract Gate exclusivamente de leitura, cobrindo: releitura de Documento 16 §12/15/16/21/22, Documento 17 Workflows 05/06/07/08/11/13/14/16, Documento 06 (máquina de estados e Regras para IA), Documento 13 §30, ADR-009, Architecture Principles (14 exceções nomeadas) e a homologação da Fase 11 CP3; classificação de 11 candidatos a Write Tool; e um relatório de governança de 39 itens. Classificação final, aprovada pelo usuário sem alteração:

| Candidato | Classificação | Motivo |
|---|---|---|
| `RequestEarlyCheckIn` | `REQUIRED_CP4`, `CONFIRMATION_REQUIRED` | Ação state-changing sobre a Reservation, decisão automática de negócio já existente (`RequestEarlyCheckInCommand`) |
| `RequestLateCheckout` | `REQUIRED_CP4`, `CONFIRMATION_REQUIRED` | Idem, pode disparar cobrança PIX (`LateCheckoutPaymentRequired`) |
| `RequestGuestAccessDelivery` | `REQUIRED_CP4`, `EXPLICIT_REQUEST_IS_CONFIRMATION` | O próprio pedido do hóspede já é a confirmação; entrega seletiva de instrução, nunca a credencial real (ADR-028) |
| `RecordGuestCheckedIn` | `NOT_MODEL_TOOL` | Comando HTTP-only, gated por permissão de staff `GUEST_OPERATIONS:MANAGE`, nunca automaticamente disparado |
| `RecordGuestCheckedOut` | `NOT_MODEL_TOOL` | Idem |
| `CancelReservation` | `FORBIDDEN` | Exige `ActorId` de staff real (`CancelReservationCommand(TenantId, ActorId, ReservationId)`), estruturalmente incompatível com um chamador autônomo de IA; ausente das listas autorizadas de Documento 16 §12/§15 |
| `CreatePix`/`GeneratePix` | `ALREADY_INDIRECT` | Único ponto de criação é reativo a evento (`LateCheckoutPaymentRequiredChargeInitializer`); nenhum Command standalone existe; Payments não possui `.Api` |
| `SendAgentResponse` | `REQUIRED_CP4` (não uma Tool de negócio — capability de entrega) | Primeiro Command síncrono de Communication, aprovado explicitamente como mudança de padrão arquitetural intencional |
| `CreateWorkflow` | `NOT_MODEL_TOOL` | Orquestração interna de Workflow, nunca disparada por um agente conversacional |
| `NotifyFrontDesk` | `ALREADY_INDIRECT` | Já disparado por coreografia de evento (Fase 10 CP4), nenhum Command standalone para o modelo invocar |
| `RegisterIncident` | `DEFERRED_TO_CP6` | Nenhum agregado/BC de "incidente reportado pelo hóspede" existe; `CleaningOccurrence` é conceito distinto (self-service do housekeeper sobre a própria Cleaning) |

Nenhuma nova exceção síncrona foi necessária — as 3 Write Tools e `SendAgentResponseCommand` operam inteiramente dentro da Exceção 3 já aprovada, reutilizando `IReservationGuestContactReader`/Exceção 5 (ADR-019) para resolução de destinatário.

### 8.2 As 3 Write Tools aprovadas

| Tool | Bounded Context consumido | Command reutilizado | Confirmação | Campos backend-derived | Campos model-derived |
|---|---|---|---|---|---|
| `RequestEarlyCheckIn` | GuestOperations | `RequestEarlyCheckInCommand` | Obrigatória | `TenantId`, `ReservationId` | `RequestedCheckInAt` |
| `RequestLateCheckout` | GuestOperations | `RequestLateCheckoutCommand` | Obrigatória | `TenantId`, `ReservationId` | `RequestedCheckOutAt` |
| `RequestGuestAccessDelivery` | GuestOperations | `RequestGuestAccessDeliveryCommand` | Nenhuma (o próprio pedido já é a confirmação) | `TenantId`, `ReservationId` | — (zero argumentos) |

`AgentToolNames` (Application) fixa agora um conjunto fechado de 11 constantes (8 Read + 3 Write) — adicionar uma décima segunda Tool exige um novo mandato, provado por teste de arquitetura dedicado (`Exactly_The_Eleven_Approved_Tools_Exist_No_More_No_Less`, sucessor renomeado do teste equivalente do CP3). `EarlyCheckInRequestResult`/`LateCheckoutRequestResult` carregam `Status` (`"approved"`/`"denied"`/`"pending_payment"`) e `DenialReasonCode` — uma negação de negócio é sempre `AgentToolExecutionOutcome.Success` (o Tool executou corretamente e obteve uma decisão real); apenas falha técnica/infraestrutura é `Failure`. Cada Tool executa no máximo um Command por interação — sem cadeias multi-hop de escrita.

### 8.3 `AgentPendingAction` (novo agregado, schema `ai_agent`) — modelo de confirmação

`AgentPendingAction` — `Id, TenantId, AgentSessionId, ProposedByInteractionId, ToolName, SanitizedArguments (texto opaco), Status, CreatedAtUtc, ConfirmedAtUtc?, ExecutedAtUtc?, CancelledAtUtc?`. Estados: `Proposed → Confirmed → Executed`, ou `Proposed/Confirmed → Cancelled` — deliberadamente **sem** estado `Expired` (nenhum TTL documentado; `PendingActionTtl=false` é uma decisão oficial do mandato, não uma lacuna). No máximo 1 ação ativa (`Proposed`/`Confirmed`) por `AgentSessionId`, garantido por índice único parcial (`ix_agent_pending_actions_active_per_session`, defesa em profundidade atrás da checagem em nível de Application). Uma segunda proposta de Write Tool enquanto uma já está ativa é **bloqueada** — nunca substituída, cancelada ou auto-executada; o agente responde explicando a situação ao hóspede. Cancelar uma ação pendente nunca invoca nenhum Command de negócio — apenas marca a proposta como `Cancelled`. `SanitizedArguments` nunca armazena o prompt bruto, `GuestPhone`, credencial/secret-reference, payload de QR/provider, ou o agregado de domínio — apenas os argumentos já validados/estreitados pela própria Tool. Referenciado por `AgentInteraction` através de uma foreign key real (`fk_agent_pending_actions_agent_interactions`, `ON DELETE RESTRICT`), mesmo precedente de `AgentToolExecution` (CP3, §7.3).

**Separação proposta/execução — `IConfirmableAgentTool`**: uma nova interface (`IConfirmableAgentTool : IAgentTool`) acrescenta `BuildSanitizedArguments(arguments) → AgentPendingActionProposalResult`, chamada no turno da proposta (valida/estreita os argumentos do modelo, sem executar nada). O mesmo `ExecuteAsync` herdado de `IAgentTool` é reutilizado tanto para Tools sem confirmação (execução imediata) quanto para a execução pós-confirmação de uma Tool confirmável — os argumentos são reconstituídos a partir de `AgentPendingAction.SanitizedArguments` via round-trip JSON.

### 8.4 `IAgentToolConfirmationPolicy` — o modelo nunca decide se uma Tool exige confirmação

Por exigência explícita do mandato ("NÃO adicionar `RequiresConfirmation` a `ModelToolCallRequest`. O MODEL NÃO decide se uma Tool exige confirmação"), a política é uma allowlist fixa server-side (`AgentToolConfirmationPolicy`, `HashSet<string>` estático): `RequestEarlyCheckIn`/`RequestLateCheckout` → exige confirmação; qualquer nome fora do conjunto (incluindo `RequestGuestAccessDelivery`) → não exige. `ModelResult` ganhou `ConfirmationIntent` (`bool?`) — usado apenas para classificar a intenção do turno seguinte (confirmar/cancelar/nenhum) via os novos marcadores determinísticos do `FakeModelProvider` (`[FAKE_MODEL_CONFIRM]`/`[FAKE_MODEL_CANCEL]`), nunca para decidir se a Tool original exigia confirmação.

### 8.5 `SendAgentResponseCommand` — primeiro Application Command síncrono de Communication

Aprovado explicitamente como mudança de padrão arquitetural intencional (mandato item 26: "uma mudança intencional e aprovada"). Contrato: `TenantId, ConversationId, ReservationId, AgentInteractionId, Content` — nunca `GuestPhone`/telefone de destino/id de provider/override de canal/token de acesso/secret. `Channel` é sempre `Conversation.Channel` (nunca escolhido pelo modelo). Recipiente resolvido via `IReservationGuestContactReader` (ADR-019/Exceção 5, já aprovada — nenhuma nova exceção síncrona). `TemplateKey = "AI_AGENT_RESPONSE"`. Idempotência determinística por `TenantId`/`AgentInteractionId`/`AI_AGENT_RESPONSE`/`Channel` — uma redelivery com o mesmo `AgentInteractionId` retorna o mesmo `MessageId`, nunca cria uma segunda linha. `GuestPhone` é usado exclusivamente em memória (checagem de nulidade, mascaramento antes de persistir em `Message.DestinationMasked`, e repasse ao `IOutboundMessageConnector` para o envio real) — nunca logado, nunca persistido em claro; mesmo padrão já aprovado de `GuestAccessDeliveryProcessor`/`ReservationCreatedCommunicationProcessor`/`PixChargeCreatedDeliveryProcessor` (Fase 9/10). Falha de entrega nunca marca a mensagem como enviada artificialmente; nenhum handoff automático ainda existe.

**Wiring**: `ICommunicationRequestDispatcher`/`CommunicationRequestDispatcher`/`CommunicationApplicationMediatorExtensions` (novo, espelha exatamente o precedente de `PaymentsApplicationMediatorExtensions` do CP3, incluindo `ServiceLifetime.Scoped`). `AddCommunicationModule` agora chama `AddCommunicationApplicationMediator()` incondicionalmente; `IHostPro.Worker/Program.cs` chama `KeepOnlyMediatorHandlers(typeof(SendAgentResponseCommandHandler))` imediatamente após — a própria dependência do handler (`IOutboundMessageConnector`) só resolve efetivamente onde `AddCommunicationReservationConsumer` (gate Development-only) também foi chamado, preservando o mesmo boundary fail-safe de toda capability de envio outbound de Communication.

**`AgentInteraction.OutboundMessageId`** (novo, nullable) — referencia o `Message` real criado por `SendAgentResponseCommand`; `ResponseText` nunca é persistido em `AgentInteraction` — `Communication.Message` permanece a única fonte de verdade do conteúdo enviado (decisão já estabelecida no CP2, §6.3, agora com uma referência real por id opaco).

### 8.6 Loop estendido do `ConversationMessageReceivedProcessor`

O construtor ganhou `IAgentPendingActionRepository`, `IAgentToolConfirmationPolicy` e `IAgentResponseDeliveryService`. Fluxo por tipo de turno:

- **Tool sem confirmação** (`RequestGuestAccessDelivery`, ou qualquer Read Tool): comportamento do CP3 preservado — executa imediatamente, sem `AgentPendingAction`.
- **Tool com confirmação** (`RequestEarlyCheckIn`/`RequestLateCheckout`): `BuildSanitizedArguments` é chamado; se aceito, cria `AgentPendingAction(Proposed)` e responde ao hóspede pedindo confirmação — o Command de negócio real **nunca** é chamado neste turno. Rejeição da proposta pela própria Tool falha a interação (mesma semântica de falha de Tool do CP3).
- **Turno de confirmação** (`ConfirmationIntent=true`): busca a `AgentPendingAction` ativa da sessão; se existir, marca `Confirmed`, reconstitui os argumentos e chama `ExecuteAsync` do Command real; sucesso marca `Executed`; falha técnica deixa a ação em `Confirmed` (nunca `Executed`) e falha a interação. Se não houver ação ativa, a interação é bem-sucedida com uma resposta genérica, sem chamar nenhuma Tool.
- **Turno de cancelamento** (`ConfirmationIntent=false`): marca a ação ativa (se houver) como `Cancelled` — nenhum Command de negócio é chamado.
- **Segunda proposta com uma ação já ativa**: rejeitada sem criar uma segunda linha em `agent_pending_actions`.

Toda interação bem-sucedida (leitura ou escrita) agora chama `IAgentResponseDeliveryService.SendAsync` ao final, persistindo `AgentInteraction.OutboundMessageId` quando a entrega é bem-sucedida. Falha na entrega **não** falha a interação (o Tool/Command de negócio já foi executado com sucesso) — `OutboundMessageId` permanece `null`.

**Camada correta para a chamada cross-context**: `IAgentResponseDeliveryService` (Application, abstrato) + `AgentResponseDeliveryService` (Infrastructure, adapter concreto que chama `ICommunicationRequestDispatcher`/`SendAgentResponseCommand`) — mesmo padrão já estabelecido pelo próprio `IAgentTool` (Exceção 3): apenas AIAgent.Infrastructure pode referenciar a Application layer de outro contexto; AIAgent.Application permanece livre de qualquer acoplamento a Communication.

### 8.7 Decisão "Opção A" estendida a uma superfície de escrita — GuestOperations

Diferente do CP3 (onde apenas Query Mediators foram promovidos), `AddGuestOperationsModule` agora registra também `AddGuestOperationsApplicationMediator()` e todos os 4 pares repositório/reader (antes exclusivos do já **removido** `GuestOperationsCommandDispatchExtensions.AddGuestOperationsCommandDispatch`) — porque todos os Commands de GuestOperations, incluindo os dois `NOT_MODEL_TOOL` (`RecordGuestCheckedIn`/`RecordGuestCheckedOut`), compartilham o mesmo grafo de dependências; nada permaneceu exclusivo do Api. A segurança é garantida exclusivamente por `KeepOnlyMediatorHandlers(typeof(RequestEarlyCheckInCommandHandler), typeof(RequestLateCheckoutCommandHandler), typeof(RequestGuestAccessDeliveryCommandHandler))`, chamada no `IHostPro.Worker/Program.cs` imediatamente após `AddGuestOperationsModule(...)` — os handlers de `RecordGuestCheckedIn`/`RecordGuestCheckedOut` permanecem registrados no módulo compartilhado (suas dependências continuam necessárias), mas são removidos da composição real do Worker.

### 8.8 Achados corrigidos durante a implementação

- **Teste de arquitetura redundante/circular** (achado antes do commit): a primeira versão de `Exactly_The_Three_Approved_Write_Tools_Exist_No_More_No_Less` comparava nomes contra o mesmo array que estava validando — sem valor real. Corrigido simplificando para uma checagem direta de existência + nome do descriptor.
- **Colisão de nome de variável (CS0136)** em `ProcessConfirmationReplyAsync` — `content` declarado duas vezes em escopos sobrepostos. Corrigido renomeando a primeira ocorrência para `noPendingActionContent`.
- **Violação de camadas capturada antes de compilar errado**: a primeira tentativa teria feito `ConversationMessageReceivedProcessor` (AIAgent.Application) referenciar `SendAgentResponseCommand`/`ICommunicationRequestDispatcher` de Communication.Application diretamente — viola a regra já estabelecida de que apenas AIAgent.Infrastructure pode acoplar-se à Application layer de outro contexto. Corrigido introduzindo `IAgentResponseDeliveryService` como o boundary correto (§8.6).
- **Bug real de canonicalização silenciosa `jsonb`**: `AgentPendingAction.SanitizedArguments` mapeado como `jsonb` reescreve/normaliza espaços em branco do JSON na escrita no Postgres, quebrando o round-trip exato de string que um teste de Integration exigia. Diagnosticado por um diff de string preciso (divergência no caractere 22, espaço ausente após `:`). Corrigido remapeando a coluna para `text` (armazenamento opaco controlado pela própria aplicação, nunca consultado via operadores JSON do Postgres) e regenerando a migration (`20260831192224_AddAgentPendingActionAndOutboundMessageId`), com o bloco de RLS/grants reaplicado manualmente (a regeneração automática não o inclui).
- **DLL pré-compilada obsoleta do MigrationRunner**: a fixture de E2E executa um `IHostPro.MigrationRunner.dll` de Release pré-compilado, não recompilado automaticamente pelas dependências de projeto do teste. Após adicionar a migration do CP4, a primeira execução E2E falhou com `column a.outbound_message_id does not exist` (Postgres `42703`). Corrigido recompilando explicitamente o MigrationRunner em Release antes de cada execução E2E subsequente.
- **Contaminação cross-test em query de pending action**: os helpers de teste E2E inicialmente buscavam "a pending action ativa mais recente do tenant inteiro" — como os 5 testes da nova classe compartilham o mesmo `GlobalTenantId`, a pending action de um teste vazava para a asserção de outro (confirmado: um teste sem nenhuma pending action própria encontrava `Count == 1`, pertencente a um teste irmão). Corrigido reescopando os três helpers para filtrar por `ReservationId` específico, via join em `AgentSessions`.
- **Bug real, não-determinístico, pré-existente (CP2) — desempate de ordenação em `IConversationHistoryReader`**: `.OrderBy(CreatedAtUtc).ThenBy(Id)` pode, raramente, ordenar incorretamente duas mensagens criadas microssegundos uma da outra (a resposta de proposta do próprio agente, seguida imediatamente da confirmação real do hóspede) quando ambas colidem no mesmo `timestamptz` truncado do Postgres — o desempate por `Id` (GUID aleatório) não garante que a mensagem inserida por último ordene por último. Isso fazia o `FakeModelProvider` (que sempre inspeciona apenas a última mensagem) ocasionalmente deixar de detectar o marcador de confirmação do hóspede, causando uma resposta genérica e deixando a `AgentPendingAction` presa em `Proposed`. Descoberto através de um padrão de falha não-reprodutível e variável ao longo de três execuções completas consecutivas da nova suíte E2E — assinatura clássica de uma race condition real, não contenção de recursos. Corrigido **sem alterar o schema compartilhado de `Message`** (correção de baixo risco, confinada à própria camada do AI Agent): `IAgentContextBuilder.BuildAsync` ganhou um novo parâmetro `triggeringInboundMessageId`; a implementação particiona de forma estável o histórico já ordenado, garantindo que a mensagem que disparou o processamento seja sempre a última, independentemente do desempate ambíguo do reader. Verificado por duas execuções completas e consecutivas, limpas, 5/5 da suíte E2E (anteriormente instável em 2/5, 3/5, 3/5 em três tentativas).
- **Asserção obsoleta de um teste do CP3 (mudança de comportamento intencional, não regressão)**: `AIAgentReadToolsWorkflowRoundTripTests.Principal_flow_GetReservationSummary_...` esperava zero mensagens outbound ("o AI Agent nunca envia nada neste checkpoint") — factualmente obsoleto no CP4, que entrega uma resposta real para toda interação bem-sucedida, incluindo as somente-leitura (mandato item 33). Corrigido para esperar exatamente 1 mensagem outbound e `AgentInteraction.OutboundMessageId` preenchido, com comentário explicativo. Busca completa confirmou que nenhum outro teste fazia a mesma suposição agora obsoleta.
- **Drift de grant `DELETE` no banco de desenvolvimento real, descoberto na verificação de schema pós-Run desta homologação**: a verificação direta (`\dp`) revelou que `ihostpro_app` possuía `DELETE` em `ai_agent.agent_tool_executions` (CP3) e `ai_agent.agent_pending_actions` (CP4) — apesar de ambas as migrations declararem explicitamente apenas `GRANT SELECT, INSERT, UPDATE` (conferido lendo o SQL de cada migration; nenhuma delas nunca concedeu `DELETE`). `agent_sessions`/`agent_interactions` (CP2, mais antigas) não apresentavam esse desvio, e nenhum `ALTER DEFAULT PRIVILEGES` no schema `ai_agent` o explica — a origem mais provável é um comando SQL manual de diagnóstico executado diretamente contra o banco de desenvolvimento em algum momento anterior desta mesma sessão de trabalho, nunca commitado como código. Corrigido via `REVOKE DELETE ON ai_agent.agent_tool_executions/agent_pending_actions FROM ihostpro_app;`, reconfirmado por nova leitura de schema: as 4 tabelas do AI Agent mostram uniformemente `INSERT/SELECT/UPDATE`, sem `DELETE`, alinhado ao que cada migration sempre declarou. Nenhuma linha de código/migration precisou de correção — o problema era exclusivamente um estado de banco de desenvolvimento fora de banda, nunca publicado, nunca chegando a produção.

Nenhum desses achados exigiu mudança de escopo, nova exceção síncrona, ou decisão de produto — todos corrigidos e a implementação continuada, conforme autorização explícita do mandato ("problemas técnicos normais: corrigir e continuar").

### 8.9 Testes — contagens exatas

| Suíte | Resultado |
|---|---|
| `IHostPro.Contexts.AIAgent.Tests.Unit` | 110 aprovados |
| `IHostPro.Contexts.AIAgent.Tests.Integration` (inclui os novos testes de `AgentPendingAction`: round-trip, FK, índice único parcial, RLS, e o round-trip de `AgentInteraction.OutboundMessageId`) | 23 aprovados |
| `IHostPro.Contexts.GuestOperations.Tests.Unit` | 71 aprovados (sem regressão) |
| `IHostPro.Contexts.GuestOperations.Tests.Integration` (ajustado para a remoção de `AddGuestOperationsCommandDispatch`, §8.7) | 18 aprovados |
| `IHostPro.Contexts.Communication.Tests.Unit` | 101 aprovados (sem regressão) |
| `IHostPro.Contexts.Communication.Tests.Integration` (inclui os 6 novos testes de `SendAgentResponseCommandHandlerTests`, primeira superfície de Command síncrono de Communication) | 26 aprovados |
| `IHostPro.Contexts.Reservations.Tests.Unit` | 90 aprovados (sem regressão) |
| `IHostPro.Contexts.Reservations.Tests.Integration` | 105 aprovados (sem regressão) |
| `IHostPro.Contexts.PropertyManagement.Tests.Unit` | 202 aprovados (sem regressão) |
| `IHostPro.Contexts.PropertyManagement.Tests.Integration` | 207 aprovados (sem regressão) |
| `IHostPro.Contexts.Housekeeping.Tests.Unit` | 120 aprovados (sem regressão) |
| `IHostPro.Contexts.Housekeeping.Tests.Integration` | 101 aprovados (sem regressão) |
| `IHostPro.Contexts.Configuration.Tests.Unit` | 93 aprovados (sem regressão) |
| `IHostPro.Contexts.Configuration.Tests.Integration` | 80 aprovados (sem regressão) |
| `IHostPro.ArchitectureTests` (novo arquivo `AIAgentWriteToolsArchitectureTests`, 7 testes; `AIAgentReadToolsArchitectureTests` ampliado para 11 Tools aprovadas; total 291, de 284) | 291 aprovados |
| `AIAgentWriteToolsWorkflowRoundTripTests` (E2E real — Postgres/RabbitMQ/Worker/Api reais) — Early Check-In (fluxo de confirmação em dois turnos), negação de negócio (sucesso técnico), Access Delivery (execução imediata sem pending action), resposta real para interação somente-leitura, duplicidade de confirmação | 5 aprovados (parte da suíte completa abaixo) |
| `IHostPro.Api.Tests.Integration` (suíte completa) | 76 aprovados |
| MigrationRunner Run #1/#2 | ver §8.10 |
| Build Release | ver §8.10 |

### 8.10 Regressão completa e evidência final

| Suíte | Resultado |
|---|---|
| MigrationRunner Run #1 (Postgres/RabbitMQ de desenvolvimento reais) | Exit code 0 — todos os 11 DbContexts migrados (nenhuma migration pendente nova nesta execução de fechamento — `AgentPendingAction`/`OutboundMessageId` já aplicados anteriormente na sessão); nova topologia RabbitMQ reconfirmada sem alteração |
| MigrationRunner Run #2 (mesmo banco, imediatamente em seguida) | Exit code 0 — zero drift, zero linha nova em qualquer backfill (`Communication conversation backfill: 3/3 tenants checados, 0 inseridos`, e demais backfills igualmente 0/0) |
| Verificação de schema pós-Run (read-only, SQL direto) | `ai_agent.agent_pending_actions`: coluna `sanitized_arguments` confirmada `text` (não `jsonb`); FK real `fk_agent_pending_actions_agent_interactions` (`ON DELETE RESTRICT`) para `ai_agent.agent_interactions(id)`; índice único parcial `ix_agent_pending_actions_active_per_session` filtrado por `status IN ('Proposed','Confirmed')`; RLS `FORCE ROW LEVEL SECURITY` com policy `tenant_isolation` fail-closed; `ai_agent.agent_interactions.outbound_message_id` presente, `uuid` nullable. **Achado e corrigido nesta verificação**: drift de grant `DELETE` em 2 das 4 tabelas do AI Agent — ver §8.8 |
| `IHostPro.Api.Tests.Integration` (suíte completa, execução limpa e isolada, RabbitMQ dev parado durante a execução e reiniciado logo em seguida) | 76 aprovados, 0 com falha (25 min 30 s) |
| Build Release (solução completa) | 0 erro (20 avisos `NU1903` pré-existentes, SSH.NET, não relacionados a este checkpoint) |
| `git diff --check` | Sem erros (apenas avisos benignos de normalização LF→CRLF) |
| Revisão manual completa do diff e dos arquivos novos do CP4 | Nenhum vazamento de secret/QR/`AccessCredentialSecretReference`/`GuestPhone` bruto/payload de provider/tipo específico de Anthropic/Claude; `GuestPhone` em `SendAgentResponseCommandHandler` usado exclusivamente em memória (checagem de nulidade, mascaramento antes de persistir, repasse ao connector), nunca logado ou persistido em claro — mesmo padrão já aprovado de `GuestAccessDeliveryProcessor`/`ReservationCreatedCommunicationProcessor`/`PixChargeCreatedDeliveryProcessor` (Fase 9/10); confirmado por leitura direta de cada Tool, de `AgentPendingAction`, de `SendAgentResponseCommand`/`Result`/`CommandHandler`, e do `ConversationMessageReceivedProcessor` reescrito |

**Nota de transparência sobre `IHostPro.Contexts.Configuration.Tests.Integration`**: durante a execução sequencial da matriz de regressão completa (múltiplas suítes de Integration rodadas uma após a outra contra o mesmo Postgres real de desenvolvimento), `Benchmark_EARLY_CHECKIN_effective_resolution_meets_the_50ms_p95_target_with_a_warm_cache` falhou uma única vez (79/80). Configuration não foi tocado pelo CP4 (nenhum arquivo do contexto aparece no diff). Reexecutado isoladamente (passou em ~1s) e depois a suíte completa novamente do zero (80/80 aprovados) — confirmando contenção de recursos transitória da máquina local sob carga prolongada, mesmo padrão de flakiness de benchmark já observado e documentado no fechamento do CP3 (§7.9), não uma regressão real deste checkpoint.

`Cp4CommitCount`: registrado no relatório final da conversa de homologação.

## 9. Checkpoint 5 — Policies, Workflow & Conversational Orchestration

**Status:** Concluído e homologado. `ConversationalOrchestrationImplemented=true`. `ModelTechnicalRetryCount=1`. `AutomaticWriteToolRetry=false`. `ResponseDeliveryRetryCount=1`. `UnknownToolExecutionBlocked=true`. `NaturalLanguageLocalTimeResolutionBlocked=true`. `TimezoneSource=DEFERRED_TO_CP7`. `SystemPromptSource=DEFERRED_TO_CP7`. `PersonalityToneConfiguration=DEFERRED_TO_CP7`. `ZeroMultipleReservationHandling=DEFERRED_ARCHITECTURAL_GAP`. `HumanHandoffImplemented=false`. `AnthropicIntegrated=false`. `ExternalLLMNetworkCalls=0`.

**Objetivo**: fechar as lacunas de robustez/segurança da orquestração conversacional do CP4 sem introduzir nenhum componente/aggregate/exceção síncrona/Tool novos — um retry único e controlado para falha técnica do model provider (nunca para Write Tool), bloqueio seguro de `ToolName` desconhecido (nunca reflection/dispatch genérico), classificação de intent não suportado e de pedido explícito de humano (via `ModelResult.Intent`, sem nenhuma ação real de handoff — isso permanece CP6), e a correção de um risco de segurança real descoberto na própria auditoria do CP5: `DateTimeOffset.TryParse` aceitava silenciosamente um datetime sem offset explícito, usando o timezone local do servidor como fallback.

### 9.1 Governança prévia — CP5 Decision/Contract Gate (read-only)

Antes de qualquer código, o usuário exigiu um Decision/Contract Gate exclusivamente de leitura, cobrindo: revalidação de Documento 16 (Arquitetura do Agente de IA, integral), Documento 17 (Catálogo de Workflows, integral), Documento 06 (Máquina de Estados, integral), Documento 08 (Motor de Configuração/Políticas, integral), Architecture Principles, e inspeção direta do código atual (`ConversationMessageReceivedProcessor`, `InboundGuestMessageProcessor`, `ModelResult`, os enums `AgentInteractionOutcome`/`AgentSessionStatus`, `PolicyScopeType`). Um relatório de governança de 38 itens foi produzido e aprovado sem alteração. Decisões oficiais do gate:

- **Policy role**: a avaliação de política de Early/Late permanece exclusivamente dentro dos Commands de GuestOperations (já existente desde ADR-024) — o AI Agent nunca executa uma segunda policy engine antes do Command. `GetRelevantPolicies` permanece Tool informativa de leitura. Nenhum `IAgentPolicyEngine`/`ToolEligibilityPolicy` paralelo foi criado.
- **Workflow role**: Early/Late/AccessDelivery continuam acionando as coreografias reais já existentes via evento; o AI Agent nunca cria Workflow diretamente nem chama um dispatcher genérico.
- **Orquestrador**: `ConversationMessageReceivedProcessor` NÃO foi extraído em um novo componente (`AgentConversationOrchestrator`/`AgentRunCoordinator`) — nenhuma nova responsabilidade concreta justificava a extração; o arquivo permanece uma única classe bem decomposta em métodos privados de responsabilidade única.
- **Máquinas de estado**: nenhum novo estado em `Conversation`/`AgentSession`/`AgentInteraction` — "Aguardando Resposta" (Documento 06 §6) permanece derivável do fato `AgentPendingAction.Status ∈ {Proposed, Confirmed}`, nunca um campo próprio.
- **0/N reservas — gap arquitetural registrado, não escondido**: `ReservationResolutionZeroOrMultiple=DEFERRED_ARCHITECTURAL_GAP`. Hoje, 0 candidatos (nenhuma Reservation encontrada) ou N>1 candidatos (múltiplas Reservations elegíveis) resultam em **nenhuma Conversation criada, nenhum processamento pelo AI Agent, nenhuma resposta ao hóspede** — apenas um log (`InboundGuestMessageProcessor`, decisão original do CP1). Responder a esses casos exigiria tornar `Conversation.ReservationId` opcional ou criar um conceito de pré-sessão fora da invariante atual ("nenhuma conversa existe sem uma origem", Documento 12 §17) — uma mudança de contrato/arquitetura, não uma decisão segura de orquestração. Por decisão explícita do usuário, este gap permanece **fora do escopo do CP5**, preservado exatamente como o CP1 o deixou, e **deverá ser reavaliado pelo CP8** (Final Homologation) para decidir se permanece deferred ou exige um checkpoint corretivo.
- **System prompt / personalidade / tom**: nenhuma dessas configurações existe em Configuration hoje (nenhum `PolicyCategory` relacionado a IA no domínio); criá-las agora seria escopo novo desproporcional a um checkpoint que ainda roda sobre `FakeModelProvider`. `SystemPromptSource=DEFERRED_TO_CP7`, `PersonalityToneConfiguration=DEFERRED_TO_CP7`.
- **Timezone**: nenhum conceito de timezone existe em Property/Tenant/Reservation em nenhum lugar do domínio. `TimezoneSource=DEFERRED_TO_CP7` — o CP5 não cria essa fonte, apenas corrige o parsing para nunca interpretar silenciosamente um datetime sem offset usando o timezone do servidor (§9.4).
- **Retry**: exatamente 1 retry controlado (2 tentativas no total) para falha técnica de `IModelProvider.GenerateAsync`; nunca para Write Tool após incerteza de execução; 1 retry para a chamada de entrega de resposta em falha técnica, reutilizando a mesma chave de idempotência determinística já existente.

### 9.2 Retry único e controlado do model provider

`GenerateWithRetryAsync` (novo método privado em `ConversationMessageReceivedProcessor`) envolve toda chamada a `IModelProvider.GenerateAsync` — tenta uma vez, e em caso de `ModelProviderException`, tenta exatamente mais uma vez (2 tentativas no total, nunca mais). Aplica-se uniformemente às duas chamadas do loop (Call#1, antes de qualquer Tool; Call#2, em `BuildSyntheticResponseAsync`, sempre depois de uma Tool real ou sintética já ter produzido seu próprio conteúdo sanitizado):

- **Call#1 esgota o retry**: nenhuma Tool é executada; a interação é marcada `Failure`; uma resposta de fallback determinística e segura ("Desculpe, não consegui processar sua mensagem agora...") ainda é entregue ao hóspede — mudança de comportamento intencional em relação ao CP2/CP4, onde uma falha de Call#1 nunca enviava nada.
- **Call#2 esgota o retry**: a Tool/Command já executou com sucesso (ou já produziu um conteúdo sintético seguro, como uma proposta ou cancelamento) — o orquestrador **nunca** re-executa a Tool. `BuildSyntheticResponseAsync` cai de volta para o próprio `toolContent` já conhecido, entregue verbatim como resposta — nunca uma paráfrase, mas também nunca "não consegui processar" quando a ação real já teve sucesso (mandato item 29/33). A interação completa como `Success`.

`FakeModelProvider` ganhou `TransientFailureTriggerMarker` (`"[FAKE_MODEL_TRANSIENT_FAILURE]"`) — lança `ModelProviderException` apenas na primeira vez que um dado conteúdo de mensagem é visto pela própria instância (contador em memória, correto porque `IModelProvider` é registrado `Scoped` — uma instância por mensagem processada), e cai para uma resposta normal na segunda chamada com o mesmo conteúdo — prova o retry ponta a ponta sem nenhuma reconfiguração de DI.

### 9.3 `ToolName` desconhecido — nunca dispatch genérico, sempre resposta segura

Mudança de comportamento intencional em relação ao CP3/CP4 (onde qualquer problema de Tool, incluindo nome desconhecido, falhava a interação inteira sem nenhuma resposta): agora, um `ToolName` fora da allowlist fixa (`_tools`) é interceptado **antes** de `ExecuteToolWithAuditAsync` — nunca localizado via reflection/lookup genérico, apenas comparado contra os nomes já registrados. `RecordUnknownToolExecutionAsync` grava um `AgentToolExecution` de auditoria com `FailureCode="unknown_tool"` (mesma semântica de antes), mas a interação **não falha** — uma resposta segura e genérica é entregue ("No momento não consigo realizar essa ação específica. Posso ajudar com outra coisa?"), sem nunca revelar o nome da Tool solicitada, stack trace ou detalhe interno. Toda falha de uma Tool REAL e conhecida (falha de negócio ou exceção inesperada) continua com o comportamento original do CP3/CP4 — interação falha, sem resposta.

### 9.4 Correção de segurança — offset de timezone obrigatório

Auditoria do CP5 encontrou um risco real: `RequestEarlyCheckInTool`/`RequestLateCheckoutTool` usavam `DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, ...)`, que aceita silenciosamente uma string sem offset explícito e a interpreta usando o timezone **local do servidor** — nenhuma fonte de timezone existe em Property/Tenant/Reservation em lugar nenhum do domínio, então esse fallback poderia produzir um instante UTC incorreto sem nenhum erro. Corrigido em ambas as Tools com uma validação de regex (`^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$`) que exige `Z` ou um offset explícito `±hh:mm` antes de aceitar o parse — uma entrada sem offset é rejeitada com o mesmo `FailureCode` (`invalid_requested_check_in_at`/`invalid_requested_check_out_at`) já usado para qualquer data malformada, nunca interpretada silenciosamente. `NaturalLanguageLocalTimeResolutionBlocked=true` registra essa correção; `TimezoneSource` permanece `DEFERRED_TO_CP7` — o CP5 corrigiu o risco sem criar a fonte real de timezone.

### 9.5 Classificação de intent não suportado e de pedido de humano

Dois novos marcadores determinísticos em `FakeModelProvider`: `UnsupportedRequestTriggerMarker` (`Intent="unsupported_request"`) e `HumanHandoffTriggerMarker` (`Intent="human_handoff_requested"`) — ambos produzem uma resposta final normal (nunca um `ToolCallRequest`), classificada via o campo `ModelResult.Intent` já existente desde o CP2, sem exigir nenhuma mudança de contrato ou novo caminho no orquestrador (a interação segue o fluxo padrão de "resposta direta", já suportado). Para pedidos não suportados (cancelamento, reembolso, desconto, negociação): resposta honesta explicando que o AI Agent não pode ajudar com isso, nunca simulando uma ação/Command que não ocorreu. Para pedido explícito de humano: o CP5 **apenas classifica** — a resposta reconhece o pedido sem jamais afirmar "já encaminhei" ou "o atendente foi notificado", já que nenhum handoff real (estado, notificação ao administrador, suspensão da IA) existe ainda — tudo isso permanece integralmente CP6.

### 9.6 Retry de entrega de resposta

`AgentResponseDeliveryService` (Infrastructure) ganhou 1 retry automático quando `SendAgentResponseCommand` falha por um motivo que não seja um dos dois códigos permanentes/de estado de dados já conhecidos (`ConversationNotFound`, `GuestContactOrPhoneNotAvailable`) — qualquer outra falha (tipicamente `connector_exception`/`connector_rejected`) é tentada novamente exatamente uma vez. Como a chave de idempotência de `SendAgentResponseCommand` já é determinística (`TenantId`/`AgentInteractionId`/`Channel`), a segunda chamada reutiliza automaticamente a mesma chave — nunca cria uma segunda `Message`.

### 9.7 Testes — contagens exatas

| Suíte | Resultado |
|---|---|
| `IHostPro.Contexts.AIAgent.Tests.Unit` (inclui os novos testes de retry, unknown tool, unsupported/human-handoff intent, e o novo `AgentResponseDeliveryServiceTests`) | 126 aprovados |
| `IHostPro.Contexts.AIAgent.Tests.Integration` (sem alteração de schema — nenhuma migration neste checkpoint) | 23 aprovados (sem regressão) |
| `IHostPro.Contexts.GuestOperations.Tests.Integration` (sem alteração de código neste checkpoint) | 18 aprovados (sem regressão) |
| `IHostPro.Contexts.Communication.Tests.Integration` (sem alteração de código neste checkpoint) | 26 aprovados (sem regressão) |
| `IHostPro.ArchitectureTests` (sem novo arquivo — nenhuma nova Tool/exceção/aggregate) | 291 aprovados (sem regressão) |
| `AIAgentConversationalOrchestrationWorkflowRoundTripTests` (E2E real — Postgres/RabbitMQ/Worker/Api reais, novo arquivo) — retry transitório com sucesso real, Tool desconhecida sem dispatch, intent não suportado, pedido de humano, rejeição de datetime sem offset | 5 aprovados (confirmado em duas execuções consecutivas; parte da suíte completa abaixo) |
| `IHostPro.Api.Tests.Integration` (suíte completa) | ver §9.8 |
| MigrationRunner | N/A — nenhuma migration, nenhuma mudança de topologia/composição neste checkpoint |
| Build Release | 0 erro |

### 9.8 Regressão completa e evidência final

| Suíte | Resultado |
|---|---|
| `IHostPro.Api.Tests.Integration` (suíte completa, execução final limpa) | 81 aprovados, 0 com falha (25 min 17 s) |
| Build Release (solução completa) | 0 erro (20 avisos `NU1903` pré-existentes, SSH.NET, não relacionados a este checkpoint) |
| `git diff --check` | Sem erros (apenas avisos benignos de normalização LF→CRLF) |
| Revisão manual do diff | Nenhum novo vazamento de secret/QR/`GuestPhone`/payload de provider/tipo Anthropic-Claude; nenhuma mudança de contrato de dados sensíveis — apenas orquestração, validação e conteúdo de resposta |

**Nota de transparência sobre `IHostPro.Api.Tests.Integration`**: as duas primeiras execuções completas desta sessão de fechamento (76 aprovados titulares mais os 5 novos deste checkpoint = 81 testes) apresentaram, cada uma, exatamente 2 falhas isoladas: `ConversationMessageReceivedWorkflowRoundTripTests.A_single_inbound_message_creates_one_AgentSession_and_one_successful_AgentInteraction` (teste do CP2, mensagem sem nenhum marcador, não tocado por este checkpoint) e `PolicyUpdatedRegressionTests.PolicyUpdated_delivered_through_real_RabbitMQ_to_the_real_Worker_advances_the_real_Redis_cache_generation` (teste de Configuration, sem nenhuma relação com o AI Agent). Investigados antes de qualquer conclusão: ambos, executados isoladamente (mesmo commit, sem nenhuma alteração de código), passaram limpos e rapidamente (8s e 36s respectivamente, bem dentro dos próprios orçamentos de timeout). Uma terceira execução completa, sem nenhuma mudança de código entre as tentativas, resultou em 81 aprovados, 0 com falha — confirmando que as falhas anteriores foram artefato de contenção de recursos (Postgres/RabbitMQ/Worker reais sob carga prolongada, agora com três classes de teste E2E do AI Agent compartilhando o mesmo fixture/Worker), não uma regressão real deste checkpoint — mesmo padrão já observado e documentado durante o fechamento de CP3/CP4 nesta mesma sessão.

### 9.9 Escopo explicitamente não implementado (por decisão do gate, não por omissão)

- Disambiguação de 0/N reservas (`ReservationResolutionZeroOrMultiple=DEFERRED_ARCHITECTURAL_GAP`, a ser reavaliado no CP8).
- Fonte real de timezone por Property/Tenant/Reservation, e qualquer parser de linguagem natural para datas (`TimezoneSource=DEFERRED_TO_CP7`).
- System prompt real, personalidade/tom/formalidade configuráveis (`SystemPromptSource=DEFERRED_TO_CP7`, `PersonalityToneConfiguration=DEFERRED_TO_CP7`).
- Handoff humano completo — estado de escalonamento, notificação ao administrador, suspensão da IA (`HumanHandoffImplemented=false`, CP6).
- Motor de autonomia por níveis 0-4 (Documento 08 §11) — não implementado em lugar nenhum da plataforma hoje; desproporcional a um checkpoint ainda em `FakeModelProvider`.
- Retry de Read Tool — deliberadamente omitido (explicitamente opcional no mandato); a idempotência/segurança de leitura já é garantida pela ausência de efeito colateral.

`Cp5CommitCount`: registrado no relatório final da conversa de homologação.
