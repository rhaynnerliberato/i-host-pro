# ADR-025 — PIX Payment Boundary

Status: Aceito
Data: 2026-08-28

## Contexto

Fase 10, Checkpoint 3 (Early Check-in / Late Checkout Core) deixou `LateCheckoutRequest` estabilizado em `PendingPayment` quando a política resolvida exige PIX (`RequiresPix=true`) — um estado deliberadamente não-terminal, sem nenhuma mutação de agenda, sem nenhum evento publicado até este checkpoint, e sem nenhuma coluna de pagamento no schema `guest_operations` (ausência estrutural, nunca uma omissão de runtime — ADR-024 §B3).

Fase 10, Checkpoint 5 ("PIX/Payment Deterministic Foundation") fecha esse boundary: cria o Bounded Context `Payments`, o agregado `PixCharge`, e o fluxo assíncrono completo entre Guest Operations, Payments, External Integrations e Communication — sem provider real, sem webhook real, sem dinheiro real. O CP5 Decision Gate (read-only) já havia auditado a fonte de verdade (Documento 07 §9, Documento 10 §14, Documento 13 §9, Documento 19 §13) e confirmado que nenhuma dessas fontes especifica payload de eventos, enum de status, ou boundary de segurança — todas as decisões de design abaixo foram tomadas nesta etapa, com aprovação explícita, não inventadas silenciosamente.

## Decisão

### Ownership

- **Guest Operations** continua dono de `LateCheckoutRequest` e da decisão final de aprovação/negação do Late Checkout — nunca do ciclo de vida financeiro da cobrança.
- **Payments** (novo Supporting Bounded Context) é dono exclusivo de `PixCharge`: ciclo de vida financeiro, snapshot de `Amount`/`CurrencyCode`, idempotência de cobrança, correlação com o provider, confirmação/falha/expiração.
- **External Integrations** é dona de `IPixProvider`, do ACL do provider real (quando escolhido), das credenciais/referências, e da futura normalização de webhook.
- **Communication** é dona da entrega do QR/copia-e-cola ao hóspede e dos templates/mensagens.

### Boundary assíncrono Guest Operations → Payments

Guest Operations **não** chama Payments de forma síncrona. Ao entrar em `PendingPayment`, publica `LateCheckoutPaymentRequired` (`GuestOperations.Contracts`) — provider-neutro, payload mínimo (`TenantId`, `LateCheckoutRequestId`, `ReservationId`, `Amount`, `CurrencyCode`, `OccurredAtUtc`, metadados padrão). Payments é o único consumidor; cria seu próprio `PixCharge`.

Payments confirma de volta via `PixChargeConfirmed` (`Payments.Contracts`). Guest Operations é o único consumidor; localiza o `LateCheckoutRequest` por `LateCheckoutRequestId`, verifica `Status == PendingPayment`, e chama `Approve()` — reaproveitando integralmente o fluxo de aprovação já homologado no Checkpoint 3 (`LateCheckoutApproved` publicado, Workflow reagenda, Housekeeping reage se `UpdatesCleaning`, Communication notifica a Portaria). `LateCheckoutRequest.Approve()` foi estendido para aceitar tanto `Pending` quanto `PendingPayment` como estados de origem — exatamente a "transição em diante" que o próprio Checkpoint 3 já havia antecipado no doc comment de `PendingPayment`.

`UpdatesCleaning` não é persistido em `LateCheckoutRequest` — o aprovador re-lê `ILateCheckoutPolicyReader` (a mesma exceção geral de Configuration & Policy já concedida a Guest Operations) no momento da confirmação. Uma pequena janela de TOCTOU é aceita (a política pode, em tese, mudar entre a solicitação original e a confirmação) — mesmo precedente já documentado para toda leitura síncrona cross-context desta plataforma.

### `PixCharge`

Campos: `Id`, `TenantId`, `LateCheckoutRequestId`, `ReservationId`, `Amount`, `CurrencyCode`, `Status`, `ProviderChargeId?`, `QrCodePayload?`, `IdempotencyKey` (gerada internamente), `ExpiresAtUtc?`, `ConfirmedAtUtc?`, `FailedAtUtc?`, `CreatedAtUtc`, `UpdatedAtUtc`.

`Amount`/`CurrencyCode` são um snapshot único, tirado de `LateCheckoutPaymentRequired` — Payments nunca relê ou recalcula `LateCheckoutPolicy`. `CurrencyCode` é **BRL-only** neste checkpoint — `Create` rejeita qualquer outro valor. `Percentage` continua oficialmente não suportado (ADR-024 §B3, decisão não reaberta).

**`QrCodePayload` é persistido em coluna comum** (`payments.pix_charges.qr_code_payload`), protegido pelos mesmos mecanismos de todo outro dado tenant-owned (RLS `ENABLE`/`FORCE` + `tenant_isolation`) — decisão explícita deste checkpoint. Classificação: dado operacional de pagamento sensível, **não** um segredo/credencial (nunca roteado pela convenção `*SecretReference`, reservada a segredos geridos externamente). Nunca aparece em log, Integration Event, query string, mensagem de exceção, atributo de telemetria, ou saída de debug tipo "ProviderMessage". Nenhuma criptografia de coluna é introduzida neste checkpoint — nenhum padrão desse tipo existe hoje nesta base, e inventar um unilateralmente seria uma decisão arquitetural maior, fora do escopo deste boundary. Registrado como follow-up de Production hardening: `QrCodePayloadAtRestProtectionReviewRequired=true` (não implica `ProductionReady=true`).

### `PixChargeStatus`

`Pending, Confirmed, Failed, Expired, Cancelled` — sem valor `Created` separado (um `PixCharge` já nasce `Pending`; `ProviderChargeId`/`QrCodePayload` podem ficar `null` até o provider aceitar).

Matriz de transição aprovada:

- `Pending → Confirmed`: normal.
- `Confirmed → Confirmed` (confirmação duplicada): no-op idempotente.
- `Confirmed → Failed`/`Confirmed → Expired`: regressão, no-op — uma confirmação real de dinheiro recebido tem precedência sobre qualquer sinal negativo ou fora de ordem.
- `Failed → Confirmed`/`Expired → Confirmed`: avanço aplicado — mesma razão acima.
- `Cancelled → Confirmed`: **não decidida** — lança `PixChargeCancelledConfirmationConflictException` em vez de decidir silenciosamente. Nada no código deste checkpoint jamais define `Cancelled`, então este ramo é inalcançável hoje, mas o guard existe explicitamente.
- `Pending → Failed`: aplicado quando o provider rejeita a criação ou falha tecnicamente.

`PaymentFailed`/`Expired` não denegam nem cancelam o `LateCheckoutRequest` — permanece `PendingPayment`, sem `LateCheckoutDenied`. Uma nova tentativa de cobrança é uma operação explícita e futura, fora de escopo deste checkpoint (nenhum endpoint de retry foi criado).

### Idempotência e cardinalidade

No máximo uma `PixCharge` **ATIVA** (`Pending`) por `(TenantId, LateCheckoutRequestId)` — índice único parcial (`status = 'Pending'`), mesma convenção de `LateCheckoutRequest`/`EarlyCheckInRequest`. O handler de `LateCheckoutPaymentRequired` verifica essa mesma condição antes de criar — uma entrega duplicada do mesmo evento nunca cria uma segunda cobrança. A constraint de banco permanece defesa em profundidade, nunca o mecanismo primário.

### Confirmação — o seam provider-neutro

Este checkpoint não tem provider real nem webhook real. `PixChargeConfirmationReceived` (`Payments.Contracts` — mensagem cross-context, não um `IntegrationEvent`, mirroring `CreateCleaningForReservation`/`CloseReservation`) representa o fato provider-neutro "uma cobrança PIX foi confirmada". É código de produção legítimo — representa o seam que uma futura normalização de webhook em External Integrations produziria, sem qualquer mudança no domínio Payments. Payload mínimo: `TenantId`, `PixChargeId`, `ConfirmedAtUtc`, `CorrelationId`, `CausationId?` — sem payload de provider, sem QR, sem PII de pagador, sem segredo de provider.

Único publicador hoje: o harness de teste E2E, via um envio real Wolverine/RabbitMQ (exchange dedicada `payments-commands`, Direct, routing key `pix_charge_confirmation_received`) — nunca um endpoint HTTP test-only, nunca lógica de teste embutida no domínio.

### Fake provider

`IPixProvider` (`ExternalIntegrations.Contracts`) — porta síncrona provider-neutra (Exceção 10). `FakePixProvider` (`ExternalIntegrations.Infrastructure`) é a única implementação: sempre aceita, determinística, sem chamada de rede, sem dinheiro real — registrada incondicionalmente (não gated a Development, diferente de `MetaWhatsAppMessagingProvider`), porque nenhum provider real existe para conflitar com ela. Escolha/integração de um provider real permanece **DEFERRED**; Asaas é registrado apenas como candidato técnico líder para uma futura prova de sandbox (documentação pública mais completa), o que não implica `ProductionProviderSelected=true`.

### Segurança / secrets

Nenhuma credencial real existe neste checkpoint — nenhum placeholder de secret é criado. Quando um provider real existir, a convenção `*SecretReference` já estabelecida será reaproveitada. O backend de secret de Production continua bloqueador transversal (ADR-011), sem relação especial com Payments.

### RLS

`payments.pix_charges` é tenant-owned: `ENABLE`/`FORCE ROW LEVEL SECURITY`, policy `tenant_isolation`, fail-closed (`current_setting('app.tenant_id', true)` ausente ⇒ zero linhas). Nenhuma tabela global.

### Webhook futuro

Não implementado. Nenhum verificador de assinatura, nenhuma tabela de roteamento por tenant, nenhum DTO de provider foi criado — dependem da escolha de um provider real, fora de escopo.

## Alternativas Consideradas

- **Chamada síncrona Guest Operations → Payments**: rejeitada — decisão explícita do mandato deste checkpoint; o boundary é assíncrono por design, coerente com a natureza intrinsecamente assíncrona de uma confirmação de pagamento externo.
- **Persistir o QR criptografado com Data Protection API**: rejeitada por ora — estabeleceria um precedente de criptografia de coluna inédito nesta base, uma decisão arquitetural maior que merece sua própria revisão, não uma decisão de passagem dentro deste checkpoint.
- **Regenerar o QR sob demanda via nova chamada ao provider**: rejeitada — não há garantia provider-neutra de que `CreateChargeAsync` seja idempotente e retorne exatamente o mesmo payload; a cobrança é criada uma vez, o payload resultante é persistido, a entrega lê esse estado persistido.
- **Endpoint HTTP test-only para simular confirmação**: rejeitada — criaria uma superfície de API não autorizada por padrão (mandato item 44) só para conveniência de teste.

## Consequências

### Positivas
- Fecha o boundary de pagamento sem inventar um provider real, sem mover dinheiro real, e sem duplicar a lógica de aprovação já homologada no Checkpoint 3.
- `PixChargeConfirmationReceived` é um seam genuíno, reaproveitável quando um provider real for integrado — nenhum retrabalho de domínio esperado.

### Riscos Aceitos
- `Architecture Principles.md` §14 passa a ter onze exceções nomeadas.
- O boundary de persistência do QR (coluna comum, sem criptografia) é aceito conscientemente para este checkpoint — revisão de Production hardening fica registrada como follow-up, não como bloqueador deste checkpoint.
- `Cancelled → Confirmed` permanece um guard não exercitado — se um caminho de cancelamento futuro chegar a produzir esse cenário, exige nova decisão explícita, nunca silenciosa.

## Referências
- `documentacao do projeto/Architecture Principles.md`, Seção 14 (décima exceção)
- ADR-024 (Guest Operations Boundary and Checkout Orchestration), §B3/§B6 — o exato ponto de parada que este ADR retoma
- ADR-021 (Communication → External Integrations) — precedente estrutural direto para a Exceção 10
- ADR-027 (Communication to Payments Secure PIX Delivery) — Exceção 11, o boundary complementar de entrega
- `Fase 10 - Check-in, Checkout e Operacoes do Hospede - Validacao e Homologacao.md`, Checkpoint 5
