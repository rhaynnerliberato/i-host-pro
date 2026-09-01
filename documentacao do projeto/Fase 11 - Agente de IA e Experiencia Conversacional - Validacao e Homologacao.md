# Fase 11 — Agente de IA e Experiência Conversacional — Validação e Homologação

Versão: 1.7
Status: Concluída — DEFINITIVAMENTE CONCLUÍDA E HOMOLOGADA NO NÍVEL MVP, COM BLOCKERS DE PRODUCTION DOCUMENTADOS (Checkpoint 8 — Final Homologation)

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
CP5 (concluído) — Policies, Workflow & Conversational Orchestration
CP6 (concluído) — Human Handoff, Safety & Audit
CP7 (concluído) — Anthropic Claude Real Proof
CP8 (concluído, este documento) — Final Homologation

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

## 10. Checkpoint 6 — Human Handoff, Safety & Audit

**Status:** Concluído e homologado. `HumanHandoffImplemented=true`. `AgentSessionEscalationImplemented=true`. `ManualResumeOnly=true`. `AdministratorNotificationImplemented=true`. `AdministratorNotificationContactOwner=Communication`. `LowConfidenceMode=ExplicitClassification`. `NumericConfidenceThreshold=false`. `NullConfidenceTriggersHandoff=false`. `PendingActionCancelledOnHandoff=true`. `HumanAssignmentImplemented=false`. `EmbeddedHumanChatImplemented=false`. `AnthropicIntegrated=false`. `ExternalLLMNetworkCalls=0`.

**Objetivo**: implementar o escalonamento real de uma sessão de IA para atendimento humano — um catálogo fechado de 10 motivos restritos classificados server-side (nunca pelo modelo), um novo agregado `AgentHumanHandoff`, um novo estado `AgentSessionStatus.Escalated` que suspende genuinamente a IA (zero chamada de modelo/Tool para qualquer mensagem subsequente até retomada manual), uma tentativa real de notificação ao administrador (Communication, via um novo destinatário dedicado `AdministratorNotificationContact`), e um endpoint HTTP real de retomada manual (`IHostPro.Api`, permissão nova `AI_AGENT:MANAGE`). Nenhuma fila/atribuição de atendentes, nenhum chat embutido, nenhum threshold numérico de confiança, nenhuma nova exceção síncrona, nenhuma integração real com Anthropic.

### 10.1 Governança prévia — CP6 Decision/Contract Gate (read-only)

Antes de qualquer código, o usuário exigiu um Decision/Contract Gate exclusivamente de leitura, cobrindo: Documento 16 (integral), Documento 17 Workflow 14 ("Mensagem → Classificar → Encaminhar → Notificar Administrador → Suspender IA"), Documento 06 §6, Documento 08 §11/§12, Documento 09 (integral), Documento 12 §9/§10, Architecture Principles (Exceção 2/papel do Workflow), ADR-018, e uma auditoria direta do código real (catálogo de permissões de Identity, existência de um BC de Audit, precedente `FrontDeskContact`, estrutura real do Workflow BC, forma atual de `AgentSession`/`AgentInteraction`/`Conversation`). Um relatório de governança de 35 itens identificou três achados críticos, resolvidos explicitamente pelo usuário antes de qualquer código:

- **Nenhum mecanismo de notificação a administrador existe hoje em lugar nenhum da plataforma** — `FrontDeskContact` é escopado por Property (Portaria), nunca administrativo; `Identity.User` não tem campo de telefone algum; Communication só envia via WhatsApp. Resolvido criando `AdministratorNotificationContact` como um novo agregado dedicado em Communication (nunca reaproveitando `FrontDeskContact`, nunca adicionando telefone a `Identity.User`).
- **Workflow BC não é necessário para o escopo mínimo do CP6** — suspender a IA é domínio do próprio AI Agent, notificar é reação do próprio Communication; nenhum Command cross-context precisa ser emitido por um terceiro orquestrador (ADR-018 permanece intacto: Workflow continua o único emissor autorizado de Commands cross-context, mas o CP6 não precisa de nenhum).
- **`AgentSessionStatus` deve ser o único dono do novo estado "escalado"**, nunca duplicado em `Conversation.Status` (`ConversationStatusChanged=false` — decisão oficial, `Conversation` permanece só `Active`).

Decisões oficiais do gate, travadas literalmente pelo mandato de implementação: `HandoffOwner=AIAgent`; `AgentSessionEscalatedState=true`; `AgentHumanHandoffAggregate=true`; `LowConfidenceThresholdMode=EXPLICIT_CLASSIFICATION_ONLY` (nunca um número); `NumericConfidenceThreshold=false`; `NullConfidenceTriggersHandoff=false`; `ResumeMode=MANUAL_ONLY`; `ResumeEndpointHost=IHostPro.Api`; `CreateAIAgentApiProject=false`; `ResumePermission=AI_AGENT:MANAGE` (nova, nunca reaproveitar `GUEST_OPERATIONS:MANAGE`); `AdministratorNotificationContactOwner=Communication`; `NewSyncExceptionRequired=false`; `HumanAssignmentImplemented=false`; `EmbeddedHumanChatImplemented=false`. Os 10 motivos restritos aprovados, um catálogo fechado (Documento 16 §16, Documento 08 §12): `ExplicitHumanRequest`, `Refund`, `Accident`, `Police`, `Negotiation`, `SevereDamage`, `SeriousComplaint`, `AggressiveBehavior`, `LowConfidence`, `IntegrationFailure` — adicionar um décimo primeiro exige um novo mandato, nunca decidido silenciosamente (provado por `AIAgentHumanHandoffArchitectureTests.AgentHumanHandoffReasonCode_Catalog_Is_Exactly_The_Ten_Approved_Values`).

### 10.2 `AgentHumanHandoff` (novo agregado, schema `ai_agent`)

`AgentHumanHandoff` — `Id, TenantId, AgentSessionId, ReasonCode, Status, RequestedAtUtc, NotificationAttemptedAtUtc?, NotifiedAtUtc?, NotificationFailureCode?, ResumedAtUtc?, ResumedByActorId?`. Referenciado por `AgentSessionId` através de uma foreign key real de banco de dados (`fk_agent_human_handoffs_agent_sessions`, `ON DELETE RESTRICT`) — mesmo precedente já estabelecido por `AgentToolExecution`/`AgentPendingAction` (CP3/CP4), ambas as tabelas no mesmo schema/Bounded Context. Deliberadamente nunca persiste: a mensagem bruta do hóspede, o prompt bruto, o histórico completo da conversa, `GuestPhone`, o telefone/destino do próprio administrador (isso vive exclusivamente em `Communication.AdministratorNotificationContact`), qualquer credencial, QR, ou payload de provider — apenas metadados de auditoria. Estados: `Requested → Notified → Resumed`, ou `Requested → (MarkNotificationFailed, permanece Requested) → Resumed` — deliberadamente **sem** `Assigned`/`Acknowledged`/`InProgress`/`Closed` (não é MVP). Índice único parcial (`ix_agent_human_handoffs_active_per_session`, `WHERE status IN ('Requested', 'Notified')`) garante no máximo um handoff ativo por sessão.

### 10.3 `AgentSessionStatus.Escalated` — suspensão real da sessão

`AgentSession` ganha `Escalate(now)`/`Resume(now)` e o novo valor de enum `Escalated`, que passa a ser o único dono do fato "IA suspensa para atendimento humano" — nunca duplicado em `Conversation.Status` (§10.1). `RecordInteraction` continua lançando exceção quando a sessão não está `Active` — agora também quando está `Escalated` (coberto por um teste de Unit dedicado). O índice único parcial de `agent_sessions` (`ix_agent_sessions_tenant_id_conversation_id_active_unique`) foi ampliado de `WHERE status = 'Active'` para `WHERE status IN ('Active', 'Escalated')` — correção crítica: sem essa ampliação, `IAgentSessionResolver`/`GetActiveByConversationIdAsync` criaria uma SEGUNDA sessão `Active` para a mesma `Conversation` assim que uma mensagem chegasse depois do escalonamento, silenciosamente contornando o guard de sessão suspensa.

### 10.4 `IAgentHumanHandoffReasonClassifier` — classificador fixo, nunca o modelo

Por decisão explícita do mandato (o modelo nunca decide sozinho que precisa de handoff — mesma disciplina já aplicada a `IAgentToolConfirmationPolicy`, CP4), a classificação é um `Dictionary<string, AgentHumanHandoffReasonCode>` fixo, server-side, case-sensitive, allowlist fechada — mapeia exatamente as 10 strings de intent (`"human_handoff_requested"`, `"refund"`, `"accident"`, `"police"`, `"negotiation"`, `"severe_damage"`, `"serious_complaint"`, `"aggressive_behavior"`, `"low_confidence"`, `"integration_failure"`) aos 10 `AgentHumanHandoffReasonCode`. Um intent não reconhecido ou nulo — incluindo o próprio `"unsupported_request"` do CP5 — retorna `null` (nenhum handoff). `FakeModelProvider` ganhou o marcador genérico `IntentTriggerPrefix` (`"[FAKE_MODEL_INTENT:"` + valor + `"]"`) para provar as 9 novas razões sem precisar de 9 marcadores nomeados; `HumanHandoffTriggerMarker` (CP5) permanece intacto e continua produzindo o mesmo intent (`"human_handoff_requested"`) — ambos os grafismos funcionam para essa mesma razão.

### 10.5 Notificação ao administrador — `AdministratorNotificationContact` (Communication) / `SendHumanHandoffNotificationCommand`

`AdministratorNotificationContact` (novo agregado, Communication.Domain) — `Id, TenantId, DestinationPhone, IsActive, CreatedAtUtc, UpdatedAtUtc`. Cardinalidade MVP: no máximo 1 registro ATIVO por Tenant (índice único parcial `WHERE is_active`, verificado diretamente contra o Postgres real por um teste dedicado de violação). Communication é o único dono — resolve/armazena/retorna o telefone inteiramente por conta própria; o AI Agent nunca resolve, armazena, ou vê esse telefone (provado por `AIAgentHumanHandoffArchitectureTests.AIAgent_Domain_And_Application_Never_Define_A_Phone_Or_Destination_Field`, uma varredura por reflection sobre as próprias assemblies do AI Agent por qualquer membro `*Phone*`).

`SendHumanHandoffNotificationCommand`/`Handler` (Communication.Application, primeiro novo Command síncrono desde `SendAgentResponseCommand` do CP4) — resolve o destinatário sozinho a partir de `TenantId` (nunca aceita destino do chamador); chave de idempotência determinística `{TenantId}:AI_HUMAN_HANDOFF:{AgentHumanHandoffId}` (uma redelivery com o mesmo `AgentHumanHandoffId` retorna a mesma `MessageId`, nunca cria uma segunda); `TemplateKey = "AI_HUMAN_HANDOFF_NOTIFICATION"`; conteúdo construído somente a partir de `ReasonCode`/`ReservationId` (referência opaca)/timestamp — nunca `GuestName`/`GuestPhone`/credencial/QR; telefone do administrador mascarado antes de persistir em `Message.DestinationMasked` (mesmo padrão de mascaramento já estabelecido por `SendAgentResponseCommand`). `IAdministratorNotificationService` (AIAgent.Application, abstrato) + `AdministratorNotificationService` (AIAgent.Infrastructure, adapter concreto via `ICommunicationRequestDispatcher`) — mesmo padrão da Exceção 3 já estabelecido por `IAgentResponseDeliveryService` (CP4): apenas AIAgent.Infrastructure pode acoplar-se à Application layer de Communication.

### 10.6 Retomada manual — `ResumeAgentSessionCommand` / endpoint HTTP real / permissão `AI_AGENT:MANAGE`

Por decisão explícita do mandato (`ResumeMode=MANUAL_ONLY`, `CreateAIAgentApiProject=false`), a retomada é um endpoint HTTP real (`POST /api/v1/ai-agent/sessions/{sessionId}/resume`) hospedado DIRETAMENTE em `IHostPro.Api/Controllers/` — o primeiro write HTTP-triggered do AI Agent, sem nenhum projeto `IHostPro.Contexts.AIAgent.Api` próprio (todo outro Bounded Context tem o seu; provado por `AIAgentHumanHandoffArchitectureTests.No_AIAgent_Api_Project_Exists`, verificando que a assembly nunca é carregada). `ResumeAgentSessionCommandHandler`: busca a sessão (falha `AgentSessionNotFound` tanto para inexistente quanto para tenant errado, mesmo código para os dois casos — nunca vazando existência entre tenants); busca o handoff ativo (falha `NoActiveHumanHandoff` se não houver); `handoff.Resume(now, actorId)` + `session.Resume(now)` juntos, na mesma transação. `AgentPendingAction` cancelada no momento do handoff nunca é reaberta pela retomada (permanece `Cancelled` para sempre).

Permissão nova `AI_AGENT:MANAGE` — catalogada em `IdentityPermissionCodes`, seedada em `IdentityCatalogSeed` (só ADMIN, mesmo precedente de `INTEGRATIONS:MANAGE`/`GUEST_OPERATIONS:MANAGE` — nenhuma regra documentada autoriza OPERATOR por padrão), com sua própria migration (`AddAiAgentManagePermission`). `AIAgentIdentityReader` (novo, `IHostPro.Api/Http/`) extrai `sub`/`tenant_id` do principal autenticado — nunca confia em `ActorId`/`TenantId` vindos do corpo da requisição. `IHostPro.Api/Program.cs` ganha, pela primeira vez na sua história, referências tanto ao AI Agent quanto à Communication — `AddAIAgentCommandDispatch` (novo, estreito, Api-only — deliberadamente NÃO reutiliza `AddAIAgentModule` completo do Worker, já que várias Read Tools precisam de dispatchers de Payments/Communication que o Api não compõe uniformemente) e `AddCommunicationModule` + `KeepOnlyMediatorHandlers` (restringindo aos dois handlers administrativos de `AdministratorNotificationContact`, nunca `SendAgentResponseCommandHandler`/`SendHumanHandoffNotificationCommandHandler`, que precisam de um `IOutboundMessageConnector` que o Api nunca registra).

### 10.7 Fluxo estendido do `ConversationMessageReceivedProcessor`

Duas novas ramificações, nesta ordem: (1) **guard de sessão suspensa** — verificado logo após a resolução da sessão, ANTES de qualquer construção de contexto/chamada de modelo; se `session.Status == Escalated`, despacha para `HandleSuspendedSessionAsync` e retorna, garantindo zero chamadas de modelo/Tool; (2) **classificação de handoff** — verificada logo após a Call#1 e o início da interação, ANTES das ramificações existentes de confirmação/tool-call; se `IAgentHumanHandoffReasonClassifier.Classify(result.Intent)` retornar uma razão, despacha para `ProcessHumanHandoffRequestAsync` e retorna cedo.

`ProcessHumanHandoffRequestAsync` — atomicamente, em uma transação: cria `AgentHumanHandoff.Request(...)`, escalona a sessão, cancela qualquer `AgentPendingAction` ativa (via `.Cancel()` já existente — nunca chama nenhum Command de negócio), completa a interação já iniciada diretamente (contornando `CompleteInteractionAndDeliverResponseAsync`, cujo `session.RecordInteraction` lançaria exceção numa sessão já `Escalated`). Fora dessa transação, chama `IAdministratorNotificationService.NotifyAsync`; em uma SEGUNDA transação, marca o handoff `Notified` (somente em sucesso real) ou `MarkNotificationFailed` (permanece `Requested`, retentável, sem rollback do escalonamento/cancelamento). Por fim entrega um reconhecimento determinístico (nunca gerado pelo modelo) — o conteúdo reflete se a notificação de fato teve sucesso, nunca afirmando um encaminhamento que não ocorreu.

`HandleSuspendedSessionAsync` — para uma nova mensagem chegando numa sessão JÁ `Escalated`: registra uma `AgentInteraction` mínima (Success, zero tokens/confidence/intent, nunca toca a sessão), busca o handoff ativo, e envia um reconhecimento determinístico refletindo o status ATUAL do handoff (`Notified` → "foi encaminhada"; só `Requested` → "está pausado... foi registrada", nunca afirmando notificação). Nunca tenta notificar de novo.

### 10.8 Achados corrigidos durante a implementação

- **Correção de um achado mischaracterizado do CP4 — drift de grant `DELETE` no schema `ai_agent`**: a homologação do CP4 (§8.8) atribuiu o mesmo sintoma (`DELETE` presente em `agent_tool_executions`/`agent_pending_actions`, apesar de toda migration declarar apenas `SELECT/INSERT/UPDATE`) a "um comando SQL manual de diagnóstico" executado fora de banda. A verificação de schema própria deste checkpoint (`\ddp ai_agent.*` contra o Postgres real de desenvolvimento) encontrou a causa real: um `ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA ai_agent GRANT arwd ... TO ihostpro_app` genuíno e permanente — toda NOVA tabela criada neste schema herda `DELETE` silenciosamente, contradizendo o `GRANT` explícito de toda migration. Isso também explica retroativamente o mesmo sintoma do CP3, nunca de fato um erro manual isolado. Corrigido na origem, na própria migration deste checkpoint (`ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA ai_agent REVOKE DELETE ON TABLES FROM ihostpro_app;`, com `GRANT` simétrico no `Down()`), aplicado diretamente ao banco real e reverificado: as 5 tabelas do AI Agent mostram uniformemente `INSERT/SELECT/UPDATE`, sem `DELETE`. Registrado aqui por transparência — a explicação anterior do CP4 está agora superada por esta.
- **`session.RecordInteraction` lançaria exceção numa sessão recém-escalonada**: o desenho inicial faria `ProcessHumanHandoffRequestAsync` retornar um `ModelResult` fluindo pelo mesmo `CompleteInteractionAndDeliverResponseAsync` de toda outra ramificação — mas esse método chama `session.RecordInteraction`, que exige `Status == Active`. Corrigido fazendo `ProcessHumanHandoffRequestAsync` completar a interação e entregar a resposta diretamente, sem passar por esse método.
- **Teste do CP5 com premissa agora obsoleta (não uma regressão, mudança de comportamento intencional do CP6)**: `AIAgentConversationalOrchestrationWorkflowRoundTripTests.Human_handoff_requested_intent_is_classified_and_never_claims_a_real_handoff` documentava e testava a fronteira do CP5 ("handoff completo é escopo do CP6") — agora factualmente incorreta, já que o CP6 liga exatamente esse classificador a um handoff real. Renomeado e reescrito (`..._creates_a_real_handoff_and_the_ack_never_overclaims_notification_success`) com asserções novas (sessão `Escalated`, `AgentHumanHandoff` real criado, razão `ExplicitHumanRequest`) — as asserções de string originais ("nunca afirma encaminhamento") permanecem válidas e verdadeiras (nenhum `AdministratorNotificationContact` é seedado nesta classe de teste, então a notificação genuinamente falha e o reconhecimento nunca superafirma).
- **Teste de Identity com contagem fixa do catálogo, quebrado pela nova permissão**: `IdentityRowLevelSecurityTests.Migration_applies_cleanly_and_seeds_the_platform_catalog` esperava exatamente 35 `Permission`s/42 `RolePermission`s — quebrado deterministicamente pela nova `AI_AGENT:MANAGE` (+1 permissão, +1 mapeamento ADMIN), exatamente como o próprio comentário do teste já documentava para cada permissão anterior (Fase 9 CP2.1, Fase 10 CP1). Corrigido para 36/43, com o mesmo padrão de comentário explicativo.
- **Índice único parcial de `agent_sessions` precisou de ampliação, não apenas um novo índice para `agent_human_handoffs`**: descoberto durante o próprio desenho do CP6 (não um bug encontrado tarde) — ver §10.3.

### 10.9 Testes — contagens exatas

| Suíte | Resultado |
|---|---|
| `IHostPro.Contexts.AIAgent.Tests.Unit` (inclui os novos testes de `AgentHumanHandoff`, `AgentHumanHandoffReasonClassifier`, `ResumeAgentSessionCommandHandler`, Escalate/Resume de `AgentSession`, e as 8 novas ramificações de `ConversationMessageReceivedProcessorTests`) | 171 aprovados |
| `IHostPro.Contexts.AIAgent.Tests.Integration` (inclui a nova seção `AgentHumanHandoff` e o teste dedicado de `AgentSessionStatus.Escalated`) | 31 aprovados |
| `IHostPro.Contexts.Communication.Tests.Unit` (inclui os 5 novos testes de `AdministratorNotificationContact`) | 106 aprovados |
| `IHostPro.Contexts.Communication.Tests.Integration` (inclui os 6 novos testes de `SendHumanHandoffNotificationCommandHandlerTests` e os 5 novos de `AdministratorNotificationContactManagementTests`) | 37 aprovados |
| `IHostPro.ArchitectureTests` (novo arquivo `AIAgentHumanHandoffArchitectureTests`, 8 testes; total 299, de 291) | 299 aprovados |
| `IHostPro.Contexts.Identity.Tests.Unit` (sem alteração de código de produção neste checkpoint além do novo catálogo/policy) | 470 aprovados |
| `IHostPro.Contexts.Identity.Tests.Integration` (inclui a correção da contagem fixa do catálogo, §10.8) | 420 aprovados |
| `IHostPro.Contexts.GuestOperations.Tests.Unit`/`Tests.Integration` (sem alteração de código neste checkpoint) | 71 / 18 aprovados (sem regressão) |
| `AIAgentHumanHandoffWorkflowRoundTripTests` (E2E real — Postgres/RabbitMQ/Worker/Api reais, novo arquivo) — handoff explícito com notificação real bem-sucedida, handoff sem contato configurado (nunca superafirma), pending action cancelada, guard de sessão suspensa (com marcador de tool-call embutido, prova de resistência a prompt-injection), retomada via endpoint HTTP autenticado real seguida de processamento normal, retomada sem a permissão correta (403 real) | 6 aprovados |
| `AIAgentConversationalOrchestrationWorkflowRoundTripTests` (atualizado, §10.8) | 5 aprovados |
| `IHostPro.Api.Tests.Integration` (suíte completa) | ver §10.10 |
| MigrationRunner Run #1/#2 | ver §10.10 |
| Build Release | 0 erro |

### 10.10 Regressão completa e evidência final

| Suíte | Resultado |
|---|---|
| MigrationRunner Run #1 (Postgres/RabbitMQ de desenvolvimento reais) | Exit code 0 — 3 migrations novas aplicadas (`AddAiAgentManagePermission` em Identity; `AddAgentHumanHandoff`, incluindo a ampliação do índice de `agent_sessions` e a correção de `ALTER DEFAULT PRIVILEGES`, em AI Agent; `AddAdministratorNotificationContact` em Communication); nenhuma mudança de topologia RabbitMQ (nenhum evento/fila nova neste checkpoint — a notificação é um Command síncrono, nunca mensageria) |
| MigrationRunner Run #2 (mesmo banco, imediatamente em seguida) | Exit code 0 — zero drift |
| Verificação de schema pós-Run (read-only, SQL direto) | `ai_agent.agent_human_handoffs`: FK real (`ON DELETE RESTRICT`) para `agent_sessions`, índice único parcial `WHERE status IN ('Requested','Notified')`, RLS `ENABLE`+`FORCE`, policy `tenant_isolation` fail-closed, grants `SELECT/INSERT/UPDATE` sem `DELETE`; `ai_agent.agent_sessions`: índice único parcial ampliado para `WHERE status IN ('Active','Escalated')`; as 5 tabelas do schema `ai_agent` confirmadas uniformemente `INSERT/SELECT/UPDATE`, sem `DELETE` (correção do achado do CP4, §10.8); `communication.administrator_notification_contacts`: índice único parcial `WHERE is_active`, RLS `ENABLE`+`FORCE`, `DELETE` revogado explicitamente pela própria migration (mesmo padrão já estabelecido do schema `communication`) |
| `IHostPro.Api.Tests.Integration` (suíte completa, execução final limpa) | 87 aprovados, 0 com falha (17 min 35 s) — ver nota de transparência abaixo |
| Build Release (solução completa) | 0 erro (20 avisos `NU1903` pré-existentes, SSH.NET, não relacionados a este checkpoint) |
| `git diff --check` | Sem erros (apenas avisos benignos de normalização LF→CRLF) |
| Revisão manual do diff e dos arquivos novos do CP6 | Nenhum vazamento de secret/QR/`GuestPhone`/telefone bruto do administrador/payload de provider/tipo Anthropic-Claude; `AdministratorNotificationContact.DestinationPhone` mascarado antes de persistir em `Message.DestinationMasked` (mesmo padrão de `SendAgentResponseCommand`); confirmado por reflection (`AIAgentHumanHandoffArchitectureTests`) que nenhum membro `*Phone*` existe em Domain/Application do AI Agent; conteúdo da notificação (`SendHumanHandoffNotificationCommandHandler.BuildContent`) contém somente `ReasonCode`/`ReservationId`/timestamp |

**Nota de transparência sobre `IHostPro.Api.Tests.Integration`**: nesta sessão de fechamento, quatro execuções completas da suíte foram realizadas em sequência direta. As três primeiras apresentaram, cada uma, uma quantidade pequena de falhas isoladas (1, 1 e 4 testes, respectivamente) — em nenhum caso um teste do próprio CP6, e em nenhum caso o mesmo teste se repetindo entre execuções: `PolicyUpdatedRegressionTests.PolicyUpdated_delivered_through_real_RabbitMQ_to_the_real_Worker_advances_the_real_Redis_cache_generation` (Configuration, já documentado como flaky no fechamento do CP5, §9.8), `FrontDeskNotificationWorkflowRoundTripTests.GuestCheckedIn_with_a_configured_front_desk_contact_creates_a_real_Message_addressed_to_the_front_desk_not_the_guest` (Fase 10, não tocado neste checkpoint), e um grupo de 4 (incluindo `ConversationMessageReceivedWorkflowRoundTripTests.A_single_inbound_message_creates_one_AgentSession_and_one_successful_AgentInteraction`, já observado como flaky no fechamento do CP5, e `WhatsAppMessageStatusRetryPolicyScopingTests.The_specific_exception_retries_while_an_unrelated_one_does_not`, Fase 9, não relacionado). Cada teste isolado, executado sozinho (mesmo commit, sem nenhuma alteração de código), passou limpo e rapidamente — confirmando resource contention real (Postgres/RabbitMQ/Worker reais sob ~17-24 minutos de execução sequencial ininterrupta, `DisableTestParallelization=true` já confirmado no próprio assembly de teste, então não é concorrência entre classes — é desgaste de recursos ao longo do tempo real de execução), nunca uma regressão deste checkpoint. A quarta execução completa, sem nenhuma mudança de código entre as tentativas, resultou em **87 aprovados, 0 com falha, 17 min 35 s** — confirmando definitivamente que nenhuma das falhas anteriores tinha relação com o código deste checkpoint.

### 10.11 Escopo explicitamente não implementado (por decisão do gate, não por omissão)

- Fila/atribuição de atendentes humanos (`HumanAssignmentImplemented=false`) — Documento 09 não define esse fluxo para o MVP; o handoff apenas suspende a IA e notifica, nunca atribui a uma pessoa específica.
- Chat embutido entre hóspede e atendente (`EmbeddedHumanChatImplemented=false`) — o canal permanece o mesmo WhatsApp já existente; nenhuma superfície nova de conversa foi criada.
- Múltiplos destinatários de notificação por Tenant — MVP é exatamente 1 contato ativo; uma lista/round-robin exigiria um novo mandato.
- Retomada automática por qualquer critério (tempo, nova mensagem, etc.) — `ResumeMode=MANUAL_ONLY` é uma decisão oficial do gate, nunca um algoritmo de auto-resume.
- Threshold numérico de confiança para acionar `LowConfidence` — a classificação depende inteiramente de `ModelResult.Intent` já vir marcado como `"low_confidence"` pelo provider; nenhuma comparação numérica (`Confidence < X`) foi introduzida em lugar nenhum do código.
- Projeto `IHostPro.Contexts.AIAgent.Api` dedicado (`CreateAIAgentApiProject=false`) — o endpoint de retomada e o de gestão do contato administrativo vivem diretamente em `IHostPro.Api`.
- Integração real com Anthropic — permanece integralmente CP7.

`Cp6CommitCount`: registrado no relatório final da conversa de homologação.

## 11. Checkpoint 7 — Anthropic Claude Real Proof

**Status:** Concluído e homologado. `AnthropicIntegrated=true`. `AnthropicModel=claude-sonnet-4-6`. `AnthropicSdk=false`. `Transport=REST_HTTPCLIENT`. `TemperatureControl=NOT_SUPPORTED_BY_SELECTED_MODEL`. `TemperatureSentToAnthropic=false`. `RealAnthropicProof=true`. `ExternalLLMNetworkCalls>0`. `RealReadToolProof=true`. `RealHumanHandoffProof=true`. `RealWriteToolProof=false`. `MultilingualProof=pt-BR,en-US`. `RealTokenUsageProof=true`. `MonetaryCostTracking=true`. `ProductionAnthropicSecretBackend=false`.

**Objetivo**: substituir, atrás da mesma interface `IModelProvider` já homologada desde o CP2, o `FakeModelProvider` determinístico por uma integração REST real com a Anthropic Messages API (`claude-sonnet-4-6`) — prova real de rede, uso de tokens, custo monetário, seleção de Read Tool, classificação de human handoff e suspensão pós-escalonamento — mantendo a regressão determinística inteira (87 testes de `IHostPro.Api.Tests.Integration`) inalterada e gated a um provider `Fake` por padrão. Nenhum SDK oficial/não-oficial, nenhuma prova real de Write Tool (fora de escopo), nenhum backend de secret de Produção.

### 11.1 Governança prévia — CP7 Decision/Contract Gate (read-only) e a incompatibilidade de temperature

Antes de qualquer código, o usuário exigiu um Decision/Contract Gate exclusivamente de leitura (42 itens), cobrindo escolha de modelo, contrato da API, config de provider, fonte/comportamento fail-closed do secret, fonte do system prompt, personalidade/tom, hierarquia de configuração (confirmando a soberania do modelo GLOBAL→TENANT→PROPERTY já decidido na Fase 5, Group/Condomínio permanecendo deferred), estratégia de context window, cost tracking, comportamento de confidence, estratégia de intent/tool-call estruturado, formato de idioma, ownership de timezone, classificação de retry, padrão de HTTP client, telemetria, e o gate de teste real (citando `MetaWhatsAppSandboxProofTests` como precedente exato). Aprovado com um mandato de implementação de 80 itens, travando literalmente: `AnthropicModel=claude-sonnet-4-6`, `Transport=REST_HTTPCLIENT`, `TemperatureDefault=0.2`, `MaxOutputTokens=2048`, `LanguageFormat=BCP47`, `TimezoneOwner=Property`, `MonetaryCostTracking=IMPLEMENT_NOW`, `ContextWindowStrategy=FULL_CURRENT_CONVERSATION_CP7_MVP`, entre outros.

**Achado crítico, descoberto antes de qualquer código de produção**: por exigência do próprio mandato (item 73/76 — verificar a documentação OFICIAL atual antes de implementar, parar se a API rejeitar o contrato), a documentação oficial da Anthropic (`platform.claude.com/docs`) foi consultada e confirmou que o parâmetro `temperature` da Messages API é *"Deprecated. Models released after Claude Opus 4.6 do not support setting temperature. A value of 1.0 will be accepted for backwards compatibility, all other values will be rejected with a 400 error."* — `claude-sonnet-4-6`, lançado depois do Opus 4.6, está nessa categoria: enviar `temperature=0.2` (a própria decisão do item 1 do mandato) teria causado HTTP 400 em toda chamada real. Reportado e parado exatamente como o próprio mandato exige, sem trocar o modelo silenciosamente. O usuário resolveu explicitamente como **Opção C**: manter `claude-sonnet-4-6` (nunca trocar de modelo por causa disso), remover `Temperature` inteiramente do contrato MVP de `AI_AGENT_BEHAVIOR` — nunca reintroduzida como nullable/override/`1.0`-fixo só para preservar artificialmente o requisito antigo, nenhuma abstração de `ProviderCapability` criada só para este ponto. `TemperatureControl=NOT_SUPPORTED_BY_SELECTED_MODEL`/`TemperatureSentToAnthropic=false` são decisões oficiais e definitivas — nunca reabertas.

### 11.2 `AnthropicModelProvider` — REST puro, sem SDK (ADR-009)

`AnthropicModelProvider` (novo, `AIAgent.Infrastructure.ModelProviders.Anthropic`) — segunda implementação real de `IModelProvider`, via `IHttpClientFactory` (sem Polly, sem SDK oficial/não-oficial da Anthropic — provado por `AnthropicModelProviderArchitectureTests.No_Third_Party_Anthropic_SDK_Package_Is_Referenced`). Endpoint `https://api.anthropic.com/v1/messages`, header `anthropic-version: 2023-06-01`, autenticação via `x-api-key` (nunca `Bearer`). Todo DTO de request/response (`AnthropicDtos.cs`) é `internal sealed`, confinado ao namespace `AIAgent.Infrastructure.ModelProviders.Anthropic` — provado por `AnthropicModelProviderArchitectureTests.Every_Anthropic_Specific_Type_Lives_In_Its_Own_Dedicated_Infrastructure_Namespace`, nunca vazando para Domain/Application (o próprio `IModelProvider`/`ModelRequest`/`ModelResult`, definidos no CP2, permanecem inteiramente provider-neutros).

### 11.3 `respond_to_guest` — control tool privado, nunca uma Tool de negócio

Como um provider real não expõe os marcadores determinísticos do `FakeModelProvider`, a extração estruturada de metadados (texto final, idioma, intent, confirmation intent) usa uma tool definition privada e não-de-negócio, `respond_to_guest` (schema JSON `message`/`language`/`intent`(enum fechado dos 11 valores conhecidos)/`confirmation_intent`) — nunca um `IAgentTool`, nunca listada no catálogo de negócio, apenas mapeada diretamente para `ModelResult`. Estratégia de `tool_choice` em duas chamadas, preservando a mesma disciplina "nunca multi-hop" já estabelecida pelo `FakeModelProvider` desde o CP3: **Call#1** (nenhuma mensagem de papel `Tool` no histórico ainda) oferece `tool_choice={"type":"any"}` com todas as Tools de negócio + `respond_to_guest`; **Call#2** (última mensagem já é de papel `Tool`, ou seja, depois de uma Tool real/sintética já ter produzido conteúdo) força `tool_choice={"type":"tool","name":"respond_to_guest","disable_parallel_tool_use":true}` sozinha. Mapeamento de papel de mensagem sem `tool_result` nativo: `Guest→"user"`, `Agent→"assistant"`, `Tool→"user"` com prefixo textual `"[Resultado do sistema] "` — decisão deliberada, já que `ModelMessage` (o contrato provider-neutro) não carrega `tool_use_id` e cada chamada é stateless, mirroring o próprio desenho stateless do `FakeModelProvider`.

### 11.4 Secret handling e seleção fail-closed de provider

`IAnthropicCredentialProvider`/`DevelopmentAnthropicCredentialProvider` (mirrors `DevelopmentWhatsAppCredentialProvider` exatamente) — resolve `AIAgent:Anthropic:Secrets:ApiKey` via `IConfiguration` (User Secrets/environment variable em Development, nunca um valor versionado em `appsettings.json`), registrado somente quando `IsDevelopment()` (parâmetro explícito `bool isDevelopmentEnvironment`, nunca resolvido via `IHostEnvironment` dentro do módulo — mirrors `AddExternalIntegrationsModule`). `ProductionAnthropicSecretBackend=false` — nenhum Key Vault/backend real existe ainda para nenhum provider desta base de código; construir um está fora do escopo deste checkpoint. Seleção via `AIAgent:ModelProvider` (`Fake` padrão/`Anthropic`) — um valor não reconhecido e explicitamente setado falha ALTO no startup (`InvalidOperationException`), nunca um fallback silencioso para Fake. Fora de Development com `Anthropic` selecionado, a resolução de DI falha no startup (nenhum `IAnthropicCredentialProvider` registrado) — o próprio requisito fail-closed do mandato, verificado estruturalmente. A chave nunca se torna campo em nenhum tipo fora do próprio credential provider — provado por `AnthropicModelProviderArchitectureTests.No_Api_Key_Field_Exists_Outside_The_Credential_Provider_Itself`.

### 11.5 `AI_AGENT_BEHAVIOR` — primeiro novo PolicyCode desde o fechamento do catálogo da Fase 5

Primeira nova `PolicyCode` desde `EARLY_CHECKIN`/`LATE_CHECKOUT` (Fase 5) — categoria `"IA"` (uma das categorias já oficiais do Documento 08 §4). Contrato final (pós-Opção C, §11.1): `SystemPrompt, Tone, Formality` — nunca `Temperature`. `GetEffectivePolicyQueryHandler` não é genérico (um `switch` fixo de 2 casos, cada um despachando para seu próprio reader tipado) — o armazenamento subjacente (`PolicyValue`/`GlobalPolicyValue`) já é genérico e não exigiu migration para o dado; o lado de leitura exigiu um novo reader tipado (`IAiAgentBehaviorPolicyReader`/`AiAgentBehaviorPolicy`, mirroring `IEarlyCheckInPolicyReader`/`EarlyCheckInPolicy` exatamente) e um novo `case` no switch — nunca uma genericização do engine (decisão explícita do mandato). Resolvido via a Exceção 1 já aprovada (qualquer contexto pode consultar Configuration & Policy síncrona/diretamente) — `AgentContextBuilder` referencia `Configuration.Contracts` diretamente, mesmo padrão já usado por `IConversationHistoryReader`.

`AgentContextBuilder` foi reescrito para compor `ModelRequest.SystemPrompt` de três partes: (1) uma instrução técnica mínima e fixa de fallback de segurança (estrutural/de segurança apenas — nunca persona de negócio, distinção explícita do mandato em relação à proibição de um prompt de negócio FIXO já registrada no CP2, Documento 16 §22); (2) o `SystemPrompt`/`Tone`/`Formality` de `AI_AGENT_BEHAVIOR` quando resolvido; (3) um fato real de horário atual, via `TimeProvider` + o timezone IANA da própria Property, quando configurado — ou uma instrução explícita "timezone não configurado, nunca presuma um" quando não.

### 11.6 `Property.TimeZoneId` — IANA, extensão do comando administrativo já existente

`Property` ganha `TimeZoneId` (string?, nullable, formato IANA) — validado via `TimeZoneInfo.TryFindSystemTimeZoneById` (nunca uma lista mantida manualmente) dentro de `UpdatePropertyCommandHandler`, estendendo o vertical já existente (`UpdatePropertyCommand`/`PropertyResult`/`UpdatePropertyRequest`/`PropertyDetailResponse`) em vez de criar um novo endpoint — preferência explícita do mandato. Omitido mantém o valor atual; `null` explícito remove; um valor inválido falha com `property_timezone_invalid`, sem efeito colateral. Sem backfill para properties existentes. `IPropertyLocalTimeContextReader`/`PropertyLocalTimeContextReader` (novo, AIAgent.Application/Infrastructure) faz um lookup cross-context de dois saltos (Reservation → PropertyId, Property → TimeZoneId) reutilizando os MESMOS dispatchers que `GetPropertyInformationTool` já usa (Exceção 3) — nenhuma nova exceção síncrona.

### 11.7 Rastreamento monetário real — `AgentInteraction.EstimatedCostUsd`/`CostPricingReference`

`AgentInteraction` ganha `EstimatedCostUsd` (decimal?) e `CostPricingReference` (string?) — computados inteiramente em `AnthropicModelProvider`, a partir do uso real de tokens × sua própria configuração de pricing (`AnthropicPricingOptions`, Infrastructure, nunca Domain/Application): `(inputTokens/1_000_000 × inputRate) + (outputTokens/1_000_000 × outputRate)`, com `inputRate=$3`/`outputRate=$15` por milhão de tokens, `reference="claude-sonnet-4-6"` — confirmados como os valores oficiais atuais da Anthropic durante a mesma verificação de documentação do §11.1. `FakeModelProvider` deixa ambos `null` (`null` = não aplicável/não precificado, nunca zero).

### 11.8 Classificação de falha permanente vs. transitória

`ModelProviderException` ganha `IsPermanent` (bool, default `false`) — 400/401/403/modelo-não-suportado são permanentes (nunca reexecutados); 429/5xx/timeout/erro de rede são transitórios, reutilizando exatamente a mesma política de 1 retry já existente desde o CP5 (`GenerateWithRetryAsync`, agora `catch (ModelProviderException ex) when (!ex.IsPermanent)`) — nenhum framework novo, extensão pontual e aditiva do mecanismo já homologado.

### 11.9 Prova real — duas suítes complementares, achado autônomo de credencial

**Suíte estreita** (`AnthropicRealProofTests`, `AIAgent.Tests.Integration`) — mirrors `MetaWhatsAppSandboxProofTests` exatamente: verifica presença local da credencial (User Secrets do `IHostPro.Worker`, id `dotnet-IHostPro.Worker-cc769433-0535-453a-bbdf-17f44d398b0c` — confirmado por evidência direta do próprio código, nunca presumido, após uma orientação anterior questionar o path), nunca `dotnet user-secrets list`, nunca imprime o valor; ausente, imprime o comando exato de configuração e passa trivialmente. Presente, chama a API real diretamente (fora do pipeline completo) e prova, num único teste: seleção real de `GetReservationSummary`, classificação real de human handoff, detecção real de idioma pt-BR/en-US, resistência a um prompt de injeção pedindo a própria API key.

**Suíte de ciclo completo** (`AnthropicRealAgentWorkflowRoundTripTests`, novo arquivo em `IHostPro.Api.Tests.Integration`) — mirrors a infraestrutura de `AIAgentReadToolsWorkflowRoundTripTests`/`AIAgentHumanHandoffWorkflowRoundTripTests` (Postgres/RabbitMQ via Testcontainers, subprocesso real do `IHostPro.Worker`, `WebApplicationFactory<Program>` real do `IHostPro.Api`, webhook assinado real), mas com `AIAgent__ModelProvider=Anthropic` no ambiente do Worker em vez do `Fake` padrão — `Fixture` própria e dedicada (nunca compartilha a Fixture das suítes determinísticas, nunca contamina a regressão de 87 testes). A chave real nunca é lida/impressa/repassada por este processo: com `DOTNET_ENVIRONMENT=Development` setado e o `UserSecretsId` do próprio `IHostPro.Worker.csproj` embutido na assembly, o Generic Host do .NET carrega automaticamente o mesmo User Secrets store já confirmado presente — mecanismo padrão, nunca uma passagem manual de valor. Gated: sem credencial local, `InitializeAsync` retorna imediatamente sem subir nenhum container, e cada `[Fact]` passa trivialmente.

Dois cenários, minimizando chamadas reais (pagas) à Anthropic: **Read Tool completo** (2 chamadas reais — Call#1 seleciona `GetReservationSummary`, execução real da Tool, Call#2 com o resultado real, resposta final real entregue como `Communication.Message` outbound real) e **Human Handoff completo + suspensão** (1 chamada real — classificação real de `human_handoff_requested` → `AgentHumanHandoff` real com `ReasonCode=ExplicitHumanRequest` → `AgentSession.Status=Escalated` → notificação real ao administrador seedado → **uma segunda mensagem inbound na mesma sessão já escalonada, provando `PostEscalationAnthropicCalls=0`** pelo mesmo sinal estrutural já usado pelo teste determinístico do CP6 — `Intent` permanece `null` porque o guard de sessão suspensa intercepta ANTES de `IModelProvider.GenerateAsync` ser chamado, independente do provider configurado). `RealWriteToolProof=false` confirmado — nenhuma Write Tool é oferecida ou executada em nenhum dos dois cenários. **2/2 aprovados**, com evidência real: `InputTokens`/`OutputTokens` > 0, `EstimatedCostUsd` > 0, `CostPricingReference` não-vazio, `ModelProvider="Anthropic"`, `ModelName="claude-sonnet-4-6"` — todos confirmados por asserção direta contra linhas reais persistidas no Postgres real, nunca por inspeção de log.

**Descoberta autônoma de credencial (mandato explícito do usuário)**: por decisão do usuário, a chave foi configurada localmente por ele mesmo (nunca colada no chat, nunca lida via `dotnet user-secrets list`, nunca impressa) — a sessão apenas confirmou presença/ausência via `IConfiguration`, checando: variáveis de ambiente (`AIAgent__Anthropic__Secrets__ApiKey`/`ANTHROPIC_API_KEY` e variantes, escopos Process/User/Machine), o User Secrets store de ambos os `UserSecretsId` existentes na máquina (`IHostPro.Worker`/`IHostPro.Api`), `launchSettings.json` de todo projeto, e confirmou que o `.env` do repositório (bloqueado pelo próprio sandbox, nunca contornado) é estruturalmente irrelevante — nenhum pacote `DotNetEnv`/dotenv é referenciado em `src/Host`. `AnthropicCredentialDiscovery=MISSING` foi o resultado inicial (reportado com `RealAnthropicProof=NOT_EXECUTED_MISSING_LOCAL_SECRET`) até o usuário configurar a chave e autorizar a execução — nenhuma tentativa de contornar o boundary de segurança, nenhum valor jamais visto pela sessão.

### 11.10 Achados corrigidos durante a implementação

- **Path incorreto do User Secrets, corrigido com evidência direta**: uma orientação inicial presumiu `src/Host/IHostPro.Worker` como o projeto correto para `dotnet user-secrets set`; o usuário exigiu confirmação por evidência antes de aceitar. Investigação do próprio código-fonte (`AnthropicRealProofTests.cs`'s `AddUserSecrets(WorkerUserSecretsId)` + `git grep "UserSecretsId" -- '*.csproj'`) confirmou que o literal `dotnet-IHostPro.Worker-cc769433-...` bate exatamente com o `<UserSecretsId>` de `IHostPro.Worker.csproj` (e não com o de `IHostPro.Api`) — a orientação original estava correta, agora com prova, não presunção.
- **Conflito de porta autoinfligido, duas vezes**: o container de desenvolvimento `ihostpro-rabbitmq` (mantido em execução para o `MigrationRunner`) ocupava a porta 5672, colidindo com o RabbitMQ efêmero que cada suíte E2E baseada em Testcontainers tenta subir — reproduzido tanto na regressão completa de 87 testes quanto na nova suíte de ciclo completo deste checkpoint. Corrigido parando o container antes de cada execução E2E e reiniciando-o depois — nenhuma chamada real à Anthropic foi afetada (a falha ocorre antes de qualquer chamada de rede), nunca uma regressão de código.
- **Relatório de um agente de pesquisa com um erro factual, verificado e corrigido antes de qualquer código**: um agente de pesquisa (Explore) usado para levantar a forma exata de `AgentInteraction`/`AgentToolExecution`/`AgentHumanHandoff` reportou incorretamente a ausência dos campos `EstimatedCostUsd`/`CostPricingReference` em `AgentInteraction` — a leitura direta do arquivo-fonte (feita antes de escrever qualquer teste novo) confirmou que ambos os campos já existiam (§11.7, implementados anteriormente nesta mesma sessão). Nenhum código foi escrito com base na informação incorreta do agente — "trust but verify" aplicado.

### 11.11 Testes — contagens exatas

| Suíte | Resultado |
|---|---|
| `IHostPro.Contexts.PropertyManagement.Tests.Unit` (inclui os 6 novos testes de validação/idempotência de `Property.TimeZoneId`) | 208 aprovados |
| `IHostPro.Contexts.PropertyManagement.Tests.Integration` | 207 aprovados |
| `IHostPro.Contexts.AIAgent.Tests.Unit` (inclui os 23 novos testes de `AnthropicModelProviderTests`, HTTP-fake, zero rede real) | 171 aprovados |
| `IHostPro.Contexts.AIAgent.Tests.Integration` (inclui `AnthropicRealProofTests`, executado de fato contra a API real desta vez) | 32 aprovados |
| `IHostPro.Contexts.Configuration.Tests.Unit` | 93 aprovados |
| `IHostPro.Contexts.Configuration.Tests.Integration` (inclui os 2 novos testes de hierarquia de `AI_AGENT_BEHAVIOR`) | 79 aprovados, 1 falha pré-existente e não relacionada (benchmark de invalidação de cache Redis, já documentado como flaky no fechamento do CP5, confirmado passando isoladamente) |
| `IHostPro.Contexts.Communication.Tests.Unit`/`Tests.Integration` (sanity check, sem alteração de código neste checkpoint) | 106 / 37 aprovados (sem regressão) |
| `IHostPro.ArchitectureTests` (novo arquivo `AnthropicModelProviderArchitectureTests`, 5 testes; total 304, de 299) | 304 aprovados |
| `AnthropicRealAgentWorkflowRoundTripTests` (E2E real, novo arquivo — real Read Tool completo, real Human Handoff completo + prova de suspensão) | 2 aprovados (execução dedicada, real, gated) |
| `IHostPro.Api.Tests.Integration` (suíte determinística completa) | ver §11.12 |
| MigrationRunner Run #1/#2 | ver §11.12 |
| Build Release | 0 erro |

### 11.12 Regressão completa e evidência final

| Suíte | Resultado |
|---|---|
| MigrationRunner Run #1 (Postgres/RabbitMQ de desenvolvimento reais) | Exit code 0 — 3 migrations novas aplicadas (`AddPropertyTimeZoneId` em PropertyManagement; `AddAgentInteractionCostTracking` em AI Agent; `AddAiAgentBehaviorPolicyDefinition` em Configuration); nenhuma mudança de topologia RabbitMQ (Anthropic é um cliente REST de saída, nunca um consumer novo) |
| MigrationRunner Run #2 (mesmo banco, imediatamente em seguida) | Exit code 0 — zero drift |
| Verificação de schema pós-Run (read-only, SQL direto via `ihostpro_migrator`) | `property_management.properties.time_zone_id`: nullable, varchar(64); `ai_agent.agent_interactions`: `estimated_cost_usd numeric(12,6)`/`cost_pricing_reference varchar(100)`, ambos nullable; `configuration.policy_definitions`: 3ª linha seedada (`AI_AGENT_BEHAVIOR`, categoria `IA`) |
| `IHostPro.Api.Tests.Integration` (suíte determinística completa, execução limpa — anterior à criação da nova suíte real deste checkpoint, cuja adição é estritamente aditiva e isolada, nunca compartilhando fixture/estado com nenhum dos 87 testes pré-existentes) | 87 aprovados, 0 com falha (29 min 21 s) |
| `AnthropicRealAgentWorkflowRoundTripTests` (execução dedicada, real, dois cenários, própria Fixture isolada) | 2 aprovados, 0 com falha (Read Tool: 6s; Human Handoff + suspensão: 14s) |
| Build Release (solução completa) | 0 erro (20 avisos `NU1903` pré-existentes, SSH.NET, não relacionados a este checkpoint) |
| `git diff --check` | Sem erros (apenas avisos benignos de normalização LF→CRLF) |
| Revisão de segurança sensível (varredura automatizada em toda saída de teste real e no diff completo) | Zero ocorrência de padrão de chave/`Authorization`/`x-api-key`/`Bearer` em qualquer saída de teste real ou no diff; `dotnet user-secrets list` nunca executado; nenhum corpo bruto de request/response Anthropic logado; texto de resposta do modelo (guest-facing) deliberadamente nunca reproduzido em nenhum relatório desta sessão |

### 11.13 Escopo explicitamente não implementado (por decisão do gate, não por omissão)

- `Temperature` no contrato `AI_AGENT_BEHAVIOR` (`TemperatureControl=NOT_SUPPORTED_BY_SELECTED_MODEL`) — removida definitivamente, nunca reintroduzida (§11.1).
- Backend real de secret de Produção (`ProductionAnthropicSecretBackend=false`) — nenhum Key Vault/equivalente existe para nenhum provider desta base de código; fora de escopo.
- SDK oficial/não-oficial da Anthropic (`AnthropicSdk=false`) — REST puro via `IHttpClientFactory`, decisão definitiva do ADR-009.
- Prova real de Write Tool (`RealWriteToolProof=false`) — nenhuma Write Tool foi oferecida ou exercitada contra a API real; fora do escopo aprovado deste checkpoint.
- Group/Condomínio na hierarquia de configuração — permanece deferred desde a Fase 5; `AI_AGENT_BEHAVIOR` segue a mesma hierarquia GLOBAL→TENANT→PROPERTY já soberana.
- Motor de capabilities por provider (`ProviderCapability` abstrato) — decisão explícita do usuário de nunca criar essa abstração só para resolver a incompatibilidade de temperature (§11.1).

`Cp7CommitCount`: registrado no relatório final da conversa de homologação.

## 12. Checkpoint 8 — Final Homologation

**Status:** Concluído. `Phase11MvpCompleted=true`. `ProductionReady=false`. `CorrectiveImplementationNeeded=false`. `CP8ClosureMode=DOCS_ONLY`.

**Objetivo**: auditoria final read-only — nunca a adição de novas features — para determinar se a Fase 11 pode ser declarada definitivamente concluída e homologada no nível MVP, separando rigorosamente MVP blockers, Production blockers, capabilities deferidas, inconsistências documentais e débito técnico não-bloqueante. Zero código, zero migration alterados neste checkpoint.

### 12.1 Metodologia

Revalidação direta contra as fontes de verdade: Documento 16 (Arquitetura do Agente de IA), Documento 06 (Máquina de Estados), Documento 08 (Motor de Configuração e Hierarquia de Regras), Documento 12 (Modelo de Dados Conceitual), Documento 07 (Catálogo de Eventos), Architecture Principles §14, e os próprios documentos de homologação da Fase 9/Fase 10 — lidos integralmente, nunca por memória. Matriz completa dos 29 itens MUST originalmente aprovados no CP0 (AI Gateway, Context Builder, Tools, Anthropic real, multilingual, personalidade/tom, contexto dinâmico, memória de sessão, histórico transacional, RAG, read/write tools, confirmação, integração de policy/workflow, human handoff, auditoria, tracking de tokens/custo, anti-alucinação, prompt dinâmico, fallback/retry, retomada manual, safety, prova real Anthropic) — todos classificados `IMPLEMENTED`, exceto os itens já formalmente deferidos desde gates anteriores (Group/Condomínio, autonomia 0-4, versionamento de prompt, voz/imagem/documento, FAQ estruturada, atribuição humana/chat embutido), nenhum `BLOCKED`.

Verificação direta de código (nunca presumida): catálogo de Tools confirmado por grep contra `AIAgentModuleExtensions.cs` (único site de registro) — exatamente 8 Read Tools + 3 Write Tools aprovadas, zero Tool proibida (`CancelReservation`/`CreatePix`/`RecordGuestCheckedIn`/`RecordGuestCheckedOut`/`CreateWorkflow`/`NotifyFrontDesk`/`RegisterIncident` como Tool — nenhuma existe). RLS/grants das 8 tabelas relevantes da Fase 11 (`ai_agent.*` × 5, `communication.conversations`/`messages`/`administrator_notification_contacts`) reverificados diretamente contra o Postgres real de desenvolvimento: `rls_enabled=true`/`rls_forced=true` em todas, grants `ihostpro_app` uniformemente `INSERT,SELECT,UPDATE`, zero `DELETE` — sem drift desde o CP6/CP7.

### 12.2 Decisão formal — `ReservationResolutionZeroOrMultiple`

O CP5 registrou esta lacuna como `DEFERRED_ARCHITECTURAL_GAP, a ser reavaliado no CP8` — este checkpoint é exatamente essa reavaliação. Hoje (inalterado desde o CP1): uma mensagem inbound cujo telefone resolve 0 ou N Reservation candidatas nunca cria `Conversation`, nunca recebe resposta do AI Agent — apenas logado.

**Decisão final, aprovada pelo usuário: Opção B — não bloqueia o MVP.**

```
ReservationResolutionZeroOrMultiple=DEFERRED_ARCHITECTURAL_GAP
BlocksPhase11Mvp=false
```

Justificativa formal: (1) Documento 16 e Documento 06, lidos integralmente, não exigem atendimento universal de toda mensagem inbound; (2) nenhum dos dois documentos define comportamento obrigatório para zero ou múltiplas reservas candidatas — a própria máquina de estados do Documento 06 §6 não modela nenhum estado de "reserva não resolvida"/"hóspede ambíguo", uma lacuna já existente no desenho conceitual original, não introduzida pela implementação; (3) o comportamento já estava explicitamente registrado desde o CP5 como gap arquitetural deferido, com reavaliação obrigatória marcada para este checkpoint; (4) o MVP do AI Agent permanece deliberadamente reservation-scoped — a automação conversacional inicia quando existe exatamente uma Reservation resolvida; (5) 0/N permanece backlog futuro explícito, nunca uma promessa silenciosa feita hoje. Nenhum fallback novo foi implementado neste checkpoint.

### 12.3 Inconsistências documentais registradas (sem alteração de comportamento, sem requisito novo)

Achados desta auditoria, registrados por transparência — nenhum exige mudança de código, nenhum documento-fonte (Documento 06/08/12/16) foi alterado por este checkpoint (fora do escopo de uma auditoria; qualquer correção aos documentos-fonte originais exigiria seu próprio mandato de aprovação):

- **Temperature** — Documento 08 §10 lista "Temperatura" como dimensão configurável obrigatória do agente IA; o modelo real selecionado (`claude-sonnet-4-6`) rejeita qualquer valor customizado com HTTP 400. Classificação final: `TemperatureControl=NOT_SUPPORTED_BY_SELECTED_MODEL`, `TemperatureSentToAnthropic=false`, `Classification=NOT_APPLICABLE_WITH_SELECTED_PROVIDER_MODEL` — uma exceção documentada e já aprovada (Opção C, CP7) a um requisito que colide com uma restrição técnica real e verificada, nunca uma omissão silenciosa.
- **Group/Condomínio** — Documento 16 §9 menciona 5 camadas de hierarquia (incluindo grupo e condomínio); Documento 08 §7 (a autoridade real do Policy Engine) define apenas 4 (`GLOBAL/TENANT/GRUPO/IMÓVEL`) — Condomínio nunca foi um escopo oficial do Policy Engine. `GroupPromptScope=false`/`CondominiumPromptScope=false` — débito pré-existente da Fase 5 (nenhuma policy da plataforma implementa o escopo GRUPO hoje, em nenhum PolicyCode), não específico da Fase 11, não bloqueante.
- **Prompt versioning** — Documento 12 §20 menciona "prompts" numa lista geral de versionamento de artefatos de configuração; o CP0 já decidiu explicitamente `PromptVersioning=NOT MVP`, reafirmado sem contradição nova.
- **Documento 06, inconsistência interna** — §6 define 8 estados para o sub-fluxo de Atendimento IA; o quadro-resumo do próprio §18 condensa para apenas 4, omitindo "Aguardando Resposta"/"Escalado para Humano"/"Aguardando Informação"/"Encerrado" — inconsistência interna do documento, identificada por este checkpoint, sem relação com a implementação real.

### 12.4 Capabilities deferidas (não-bloqueantes, todas já aprovadas em gates anteriores)

Reservation resolution zero/multiple fallback; Group prompt scope; Condominium prompt scope; autonomia por níveis 0-4 (Documento 08 §11); versionamento de prompt; voz; imagem; documento; FAQ estruturada; Wi-Fi/estacionamento/regras estruturadas; atribuição de atendentes humanos; chat embutido entre hóspede e atendente; orquestração baseada em Workflow (nunca necessária — suspender é domínio do próprio AI Agent, notificar é reação do próprio Communication).

### 12.5 Production blockers (preservados explicitamente, nunca convertidos em MVP blocker)

| Flag | Valor | Origem |
|---|---|---|
| `ProductionAnthropicSecretBackend` | `false` | CP7 — nenhum backend real de secret existe para nenhum provider desta base |
| `MetaAppPublished` | `false` | Fase 9 — herdado, não reaberto |
| `RealDeliveredWebhookProof` | `false` | Fase 9 — herdado, não reaberto |
| `WolverineClusterAgentAssignmentDebt` | `true` | Fase 9/10 — débito técnico transversal, destino Fase 12 (Hardening); manifestação plausível observada durante shutdown do Worker no E2E real do CP7 (`durability agent` para `dashboard_messaging`), sem causar nenhuma falha de teste |
| `ProductionContextBudgetStrategyRequired` | `true` | CP7 — `ContextWindowStrategy=FULL_CURRENT_CONVERSATION_CP7_MVP` foi decisão explícita do mandato; sem estratégia de truncamento para conversas longas em escala real |
| `PricingConfigStaleness` | `ProductionOperationalDebt` | CP7 — pricing hardcoded, referência explícita e auditável hoje, mas sem atualização automática |

Todos: `BlocksPhase11Mvp=false`, `BlocksProductionReady=true`.

### 12.6 Evidência reutilizada (nenhuma repetição desnecessária)

Fechamento docs-only — nenhum código foi alterado, então nenhuma regressão nova foi executada. Evidência já obtida no CP7, reutilizada integralmente: `IHostPro.Api.Tests.Integration`=87/87; `IHostPro.ArchitectureTests`=304/304; `AnthropicRealProofTests`=1/1 (real); `AnthropicRealAgentWorkflowRoundTripTests`=2/2 (real); Release build=0 erro; `git diff --check`=limpo; revisão de dados sensíveis=limpa. RLS/grants das 8 tabelas relevantes reverificados frescos nesta auditoria (§12.1), sem drift. Nenhuma migration nova — `MigrationRunner` não reexecutado (nenhuma alteração de schema neste checkpoint).

### 12.7 Fechamento formal da Fase 11

```
Phase11MvpCompleted=true
AnthropicIntegrated=true
AnthropicModel=claude-sonnet-4-6
RealAnthropicProof=true
ExternalLLMNetworkCalls>0
ConversationalOrchestrationImplemented=true
ReadToolsImplemented=true
BusinessWriteToolsImplemented=true
WriteConfirmationImplemented=true
HumanHandoffImplemented=true
MonetaryCostTracking=true
MultilingualImplemented=true
StructuredRagImplemented=true
VectorDatabase=false
Embeddings=false
RealWriteToolProof=false
BlocksPhase11MvpDueToRealWriteToolProof=false
ReservationResolutionZeroOrMultiple=DEFERRED_ARCHITECTURAL_GAP
BlocksPhase11MvpDueToReservationResolutionZeroOrMultiple=false
GroupPromptScope=false
CondominiumPromptScope=false
BlocksPhase11MvpDueToPromptHierarchy=false
PromptVersioning=false
BlocksPhase11MvpDueToPromptVersioning=false
HumanAssignmentImplemented=false
EmbeddedHumanChatImplemented=false
BlocksPhase11MvpDueToHumanOperations=false
ProductionReady=false
```

**Fase 11 — Agente de IA e Experiência Conversacional = DEFINITIVAMENTE CONCLUÍDA E HOMOLOGADA NO NÍVEL MVP, COM BLOCKERS DE PRODUCTION DOCUMENTADOS.**

`Cp8CommitCount`: registrado no relatório final da conversa de homologação.
