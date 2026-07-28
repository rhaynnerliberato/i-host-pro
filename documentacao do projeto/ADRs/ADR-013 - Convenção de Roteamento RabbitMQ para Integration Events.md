# ADR-013 — Convenção de Roteamento RabbitMQ para Integration Events

Status: Aceito
Data: 2026-07-28

## Contexto

A ADR-004 já decidiu Wolverine + RabbitMQ como backbone de mensageria, e a Etapa 15A (Incremento 2) implementou a fundação do outbox durável do contexto Identity & Access, usando um único exchange (`identity-events-test`) exclusivamente para um evento canário de teste — deliberadamente descartado como convenção real (comentário explícito no código: "Real per-event routing is a decision for when the six events are actually implemented").

Nenhum documento do projeto (`Architecture Principles.md` §8/11/13, Documento 07, ADR-004) definia até agora uma convenção de nomenclatura de exchange, routing key ou versionamento no transporte para o roteamento real de Integration Events via RabbitMQ. A Etapa 15 (implementação dos seis primeiros eventos reais do contexto Identity & Access — `UserLoggedIn`, `LoginFailed`, `AccountLockedOut`, `UserLoggedOut`, `RefreshTokenReuseDetected`, `SessionRevoked`) exigiu essa decisão antes de poder registrar o roteamento em `Program.cs`. Apresentada como proposta ao usuário antes de qualquer nome ser escolhido, e aprovada durante o planejamento da Etapa 15.

## Decisão

- **Um exchange do tipo `topic` por Bounded Context**, nomeado `<contexto-em-kebab-case>-events` (ex.: `identity-events` para Identity & Access) — não um exchange por evento, nem um único exchange compartilhado por toda a plataforma. Cada contexto futuro que publicar Integration Events cria seu próprio exchange seguindo este padrão, no momento em que implementar seu primeiro evento real.
- **Routing key = nome do evento em `snake_case`** (ex.: `UserLoggedIn` → `user_logged_in`), publicado via `Wolverine.RabbitMQ`'s `ToRabbitRoutingKey(exchangeName, routingKey, exchange => exchange.ExchangeType = ExchangeType.Topic)`. Um exchange `topic` (em vez de `fanout`) permite que futuros consumidores façam bind seletivo por padrão de routing key (ex.: `session_*`, `*`), sem exigir um exchange dedicado por evento à medida que o catálogo cresce (Documento 07 já prevê 16+ categorias de eventos para módulos futuros).
- **Todo roteamento usa `.UseDurableOutbox()` explicitamente** — confirmado empiricamente na Etapa 15A que, sem essa chamada, o Wolverine cai no modo de envio "Inline" por padrão, que não usa o outbox persistente: uma indisponibilidade do broker faz a mensagem ser descartada após poucas tentativas síncronas em vez de cair para persistência e relay durável.
- **Versionamento vive no nome do tipo do evento, nunca na routing key ou no exchange** (Documento 07 §19, Architecture Principles §13): uma mudança de estrutura incompatível cria um novo tipo (`UserLoggedInV2`) com sua própria routing key (`user_logged_in_v2`), publicado no mesmo exchange do contexto. O campo `Version` já existente no envelope `IntegrationEvent` (`BuildingBlocks.Messaging.Abstractions`) permanece a fonte de verdade para o número de versão dentro do payload; o sufixo no nome do tipo/routing key é o que permite a um consumidor (ou ao próprio roteamento) distinguir as duas formas sem inspecionar o corpo da mensagem.
- Esta convenção aplica-se a todo Bounded Context futuro que publique Integration Events via RabbitMQ, não apenas a Identity & Access.

## Alternativas Consideradas

- **Um exchange por evento** (ex.: `identity.user-logged-in`, `identity.login-failed`, ...): mais isolamento por evento individualmente, mas não escala com o crescimento do catálogo (Documento 07 já cataloga eventos de 16 domínios futuros) e diverge do único precedente já existente no código (exchange único por contexto, usado no canário de teste da Etapa 15A). Descartada.
- **Um único exchange para toda a plataforma** (ex.: `ihostpro-events`, routing key prefixada por contexto): centraliza o roteamento de todos os Bounded Contexts em um único ponto de configuração, mas acopla contextos que devem permanecer independentes (Architecture Principles §18 — extração futura para microsserviços deve ser possível por contexto, sem redesenhar o Event Bus de outro contexto). Descartada.
- **Fanout em vez de topic:** mais simples (sem routing key), mas exige um exchange por evento para permitir bind seletivo futuro — reintroduz o problema da primeira alternativa descartada.

## Consequências

### Positivas
- Convenção única, documentada, aplicável a todo contexto futuro sem nova decisão arquitetural.
- Consumidores futuros (nenhum existe ainda neste incremento — `IHostPro.Worker` não consome nenhum destes seis eventos) podem fazer bind seletivo por padrão de routing key sem exigir mudança no lado da publicação.
- Consistente com o único precedente já existente no código (exchange único por contexto).

### Riscos Aceitos
- Um exchange `topic` por contexto significa que todos os eventos de um contexto compartilham a mesma unidade de infraestrutura RabbitMQ (o exchange) — uma reconfiguração do exchange (ex.: mudar durabilidade) afeta todos os eventos do contexto simultaneamente. Mitigado pela granularidade da routing key, que já isola o roteamento lógico por evento mesmo compartilhando o exchange físico.

## Referências
- ADR-004 (Arquitetura Orientada a Eventos e Processamento Assíncrono)
- Documento 07 §13.2 (Roteamento RabbitMQ dos Eventos do Incremento 2), §19 (Versionamento)
- `documentacao do projeto/Architecture Principles.md`, Seções 8, 11, 13
- `IdentityOutboxTransactionExecutorTests.cs` (Etapa 15A) — precedente de exchange único por contexto, descartado como convenção real
