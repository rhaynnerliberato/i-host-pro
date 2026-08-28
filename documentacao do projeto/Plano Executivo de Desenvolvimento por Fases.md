# Plano Executivo de Desenvolvimento por Fases

Versão: 1.0
Status: Oficial

## 1. Propósito

Este documento registra o plano executivo de desenvolvimento do iHostPro, dividido em fases ordenadas por dependência técnica, conforme exigido por `Documento 99 - Development Authorization.txt` (seção "PLANEJAMENTO") e pela hierarquia de fontes de `ai-rules/00 - Engineering Constitution.md` (§6).

Este é o único documento do workspace que define a sequência oficial de fases do projeto. Nenhum outro documento — incluindo relatórios de homologação, ADRs ou catálogos de produto — deve ser interpretado como plano executivo prospectivo.

## 2. Regras de Governança do Plano

- Uma fase somente é considerada iniciada oficialmente quando referenciada por este documento.
- O escopo detalhado de cada fase deve ser refinado e aprovado antes de sua implementação.
- Nenhuma fase pode ser iniciada antes da conclusão e homologação da fase da qual depende tecnicamente, salvo exceção explicitamente registrada aqui.
- Alterações a este plano exigem atualização deste documento e, quando afetarem decisões arquiteturais já registradas, um ADR correspondente.

## 3. Sequência Oficial de Fases

### Fase 0 — Fundação da Plataforma

**Status:** Concluída.
**Escopo:** BuildingBlocks, Hosts (`IHostPro.Api`, `IHostPro.Worker`), `IHostPro.MigrationRunner`, infraestrutura e estrutura base da solução.
**Commit:** `16560e1`.

### Fase 1 — Identity & Access

**Status:** Concluída.
**Escopo:** autenticação, sessões, JWT, RBAC, usuários, papéis, permissões e senhas.
**Commits:** `458f0f7`, `f26c82c`, `4e726eb461bb48b762006d13bca2f50a6e711e0a`.
**Homologação:** `Fase 1 - Identity and Access - Validacao e Homologacao.md`.

### Fase 2 — Property Management

**Status:** Concluída.
**Escopo:** condomínios, imóveis, lifecycle, ownership e consultas do proprietário.
**Commit:** `f714a995a621008e995c8123d2e3b2a508c9ca8f`.
**Homologação:** `Fase 2 - Property Management - Validacao e Homologacao.md`.

### Fase 3 — Reservation Management

**Status:** Em conclusão.
**Escopo atual:** reserva manual operacional, consulta, atualização, cancelamento, validação de imóvel/capacidade, conflitos de agenda, auditoria e eventos.
**Implementação:** a implementação existente na branch `feature/reservations-core` pertence oficialmente à Fase 3.
**Homologação:** `Fase 3 - Reservation Management - Validacao e Homologacao.md`.

### Fase 4 — Frontend Foundation e Administração Operacional

Primeira fase com interface utilizável no navegador.

**Escopo de alto nível:**
- criação do projeto Angular;
- autenticação e renovação de sessão;
- layout, navegação e autorização;
- administração de usuários;
- condomínios e imóveis;
- reservas manuais;
- dashboard operacional inicial;
- cliente HTTP gerado por NSwag;
- Angular Material;
- Transloco;
- responsividade;
- Playwright para fluxos críticos.

**Pré-condição:** a Fase 4 somente começa depois de a Fase 3 ser homologada, commitada e publicada.

### Fase 5 — Configuration & Policy

Configurações hierárquicas, políticas operacionais e regras variáveis. Escopo detalhado a refinar e aprovar antes da implementação.

### Fase 6 — Housekeeping e Portal da Faxineira

Ciclo de faxinas, atribuição, execução, checklist, ocorrências e portal. Escopo detalhado a refinar e aprovar antes da implementação.

**Status:** Concluída funcionalmente (Incremento 1 — Housekeeping Foundation + Incremento 2A — Portal da Faxineira Core). Files/Evidências (fotos/vídeos de ocorrências/checklist) permanece como escopo futuro deferido, não implementado, sem fase de implementação atribuída.
**Homologação:** `Fase 6 - Housekeeping - Validacao e Homologacao.md`.

### Fase 7 — Agenda e Dashboard Operacional

Agenda unificada, visualizações diária/semanal/mensal e indicadores operacionais. Escopo detalhado a refinar e aprovar antes da implementação.

### Fase 8 — Workflow Orchestration

Coordenação de eventos e automações entre os contextos. Escopo detalhado a refinar e aprovar antes da implementação.

### Fase 9 — Comunicação e Integrações do MVP

Templates, notificações, WhatsApp e sincronização inicial com Airbnb. Escopo detalhado a refinar e aprovar antes da implementação.

**Status:** **Concluída — DEFINITIVAMENTE HOMOLOGADA NO NÍVEL MVP, COM BLOCKERS DE PRODUCTION DOCUMENTADOS.** Checkpoint 1 (Communication Foundation), Checkpoint 2 (WhatsApp real, com blockers operacionais de Production documentados — app Meta não publicado, entre outros), Checkpoint 3 (Airbnb, concluído no nível de fundação determinística — CP3.2 homologado sem integração HTTP/OAuth/sync real com a Airbnb, bloqueado exclusivamente por `AirbnbPartnerAccessAvailable=false`) e Checkpoint 4 (homologação final da fase) todos concluídos. `ProductionReady=false` — ver o documento de homologação para a lista consolidada de blockers de Production.
**Homologação:** `Fase 9 - Comunicacao e Integracoes do MVP - Validacao e Homologacao.md`.

### Fase 10 — Check-in, Checkout e Operações do Hóspede

Check-in, checkout, early check-in, late checkout e comunicação com portaria.

**Status:** Em andamento. Checkpoint 0 (Architecture & Product Decision Gate, read-only), Checkpoint 1 (Guest Operations Foundation), Checkpoint 2 (Check-in/Checkout Core), Checkpoint 3 (Early Check-in / Late Checkout Core), Checkpoint 4 (Portaria Notification Foundation) e Checkpoint 5 (PIX/Payment Deterministic Foundation) concluídos — novo Bounded Context Guest Operations, `ReservationStatus.Closed`, comando cross-context `CloseReservation`, eventos `GuestCheckedIn`/`GuestCheckedOut`, segundo orquestrador de Workflow, primeiro endpoint HTTP público (`GuestOperations.Api`, check-in/checkout), gatilho de criação via coreografia (`ReservationCreated`), estratégia de upgrade para Reservations preexistentes; no Checkpoint 3, os agregados `EarlyCheckInRequest`/`LateCheckoutRequest` com decisão automática, as duas primeiras exceções síncronas cross-context de Guest Operations (`IReservationScheduleReader`/`ICleaningReadinessReader`, Architecture Principles §14 Exceções 7/8), reagendamento real da Reservation via dois novos orquestradores de Workflow, quatro novos Integration Events, e a reação real (auditoria, sem mutação de agenda inventada) de Housekeeping; no Checkpoint 4, um Architecture & Product Decision Gate read-only prévio, o novo agregado `FrontDeskContact` (Property Management, um contato ativo por Condomínio), a nona exceção síncrona cross-context (`IFrontDeskContactReader`, Communication → Property Management, ADR-026 dedicada, Architecture Principles §14 Exceção 9), a extensão factual de ADR-019 (`GuestName`), três novos consumidores de Communication para notificação operacional de Portaria; e, no Checkpoint 5, um novo Bounded Context Payments (sem API pública), o agregado `PixCharge` (BRL-only, matriz de transição de status completa, idempotência de cobrança), o boundary assíncrono Guest Operations ↔ Payments (`LateCheckoutPaymentRequired`/`PixChargeConfirmed`), a décima e décima primeira exceções síncronas cross-context (`IPixProvider`, Payments → External Integrations, ADR-025 dedicada, Architecture Principles §14 Exceção 10; `IPixChargeDeliveryReader`, Communication → Payments, ADR-027 dedicada, Exceção 11), `FakePixProvider` determinístico (zero provider real, zero dinheiro real), e a entrega segura do QR PIX ao hóspede — tudo provado por testes reais (unitários, arquitetura, HTTP real com JWT real, e E2E via RabbitMQ/Worker/Postgres reais). `Percentage` (cálculo de desconto/acréscimo proporcional) permanece oficialmente não suportado, pendente de um domínio de pricing futuro. Credencial de acesso permanece deferida (sub-gate de segurança pendente antes da homologação final da Fase 10). Provider PIX real, webhook real, e Refunds permanecem deferidos a checkpoint futuro.
**Homologação:** `Fase 10 - Check-in, Checkout e Operacoes do Hospede - Validacao e Homologacao.md`.

### Fase 11 — AI Agent

Atendimento contextual, Tools autorizadas, decisões auditáveis e escalonamento humano. Escopo detalhado a refinar e aprovar antes da implementação.

### Fase 12 — Hardening, Deploy e Piloto do MVP

Segurança final, observabilidade, CI/CD, implantação e validação com usuários. Referenciada previamente em `ADR-011 - Ambiente de Desenvolvimento e CI-CD.md` como o gate de decisão final de CI/CD e provedor de nuvem. Escopo detalhado a refinar e aprovar antes da implementação.

## 4. Observação sobre as Fases 5 a 12

As Fases 5 a 12 representam direcionamento executivo de alto nível, aprovado quanto à sua existência e ordem. O escopo detalhado de cada uma deverá ser refinado e submetido à aprovação antes do início de sua implementação, seguindo o mesmo processo já aplicado às Fases 0 a 3.
