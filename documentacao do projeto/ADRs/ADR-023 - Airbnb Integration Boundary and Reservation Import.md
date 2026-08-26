# ADR-023 — Airbnb Integration Boundary and Reservation Import

Status: Aceito
Data: 2026-08-26

## Contexto

A Fase 9, Checkpoint 3.0 (auditoria read-only) confirmou, via pesquisa contra fontes oficiais da Airbnb (Help Center, API Terms of Service), que não existe hoje nenhum acesso público/self-serve à API oficial da Airbnb: o único caminho documentado é através de um Channel Manager já existente, ou de uma avaliação de parceria iniciada pela própria Airbnb (NDA, revisão de segurança obrigatória, sem sandbox/ambiente de teste documentado). O Checkpoint 3.1 (Decision Gate) validou uma arquitetura conceitual completa — ownership, cardinalidade, modelo de dados, boundary de PII, semântica de idempotência/update/cancellation, impacto no fan-out de `ReservationCreated` — sem escrever nenhum código, e recomendou explicitamente que nenhuma nova exceção síncrona (`Architecture Principles.md` §14) fosse necessária. O Checkpoint 3.2 ("Airbnb Deterministic Foundation") implementou essa arquitetura de forma real e testável, permanecendo estritamente dentro do que pode ser construído sem um contrato de parceria Airbnb: nenhum cliente HTTP real, nenhum OAuth, nenhum endpoint especulativo, nenhum polling/sync automático.

## Decisão

### Ownership (sem mudança em relação ao CP3.1)

- **External Integrations** possui: `AirbnbIntegration` (config por tenant), `AirbnbListingMapping` (mapeamento listing↔property), referências de credencial, protocolo do provider (futuro), execução/estado de sincronização, identificadores externos, erros do provider.
- **Reservations** possui: o aggregate `Reservation` (incluindo os novos campos `Source`/`ExternalReservationId`), a semântica de negócio de import/update/cancel, e `ReservationCreated` (evento já existente, reutilizado).
- **Property Management** permanece inteiramente provider-neutro — nenhum identificador ou conceito Airbnb existe em `Property`/`PropertyManagement.*` (confirmado por `ArchitectureTests.PropertyManagement_Assemblies_Contain_No_Airbnb_Named_Type`).

### Cardinalidade e modelo de dados

Uma `AirbnbIntegration` por tenant (índice único em `tenant_id`, mesmo padrão de `WhatsAppIntegration`), múltiplos `AirbnbListingMapping` por integração (único em `tenant_id + external_listing_id`). Ambos tenant-owned/RLS — deliberadamente **não** um diretório de rotas global como `WhatsAppTenantRoute`: nenhum contrato de webhook Airbnb conhecido hoje exige resolver o tenant antes de conhecê-lo (reavaliar apenas se uma futura parceria revelar um modelo equivalente ao da Meta).

`Reservation` ganha `Source` (`ReservationSource.Manual` | `Airbnb`) e `ExternalReservationId` (nullable, obrigatório apenas para `Airbnb`), com um índice único parcial `(tenant_id, source, external_reservation_id) WHERE external_reservation_id IS NOT NULL` — a chave de idempotência de import. `Reservation.CreateImported(...)` é uma nova factory, ao lado de `Create(...)` (inalterada) — nunca uma reescrita do factory existente.

### Import assíncrono — sem nova exceção síncrona

Confirmado (CP3.1 item 13, reconfirmado na implementação real): o fluxo de import/update/cancel Airbnb **não exige** nenhuma nova exceção às 5 já registradas em `Architecture Principles.md` §14 (ADR-002 ×2, ADR-014, ADR-019, ADR-021). External Integrations publica `AirbnbReservationImported`/`AirbnbReservationUpdated`/`AirbnbReservationCancelled` (`ExternalIntegrations.Contracts`) através do seu outbox real; Reservations consome de forma inteiramente assíncrona, exatamente como já consome `CleaningCreated`/`CleaningAssigned`/etc. de Housekeeping — pub/sub desacoplado padrão, nunca uma chamada síncrona in-process.

Isso estabelece um segundo padrão de referência cross-context a `ExternalIntegrations.Contracts`, distinto da quinta exceção síncrona da ADR-021: `Reservations.Application`/`Reservations.Infrastructure` (apenas os processadores/handlers Airbnb nomeados, nunca o restante do Bounded Context) podem referenciar `ExternalIntegrations.Contracts` para consumir eventos assíncronos — nunca `Domain`/`Application`/`Infrastructure`/`Api` de External Integrations, nunca uma chamada síncrona. `ArchitectureTests.No_Other_Bounded_Context_Assembly_References_ExternalIntegrationsContracts_Except_Communication_And_ReservationsAirbnbConsumer` fecha essa fronteira por nome de tipo, não por namespace.

### PII mínima no evento de import (CP3.1 Decision Gate item 12, Opção A)

`AirbnbReservationImported` carrega exatamente os campos que `Reservation.CreateImported` já exige — `ExternalReservationId`, `PropertyId` (já resolvido, nunca `ExternalListingId`), `GuestName`, `CheckInAt`, `CheckOutAt`, `GuestCount`, `OccurredAtUtc` — e nada além disso: nunca e-mail, telefone, reviews, conteúdo de mensagens, payload bruto do provider ou preço/moeda/taxas. `GuestPhone` fica deliberadamente ausente por padrão (`Reservation.CreateImported` recebe `null`). `ArchitectureTests.Airbnb_Reservation_Events_Never_Declare_A_Forbidden_PII_Property` guarda essa invariante estruturalmente. Isso é aceitável sem nova exceção síncrona porque o papel do evento aqui é um GATILHO DE CRIAÇÃO — precisa carregar os mesmos fatos mínimos que qualquer `Create()` já exigiria — diferente do papel de `ReservationCreated` (uma notificação pós-fato, deliberadamente PII-free, cujo consumidor que precisa de PII adicional usa uma leitura síncrona purpose-limited, ADR-019).

### `ReservationCreated` reutilizado — nenhum novo consumidor, nenhuma mudança em ADR-020

Uma reserva importada da Airbnb publica exatamente o mesmo `ReservationCreated` que uma reserva manual publica — agora com um campo `Source` (`"manual"` | `"airbnb"`, string estável, mesmo padrão de `Status`). Isso não introduz nenhum consumidor novo em `ReservationCreated` (Housekeeping/Dashboard/Workflow/Communication continuam sendo exatamente os mesmos quatro), portanto **não altera o isolamento de handler chains da ADR-020** — nenhum `AddStickyHandler` novo foi necessário. Confirmado empiricamente por um round-trip real (RabbitMQ + Worker + MigrationRunner reais): Housekeeping e Dashboard reagem identicamente a uma reserva Airbnb-importada; Workflow dispara `CreateCleaningForReservation` identicamente, e Housekeeping cria uma `Cleaning` real a partir dele.

### Communication permanece source-aware — consent boundary

`ReservationCreatedCommunicationProcessor` verifica `Source` antes de qualquer outra ação: `Source="airbnb"` é um skip deliberado (log estruturado `ReservationCommunicationSkipped`/`ConsentNotEstablishedForImportedReservation`), nunca cria `Message`, nunca marca `Failed`. `Source="manual"` mantém o comportamento existente inalterado. Este é o único ponto do sistema onde o Bounded Context de destino precisa saber a origem da reserva — nenhum outro consumidor de `ReservationCreated` foi alterado.

### Bloqueado por parceria Airbnb — registrado, não implementado

`AirbnbPartnerAccessRequired=true`, `AirbnbPartnerAccessAvailable=false`, `RealIntegrationTestingBlocked=true` permanecem registrados (CP3.0/CP3.1). Este checkpoint não implementa: cliente HTTP real, OAuth, sync orchestration/polling/scheduler, endpoints administrativos públicos (`AirbnbIntegrationController`/etc. — deliberadamente não criados; sem uso real ainda, evitando NSwag/Angular especulativos), iCal (capacidade futura separada, calendário-only, sem dados de reserva/hóspede). `AirbnbSyncStarted` foi formalizado em `ExternalIntegrations.Contracts` mas deliberadamente não publicado/consumido — sem orquestração de sync real para o disparar.

## Alternativas Consideradas

- **Introduzir uma sexta exceção síncrona (Reservations → External Integrations, leitura direta)**: rejeitada — o fluxo de import é, por natureza, assíncrono (a Airbnb notifica/é sincronizada em batch, nunca em request-response com o hóspede esperando), e a arquitetura pub/sub já existente entre Reservations e outros publishers (Housekeeping) resolve o problema sem nenhuma exceção nova.
- **Modelar `AirbnbListingMapping` como diretório global (padrão `WhatsAppTenantRoute`)**: rejeitada — nenhum contrato de webhook Airbnb conhecido hoje exige resolver `TenantId` antes de conhecê-lo; tenant-owned/RLS é o padrão default e mais seguro, reavaliar apenas se uma parceria futura revelar necessidade equivalente à da Meta (ADR-022).
- **Carregar PII completa (e-mail, telefone, mensagens) em `AirbnbReservationImported`**: rejeitada — violaria o princípio de minimização já estabelecido para `ReservationCreated`/`WhatsAppMessageStatusChanged`; o payload aprovado é o mínimo estrutural que `Reservation.CreateImported` exige.
- **Criar endpoints administrativos públicos (`AirbnbIntegrationController`) neste checkpoint**: rejeitada por ora — nenhum caller real existe ainda (a fundação determinística semeia `AirbnbIntegration`/`AirbnbListingMapping` diretamente via repositório em teste), e criar um endpoint sem uso real dispararia NSwag/Angular especulativamente; adiado para o checkpoint que precisar de fato de um fluxo administrativo real.
- **Auto-criar uma `Reservation` para um `AirbnbReservationUpdated` com `ExternalReservationId` desconhecido**: rejeitada — nenhum precedente documental autoriza essa política; o consumidor loga e ignora, nunca inventa uma criação implícita.

## Consequências

### Positivas
- Zero acoplamento especulativo a um contrato de API Airbnb que ainda não existe — toda a fundação é testável deterministicamente (sem rede real), e continuará válida qualquer que seja o formato final do contrato de parceria.
- `ReservationCreated`/ADR-020 permanecem intocados — reutilizar o evento existente é estritamente aditivo (`Source`), nunca uma reestruturação do fan-out já homologado.
- O boundary de consentimento fica centralizado em um único ponto (Communication), nunca espalhado por múltiplos consumidores.
- A fronteira cross-context (`Reservations.Application`/`Infrastructure` → `ExternalIntegrations.Contracts`) é explícita, visível em tempo de compilação, e guardada por `ArchitectureTests` por nome de tipo — nunca escondida atrás de indireção.

### Riscos Aceitos
- `ExternalIntegrations.Contracts` agora tem dois padrões de referenciamento externo distintos: a quinta exceção síncrona da ADR-021 (Communication → `IMessagingProvider`) e este novo padrão assíncrono nomeado (Reservations → eventos Airbnb) — registrado aqui explicitamente, nunca generalizado para qualquer outro par.
- `AirbnbReservationUpdated`/`AirbnbReservationCancelled` para um `ExternalReservationId` desconhecido são deliberadamente ignorados (log + no-op) — se uma condição de corrida real (update chegando antes do import, numa janela de rede real) se mostrar comum quando a parceria existir, essa política precisará de revisão então, não assumida agora.
- Nenhuma validação de elegibilidade/conflito de agenda é feita no import Airbnb (diferente da criação manual, que valida capacidade e conflito de datas) — o mandato do CP3.2 não pediu essa validação, e inventá-la seria escopo não solicitado; uma reserva Airbnb duplicada/conflitante com uma manual é um risco operacional real, não coberto por este checkpoint, a ser resolvido quando a sincronização real existir.
- `ReservationCreatedCommunicationProcessor`'s source-aware skip é a única forma de consentimento hoje — não existe ainda nenhuma política de opt-in Airbnb real; enquanto isso não for resolvido, todo hóspede Airbnb-importado permanece sem qualquer comunicação automática (aceito deliberadamente, nunca contornado).

## Referências
- `documentacao do projeto/Architecture Principles.md`, Seções 13, 14, 17
- ADR-019 (Purpose-limited Reservation Guest Contact Read for Communication) — precedente da distinção evento-de-notificação vs. leitura-síncrona-purpose-limited
- ADR-020 (Isolamento de Handler Chains do Wolverine) — confirmado intacto, não reaberto por esta ADR
- ADR-021 (External Integrations ACL and Synchronous Provider Boundary) — a quinta exceção síncrona, distinta do padrão assíncrono desta ADR
- ADR-022 (WhatsApp Webhook Security and Tenant Routing Boundary) — precedente de diretório global de rotas, deliberadamente não reutilizado aqui
- `Documento 07 — Catálogo de Eventos de Domínio`, §16 (payloads de `AirbnbSyncStarted`/`AirbnbReservationImported`/`AirbnbReservationUpdated`/`AirbnbReservationCancelled` formalizados por esta ADR)
- `Fase 9 - Comunicacao e Integracoes do MVP - Validacao e Homologacao.md`, Checkpoint 3.1 (Decision Gate) e Checkpoint 3.2 (esta implementação)
