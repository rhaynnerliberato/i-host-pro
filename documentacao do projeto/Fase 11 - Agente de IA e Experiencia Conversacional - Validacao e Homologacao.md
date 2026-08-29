# Fase 11 — Agente de IA e Experiência Conversacional — Validação e Homologação

Versão: 1.0
Status: Em andamento — Checkpoint 1 concluído

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
CP1 (concluído, este documento) — Inbound Conversation Foundation
CP2 — AI Agent Foundation
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
