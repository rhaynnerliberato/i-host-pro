# ADR-026 — Communication to Property Management Front Desk Contact Resolution

Status: Aceito
Data: 2026-08-27

## Contexto

`Architecture Principles.md` §14 já registra oito exceções síncronas nomeadas e estritas, mais duas exceções gerais de propósito geral (Identity & Access, Configuration & Policy — ADR-002): Reservations → Property Management (elegibilidade — ADR-014), Communication → Reservations (contato do hóspede — ADR-019), Communication → External Integrations (execução do envio — ADR-021), e Guest Operations → Reservations / Guest Operations → Housekeeping (agenda da reserva / prontidão de limpeza — ADR-024, amendment Checkpoint 3).

Fase 10, Checkpoint 4 ("Portaria Notification Foundation") exige que **Communication** envie uma notificação operacional à Portaria ("front desk") de um Condomínio quando eventos reais de Guest Operations ocorrem (`GuestCheckedIn`, `EarlyCheckinApproved`, `LateCheckoutApproved`). O destinatário dessa notificação — o contato ("Portaria") configurado para o Condomínio ao qual o Property pertence — é armazenado exclusivamente dentro do novo agregado `FrontDeskContact` (`PropertyManagement.Domain`), cujo cadastro estrutural pertence a Property Management (decisão de ownership do Fase 10 CP4 Decision Gate: `Architecture Principles.md` §3 já registrava "Portarias" como dado de Property Management, antes mesmo deste checkpoint).

Esta ADR resolve a lacuna: como Communication resolve o contato de Portaria ATUAL de um Property sem (a) conhecer a estrutura de Condomínio, (b) consultar `PropertyManagementDbContext`/o schema `property_management` diretamente, ou (c) tolerar uma projeção eventualmente consistente que poderia endereçar uma mensagem a um contato já desativado/substituído.

## Decisão

Está aprovada uma nona exceção síncrona, estrita e específica: **Communication pode consultar Property Management exclusivamente para resolver o contato de Portaria ATIVO do Condomínio ao qual um Property pertence, necessário ao envio de UMA notificação operacional vinculada a um evento real de Guest Operations** — nunca para qualquer outra finalidade.

A exceção obedece obrigatoriamente a:

1. **Contrato público em `PropertyManagement.Contracts`** — `IFrontDeskContactReader` e `FrontDeskContactReadResult`, mirroring a forma de `IPropertyReservationEligibilityReader`/`PropertyReservationEligibility` (ADR-014) e `IReservationGuestContactReader`/`ReservationGuestContact` (ADR-019).
2. **Implementação somente em `PropertyManagement.Infrastructure`** — `FrontDeskContactReader`, único implementador permitido.
3. **Communication não referencia** `PropertyManagement.Domain`, `PropertyManagement.Application`, `PropertyManagement.Infrastructure` ou `PropertyManagementDbContext`/o schema `property_management` diretamente — apenas `PropertyManagement.Contracts`. Regra validada automaticamente por `IHostPro.ArchitectureTests` (NetArchTest).
4. **Entrada mínima**: `TenantId`, `PropertyId` — Communication já possui `PropertyId` diretamente do evento (ver item 8 abaixo); a resolução interna Property → Condomínio → contato ativo é responsabilidade exclusiva de Property Management. Communication nunca recebe nem precisa conhecer `CondominiumId`.
5. **Resposta mínima**: `ContactId`, `DisplayName` (opcional), `PhoneNumber` — nunca o agregado `FrontDeskContact`, nunca `CondominiumId`, nunca dado de Property/Condomínio.
6. **Operação somente leitura** — `GetActiveByPropertyIdAsync` nunca modifica estado de Property Management.
7. **Três casos colapsam no mesmo resultado `null`**: Property inexistente para o tenant informado, Property sem Condomínio vinculado, e Condomínio sem `FrontDeskContact` ativo configurado — todos são, do ponto de vista do chamador, a mesma situação ordinária ("nada a notificar"), nunca distinguidos. `IsActive=false` é tratado exatamente como "não configurado" (nunca retornado).
8. **`Purpose-limited`, não uma exceção geral de leitura cross-context** — esta ADR autoriza exatamente um consumidor (Communication) e exatamente um propósito (resolver o destinatário de uma notificação operacional vinculada a um evento real de Guest Operations). Não autoriza nenhum outro Bounded Context a consultar `FrontDeskContact`, nem autoriza Communication a consultar qualquer outro dado de Property Management além deste único contrato.
9. **Não cria precedente geral para leitura cross-context de Property Management** — cada futura necessidade de leitura síncrona de outro dado desta Bounded Context exige sua própria decisão, nomeada e estrita, nos mesmos termos desta.
10. **Retorna `null` quando o Property não existe para o tenant informado** — um id inexistente e um id de outro tenant são indistinguíveis por desenho, mesma convenção de ADR-014/ADR-019.
11. **Tenant-scoped, RLS, fail-closed** — a implementação abre sua própria transação curta, somente leitura, com `SET LOCAL app.tenant_id` explícito para o `tenantId` informado pelo chamador (mesmo mecanismo de `TenantAwareTransactionScope` já usado por `PropertyReservationEligibilityReader`/`ReservationGuestContactReader`) — nunca `IgnoreQueryFilters`, nunca um papel com `BYPASSRLS`. Communication nunca pode ler o contato de outro tenant.
12. **PII/segurança**: `PhoneNumber` é dado de contato operacional (não guest data) — nunca logado por inteiro em texto estruturado, nunca incluído em Integration Event, nunca em query string, nunca persistido em audit em texto puro; logs estruturados usam `ContactId`/referência mascarada, mesmo padrão de ADR-019, item 11. `GuestPhone`/`AccessCredential` NUNCA fazem parte deste boundary — permanecem, respectivamente, fora de escopo e `DEFERRED PENDING SECURE DELIVERY BOUNDARY` (ADR-024 §A7), sem qualquer relação com esta exceção.
13. **Por que síncrona, e não uma projeção eventualmente consistente**: o envio precisa usar o contato ATUALMENTE configurado — uma projeção local em Communication (eventualmente consistente) poderia endereçar uma notificação a um contato já desativado ou substituído entre o evento de origem e o processamento da notificação. Isso difere da lista operacional de chegadas/saídas (explicitamente fora de escopo deste checkpoint — ver Fase 10, CP4 Decision Gate), que toleraria melhor consistência eventual; a resolução de destinatário para um envio pontual não tolera.
14. **Nenhuma transação distribuída, nenhuma conexão cruzada mantida aberta** — mesma disciplina de ADR-014/ADR-019: a leitura conclui e sua conexão fecha antes de Communication abrir sua própria transação de escrita (persistir a `Message`).
15. **Restrição de referência verificada por arquitetura**: um `ArchitectureTest` dedicado prova que `IFrontDeskContactReader` é referenciado exclusivamente pelo assembly `Communication.Application`/`Communication.Infrastructure` — nenhum outro Bounded Context pode passar a usá-lo silenciosamente no futuro sem que o teste falhe e force uma nova decisão.
16. **Eventual separação física dos contextos deve preservar o mesmo contrato** — mesma cláusula de ADR-014/ADR-019: se Property Management for extraído para um serviço separado, `IFrontDeskContactReader` se torna uma chamada de rede com a mesma assinatura mínima.

## Alternativas Consideradas

- **Projeção local eventualmente consistente em Communication (ou em Guest Operations), alimentada por `PropertyCreated`/`PropertyActivated`/futuros eventos de `FrontDeskContact`**: rejeitada para a resolução de destinatário do envio em si (item 13) — o risco de endereçar uma mensagem a um contato desativado/substituído é inaceitável para um envio pontual, ao contrário de uma lista operacional. Nenhum evento `FrontDeskContactCreated`/`Updated` foi criado (mandato do CP4) justamente porque nenhum consumidor real precisa dele — a resolução é sempre síncrona.
- **Estender ADR-019 (`IReservationGuestContactReader`) para também retornar o contato de Portaria**: rejeitada — ADR-019 é estrita e nomeada para o boundary Communication → Reservations; Property Management é o owner do dado de Portaria, não Reservations. Misturar os dois boundaries dentro de um único contrato violaria o princípio de "purpose-limited" de ambas as ADRs.
- **Communication consultar `PropertyManagementDbContext`/schema `property_management` diretamente**: rejeitada — violaria o isolamento físico por Bounded Context (Architecture Principles §3/§7).
- **Entrada por `CondominiumId` em vez de `PropertyId`**: rejeitada — exigiria que Communication conhecesse a existência/estrutura de Condomínio, quebrando o princípio "Communication não precisa conhecer CondominiumId" (mandato do CP4, item 11); `PropertyId` já está disponível a partir do evento de origem (item 4).

## Consequências

### Positivas
- Resolve a necessidade real do Checkpoint 4 sem inventar um novo Bounded Context, sem reabrir ADR-019, e sem criar um evento sem consumidor (`FrontDeskContactCreated`/`Updated`) — reaproveita integralmente o padrão já testado e homologado de ADR-014/ADR-019.
- Mantém a superfície de acoplamento mínima, nomeada e testável por arquitetura.
- O colapso dos três casos (Property sem Condomínio, Condomínio sem contato, contato inativo) em um único resultado `null` simplifica o consumidor: Communication sempre trata a ausência de destinatário como um no-op deliberado, nunca como falha.

### Riscos Aceitos
- `Architecture Principles.md` §14 passa a ter nove exceções nomeadas. Qualquer futura extração de Property Management para um serviço separado precisa preservar `IFrontDeskContactReader` como fronteira de rede explícita (item 16).
- Uma janela de TOCTOU trivial existe entre a leitura do contato e o envio da mensagem (o contato poderia, em tese, ser desativado nesse intervalo) — aceita nos mesmos termos já estabelecidos para toda leitura síncrona cross-context desta plataforma (ADR-014/ADR-019, Riscos Aceitos); irrelevante para a correção operacional deste fluxo.

## Referências
- `documentacao do projeto/Architecture Principles.md`, Seção 14 (nona exceção)
- ADR-014 (Exceção Síncrona Reservations → Property Management) — precedente estrutural direto
- ADR-019 (Purpose-limited Reservation Guest Contact Read for Communication) — precedente estrutural direto, mesma forma de contrato/implementação/teste
- ADR-024 (Guest Operations Boundary and Checkout Orchestration), amendment Checkpoint 3 — Exceções 7/8, mesmo padrão de exceção síncrona estrita e nomeada
- `Fase 10 - Check-in, Checkout e Operacoes do Hospede - Validacao e Homologacao.md`, Checkpoint 4 (Portaria Notification Foundation)
- `IPropertyReservationEligibilityReader.cs`, `PropertyReservationEligibility.cs`, `PropertyReservationEligibilityReader.cs` / `IReservationGuestContactReader.cs`, `ReservationGuestContact.cs`, `ReservationGuestContactReader.cs` (forma espelhada)

**Nota**: `ADR-025` permanece reservada exclusivamente para PIX/Payment (Fase 10, Checkpoint 5) — não criada, não referenciada, não reutilizada por esta ADR.
