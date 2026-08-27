# ADR-019 — Purpose-limited Reservation Guest Contact Read for Communication

Status: Atualizado
Data original: 2026-08-18
Data desta revisão: 2026-08-27 (Fase 10, Checkpoint 4 — extensão factual do contrato, ver seção ao final)

## Contexto

`Architecture Principles.md` §14 estabelece que comunicação entre Bounded Contexts é sempre assíncrona, via Integration Events, com exatamente duas exceções de consulta síncrona controlada de propósito geral (Identity & Access, Configuration & Policy — ADR-002) mais uma terceira, estrita e nomeada (Reservations → Property Management, elegibilidade — ADR-014).

A Fase 9 (Comunicação e Integrações do MVP), Checkpoint 1, exige que o novo Bounded Context **Communication** envie uma mensagem WhatsApp ao hóspede quando uma Reservation é criada. O destinatário dessa mensagem — o telefone do hóspede — é armazenado exclusivamente dentro do aggregate `Reservation` (`Reservations.Domain`) e é **deliberadamente excluído de todo Integration Event** publicado por Reservations (`ReservationCreated`/`ReservationUpdated`/`ReservationCancelled`) desde a Fase 3, Incremento 1 — confirmado por testes estruturais de ausência de PII presentes desde então e nunca reabertos por nenhuma fase subsequente. Reintroduzir esse dado em um evento romperia esse precedente testado e amplamente reafirmado (mais recentemente, Fase 8, Checkpoint 2.1, §5.13.3).

Esta ADR resolve a lacuna: como Communication obtém o telefone do hóspede sem (a) adicionar PII a um Integration Event, (b) criar o Bounded Context Guest agora (fora do escopo aprovado desta fase), ou (c) consultar `ReservationsDbContext`/o schema `reservations` ou um controller HTTP interno diretamente.

## Decisão

Está aprovada uma quarta exceção síncrona, estrita e específica: **Communication pode consultar Reservations exclusivamente para obter os dados mínimos de contato do hóspede necessários ao envio de UMA comunicação vinculada a uma Reservation existente** — nunca para qualquer outra finalidade.

A exceção obedece obrigatoriamente a:

1. **Contrato público em `Reservations.Contracts`** — `IReservationGuestContactReader` e `ReservationGuestContact`, mirroring exatamente a forma de `IPropertyReservationEligibilityReader`/`PropertyReservationEligibility` (ADR-014).
2. **Implementação somente em `Reservations.Infrastructure`** — `ReservationGuestContactReader`, único implementador permitido.
3. **Communication não referencia** `Reservations.Domain`, `Reservations.Application`, `Reservations.Infrastructure` ou `ReservationsDbContext`/o schema `reservations` diretamente — apenas `Reservations.Contracts`. Regra validada automaticamente por `IHostPro.ArchitectureTests` (NetArchTest) — ver também item 13 abaixo.
4. **Resposta mínima**: `ReservationId`, `GuestPhone` — nunca valor da reserva, datas, status completo, propriedade, endereço, documentos, `GuestCount` ou qualquer outro campo de `Reservation` que Communication não precise. `GuestName` está deliberadamente fora desta versão do contrato — nenhum template do Checkpoint 1 o exige (ver Fase 9 doc, §Template MVP); se um template futuro exigir `GuestName`, essa é uma decisão material separada, exigindo avaliação explícita antes de estender o DTO, nunca uma adição silenciosa "por via das dúvidas".
5. **Operação somente leitura** — `GetGuestContactAsync` nunca modifica estado de Reservations.
6. **`Purpose-limited`, não uma exceção geral de leitura de PII** — esta ADR autoriza exatamente um consumidor (Communication) e exatamente um propósito (entrega de uma comunicação vinculada a uma Reservation). Não autoriza nenhum outro Bounded Context a consultar dados de contato do hóspede, nem autoriza Communication a consultar qualquer outro dado de Reservations além deste único contrato.
7. **Não cria precedente geral para PII cross-context** — cada futura necessidade de acesso a dado pessoal por outro Bounded Context exige sua própria ADR, nomeada e estrita, nos mesmos termos desta.
8. **Não autoriza PII em Integration Events** — o payload de `ReservationCreated`/`ReservationUpdated`/`ReservationCancelled` permanece inalterado; esta ADR não reabre essa decisão.
9. **Retorna `null` quando a Reservation não existe para o tenant informado** — um id inexistente e um id de outro tenant são indistinguíveis por desenho, mesma convenção de todo outro lookup cross-context/cross-tenant já estabelecido nesta plataforma (ADR-014, item equivalente).
10. **Tenant-scoped, RLS, fail-closed** — a implementação abre sua própria transação curta, somente leitura, com `SET LOCAL app.tenant_id` explícito para o `tenantId` informado pelo chamador (mesmo mecanismo de `TenantAwareTransactionScope` já usado por `PropertyReservationEligibilityReader`) — nunca `IgnoreQueryFilters`, nunca um papel com `BYPASSRLS`. Communication nunca pode ler o contato de outro tenant.
11. **Toda leitura gera um registro de auditoria estruturado, PII-safe** — `TenantId`, `ReservationId`, `Purpose = "communication_delivery"`, `Caller = "Communication"`, timestamp implícito do log, e o resultado (`Found`/`NotFound`) — nunca o valor do telefone em si. Via `ILogger<T>` estruturado, mesmo padrão já estabelecido em `Identity.Application` e reafirmado em `Workflow.Application` (Fase 8, Checkpoint 2.1) — nenhuma persistência nova, nenhum Audit BC.
12. **Nenhuma transação distribuída, nenhuma conexão cruzada mantida aberta** — mesma disciplina de ADR-014, item 6/8: a leitura conclui e sua conexão fecha antes de Communication abrir sua própria transação de escrita (persistir a `Message`).
13. **Restrição de referência verificada por arquitetura**: um `ArchitectureTest` dedicado (não apenas a regra genérica de camadas) prova que `IReservationGuestContactReader` é referenciado exclusivamente pelo assembly `Communication.Application` (ou `Communication.Infrastructure`, conforme a implementação real) — nenhum outro Bounded Context pode passar a usá-lo silenciosamente no futuro sem que o teste falhe e force uma nova decisão.
14. **Eventual separação física dos contextos deve preservar o mesmo contrato** — mesma cláusula de ADR-014, item 11: se Reservations for extraído para um serviço separado, `IReservationGuestContactReader` se torna uma chamada de rede com a mesma assinatura mínima.

## Alternativas Consideradas

- **Adicionar `GuestPhone` a `ReservationCreated`**: rejeitada explicitamente pelo usuário — reabriria a exclusão de PII de Integration Events, testada e reafirmada em toda fase desde a Fase 3.
- **Criar o Bounded Context Guest agora**, como owner canônico dos dados de contato: rejeitada para este Checkpoint — fora do escopo aprovado da Fase 9, Checkpoint 1; `Architecture Principles.md` §3 já registra "Guest" como BC futuro, não construído nesta fase.
- **Communication consultar `ReservationsDbContext`/schema `reservations` diretamente**: rejeitada — violaria o isolamento físico por Bounded Context (Architecture Principles §3/§7) e tornaria RLS/fail-closed responsabilidade de um contexto que não é o owner do dado.
- **Communication chamar um controller HTTP interno de Reservations**: rejeitada — introduziria acoplamento por rede/HTTP dentro do mesmo processo monolítico, sem necessidade; o padrão já estabelecido (ADR-002/ADR-014) é a chamada direta ao contrato público via DI, no mesmo processo.

## Consequências

### Positivas
- Resolve a necessidade real do Checkpoint 1 sem reabrir a exclusão de PII de eventos, sem antecipar o BC Guest, e sem inventar um mecanismo novo — reaproveita integralmente o padrão já testado e homologado de ADR-014.
- Mantém a superfície de acoplamento mínima, nomeada e testável por arquitetura.
- A auditoria estruturada, PII-safe, mantém rastreabilidade de quem/quando um dado de contato foi lido, sem persistir o próprio dado sensível em nenhum log.

### Riscos Aceitos
- `Architecture Principles.md` §14 passa a ter quatro exceções nomeadas. Qualquer futura extração de Reservations para um serviço separado precisa preservar `IReservationGuestContactReader` como fronteira de rede explícita (item 14).
- Uma janela de TOCTOU trivial existe entre a leitura do contato e o envio da mensagem (o hóspede poderia, em tese, ter seu telefone alterado nesse intervalo) — aceita nos mesmos termos já estabelecidos para toda leitura síncrona cross-context desta plataforma (ADR-014, Riscos Aceitos); irrelevante para a correção de negócio deste fluxo (uma mensagem de boas-vindas com o telefone momentaneamente desatualizado não é um caso crítico).

## Referências
- `documentacao do projeto/Architecture Principles.md`, Seção 14 (quarta exceção)
- ADR-002 (Arquitetura da Solução) — regra geral e as duas exceções originais
- ADR-014 (Exceção Síncrona Reservations → Property Management) — precedente estrutural direto, mesma forma de contrato/implementação/teste
- `Fase 8 - Workflow Orchestration - Validacao e Homologacao.md`, §5.13 (precedente de auditoria estruturada, PII-safe, via `ILogger<T>`)
- `Fase 9 - Comunicacao e Integracoes do MVP - Validacao e Homologacao.md` (Checkpoint 1)
- `IPropertyReservationEligibilityReader.cs`, `PropertyReservationEligibilityReader.cs` (forma espelhada)

## Amendment — Fase 10, Checkpoint 4 (extensão factual: `GuestName`)

O item 4 desta ADR já previa esta situação: *"`GuestName` está deliberadamente fora desta versão do contrato... se um template futuro exigir `GuestName`, essa é uma decisão material separada, exigindo avaliação explícita antes de estender o DTO, nunca uma adição silenciosa 'por via das dúvidas'."*

Fase 10, Checkpoint 4 (Portaria Notification Foundation) é exatamente essa decisão explícita: os três novos processadores de notificação de Portaria (`GuestCheckedInFrontDeskNotificationProcessor`, `EarlyCheckinApprovedFrontDeskNotificationProcessor`, `LateCheckoutApprovedFrontDeskNotificationProcessor`) precisam do nome do hóspede para renderizar uma notificação operacional legível ("Hóspede {{GuestName}} chegou..."). `ReservationGuestContact` foi estendido com um terceiro campo, `GuestName` (`string`, não-nulo — `Reservation.GuestName` é obrigatório desde a Fase 3).

Esta extensão é registrada aqui, não como uma nova exceção síncrona (o boundary permanece exatamente o mesmo — Communication → Reservations, um único consumidor, um único propósito ampliado: "entrega de UMA comunicação vinculada a uma Reservation existente", agora explicitamente cobrindo tanto a mensagem ao hóspede quanto a notificação à Portaria sobre o mesmo evento do hóspede):

- **Escopo inalterado**: itens 1, 2, 3, 5-14 desta ADR permanecem exatamente como escritos — mesmo contrato (`Reservations.Contracts`), mesma implementação exclusiva (`Reservations.Infrastructure`), mesmo consumidor exclusivo (Communication), mesma auditoria PII-safe, mesmo isolamento tenant-scoped/RLS.
- **`GuestPhone` nunca é usado na notificação de Portaria** — a Exceção Síncrona #9 (ADR-026) resolve o destinatário da Portaria separadamente (`IFrontDeskContactReader`); `GuestName` é o único campo desta ADR que atravessa para o novo caso de uso. `GuestPhone` continua reservado exclusivamente ao envio da mensagem ao hóspede.
- **Nenhuma nova exceção síncrona foi criada por esta extensão** — permanece a mesma exceção #4, apenas com um DTO factualmente mais amplo.

Ver ADR-026 para a exceção síncrona #9 (Communication → Property Management, resolução do contato de Portaria) que acompanha esta extensão no mesmo checkpoint.
