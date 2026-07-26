# ADR-003 — Persistência e Multi-Tenant

Status: Aceito
Data: 2026-07-26

## Contexto

O Documento 12 delega ao Claude Code a escolha do modelo físico de persistência, e o Documento 11 (§7) exige isolamento multi-tenant obrigatório em todas as camadas, com possibilidade de evolução futura para estratégias de isolamento mais rígidas.

## Decisão

- **Banco de dados:** PostgreSQL.
- **ORM:** Entity Framework Core (provider Npgsql).
- **Estratégia multi-tenant:** banco físico único, **um schema PostgreSQL dedicado por Bounded Context**, com `TenantId` obrigatório em toda tabela de negócio e **Row-Level Security (RLS)** como camada de defesa adicional.
- **Migrations:** cada contexto possui seu próprio `DbContext` e sua própria tabela de histórico de migrations; aplicadas por um processo dedicado (`IHostPro.MigrationRunner`), nunca automaticamente no startup da API.

## Alternativas Consideradas

- **SQL Server:** tecnicamente equivalente, mas descartado por causa do modelo de licenciamento por núcleo de CPU, que escala mal em custo para um SaaS multi-tenant crescendo — confirmado como decisão mesmo em contexto de equipe .NET, pela análise de custo apresentada e aprovada.
- **Banco dedicado por tenant desde o início:** descartado por complexidade operacional prematura para o estágio atual (Documento 11 §31).
- **Schema único (`public`) para todos os contextos:** descartado — dificultaria a autonomia de evolução por contexto e a futura extração de um contexto para banco dedicado.
- **Migration automática no startup (`Database.Migrate()` no `Program.cs`):** descartada — risco de corrida entre múltiplas instâncias tentando migrar simultaneamente.

## Consequências

### Positivas
- Custo operacional previsível e proporcional ao crescimento do número de tenants.
- Isolamento lógico forte entre contextos (schemas), preparando o terreno para extração futura sem reescrita do domínio.
- JSONB nativo do PostgreSQL atende diretamente a necessidade de armazenar Políticas/Configurações de schema variável (Documento 08).

### Riscos Aceitos
- Nenhum identificado além dos inerentes a qualquer estratégia de schema compartilhado (mitigado por RLS como defesa em profundidade).

## Referências
- Documento 08, Documento 11 §7, Documento 12
- Architecture Principles §7, §10, §16
