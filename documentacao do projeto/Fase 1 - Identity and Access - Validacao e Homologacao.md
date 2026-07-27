# Fase 1 — Identity & Access — Validação e Homologação

Versão: 1.0

Status: Oficial — Incremento 1 aprovado

---

## 1. Objetivo

Este documento registra a validação e homologação real do **Incremento 1** (fundação do contexto Identity & Access — Domain, migration, RLS, roles PostgreSQL, custom stores) da Fase 1, incluindo os problemas encontrados durante execução real contra PostgreSQL e as correções aplicadas.

Este documento não repete decisões arquiteturais já registradas em `Architecture Principles.md` (Seções 7 e 10) e nas ADRs — apenas registra a evidência de validação e o histórico de correções encontradas durante a homologação, conforme `ai-rules/06 - Definition of Done.md`.

Futuros incrementos da Fase 1 (Incremento 2 em diante) devem adicionar sua própria seção a este documento, não criar um novo arquivo.

---

## 2. Escopo homologado

Domain (`Tenant`, `User`, `Role`, `Permission`, `RolePermission`, `UserRole`, `Session`, `RefreshToken`), migration inicial (schema, tabelas, constraints tenant-aware, RLS, grants, seeds), custom stores do ASP.NET Core Identity, hasher Argon2id, `TenantTransactionBehavior`/`TenantBootstrapBehavior`/`ITenantAwareUnitOfWork`, script de provisionamento de roles PostgreSQL.

Fora de escopo (Incremento 2): login, JWT, refresh token, logout, endpoints HTTP, Redis, `security_audit_log`.

---

## 3. Ambiente de execução

Docker Desktop 28.4.0 (WSL2), `docker compose` v2.39.4, PostgreSQL 16 (imagem oficial `postgres:16`), .NET SDK 10.0.302, `dotnet-ef` 10.0.0 (via `.config/dotnet-tools.json`, manifesto local do repositório). Testes de integração via Testcontainers.PostgreSql 4.1.0 (containers efêmeros, imagem `postgres:16`).

---

## 4. Problemas reais encontrados e corrigidos

Todos os problemas abaixo só se manifestaram durante execução real (nunca durante desenvolvimento/compilação) — motivo pelo qual a homologação contra infraestrutura real era um critério de aceite obrigatório, não opcional.

### 4.1 — Substituição de variável do `psql` não funciona em bloco `DO $$ ... $$`

- **Sintoma**: `docker/postgres/init/01-create-roles.sh` falhava com `ERROR: syntax error at or near ":"` ao subir o Postgres de volume vazio; nenhuma role era criada.
- **Causa**: a substituição de variável do `psql` (`:'nome'`) não é aplicada dentro de regiões *dollar-quoted* (`$$ ... $$`); o script original envolvia a lógica condicional `CREATE`/`ALTER ROLE` em `DO $$ ... $$`, e o texto literal `:'migrator_password'` era enviado ao servidor sem substituição.
- **Correção**: lógica reescrita como `SELECT format('CREATE/ALTER ROLE %I ... %L', ...) WHERE NOT EXISTS/EXISTS (...) \gexec`, sempre fora de `$$ ... $$`. `%I` para identificadores, `%L` para a senha (literal seguro), nunca concatenação direta.
- **Arquivo**: `docker/postgres/init/01-create-roles.sh`.

### 4.2 — Tabela de histórico de migrations caindo em `public`

- **Sintoma**: `IHostPro.MigrationRunner` falhava com `permission denied for schema public`.
- **Causa**: o EF Core usa o schema `public` por padrão para `__EFMigrationsHistory` quando não configurado explicitamente; a role `ihostpro_migrator` não tem `CREATE` em `public` (Postgres 15+ revoga esse privilégio de `PUBLIC` por padrão) — a decisão já registrada em `Architecture Principles.md` §10 ("cada DbContext possui sua própria tabela de histórico") nunca havia sido implementada de fato.
- **Correção**: `MigrationsHistoryTable("__EFMigrationsHistory", "identity")` configurado de forma idêntica nos 4 pontos que constroem `IdentityDbContext`.
- **Arquivos**: `IdentityDbContextFactory.cs`, `IdentityModuleExtensions.cs`, `IHostPro.MigrationRunner/Program.cs`, `IdentityRowLevelSecurityTests.cs` (helper de teste).

### 4.3 — `ihostpro_migrator` sem privilégio para criar o schema

- **Sintoma**: após a correção 4.2, `IHostPro.MigrationRunner` passou a falhar com `permission denied for database ihostpro` ao executar `CREATE SCHEMA identity`.
- **Causa**: nenhuma etapa do provisionamento concedia `CREATE` no banco à role `ihostpro_migrator` — privilégio necessário para criar um schema pela primeira vez, e não específico do contexto Identity (toda primeira migration de qualquer contexto futuro precisará dele).
- **Correção**: `GRANT CREATE ON DATABASE <db> TO ihostpro_migrator` adicionado ao script de provisionamento (via `SELECT format('GRANT CREATE ON DATABASE %I TO %I', ...) \gexec`, incondicional — `GRANT` é naturalmente idempotente). `ihostpro_app` não recebe grant equivalente.
- **Arquivo**: `docker/postgres/init/01-create-roles.sh`.

### 4.4 — Mesma causa raiz (4.3) no setup dos testes de integração

- **Sintoma**: os 8 testes de integração originais falhavam no mesmo ponto de 4.3.
- **Causa**: o SQL de setup em `IdentityRowLevelSecurityTests.InitializeAsync()` criava as roles diretamente (independente do script de infraestrutura) e replicava a mesma lacuna — além de conceder `GRANT ALL ON SCHEMA public`, privilégio obsoleto após a correção 4.2.
- **Correção**: `GRANT ALL ON SCHEMA public` substituído por `GRANT CREATE ON DATABASE ihostpro_test TO ihostpro_migrator`, espelhando exatamente o modelo de privilégios do script de infraestrutura.
- **Arquivo**: `IdentityRowLevelSecurityTests.cs` (apenas código de teste).

### 4.5 — Testes que consultavam via `_appConnectionString` nunca definiam `app.tenant_id` no Postgres

- **Sintoma**: `SeedTenantWithUserAsync` falhava com `new row violates row-level security policy`; depois de corrigido, `Correct_tenant_sees_its_own_row` e `Wrong_tenant_cannot_update_the_row` falhavam por não encontrar a linha esperada; `Wrong_tenant_sees_zero_rows` passava, mas testava "tenant ausente", não "tenant diferente e válido" (falso positivo de cobertura).
- **Causa**: código de teste que usa `IdentityDbContext` diretamente (fora de `ITenantAwareUnitOfWork`) só resolvia o `ITenantContext` do lado C# (usado pelo Global Query Filter do EF), nunca emitia `SET LOCAL`/`set_config` no Postgres — exigido pela policy de RLS, que atua independentemente do filtro do EF.
- **Correção**: os métodos afetados passaram a abrir transação explícita e executar `SELECT set_config('app.tenant_id', @tenantId, true)` (parametrizado) antes de operar, replicando manualmente a semântica de `ITenantAwareUnitOfWork`. `Wrong_tenant_sees_zero_rows`/`Wrong_tenant_cannot_update_the_row` passaram a usar um segundo tenant genuinamente provisionado (não um `Guid` nunca persistido).
- **Arquivo**: `IdentityRowLevelSecurityTests.cs` (apenas código de teste; dois testes novos também adicionados: `Insert_without_tenant_context_fails_closed`, `Wrong_tenant_cannot_update_the_row`).

Nenhuma das cinco correções alterou regra de negócio, contrato público ou decisão arquitetural já aprovada — todas completaram a implementação de decisões já registradas (schema por contexto, RLS, least-privilege) ou corrigiram código de teste.

---

## 5. Evidência de validação (execução real)

### 5.1 Roles PostgreSQL

| Verificação | `ihostpro_migrator` | `ihostpro_app` |
|---|---|---|
| `rolcanlogin` | true | true |
| `rolsuper` | false | false |
| `rolcreatedb` | false | false |
| `rolcreaterole` | false | false |
| `rolbypassrls` | false | false |
| `rolreplication` | false | false |
| `has_database_privilege(..., 'CREATE')` | **true** | **false** |
| `has_schema_privilege(..., 'public', 'CREATE')` | — | **false** |
| Membro de outra role | não | não |

Idempotência do script validada por reexecução manual dupla (mesma senha; senha rotacionada) contra ambiente já inicializado: sem erro, sem duplicação de role, grants de tabela preservados, rotação de senha efetiva (confirmada via conexão de um container separado na rede Docker, evitando o `trust` da imagem oficial para conexões `127.0.0.1`/`::1`).

### 5.2 Schema, ownership e migrations

`identity` — owner `ihostpro_migrator`; grants `ihostpro_migrator=UC`, `ihostpro_app=U`. `identity.__EFMigrationsHistory` — existe, owner `ihostpro_migrator`. Schema `public` — zero relações. `IHostPro.MigrationRunner` executado duas vezes consecutivas: primeira aplica a migration; segunda não reaplica, não duplica seeds (`roles=7`, `permissions=32`, `role_permissions=39` em ambas as execuções).

### 5.3 RLS

`relrowsecurity`/`relforcerowsecurity` = true/true exatamente em `users`, `sessions`, `user_roles`, `refresh_tokens`; false/false nas 5 tabelas de catálogo. Policy `tenant_isolation` (`cmd=ALL`, `USING`/`WITH CHECK` idênticos) presente nas 4 tabelas tenant-owned.

### 5.4 Constraints tenant-aware

8 foreign keys compostas confirmadas via `pg_constraint`, incluindo `FK_users_tenants_tenant_id` (adicionada durante a homologação — lacuna encontrada na revisão do `Down()` da migration).

### 5.5 Testes

| Suíte | Preexistente (Fase 0) | Novo (Incremento 1) | Resultado |
|---|---|---|---|
| Unitários | 0 | 66 | 66/66 aprovados |
| Arquitetura | 5 | 4 | 9/9 aprovados |
| Integração (PostgreSQL real via Testcontainers) | 0 | 10 | 10/10 aprovados |
| **Total** | **5** | **80** | **85/85 aprovados** |

Build Release: 0 erros, 0 avisos, 15 projetos.

---

## 6. Critérios objetivos de aceite

- [x] Build 0 erros/0 avisos.
- [x] Migration aplicada e idempotente contra PostgreSQL real.
- [x] RLS `ENABLE`+`FORCE`+`USING`+`WITH CHECK`, validado fail-closed (tenant correto/incorreto/ausente).
- [x] FKs tenant-aware bloqueando relações cross-tenant, inclusive sob a role de migração.
- [x] Nenhum vazamento de tenant em conexão reaproveitada do pool.
- [x] `ihostpro_app` sem `BYPASSRLS`/`CREATE` em qualquer schema, incluindo `public`.
- [x] `ihostpro_migrator` com `CREATE` apenas no banco (para criar schemas), sem `SUPERUSER`/`CREATEDB`/`BYPASSRLS`.
- [x] Seeds determinísticos, sem duplicação em reexecução.
- [x] Script de provisionamento de roles idempotente, testado em volume vazio e já inicializado.
- [x] Nenhum objeto do contexto Identity no schema `public`.
- [x] 85/85 testes aprovados.

---

## 7. Status final

**Incremento 1 — Identity & Access Infrastructure: Implementação concluída · Homologação concluída · Status aprovado · Nenhum bloqueador pendente.**
