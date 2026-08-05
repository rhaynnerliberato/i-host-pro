# Fase 3 — Reservation Management — Validação e Homologação

Versão: 1.2

Status: Oficial — Fase 3 concluída. Homologação aprovada tecnicamente (Seção 13) e aprovação final do usuário recebida. Commit funcional `1eb455e89e77fbc3957cc42b2886a613210beb3d` ("feat(reservations): implement reservation management") realizado em `feature/reservations-core`. Push da branch ainda pendente neste momento.

---

## 1. Objetivo

Este documento registra a validação e homologação real do Incremento 1 da Fase 3 (Reservation Management), conforme `Plano Executivo de Desenvolvimento por Fases.md`.

Este documento não repete decisões arquiteturais já registradas em `Architecture Principles.md`, no Documento 07 (Catálogo de Eventos de Domínio) ou nas ADRs — apenas registra a evidência de validação, as decisões tomadas durante este incremento e o histórico de correções encontradas durante a homologação, conforme `ai-rules/06 - Definition of Done.md`.

## 2. Escopo

Reserva manual operacional: criação, consulta (detalhe e listagem), atualização (PATCH presence-aware), cancelamento, validação de imóvel/capacidade contra o contexto Property Management, conflito de agenda (advisory lock), auditoria própria (`reservations.reservation_audit_log`) e três Integration Events (`ReservationCreated`, `ReservationUpdated`, `ReservationCancelled`).

## 3. Fora de escopo (não alterado nesta fase)

Agenda unificada, dashboard operacional, workflows, comunicação/WhatsApp, check-in/checkout, housekeeping, AI Agent, frontend — conforme `Plano Executivo de Desenvolvimento por Fases.md`, Fases 5-11.

## 4. Decisões arquiteturais tomadas neste incremento

### 4.1 Exceção síncrona Reservations → Property Management (elegibilidade)

`Architecture Principles.md` §14 originalmente listava apenas duas exceções de consulta síncrona (Identity & Access, Configuration & Policy). A consulta síncrona `IPropertyReservationEligibilityReader` (Reservations → Property Management, exclusivamente para elegibilidade de imóvel) foi identificada, durante a auditoria de continuidade de 2026-08-04, como não coberta por essas duas exceções nem por nenhum ADR. Registrada formalmente como uma terceira exceção estrita e específica em **ADR-014**, com a lista completa de restrições (contrato público mínimo, nenhuma FK física, nenhuma transação de dois contextos simultânea, falhas fechadas, TOCTOU aceito nos mesmos termos de Ownership). `Architecture Principles.md` §14/§18 atualizados de acordo.

### 4.2 Ordem transacional do `UpdateReservation`

Corrigida para o fluxo de 14 passos aprovado (leitura de snapshot tenant-aware/RLS fora de qualquer transação de escrita → cálculo do PropertyId prospectivo → consulta de elegibilidade concluída e fechada → transação de escrita abre → releitura do agregado → comparação explícita do `xmin` contra o snapshot inicial → `ReservationConcurrencyConflict` sem retry em caso de divergência → advisory lock → conflito de datas → persistência). Nenhuma transação de dois contextos permanece aberta simultaneamente. Ver `UpdateReservationCommandHandler.cs` para o fluxo comentado passo a passo.

## 5. Endpoints

| Método | Rota | Política |
|---|---|---|
| POST | `/api/v1/reservations` | `RESERVATIONS:MANAGE` |
| GET | `/api/v1/reservations` | `RESERVATIONS:MANAGE` |
| GET | `/api/v1/reservations/{id}` | `RESERVATIONS:MANAGE` |
| PATCH | `/api/v1/reservations/{id}` | `RESERVATIONS:MANAGE` |
| POST | `/api/v1/reservations/{id}/cancel` | `RESERVATIONS:MANAGE` |

## 6. Permissões

`RESERVATIONS:MANAGE` associada a ADMIN e OPERATOR (seed em `IdentityCatalogSeed.cs`) — confirmado contra `Documento 09 — Catálogo Completo de Atores e Permissões.txt` §15 (Matriz Simplificada: Reservas — Admin=X, Operador=X, Faxineira=–, Proprietário=L, IA=L) e §6 (Operador: "Cadastrar reservas manuais"). Nenhuma alteração de seed necessária — já estava correto desde a implementação original.

## 7. Banco de dados e RLS

Schema `reservations` (migração `20260804175715_InitialCreate`):

- `reservations.reservations` — tenant-owned, `ENABLE`/`FORCE ROW LEVEL SECURITY`, política `tenant_isolation`. `PropertyId` é um Guid opaco, sem FK física para `property_management.properties` (elegibilidade validada em tempo de aplicação via `IPropertyReservationEligibilityReader`, nunca pelo banco). Concorrência otimista via `xmin` (coluna sombra, `IsRowVersion()`). Três índices: `(tenant_id, check_in_at, id)`, `(tenant_id, property_id, check_in_at)`, `(tenant_id, status, check_in_at)`.
- `reservations.reservation_audit_log` — tenant-owned, `ENABLE`/`FORCE ROW LEVEL SECURITY`, append-only (papel `ihostpro_app` sem `GRANT UPDATE/DELETE`).
- Schema de mensageria `reservations_messaging` — outbox durável Wolverine, isolado dos schemas `identity_messaging`/`property_management_messaging`.

Validado por `ReservationsFoundationTests.cs` (15 testes, Seção 10) — inclui verificação explícita de `pg_roles.rolbypassrls = false` para `ihostpro_app` e de `pg_class.relrowsecurity`/`relforcerowsecurity = true` para a tabela `reservations`.

## 8. Eventos

Três Integration Events, exchange `reservation-events` (topic), routing keys `reservation_created`/`reservation_updated`/`reservation_cancelled` — documentados em detalhe no Documento 07 §27 (payloads e roteamento). Nenhum carrega nome do hóspede, telefone, datas ou quantidade de hóspedes — apenas identificadores e, para `ReservationUpdated`, códigos estáveis de campo alterado.

## 9. Auditoria

`reservations.reservation_audit_log`, um registro por criação/atualização-com-mudança/cancelamento, na mesma transação da escrita de domínio. `ChangedFields` armazena apenas nomes de campo (snake_case), nunca valores. PATCH idempotente (sem mudança efetiva) não gera auditoria nem evento.

## 10. Concorrência

Dois mecanismos independentes, cobrindo janelas diferentes:

1. **Conflito de agenda** (duas reservas sobrepostas para o mesmo imóvel): `pg_advisory_xact_lock` sobre `(tenantId, propertyId)`, serializando o Create/Update dentro da transação de escrita. Provado deterministicamente por `Two_deterministically_synchronized_creates_prove_the_advisory_lock_alone_serializes_the_conflict_check` (3/3 execuções aprovadas nesta sessão) e `Two_genuinely_concurrent_creates_for_the_same_overlapping_period_allow_only_one_to_succeed`.
2. **Concorrência otimista no Update** (a mesma reserva sendo modificada entre o snapshot pré-transação e a escrita): comparação explícita de `xmin` (Seção 4.2). Provado deterministicamente por `A_row_changed_between_the_snapshot_and_the_write_transaction_fails_with_ReservationConcurrencyConflict_and_never_audits_or_publishes` (integração, PostgreSQL real, mutação concorrente genuína via SQL direto) e `Update_returns_409_when_the_row_changed_between_the_snapshot_and_the_write_transaction` (HTTP real).

## 11. Defeitos reais encontrados e corrigidos durante a homologação

### 11.1 `ReservationConflictGuard` rejeitava qualquer offset não-UTC com erro 500

**Sintoma**: `POST /api/v1/reservations` com `checkInAt`/`checkOutAt` em qualquer offset diferente de UTC (ex.: `-03:00`, o caso mais comum no Brasil) retornava `500` não tratado.

**Causa**: `HasConflictingReservationAsync` passava os valores crus do comando (não normalizados) diretamente para uma query EF contra colunas `timestamp with time zone` — o driver Npgsql exige offset exatamente zero.

**Correção**: normalização para UTC (`.ToUniversalTime()`) no próprio `ReservationConflictGuard`, mesma convenção já usada em `Reservation.Create`/`Reschedule`. Arquivo: `ReservationConflictGuard.cs`. Nenhum outro ponto do fluxo precisou de alteração.

### 11.2 "Offset explícito obrigatório" nunca foi realmente implementado

**Sintoma**: o doc comment de `CreateReservationRequest.cs` afirmava que um `DateTimeOffset` sem offset explícito falhava a deserialização — a homologação HTTP real provou que `System.Text.Json` aceita esse valor silenciosamente, assumindo UTC.

**Decisão do usuário**: implementar a validação real (opção recomendada, entre três apresentadas).

**Correção**: novo `RequireExplicitOffsetDateTimeOffsetConverter` (`IHostPro.Contexts.Reservations.Api.Http`), aplicado via atributo em `CreateReservationRequest.CheckInAt`/`CheckOutAt` e reaproveitado por `OptionalJsonConverter<T>` (caso especial para `T == typeof(DateTimeOffset)`) em `UpdateReservationRequest`. Escopo estritamente local a Reservations — não registrado globalmente em `Program.cs`, então nenhum outro Bounded Context é afetado.

## 12. Testes

Ver Seção 13 para a execução final. Suítes de Reservations após este incremento:

| Suíte | Total |
|---|---|
| Unitários (`IHostPro.Contexts.Reservations.Tests.Unit`) | 50 |
| Integração — `ReservationCommandHandlerTests` (ISender direto) | 15 |
| Integração — `ReservationsEndpointsTests` (HTTP real, JWT real) | 30 |
| Integração — `ReservationsFoundationTests` (RLS/schema) | 15 |
| Arquitetura (Reservations*) | 20 |

Lacunas mínimas identificadas pela auditoria de continuidade e fechadas neste incremento: 401/403, RLS fail-closed, offset HTTP obrigatório (Create e Update), normalização UTC, intervalo inválido, filtro por interseção de período, ordenação determinística, PATCH real via HTTP (`guestPhone` null/omitido), conflito de concorrência via HTTP real, cancelamento repetido via HTTP, PII ausente estrutural e no payload serializado real dos três eventos.

## 13. Homologação final

Rodada única, executada em 2026-08-04, ambiente Docker efêmero (Testcontainers — PostgreSQL 16, RabbitMQ 3 `rabbitmq:3-management-alpine`), .NET SDK 10, build Release.

| Etapa | Resultado |
|---|---|
| 1. Build Release da solução completa (`dotnet build IHostPro.sln -c Release`) | 0 erros, 0 avisos |
| 2. Testes de arquitetura (`IHostPro.ArchitectureTests`) | 117/117 aprovados |
| 3. Testes unitários de Reservations | 50/50 aprovados |
| 4. Testes de integração completos de Reservations (`ReservationCommandHandlerTests` + `ReservationsEndpointsTests` + `ReservationsFoundationTests`) | 52/52 aprovados |
| 5. Testes focados — Property Management (eligibility reader) | Não existe um arquivo dedicado só a `PropertyReservationEligibilityReader` no lado de Property Management — a leitura é exercida de ponta a ponta, contra o schema `property_management` real, pelos próprios testes de Reservations. Reexecutados isoladamente: `Create_for_a_nonexistent_property_returns_404`, `Create_for_an_inactive_property_returns_400`, `Create_exceeding_the_propertys_capacity_returns_400` — 3/3 aprovados |
| 6. Testes focados — Identity (`RESERVATIONS:MANAGE`) | Mesma observação — sem arquivo dedicado isolado; a política é exercida de ponta a ponta pelo `PermissionAuthorizationHandler` real contra o catálogo seedado real. Reexecutados isoladamente: `Create_with_a_role_lacking_RESERVATIONS_MANAGE_returns_403`, `Create_as_ADMIN_or_OPERATOR_succeeds` (ADMIN, OPERATOR) — 3/3 aprovados |
| 7. `WolverineThreeStoreCompositionTests` (classe completa, uma execução) | 4/4 aprovados — inclui `Reservations_events_survive_a_rabbitmq_outage_and_deliver_on_recovery_through_the_same_provisioned_topology` (RabbitMQ real, Program.cs real) |
| 8. `MigrationRunner` duas vezes no mesmo ambiente efêmero | Embutido na execução do item 7 (`MigrationRunner_provisions_rabbitmq_topology_idempotently_and_the_real_host_delivers_through_it`): 1ª execução exit 0, todas as três exchanges (`identity-events`/`property-management-events`/`reservation-events`) ausentes antes e presentes depois; 2ª execução exit 0, idempotente, todas ainda presentes — nenhuma execução standalone adicional realizada, redundante com o item 7 |
| 9. Homologação HTTP real (Host real, PostgreSQL real, RabbitMQ real, Identity real) | Host real via composição idêntica a `Program.cs` (`ReservationsEndpointsTests`, JWT real emitido/validado pelo stack real do Identity, PostgreSQL real) + RabbitMQ real via `WolverineThreeStoreCompositionTests` (item 7) — ver Seção 13.1 para os 20 cenários |

### 13.1 Cenários HTTP validados (mínimo exigido)

| Cenário | Teste |
|---|---|
| 401 | `Create_without_a_token_returns_401` |
| 403 | `Create_with_a_role_lacking_RESERVATIONS_MANAGE_returns_403` |
| Criação | `Create_as_ADMIN_or_OPERATOR_succeeds` |
| Listagem | `List_with_from_and_to_returns_only_reservations_whose_period_intersects_the_range`, `List_orders_deterministically_by_check_in_then_id` |
| Detalhe | `GetById_for_an_existing_reservation_returns_200` |
| Atualização | `Update_with_guestPhone_explicit_null_removes_it_via_real_http`, `Update_with_guestPhone_omitted_keeps_it_unchanged_via_real_http` |
| `guestPhone` null | `Update_with_guestPhone_explicit_null_removes_it_via_real_http` |
| Cancelamento | `Cancelling_twice_returns_409_the_second_time` (primeira chamada) |
| Cancelamento repetido | `Cancelling_twice_returns_409_the_second_time` (segunda chamada) |
| Conflito de período | `An_overlapping_reservation_for_the_same_property_returns_409_with_ReservationDateConflict` |
| Limite de capacidade | `Create_exceeding_the_propertys_capacity_returns_400_with_PropertyCapacityExceeded` |
| Imóvel inexistente | `Create_for_a_nonexistent_property_returns_404` |
| Imóvel inativo | `Create_for_an_inactive_property_returns_400_with_PropertyNotActive` |
| Isolamento de tenant | `GetById_for_a_cross_tenant_reservation_returns_404` |
| Offset obrigatório | `Create_with_checkInAt_missing_an_explicit_offset_returns_400`, `Create_with_checkOutAt_missing_an_explicit_offset_returns_400`, `Update_with_checkInAt_missing_an_explicit_offset_returns_400` |
| Interseção de período | `List_with_from_and_to_returns_only_reservations_whose_period_intersects_the_range` |
| Concorrência | `Update_returns_409_when_the_row_changed_between_the_snapshot_and_the_write_transaction` |
| Auditoria | `Creating_a_reservation_via_real_http_writes_exactly_one_reservation_created_audit_entry` |
| Outbox | `WolverineThreeStoreCompositionTests.Reservations_events_survive_a_rabbitmq_outage_and_deliver_on_recovery_through_the_same_provisioned_topology` (item 7) |
| Ausência de PII | `The_ReservationCreated/Updated/Cancelled_events_real_serialized_payload_carries_no_...` (unitários, payload serializado real) |

## 14. Inventário Git

Branch `feature/reservations-core` (preservada, nenhuma alteração de estado do git). `git diff --check`: sem erros reais (apenas avisos de normalização de fim de linha).

| Status | Total |
|---|---|
| Modificados (M) | 17 |
| Removidos (D) | 0 |
| Novos | 101 |
| **Total** | **118** |

Novos, por diretório: `Reservations.Application` 31, `Reservations.Infrastructure` 19, `Reservations.Tests.Unit` 15, `Reservations.Api` 14, `Reservations.Contracts` 5, `Reservations.Tests.Integration` 4, `Reservations.Domain` 4, `PropertyManagement.Contracts` 2, `PropertyManagement.Infrastructure` 1, `ArchitectureTests` (3 arquivos Reservations) 3, documentação nova (Plano Executivo, ADR-014, este documento) 3.

## 15. Status final

Incremento 1 da Fase 3 — implementação, correção transacional do `UpdateReservation`, dois defeitos reais encontrados e corrigidos durante a homologação, lacunas mínimas de teste fechadas, documentação sincronizada. Homologação final aprovada tecnicamente (Seção 13) e aprovação final do usuário recebida. **Commit funcional `1eb455e89e77fbc3957cc42b2886a613210beb3d` realizado em `feature/reservations-core` · Fase 3 concluída · push da branch ainda pendente neste momento.** Fase 4 (Frontend) não iniciada.
