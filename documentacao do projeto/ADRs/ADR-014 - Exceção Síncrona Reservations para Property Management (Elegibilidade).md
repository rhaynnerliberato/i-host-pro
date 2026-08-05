# ADR-014 — Exceção Síncrona Reservations → Property Management (Elegibilidade)

Status: Aceito
Data: 2026-08-04

## Contexto

`Architecture Principles.md` §14 estabelece que comunicação entre Bounded Contexts é sempre assíncrona, via Integration Events, com exatamente duas exceções de consulta síncrona controlada: Identity & Access e Configuration & Policy (ADR-002).

A implementação de Reservations — Fase 3, Incremento 1 (reserva manual operacional) exigiu, por especificação funcional já aprovada, que `CreateReservationCommandHandler` e `UpdateReservationCommandHandler` validem se um Imóvel existe, está ativo e qual sua capacidade, antes de criar ou atualizar uma reserva. Essa informação pertence exclusivamente ao contexto Property Management.

Essa consulta foi implementada como `IPropertyReservationEligibilityReader` (`PropertyManagement.Contracts`), com implementação em `PropertyManagement.Infrastructure`, antes da auditoria de continuidade de 2026-08-04 identificar que Property Management não constava na lista de exceções de `Architecture Principles.md` §14 nem em nenhum ADR — uma lacuna documental real, registrada e apresentada ao usuário sem correção silenciosa. Esta ADR resolve essa lacuna mediante decisão explícita do usuário: autorizar uma terceira exceção, estrita e específica — não uma autorização genérica de consulta síncrona a Property Management.

## Decisão

Está aprovada uma terceira exceção síncrona, estrita e específica: **Reservations pode consultar Property Management exclusivamente para obter a elegibilidade de um imóvel para receber uma reserva** — nunca para qualquer outra finalidade.

A exceção obedece obrigatoriamente a:

1. **Contrato público em `PropertyManagement.Contracts`** — `IPropertyReservationEligibilityReader` e `PropertyReservationEligibility`, já implementados dessa forma.
2. **Implementação somente em `PropertyManagement.Infrastructure`** — `PropertyReservationEligibilityReader`, confirmado como único implementador existente.
3. **Reservations não referencia** `PropertyManagement.Domain`, `PropertyManagement.Application`, `PropertyManagement.Infrastructure` ou `PropertyManagementDbContext`/o schema `property_management` diretamente — apenas `PropertyManagement.Contracts`. Regra validada automaticamente por `IHostPro.ArchitectureTests` (NetArchTest).
4. **Resposta mínima**: `PropertyId`, `IsActive`, `Capacity` — nunca uma projeção completa de Imóvel, endereço, proprietário ou qualquer outro campo que Reservations não precise. `PropertyReservationEligibility` já tem exatamente essa forma.
5. **Operação somente leitura** — `GetAsync` nunca modifica estado de Property Management.
6. **Nenhuma transação distribuída** — a leitura de elegibilidade conclui e sua conexão fecha completamente antes de qualquer transação de escrita de Reservations abrir (ver Seção "Fluxo Transacional").
7. **Nenhuma FK entre contextos** — `Reservation.PropertyId` é um identificador opaco, sem constraint de banco de dados apontando para `property_management.properties`; a integridade referencial é responsabilidade exclusiva desta consulta em tempo de aplicação.
8. **Nenhuma conexão ou transação de Property Management permanece aberta durante uma transação de escrita de Reservations** — requisito arquitetural central desta ADR, motivado pela correção da ordem transacional do `UpdateReservation` (ver Seção 3 da decisão executiva de 2026-08-04).
9. **Falhas continuam fechadas** — indisponibilidade, erro ou timeout na consulta de elegibilidade resulta em falha da operação de Reservations (`PropertyNotFound`/erro), nunca em uma reserva criada/atualizada sem confirmação positiva de elegibilidade.
10. **A exceção não autoriza consultas genéricas de Reservations a Property Management** — nenhum outro método, contrato ou consulta síncrona entre esses dois contextos está autorizado por esta ADR. Qualquer necessidade futura de uma nova consulta síncrona exige uma nova ADR.
11. **Eventual separação física dos contextos deve preservar o mesmo contrato** — se Property Management for extraído para um serviço separado (Architecture Principles §18), `IPropertyReservationEligibilityReader` se torna uma chamada de rede (gRPC/REST) com a mesma assinatura e mesma resposta mínima, sem alterar o contrato público.
12. **A janela de TOCTOU entre a leitura de elegibilidade e o commit da reserva é aceita e documentada** — mesmo precedente já aprovado em Property Ownership (Fase 2, Checkpoint 5): entre a consulta e a persistência, o estado do Imóvel em Property Management pode mudar (ex.: ser desativado). Esta ADR não introduz um mecanismo de lock cross-context para eliminar essa janela; o advisory lock de Reservations (`pg_advisory_xact_lock`) protege apenas contra conflitos de período dentro do próprio contexto Reservations.

## Alternativas Consideradas

- **Comunicação assíncrona via projeção local**: Reservations manteria uma projeção própria de elegibilidade de imóveis, atualizada por `PropertyActivated`/`PropertyDeactivated`/`PropertyArchived`/`PropertyCreated`. Elimina a consulta síncrona e a dependência de compilação em `PropertyManagement.Contracts`, mas introduz consistência eventual na validação de uma operação de escrita síncrona (criar/atualizar reserva), exigindo desenho adicional (o que fazer se a projeção local ainda não recebeu um evento de desativação recente) fora do escopo já aprovado para este incremento. Descartada por ora — pode ser reconsiderada em uma fase futura caso o padrão de consulta síncrona se mostre limitante.
- **Consulta síncrona genérica para Property Management** (qualquer contexto, qualquer finalidade): rejeitada explicitamente pelo usuário — mantém a superfície de acoplamento mínima e nomeada, consistente com o desenho das duas exceções originais (Identity & Access, Configuration & Policy), que também são estritas e não genéricas.

## Consequências

### Positivas
- Resolve a lacuna documental identificada na auditoria de continuidade sem exigir redesenho do incremento já implementado e testado.
- Mantém a superfície de acoplamento entre os dois contextos mínima, nomeada e testável por arquitetura.
- Preserva o precedente de TOCTOU aceito já estabelecido em Ownership, sem introduzir um novo padrão de exceção.

### Riscos Aceitos
- A janela de TOCTOU (item 12) significa que, em tese, uma reserva pode ser confirmada com base em um Imóvel que é desativado imediatamente após a leitura de elegibilidade e antes do commit. Risco aceito nos mesmos termos do precedente de Ownership — mitigação futura, se necessária, é uma decisão de fase posterior.
- `Architecture Principles.md` §14 passa a ter três exceções nomeadas em vez de duas — qualquer futura extração de Property Management para um serviço separado precisa preservar `IPropertyReservationEligibilityReader` como uma fronteira de rede explícita (item 11), o que a Seção 18 do documento já antecipava para as duas exceções originais.

## Referências
- `documentacao do projeto/Architecture Principles.md`, Seção 14 (atualizada por esta ADR)
- ADR-002 (Arquitetura da Solução) — regra geral e as duas exceções originais
- `Fase 2 - Property Management - Validacao e Homologacao.md`, Seção 4 (precedente de TOCTOU aceito em Ownership)
- `IPropertyReservationEligibilityReader.cs`, `PropertyReservationEligibility.cs`, `PropertyReservationEligibilityReader.cs`
- Auditoria de continuidade de 2026-08-04 (identificação da lacuna documental)
