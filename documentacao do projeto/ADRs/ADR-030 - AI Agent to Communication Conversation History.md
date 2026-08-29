# ADR-030 — AI Agent to Communication Conversation History

Status: Aceito
Data: 2026-08-29

## Contexto

Fase 11, Checkpoint 2 ("AI Agent Foundation") exige que o novo Bounded Context AI Agent, ao processar um `ConversationMessageReceived`, construa um contexto mínimo de conversa antes de chamar o `IModelProvider` — o que inclui o histórico sanitizado das mensagens já trocadas naquela `Conversation`. `Conversation`/`Message` são propriedade exclusiva de Communication (Documento 12 §9/§10); AI Agent nunca acessa `CommunicationDbContext`, nunca referencia `Communication.Domain`/`Communication.Infrastructure` diretamente.

`Architecture Principles.md` §14 já registra treze exceções síncronas nomeadas e estritas. A Exceção 3 (AI Agent) já existente é analiticamente distinta: cobre Tools — adapters finos que invocam o Application Service público de outro contexto para **executar** uma capability explícita (mesmo mecanismo de despacho de comando já usado por Workflow Orchestration, Exceção 2). O que este checkpoint precisa é uma **leitura** purpose-limited de um dado já existente — o mesmo padrão já usado pelas nove exceções de leitura (4/5/7/8/9/11/12/13), cada uma nomeada e numerada individualmente mesmo quando repete o par de Bounded Contexts (ADR-028 não é extensão de ADR-026; ADR-029 não é extensão de ADR-019). Auditado antes de codificar (mandato do CP2, item 10): esta é uma exceção **nova**, não uma extensão informal da Exceção 3.

## Decisão

Está aprovada uma décima quarta exceção síncrona, estrita e específica: **AI Agent pode consultar Communication exclusivamente para obter o histórico sanitizado de mensagens de uma Conversation** — nunca para qualquer outra finalidade, nunca para escrever.

A exceção obedece obrigatoriamente a:

1. **Contrato público em `Communication.Contracts`** — `IConversationHistoryReader` e `ConversationHistoryMessage`/`ConversationMessageDirection`, mirroring a forma de todos os readers anteriores (ADR-019/024/026/027/028/029).
2. **Implementação somente em `Communication.Infrastructure`** — `ConversationHistoryReader`, único implementador permitido.
3. **AI Agent não referencia** `Communication.Domain`, `Communication.Application`, `Communication.Infrastructure` ou `CommunicationDbContext`/o schema `communication` diretamente — apenas `Communication.Contracts`. Regra validada automaticamente por `IHostPro.ArchitectureTests` (NetArchTest).
4. **Entrada mínima**: `TenantId`, `ConversationId` — ambos resolvidos pelo backend (a partir de `ConversationMessageReceived`/`AgentSession`), nunca informados por um JWT/service-account do hóspede.
5. **Resposta mínima**: uma lista de `ConversationHistoryMessage(MessageId, Direction, Content, OccurredAtUtc)` — nunca o agregado `Message`, nunca uma referência a `Reservation`, nunca `ProviderMessageId`/status/destino/motivo de falha, nunca `GuestPhone`, nunca `AccessCredential`/`AccessCredentialSecretReference`, nunca payload de QR PIX, nunca metadado de provider.
6. **Operação somente leitura** — `GetHistoryAsync` nunca modifica estado de Communication.
7. **Ordem cronológica determinística**: `CreatedAtUtc` ascendente, com `MessageId` como tie-breaker — nunca depende da ordem natural de retorno do banco.
8. **Segurança de conteúdo sensível**:
   - Uma mensagem cujo `RenderedContent` persistido já é o marcador fixo `"[SENSITIVE CONTENT REDACTED]"` (entrega de credencial de acesso, ADR-028) é retornada exatamente como esse marcador — nunca reconstruída, porque o conteúdo real nunca foi persistido em primeiro lugar.
   - Uma mensagem de entrega de cobrança PIX (`TemplateKey = "LATE_CHECKOUT_PIX_PAYMENT"`, ADR-025/ADR-027) **é diferente**: por decisão já homologada da Fase 10, o payload real do QR/copia-e-cola é renderizado diretamente em `RenderedContent` (esse é o destino final pretendido do dado, para o hóspede ler/escanear). Como isso jamais pode alcançar o AI Agent, `ConversationHistoryReader` — o lado de LEITURA, nunca o de escrita já homologado da Fase 10 — substitui esse conteúdo pelo mesmo marcador fixo antes de retornar. Nenhuma mudança foi feita à persistência já aprovada da Fase 10.
   - Texto de mensagem inbound do hóspede pode ser retornado integralmente.
   - Texto de mensagem outbound não-sensível pode ser retornado integralmente.
9. **Tenant-scoped, RLS, fail-closed** — a implementação abre sua própria transação curta, somente leitura, com `SET LOCAL app.tenant_id` explícito para o `tenantId` informado pelo chamador (mesmo mecanismo de `TenantAwareTransactionScope` já usado por todos os readers anteriores) — nunca `IgnoreQueryFilters`, nunca um papel com `BYPASSRLS`. AI Agent nunca pode ler o histórico de outro tenant.
10. **`Purpose-limited`, não uma exceção geral de leitura cross-context** — esta ADR autoriza exatamente um consumidor (AI Agent) e exatamente um propósito (histórico sanitizado de conversa). Não autoriza nenhum outro Bounded Context a consultar este reader, nem autoriza AI Agent a consultar qualquer outro dado de Communication.
11. **Não cria precedente geral para leitura cross-context de Communication** — cada futura necessidade de leitura síncrona de outro dado desta Bounded Context exige sua própria decisão, nomeada e estrita, nos mesmos termos desta.
12. **Retorna lista vazia quando a Conversation não existe ou não tem mensagens** — indistinguível por desenho, mesma convenção de ADR-014/019/026/027/028/029.
13. **Restrição de referência verificada por arquitetura**: um `ArchitectureTest` dedicado prova que `IConversationHistoryReader` é referenciado exclusivamente pelos assemblies `AIAgent.Application`/`AIAgent.Infrastructure` — nenhum outro Bounded Context pode passar a usá-lo silenciosamente no futuro sem que o teste falhe e force uma nova decisão.
14. **Eventual separação física dos contextos deve preservar o mesmo contrato** — mesma cláusula de ADR-014/019/024/026/027/028/029: se Communication for extraído para um serviço separado, `IConversationHistoryReader` se torna uma chamada de rede com a mesma assinatura mínima.

## Alternativas Consideradas

- **Estender a Exceção 3 (AI Agent → Application Services de outros contextos) para cobrir também esta leitura**: rejeitada — Exceção 3 é sobre Tools **executando** capabilities explícitas via Application Service, nunca uma leitura dedicada e minimalista; misturar os dois obscureceria o propósito estrito de cada uma, mesmo racional que já levou ADR-028/029 a não estenderem exceções de leitura pré-existentes com o mesmo par de contextos.
- **Publicar o conteúdo completo da Conversation em `ConversationMessageReceived` ou em um novo evento**: rejeitada — o histórico precisa refletir o estado ATUAL no momento do processamento (mensagens adicionais podem já existir), incompatível com um snapshot assíncrono; mesmo racional que já justifica todo reader síncrono desta plataforma (Documento 04 §6).
- **AI Agent manter sua própria cópia/projeção do histórico, alimentada por `ConversationMessageReceived`**: rejeitada — Communication é a dona do dado `Message`; duplicar criaria uma segunda fonte de verdade sujeita a divergência e reintroduziria o mesmo risco de conteúdo sensível em dois lugares.
- **Redigir o payload do PIX na escrita (Fase 10) em vez de na leitura (este checkpoint)**: rejeitada — mudaria o comportamento já homologado da Fase 10 (o hóspede precisa do QR real na mensagem que recebe) sem necessidade; a redação no boundary de leitura do AI Agent é suficiente e não exige reabrir uma decisão já aprovada.

## Consequências

### Positivas
- Resolve a necessidade real do Checkpoint 2 sem inventar um novo Bounded Context, sem reabrir a Exceção 3, e sem duplicar dados de Communication em AI Agent.
- Mantém a superfície de acoplamento mínima, nomeada e testável por arquitetura — mesmo padrão já testado e homologado de ADR-014/019/024/026/027/028/029.
- Descobre e corrige, no próprio boundary de leitura, um caso real de conteúdo sensível (QR PIX) que a Fase 10 legitimamente persiste em texto claro para fins de entrega ao hóspede — provado por teste de integração dedicado.

### Riscos Aceitos
- `Architecture Principles.md` §14 passa a ter catorze exceções nomeadas.
- A lista de `TemplateKey`s sensíveis (`LATE_CHECKOUT_PIX_PAYMENT`) é mantida como literal duplicado dentro de `ConversationHistoryReader`, não uma configuração centralizada — aceitável no volume atual (dois casos: credencial já redigida na escrita, PIX redigido na leitura); se um terceiro tipo sensível surgir, promover a uma lista/configuração compartilhada é decisão futura.
- Uma janela de TOCTOU trivial existe entre a leitura do histórico e a chamada ao `IModelProvider` (uma mensagem poderia, em tese, ser adicionada nesse intervalo) — aceita nos mesmos termos já estabelecidos para toda leitura síncrona cross-context desta plataforma.

## Referências
- `documentacao do projeto/Architecture Principles.md`, Seção 14 (décima quarta exceção)
- ADR-019/024/026/027/028/029 — precedente estrutural direto do padrão "reader" purpose-limited
- ADR-025/ADR-027 (PIX QR renderizado em `Message.RenderedContent`) — motivo da redação no lado de leitura
- ADR-028 (marcador `"[SENSITIVE CONTENT REDACTED]"` já usado na escrita para credencial de acesso)
- `Fase 11 - Agente de IA e Experiencia Conversacional - Validacao e Homologacao.md`, Checkpoint 2 (AI Agent Foundation)
- `IConversationHistoryReader.cs`, `ConversationHistoryMessage.cs`, `ConversationHistoryReader.cs`
