# ADR-029 — Communication to Reservations Guest Phone Resolution

Status: Aceito
Data: 2026-08-29

## Contexto

Fase 11, Checkpoint 1 ("Inbound Conversation Foundation") exige que Communication resolva, a partir do telefone normalizado do remetente de uma mensagem inbound do WhatsApp, qual(is) Reservation(s) esse telefone pode representar — pré-condição para criar/reutilizar uma `Conversation` e persistir a mensagem inbound como um `Message` vinculado a uma Reservation real.

`Architecture Principles.md` §14 já registra doze exceções síncronas nomeadas e estritas. A única exceção síncrona pré-existente entre Communication e Reservations é a Exceção 5 (ADR-019, `IReservationGuestContactReader`) — mas seu propósito é estritamente o inverso do que este checkpoint precisa: `ReservationId → GuestPhone`, para uma Reservation já conhecida. O CP1 precisa do sentido oposto — `GuestPhoneNormalized → Reservation candidate(s)`, sem nenhuma Reservation identificada ainda. Auditado antes de codificar (mandato do CP1, item 14): esta é uma exceção **nova**, não uma extensão de ADR-019 — mesmo par de Bounded Contexts, propósito e contrato distintos, mirroring exatamente a relação já estabelecida entre ADR-026 (Exceção 9) e ADR-028 (Exceção 12, "não uma extensão da Exceção 9... mesmo par de contextos, propósito distinto").

## Decisão

Está aprovada uma décima terceira exceção síncrona, estrita e específica: **Communication pode consultar Reservations exclusivamente para resolver Reservation(s) Confirmed elegíveis a partir do telefone normalizado do remetente de uma mensagem inbound** — nunca para qualquer outra finalidade.

A exceção obedece obrigatoriamente a:

1. **Contrato público em `Reservations.Contracts`** — `IReservationByGuestPhoneReader` e `ReservationCandidate`, mirroring a forma de `IReservationGuestContactReader`/`ReservationGuestContact` (ADR-019) e `IReservationScheduleReader`/`ReservationScheduleSnapshot` (ADR-024 amendment).
2. **Implementação somente em `Reservations.Infrastructure`** — `ReservationByGuestPhoneReader`, único implementador permitido.
3. **Communication não referencia** `Reservations.Domain`, `Reservations.Application`, `Reservations.Infrastructure` ou `ReservationsDbContext`/o schema `reservations` diretamente — apenas `Reservations.Contracts`. Regra validada automaticamente por `IHostPro.ArchitectureTests` (NetArchTest).
4. **Entrada mínima**: `TenantId`, `GuestPhoneNormalized` — o telefone já reduzido a dígitos-somente pelo chamador (mesma regra que `ExternalIntegrations.Infrastructure.Meta.MetaWebhookMessageProcessor` aplica ao produzir `InboundGuestMessageReceived.SenderPhoneNormalized`). Nenhum telefone bruto/não normalizado cruza este boundary.
5. **Resposta mínima**: uma lista de `ReservationCandidate(ReservationId, PropertyId, CheckInAt, CheckOutAt)` — nunca o agregado `Reservation`, nunca `GuestName`/`GuestPhone` novamente, nunca status administrativo além do filtro de elegibilidade já aplicado, nunca dado de pagamento/credencial de acesso.
6. **Operação somente leitura** — `FindEligibleByGuestPhoneAsync` nunca modifica estado de Reservations.
7. **Regra oficial de elegibilidade — apenas `ReservationStatus.Confirmed`**: `Cancelled` e `Closed` nunca são elegíveis, sem exceção. Nenhuma janela temporal foi criada (nenhuma heurística de "Closed nas últimas 24h/48h", "checkout recente" ou "check-in próximo") — decisão explícita do usuário, não inventada por interpretação. Racional: `Closed` é terminal e representa reserva encerrada; durante pré check-in/check-in/hospedagem/pré-checkout a Reservation permanece `Confirmed`; os estados operacionais mais granulares (`Active`/`CheckedIn`/`CheckedOut`) pertencem a Guest Operations, nunca duplicados aqui.
8. **0/1/N candidatos — nenhuma escolha automática**: 0 candidatos e N candidatos nunca resultam na criação de uma `Conversation` — apenas exatamente 1 candidato resolve automaticamente. A desambiguação conversacional entre N candidatos é deferida a um checkpoint futuro (quando o Agente de IA existir). Esta ADR não decide esse comportamento downstream — apenas garante que o contrato nunca perde informação ao retornar todos os candidatos elegíveis, nunca "o primeiro" ou "o mais recente".
9. **Normalização de telefone**: nenhum boundary compartilhado de normalização existia neste momento (auditado antes de implementar). Em vez de criar uma nova categoria de utilitário cross-context sem uma decisão dedicada (mesmo critério que já levou ADR-021 a rejeitar `ExternalIntegrations.Abstractions`), `ReservationByGuestPhoneReader` reduz `GuestPhone` a dígitos-somente no momento da comparação, com a MESMA regra que `MetaWebhookMessageProcessor` já aplica no lado de `ExternalIntegrations` — documentada de forma idêntica nos dois pontos e coberta por teste unitário/integração próprio em cada lado. Se uma terceira necessidade real surgir, promover a um utilitário compartilhado (`BuildingBlocks` ou equivalente) é uma decisão futura, não assumida agora.
10. **Retorna lista vazia quando nenhuma Reservation Confirmed corresponde** — telefone inexistente e telefone de outro tenant são indistinguíveis por desenho, mesma convenção de ADR-014/ADR-019/ADR-026/ADR-028.
11. **Tenant-scoped, RLS, fail-closed** — a implementação abre sua própria transação curta, somente leitura, com `SET LOCAL app.tenant_id` explícito para o `tenantId` informado pelo chamador (mesmo mecanismo de `TenantAwareTransactionScope` já usado por todos os readers anteriores) — nunca `IgnoreQueryFilters`, nunca um papel com `BYPASSRLS`. Communication nunca pode ler Reservations de outro tenant.
12. **`Purpose-limited`, não uma exceção geral de leitura cross-context** — esta ADR autoriza exatamente um consumidor (Communication) e exatamente um propósito (resolver candidatos de Reservation a partir de um telefone de mensagem inbound). Não autoriza nenhum outro Bounded Context a consultar este reader, nem autoriza Communication a consultar qualquer outro dado de Reservations além deste único contrato e do já existente ADR-019.
13. **Não cria precedente geral para leitura cross-context de Reservations** — cada futura necessidade de leitura síncrona de outro dado desta Bounded Context exige sua própria decisão, nomeada e estrita, nos mesmos termos desta.
14. **PII/segurança**: `GuestPhoneNormalized` nunca é logado por inteiro em texto estruturado — logs estruturados usam apenas `TenantId`/contagem de candidatos, mesmo padrão de ADR-019 item 11/ADR-026 item 12. Nenhum dado retornado (`ReservationCandidate`) é PII por si só (nem `GuestName`, nem `GuestPhone`, nem documento).
15. **Migration aditiva, sem mudança de invariante**: um índice novo `(tenant_id, guest_phone)` em `reservations.reservations` (migration `AddGuestPhoneIndex`) — deliberadamente sem a coluna `status` (mandato do CP1, item 9), reavaliado apenas se um plano de consulta real demonstrar necessidade.
16. **Restrição de referência verificada por arquitetura**: um `ArchitectureTest` dedicado prova que `IReservationByGuestPhoneReader` é referenciado exclusivamente pelo assembly `Communication.Application`/`Communication.Infrastructure` — nenhum outro Bounded Context pode passar a usá-lo silenciosamente no futuro sem que o teste falhe e force uma nova decisão.
17. **Eventual separação física dos contextos deve preservar o mesmo contrato** — mesma cláusula de ADR-014/ADR-019/ADR-026/ADR-028: se Reservations for extraído para um serviço separado, `IReservationByGuestPhoneReader` se torna uma chamada de rede com a mesma assinatura mínima.

## Alternativas Consideradas

- **Estender `IReservationGuestContactReader` (ADR-019) para também resolver por telefone**: rejeitada — propósito e direção do contrato são opostos (`ReservationId → GuestPhone` vs. `GuestPhone → Reservation candidates`); misturar os dois violaria o princípio "purpose-limited" já estabelecido por ADR-019, mesmo racional que levou ADR-028 a não estender ADR-026.
- **Tabela de índice duplicada em Communication (`GuestPhone → ReservationId`), alimentada por projeção eventualmente consistente**: rejeitada — Reservations é a dona do dado `GuestPhone`; duplicar em Communication criaria uma segunda fonte de verdade sujeita a divergência, exatamente o risco que os readers síncronos já existentes (ADR-014/019/026/027/028) evitam para dados que precisam refletir o estado ATUAL.
- **Aplicar uma janela temporal (ex.: `Closed` nas últimas 24h/48h) para cobrir hóspedes que acabaram de fazer checkout**: rejeitada explicitamente pelo usuário — nenhuma heurística de negócio sem decisão documentada; `Confirmed` é o único filtro de lifecycle necessário no MVP deste checkpoint.
- **Criar um utilitário compartilhado de normalização de telefone (`BuildingBlocks` ou um novo projeto `Shared`) já nesta ADR**: rejeitada por ora — apenas dois usos reais existem hoje (`ExternalIntegrations`, produzindo o valor normalizado; `Reservations`, comparando contra ele), abaixo do critério de 3+ contextos que `Architecture Principles.md` §12 já usa para BuildingBlocks (mesmo critério que levou ADR-021 a rejeitar `ExternalIntegrations.Abstractions`). Promover a um utilitário compartilhado fica para quando um terceiro consumidor real existir.

## Consequências

### Positivas
- Resolve a necessidade real do Checkpoint 1 sem inventar um novo Bounded Context, sem reabrir ADR-019, e sem duplicar dados de Reservations em Communication.
- Mantém a superfície de acoplamento mínima, nomeada e testável por arquitetura — mesmo padrão já testado e homologado de ADR-014/019/024/026/027/028.
- A regra de elegibilidade estritamente `Confirmed` (sem janela temporal) é simples de auditar e testar exaustivamente (0/1/N candidatos, tenant cruzado, telefone diferente, normalização) — provado por 8 testes de integração reais contra Postgres.

### Riscos Aceitos
- `Architecture Principles.md` §14 passa a ter treze exceções nomeadas.
- A normalização de telefone é implementada de forma independente em dois lugares (`ExternalIntegrations.Infrastructure.Meta.MetaWebhookMessageProcessor` e `Reservations.Infrastructure.Communication.ReservationByGuestPhoneReader`) — pequena duplicação deliberadamente aceita (item 9) em vez de uma categoria de utilitário compartilhado prematura; cada lado tem seu próprio teste unitário/integração provando a regra idêntica.
- Um hóspede cujo checkout já foi `Closed` não pode mais iniciar uma nova conversa a partir do mesmo número sobre essa estadia — aceito como o comportamento correto do MVP (item 7); se um caso de uso real de pós-checkout surgir, exige sua própria decisão, não uma reabertura silenciosa desta ADR.
- Uma janela de TOCTOU trivial existe entre a leitura de candidatos e a criação da `Conversation` (uma Reservation poderia, em tese, ser cancelada nesse intervalo) — aceita nos mesmos termos já estabelecidos para toda leitura síncrona cross-context desta plataforma.

## Referências
- `documentacao do projeto/Architecture Principles.md`, Seção 14 (décima terceira exceção)
- ADR-019 (Purpose-limited Reservation Guest Contact Read for Communication) — precedente estrutural direto, propósito inverso
- ADR-021 (External Integrations ACL) — precedente da decisão de não criar uma categoria de projeto/utilitário compartilhado sem prova de 3+ usos
- ADR-026 / ADR-028 (mesmo par de contextos, propósitos distintos, cada um com sua própria ADR) — precedente direto para "não é uma extensão, é uma nova exceção"
- `Fase 11 - Agente de IA e Experiência Conversacional - Validacao e Homologacao.md`, Checkpoint 1 (Inbound Conversation Foundation)
- `IReservationByGuestPhoneReader.cs`, `ReservationCandidate.cs`, `ReservationByGuestPhoneReader.cs`
