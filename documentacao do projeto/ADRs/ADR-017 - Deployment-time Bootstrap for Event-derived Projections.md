# ADR-017 — Deployment-time Bootstrap for Event-derived Projections

Status: Aceito
Data: 2026-08-13

## Contexto

Toda projeção local alimentada por Integration Events (Housekeeping's `property_projection`, Reservations' `cleaning_schedule_projection`, e agora as quatro projeções de Dashboard & Reporting) compartilha a mesma vulnerabilidade estrutural: RabbitMQ (topic exchanges) nunca reproduz histórico para uma fila recém-vinculada. Uma projeção nova só recebe eventos publicados **depois** que seu consumer começa a escutar — qualquer dado que já existia no BC de origem antes disso fica permanentemente invisível ao consumer, sem nenhum mecanismo de auto-recuperação em runtime.

Este problema já se materializou uma vez, de forma concreta: Fase 7, Incremento 1, Checkpoint 3 (`property_not_found` em `CreateCleaning` para propriedades criadas antes do consumer de projeção de Housekeeping existir). A correção foi `PropertyProjectionBootstrap` — um mecanismo one-time, idempotente, vivendo em `tools/IHostPro.MigrationRunner`, executado após as migrações de schema, lendo `property_management.properties` e escrevendo `housekeeping.property_projection` dentro da mesma transação por tenant, respeitando RLS via `SET LOCAL app.tenant_id`. Na época, foi classificado deliberada e estritamente como "preocupação de migração de dados/deployment, nunca dependência de runtime entre Bounded Contexts" — e o usuário autorizou seu escopo exclusivamente para aquela única projeção, sem generalizar o mecanismo.

O Incremento 2 (Dashboard & Reporting Foundation) reproduz exatamente a mesma vulnerabilidade estrutural, multiplicada por quatro: `DashboardReservationProjection`, `DashboardCleaningProjection`, `DashboardPropertyProjection` e `DashboardOccurrenceProjection` precisam, cada uma, de um backfill equivalente para não nascerem cegas a todo dado operacional pré-existente. Reescrever `PropertyProjectionBootstrap` como quatro cópias ad-hoc, cada uma reinventando sua própria orquestração dentro de `Program.cs`, degradaria a legibilidade do `MigrationRunner` sem necessidade; por outro lado, construir uma engine de migração de dados genérica (DSL, discovery por reflection, scheduler, ETL framework) seria desproporcional ao problema real e violaria a mesma cautela contra abstração prematura já registrada pelo ADR-016 (Alternativa Rejeitada 2).

## Alternativas Rejeitadas

1. **Deixar cada bootstrap como uma função solta, chamada individualmente em `Program.cs`** (o padrão original de `PropertyProjectionBootstrap`, sem nenhuma abstração comum). Rejeitada: com cinco bootstraps reais (o de Housekeeping + os quatro de Dashboard), `Program.cs` se tornaria uma sequência não uniforme de chamadas ad-hoc, sem um ponto único de log/observação/falha consistente.
2. **DSL de migração de dados** (uma linguagem declarativa própria para descrever transformações origem→projeção). Rejeitada: complexidade desproporcional a cinco steps conhecidos e finitos; nenhum requisito real pede uma linguagem genérica.
3. **Engine genérica com discovery por reflection** (escanear assemblies procurando por convenção de nome/atributo). Rejeitada: esconde o que realmente roda atrás de convenção implícita, dificulta auditoria, e nenhum dos BCs deste projeto usa esse padrão em nenhum outro lugar (o próprio discovery de `IModuleDbContext` em `Program.cs` é uma exceção já estabelecida e explícita, não um precedente para expandir).
4. **Framework de ETL completo** (staging tables, pipelines configuráveis, retries automáticos multi-estágio). Rejeitada: nenhum dos cinco bootstraps reais precisa de mais do que um único `INSERT ... SELECT ... ON CONFLICT DO NOTHING` por tenant, dentro de uma transação.
5. **Scheduler/hosted service para reexecutar bootstraps periodicamente em runtime.** Rejeitada explicitamente: bootstrap é estritamente um evento de deployment/upgrade, nunca um processo contínuo — reintroduzir isso como responsabilidade de runtime era exatamente o "dependência de runtime entre Bounded Contexts" que a classificação original de `PropertyProjectionBootstrap` já vetava.

## Decisão

Generalização **mínima**: uma interface `IProjectionBootstrapStep` (dois membros — `Name` e `ExecuteAsync(CancellationToken)`), vivendo em `tools/IHostPro.MigrationRunner` (nunca em um projeto compartilhado/BuildingBlocks — isto não é infraestrutura de produção, é uma preocupação exclusiva desta ferramenta de deployment). `Program.cs` constrói uma lista explícita, tipada, de instâncias de `IProjectionBootstrapStep` e as executa sequencialmente, logando início/fim de cada uma. Nenhum discovery automático, nenhuma configuração externa, nenhuma ordem implícita — a lista em `Program.cs` é a única fonte de verdade sobre quais steps existem e em que ordem rodam.

`PropertyProjectionBootstrap` é adaptado para essa forma através de um adapter (`PropertyProjectionBootstrapStep`) que implementa `IProjectionBootstrapStep` delegando para o método estático já existente — **sem alterar sua semântica, sua assinatura pública ou seu SQL**. Os quatro novos bootstraps de Dashboard (`DashboardReservationProjectionBootstrapStep`, `DashboardCleaningProjectionBootstrapStep`, `DashboardPropertyProjectionBootstrapStep`, `DashboardOccurrenceProjectionBootstrapStep`) seguem o mesmo desenho: leem os schemas de origem (`reservations`, `housekeeping`) e escrevem exclusivamente no schema `dashboard`, por tenant, sob `SET LOCAL app.tenant_id`, com `ON CONFLICT DO NOTHING` garantindo idempotência.

**Princípio arquitetural, sem exceção:** em runtime, cada Bounded Context permanece 100% orientado a eventos — nenhuma projeção lê seu schema de origem diretamente fora deste mecanismo de deployment.

```
deployment:  source schema  →  projection bootstrap  (uma vez, por upgrade)
runtime:     Integration Events  →  projection updates  (contínuo, sempre)
```

Bootstrap nunca é executado por um handler Wolverine, nunca por um hosted service, nunca a cada request — apenas pelo `IHostPro.MigrationRunner`, como parte do processo de deployment/upgrade, exatamente como as migrações de schema EF Core que o antecedem no mesmo `Program.cs`.

**Exceção explícita, e apenas nesta ferramenta:** `IHostPro.MigrationRunner`, por ser tooling/deployment concern (nunca um processo de runtime do produto), está autorizado a executar leitura cross-schema dentro do mesmo banco físico PostgreSQL para inicializar uma projeção derivada — exatamente como `PropertyProjectionBootstrap` já fazia. **Isto não autoriza**: (a) `Dashboard.Infrastructure` (ou qualquer outro BC) ler `reservations.*`/`housekeeping.*`/`property_management.*` em runtime; (b) nenhum novo sync reader síncrono entre Bounded Contexts (a lista fechada de exceções em Architecture Principles §14 permanece inalterada); (c) generalizar este mecanismo para qualquer outra finalidade além de backfill de projeção derivada em deployment/upgrade.

## Consequências

### Positivas
- `Program.cs` ganha um ponto único, uniforme, de execução/log/falha para todo bootstrap de projeção — em vez de cinco chamadas ad-hoc estruturalmente distintas.
- `PropertyProjectionBootstrap` migra para a nova forma sem qualquer alteração de comportamento, SQL ou assinatura pública — coberto por regressão dos três testes preventivos já existentes (Fase 7, Checkpoint 3).
- Os quatro bootstraps de Dashboard reaproveitam a mesma disciplina de idempotência/RLS/transação-por-tenant já validada, sem reinventar o padrão.
- Mantém runtime 100% orientado a eventos — nenhum BC ganha uma nova dependência de leitura cross-schema fora deste mecanismo estritamente de deployment.

### Riscos Aceitos
- Cada novo bootstrap ainda exige código dedicado (nenhuma automação de discovery) — aceito deliberadamente (Alternativa Rejeitada 3): a lista explícita em `Program.cs` é a garantia de auditabilidade, não um custo a eliminar.
- `IProjectionBootstrapStep` não impõe nenhuma validação de que um step respeita RLS/idempotência — essa disciplina permanece responsabilidade de quem escreve cada step, verificada por teste dedicado (fresh-install/upgrade/rerun), não pela interface em si.

## Referências
- Fase 7, Incremento 1, Checkpoint 3 (`documentacao do projeto/Fase 7 - Agenda e Dashboard Operacional - Validacao e Homologacao.md`, §6) — a origem do problema e da primeira solução (`PropertyProjectionBootstrap`), não alterada em substância por este ADR.
- ADR-016 (Tenant-safe Execution Boundary for Persistent Wolverine Consumers) — mesma cautela contra abstração prematura (Alternativa Rejeitada 2 daquele ADR) aplicada aqui.
- Architecture Principles, Seção 14 — a lista fechada de exceções síncronas entre Bounded Contexts, que este ADR explicitamente não amplia.
- `IProjectionBootstrapStep.cs`, `PropertyProjectionBootstrapStep.cs`, `tools/IHostPro.MigrationRunner/Program.cs`.
