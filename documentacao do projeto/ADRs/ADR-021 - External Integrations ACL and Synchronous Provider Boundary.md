# ADR-021 — External Integrations ACL and Synchronous Provider Boundary

Status: Aceito
Data: 2026-08-19

## Contexto

A Fase 9, Checkpoint 2.0 (auditoria read-only do WhatsApp real) confirmou que `Architecture Principles.md` §17, item 2 e `Documento 19` §5 já mandatam, em documentação previamente aprovada, que todo novo Connector externo (WhatsApp, Airbnb, PIX) deve viver exclusivamente no Bounded Context **External Integrations** — hoje inexistente no código (zero scaffolding confirmado por busca direta na solução). O Checkpoint 1 manteve deliberadamente `IOutboundMessageConnector`/`FakeWhatsAppConnector` dentro de `Communication.Application`/`Communication.Infrastructure`, por decisão explícita do próprio mandato do CP1 (generalizar prematuramente com um único connector fake seria especulativo).

Ao planejar o Checkpoint 2.1 (External Integrations + Credential/Configuration Foundation), um segundo conflito documental real foi encontrado, distinto do anterior, **antes de qualquer scaffold ser criado**:

- `Architecture Principles.md` §17, item 2 cita `ExternalIntegrations.Abstractions.IMessagingProvider` como uma interface que um novo Connector deve implementar — pressupondo a existência de um projeto `Abstractions` cross-context-referenciável.
- `Architecture Principles.md` §13 (Regras para Contracts) é categórico e fechado: *"[`<Contexto>.Contracts`] é o único projeto de um contexto que outros contextos podem referenciar diretamente. Nunca se referencia `Domain`, `Application` ou `Infrastructure` de outro contexto."* — sem exceção nomeada para um segundo tipo de projeto público.
- `Architecture Principles.md` §12 (Regras para BuildingBlocks) exclui explicitamente "clientes de integrações externas" de qualquer elegibilidade a `BuildingBlocks` — fechando também essa rota para `IMessagingProvider`.
- `Architecture Principles.md` §14 (Regras de Comunicação Entre Contextos) define a regra geral de comunicação assíncrona via Integration Events, com exatamente 4 exceções síncronas nomeadas e fechadas (Identity & Access, Configuration & Policy — ADR-002; Reservations → Property Management, elegibilidade — ADR-014; Communication → Reservations, contato do hóspede — ADR-019). External Integrations não constava nessa lista.
- Busca direta no código confirmou que o único projeto `*.Abstractions` já existente na solução é `BuildingBlocks.Messaging.Abstractions` — tier BuildingBlocks, sujeito aos 5 critérios do §12, que `IMessagingProvider` não cumpre (vocabulário de negócio de mensageria/integração externa; uso provado em apenas 1 contexto real até agora, não 3+). Não existe nenhum precedente de projeto `Abstractions` por-BC cross-referenciável fora de `BuildingBlocks`.

A implementação foi interrompida (`PARE`) antes de qualquer scaffold, e o conflito foi reportado ao usuário em vez de resolvido silenciosamente — conforme as regras de engenharia do projeto exigem quando duas instruções/documentos entram em conflito sem prioridade expressa.

## Decisão

**Rejeitado**: criar `IHostPro.Contexts.ExternalIntegrations.Abstractions` como um novo tipo de projeto cross-context-referenciável. Rejeitado também qualquer mecanismo de composição que escondesse essa dependência (`Func<>`/delegate customizado, service locator, reflection dinâmica no Host) — a relação entre os dois Bounded Contexts deve ser explícita e visível em tempo de compilação, nunca mascarada.

**Aprovado**: a superfície pública provider-neutral de External Integrations (`IMessagingProvider` e seus tipos de request/result) é publicada em **`IHostPro.Contexts.ExternalIntegrations.Contracts`** — o mesmo e único tipo de projeto que qualquer outro Bounded Context já usa para ser referenciado externamente (`Architecture Principles.md` §13, regra preservada sem exceção estrutural nova). `Communication.Application` pode referenciar `ExternalIntegrations.Contracts` — nunca `ExternalIntegrations.Domain`/`Application`/`Infrastructure`/`Api`. `ExternalIntegrations.Infrastructure` implementa o contrato definido em `ExternalIntegrations.Contracts` — nunca referencia `Communication.Application`/`Domain`/`Infrastructure` (nenhuma dependência invertida escondida). A ligação concreta (`IMessagingProvider` → implementação real) é registrada exclusivamente no `Host` (`IHostPro.Worker`/`IHostPro.Api`), que já tem permissão arquitetural de referenciar qualquer módulo para fins de composição — sem lógica de negócio no Host.

Isso preserva `Contracts` como o único tipo de projeto publicamente referenciável por-BC (§13 intacto), mas **estende seu conteúdo além de Integration Events**: `ExternalIntegrations.Contracts` publica tanto Integration Events futuros (§16 do Documento 07 — `WhatsAppMessageReceived`/`WhatsAppMessageSent`/`WhatsAppWebhookFailed`, quando implementados) quanto uma interface síncrona provider-neutral (`IMessagingProvider`) — uma extensão de propósito explicitamente registrada aqui, nunca uma segunda categoria de projeto.

### A quinta exceção síncrona (Architecture Principles §14)

A chamada `Communication.Application` → `ExternalIntegrations.Contracts.IMessagingProvider` constitui, de fato, uma nova comunicação síncrona cross-context — não deve ser escondida atrás de linguagem que sugira que é "apenas" uma referência de projeto inofensiva. É formalmente registrada como a **quinta exceção síncrona** de `Architecture Principles.md` §14, ao lado de Identity & Access, Configuration & Policy, Reservations→Property Management (ADR-014) e Communication→Reservations (ADR-019).

**Justificativa da exceção**: o envio outbound de WhatsApp precisa transportar, para o provider, `Destination` (telefone do hóspede) e `RenderedContent` (corpo da mensagem já processado) — ambos PII/dados de negócio sensíveis — e precisar receber de volta, de forma síncrona, o resultado imediato da tentativa (aceite/rejeição, `ProviderMessageId`, classificação de erro) para que `Message` (Communication.Domain) decida corretamente sua própria transição de estado (`MarkSent`/`MarkFailed`). Modelar isso como um Integration Event assíncrono (solicitação de envio publicada no RabbitMQ, consumida por External Integrations, resposta publicada de volta) exigiria colocar telefone e corpo de mensagem no broker apenas para atravessar a fronteira entre dois Bounded Contexts que já rodam no mesmo processo (`IHostPro.Worker`) — rejeitado explicitamente por essa razão (ver `Documentation chronology` no relatório de fechamento do CP2.1 para o registro completo da decisão).

A exceção é **estreita e purpose-limited**, mesmo precedente estrito já usado por ADR-014/ADR-019 — não generaliza automaticamente para nenhum outro par de Bounded Contexts, nem para nenhuma outra finalidade dentro do próprio par Communication/External Integrations:

1. External Integrations continua sendo o único owner de connectors externos (Documento 19 §5, Documento 05 §19, `Architecture Principles.md` §17 — decisão já aprovada, não reaberta aqui).
2. Communication nunca chama a API da Meta (ou de qualquer provider) diretamente — apenas o contrato provider-neutral.
3. Communication só referencia `ExternalIntegrations.Contracts` — nunca `Domain`/`Application`/`Infrastructure`/`Api` de External Integrations.
4. `ExternalIntegrations.Contracts` é a única superfície pública deste Bounded Context, exatamente como todo outro contexto já opera.
5. `IMessagingProvider` é deliberadamente provider-neutral — nenhum tipo/nome/conceito específico de um provider real (Meta, Twilio, ou qualquer outro) pode aparecer nessa interface ou em seus DTOs de request/result.
6. DTOs específicos de provider (payload da Graph API, envelope de erro da Meta, etc.) ficam exclusivamente em `ExternalIntegrations.Infrastructure` — nunca vazam para `ExternalIntegrations.Contracts`/`Domain`/`Application`, nem para qualquer parte de Communication.
7. Esta não é uma implementação de comando cross-context — **ADR-018 permanece intacta e não é reaberta**: Workflow Orchestration continua sendo o único Bounded Context autorizado a enviar comandos explícitos via Wolverine `Send`. A chamada aqui é uma dependência de DI in-process comum, nunca transportada por RabbitMQ/Wolverine.
8. Nenhuma mensagem/telefone/corpo de mensagem é transportado via RabbitMQ apenas para atravessar esta fronteira — a chamada é 100% in-process.
9. O `Host` é responsável exclusivamente pela composição/DI (registro de `IMessagingProvider` → implementação concreta) — zero lógica de negócio no Host.
10. Eventos futuros de status de entrega (`Recebida`/`Lida`/`Falhou`, quando o webhook real existir) continuam **assíncronos e PII-safe** — nunca reabertos por esta ADR para carregar telefone/corpo de mensagem (ver `Documento 07` §10 vs. §16, a mesma separação ACL já catalogada: `MessageDelivered`/`MessageRead`/`MessageFailed` são eventos do próprio agregado `Message` em Communication; `WhatsAppMessageReceived`/`WhatsAppMessageSent`/`WhatsAppWebhookFailed` são eventos de External Integrations, contendo apenas identificadores provider-neutros — `TenantId`, `MessageId`/`ProviderMessageId`, status, código de erro, timestamp/correlation — nunca telefone/corpo/secret/payload bruto do webhook).
11. Nenhum outro Bounded Context ganha, por esta ADR, o direito de chamar `ExternalIntegrations.Contracts` de forma síncrona — a autorização é estritamente Communication → External Integrations, nomeada, não genérica.
12. Esta ADR não cria autorização genérica para novas leituras/comandos síncronos entre quaisquer outros pares de Bounded Contexts — cada futura necessidade equivalente exige sua própria ADR, nos mesmos termos estritos já estabelecidos por ADR-014/ADR-018/ADR-019.

### Correção de `Architecture Principles.md` §17

O texto atual de §17, item 2, referencia `ExternalIntegrations.Abstractions` (`ex.: IReservationProvider, IMessagingProvider, IPaymentProvider`) — uma inconsistência documental pré-existente com §13, nunca antes detectada porque nenhum Connector real havia sido implementado até este Checkpoint. Corrigido para `ExternalIntegrations.Contracts`. A correção:

- Reconcilia §17 com §13/§14, sem introduzir nenhuma categoria pública de projeto nova.
- Não altera ownership (External Integrations continua o único owner de Connectors externos).
- Não cria nenhuma capacidade nova além da quinta exceção síncrona já registrada nesta ADR.
- Remove uma inconsistência documental pré-existente, nunca antes exercitada por código real.

## Alternativas Consideradas

- **Criar `ExternalIntegrations.Abstractions` como um novo tipo de projeto cross-context-referenciável** (o que o texto literal do §17 original pressupunha): rejeitada — criaria uma segunda categoria de "superfície pública" por Bounded Context, nunca antes usada por nenhum outro contexto da plataforma, sem justificativa que não pudesse ser satisfeita por `Contracts`, e sem precedente real na solução (o único projeto `*.Abstractions` existente, `BuildingBlocks.Messaging.Abstractions`, é BuildingBlocks-tier, categoria que §12 já exclui explicitamente para "clientes de integrações externas").
- **Manter a interface em `Communication.Application` (como hoje, `IOutboundMessageConnector`) e fazer `ExternalIntegrations.Infrastructure` referenciá-la para implementá-la**: rejeitada — apenas inverte a direção da mesma violação de §13 (Infrastructure de um Bounded Context referenciando Application de outro), sem resolver o problema estrutural.
- **Mascarar a dependência via `Func<>`/delegate customizado ou service locator registrado no Host**: rejeitada explicitamente pelo usuário — esconderia uma dependência cross-context real atrás de indireção, tornando a fronteira arquitetural menos visível e mais difícil de auditar/testar por `ArchitectureTests`, exatamente o oposto do objetivo de um ACL bem definido.
- **Modelar como Integration Event assíncrono (Communication publica solicitação de envio, External Integrations reage e publica resposta)**: rejeitada para este caso específico — exigiria transportar `Destination`/`RenderedContent` (PII/dados sensíveis) pelo RabbitMQ apenas para atravessar uma fronteira entre dois Bounded Contexts que já compartilham o mesmo processo, e impediria `Message` de decidir sua transição de estado de forma síncrona e imediata após a tentativa de envio.
- **Tratar `IMessagingProvider` como candidato a `BuildingBlocks`**: rejeitada — `Architecture Principles.md` §12 exclui explicitamente "clientes de integrações externas" da elegibilidade a `BuildingBlocks`, e o componente carrega vocabulário de negócio (mensageria/integração externa) e ainda não tem uso provado em 3+ Bounded Contexts (critérios 1 e 2 do §12, ambos reprovados).

## Consequências

### Positivas
- Preserva a regra fechada de §13 (`Contracts` como única superfície pública por-BC) sem exceção estrutural — apenas estende o propósito do conteúdo publicável em `Contracts`, de forma explícita e documentada.
- A dependência cross-context é visível em tempo de compilação (referência de projeto real, `Communication.Application.csproj` → `ExternalIntegrations.Contracts.csproj`), auditável por `ArchitectureTests`, nunca escondida atrás de indireção.
- Reconcilia formalmente §17 com §12/§13/§14 — remove uma inconsistência documental que já existia antes deste Checkpoint, nunca exercitada por código real até agora.
- Mantém `ExternalIntegrations.Infrastructure` livre de qualquer dependência inversa em `Communication` — o ACL funciona nos dois sentidos (Communication não conhece Meta; External Integrations não conhece o Domain de Communication).
- A exceção síncrona é nomeada, estreita e não generaliza — mesma disciplina já demonstrada por ADR-014/ADR-018/ADR-019.

### Riscos Aceitos
- `ExternalIntegrations.Contracts` passa a ter dois propósitos distintos (Integration Events publicados + uma interface síncrona provider-neutral) — uma extensão de escopo em relação ao texto original de §13 ("contendo apenas os Integration Events que ele publica"), registrada aqui como uma decisão explícita, não uma violação silenciosa. Se um sexto Bounded Context precisar do mesmo padrão (contrato síncrono + eventos no mesmo `Contracts`), essa generalização deve ser reavaliada then, não assumida agora.
- Um quinto par de Bounded Contexts com comunicação síncrona aumenta ligeiramente a superfície de exceções ao modelo assíncrono padrão da plataforma — mitigado pelo mesmo argumento já aceito para as quatro exceções anteriores: cada uma resolve uma necessidade real e não composicional (resolução em tempo de requisição, ou aqui, PII que não deve transitar pelo broker).

## Correção pós-publicação (Checkpoint 2.1.1)

Não é uma nova decisão arquitetural — apenas um registro do mecanismo de auditoria já esperado pelo mandato do CP2.1 (Documento 17 §28-proporcional) e implementado nesta correção: alterações administrativas na configuração de `WhatsAppIntegration` (`ConfigureWhatsAppIntegrationCommand`) são auditadas via structured Application logging (`ILogger<T>`), mesmo padrão já usado por `Workflow.Application`/`Identity.Application` — nenhuma persistência de auditoria nova, nenhum secret ou PII no log. Ver `Fase 9 - Comunicacao e Integracoes do MVP - Validacao e Homologacao.md` §10 para o registro completo.

## Nota pós-publicação (Checkpoint 2.2)

Não é uma nova decisão arquitetural — a fronteira em si (Communication referencia exclusivamente `ExternalIntegrations.Contracts`, chamada síncrona in-process, nunca via broker) permanece exatamente como decidida acima. O Checkpoint 2.2 fez uma mudança focada e previamente autorizada (mandato §22/§23) no *conteúdo* de `OutboundMessageRequest` — `RenderedContent` foi substituído por `TemplateKey`/`TemplateVariables` estruturados, para que o provider real (Meta) nunca precise reconstruir parâmetros a partir de texto já renderizado — e adicionou `ProviderFailureCategory.DeliveryOutcomeUnknown`. Ambas são extensões de conteúdo dentro do mesmo propósito já registrado no "Risco Aceito" acima (`Contracts` com escopo estendido), não uma nova exceção síncrona nem uma mudança de fronteira. Ver `Fase 9 - Comunicacao e Integracoes do MVP - Validacao e Homologacao.md` §11 para o registro completo da implementação real (`MetaWhatsAppMessagingProvider`, `WhatsAppTemplateMapping`, `ProviderMessageId`).

## Referências
- `documentacao do projeto/Architecture Principles.md`, Seções 12, 13, 14, 17 (todas reconciliadas por esta ADR)
- `Documento 05` §19, `Documento 19` §5 (mandato pré-existente: Connectors externos vivem em External Integrations)
- `Documento 07` §10 (Eventos da Comunicação) vs. §16 (Eventos das Integrações) — a separação ACL de eventos já catalogada, preservada por esta ADR
- ADR-002 (exceções síncronas originais — Identity & Access, Configuration & Policy)
- ADR-014 (Exceção Síncrona Reservations para Property Management — precedente de exceção estrita e nomeada)
- ADR-018 (Workflow-issued Cross-context Commands — confirmado intacto, não reaberto por esta ADR)
- ADR-019 (Purpose-limited Reservation Guest Contact Read for Communication — precedente direto de exceção síncrona purpose-limited)
- `Fase 9 - Comunicacao e Integracoes do MVP - Validacao e Homologacao.md`, Checkpoint 2.0 (auditoria que originou o mandato de External Integrations) e Checkpoint 2.1 (esta correção)
