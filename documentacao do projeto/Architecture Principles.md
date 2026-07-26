# iHostPro — Architecture Principles

Versão: 1.0

Status: Oficial — Arquitetura Congelada para a Fase 0

Data: 2026-07-26

---

## 1. Propósito e Autoridade deste Documento

Este documento consolida, em um único lugar, todas as decisões arquiteturais aprovadas para o iHostPro. Ele é a **principal referência arquitetural do projeto** e deverá ser respeitado obrigatoriamente durante todo o desenvolvimento.

Este documento **não substitui** as ADRs correspondentes (`documentacao do projeto/ADRs/`) — ele consolida e resume as decisões nelas registradas para consulta rápida durante o dia a dia de desenvolvimento. Em caso de dúvida sobre o detalhamento de uma decisão específica, a ADR correspondente é a fonte primária.

Este documento **não substitui** o Documento 11 (Arquitetura Técnica da Plataforma) nem o Documento 05 (Arquitetura Funcional), que definem os princípios de negócio por trás destas decisões técnicas. Este documento traduz aqueles princípios em regras técnicas objetivas e aplicáveis pelo time de desenvolvimento.

**Regra de governança:** a partir da aprovação deste documento, a arquitetura está **congelada** para a Fase 0. Nenhuma decisão aqui registrada poderá ser alterada sem a criação de uma nova ADR e aprovação explícita do usuário, conforme `ai-rules/01 - Decision Making Policy.md` (Categoria B — Proposta Arquitetural).

---

## 2. Estilo Arquitetural: Modular Monolith

O iHostPro é implementado como um **Monólito Modular** (Modular Monolith): um único processo implantável dividido em módulos fortemente coesos e fracamente acoplados (Bounded Contexts), em vez de um monólito acoplado ou uma decomposição prematura em microsserviços.

Regras:

- Cada Bounded Context é fisicamente isolado em seus próprios projetos (Domain/Application/Infrastructure/Api/Contracts).
- A comunicação entre contextos ocorre através de contratos explícitos (eventos ou interfaces públicas), nunca por acesso direto a classes internas de outro contexto.
- Esse isolamento é o que torna possível a evolução futura para microsserviços (Seção 15) sem reescrita do domínio.

---

## 3. Bounded Contexts

A plataforma é composta pelos seguintes Bounded Contexts:

| Bounded Context | Tipo | Responsabilidade |
|---|---|---|
| Identity & Access | Generic | Usuários, Papéis, Permissões, Sessões, Tenant/Administração |
| Configuration & Policy | Supporting (fundacional) | Configurações, Políticas, Templates, Feature Flags |
| Property Management | Supporting | Imóveis, Condomínios, Grupos, Portarias |
| Reservation & Scheduling | **Core** | Reservas, Hospedagens, Agenda/Calendário |
| Housekeeping | **Core** | Faxinas, Checklist, Portal da Faxineira |
| Guest | Supporting | Perfil e histórico do hóspede |
| Communication | Supporting (fundacional) | Conversas, Mensagens, canais (WhatsApp/Email/Push) |
| Workflow Orchestration | **Core** | Motor de Sagas, coordenação de processos multi-etapa |
| Guest Requests & Operations | Supporting | Early Check-in, Late Checkout, Intercorrências, Pagamentos (PIX) |
| AI Agent | **Core** | AI Gateway, Context Builder, Tools |
| External Integrations | Supporting (Anti-Corruption Layer) | Connectors Airbnb, WhatsApp, PIX |
| Notifications | Generic | Alertas internos (Admin/Faxineira/Proprietário/Sistema) |
| Audit | Generic | Log imutável, consumidor transversal de eventos |
| Dashboard & Reporting | Supporting | Indicadores, relatórios, BI (read-model) |
| Files | Generic | Armazenamento de evidências/documentos |
| Platform | Generic | Health checks, jobs cron genéricos, licenciamento |

Um novo Bounded Context só deverá ser criado quando representar um domínio genuinamente independente, com linguagem ubíqua própria — conforme os critérios do Documento 05 §30.

---

## 4. Clean Architecture

Cada Bounded Context segue internamente a separação de camadas do Clean Architecture:

```
Api  →  Application  →  Domain
              ↑
      Infrastructure
```

Regras de dependência **dentro** de um contexto:

- `Domain` não depende de nada além de `BuildingBlocks.Domain`. Nunca referencia frameworks, ORM ou infraestrutura.
- `Application` depende de `Domain` (mesmo contexto) + `BuildingBlocks.Application` + `BuildingBlocks.Messaging.Abstractions`. Nunca depende de `Infrastructure`.
- `Infrastructure` depende de `Application`/`Domain` do mesmo contexto — implementa as interfaces que eles definem (repositórios, publicação de eventos, clientes externos).
- `Api` depende de `Application`/`Infrastructure` apenas para registro de injeção de dependência e exposição de endpoints — nunca contém regra de negócio.
- O sentido da dependência é sempre em direção ao domínio. Infraestrutura serve o domínio, nunca o contrário.

---

## 5. Domain-Driven Design (DDD)

- Cada Bounded Context modela seus próprios Aggregates, Entities e Value Objects, usando a linguagem ubíqua definida no Documento 04.
- Invariantes de negócio são protegidas dentro do Aggregate Root — nunca em serviços externos ou na camada de apresentação.
- Classes-base genéricas (`AggregateRoot<TId>`, `Entity`, `ValueObject`) vivem em `BuildingBlocks.Domain` (ver Seção 12).
- Domain Events (internos, ver Seção 8) representam fatos relevantes dentro de um único Aggregate/contexto.

---

## 6. CQRS

- Toda operação de escrita é modelada como um **Command** (`ICommand`/`ICommandHandler`); toda operação de leitura como uma **Query** (`IQuery`/`IQueryHandler`).
- A camada `Api` de cada contexto é mantida "fina": endpoints apenas montam o Command/Query e o despacham, sem lógica de negócio.
- **Biblioteca de dispatch:** `Mediator` (Martin Othamar, baseado em source generators, licença MIT) — **não** o pacote `MediatR` de Jimmy Bogard, devido ao risco de licenciamento comercial identificado para versões recentes. Ver ADR-002.
- Pipeline behaviors genéricos (validação via FluentValidation, logging, transação) vivem em `BuildingBlocks.Application` — nunca contêm regra de negócio.

---

## 7. Multi-Tenant

- Isolamento obrigatório: nenhuma entidade pertence a mais de um tenant; nenhuma consulta pode acessar dados de outro tenant (Documento 04 §3.1, Documento 11 §7).
- Estratégia física: banco único, um schema PostgreSQL por Bounded Context, com `TenantId` obrigatório em toda tabela de negócio.
- **Row-Level Security (RLS)** do PostgreSQL é habilitado como camada de defesa adicional além do filtro aplicado pela aplicação.
- `ITenantContext`/`TenantContextAccessor` (em `BuildingBlocks.Infrastructure`) resolve o tenant da requisição atual e é usado por um **Global Query Filter** do EF Core em toda `BaseDbContext`, garantindo que nenhuma consulta escape do isolamento por engano.
- Eventos e jobs em background sempre preservam o `TenantId` no payload/contexto de execução.
- A evolução futura para schema-dedicado ou banco-dedicado por tenant é suportada pela arquitetura sem exigir reescrita do domínio (Documento 11 §7).

---

## 8. Event-Driven Architecture: Domain Events, Integration Events, Outbox e Event Bus

Quatro responsabilidades distintas, que nunca devem ser confundidas:

| Conceito | Escopo | Mecanismo | Onde vive |
|---|---|---|---|
| **Domain Event** | Interno a um único Aggregate/contexto | Despacho em processo, síncrono, mesma transação (via `Mediator` `INotification`) | `<Contexto>.Domain` |
| **Integration Event** | Fato relevante para outros contextos ou sistemas externos | Assíncrono, publicado no Event Bus | `<Contexto>.Contracts` |
| **Outbox Pattern** | Garantia de entrega confiável | Gravação do evento na mesma transação da mudança de negócio; relay via Outbox/Inbox transacional do Event Bus (ver Seção 11) | `<Contexto>.Infrastructure` |
| **Event Bus** | Transporte físico entre contextos | Biblioteca de mensageria + RabbitMQ (pub/sub, retries, dead-lettering) — tecnologia concreta definida na Seção 11 e no ADR-004 | Infraestrutura compartilhada |

Fluxo: `Aggregate` levanta um `Domain Event` → se relevante para outros contextos, é traduzido em um `Integration Event` → gravado na tabela de Outbox na mesma transação da mudança de negócio → após commit, o relay do Event Bus publica a mensagem → contextos consumidores reagem de forma independente, através de handlers que implementam `IIntegrationEventHandler<TEvent>` (Seção 11).

Nem todo Domain Event vira um Integration Event — apenas os que representam fatos relevantes fora do contexto de origem (Documento 07 §22 — eventos nunca representam comandos, consultas ou intenções).

---

## 9. Sagas (Workflow Orchestration)

- O Motor de Workflows (Documento 17) é implementado como **Sagas / State Machines** da biblioteca de mensageria adotada (ver Seção 11 e ADR-004), persistidas no PostgreSQL.
- Estados padronizados: Pendente → Executando → Aguardando Evento → Concluído / Erro → Reprocessando (Documento 17 §3).
- O contexto **Workflow Orchestration** é o único autorizado a **enviar comandos** (não apenas consumir eventos) para outros contextos (Reservation, Housekeeping, Requests, Communication) — sempre através dos contratos públicos de Application desses contextos, transportados pelo Event Bus.

---

## 10. Estratégia de Persistência

- **PostgreSQL** único, com **um schema dedicado por Bounded Context** e um `DbContext` próprio por contexto (Entity Framework Core).
- Cada `DbContext` possui sua própria tabela de histórico de migrations, eliminando conflitos entre contextos.
- Migrations são aplicadas por um processo dedicado, o **`IHostPro.MigrationRunner`**, nunca automaticamente no `Program.cs` da API (ver Seção 16).
- Alterações destrutivas seguem a estratégia *expand/contract* (adicionar → migrar dados → remover em release subsequente).

---

## 11. Estratégia de Mensageria

- **Wolverine** é o backbone único de mensageria: Domain/Integration Events, Outbox/Inbox transacional (via `WolverineFx.EntityFrameworkCore`), retries/redelivery, mensagens agendadas e Sagas/state machines.
- **RabbitMQ** é o transporte físico, configurado desde a Fase 0 (não in-memory), por ser o backbone de toda a arquitetura orientada a eventos da plataforma.
- Tarefas puramente cronológicas não disparadas por evento de negócio (ex.: gatilho noturno de backup) usam um `BackgroundService` leve com a biblioteca `Cronos`, sem introduzir uma segunda plataforma de agendamento.
- **MassTransit (8.x e 9.x), Hangfire, Quartz.NET, NServiceBus, Rebus, Brighter e uma implementação própria sobre `RabbitMQ.Client` foram avaliados e descartados** — ver ADR-004 para a justificativa completa de cada um, incluindo o motivo da substituição do MassTransit (decisão original) pelo Wolverine (decisão atual).

### Isolamento do Wolverine (obrigatório)

O Wolverine é tratado como um **detalhe de infraestrutura substituível**, nunca como parte do contrato que um Bounded Context de negócio conhece:

- Nenhum tipo de um Bounded Context (`Domain`, `Application`) pode referenciar um assembly `WolverineFx.*`. Apenas `BuildingBlocks.Infrastructure` e o `Host` (`IHostPro.Api`/`IHostPro.Worker`) têm essa permissão.
- Handlers de negócio implementam **`IIntegrationEventHandler<TEvent>`** (definida em `BuildingBlocks.Application`) — uma abstração própria da plataforma, sem qualquer tipo do Wolverine na assinatura.
- Cada módulo expõe, em sua própria camada de `Infrastructure`, um adaptador mínimo e mecânico (sem lógica de negócio) que o Wolverine descobre por convenção e que apenas delega para a implementação de `IIntegrationEventHandler<TEvent>` resolvida via injeção de dependência.
- Publicação de eventos ocorre exclusivamente através de `IEventPublisher` (`BuildingBlocks.Messaging.Abstractions`); nenhum código de negócio injeta `IMessageBus` do Wolverine diretamente.
- Essa regra é validada automaticamente por teste de arquitetura (NetArchTest), não apenas por convenção.

---

## 12. Regras para BuildingBlocks

Um componente só pertence a `BuildingBlocks` se **todos** os critérios abaixo forem verdadeiros:

1. **Zero vocabulário de negócio** — nenhum conceito da linguagem ubíqua de um contexto específico.
2. **Uso concreto comprovado em 3 ou mais Bounded Contexts** — nunca "pode ser útil no futuro".
3. **Estabilidade** — contrato muda raramente; não é um lugar para abstrações de negócio em evolução.
4. **Nenhuma dependência de schema de persistência ou API externa de um módulo específico.**
5. **Teste do "outro produto"** — se construíssemos um SaaS completamente diferente, o componente continuaria fazendo sentido sem alteração.

Conteúdo aprovado de `BuildingBlocks`:

- `BuildingBlocks.Domain`: `Entity`, `AggregateRoot<TId>`, `ValueObject`, `IDomainEvent` (marcador), `Result<T>`/`Error`.
- `BuildingBlocks.Application`: `ICommand`/`IQuery`/handlers (marcadores para o `Mediator`), `IIntegrationEventHandler<TEvent>` (contrato de reação a eventos consumidos, sem qualquer tipo do Wolverine), pipeline behaviors genéricos, `IUnitOfWork`.
- `BuildingBlocks.Messaging.Abstractions`: envelope genérico `IntegrationEvent` (campos comuns do Documento 07 §3 — EventId, TenantId, CorrelationId, CausationId, Timestamp, Version) e `IEventPublisher`.
- `BuildingBlocks.Infrastructure`: `ITenantContext`/`TenantContextAccessor`, `AuditInterceptor` (EF Core `SaveChangesInterceptor` genérico), `BaseDbContext` com convenções comuns, implementação do `IEventPublisher` sobre o Wolverine e extension methods de registro (Outbox/Mensageria/Serilog/OpenTelemetry) — único ponto da plataforma, além do `Host`, autorizado a referenciar `WolverineFx.*`.

**Nunca pertence a `BuildingBlocks`:** contratos concretos de Integration Events (`ReservationConfirmed` etc. — vão para `<Contexto>.Contracts`), clientes de integrações externas, implementações concretas de storage, qualquer regra de validação de negócio usada por menos de 3 contextos.

Antes de adicionar qualquer componente a `BuildingBlocks`, é obrigatório demonstrar o cumprimento dos 5 critérios acima.

---

## 13. Regras para Contracts

- Cada Bounded Context possui seu próprio projeto **`<Contexto>.Contracts`**, contendo apenas os Integration Events que ele **publica** — registros/DTOs imutáveis, sem lógica.
- Este é o **único** projeto de um contexto que outros contextos podem referenciar diretamente. Nunca se referencia `Domain`, `Application` ou `Infrastructure` de outro contexto.
- Todo evento em `<Contexto>.Contracts` herda do envelope genérico `IntegrationEvent` de `BuildingBlocks.Messaging.Abstractions`.
- Nomenclatura de eventos: verbo no passado, PascalCase, conforme Documento 07 §4 (`ReservationConfirmed`, não `ConfirmReservation`).
- Eventos nunca são alterados após publicação em produção. Mudança de estrutura exige nova versão (`ReservationConfirmedV2`), preservando compatibilidade (Documento 07 §19).

---

## 14. Regras de Comunicação Entre Contextos

- **Regra geral:** comunicação entre Bounded Contexts é sempre assíncrona, via Integration Events através do Event Bus.
- **Exceção 1 — consulta síncrona controlada:** qualquer contexto pode consultar **Identity & Access** (autenticação/autorização) e **Configuration & Policy** (resolução de política/configuração) de forma síncrona, através de um contrato público de leitura — nunca acesso direto ao domínio interno desses contextos. Justificativa: ambos exigem avaliação em tempo de requisição, incompatível com consistência eventual (Documento 04 §6, Documento 08 §25).
- **Exceção 2 — Workflow Orchestration:** único contexto autorizado a enviar comandos (não apenas eventos) a outros contextos, através dos contratos públicos de Application desses contextos (Seção 9).
- **Exceção 3 — AI Agent:** nunca referencia domínio/infraestrutura de outro contexto. Interage exclusivamente através de **Tools**, adapters finos que invocam o Application Service público do contexto correspondente (Documento 16 §12, Documento 13 §30).
- **Proibições absolutas:** Reservation & Scheduling ↔ Housekeeping direto; Guest ↔ Communication direto; qualquer contexto Core dependendo de Notifications, Dashboard & Reporting ou Audit (contextos terminais — apenas consomem eventos).
- Regras de dependência são validadas automaticamente por testes de arquitetura (`IHostPro.ArchitectureTests`, NetArchTest) no pipeline de CI.

---

## 15. Convenções de Nomenclatura

- Namespace raiz: `IHostPro.*`.
- Padrão de projeto: `IHostPro.Contexts.<NomeDoContexto>.<Camada>` (ex.: `IHostPro.Contexts.Reservations.Domain`).
- Nomes de contextos em inglês técnico, para consistência com convenções .NET — a documentação de negócio permanece em português; isso afeta apenas identificadores de código.
- Integration Events: PascalCase no passado (`ReservationConfirmed`, `CleaningCompleted`).
- Commands/Queries: sufixos `Command`/`Query` (ex.: `ConfirmReservationCommand`, `GetReservationByIdQuery`).
- Handlers: sufixo `Handler` (ex.: `ConfirmReservationCommandHandler`).
- Handlers de Integration Events (implementações de `IIntegrationEventHandler<TEvent>`): sufixo `EventHandler` (ex.: `ReservationConfirmedEventHandler`), para distinguir de `CommandHandler`/`QueryHandler`.

---

## 16. Convenções para Novos Módulos (Bounded Contexts)

Ao adicionar um novo Bounded Context:

1. Criar os projetos `Domain`, `Application`, `Infrastructure`, `Api`, `Contracts`, `Tests.Unit`, `Tests.Integration` seguindo a convenção de nomenclatura da Seção 15.
2. O `DbContext` do novo contexto deve implementar a interface marcadora `IModuleDbContext`, descoberta automaticamente pelo `IHostPro.MigrationRunner` por reflexão — nenhum módulo existente precisa ser alterado.
3. Definir o schema PostgreSQL dedicado do novo contexto.
4. Registrar o módulo no `Host` (`IHostPro.Api`/`IHostPro.Worker`) através de um único extension method (`AddXxxModule()`) no composition root.
5. Publicar seus Integration Events em `<Contexto>.Contracts`; nunca reutilizar contratos de outro contexto.
6. Documentar o novo contexto no Documento 05 (Arquitetura Funcional) e nesta Seção 3, quando aprovado.

---

## 17. Convenções para Novas Integrações Externas

Toda nova integração externa (nova OTA, novo gateway de pagamento, nova fechadura inteligente) deve:

1. Ser implementada como um novo Connector dentro do contexto **External Integrations**, nunca em outro contexto (Documento 05 §19, Documento 19 §5).
2. Implementar uma interface comum já definida em `ExternalIntegrations.Abstractions` (ex.: `IReservationProvider`, `IMessagingProvider`, `IPaymentProvider`) sempre que uma interface equivalente já existir.
3. Publicar Integration Events representando fatos consumados (nunca comandos) para o contexto de negócio correspondente consumir.
4. Possuir configuração própria (credenciais, timeout, retries) fora do código-fonte, conforme Documento 19 §18.
5. Suportar substituição por um dublê de teste (mock/fake) para testes automatizados sem depender do serviço real (Documento 19 §28).
6. Nenhum módulo de negócio deverá ser alterado para suportar uma nova integração — apenas o novo Connector é adicionado (Documento 19 §29).

---

## 18. Regras para Evolução Futura para Microsserviços

A arquitetura foi deliberadamente desenhada para tornar essa evolução incremental e de baixo risco, caso e quando for aprovada:

- As fronteiras de Bounded Context **são** as fronteiras de futuro serviço — um contexto já é fisicamente isolado em seus próprios projetos, schema de banco e contratos.
- Os projetos `<Contexto>.Contracts` **já são** o contrato público entre "serviços" — extrair um contexto para um processo/deploy independente não exige redesenhar contratos, apenas apontar o mesmo RabbitMQ e reutilizar os mesmos Integration Events.
- O Event Bus já é externo (RabbitMQ), não in-process — um contexto extraído continua publicando/consumindo exatamente da mesma forma.
- O schema PostgreSQL dedicado por contexto permite migrar esse contexto para um banco físico dedicado sem reescrever o domínio (Documento 11 §7).
- As duas exceções de consulta síncrona (Identity & Access, Configuration & Policy) precisariam se tornar chamadas de rede (gRPC/REST) explícitas caso esses contextos sejam extraídos — essa é a única fronteira que exigiria adaptação real, e é feita deliberadamente pequena e isolada por design.
- Esta evolução é uma **decisão arquitetural de Categoria B** (Decision Making Policy) e exigirá uma nova ADR e aprovação explícita — este documento não a autoriza preventivamente, apenas garante que ela permaneça tecnicamente viável.

---

## 19. Governança deste Documento

- Este documento reflete o estado aprovado da arquitetura na data acima.
- Qualquer alteração a uma decisão aqui registrada exige: (1) identificação da mudança e motivação; (2) nova ADR propondo a alteração; (3) aprovação explícita do usuário; (4) atualização deste documento na mesma tarefa que aprovar a ADR.
- Nenhuma decisão arquitetural aqui registrada poderá ser contornada silenciosamente durante a implementação. Desvios identificados durante o desenvolvimento devem ser reportados antes de prosseguir, conforme `ai-rules/00 - Engineering Constitution.md` §20.
