# Fase 1 — Identity & Access — Validação e Homologação

Versão: 1.0

Status: Oficial — Incremento 1 aprovado · Incremento 2 (Etapa 15) aprovado

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

---

## 8. Incremento 2 — Etapa 15: Integration Events reais de Login, Logout e Refresh Token

Esta seção registra a validação real da **Etapa 15** do Incremento 2 (as demais etapas do Incremento 2 — login, JWT, refresh token, logout, endpoints HTTP, Redis, `security_audit_log` — devem receber suas próprias seções neste documento à medida que forem homologadas, conforme a convenção definida na Seção 1).

### 8.1 Escopo desta etapa

- Seis Integration Events reais do contexto Identity & Access, definidos em `IHostPro.Contexts.Identity.Contracts` (sem dependência de Domain/Infrastructure/Wolverine): `UserLoggedIn`, `LoginFailed`, `AccountLockedOut`, `UserLoggedOut`, `RefreshTokenReuseDetected`, `SessionRevoked` — payloads e códigos de motivo estáveis conforme Documento 07 §13.1.
- Emissão via `IIntegrationEventCollector.Enqueue` nos handlers de `LoginCommand`, `LogoutCommand` e `RefreshTokenCommand`; persistência e drenagem via `IIdentityTransactionExecutor` (outbox transacional); entrega externa somente após commit, nunca chamada direta a `WolverineEventPublisher.PublishAsync`.
- Roteamento RabbitMQ das seis rotas com `.UseDurableOutbox()` explícito em `Program.cs`, seguindo a convenção registrada em `ADR-013` (exchange de tópico por Bounded Context, routing key em snake_case, versionamento via sufixo no nome do tipo).
- Documentação sincronizada: Documento 07 §13.1 (payloads/códigos) e §13.2 (roteamento RabbitMQ); `Architecture Principles.md` §11 com referência à ADR-013.
- Fora de escopo desta etapa (não implementado, por instrução explícita): rate limiting, limpeza/expurgo de tokens, novos endpoints HTTP.

### 8.2 Ambiente de execução

Idêntico ao registrado na Seção 3, acrescido de RabbitMQ (imagem oficial, via Testcontainers) para os testes de integração que inspecionam o outbox (`wolverine_outgoing_envelopes`) e simulam indisponibilidade/recuperação do broker.

### 8.3 Problema real encontrado e corrigido (código de teste)

- **Sintoma**: a inspeção do outbox nos testes de integração falhava de forma intermitente com `JsonReaderException` ao tentar interpretar `wolverine_outgoing_envelopes.body` como JSON puro do evento; em alguns casos a consulta também retornava envelopes de um tipo de evento diferente do esperado.
- **Causa**: (1) a consulta inicial usava correspondência por fragmento (`message_type ILIKE '%...%'`), que podia colidir com envelopes internos não relacionados; (2) `wolverine_outgoing_envelopes.body` **não é o JSON puro do evento** — é o formato de wire binário interno do Wolverine (uma sequência de pares chave/valor de cabeçalho prefixados por tamanho, como `source`, `message-type`, `reply-uri`, seguidos do JSON real do evento e, quando mais de um envelope é agrupado no mesmo lote, por vezes seguido de bytes adicionais de outro envelope).
- **Correção** (apenas código de teste — nenhuma mudança em código de produção foi necessária): consulta ajustada para igualdade exata (`message_type = @messageType`, com `typeof(TEvent).FullName`); extração do JSON via varredura de chaves balanceadas a partir do primeiro `{` (contando profundidade, ignorando chaves dentro de strings/escapes) até a `}` correspondente — em vez de "primeiro `{` até último `}`", que podia atravessar os dados de mais de um envelope.
- **Achado adicional**: em ambiente com o broker acessível, o Durability Agent do Wolverine entrega e remove o envelope do outbox quase instantaneamente, o que tornava a leitura direta do outbox uma corrida contra esse processo em alguns cenários; os testes que precisam observar o envelope pendente pausam o container RabbitMQ ao redor da operação testada.
- **Arquivo**: `IdentityIntegrationEventsTests.cs` (apenas código de teste).
- Nenhuma das correções desta seção alterou código de produção — o mecanismo de publicação e o outbox funcionaram corretamente durante toda a investigação; a falha estava exclusivamente na forma como o teste interpretava o conteúdo binário do outbox.

### 8.4 Evidência de validação (execução real)

#### 8.4.1 Testes — build Release e suíte completa, duas execuções consecutivas (`dotnet test IHostPro.sln -c Release --no-build -m:1`)

| Suíte | Total | Execução 1 | Execução 2 |
|---|---|---|---|
| Arquitetura | 18 | 18/18 aprovados | 18/18 aprovados |
| Unitários (Identity) | 273 | 273/273 aprovados | 273/273 aprovados |
| Integração (Identity — PostgreSQL + RabbitMQ reais via Testcontainers) | 138 | 138/138 aprovados | 138/138 aprovados |
| Unitários (BuildingBlocks) | 7 | 7/7 aprovados | 7/7 aprovados |
| **Total** | **436** | **436/436 aprovados** | **436/436 aprovados** |

Build Release: 0 erros, 0 avisos, 15 projetos — executado antes de cada uma das duas rodadas.

Novidades cobertas por esta contagem, em relação ao estado anterior à Etapa 15:
- **Arquitetura** (+3): `Contracts_Should_Not_Depend_On_Domain_Infrastructure_Or_Wolverine` (`IdentityDependencyTests.cs`) e o novo arquivo `IdentityIntegrationEventContentTests.cs` (2 testes: ausência estrutural de dados sensíveis via reflexão sobre os seis eventos; tripwire de catálogo confirmando que apenas os seis eventos aprovados existem em Contracts).
- **Integração** (+14): novo arquivo `IdentityIntegrationEventsTests.cs`, cobrindo evento/payload correto por fluxo (sucesso, cada motivo de rejeição, lockout), lockout publicando uma única vez, logout repetido sem publicação, grace window sem publicação de reuse, tenant não resolvido sem publicação, rollback sem envelope, concorrência/retry sem duplicação, indisponibilidade do broker mantendo o envelope pendente com entrega após recuperação (dois cenários dedicados), as seis rotas usando outbox durável, e ausência de dados sensíveis confirmada por inspeção do payload real serializado.
- **Unitários**: `LoginCommandHandlerTests.cs`, `LogoutCommandHandlerTests.cs` e `RefreshTokenCommandHandlerTests.cs` estendidos com asserções de enfileiramento de evento/payload por branch de negócio (sucesso, cada rejeição, lockout, logout idempotente vs. efetivo, reuse dentro/fora da grace window).

### 8.5 Critérios objetivos de aceite desta etapa

- [x] Documento 07 completo com payloads e códigos de motivo finais antes da implementação (§13.1/§13.2).
- [x] ADR-013 registrada e aprovada para a convenção de roteamento RabbitMQ, antes de qualquer nome de exchange/routing key ser definido.
- [x] Contracts sem dependência de Domain/Infrastructure/Wolverine (teste de arquitetura).
- [x] Nenhum evento carrega e-mail, IP, User-Agent, senha, token de acesso, refresh token, hash de token, secret ou JWT (teste de arquitetura estrutural via reflexão + inspeção do payload real nos testes de integração).
- [x] Emissão apenas via `IIntegrationEventCollector.Enqueue` nos handlers; nenhuma chamada direta a `WolverineEventPublisher.PublishAsync`.
- [x] Persistência via `IIdentityTransactionExecutor`; entrega externa somente após commit (outbox).
- [x] Todas as seis rotas usam `.UseDurableOutbox()` explicitamente.
- [x] Regras de emissão por fluxo (sucesso, rejeição, lockout, tenant inexistente, logout efetivo/idempotente, reuse dentro/fora da grace window, retry revertido) validadas uma a uma por teste de integração dedicado.
- [x] Rollback/exceção não deixa envelope pendente no outbox.
- [x] Concorrência/retry não duplica publicação.
- [x] Indisponibilidade do broker mantém o envelope pendente; recuperação entrega automaticamente.
- [x] Application e Domain continuam sem referência a Wolverine.
- [x] Nenhum rate limiting, limpeza de tokens ou novo endpoint HTTP implementado (fora do escopo autorizado desta etapa).
- [x] Build Release 0 erros/0 avisos; suíte completa (436 testes) aprovada em duas execuções consecutivas com `-m:1`.

### 8.6 Status desta etapa

**Incremento 2 — Etapa 15 (Seis Integration Events reais de Login, Logout e Refresh Token): Implementação concluída · Homologação concluída · Status aprovado · Nenhum bloqueador pendente.**

---

## 9. Incremento 2 — Homologação real e encerramento

Esta seção registra a etapa final de homologação e encerramento do Incremento 2 completo (login, JWT, refresh token, logout, seis Integration Events — Etapas 6 a 15), incluindo a implementação do `DevelopmentIdentitySeeder` (previamente aprovado, ainda não implementado) e uma homologação real de ponta a ponta contra containers Docker reais, isolados do ambiente de desenvolvimento já em uso.

### 9.1 `DevelopmentIdentitySeeder`

- **Escopo**: cria o tenant/usuário administrativo descrito em `DevelopmentSeedOptions` (`Identity:DevelopmentSeed`, já existente desde ajuste 3-4 do plano do Incremento 2) apenas quando o host roda em Development; fora de Development o tipo nem é registrado no container de DI. Operação idempotente (verifica existência antes de inserir), protegida por `pg_advisory_xact_lock` do PostgreSQL (segura sob múltiplas instâncias — `IHostPro.Api` e `IHostPro.Worker` podem registrar o serviço simultaneamente). Senha do administrador nunca em `appsettings` — apenas variável de ambiente/User Secrets, validada via a mesma `PasswordPolicyValidator` usada pelo restante do módulo (nenhuma segunda fonte de verdade para política de senha). Nenhum endpoint HTTP administrativo foi criado — execução via `IHostedService`, disparada apenas no startup do host. Nenhuma role é atribuída ao usuário criado (fora de escopo, conforme instruído).
- **Bug real encontrado e corrigido durante os próprios testes deste seeder** (não pela homologação manual — pelos testes automatizados de integração, antes de qualquer execução real): a verificação de idempotência (`Users.AnyAsync`) e a inserção do usuário dependem do *Global Query Filter* do EF Core (`BaseDbContext`, `entity.TenantId == ITenantContext.TenantId`) além do `SET LOCAL app.tenant_id` exigido pela Row-Level Security do PostgreSQL — a primeira versão do seeder só resolvia o segundo mecanismo. Sem resolver também `ITenantContext.SetTenant(tenant.Id)`, o filtro do EF Core (fail-closed por design quando o tenant não está resolvido) fazia toda consulta a `users` retornar zero linhas, mascarando a existência do usuário já criado — isso quebrava idempotência e concorrência: a segunda execução (ou uma execução concorrente) tentava inserir o mesmo usuário novamente, violando a constraint única `IX_users_tenant_id_normalized_email`. Corrigido resolvendo `ITenantContext.SetTenant(tenant.Id)` antes de qualquer consulta/inserção em `users`, além do `SET LOCAL` já existente. Nenhuma execução real (manual ou em ambiente Docker) chegou a ocorrer com o bug presente — foi encontrado e corrigido pelos testes automatizados de concorrência/idempotência antes disso.
- **Evidência de teste**: `DevelopmentIdentitySeederTests.cs` (novo, 7 testes de integração contra PostgreSQL real via Testcontainers) — seed desabilitado não cria nada mesmo com os demais campos preenchidos; seed habilitado cria tenant/usuário com hash de senha funcional (verificado via `Argon2PasswordHasher.VerifyHashedPassword`); execução sequencial duas vezes é idempotente; três instâncias concorrentes não duplicam nem causam deadlock; senha fora da política de senha falha o startup do host sem criar tenant nem usuário (rollback completo); usuário criado não recebe nenhuma role; a senha configurada nunca aparece em nenhuma mensagem logada.

### 9.2 Ambiente da homologação real

Ambiente Docker **isolado e efêmero**, criado especificamente para esta homologação (projeto `ihostpro-e2e`: containers, volume e rede próprios) — em nenhum momento o ambiente de desenvolvimento já existente do usuário (`ihostpro-postgres`, projeto `ihostpro`, com dados persistentes) foi parado, alterado ou teve seu volume tocado. PostgreSQL 16, Redis 7 (alpine) e RabbitMQ 3 (management-alpine), imagens oficiais idênticas às do `docker-compose.yml` do repositório. Ambiente completamente removido (containers, volume, rede) ao final da homologação.

`IHostPro.MigrationRunner` executado duas vezes consecutivas contra o banco vazio: primeira aplica a migration e provisiona o outbox (`identity_messaging`, 8 tabelas, 2 sequências); segunda não reaplica nada, seeds determinísticos idênticos em ambas (`roles=7`, `permissions=32`, `role_permissions=39`) — mesmo resultado já registrado para o Incremento 1 (Seção 5.2), agora confirmado também para o schema completo do Incremento 2.

`IHostPro.Api` iniciada com as credenciais `ihostpro_app` (least-privilege) contra esse ambiente, ambiente `Development` (necessário para o seeder), chave de assinatura JWT RSA gerada localmente apenas para esta homologação (nunca reaproveitada, nunca commitada).

### 9.3 Evidência da homologação real (requisições HTTP reais contra o processo real)

| Cenário | Resultado observado |
|---|---|
| Login com credenciais corretas | `200 OK`, `access_token`/`refresh_token` reais emitidos, `Cache-Control: no-store` |
| Login com senha incorreta | `401`, corpo `{"title":"invalid_credentials"}` — nenhum detalhe interno exposto |
| Endpoint protegido (`logout`) sem JWT | `401` |
| Endpoint protegido (`logout`) com JWT inválido/malformado | `401` |
| Refresh — rotação normal | `200 OK`, novo par de tokens |
| Refresh — token já rotacionado apresentado de novo **dentro** da grace window (inclusive concorrência real: duas requisições simultâneas com o mesmo token) | Uma requisição vence (200, rotaciona); a outra falha (`401`) **sem** gerar `RefreshTokenReuseDetected`/revogar a sessão — confirmado via `security_audit_log` (`RefreshRejected` / `ConcurrentRotationGraceWindow`) |
| Refresh — token já rotacionado apresentado de novo **fora** da grace window | `401`; `RefreshTokenReuseDetected` + `SessionRevoked` publicados; sessão revogada em `sessions` (`status`/`revoked_at`); o access token e o refresh token sucessor da mesma sessão passam a ser rejeitados (`401`) a partir daí |
| Logout efetivo | `204 No Content`; `sessions.status`/`revoked_at` atualizados; exatamente um par `UserLoggedOut`/`SessionRevoked` |
| Logout repetido (mesmo access token, sessão já revogada) — inclusive com Redis indisponível e com Redis disponível | `204` nas duas vezes; nenhum evento novo em `security_audit_log` |
| Redis indisponível durante logout | `204` (sucesso) — comportamento fail-open confirmado (`RedisSessionRevocationCache`): aviso registrado em log, PostgreSQL permanece fonte da verdade, logout não é afetado |
| RabbitMQ indisponível durante o commit (login e reuse detection) | Requisição HTTP conclui (login: latência normal; ver risco na Seção 9.4); envelope(s) confirmados pendentes em `wolverine_outgoing_envelopes` via consulta direta ao PostgreSQL; nenhuma exceção não tratada; nenhum dado perdido |
| Recuperação do RabbitMQ | Envelopes pendentes entregues automaticamente (1–2s após o broker voltar a ficar saudável); tabela de outbox volta a zero |
| Inspeção direta do payload publicado (`RefreshTokenReuseDetected`) no PostgreSQL | Nenhuma ocorrência de IP, e-mail ou domínio do usuário nos bytes do envelope |
| Inspeção do Redis | Única chave presente segue exatamente o formato documentado (`ihostpro:{tenantId}:session-revoked:{sessionId}`), valor é apenas o marcador fixo `"1"` — nenhum dado sensível |
| Inspeção dos logs da aplicação | Nenhuma ocorrência da senha configurada, do e-mail em texto livre, da chave privada JWT ou de qualquer refresh token emitido |
| `security_audit_log` | Contém apenas identificadores (uuid), código de motivo ASCII estável e IP (campo permitido neste log de auditoria — distinto da regra dos Integration Events) — nunca e-mail, senha, token ou hash |

### 9.4 Achados reais (riscos operacionais identificados nesta rodada — **corrigidos na Seção 10**)

- **Latência elevada durante indisponibilidade do RabbitMQ**: quando o broker está indisponível e a tentativa de publicação síncrona inicial falha, a requisição HTTP que originou o evento pode ficar bloqueada por dezenas de segundos a poucos minutos antes de retornar ao cliente, mesmo com o outbox durável funcionando corretamente (a decisão de negócio final estava sempre correta — nenhum dado era perdido, nenhuma resposta incorreta era dada). Causa raiz identificada e corrigida — ver Seção 10.1.
- **Latência adicional (~10-19s) durante indisponibilidade do Redis**: o logout permanecia correto (fail-open, `204`), mas a chamada ao Redis indisponível adicionava dezenas de segundos à resposta antes de desistir e prosseguir. Causa raiz identificada e corrigida — ver Seção 10.2.
- **Bug de teste corrigido durante esta etapa** (não é um problema de produção): `IdentityIntegrationEventsTests.cs` apresentou falha intermitente (`No balanced JSON object found`) na suíte completa, isolada a cenários com múltiplos envelopes `LoginFailed` (testes de lockout). Causa: a extração do JSON a partir do `body` binário do envelope (Seção 8.3) buscava apenas o **primeiro** byte `{` no array — um byte de comprimento do cabeçalho binário do Wolverine pode coincidentemente valer `0x7B` ('{'), e corpos maiores (como os de `LoginFailed`, com código de motivo mais longo) aumentam a chance dessa colisão. Corrigido: a extração agora tenta cada ocorrência de `{` como candidato e só aceita a que também é JSON sintaticamente válido (`JsonDocument.Parse` bem-sucedido), não apenas balanceada — colisão binária praticamente nunca produz JSON válido. Confirmado com o arquivo completo (14/14) executado uma vez adicional após a correção, além das duas execuções completas da suíte da solução exigidas nesta etapa.

### 9.5 Critérios objetivos de aceite desta etapa

- [x] `DevelopmentIdentitySeeder` implementado conforme aprovação anterior (Development-only, idempotente, advisory lock, senha via variável de ambiente/User Secrets, sem endpoint administrativo, sem atribuição de role).
- [x] Ambiente Docker limpo e isolado, sem impacto no ambiente de desenvolvimento já existente.
- [x] `MigrationRunner` executado duas vezes; segunda execução sem novas alterações; seeds determinísticos idênticos.
- [x] `IHostPro.Api` iniciada com credenciais `ihostpro_app`.
- [x] Login HTTP real, JWT válido aceito, JWT ausente/inválido rejeitado.
- [x] Refresh e rotação reais; concorrência real dentro da grace window sem revogação; reuse real fora da grace window com revogação em cascata (access token e refresh token sucessor).
- [x] Logout e logout repetido (idempotente), inclusive sob indisponibilidade do Redis.
- [x] Redis indisponível — fail-open confirmado, logout não afetado.
- [x] RabbitMQ indisponível durante o commit — envelope permanece pendente, confirmado diretamente no PostgreSQL; recuperação entrega automaticamente.
- [x] Auditoria (`security_audit_log`) e envelopes (`wolverine_outgoing_envelopes`) inspecionados diretamente no PostgreSQL.
- [x] Redis, logs da aplicação, auditoria e payload dos Integration Events confirmados sem dados sensíveis.
- [x] Build Release 0 erros/0 avisos; suíte completa aprovada em duas execuções consecutivas com `-m:1` (números na Seção 9.6).
- [x] Nenhum rate limiting, limpeza de tokens ou nova funcionalidade implementada além do já aprovado.
- [x] Nenhum commit, push, tag ou merge realizado.

### 9.6 Evidência de testes — build Release e suíte completa, duas execuções consecutivas (`dotnet test IHostPro.sln -c Release --no-build -m:1`)

| Suíte | Total | Execução 1 | Execução 2 |
|---|---|---|---|
| Arquitetura | 18 | 18/18 aprovados | 18/18 aprovados |
| Unitários (Identity) | 273 | 273/273 aprovados | 273/273 aprovados |
| Integração (Identity — PostgreSQL + RabbitMQ reais via Testcontainers) | 145 | 145/145 aprovados | 145/145 aprovados |
| Unitários (BuildingBlocks) | 7 | 7/7 aprovados | 7/7 aprovados |
| **Total** | **443** | **443/443 aprovados** | **443/443 aprovados** |

Novidade nesta contagem em relação à Seção 8.4.1: `DevelopmentIdentitySeederTests.cs` (+7 testes de integração, Seção 9.1).

### 9.7 Status desta etapa

**Incremento 2 — Identity & Access (Login, JWT, Refresh Token, Logout, seis Integration Events): Implementação concluída · Homologação real concluída · `DevelopmentIdentitySeeder` implementado e testado · Status aprovado · Riscos operacionais identificados na Seção 9.4, corrigidos na Seção 10 — nenhum bloqueador crítico pendente.**

---

## 10. Correção da latência sob indisponibilidade de RabbitMQ/Redis

Os dois riscos operacionais registrados na Seção 9.4 foram investigados, corrigidos e revalidados com medição objetiva de latência (antes/depois), a pedido explícito do usuário antes do encerramento do Incremento 2.

### 10.1 RabbitMQ — causa raiz e correção

**Investigação**: nenhuma das seis rotas está em modo Inline (todas usam `.UseDurableOutbox()`); o outbox durável já persistia corretamente apenas no PostgreSQL dentro da transação (confirmado: com o broker totalmente parado — `docker stop` —, o login retornava em ~0.2s com o envelope corretamente pendente). A causa real: `IDbContextOutbox.SaveChangesAndFlushMessagesAsync` ([IdentityOutboxTransactionExecutor.cs](i-host-pro/src/Contexts/Identity/IHostPro.Contexts.Identity.Infrastructure/Persistence/IdentityOutboxTransactionExecutor.cs)) faz, após persistir, uma tentativa oportunista e síncrona de entrega imediata (comportamento padrão do Wolverine para baixa latência no caminho feliz) — quando o broker está inacessível de forma "muda" (partição de rede, sem recusa de conexão), essa tentativa usa os timeouts padrão do `RabbitMQ.Client.ConnectionFactory` (`RequestedConnectionTimeout`=30s, `ContinuationTimeout`=20s), e o Wolverine tenta 3 vezes antes de "travar" (latch) o agente de envio.

**Correção**: `RabbitMqClientTimeoutOptions` (novo, `IHostPro.BuildingBlocks.Infrastructure.Messaging`) — `ConnectTimeout`/`ContinuationTimeout`, validados (`RabbitMqClientTimeoutOptionsValidator`, bounds 200ms–30s), aplicados em [WolverineConfigurationExtensions.UseIHostProRabbitMq](i-host-pro/src/BuildingBlocks/IHostPro.BuildingBlocks.Infrastructure/Messaging/WolverineConfigurationExtensions.cs) sobre o `ConnectionFactory`. Vinculado diretamente de `IConfiguration` (não via `IOptions<T>`/DI — o transporte do Wolverine é configurado antes do container de DI existir) e validado de forma síncrona, mas seguindo o mesmo padrão de bounds explícitos das demais Options classes do projeto. Nenhum valor mágico solto no código — os defaults (2s/2s) vivem na própria classe de opções e podem ser sobrescritos por ambiente via `RabbitMq:ConnectTimeout`/`RabbitMq:ContinuationTimeout`.

### 10.2 Redis — causa raiz e correção

**Investigação**: `AddIdentitySessionRevocationCache` já usava `ConnectionMultiplexer.Connect(...)` com `AbortOnConnectFail=false` (correto). A causa real: os timeouts padrão do `StackExchange.Redis.ConfigurationOptions` nunca eram configurados explicitamente (`ConnectTimeout`/`SyncTimeout`/`AsyncTimeout`=5000ms, `ConnectRetry`=3) — `RedisSessionRevocationCache.MarkRevokedAsync`/`IsRevokedAsync` (esta última usada na validação de **todo** Bearer JWT autenticado via `ConfigureJwtBearerOptions`, não apenas no logout) ficavam bloqueadas até esses timeouts antes de cair no `catch` fail-open já existente.

**Correção**: `SessionRevocationCacheOptions` estendida com `ConnectTimeout`/`OperationTimeout`/`ConnectRetry`, validados (`SessionRevocationCacheOptionsValidator`, bounds 200ms–10s / 0–5 tentativas), aplicados em [IdentitySessionRevocationCacheExtensions.cs](i-host-pro/src/Contexts/Identity/IHostPro.Contexts.Identity.Infrastructure/Caching/IdentitySessionRevocationCacheExtensions.cs) sobre o `ConfigurationOptions` do StackExchange.Redis — via `IOptions<T>`/`ValidateOnStart()`, o caminho normal já usado pelo resto do módulo. O comportamento fail-open em si (Redis nunca é fonte da verdade) permanece inalterado — apenas a velocidade com que ele passa a valer.

### 10.3 Evidência de testes automatizados adicionados

- `RabbitMqClientTimeoutOptionsValidatorTests.cs` (novo, BuildingBlocks — 6 testes): sucesso com os defaults documentados; falha abaixo/acima dos limites para cada campo.
- `SessionRevocationCacheOptionsValidatorTests.cs` (novo, Identity — 9 testes): sucesso com os defaults; falha para `ConnectionString` ausente, cada timeout fora dos limites, `ConnectRetry` fora dos limites; acumulação de todas as falhas simultâneas.

### 10.4 Re-homologação real — medição objetiva de latência (antes / depois)

Ambiente Docker isolado idêntico ao da Seção 9.2 (novo, efêmero, removido ao final), medições feitas com `curl -w "%{time_total}"` (tempo total decorrido, medido pelo cliente) contra o processo real do `IHostPro.Api`. "Antes" foi obtido forçando explicitamente os valores padrão originais via variável de ambiente (o código-fonte não os contém mais como default) contra o mesmo ambiente, não estimado.

| Cenário | Antes (defaults originais) | Depois (defaults corrigidos) | Redução |
|---|---|---|---|
| Login/refresh normais (broker e Redis disponíveis) | 0.2–0.6s | 0.2–0.6s (inalterado) | — |
| Refresh reuse fora da grace window — pior caso, 2 eventos publicados (`RefreshTokenReuseDetected`+`SessionRevoked`), RabbitMQ pausado (partição de rede simulada) | **120.15s** | **12.16s** | ~10× |
| Logout com Redis indisponível desde antes do login (JWT Bearer + revogação) | **19.26s** | **1.97s** | ~10× |

Em ambos os casos "depois": envelope(s) confirmados pendentes em `wolverine_outgoing_envelopes` enquanto o RabbitMQ estava pausado; entregues automaticamente em ≤1s após o broker voltar a ficar saudável (tabela de outbox volta a zero); `identity.sessions` confirmado com `status`/`revoked_at` corretamente atualizados via consulta direta ao PostgreSQL em ambas as medições de Redis (antes e depois) — **PostgreSQL permanece a fonte de verdade em todos os cenários**, independentemente da disponibilidade de RabbitMQ/Redis ou da configuração de timeout.

### 10.5 Critérios objetivos de aceite desta etapa

- [x] Causa raiz de ambos os problemas identificada por investigação de código + reflexão sobre os pacotes reais (`RabbitMQ.Client`, `StackExchange.Redis`), não suposição.
- [x] Confirmado que nenhuma rota está em modo Inline e que os seis eventos são persistidos apenas no PostgreSQL dentro da transação.
- [x] Defaults atuais apresentados, novos valores propostos e justificados (segurança/disponibilidade/UX) antes de qualquer alteração de código.
- [x] Valores configuráveis via Options Pattern, com validação e bounds — nenhum valor mágico solto no código.
- [x] Nenhuma nova funcionalidade implementada — apenas configuração de timeouts já existentes nas bibliotecas cliente.
- [x] Re-homologação real: login/refresh/logout com RabbitMQ indisponível; envelope pendente confirmado e entrega após recuperação; logout e JWT Bearer com Redis indisponível; PostgreSQL confirmado como fonte da verdade em todos os cenários.
- [x] Medição objetiva de latência antes/depois, com o mesmo ambiente Docker isolado, mesmos cenários — não estimativa.
- [x] Build Release 0 erros/0 avisos; suíte completa aprovada em duas execuções consecutivas com `-m:1` (números na Seção 10.6).
- [x] Nenhum commit, push, tag ou merge realizado.

### 10.6 Evidência de testes — build Release e suíte completa, duas execuções consecutivas (`dotnet test IHostPro.sln -c Release --no-build -m:1`)

| Suíte | Total | Execução 1 | Execução 2 |
|---|---|---|---|
| Arquitetura | 18 | 18/18 aprovados | 18/18 aprovados |
| Unitários (Identity) | 282 | 282/282 aprovados | 282/282 aprovados |
| Integração (Identity) | 145 | 145/145 aprovados | 145/145 aprovados |
| Unitários (BuildingBlocks) | 13 | 13/13 aprovados | 13/13 aprovados |
| **Total** | **458** | **458/458 aprovados** | **458/458 aprovados** |

Novidade nesta contagem em relação à Seção 9.6: `SessionRevocationCacheOptionsValidatorTests.cs` (+9, Unitários Identity), `RabbitMqClientTimeoutOptionsValidatorTests.cs` (+6, Unitários BuildingBlocks).

### 10.7 Status desta etapa

**Incremento 2 — Identity & Access: latência sob indisponibilidade de Redis corrigida e aprovada. Latência sob indisponibilidade de RabbitMQ reduzida ~10× mas ainda acima do objetivo de <1s — investigação adicional na Seção 11.**

---

## 11. Eliminação da tentativa síncrona múltipla — RabbitMQ

A correção da Seção 10.1 (redução de `ContinuationTimeout`/`ConnectTimeout`) reduziu a latência do pior caso de 120,15s para 12,16s, mas isso ainda estava acima do objetivo de <1s. Investigação adicional, sem alterar a versão do Wolverine e sem dispatcher próprio, conforme solicitado.

### 11.1 Causa raiz exata (decompilação real do `Wolverine.dll`/`Wolverine.RabbitMQ.dll` 6.22.0 e `JasperFx.dll` 2.34.0 — não documentação de terceiros)

Cadeia de chamadas rastreada a partir de `SaveChangesAndFlushMessagesAsync`:

1. `MessageContext.FlushOutgoingMessagesAsync` → `Envelope.StoreAndForwardAsync()` → `DurableSendingAgent.storeAndForwardAsync`: persiste no PostgreSQL (`_outbox.StoreOutgoingAsync`) — correto e rápido — e então chama `_sending.PostAsync(envelope)`.
2. `_sending` é um `JasperFx.Blocks.RetryBlock<Envelope>`. **`RetryBlock<T>.PostAsync` sempre faz uma primeira tentativa síncrona, aguardada**, antes de qualquer fallback para a fila em segundo plano — esse é o mecanismo intencional de baixa latência no caminho feliz do Wolverine.
3. Se essa tentativa falha (`_sender.SendAsync` — o canal RabbitMQ real, via `RabbitMqChannelAgent.EnsureInitiated()`), `SendingAgent.sendWithExplicitHandlingAsync` captura a exceção e chama `markFailedAsync`, que **reexecuta `await _sending.PostAsync(message)` recursivamente e aguardado**, repetindo até `Endpoint.FailuresBeforeCircuitBreaks` (propriedade pública, **default = 3**) falhas consecutivas — só então o circuito "trava" (`Latched = true`) e desvia definitivamente para a fila em segundo plano (`EnqueueForRetryAsync`).

Isso explica com exatidão os números já medidos: 3 tentativas × `ContinuationTimeout` × número de eventos na transação.

**Resposta às perguntas da investigação**: não existe operação Wolverine que só persista sem nenhuma tentativa de entrega para um endpoint `.UseDurableOutbox()` — a primeira tentativa síncrona é um comportamento intencional (não um bug, não uma limitação da versão 6.22). Existe, porém, API pública oficial para limitar a **quantas** tentativas síncronas ocorrem antes do desvio: `Endpoint.FailuresBeforeCircuitBreaks`, exposta via `SubscriberConfiguration<T,TEndpoint>.CircuitBreaking(Action<ICircuitParameters> configure)` — já herdada por `RabbitMqSubscriberConfiguration`, a mesma classe já usada em `RouteIdentityEvent<T>`. Nenhuma alternativa (adaptação a outro mecanismo, atualização de versão — existe a V7.36.1, mas desnecessária — ou dispatcher próprio) foi necessária.

### 11.2 Solução implementada

`.CircuitBreaking(cb => cb.FailuresBeforeCircuitBreaks = 1)` adicionado à função central `RouteIdentityEvent<T>` em [Program.cs](i-host-pro/src/Host/IHostPro.Api/Program.cs), aplicando-se automaticamente às seis rotas (mesma função usada para todas). Reduz a exposição da requisição de três tentativas síncronas para uma única antes do desvio para o Durability Agent.

Com `FailuresBeforeCircuitBreaks = 1` isolado (mantendo os timeouts de 2s da Seção 10.1), o pior caso (2 eventos, RabbitMQ pausado) mediu ~4,05–4,17s em 5 execuções — abaixo de 1s ainda não. Reduzindo então prioritariamente `ContinuationTimeout` (identificado como o timeout dominante no cenário de conexão já estabelecida — `ConnectTimeout` mantido separado, inalterado em 2s, pois só governa o estabelecimento de uma conexão TCP nova, já rápido em falhar mesmo com o broker totalmente parado):

| `ContinuationTimeout` testado | Resultado (5 execuções, pior caso) | Estável abaixo de 1s? |
|---|---|---|
| 2s (apenas `FailuresBeforeCircuitBreaks=1`) | 4,05s – 4,17s | Não |
| 500ms | 1,04s – 1,20s | Não (margem insuficiente) |
| **300ms (escolhido)** | **0,65s – 0,83s** | **Sim** |

`ContinuationTimeout` final: **300ms** (era 2s, era 20s originalmente) — valor final do `RabbitMqClientTimeoutOptions`, configurável via `RabbitMq:ContinuationTimeout`, validado (bounds 200ms–30s, inalterados). `ConnectTimeout` permanece **2s**, configurável separadamente via `RabbitMq:ConnectTimeout`, conforme solicitado.

### 11.3 Evidência de testes automatizados adicionados

- `IdentityIntegrationEventsTests.Route_caps_FailuresBeforeCircuitBreaks_at_one_and_still_uses_the_durable_outbox` (novo, `[Theory]` × 6 rotas): confirma `Endpoint.FailuresBeforeCircuitBreaks == 1` e `Endpoint.Mode == EndpointMode.Durable` para cada uma das seis rotas, inspecionando o host real após `StartAsync()` (confirmado empiricamente que a configuração "delayed" do Wolverine só é aplicada aos `Endpoint`s reais durante o start do host — um `WolverineOptions` nunca iniciado ainda mostra os defaults de construção).
- `IdentityIntegrationEventsTests.Reuse_detection_with_broker_unreachable_completes_without_multiple_synchronous_retries` (novo): publica dois eventos na mesma transação com um RabbitMQ dedicado parado (`StopAsync()`), afirma que a requisição conclui em menos de 10s (guarda de regressão generosa, não o alvo objetivo de <1s medido manualmente — o timing exato de falha de canal não é perfeitamente reproduzível em execução automatizada) e que ambos os envelopes permanecem persistidos.

### 11.4 Homologação final — medição objetiva, 5 repetições por cenário

Ambiente Docker isolado idêntico às Seções 9.2/10.4, medições com `curl -w "%{time_total}"` contra o processo real, valores finais do código (sem sobrescrita por variável de ambiente).

| Cenário | Execuções (5×) | Faixa |
|---|---|---|
| Latência normal — evento único (login), broker saudável | 0,21s / 0,24s / 0,42s / 0,90s (1ª = warm-up JIT) / 0,21s | 0,21s–0,90s |
| Latência normal — dois eventos (reuse), broker saudável | 0,07s / 0,07s / 0,07s / 0,09s / 0,15s | 0,07s–0,15s |
| RabbitMQ **pausado** (partição de rede) — evento único | 0,13s / 0,13s / 0,14s / 0,16s / 0,54s (1ª) | 0,13s–0,54s |
| RabbitMQ **pausado** — dois eventos | 0,637s / 0,639s / 0,643s / 0,645s / 0,649s | **0,637s–0,649s** |
| RabbitMQ **completamente parado** — evento único | 0,11s / 0,12s / 0,13s / 0,14s / 0,17s | 0,11s–0,17s |
| RabbitMQ **completamente parado** — dois eventos | 0,023s / 0,026s / 0,027s / 0,030s / 0,032s | 0,023s–0,032s |

**Objetivo de <1s atingido de forma estável em todos os seis cenários e nas 30 execuções.** O cenário de broker totalmente parado é ainda mais rápido que o pausado — confirma o diagnóstico: recusa de conexão é quase instantânea (não depende do timeout configurado), enquanto uma conexão já estabelecida cujo par fica mudo (pausado) é o caso dominado por `ContinuationTimeout`.

**Confirmações adicionais** (após reiniciar o RabbitMQ): os 25 envelopes pendentes (acumulados nos cenários acima) foram entregues automaticamente em 4s, sem intervenção manual, outbox voltando a zero. `security_audit_log` acumulado em toda a investigação desta etapa: 45 `LoginSucceeded`, 30 `RefreshSucceeded`, 30 `RefreshTokenReuseDetected` — números que se reconciliam exatamente com as chamadas HTTP realizadas (nenhuma perda, nenhuma duplicação). `identity.sessions`: 30 revogadas (uma por `RefreshTokenReuseDetected`), 15 ativas (45 − 30) — exatamente o esperado. **PostgreSQL confirmado como única fonte de verdade em todos os cenários**, independentemente da disponibilidade do RabbitMQ.

### 11.5 Critérios objetivos de aceite desta etapa

- [x] `.CircuitBreaking(cb => cb.FailuresBeforeCircuitBreaks = 1)` aplicado às seis rotas via a função central `RouteIdentityEvent<T>` (Program.cs e seu espelho em `IdentityIntegrationEventsTests.BuildHostAsync`).
- [x] Nenhuma alteração de versão do Wolverine; nenhum dispatcher próprio — apenas API pública oficial (`SubscriberConfiguration.CircuitBreaking`).
- [x] `ContinuationTimeout` reduzido de forma incremental e medida (2s → 500ms → 300ms), nunca fixado por estimativa — 300ms foi o menor valor estável em 5 execuções.
- [x] `ConnectTimeout` mantido separado, configurável, inalterado em 2s.
- [x] Testes automatizados confirmando `FailuresBeforeCircuitBreaks == 1` e `.UseDurableOutbox()` nas seis rotas.
- [x] Teste automatizado confirmando que a resposta HTTP não aguarda múltiplas tentativas síncronas sob indisponibilidade do broker.
- [x] Homologação com 5 repetições por cenário: latência normal, RabbitMQ pausado, RabbitMQ parado, evento único, transação com dois eventos.
- [x] Envelope pendente confirmado; entrega automática após recuperação sem intervenção manual; ausência de perda ou duplicação confirmada via `security_audit_log`/`sessions`.
- [x] Caminho normal sem regressão (latência inalterada, dentro da faixa já observada nas seções anteriores).
- [x] Objetivo de <1s atingido de forma estável (0,023s–0,90s em todos os cenários, 30 execuções).
- [x] Build Release 0 erros/0 avisos; suíte completa aprovada em duas execuções consecutivas com `-m:1` (números na Seção 11.6).
- [x] Nenhum commit, push, tag ou merge realizado.

### 11.6 Evidência de testes — build Release e suíte completa, duas execuções consecutivas (`dotnet test IHostPro.sln -c Release --no-build -m:1`)

| Suíte | Total | Execução 1 | Execução 2 |
|---|---|---|---|
| Arquitetura | 18 | 18/18 aprovados | 18/18 aprovados |
| Unitários (Identity) | 282 | 282/282 aprovados | 282/282 aprovados |
| Integração (Identity) | 152 | 152/152 aprovados | 152/152 aprovados |
| Unitários (BuildingBlocks) | 13 | 13/13 aprovados | 13/13 aprovados |
| **Total** | **465** | **465/465 aprovados** | **465/465 aprovados** |

Novidade nesta contagem em relação à Seção 10.6: `Route_caps_FailuresBeforeCircuitBreaks_at_one_and_still_uses_the_durable_outbox` (+6, `[Theory]` × 6 rotas) e `Reuse_detection_with_broker_unreachable_completes_without_multiple_synchronous_retries` (+1), ambos em Integração (Identity).

### 11.7 Status desta etapa

**Incremento 2 — Identity & Access: latência sob indisponibilidade de RabbitMQ eliminada na origem (não apenas mitigada) via `CircuitBreaking` oficial do Wolverine + `ContinuationTimeout` afinado — objetivo de <1s atingido de forma estável em 30/30 execuções, cobrindo evento único, dois eventos, broker pausado e broker parado · PostgreSQL confirmado como única fonte de verdade em todo cenário · Status aprovado · Nenhum bloqueador pendente · Nenhum commit realizado.**
