# ADR-027 — Communication to Payments Secure PIX Delivery

Status: Aceito
Data: 2026-08-28

## Contexto

ADR-025 estabeleceu que `PixChargeCreated` (`Payments.Contracts`) nunca carrega o payload financeiro (QR/copia-e-cola) — deliberadamente provider-neutro e livre de dados sensíveis, mesma disciplina de todo Integration Event nesta plataforma. Communication, porém, precisa desse payload no momento em que monta e envia a mensagem ao hóspede.

Esta ADR resolve a lacuna: como Communication resolve o QR/copia-e-cola de uma `PixCharge` já criada, sem (a) recebê-lo pelo próprio Integration Event, (b) consultar `PaymentsDbContext`/o schema `payments` diretamente, ou (c) reconstruir/regenerar o valor chamando o provider uma segunda vez.

## Decisão

Está aprovada uma décima primeira exceção síncrona, estrita e específica: **Communication pode consultar Payments exclusivamente para resolver o payload de QR/copia-e-cola necessário para entregar UMA cobrança PIX já criada ao hóspede que é dono da Reservation subjacente** — nunca para qualquer outra finalidade.

A exceção obedece obrigatoriamente a:

1. **Contrato público em `Payments.Contracts`** — `IPixChargeDeliveryReader` e `PixChargeDeliveryReadResult`, mirroring a forma de `IFrontDeskContactReader`/`FrontDeskContactReadResult` (ADR-026) e `IReservationGuestContactReader`/`ReservationGuestContact` (ADR-019).
2. **Implementação somente em `Payments.Infrastructure`** — `PixChargeDeliveryReader`, único implementador permitido.
3. **Communication não referencia** `Payments.Domain`, `Payments.Application`, `Payments.Infrastructure` ou `PaymentsDbContext`/o schema `payments` diretamente — apenas `Payments.Contracts`. Regra validada automaticamente por `IHostPro.ArchitectureTests` (NetArchTest).
4. **Entrada mínima**: `TenantId`, `PixChargeId` — Communication já possui `PixChargeId` diretamente de `PixChargeCreated.AggregateId`.
5. **Resposta mínima**: `PixChargeId`, `QrCodePayload`, `Amount`, `CurrencyCode`, `ExpiresAtUtc?` — nunca o agregado `PixCharge`, nunca `ProviderChargeId`, nunca `IdempotencyKey`, nunca qualquer segredo/payload bruto de provider.
6. **Operação somente leitura** — `GetForDeliveryAsync` nunca modifica estado de Payments.
7. **`null` quando não há payload de entrega disponível** — cobrança inexistente para o tenant informado e cobrança sem `QrCodePayload` ainda persistido (provider não aceitou) colapsam na mesma resposta `null`, tratada como no-op deliberado pelo chamador — nunca distinguida.
8. **Nunca regenera** — a implementação lê exclusivamente o `QrCodePayload` já persistido em `PixCharge` (ADR-025); nunca invoca `IPixProvider.CreateChargeAsync` uma segunda vez. Motivo: nenhuma garantia provider-neutra de que a criação seja idempotente ao ponto de devolver exatamente o mesmo payload.
9. **`Purpose-limited`, não uma exceção geral de leitura cross-context** — autoriza exatamente um consumidor (Communication) e exatamente um propósito (resolver o destinatário/payload de uma entrega de PIX vinculada a uma `PixCharge` já criada). Não autoriza nenhum outro Bounded Context a consultar `PixCharge`, nem autoriza Communication a consultar qualquer outro dado de Payments além deste único contrato.
10. **Não cria precedente geral** para leitura cross-context de Payments — cada futura necessidade de leitura síncrona desta Bounded Context exige sua própria decisão, nomeada e estrita, nos mesmos termos desta.
11. **Retorna `null` quando a cobrança não existe para o tenant informado** — um id inexistente e um id de outro tenant são indistinguíveis por desenho, mesma convenção de ADR-014/019/026.
12. **Tenant-scoped, RLS, fail-closed** — a implementação abre sua própria transação curta, somente leitura, com `SET LOCAL app.tenant_id` explícito para o `tenantId` informado pelo chamador (mesmo mecanismo de `TenantAwareTransactionScope` já usado por `FrontDeskContactReader`/`ReservationGuestContactReader`) — nunca `IgnoreQueryFilters`, nunca um papel com `BYPASSRLS`.
13. **O QR nunca trafega pelo RabbitMQ** — `PixChargeCreated` carrega apenas `TenantId`/`AggregateId` (o `PixChargeId`)/`LateCheckoutRequestId`/`ReservationId`; este boundary síncrono é o único caminho pelo qual o payload atravessa de Payments para Communication.
14. **O QR é renderizado no CONTEÚDO da mensagem ao hóspede** — esse é seu destino final legítimo (o hóspede precisa lê-lo/escaneá-lo); as regras de "nunca logar/nunca em evento/nunca em query string" (ADR-025) protegem toda OUTRA fronteira interna, não esta.
15. **Nenhuma transação distribuída, nenhuma conexão cruzada mantida aberta** — mesma disciplina de ADR-014/019/026: a leitura conclui e sua conexão fecha antes de Communication abrir sua própria transação de escrita (persistir a `Message`).
16. **Restrição de referência verificada por arquitetura** — um `ArchitectureTest` dedicado prova que `IPixChargeDeliveryReader` é referenciado exclusivamente por `Communication.Application`/`Communication.Infrastructure`.
17. **Eventual separação física dos contextos deve preservar o mesmo contrato** — mesma cláusula de ADR-014/019/026: se Payments for extraído para um serviço separado, `IPixChargeDeliveryReader` se torna uma chamada de rede com a mesma assinatura mínima.

## Alternativas Consideradas

- **Incluir o QR diretamente em `PixChargeCreated`**: rejeitada — colocaria payload financeiro sensível no broker apenas para atravessar a fronteira entre dois Bounded Contexts, mesma razão já registrada em ADR-021 para `Destination`/`RenderedContent`; e ampliaria a superfície de qualquer consumidor futuro do mesmo evento.
- **Communication consultar `PaymentsDbContext`/schema `payments` diretamente**: rejeitada — violaria o isolamento físico por Bounded Context (Architecture Principles §3/§7).
- **Regenerar o QR chamando `IPixProvider` novamente a partir do reader**: rejeitada — sem garantia de idempotência do provider ao nível de payload; a cobrança é criada uma vez, o payload persistido é a fonte de verdade única (ADR-025).

## Consequências

### Positivas
- Resolve a necessidade real do Checkpoint 5 sem reabrir ADR-025, sem duplicar payload financeiro no broker, e sem inventar um segundo mecanismo de persistência.
- Mantém a superfície de acoplamento mínima, nomeada e testável por arquitetura — mesmo padrão já testado e homologado de ADR-014/019/026.

### Riscos Aceitos
- `Architecture Principles.md` §14 passa a ter onze exceções nomeadas.
- Uma janela de TOCTOU trivial existe entre a leitura do payload e o envio da mensagem — aceita nos mesmos termos já estabelecidos para toda leitura síncrona cross-context desta plataforma.

## Referências
- `documentacao do projeto/Architecture Principles.md`, Seção 14 (décima primeira exceção)
- ADR-025 (PIX Payment Boundary) — a decisão de persistência do QR que este boundary de leitura pressupõe
- ADR-019 (Purpose-limited Reservation Guest Contact Read for Communication) / ADR-026 (Communication to Property Management Front Desk Contact Resolution) — precedente estrutural direto, mesma forma de contrato/implementação/teste
- `Fase 10 - Check-in, Checkout e Operacoes do Hospede - Validacao e Homologacao.md`, Checkpoint 5
- `IFrontDeskContactReader.cs`, `FrontDeskContactReadResult.cs`, `FrontDeskContactReader.cs` (forma espelhada)
