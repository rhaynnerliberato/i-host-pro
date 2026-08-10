# Fase 5 — Configuration & Policy — Validação e Homologação

Versão: 1.4 (Fase 5 concluída, publicada em `master` via fast-forward — ver §18, §19 e §20)

Status: **Fase 5 (Configuration & Policy) encerrada.** Incremento 1 (Policy Engine Foundation) — concluído e publicado: todos os 7 checkpoints aprovados, suíte de backend completa (1.643 testes) e suíte E2E completa (54 testes, duas execuções consecutivas) 100% aprovadas, build Debug e Release ambas limpas, *benchmark* oficial da decisão 7 dentro da meta (p95 = 36,41 ms ≤ 50 ms), débito preventivo de descoberta Wolverine fechado (§18.2). Incremento 2 (Configuration/Settings) — **auditoria concluída, nenhuma implementação justificada no MVP atual** (§19): nenhum candidato encontrado satisfez simultaneamente consumidor real, variabilidade por tenant documentada, natureza de configuração (não Política/segurança/infraestrutura) e valor de negócio suficiente. Esta é uma decisão explícita de escopo baseada em auditoria real do código e da documentação — não uma implementação incompleta ou pendente. Administração de Políticas (`/policies`) entregue e homologada; Configurações (Settings) conscientemente adiadas até surgir um consumidor real; Feature Flags, Templates, vigência temporal e escopo por Grupo/Condomínio/Usuário permanecem fora do MVP, conforme decisões oficiais já registradas em §3/§4. `feature/configuration-policy` publicada e integrada em `master` por fast-forward (§20) — `origin/master` sincronizado, branch `feature/configuration-policy` preservada, nenhuma tag criada.

---

## 1. Objetivo

Este documento registra a validação e homologação real do Incremento 1 da Fase 5 (Configuration & Policy — Policy Engine Foundation), conforme `Plano Executivo de Desenvolvimento por Fases.md` (Fase 5, escopo originalmente "a refinar e aprovar antes da implementação" — refinado e aprovado via relatório de decisões apresentado e aprovado pelo usuário).

Este documento não repete decisões arquiteturais já registradas em `Architecture Principles.md`, no Documento 07 (Catálogo de Eventos de Domínio), no Documento 08 (Motor de Configuração e Hierarquia de Regras) ou nas ADRs — apenas registra o plano aprovado, o escopo, as decisões oficiais tomadas para este incremento, a evidência de validação por checkpoint e o histórico de defeitos encontrados durante a homologação.

## 2. Escopo do Incremento 1 — Policy Engine Foundation

Motor de políticas operacionais hierárquicas (`PROPERTY → TENANT → GLOBAL`), limitado ao catálogo inicial de duas políticas: `EARLY_CHECKIN` e `LATE_CHECKOUT`. Entrega: domínio e persistência (`PolicyDefinition`/`PolicyValue`/`PolicyAuditEntry`), resolução com precedência e contratos típados de leitura (`IEarlyCheckInPolicyReader`/`ILateCheckoutPolicyReader`), API administrativa de políticas, área de frontend `/policies`, cache e evento `PolicyUpdated`.

## 3. Fora de escopo (explicitamente excluído deste incremento)

- **Grupo de Imóveis**: não faz parte do MVP; hierarquia inicial = 3 níveis (`GLOBAL → TENANT → PROPERTY`). Não alterar Property Management. Não criar uma entidade Grupo dentro de Configuration & Policy.
- **Condomínio como nível de configuração**: não é um nível de escopo no MVP; nenhuma conversão implícita de Condomínio em Grupo; nenhuma precedência por Condomínio.
- **Feature Flags**: fora do MVP. Referência mantida em `Architecture Principles.md`; nenhuma tabela/API/frontend implementada.
- **Configurações** (`ConfigurationDefinition`/`ConfigurationValue`): adiadas para incremento futuro — não existe catálogo inicial aprovado de configurações com consumidores reais.
- **Vigência temporal**: fora do MVP. Nenhum `validFrom`/`validUntil`, agendamento de ativação, expiração automática ou sazonalidade.
- **Valores padrão (defaults)**: nenhum valor padrão inventado para Early Check-in/Late Checkout; GLOBAL permanece somente leitura e pode ficar vazio neste incremento.
- **Integração funcional com Reservations**: não alterada neste incremento — nenhum campo de early-check-in/late-checkout adicionado ao agregado `Reservation` ou a seus contratos.
- **Escopo de usuário individual**: não existe; preferências pessoais (idioma/aparência) fora deste incremento.
- **Fases 6 em diante**: nenhuma funcionalidade dessas fases implementada.

## 4. Decisões oficiais aprovadas para este incremento

Registradas aqui apenas como referência resumida — a formulação completa e a matriz de doze campos por decisão foram apresentadas no relatório de aprovação da Fase 5 e são mantidas pelo usuário como a fonte de aprovação; este documento não as reescreve por extenso, apenas resume o efeito prático de cada uma, para consulta rápida durante a implementação:

| # | Decisão | Efeito prático nesta implementação |
|---|---|---|
| 1 | Grupo de Imóveis fora do MVP; hierarquia de 3 níveis, código de escopo validado e extensível (não enum rígido) | `PolicyScope`/`ScopeType` implementado sem `enum` rígido de banco — ver Checkpoint 2 |
| 2 | Condomínio não é nível de configuração | Nenhuma referência a Condomínio em Configuration & Policy |
| 3 | Feature Flags fora do MVP | Nenhum artefato de Feature Flags criado |
| 4 | Fail-closed com distinção típada `NotConfigured` vs `Unavailable` | Contratos de leitura (Checkpoint 3) devem expor essa distinção explicitamente |
| 5 | Um único evento `PolicyUpdated` (não um por nível de escopo); payload mínimo sem valores/condições/PII | Documento 07 atualizado antes da publicação real do evento (Checkpoint 6) |
| 6 | Policy e Configuration são conceitos e persistências separados; este incremento implementa apenas Policy | Nenhuma tabela/entidade `Configuration*` criada |
| 7 | Meta de 50ms é critério de engenharia medido (p95 ≤ 50ms), não assumido | Protocolo de medição executado e reportado no Checkpoint 7 |
| 8 | Escopo de usuário não existe | Nenhum nível `USER` implementado |
| 2.1 | Vigência temporal fora do MVP | Nenhum campo de vigência no modelo |
| 2.2 | Nenhum valor padrão inventado; GLOBAL somente leitura, pode ficar vazio | Seed do Checkpoint 2 cria apenas `PolicyDefinition`, nunca `PolicyValue` |
| 2.3 | Nenhuma alteração em Reservations | Verificado estruturalmente por `ConfigurationSourceConventionTests.No_source_file_implements_reservations_functional_integration_yet` |

## 5. Catálogo inicial de políticas

### 5.1 `EARLY_CHECKIN` (tipo objeto)

`allowed: boolean`; `earliestTime: horário opcional`; `requiresCleaningCompleted: boolean`; `requiresForm: boolean`; `notifyFrontDesk: boolean`. Nenhum valor padrão.

### 5.2 `LATE_CHECKOUT` (tipo objeto)

`allowed: boolean`; `latestTime: horário opcional`; `chargeType: none|fixedAmount|percentage`; `chargeValue: decimal opcional`; `requiresPix: boolean`; `blocksCalendar: boolean`; `updatesCleaning: boolean`. Validações técnicas apenas (não de negócio/integração com Reservations): formato de horário não ambíguo; `percentage` entre 0-100; `fixedAmount ≥ 0`; `chargeValue` obrigatório quando `chargeType != none`; `chargeValue` ausente quando `chargeType == none`.

Nenhum outro campo, política ou valor padrão além do listado acima existe ou foi inventado nesta fase.

## 6. Checkpoint 1 — Fundação

### 6.1 Projetos criados

Solução `IHostPro.sln`, pasta `Contexts/Configuration`: `IHostPro.Contexts.Configuration.Domain`, `.Contracts`, `.Application`, `.Infrastructure`, `.Api`. Pasta `tests/Contexts/Configuration`: `IHostPro.Contexts.Configuration.Tests.Unit`, `.Tests.Integration`. Adicionados via `dotnet sln add --solution-folder` (nunca edição manual do `.sln`).

Referências entre camadas seguem exatamente a convenção de scaffolding já usada por Reservations (Architecture Principles §16): `Domain` não referencia nada além de `BuildingBlocks.Domain`; `Contracts` não referencia nenhum projeto (nada a expor ainda — leitores típados são Checkpoint 3, `PolicyUpdated` é Checkpoint 6); `Application` referencia `Domain`+`Contracts`+`BuildingBlocks.Application`; `Infrastructure` referencia `Domain`+`Application`+`BuildingBlocks.Domain`+`BuildingBlocks.Infrastructure`; `Api` referencia `Application`+`BuildingBlocks.Domain`+`Identity.Contracts` (única exceção cross-context aprovada, para os códigos de permissão).

Diferença deliberada em relação a Reservations: Configuration & Policy não consome nenhum outro contexto de forma síncrona — é o contexto sendo consumido (Architecture Principles §14, Exceção 1, já aprovada desde a Fase 0) — portanto `Application` não tem (e não deve ter) uma exceção do tipo "depende de X.Contracts".

### 6.2 Registro no Host

`IHostPro.Api/Program.cs`: `AddConfigurationModule(builder.Configuration)` + `AddConfigurationApplicationMediator()` registrados após os equivalentes de Reservations. Outbox Ancillary Wolverine (`EnrollAncillaryPostgresqlOutbox(..., "configuration_messaging", typeof(ConfigurationDbContext))`) registrado com zero rota de evento (nenhum evento existe até o Checkpoint 6). `appsettings.json`: connection string `Configuration` adicionada (`ihostpro_app`).

### 6.3 MigrationRunner

`typeof(ConfigurationDbContext).Assembly` adicionado à lista `moduleAssemblies` (descoberta por reflexão via `IModuleDbContext`). Bloco de outbox `configuration_messaging` provisionado espelhando exatamente o bloco de Reservations (`SetupResources()` + `GRANT`/`ALTER DEFAULT PRIVILEGES` para `ihostpro_app`). Exchange RabbitMQ `configuration-events` (topic) declarada, sem rota ainda. `appsettings.json`: connection string `Configuration` adicionada (`ihostpro_migrator`).

Observação: o schema `configuration` e o schema de mensageria `configuration_messaging` estão **provisionados em código**, mas o `MigrationRunner` real ainda não foi executado contra um banco real neste checkpoint (nenhuma migração existe — ver §6.5); a execução real do provisionamento acontece a partir do Checkpoint 2, quando a primeira migração for adicionada.

### 6.4 Permissões

`IdentityPermissionCodes.PoliciesRead` (`POLICIES:READ`) e `.PoliciesManage` (`POLICIES:MANAGE`) promovidas a constantes em `Identity.Contracts`. Ambas já estavam seedadas em `IdentityCatalogSeed.cs` (ADMIN=`POLICIES:MANAGE`, AI_AGENT=`POLICIES:READ`) desde uma fase anterior — nenhuma alteração de seed necessária, apenas a promoção da string literal para constante. Consumo real (autorização de endpoint) é Checkpoint 4.

### 6.5 Schemas e persistência

`ConfigurationDbContext` criado, `SchemaName => "configuration"`, herda `BaseDbContext` (Global Query Filter tenant-aware). Mapeamento de envelope Wolverine (`MapWolverineEnvelopeStorage("configuration_messaging")`) já ativo desde este checkpoint, mesmo sem nenhum evento real — aplica desde o primeiro momento a correção de composição Wolverine já registrada na Fase 2, Checkpoint 6 (nunca reproduzir o defeito de envelopes persistidos silenciosamente no Main Store).

Deliberadamente **zero `DbSet`s** neste checkpoint. `PolicyDefinition`/`PolicyValue`/`PolicyAuditEntry` e a primeira migração pertencem ao Checkpoint 2. Valores de escopo GLOBAL nunca serão mapeados nesta mesma tabela tenant-aware (Decisão 2.2/§4) — terão persistência própria, sem RLS, sem `TenantId`.

### 6.6 Documentação inicial

Este documento, criado nesta etapa. Documento 07 e Documento 08 não alterados neste checkpoint (nenhum evento e nenhuma decisão de catálogo nova a registrar ainda além do já aprovado no relatório da Fase 5). `Architecture Principles.md` não alterado (nenhuma correção factual necessária — a exceção síncrona de Configuration & Policy já estava registrada desde a Fase 0).

**Documento 000 (Índice de Documentação) não alterado neste checkpoint.** Sua seção "Phase Homologation Records" é explicitamente descrita como "retrospective" (registro de fases encerradas) e hoje lista apenas as Fases 1-3. Duas observações, nenhuma corrigida silenciosamente:

- A Fase 4 já está encerrada e publicada em `master`, mas seu registro nunca foi adicionado a essa lista — lacuna pré-existente, não introduzida por este incremento, fora do escopo autorizado desta tarefa.
- A Fase 5 (este documento) ainda está em andamento (apenas Checkpoint 1 concluído) — adicioná-la agora à seção "retrospective" a caracterizaria como encerrada, o que não é o caso. A entrada será adicionada ao Documento 000 quando o Incremento 1 for de fato encerrado (Checkpoint 7 — Homologação), seguindo o mesmo padrão usado pelas Fases 1-3.

### 6.7 Testes de arquitetura

`ConfigurationDependencyTests.cs` (7 testes) — isolamento total de camada (Domain sem Application/Infrastructure/Api/EF Core; Application sem Infrastructure/Api; Contracts sem AspNetCore/EF Core/Domain/Infrastructure; Domain/Application/Infrastructure/Contracts sem dependência de nenhum outro Bounded Context; Api depende apenas de `Identity.Contracts`, nunca de outro contexto; Api nunca referencia `IdentityDbContext`/`PropertyManagementDbContext`/`ReservationsDbContext`; `ConfigurationDbContext` possui `SchemaName == "configuration"`).

`ConfigurationSourceConventionTests.cs` (4 testes) — nenhum arquivo-fonte contém os literais `"POLICIES:READ"`/`"POLICIES:MANAGE"` fora de `IdentityPermissionCodes`; nenhum arquivo-fonte referencia o schema de mensageria de outro contexto; nenhum arquivo-fonte implementa integração funcional com Reservations ainda (`ReservationsDbContext`/`IReservationReader`/`IReservationsRequestDispatcher`); nenhuma migração existe ainda (guarda a ser substituída, não apenas relaxada, no Checkpoint 2).

**Defeito encontrado e corrigido durante esta homologação** (ver §8.1): três comentários de documentação nos próprios arquivos novos de Configuration citavam literalmente os nomes de classes de Reservations usados como modelo de design (`ReservationsDbContext`, `ReservationsDbContextFactory`, `IReservationsRequestDispatcher`), disparando falso positivo em `No_source_file_implements_reservations_functional_integration_yet`. Corrigido reformulando os comentários para descrever o padrão sem repetir o literal proibido, sem perda de informação de rastreabilidade do design.

## 7. Checkpoint 2 — Domínio e persistência

### 7.1 Modelo de domínio (`IHostPro.Contexts.Configuration.Domain`)

- `PolicyScopeType` — enum `Global`/`Tenant`/`Property`, persistido via `HasConversion<string>()` (coluna `varchar`, nunca um `ENUM` nativo do PostgreSQL) — decisão 1: "não usar PostgreSQL enum rígido... persistir o código do escopo de forma validada e extensível."
- `PolicyScope` — value object (`Type` + `ReferenceId` opcional), `Create`/`Tenant()`/`Property(id)`/`Global()`, valida que `Property` exige `ReferenceId` não vazio e que `Tenant`/`Global` não carregam `ReferenceId`.
- `PolicyVersion` — value object envolvendo um `int` (`First()`=1, `Next()`, rejeita valores `< 1`).
- `PolicyValueType` — enum `Object` (único tipo hoje, ambas as políticas do catálogo são `type: object`), mesmo padrão `HasConversion<string>()`.
- `PolicyDefinition` — `Entity<string>` (catálogo do sistema, nunca tenant-owned): `Name`/`Description`/`Category`/`ValueType`/`SchemaVersion`/`IsActive`. Nenhum comando de escrita exposto — só existe via seed.
- `PolicyValue` — `AggregateRoot<Guid>`, `ITenantOwned`, apenas escopos `Tenant`/`Property`: `CreateInitialVersion`/`CreateNextVersion` (razão obrigatória, rejeitam escopo `Global`), `Supersede()` (só marca `IsCurrent = false`, nunca altera `Value`) — append-only.
- `PolicyAuditEntry` — `Entity<Guid>`, `ITenantOwned`: versão/valor anterior e novo, autor, data/hora, razão, origem, `SessionId`/`IpAddress` opcionais. Nunca registra credenciais (nenhum campo poderia carregá-las).
- `GlobalPolicyValue` — `Entity<Guid>`, **sem** `ITenantOwned`, persistência própria para o escopo GLOBAL (decisão §4: "valores GLOBAL não devem ser misturados na tabela tenant-aware protegida por RLS").
- `EffectivePolicyResult` — **deliberadamente adiado para o Checkpoint 3.** É o único dos sete tipos citados em §4 que não foi criado agora: seu formato depende diretamente da semântica de resolução (`Resolved`/`NotConfigured`/`Unavailable`, decisão 4) e dos leitores típados, ambos objeto do Checkpoint 3 ("Resolução e contrato"). `PolicyScope`/`PolicyVersion`/`PolicyValueType`, em contraste, são pré-requisitos estruturais diretos de `PolicyDefinition`/`PolicyValue`/`PolicyAuditEntry` e por isso foram criados agora.

### 7.2 Persistência — migração `InitialCreate`

Schema `configuration`, quatro tabelas:

- `policy_definitions` — catálogo, **sem RLS**, `HasData` com as duas definições do catálogo.
- `policy_values` — tenant-owned, RLS `ENABLE`+`FORCE`, coluna `value` (`jsonb`), FK `policy_code → policy_definitions.code` (`Restrict`, intra-contexto — nunca cross-contexto). Índice único `(tenant_id, policy_code, scope_type, scope_reference_id, version)`. Índice único parcial adicional, via SQL bruto (`CREATE UNIQUE INDEX ... WHERE is_current`), sobre `(tenant_id, policy_code, scope_type, COALESCE(scope_reference_id, '00000000-...'))` — o `COALESCE` é necessário porque o PostgreSQL trata `NULL` como distinto de `NULL` em índices únicos, e sem ele duas versões `TENANT` (cujo `scope_reference_id` é sempre `NULL`) nunca conflitariam entre si, justamente o escopo que a garantia "uma única versão corrente por escopo" mais precisa cobrir.
- `policy_audit_log` — tenant-owned, RLS `ENABLE`+`FORCE`, append-only.
- `global_policy_values` — **sem RLS**, sem `tenant_id`, índice único em `policy_code`, permanece vazia neste incremento (decisão 2.2).

Privilégios de `ihostpro_app` (mínimo necessário por tabela, nunca DDL/BYPASSRLS):

| Tabela | Privilégios |
|---|---|
| `policy_definitions` | `SELECT` apenas |
| `global_policy_values` | `SELECT` apenas |
| `policy_values` | `SELECT`, `INSERT`, e `UPDATE` restrito à coluna `is_current` (`GRANT UPDATE (is_current)`) — nunca `UPDATE` amplo, para que nenhum caminho de aplicação possa alterar `Value`/`Reason`/etc. de uma versão já gravada, e nunca `DELETE` |
| `policy_audit_log` | `SELECT`, `INSERT` apenas — mesma convenção append-only de `reservation_audit_log` |

### 7.3 Seed do catálogo

`ConfigurationCatalogSeed.PolicyDefinitions` (`IHostPro.Contexts.Configuration.Infrastructure/Seed`), aplicado via `HasData` na migração — apenas `EARLY_CHECKIN` e `LATE_CHECKOUT`, `ValueType = Object`, `SchemaVersion = 1`, `IsActive = true`. `Category = "CHECK_IN_OUT"` para ambas — rótulo puramente organizacional, sem efeito de comportamento; nenhuma taxonomia de categorias está documentada em nenhum lugar do catálogo aprovado (§3), então este valor foi escolhido como agrupamento razoável e de baixo risco, não como regra de negócio. Sinalizado aqui de forma transparente, não decidido silenciosamente. **Nenhum `PolicyValue` ou `GlobalPolicyValue` é seedado** (decisão 2.2) — verificado por teste de integração dedicado.

### 7.4 Teste de arquitetura atualizado

`ConfigurationSourceConventionTests.No_migration_exists_yet` substituído por `Exactly_one_migration_exists_and_is_named_InitialCreate` — cumprindo a própria instrução do teste original ("this test must be replaced, not merely relaxed, once the first migration is added").

### 7.5 Testes unitários (`IHostPro.Contexts.Configuration.Tests.Unit`, 35 testes)

`PolicyScopeTests`, `PolicyVersionTests`, `PolicyDefinitionTests`, `PolicyValueTests`, `PolicyAuditEntryTests`, `GlobalPolicyValueTests` — cobrem toda validação de construção: escopo `Property` exige `ReferenceId`, `Tenant`/`Global` rejeitam `ReferenceId`; versão mínima 1; razão obrigatória ao criar/versionar `PolicyValue`; `PolicyValue` rejeita escopo `Global`; `Supersede()` não altera `Value`; `PolicyAuditEntry` rejeita escopo `Global`, exige razão/origem.

### 7.6 Testes de integração/fundação (`IHostPro.Contexts.Configuration.Tests.Integration`, 26 testes, PostgreSQL real via Testcontainers)

`ConfigurationFoundationTests.cs`, espelhando `ReservationsFoundationTests.cs` — migração aplica-se de forma limpa e idempotente; catálogo seedado corretamente (`EARLY_CHECKIN`/`LATE_CHECKOUT`) e nenhuma linha em `policy_values`/`global_policy_values`; RLS fail-closed em `policy_values` (tenant correto vê sua própria linha; tenant diferente vê zero linhas e não consegue alterá-las via `UPDATE` direto; ausência de tenant vê zero linhas sem erro; `INSERT` sem `app.tenant_id` falha fechado com `DbUpdateException`); `ENABLE`/`FORCE ROW LEVEL SECURITY` ativos em `policy_values`/`policy_audit_log`; **ausência** de RLS confirmada em `policy_definitions`/`global_policy_values`; papel de aplicação não cria/altera/derruba tabelas, não desabilita RLS, não tem `BYPASSRLS`; `policy_values` aceita `UPDATE` apenas em `is_current` e rejeita `UPDATE` na coluna `value` e qualquer `DELETE`; índice único parcial comprovado (`Only_one_current_row_is_allowed_per_scope` — duas linhas `IsCurrent=true` para o mesmo escopo geram `DbUpdateException`); `policy_audit_log` aceita `INSERT` e rejeita `UPDATE`/`DELETE`; `policy_definitions`/`global_policy_values` são legíveis mas não graváveis pelo papel de aplicação; schema de mensageria `configuration_messaging` provisionado e sem privilégios de DDL para `ihostpro_app`.

## 8. Checkpoint 3 — Resolução e contrato

### 8.1 Contratos públicos (`IHostPro.Contexts.Configuration.Contracts`)

Projeto mantido com **zero referências de projeto** (nem mesmo `BuildingBlocks.Domain`) — nenhum outro Contexto Delimitado, e nada em Contracts, pode depender do resolvedor genérico ou de qualquer tipo interno.

- `PolicyReadStatus` — enum `Resolved`/`NotConfigured` apenas (decisão 4/§5). `Unavailable` deliberadamente **não** é um valor deste enum — ver §8.4.
- `PolicyResolvedScope` — enum `Property`/`Tenant`/`Global`, tipo próprio de Contracts (nunca reutiliza `PolicyScopeType` do Domain, que Contracts não pode referenciar).
- `PolicyReadResult<TValue>` — `Status`; `Value` (só quando `Resolved`); `ResolvedScope` (só quando `Resolved`); `Version` (`int?`, `null` quando `ResolvedScope == Global`, já que `GlobalPolicyValue` não tem histórico de versão — nenhum campo de versão foi adicionado a essa tabela, que a especificação nunca exigiu).
- `EarlyCheckInPolicy`/`LateCheckoutPolicy`/`LateCheckoutChargeType` — as formas típadas exatas do catálogo (§3), nenhum campo além do documentado.
- `IEarlyCheckInPolicyReader`/`ILateCheckoutPolicyReader` — os dois únicos contratos síncronos expostos; nenhum leitor genérico baseado em string é exposto (decisão §5: "Não expor um leitor genérico baseado em string").
- `PolicyEngineUnavailableException` — o único canal de falha de indisponibilidade (ver §8.4).

### 8.2 Resolvedor genérico (interno ao contexto)

`IPolicyValueResolver`/`PolicyValueResolver` (`Configuration.Infrastructure/Resolution`), ambos `internal` — decisão §5: "manter o resolvedor genérico interno ao contexto." Implementa a precedência PROPERTY → TENANT → GLOBAL: consulta `policy_values` (`IsCurrent = true`) no escopo PROPERTY (quando `propertyId` informado), depois TENANT, depois `global_policy_values`. Abre sua própria transação curta, somente leitura, tenant-scoped (`TenantAwareTransactionScope`, mesmo padrão de `PropertyReservationEligibilityReader`), sempre fechada antes do retorno — nenhuma transação de Configuration permanece aberta simultaneamente com uma transação de escrita do consumidor (decisão §5).

`EarlyCheckInPolicyReader`/`LateCheckoutPolicyReader` (também `internal`, mirrors `ConfigurationRequestDispatcher` — implementam as interfaces públicas mas a classe concreta nunca é referenciável fora deste assembly, garantia do compilador, não apenas convenção documentada) chamam o resolvedor e desserializam o JSON bruto para o tipo típado correspondente via `System.Text.Json` (`JsonSerializerDefaults.Web` + conversor de enum camelCase, para bater exatamente com os nomes de campo do catálogo, ex.: `chargeType: "percentage"`).

### 8.3 Registro em DI

`ConfigurationModuleExtensions.AddConfigurationModule` registra `IPolicyValueResolver`, `IEarlyCheckInPolicyReader`, `ILateCheckoutPolicyReader` como `Scoped`.

### 8.4 Fail-closed: `NotConfigured` nunca esconde indisponibilidade

Decisão 4 exige distinguir `NotConfigured` (motor respondeu corretamente, nada configurado) de indisponibilidade (timeout, falha de banco/cache, erro inesperado, motor fora do ar) — mas §5 já restringe `Status` a apenas dois valores. Reconciliação: indisponibilidade nunca é um valor de `PolicyReadStatus` — é representada por `PolicyEngineUnavailableException`, lançada ("thrown"), a opção explicitamente permitida por §5 ("Falhas de infraestrutura devem ser lançadas ou retornadas como falha explícita"). Cada leitor típado normaliza **qualquer** exceção (falha de banco, erro de desserialização, erro inesperado) — exceto cancelamento explícito do chamador — em `PolicyEngineUnavailableException`, nunca convertendo-a silenciosamente em `NotConfigured`. Esta escolha (exceção tipada, não `Result<T>`) segue a convenção já estabelecida pelos três outros projetos `.Contracts` existentes (Identity/PropertyManagement/Reservations), nenhum dos quais depende de `BuildingBlocks.Domain` ou usa `Result<T>` em sua superfície pública.

### 8.5 Testes unitários adicionados (`IHostPro.Contexts.Configuration.Tests.Unit`, +11 testes, total 46)

`PolicyReadResultTests` (3), `PolicyValueJsonShapeTests` (6 — round-trip exato dos nomes de campo do catálogo, incluindo os três valores de `chargeType`), `PolicyEngineUnavailableExceptionTests` (2).

### 8.6 Testes de integração adicionados (`IHostPro.Contexts.Configuration.Tests.Integration`, +11 testes, total 37)

`PolicyResolutionTests.cs`, PostgreSQL real via Testcontainers, leitores resolvidos pela composição pública (`AddConfigurationModule`) exatamente como um consumidor real faria — nunca referenciando o resolvedor/implementações internas por nome:

- Precedência: PROPERTY vence TENANT e GLOBAL; TENANT vence GLOBAL quando não há valor PROPERTY; GLOBAL é usado quando nada existe em TENANT/PROPERTY (com `Version == null`); `NotConfigured` quando nada existe em nenhum nível; resolução sem `propertyId` ignora o nível PROPERTY; versões substituídas (`IsCurrent = false`) nunca são resolvidas; dados cross-tenant nunca são resolvidos.
- Leitores típados: round-trip completo de todos os campos de `EarlyCheckInPolicy`/`LateCheckoutPolicy`, incluindo `chargeType`/`chargeValue`.
- Fail-closed: um valor armazenado sintaticamente válido como JSON mas incompatível com a forma típada (`{"allowed":"not-a-boolean"}`) lança `PolicyEngineUnavailableException`, nunca retorna `NotConfigured`.
- Ausência de transação cruzada: duas resoluções consecutivas no mesmo leitor/mesmo escopo de DI, ambas bem-sucedidas (prova indireta de que a transação curta do resolvedor é corretamente fechada a cada chamada — uma transação vazada faria a segunda chamada falhar com `NestedUnitOfWorkException`).

**Defeitos encontrados e corrigidos durante a escrita destes testes** (erros de configuração do próprio teste, não do código de produção — ver §10.2).

## 9. Checkpoint 4 — API administrativa

### 9.1 Camada de aplicação (`IHostPro.Contexts.Configuration.Application/Policies`)

Cinco operações, nunca mais que isso (§8 da instrução): `ListPolicyDefinitionsQuery`; `GetPolicyValueByScopeQuery`; `GetEffectivePolicyQuery`; `CreatePolicyValueVersionCommand`; `GetPolicyHistoryQuery`. Todos os campos de escopo nos Commands/Queries são `string` (nunca `PolicyScopeType` do Domain) — Api nunca referencia o Domain deste contexto, então o parsing/validação de `scopeType` acontece inteiramente no handler, via `PolicyScopeParser` (helper interno compartilhado).

**`PolicyScopeParser`** decide a distinção `scope_not_supported` (400) vs `forbidden` (403): `scopeType` não reconhecido ou `Property` sem `propertyId` → `scope_not_supported` (problema estrutural do request); `scopeType == Global` → `forbidden` (proibição de negócio absoluta, decisão 2.2 — nunca uma questão de formato).

**`PolicyValueValidation`** aplica a validação técnica do catálogo (§3) antes de qualquer escrita: desserialização estrutural para `EarlyCheckInPolicy`/`LateCheckoutPolicy`; para `LATE_CHECKOUT`, as quatro regras exatas do catálogo (`percentage` 0-100; `fixedAmount ≥ 0`; `chargeValue` obrigatório quando `chargeType != none`; `chargeValue` ausente quando `chargeType == none`) — fecha o débito registrado no Checkpoint 3 (§13, item revogado nesta seção).

`CreatePolicyValueVersionCommandHandler` implementa a concorrência otimista por comparação explícita: `ExpectedVersion == null` exige que nenhuma versão corrente exista; caso contrário deve bater exatamente com a versão corrente lida na mesma transação — qualquer divergência é `version_conflict`, nunca uma sobrescrita silenciosa. `PolicyValue.Supersede()` é chamado na linha anterior (nunca apagada) antes de inserir a nova, na mesma transação, junto com o `PolicyAuditEntry` correspondente.

Portas expostas para Infrastructure implementar: `IPolicyDefinitionReader`; `IPolicyValueReader` (leitura não rastreada, para as consultas administrativas — nunca confundida com os leitores típados do Checkpoint 3, que resolvem hierarquicamente); `IPolicyValueRepository` (leitura rastreada da linha corrente, para `Supersede()`); `IPolicyAuditWriter`; `ICreatePolicyValueVersionExecutor` (mirrors `IUpdateReservationExecutor` — traduz `DbUpdateException` do índice único parcial, a última linha de defesa contra uma corrida genuína que passe pela pré-checagem, em `version_conflict`).

### 9.2 Camada de infraestrutura e composição

`PolicyDefinitionReader`/`PolicyValueReader`/`PolicyValueRepository`/`PolicyAuditWriter`/`CreatePolicyValueVersionExecutor` implementam as portas acima em `Configuration.Infrastructure/Persistence`. `ConfigurationCommandDispatchExtensions.AddConfigurationCommandDispatch()` (mirrors `ReservationsCommandDispatchExtensions`) registra `ValidationBehavior` (open generic) e, apenas para as três queries que tocam `ConfigurationDbContext` diretamente (`ListPolicyDefinitionsQuery`/`GetPolicyValueByScopeQuery`/`GetPolicyHistoryQuery`), `TenantTransactionBehavior<,,ConfigurationDbContext>` fechado por tipo de mensagem. `GetEffectivePolicyQuery` e `CreatePolicyValueVersionCommand` deliberadamente **não** recebem esse *behavior*: o primeiro delega inteiramente aos leitores típados do Checkpoint 3 (que já abrem sua própria transação curta no mesmo `ConfigurationDbContext`); o segundo usa `ICreatePolicyValueVersionExecutor` diretamente — em ambos os casos, adicionar o *behavior* genérico aninharia uma segunda transação e lançaria `NestedUnitOfWorkException`. `IHostPro.Api/Program.cs` passou a chamar `AddConfigurationCommandDispatch()` (que já chama `AddConfigurationApplicationMediator()` internamente) no lugar da chamada anterior, mais simples.

### 9.3 API (`IHostPro.Contexts.Configuration.Api`)

`PoliciesController`, rota base `api/v1/policies`, mirrors `ReservationsController`:

| Método | Rota | Política |
|---|---|---|
| GET | `/api/v1/policies` | `POLICIES:READ` |
| GET | `/api/v1/policies/{policyCode}/values?scopeType=&propertyId=` | `POLICIES:READ` |
| GET | `/api/v1/policies/{policyCode}/effective?propertyId=` | `POLICIES:READ` |
| GET | `/api/v1/policies/{policyCode}/history?scopeType=&propertyId=` | `POLICIES:READ` |
| POST | `/api/v1/policies/{policyCode}/values` | `POLICIES:MANAGE` |

Nenhum endpoint de criação/alteração de `PolicyDefinition`, exclusão física, edição de GLOBAL, restauração automática de versão ou vigência temporal existe (§8, "Must NOT allow"). `ConfigurationIdentityReader` (mirrors `ReservationsIdentityReader`) lê `sub`/`tenant_id` exclusivamente dos claims do token validado. `PolicyValueDetailResponse.Value`/`EffectivePolicyResponse.Value` são embutidos como JSON real (`JsonElement`/`object`), nunca como string contendo JSON escapado.

`PolicyResultHttpMapper` distingue os sete resultados exigidos por §8 — cada um com seu próprio `Title` de `ProblemDetails`, nunca agrupados em baldes genéricos como o mapper de Reservations faz: `policy_not_found` (404); `invalid_policy_value` (400); `scope_not_supported` (400); `policy_not_configured` (404); `version_conflict` (409); `forbidden` (403); `validation_error` (400, fallback para qualquer código não listado, incluindo os códigos estáveis do FluentValidation). Toda ação tem `ProducesResponseType` desde o primeiro commit (ainda não realizado).

### 9.4 Permissões — consumo real

`IdentityPermissionCodes.PoliciesRead`/`.PoliciesManage`, promovidas a constantes no Checkpoint 1, são finalmente consumidas pelo controller. **Defeito real encontrado e corrigido** (ver §11.3): as duas políticas de autorização nunca haviam sido registradas em `IdentityAuthorizationExtensions.AddIdentityAuthorization()` — corrigido adicionando as duas chamadas `.AddPolicy(...)` faltantes, exatamente no padrão já usado para `PropertiesManage`/`ReservationsManage`/etc. Por decisão de catálogo pré-existente (não alterada nesta tarefa — `IdentityCatalogSeed.cs`), `POLICIES:READ` e `POLICIES:MANAGE` são deliberadamente assimétricas: apenas ADMIN tem `MANAGE`, apenas AI_AGENT tem `READ` — nenhum papel tem as duas. Os testes HTTP deste checkpoint respeitam essa assimetria (usam o papel certo para cada ação).

### 9.5 Testes unitários adicionados (`IHostPro.Contexts.Configuration.Tests.Unit`, +25 testes, total 71)

Fakes escritos à mão (sem biblioteca de mock, mesma convenção do resto da solução): `FakePolicyDefinitionReader`, `FakePolicyValueReader`, `FakePolicyValueRepository`, `FakePolicyAuditWriter`, `PassThroughCreatePolicyValueVersionExecutor`, `FakeEarlyCheckInPolicyReader`/`FakeLateCheckoutPolicyReader`. Cobrem os cinco handlers: `policy_not_found`; `forbidden` (Global); `scope_not_supported` (Property sem `propertyId`); `invalid_policy_value` (forma malformada e as quatro regras de `LATE_CHECKOUT`); `version_conflict` (três variantes: `expectedVersion` nulo com corrente existente, `expectedVersion` divergente, `expectedVersion` informado sem corrente); criação da versão 1; criação da próxima versão com `Supersede()` da anterior comprovado.

### 9.6 Testes de integração HTTP reais adicionados (`IHostPro.Contexts.Configuration.Tests.Integration`, +23 testes, total 60)

`ConfigurationEndpointsTests.cs`, mirrors `ReservationsEndpointsTests.cs` — host ASP.NET Core real (`TestServer`), JWT real emitido pelo próprio stack do Identity, PostgreSQL real (Testcontainers) para Identity (catálogo de permissões) e Configuration — sem dependência de Property Management (Configuration não faz nenhuma consulta síncrona cruzada de escrita). Cobre, via HTTP real: 401 sem token; 403 para papel sem `POLICIES:READ`/`POLICIES:MANAGE`; os sete `ProblemDetails` (`policy_not_found`, `invalid_policy_value` em duas variantes incluindo a regra de `LATE_CHECKOUT`, `scope_not_supported`, `policy_not_configured`, `version_conflict` em duas variantes, `forbidden`, `validation_error`); caminhos de sucesso para as cinco operações; o ciclo completo criar-nova-versão-com-`expectedVersion`-correto seguido de leitura do histórico confirmando duas linhas (a nova corrente, a anterior superseded).

### 9.7 Validação adicional: migração aplicada a banco de desenvolvimento persistente real

Além dos ambientes efêmeros (Testcontainers) usados pelos testes automatizados, o `IHostPro.MigrationRunner` real foi executado contra o Postgres de desenvolvimento persistente do repositório (`ihostpro-postgres`, container do `docker-compose.yml`) durante a preparação do Checkpoint 4 — os schemas `configuration`/`configuration_messaging`, as quatro tabelas, o catálogo seedado (`EARLY_CHECKIN`/`LATE_CHECKOUT`) e a exchange RabbitMQ `configuration-events` foram confirmados presentes e corretos nesse ambiente real, não apenas efêmero. Nenhum dado de outro contexto foi alterado.

### 9.8 Regeneração do cliente NSwag — procedimento controlado

A regeneração exigia a API real em execução, o que exige RabbitMQ em `localhost:5672` — porta já ocupada pelo container `ihostpro-homolog-rabbitmq` (ambiente de homologação, em uso, credenciais diferentes das do ambiente dev). Por instrução explícita do usuário, executado um procedimento controlado e totalmente reversível:

1. Estado de `ihostpro-homolog-rabbitmq` registrado antes de qualquer ação: `running`, `RestartPolicy: no`, portas `5672→5672`/`15672→15674`, volume nomeado `7d69184067864c740e4d17597f429421793b9d4c94f21cc71f0cadc384774af2` montado em `/var/lib/rabbitmq`, credenciais `ihostpro`/`ihostpro_homolog`.
2. `docker stop ihostpro-homolog-rabbitmq` (nunca `rm`, nunca `down -v`) — porta 5672 confirmada livre.
3. RabbitMQ de dev iniciado via `docker compose up -d rabbitmq` (o serviço definido no `docker-compose.yml` do próprio repositório), aguardado até `healthy`.
4. `IHostPro.MigrationRunner` e `IHostPro.Api` executados com as credenciais de dev (`ihostpro`/`ihostpro_dev`) passadas por variável de ambiente (`RabbitMq__Username`/`RabbitMq__Password`) — nunca gravadas em nenhum arquivo de configuração persistente, nunca usando as credenciais de homolog no ambiente dev.
5. `swagger.json` real confirmado com as quatro rotas de `/api/v1/policies`; `npm run generate:api` executado; os cinco métodos (`policies`, `valuesGET`, `valuesPOST`, `effective`, `history`) e as quatro interfaces (`PolicyDefinitionResponse`, `PolicyValueDetailResponse`, `EffectivePolicyResponse`, `CreatePolicyValueVersionRequest`) confirmados corretamente típados, com `Observable<T>` correto em cada método (nenhum `void` indevido); `ProblemDetails` já existente no cliente reaproveitado sem alteração de forma.
6. `npx tsc --noEmit` confirmou o cliente gerado compilando sem erros.
7. Uma segunda geração executada e comparada byte a byte (`diff`) contra a primeira — **idêntica**, confirmando determinismo.
8. API parada; apenas o RabbitMQ de dev iniciado nesta operação foi parado (`docker stop ihostpro-rabbitmq`, container preservado, não removido); `ihostpro-homolog-rabbitmq` reiniciado (`docker start`) e confirmado de volta ao estado exato do passo 1 (mesmo volume, mesmas portas, mesma `RestartPolicy`, `rabbitmq-diagnostics ping` respondendo).
9. Confirmada ausência de processos/portas/containers órfãos ao final — apenas os processos de build server do próprio `dotnet` (normais, presentes durante toda a sessão, não relacionados a esta operação) permaneceram.

O diff resultante em `api-client.ts` foi puramente aditivo — 439 linhas inseridas, zero removidas/alteradas nas seções pré-existentes.

## 10. Checkpoint 5 — Frontend

### 10.1 Estrutura do módulo (`frontend/IHostPro.Web/src/app/features/policies/`)

Mirrors exatamente a convenção já estabelecida por `users`/`property-management`/`reservations` (Fases 4): `policies.service.ts` (wrapper fino sobre os cinco métodos do `Client` gerado — `policies()`, `valuesGET()`, `valuesPOST()`, `effective()`, `history()`); `policy-error.ts` (classificador do `ProblemDetails` lançado pelo `Client`); `policies-list/` (alvo da rota); `policy-detail-dialog/` (diálogo único de visualização/gestão por política, reaproveitando `ConfirmDialog`-like o padrão de diálogo "seção múltipla com formulário embutido" já usado por `role-management-dialog`).

### 10.2 Rota e navegação — reconciliação explícita do catálogo de permissões assimétrico

O catálogo de permissões aprovado (`IdentityCatalogSeed`, não alterado nesta tarefa) é deliberadamente assimétrico: apenas ADMIN tem `POLICIES:MANAGE`, apenas AI_AGENT tem `POLICIES:READ` — nenhum papel tem as duas (já registrado em §9.4). A instrução original do frontend (§9) pede "nav gated by POLICIES:READ", o que, aplicado literalmente, deixaria ADMIN (o único papel capaz de efetivamente usar a tela — criar novas versões) sem conseguir sequer navegar até ela pela barra lateral.

**Decisão de reconciliação, tomada nesta camada de apresentação, sem alterar nenhuma regra de negócio ou permissão do backend**: a rota `/policies` e o item de navegação "Políticas" são liberados por `permissions: ['POLICIES:READ', 'POLICIES:MANAGE']` — semântica OU, usando o suporte já existente de `permissionGuard` a um array de códigos (`.some(...)`), sem nenhuma alteração no guard em si. `NavItem.requiredPermission` (`admin-layout.ts`) foi ampliado de `string` para `string | string[]` — extensão aditiva, retrocompatível com as quatro entradas existentes (`users`/`condominiums`/`properties`/`reservations`, cada uma ainda com um único código). A seção "Nova versão" dentro do diálogo permanece estritamente gated por `POLICIES:MANAGE` (`canManage` computed sobre `UserProfileService.hasPermission`), espelhando exatamente a exigência real do backend (`POST .../values` exige `POLICIES:MANAGE`) — um usuário com apenas `POLICIES:READ` (AI_AGENT) consegue abrir e visualizar a tela, mas nunca vê o formulário de criação.

Esta reconciliação é sinalizada aqui de forma transparente, exatamente como a instrução de engenharia exige diante de uma especificação ambígua diante do estado real do catálogo — não é uma regra de negócio inventada, é uma composição OR de duas permissões já existentes e aprovadas, aplicada apenas ao gate de navegação/rota (nunca às ações em si).

### 10.3 Serviço e mapeamento de erros

`PoliciesService` — wrapper fino, sem lógica própria, idêntico em forma a `UsersService`/`ReservationsService`. `classifyPolicyActionError` (`policy-error.ts`) difere estruturalmente de `classifyUserActionError`/`classifyReservationError`: `PolicyResultHttpMapper` (backend, §9.3) distingue seus sete resultados pelo `Title` do `ProblemDetails` em si (`policy_not_found`, `invalid_policy_value`, `scope_not_supported`, `policy_not_configured`, `version_conflict`, `forbidden`, `validation_error`) — nunca por um array `codes` (populado apenas no fallback genérico `validation_error`, com os códigos do FluentValidation). O classificador extrai `{ status, title, codes }`; os componentes mapeiam `title` diretamente para uma chave i18n (`policies.detail.form.errors.<title>`), com `generic` como fallback para qualquer `title` não reconhecido.

### 10.4 `PoliciesList` — catálogo (somente leitura)

Lista as duas políticas seedadas (`GET /api/v1/policies`) em uma tabela simples (código/nome/categoria/ação "Gerenciar") — sem paginação, sem filtro (catálogo fixo, nunca criado/editado/removido por esta UI). Estados `loading`/`loaded`/`empty`/`error`, mesmo padrão de sinal (`LoadState`) usado em toda a Fase 4.

### 10.5 `PolicyDetailDialog` — visualização, histórico e criação de nova versão

Um único seletor de escopo (Tenant ou Property + campo de texto livre para `propertyId`, mesma convenção de `reservation-form-dialog` — sem *picker*, sem validação de formato) controla, ao clicar "Carregar", três leituras coordenadas na mesma chamada `forkJoin`: valor efetivo (`GET .../effective`, resolução hierárquica Property→Tenant→Global), valor exato no escopo selecionado (`GET .../values`, nunca resolvido hierarquicamente — 404 `policy_not_configured` é um resultado esperado e tratado como estado, nunca como erro de UI) e histórico no escopo selecionado (`GET .../history`, mais recente primeiro). GLOBAL nunca é uma opção selecionável — `GetPolicyValueByScopeQuery`/`GetPolicyHistoryQuery` rejeitam esse escopo com `forbidden` (confirmado lendo o handler e seu teste unitário antes de implementar), então só é visível indiretamente, via `resolvedScope` do valor efetivo.

**Indicação de herança**: computada comparando `resolvedScope` do valor efetivo com o escopo selecionado — "Definido neste nível" (iguais), "Herdado da configuração da empresa" (`resolvedScope == Tenant` com Property selecionado), "Herdado do padrão do sistema" (`resolvedScope == Global`), ou "Não configurado em nenhum nível" (`status != Resolved`).

**Criação de nova versão**: visível apenas para `POLICIES:MANAGE` (§10.2). O valor efetivo/exato é exibido como JSON bruto (visualização, nunca editada diretamente); apenas o formulário de nova versão tem forma típada por código de política — `EARLY_CHECKIN` (permitido, horário mais cedo, três *flags*) e `LATE_CHECKOUT` (permitido, horário mais tarde, tipo de cobrança, valor da cobrança, três *flags*), espelhando exatamente `EarlyCheckInPolicy`/`LateCheckoutPolicy` (Contracts) — inclusive a serialização `TimeOnly` como `"HH:mm:ss"` (o `<input type="time">` produz `"HH:mm"`; convertido nos dois sentidos por `toTimeOnlyValue`/`toTimeInputValue`) e a regra de consistência `chargeType`/`chargeValue` de `LATE_CHECKOUT` (percentage 0-100, fixedAmount ≥ 0, none exige `chargeValue` nulo) espelhada no cliente antes do envio — mesma racional já usada por `reservation-form-dialog`'s `checkOutBeforeCheckIn`: mirror de uma regra já existente no backend, para UX imediata, nunca uma regra nova, com o backend permanecendo a única autoridade. O formulário é sempre repopulado a partir do valor corrente recém-carregado (motivo sempre em branco); `expectedVersion` nunca é editável manualmente — é derivado exclusivamente da última leitura bem-sucedida (`version` do valor corrente, ou `undefined` quando nada está configurado), reforçando o fluxo real "carregar → ajustar → salvar" que a concorrência otimista do backend pressupõe.

### 10.6 i18n

Chave `layout.nav.policies` e seção `policies` completa (`list`, `detail` com `effective`/`history`/`form`/`errors`) adicionadas a `en.json`/`pt-BR.json`, espelhando a forma de aninhamento já usada por `users`.

### 10.7 Testes unitários (Vitest) — 40 novos, 279/279 no total do frontend

| Arquivo | Testes |
|---|---|
| `policy-error.spec.ts` | 7 |
| `policies.service.spec.ts` | 5 |
| `policies-list/policies-list.spec.ts` | 4 |
| `policy-detail-dialog/policy-detail-dialog.spec.ts` | 21 |
| `admin-layout.spec.ts` (3 novos casos para o gate OR de "Políticas") | 3 |

`ng test --watch=false`: 279/279 aprovados (33 arquivos de teste). `ng build` (produção): 0 erros — inclui o *lazy chunk* `policies-list` confirmado gerado separadamente.

### 10.8 Testes Playwright E2E — escritos neste checkpoint, execução real adiada para o Checkpoint 7

`PoliciesAuthorizationE2ETests.cs` (2 testes: OPERATOR sem `POLICIES:READ`/`POLICIES:MANAGE` não vê o item de navegação nem acessa `/policies` diretamente; ADMIN vê e acessa) e `PoliciesE2ETests.cs` (5 testes: catálogo visível; primeira versão de `EARLY_CHECKIN` em escopo Tenant; independência entre escopo Tenant e Property; histórico com duas versões após uma edição; `LATE_CHECKOUT` com cobrança percentual) foram escritos mirando exatamente a estrutura de `PropertyManagementAuthorizationE2ETests.cs`/`PropertyManagementE2ETests.cs`, e compilam sem erros (`dotnet build tests/Frontend/IHostPro.Web.Tests.E2E/...`, confirmado).

**Lacuna real encontrada e corrigida em `WebE2EFixture.cs`**: o *fixture* nunca migrava o schema `configuration`/provisionava `configuration_messaging`/injetava `ConnectionStrings__Configuration` no subprocesso da API — herdado de antes deste incremento existir, nunca notado porque nenhum teste anterior precisava do contexto Configuration. Sem essa correção, qualquer teste de Policies falharia com "relation configuration.policy_definitions does not exist" contra o Postgres efêmero do próprio *fixture*. Corrigido adicionando, no mesmo padrão já usado para Identity/PropertyManagement/Reservations: migração de `ConfigurationDbContext` em `MigrateSchemasAsync`; provisionamento do outbox `configuration_messaging` em `ProvisionMessageStoresAsync`; `ConnectionStrings__Configuration` em `StartApiProcess`; referência de projeto a `IHostPro.Contexts.Configuration.Infrastructure` no `.csproj` do próprio projeto de testes E2E. `PolicyDefinition` é catálogo global (nunca por tenant — confirmado lendo `PolicyDefinition.cs`), então nenhum seed adicional de dado é necessário além da migração em si (o catálogo já vem embutido na migração `InitialCreate` via `HasData`).

**Execução real não realizada neste checkpoint**: `WebE2EFixture` usa uma porta RabbitMQ efêmera fixa (host `5672:5672` — sem *override*, documentado no próprio arquivo), a mesma porta atualmente ocupada por `ihostpro-homolog-rabbitmq` (ambiente de homologação, em uso). Rodar esta suíte agora exigiria repetir o mesmo procedimento controlado de troca temporária de RabbitMQ já documentado em §9.8 — uma operação explicitamente aprovada pelo usuário naquela ocasião especificamente para a regeneração do NSwag, não uma autorização permanente para repetição livre. Como o Checkpoint 7 (Homologação) já está formalmente definido para incluir "duas execuções consecutivas de E2E sem processos órfãos" contra o ambiente RabbitMQ/Postgres real, a execução efetiva destes sete novos testes (e de toda a suíte E2E already existente) fica consolidada nesse checkpoint, evitando uma segunda troca de ambiente ad-hoc. Nenhum teste foi marcado como `Skip`; simplesmente não foram executados nesta sessão — reportado aqui de forma explícita, sem alegar uma execução que não ocorreu.

### 10.9 Fora do escopo (confirmado não implementado)

Nenhuma UI para: Configurações; Feature Flags; Templates; regras SE/ENTÃO; vigência temporal; edição de GLOBAL — exatamente como §3 da instrução original exige.

## 11. Checkpoint 6 — Cache e eventos

### 11.1 `PolicyUpdated` — contrato e publicação via outbox

`Configuration.Contracts/PolicyUpdated.cs` — o único evento deste incremento (official decision 5: "não adotar um evento diferente para cada nível"). Payload mínimo, exatamente o exigido: `PolicyCode`; `ScopeType` ("Tenant" ou "Property" — nunca "Global"); `ScopeReferenceId` (Guid?); `PolicyVersion` (nunca reaproveita `IntegrationEvent.Version`, que é a versão do envelope, não do dado de negócio). `AggregateType = "PolicyValue"`; `AggregateId` = id da linha recém-criada. Este é o primeiro tipo em `Configuration.Contracts` que precisa de `IntegrationEvent` — o projeto ganhou sua primeira `ProjectReference` (a `BuildingBlocks.Messaging.Abstractions`), antecipada pelo próprio comentário do `.csproj` desde o Checkpoint 1.

`CreatePolicyValueVersionCommandHandler` enfileira o evento em `IIntegrationEventCollector` (cópia própria da abstração, mesma convenção de Reservations/PropertyManagement/Identity — Application layers nunca compartilham esse tipo entre contextos) logo após persistir a nova versão, dentro da mesma transação. `CreatePolicyValueVersionExecutor` foi estendido para publicar através do outbox durável (`IDbContextOutbox<ConfigurationDbContext>`, com `MessageContext.OverrideStorage` para o Ancillary Store correto — mirrors `ReservationsOutboxTransactionExecutor` exatamente, incluindo a ausência de um `transaction.CommitAsync()` explícito: `SaveChangesAndFlushMessagesAsync` já comita a transação ambiente). Configuration tem apenas um comando de escrita neste incremento, então a lógica de outbox vive diretamente no executor existente, sem uma segunda camada de abstração só para compartilhamento que nunca ocorreria (diferente de Reservations/PropertyManagement, que têm vários comandos de escrita compartilhando um executor genérico).

### 11.2 Roteamento RabbitMQ

`IHostPro.Api/Program.cs`: `RouteConfigurationEvent<PolicyUpdated>("policy_updated")`, mesmo padrão de `RouteReservationEvent`/`RoutePropertyManagementEvent`, exchange `configuration-events` (já declarada desde o Checkpoint 1 pelo `IHostPro.MigrationRunner`, sem uso real até agora).

### 11.3 Cache Redis (`Configuration.Infrastructure/Caching`)

Reaproveita a mesma tecnologia já usada por `RedisSessionRevocationCache` (Identity) — §6: "usar Redis já existente... não criar uma segunda tecnologia de cache" — mas com uma conexão própria (`Configuration:PolicyCache`), nunca compartilhando a `IConnectionMultiplexer` de Identity entre contextos (mesmo princípio de isolamento já aplicado a cada `DbContext` contra o mesmo Postgres físico).

Chave: `ihostpro:{tenantId:N}:policy-cache:{policyCode}:{generation}:{propertyId:N|"_"}`. Invalidação **geracional**: `InvalidateAsync(tenantId, policyCode)` executa um único `INCR` numa chave de geração separada — nenhuma entrada antiga precisa ser localizada/apagada (SCAN/KEYS nunca são usados); toda chave futura para esse (tenant, policyCode) automaticamente aponta para a nova geração, e as entradas da geração anterior simplesmente nunca são mais endereçadas, expirando pelo próprio TTL. Deliberadamente **por (tenantId, policyCode), nunca por escopo exato** — uma alteração no nível Tenant pode afetar a resolução efetiva de qualquer Imóvel que não tenha override próprio, e essas entradas de cache por Imóvel não podem ser enumeradas individualmente; invalidar o par inteiro é a única forma correta de nunca deixar uma leitura indiretamente afetada permanecer stale.

Resolved e NotConfigured são cacheados distintamente (`PolicyValueResolution.Found`), como o §6 exige. Fail-closed com degradação para PostgreSQL (nunca para um valor inventado): toda operação de leitura/escrita do cache é envolvida num catch amplo — mas, diferente de `RedisSessionRevocationCache` (que degrada para um valor de negócio seguro, "não revogado"), aqui não existe valor seguro para supor (decisão oficial 4 proíbe explicitamente um valor otimista/hardcoded), então a degradação é sempre "nada em cache" — quem chama (`CachedPolicyValueResolver`) cai para o PostgreSQL, que permanece autoritativo mesmo com o Redis fora do ar. `InvalidateAsync` é a única operação que **não** engole falhas — deixa a exceção propagar para o próprio mecanismo de retry/circuit-breaker do Wolverine, com o TTL configurável como teto final de obsolescência.

`IPolicyValueCache` (leitura/escrita, usa `PolicyValueResolution`) permanece `internal`, como `IPolicyValueResolver`. `IPolicyCacheInvalidator` (só `InvalidateAsync`) é público — o único motivo é que seu consumidor real, `PolicyUpdatedCacheInvalidationHandler`, é construído pelo container de DI do `IHostPro.Worker` (outro assembly) e precisa ser uma classe pública; um construtor público não pode ter parâmetro menos acessível que o próprio tipo.

### 11.4 `CachedPolicyValueResolver` — decorator

Envolve o `PolicyValueResolver` real (só banco) com o cache — registrado como o `IPolicyValueResolver` público via uma segunda registração **keyed** do mesmo `PolicyValueResolver` (`AddKeyedScoped<IPolicyValueResolver, PolicyValueResolver>("uncached")`), para que o decorator dependa da interface (testável com fake) em vez do tipo concreto, sem ciclo de DI. Cache miss (por qualquer motivo, incluindo falha do cache) sempre cai para o resolvedor real; uma falha real de banco continua propagando e virando `PolicyEngineUnavailableException` exatamente como antes deste checkpoint — o decorator nunca muda esse contrato, só adiciona um caminho mais rápido quando a resposta já é conhecida.

### 11.5 Consumidor em `IHostPro.Worker`

Primeira vez neste repositório que um handler de mensagem real é adicionado — `IHostPro.Worker` só tinha a infraestrutura (RabbitMQ com `listen: true`, `TenantResolutionMiddleware`) desde o Incremento 1 de Identity, nunca um handler de negócio.

`PolicyUpdatedCacheInvalidationHandler` (público, `Configuration.Infrastructure/Messaging`) implementa `IIntegrationEventHandler<PolicyUpdated>` (Architecture Principles §11) — nenhuma referência a Wolverine. `PolicyUpdatedHandler` (classe estática) é o adaptador mecânico que o Wolverine descobre por convenção (nome terminado em `Handler`, método público `Handle`, parâmetros adicionais resolvidos via injeção de método) e que apenas delega. `IHostPro.Worker/Program.cs` precisou de três coisas que nenhum handler anterior exigiu: `opts.Discovery.IncludeAssembly(typeof(PolicyUpdatedHandler).Assembly)` (Wolverine só varre o assembly de entrada por padrão); `opts.Publish(x => { x.Message<PolicyUpdated>(); x.ToRabbitTopics("configuration-events", ex => ex.BindTopic("policy_updated").ToQueue("configuration.policy-updated")); })` (a forma documentada do Wolverine de vincular uma fila a um exchange topic para consumo, confirmada em código-fonte oficial do projeto, não apenas na documentação prosaica); e a referência de projeto a `Configuration.Infrastructure` no `.csproj` do Worker. `AddConfigurationPolicyCache` é chamado diretamente (sem `AddConfigurationModule` completo — o Worker nunca toca `ConfigurationDbContext`, só invalida cache).

### 11.6 Testes unitários adicionados (`IHostPro.Contexts.Configuration.Tests.Unit`, +5 testes, total 76)

`CachedPolicyValueResolverTests.cs`, com fakes escritos à mão para `IPolicyValueResolver`/`IPolicyValueCache`: cache hit nunca chama o resolvedor interno; cache miss cai para o resolvedor e popula o cache; `NotConfigured` é cacheado e devolvido tal qual `Resolved`; falha do resolvedor interno (equivalente a uma falha de banco) continua propagando sem alteração. Uma falha do PRÓPRIO cache não é simulada aqui via fake-que-lança-exceção: o contrato de `IPolicyValueCache` exige que implementações nunca lancem, então um fake que viola esse contrato só provaria que o decorator tolera uma implementação com bug, não uma propriedade real do sistema — essa degradação é responsabilidade de `RedisPolicyValueCache`, verificada contra um Redis genuinamente inalcançável nos testes de integração (§11.7). Exige `InternalsVisibleTo` de `Configuration.Infrastructure` para o projeto de testes unitários (primeira vez neste contexto — prática padrão .NET para testes, não enfraquece a fronteira real entre Bounded Contexts, que é imposta pelo grafo de `ProjectReference` e pelo NetArchTest, nunca pelo `internal` do C# isoladamente).

Além disso, `CreatePolicyValueVersionCommandHandlerTests.cs` (Checkpoint 4) ganhou asserções sobre o `PolicyUpdated` enfileirado (campos corretos nos dois testes de sucesso) e um novo teste confirmando que nenhum evento é enfileirado quando o comando é rejeitado antes do executor rodar.

### 11.7 Testes de integração adicionados (`IHostPro.Contexts.Configuration.Tests.Integration`, +4 testes, total 64)

`PolicyCacheAndOutboxTests.cs` — PostgreSQL real (Testcontainers) sempre; Redis real para os dois testes de cache; RabbitMQ real (iniciado e depois **parado antes do comando rodar**, mesma técnica de `CondominiumIntegrationEventsTests` — a primeira tentativa de entrega do Wolverine é síncrona e quase imediata quando o broker está acessível, o que já havia sido documentado como uma corrida capaz de zerar a contagem de envelopes) para os dois testes de outbox:

- **Invalidação determinística**: uma resolução `NotConfigured` fica cacheada mesmo depois de uma versão ser criada (prova a baseline de que o cache realmente funciona); só após chamar `IPolicyCacheInvalidator.InvalidateAsync` explicitamente (exatamente o que o consumidor real faz, e nada mais) a próxima resolução reflete o valor novo.
- **Invalidação por (tenantId, policyCode) atinge escopos diferentes**: uma alteração no nível Tenant, feita diretamente no banco, deixa uma resolução em nível Property (que herda do Tenant, sem override próprio) stale até a mesma invalidação — prova a escolha deliberadamente grosseira de granularidade (§11.3).
- **Envelope `PolicyUpdated` só é persistido em sucesso**: comando aceito grava exatamente um envelope em `configuration_messaging.wolverine_outgoing_envelopes`; comando rejeitado (`version_conflict`) não grava nenhum. Contagem filtrada pela presença do `tenantId` no corpo do envelope (mesma técnica de `CondominiumIntegrationEventsTests.EnvelopeIsPendingAsync`) — necessário porque este incremento tem um único tipo de evento, então uma contagem só por tipo veria envelopes de outros testes da mesma suíte, que compartilham o mesmo Postgres/schema.

O consumidor real hospedado em `IHostPro.Worker`, e uma entrega genuína ponta a ponta via RabbitMQ, não são exercidos aqui — este teste chama `IPolicyCacheInvalidator.InvalidateAsync` diretamente, exatamente o que `PolicyUpdatedCacheInvalidationHandler` faz e nada além disso. Reservado para o Checkpoint 7 (mesmo raciocínio já registrado para a suíte Playwright no Checkpoint 5 — evitar uma segunda troca ad-hoc da porta RabbitMQ 5672). A tolerância a indisponibilidade do cache já está coberta pela suíte inteira de `PolicyResolutionTests` (ver §11.8).

### 11.8 Regressão real encontrada e corrigida: fixtures existentes nunca previam o cache

**Sintoma**: ao rodar `PolicyResolutionTests.cs` (Checkpoint 3, 11 testes) e `ConfigurationEndpointsTests.cs` (Checkpoint 4, 23 testes) depois de `AddConfigurationModule` passar a registrar o cache, todos os testes de `PolicyResolutionTests` falhavam com `Unable to resolve service for type 'ILogger<RedisPolicyValueCache>'` — o `ServiceCollection` manual dessa fixture nunca chamava `AddLogging()`.

**Correção**: `services.AddLogging()` adicionado à fixture; `Configuration:PolicyCache:ConnectionString` configurado para um endereço sintaticamente válido mas propositalmente inalcançável (`localhost:1`) em ambas as fixtures — essas duas suítes são sobre resolução/API administrativa, não sobre cache (que é `PolicyCacheAndOutboxTests`, §11.7), então um Redis permanentemente indisponível é deliberado: cada uma das 34 resoluções nessas duas suítes agora também confirma, incidentalmente mas de forma real, que uma indisponibilidade permanente do cache nunca impede uma leitura correta contra o PostgreSQL — o mesmo tipo de cobertura de outage que §12 do plano original pede, sem precisar de um Redis Testcontainer adicional nessas duas fixtures.

Nenhuma alteração de código de produção foi necessária — inteiramente uma lacuna das próprias fixtures de teste, que nunca antecipavam uma dependência de cache até este checkpoint introduzi-la.

### 11.9 Fora do escopo (confirmado não implementado)

Nenhuma `ConfigurationDefinition`/`ConfigurationValue` (decisão 6: adiado, sem catálogo aprovado); nenhum `ConfigurationUpdated` (aprovado na decisão 5 para incremento futuro, mas sem payload/roteamento definidos — não registrado no Documento 07 além de uma menção); nenhum consumidor em Reservations ou qualquer outro Bounded Context (§10 da instrução: "não criar consumidor em Reservations neste incremento").

## 12. Status por checkpoint

| Checkpoint | Status |
|---|---|
| 1. Fundação | Concluído — projetos, referências, registro no Host, MigrationRunner, schemas, permissões, documentação inicial e testes de arquitetura |
| 2. Domínio e persistência | Concluído — `PolicyDefinition`/`PolicyValue`/`PolicyAuditEntry`/`GlobalPolicyValue`; migração `InitialCreate` (RLS, grants, índice parcial); seed do catálogo sem valores padrão; 35 testes unitários + 26 testes de integração/fundação, todos aprovados contra PostgreSQL real |
| 3. Resolução e contrato | Concluído — resolvedor genérico interno; dois leitores típados públicos; distinção fail-closed `NotConfigured`/indisponibilidade via `PolicyEngineUnavailableException`; sem transação cruzada; 46 testes unitários + 37 testes de integração, todos aprovados |
| 4. API administrativa | Concluído — cinco endpoints típados; sete `ProblemDetails` distintos; validação técnica completa do catálogo; concorrência otimista com `version_conflict`; permissões `POLICIES:READ`/`POLICIES:MANAGE` finalmente consumidas (e sua lacuna de registro corrigida); cliente NSwag regenerado e determinístico; 71 testes unitários + 60 testes de integração, todos aprovados |
| 5. Frontend | Concluído — rota/nav com gate OR reconciliado e sinalizado; serviço, classificador de erro, listagem, diálogo de detalhe/histórico/nova versão típada por código; i18n completa; 279/279 testes Vitest (40 novos) + build de produção limpo; 7 testes Playwright E2E escritos e compilando, execução real adiada para o Checkpoint 7 (mesma restrição de porta RabbitMQ já documentada em §9.8) |
| 6. Cache e eventos | Concluído — `PolicyUpdated` publicado via outbox durável após commit; cache Redis geracional com invalidação imediata e fail-closed para PostgreSQL; primeiro consumidor de mensagem real do repositório, hospedado em `IHostPro.Worker`; 76/76 testes unitários (+5) + 64/64 testes de integração (+4, incluindo Redis e RabbitMQ reais); execução real do consumidor via RabbitMQ vivo adiada para o Checkpoint 7 |
| 7. Homologação | **Concluído** — swap controlado RabbitMQ+Redis (homolog→dev) executado e revertido; `MigrationRunner` re-executado com sucesso (idempotente); suíte completa de backend (1.643 testes) 100% aprovada; nove defeitos reais encontrados e corrigidos durante a primeira execução de ponta a ponta (§13.7-§13.15); *round-trip* real comprovado (versão 5 em cache → escrita da versão 6 → `PolicyUpdated` via RabbitMQ real → Worker real invalida o cache Redis real → leitura seguinte reflete a versão 6); 7/7 testes Playwright de Policies aprovados; duas execuções consecutivas da suíte E2E completa (54/54 cada, zero recursos órfãos); *benchmark* oficial da decisão 7: p95 = 36,41 ms (meta ≤ 50 ms, atingida); build Release limpo; ambiente de homologação restaurado ao estado original |

## 13. Defeitos reais encontrados e corrigidos durante a homologação

### 13.1 Falso positivo em `ConfigurationSourceConventionTests` por comentários de documentação (Checkpoint 1)

**Sintoma**: `No_source_file_implements_reservations_functional_integration_yet` falhava, apontando `IConfigurationRequestDispatcher.cs`, `ConfigurationDbContext.cs` e `ConfigurationDbContextFactory.cs` como violadores.

**Causa**: o teste verifica a presença literal dos fragmentos `"ReservationsDbContext"`/`"IReservationReader"`/`"IReservationsRequestDispatcher"` em qualquer arquivo-fonte de Configuration, para impedir reimplementação prematura de conceitos de Reservations (mesmo princípio já aplicado em `ReservationsSourceConventionTests.cs` para `EarlyCheckIn`/`LateCheckout`). Os três arquivos citavam esses nomes de classe em comentários explicativos ("mirrors `ReservationsDbContext` exactly"), não em código funcional — falso positivo, não uma violação real.

**Correção**: comentários reformulados para descrever o padrão de design sem repetir o literal proibido (ex.: "mirrors the same pattern already used by every other Bounded Context's own DbContext exactly"). Nenhuma alteração de comportamento; apenas texto de documentação. Arquivos: `IConfigurationRequestDispatcher.cs`, `ConfigurationDbContext.cs`, `ConfigurationDbContextFactory.cs`.

Nenhum defeito adicional foi encontrado durante o Checkpoint 2 — todas as 61 novas verificações (35 unitárias + 26 de integração) passaram na primeira execução após a correção de um `using` ausente no próprio arquivo de teste de integração (erro de compilação, não um defeito de comportamento).

### 13.2 Ajustes de configuração do próprio teste durante o Checkpoint 3 (não defeitos de produção)

Ao escrever `PolicyResolutionTests.cs`, três problemas de configuração do teste em si (nunca do código de produção) precisaram de correção antes de todos os 11 novos testes passarem:

1. **Papéis `ihostpro_app`/`ihostpro_migrator` ausentes**: a migração `InitialCreate` referencia ambos incondicionalmente (`GRANT`/`ALTER DEFAULT PRIVILEGES`) — corrigido criando os dois papéis no `Fixture.InitializeAsync`, mesmo essa suíte conectando como o superusuário do container para tudo (diferentemente de `ConfigurationFoundationTests`, que testa fronteiras de privilégio; esta suíte testa apenas a resolução).
2. **Poluição de estado entre testes via `global_policy_values`**: essa tabela não tem fronteira de tenant (ao contrário de `policy_values`), então múltiplos testes semeando o mesmo `policy_code` colidiam (`duplicate key`) ou vazavam dados de um teste para o próximo (`Cross_tenant_data_is_never_resolved` via um valor GLOBAL deixado por um teste anterior). Corrigido implementando `IAsyncLifetime` na própria classe de teste, truncando `policy_values`/`policy_audit_log`/`global_policy_values` (nunca `policy_definitions`, o catálogo seedado) antes de cada teste.
3. **Filtro de consulta global do EF não aplicado**: o `ITenantContext` injetado no escopo de DI usado para resolver `IEarlyCheckInPolicyReader`/`ILateCheckoutPolicyReader` nunca era definido para o tenant do teste — em produção, `TenantResolutionMiddleware` faz isso antes de qualquer handler/leitor rodar; sem um equivalente no teste, o Global Query Filter do EF (independente da política RLS que o resolvedor já configura corretamente via sua própria transação) filtrava todas as linhas. Corrigido resolvendo `ITenantContext` do mesmo escopo de DI e chamando `SetTenant(tenantId)` antes de invocar o leitor, em todo teste que resolve um leitor via DI.

Nenhuma alteração de código de produção foi necessária para nenhum dos três pontos — são inteiramente particularidades da própria composição do teste.

### 13.3 Políticas de autorização `POLICIES:READ`/`POLICIES:MANAGE` nunca registradas (Checkpoint 4)

**Sintoma**: `[Authorize(Policy = IdentityPermissionCodes.PoliciesRead)]`/`[Authorize(Policy = IdentityPermissionCodes.PoliciesManage)]` referenciavam nomes de política ASP.NET Core que nunca haviam sido efetivamente registrados — toda chamada a esses endpoints teria falhado com `InvalidOperationException: The AuthorizationPolicy named 'POLICIES:READ' was not found` em tempo de execução.

**Causa**: `IdentityAuthorizationExtensions.AddIdentityAuthorization()` registra deliberadamente apenas as políticas "efetivamente consumidas por um endpoint existente" (seu próprio comentário de design, Incremento 3) — `POLICIES:READ`/`POLICIES:MANAGE` nunca haviam sido adicionadas porque nenhum endpoint as consumia antes deste checkpoint. Não é um defeito de nenhum checkpoint anterior — é exatamente o próximo passo que o próprio comentário do arquivo já previa.

**Correção**: adicionadas as duas chamadas `.AddPolicy(...)` faltantes em `IdentityAuthorizationExtensions.cs`, no mesmo padrão de `PropertiesManage`/`ReservationsManage`. Confirmado via os 60 testes de integração HTTP reais deste checkpoint, incluindo os cenários de 403 para papéis sem a permissão correta.

### 13.4 `MvcApplicationPartsAssemblyInfo.cs` obsoleto — `PoliciesController` ausente do roteamento real (Checkpoint 4)

**Sintoma**: com a solução inteira compilando sem erros/avisos e o `deps.json` de `IHostPro.Api` corretamente listando `IHostPro.Contexts.Configuration.Api`, a API real, ao subir, retornava `404` (nunca `401`/`403`) para `GET /api/v1/policies`, e as quatro rotas de `/api/v1/policies` estavam ausentes do `swagger.json` real — apenas descoberto ao tentar regenerar o cliente NSwag (§9.8), já que nenhum teste automatizado deste incremento sobe o Host real completo via `dotnet run` (os testes de integração hospedam o pipeline via `TestServer`/`ConfigureServices` explícito, nunca via `AddApplicationPart` implícito do `dotnet run`).

**Causa**: o descobrimento padrão de controllers do ASP.NET Core (`AddControllers()`) depende, em tempo de execução, dos atributos `[assembly: ApplicationPartAttribute("...")]` gravados no assembly de entrada — um arquivo gerado automaticamente pelo SDK (`obj/Debug/net10.0/IHostPro.Api.MvcApplicationPartsAssemblyInfo.cs`) a partir do grafo de `ProjectReference` no momento da compilação. A build incremental do MSBuild não recalculou esse arquivo específico quando a referência a `IHostPro.Contexts.Configuration.Api` foi adicionada ao `.csproj` do Host nos checkpoints anteriores — o arquivo permaneceu com a lista antiga (Identity/PropertyManagement/Reservations + Swashbuckle), mesmo com todo o resto da solução compilando corretamente e o `deps.json` (usado por outros mecanismos, não pela descoberta de controllers) já correto. Nenhum erro, aviso ou log seria gerado por esse desalinhamento — o controller simplesmente não aparece no pipeline de roteamento, silenciosamente.

**Correção**: limpeza e recompilação completa (`rm -rf src/Host/IHostPro.Api/obj src/Host/IHostPro.Api/bin` seguido de `dotnet build`) — o arquivo gerado passou a listar corretamente as cinco `ApplicationPartAttribute`, incluindo `IHostPro.Contexts.Configuration.Api`. Confirmado por HTTP real: `GET /api/v1/policies` sem token passou a retornar `401` (não mais `404`), e as quatro rotas apareceram no `swagger.json`.

**Risco para checkpoints/incrementos futuros**: qualquer novo projeto `.Api` referenciado pelo Host exige uma build LIMPA (não incremental) antes de confiar em testes manuais/exploratórios contra o Host real subido via `dotnet run` — os testes automatizados deste repositório (que hospedam o pipeline programaticamente) não são afetados por essa classe de problema, apenas a execução real do processo `IHostPro.Api`. Registrado como risco/lição aprendida em §16.

### 13.5 `WebE2EFixture.cs` nunca provisionava o schema Configuration (Checkpoint 5)

Ver §10.8 — lacuna pré-existente no *fixture* de E2E (nunca migrava/provisionava Configuration), corrigida neste checkpoint mesmo sem a suíte ter sido executada de fato (correção confirmada apenas por compilação limpa; validação em tempo de execução fica para o Checkpoint 7, junto com a primeira execução real de toda a suíte de Policies).

### 13.6 `PolicyResolutionTests`/`ConfigurationEndpointsTests` nunca previam a dependência de cache (Checkpoint 6)

Ver §11.8 — as duas fixtures de teste dos Checkpoints 3/4 quebraram assim que `AddConfigurationModule` passou a registrar o cache (`ILogger<RedisPolicyValueCache>` não resolvia, por ausência de `AddLogging()`) — corrigido, e a configuração resultante (Redis deliberadamente inalcançável nessas duas suítes) passou a cobrir, incidentalmente, a tolerância a indisponibilidade permanente do cache em 34 resoluções reais.

### 13.7 `IHostPro.Worker` nunca escutava de fato `configuration.policy-updated` (Checkpoint 7)

**Sintoma**: com a suíte automatizada inteira aprovada (incluindo os 64 testes de integração de Configuration, que exercitam RabbitMQ real via Testcontainers) e o Worker subindo sem nenhum erro visível, a primeira execução real de ponta a ponta — API real escrevendo uma nova versão de política, Worker real processando a mensagem — nunca invalidava o cache. Inspeção direta via a API de gerenciamento do RabbitMQ (`GET /api/queues/%2F/configuration.policy-updated`) mostrou que a fila **nunca havia sido criada**.

**Causa**: `IHostPro.Worker/Program.cs` declarava `opts.Publish(x => { x.Message<PolicyUpdated>(); x.ToRabbitTopics("configuration-events", exchange => exchange.BindTopic("policy_updated").ToQueue("configuration.policy-updated")); })` — uma regra do lado **publicador** (como uma mensagem deveria ser roteada quando este processo a *envia*), não do lado ouvinte. Confirmado contra a documentação oficial do Wolverine e por observação direta: uma regra `Publish` nunca declara fila nem faz um processo escutar algo, mesmo quando usa `BindTopic(...).ToQueue(...)` — nenhum dos 64 testes de integração existentes exercitava esse caminho real, pois todos chamam `IPolicyCacheInvalidator.InvalidateAsync` diretamente, exatamente o que o consumidor real faria, sem nunca passar pela descoberta/roteamento de fila do Wolverine.

**Correção**: a fila `configuration.policy-updated` e seu *binding* (`policy_updated`) para a exchange `configuration-events` passaram a ser provisionados exclusivamente por `IHostPro.MigrationRunner` (a mesma autoridade única de provisionamento já usada para todo o resto da topologia de mensageria desta plataforma, via `DeclareExchange(...).BindQueue(...)`); `IHostPro.Worker` agora só chama `opts.ListenToRabbitQueue("configuration.policy-updated")` para anexar um consumidor à fila já existente. Confirmado por observação direta: `MigrationRunner` re-executado logou "Declared Rabbit MQ queue 'configuration.policy-updated'" e "Declared a Rabbit Mq binding 'policy_updated' from exchange configuration-events to configuration.policy-updated"; a API de gerenciamento do RabbitMQ passou a mostrar a fila com um consumidor ativo (`"consumer_tag":"Wolverine"`).

### 13.8 `TenantResolutionMiddleware` baseado no tipo abstrato `IntegrationEvent` não era resolvível pelo codegen do Wolverine (Checkpoint 7)

**Sintoma**: após corrigir §13.7, o Worker passou a receber a mensagem real, mas falhava com `JasperFx.CodeGeneration.UnResolvableVariableException: JasperFx was unable to resolve a variable of type IHostPro.BuildingBlocks.Messaging.Abstractions.IntegrationEvent` ao gerar o código do handler.

**Causa**: `TenantResolutionMiddleware.Before(IntegrationEvent message, ITenantContext tenantContext)` — registrado globalmente via `opts.Policies.AddMiddleware(typeof(TenantResolutionMiddleware), chain => typeof(IntegrationEvent).IsAssignableFrom(chain.MessageType))` — tomava o tipo-base abstrato como parâmetro. O gerador de código do Wolverine rastreia a variável de mensagem de uma *chain* pelo seu tipo concreto declarado (`PolicyUpdated`, neste caso) e nunca faz upcast automático para satisfazer um parâmetro de tipo-base — esta era a primeira vez que este middleware, existente desde etapas anteriores da plataforma, era de fato exercitado ponta a ponta contra um consumidor real.

**Correção**: tentativa intermediária registrada em §13.9. Correção final: o parâmetro deixou de depender de correspondência por tipo de mensagem — `Before` passou a receber `Wolverine.Runtime.MessageContext context` (sempre resolvível pelo Wolverine, independente do tipo de mensagem, confirmado diretamente nos próprios stack traces desta e da falha em §13.9) e lê a mensagem de `context.Envelope.Message`, convertendo-a para `IntegrationEvent` dentro do próprio método. Confirmado por observação direta: o Worker deixou de lançar `UnResolvableVariableException` e passou a processar a mensagem real.

### 13.9 Tentativa de generalizar `TenantResolutionMiddleware` via método genérico aberto também falhou (Checkpoint 7)

**Sintoma**: a primeira tentativa de correção de §13.8 trocou o parâmetro de `IntegrationEvent` por um método genérico `Before<TMessage>(TMessage message, ITenantContext tenantContext) where TMessage : IntegrationEvent` — abordagem documentada oficialmente pelo Wolverine para middleware polimórfico. Mesmo assim, o Worker continuou falhando com a mesma exceção, agora apontando para o nome literal do parâmetro de tipo não vinculado: `unable to resolve a variable of type IHostPro.BuildingBlocks.Infrastructure.Multitenancy.TMessage`.

**Causa**: confirmado por observação direta (a documentação oficial do Wolverine não cobre este caso específico) que o mecanismo usado para registrar este middleware — `opts.Policies.AddMiddleware(Type, Func<HandlerChain,bool>)`, uma política reflexiva de baixo nível — não fecha (`MakeGenericMethod`) métodos genéricos abertos sobre o tipo concreto de cada *chain*; a sugestão de middleware genérico da documentação aparentemente pressupõe um caminho de descoberta diferente, não este.

**Correção**: substituída pela abordagem definitiva registrada em §13.8 (parâmetro `MessageContext`, sem depender de correspondência de tipo genérico ou de herança). Este item permanece registrado separadamente, mesmo sendo parte da mesma jornada de correção de §13.8, porque representa uma segunda tentativa real e distinta que também falhou, e não deve ser confundida com instabilidade do teste (*flakiness*) — foi uma limitação real e reproduzível do mecanismo de registro de middleware usado.

### 13.10 `RedisPolicyValueCache` `internal` impedia a construção direta pelo codegen do Worker (Checkpoint 7)

**Sintoma**: após corrigir §13.8/§13.9, o Worker avançou até uma nova falha: `Wolverine.Configuration.InvalidServiceLocationException: Found service locations while generating code for Message Handler for PolicyUpdated, but ServiceLocationPolicy.NotAllowed is in effect... Concrete type RedisPolicyValueCache is not public, so requires service location`.

**Causa**: `RedisPolicyValueCache` era `internal` desde o Checkpoint 6, espelhando a acessibilidade de `IPolicyValueCache`. Isso era invisível a todos os testes automatizados (cada um resolve `IPolicyCacheInvalidator`/`IPolicyValueCache` via DI normal, dentro de um host construído no mesmo assembly do projeto de teste, que tem acesso a tipos internos via `InternalsVisibleTo`), mas o gerador de código do Wolverine, ao montar a *handler chain* real em `IHostPro.Worker` (um assembly diferente), recusa-se a construir inline (`new RedisPolicyValueCache(...)`) um tipo concreto não público — a primeira vez que essa cadeia de dependência real foi exercitada de ponta a ponta.

**Correção**: `RedisPolicyValueCache` passou a ser `public`. Como `IPolicyValueCache` permanece `internal` e seus dois membros (`TryGetAsync`/`SetAsync`) usam `PolicyValueResolution` (também `internal`) em suas assinaturas, esses dois métodos passaram a ser implementações explícitas de interface (`Task<PolicyValueResolution?> IPolicyValueCache.TryGetAsync(...)`) — nunca parte da superfície pública da classe, evitando o erro de acessibilidade inconsistente (CS0051) que uma assinatura pública com tipo de retorno `internal` produziria. `IPolicyCacheInvalidator.InvalidateAsync` permanece um método público normal, sem alteração. Confirmado por observação direta: o Worker deixou de lançar `InvalidServiceLocationException` e passou a construir `RedisPolicyValueCache` diretamente no código gerado.

### 13.11 `PolicyUpdatedCacheInvalidationHandler` era descoberto duas vezes pela convenção do Wolverine (Checkpoint 7)

**Sintoma**: após corrigir §13.10, o Worker falhou uma última vez com um erro de compilação no código gerado: `CS0128: Uma variável de local ou função denominada 'policyUpdatedCacheInvalidationHandler' já está definida neste escopo`.

**Causa**: `PolicyUpdatedCacheInvalidationHandler` (a classe de negócio que implementa `IIntegrationEventHandler<PolicyUpdated>`) tinha, por coincidência, um nome terminado em "Handler" e um método público `HandleAsync` — exatamente o padrão de convenção que o próprio Wolverine usa para descobrir handlers automaticamente (`opts.Discovery.IncludeAssembly`). O Wolverine descobriu essa classe como um handler de `PolicyUpdated` por conta própria, **além** de descobrir o adaptador mecânico pretendido (`PolicyUpdatedHandler.Handle`), e fundiu ambas as *chains* em um único método gerado — produzindo duas declarações conflitantes da mesma variável local. Nenhum teste automatizado detectava isso, pois nenhum exercitava `opts.Discovery.IncludeAssembly` contra este assembly com uma mensagem real antes desta homologação.

**Correção**: a classe foi renomeada para `PolicyUpdatedCacheInvalidation` (removendo o sufixo "Handler", que é o único gatilho da convenção de descoberta do Wolverine por nome de classe) — nenhuma outra alteração de comportamento. `PolicyUpdatedHandler` permanece o único ponto que o Wolverine descobre para `PolicyUpdated`, exatamente a intenção original de design. Confirmado por observação direta: o código gerado passou a compilar sem erros, e o *round-trip* real (versão 5 em cache → `PolicyUpdated` publicado → Worker consome → contador de geração avança de 0 para 1 → `GET /effective` retorna a versão 6) foi comprovado de ponta a ponta pela primeira vez.

**Risco/lição para consumidores futuros de eventos**: qualquer futura classe de negócio que implemente `IIntegrationEventHandler<TEvent>` deve evitar terminar seu próprio nome em "Handler" caso também termine com um método público iniciado por "Handle"/"Consume" — do contrário corre o mesmo risco de dupla descoberta pela convenção do Wolverine. Nenhum mecanismo automático (teste de arquitetura) impede essa colisão hoje; registrado como débito técnico em §16.

### 13.12 Corrida real entre a invalidação assíncrona do cache e a releitura imediata da UI após salvar (Checkpoint 7)

**Sintoma**: com os cinco defeitos de §13.7-§13.11 corrigidos e o *round-trip* manual comprovado, a primeira execução real dos testes Playwright de Policies (`PoliciesE2ETests`, com `IHostPro.Worker` agora também rodando dentro de `WebE2EFixture` — ver §13.13) revelou uma falha adicional: `Admin_creates_a_Property_scoped_version_independent_of_the_Tenant_scope` expirava (30s) esperando "Definido neste nível." aparecer logo após escrever um valor no escopo Property.

**Causa**: `PolicyDetailDialog.submitNewVersion()` chama `this.load()` (uma nova leitura de `getEffective`/`getValueAtScope`/`getHistory`) imediatamente ao receber a resposta `201` do POST. A resposta HTTP só garante que o commit no PostgreSQL e o enfileiramento no outbox aconteceram — não que a invalidação assíncrona (outbox → RabbitMQ → `IHostPro.Worker` → Redis) já tenha sido concluída. Confirmado por observação direta via HTTP puro (reprodução isolada, fora do Playwright): a mesma sequência (leitura fria → escrita → releitura imediata) funciona corretamente quando medida com folga de tempo, mas a UI nunca dá esse tempo — e como nada no frontend refaz a busca automaticamente depois da primeira, uma corrida perdida deixa a tela mostrando um valor efetivo desatualizado indefinidamente, não apenas por um instante.

**Correção**: `CreatePolicyValueVersionExecutor` (Infrastructure) passou a chamar `IPolicyCacheInvalidator.InvalidateAsync` diretamente, de forma síncrona, na mesma requisição que executa a escrita — logo após `SaveChangesAndFlushMessagesAsync`, além de (nunca em vez de) publicar `PolicyUpdated` para o outbox, que continua existindo para qualquer futuro consumidor externo a este contexto. Elimina a corrida na origem, sem depender de tempo de RabbitMQ/Worker. Um regressão real foi encontrada e corrigida ao implementar isto pela primeira vez: `IPolicyCacheInvalidator.InvalidateAsync` deliberadamente nunca engole falhas (para permitir que o retry/circuit-breaker do próprio Wolverine trate uma falha quando chamado de dentro de `PolicyUpdatedCacheInvalidation`) — chamado de forma síncrona dentro da requisição HTTP, sem proteção, uma falha genuína do Redis (`ConfigurationEndpointsTests` usa deliberadamente `localhost:1`, um Redis inalcançável, para provar tolerância a indisponibilidade — ver §13.6) transformava-se em `500` para uma escrita que, na verdade, já havia sido concluída com sucesso no PostgreSQL. Corrigido envolvendo apenas essa chamada em um `try/catch` que registra a falha e segue adiante — o sucesso da escrita nunca fica condicionado a essa otimização.

### 13.13 `WebE2EFixture` nunca rodava um `IHostPro.Worker` real — Redis nunca era efetivamente exercitado pela suíte de Policies (Checkpoint 7)

**Sintoma**: ao investigar §13.12, ficou claro que nenhum dos testes Playwright de Policies jamais dependia de fato de uma invalidação de cache correta, porque `WebE2EFixture` só sobe `IHostPro.Api` e o `ng serve` — nunca um `IHostPro.Worker` real, apesar de já usar RabbitMQ e Redis reais (Testcontainers) para o próprio `IHostPro.Api`.

**Correção**: `WebE2EFixture` passou a também subir um `IHostPro.Worker` real (`StartWorkerProcess`, mesmo padrão de subprocesso já usado para `IHostPro.Api`), compartilhando a mesma string de conexão RabbitMQ/Redis. Isso exigiu declarar a fila `configuration.policy-updated` e seu *binding* na própria provisão de topologia RabbitMQ do fixture (`ProvisionRabbitMqTopologyAsync`), que também nunca declarava a exchange `configuration-events` (a mesma lacuna já corrigida em `IHostPro.MigrationRunner`, §13.7) — sem isso, `IHostPro.Api` publicaria em uma exchange inexistente, e o novo `IHostPro.Worker` do fixture nunca teria uma fila para escutar. Com a invalidação síncrona de §13.12, o Worker real deste fixture deixa de ser estritamente necessário para a corrida específica do §13.12, mas permanece — cobrindo genuinamente RabbitMQ e Redis reais, incluindo qualquer consumidor futuro, exatamente como a instrução original pedia ("RabbitMQ real; Redis real").

### 13.14 Testes de Policies assumiam isolamento entre métodos que o fixture compartilhado não garante (Checkpoint 7)

**Sintoma**: mesmo após §13.12, dois testes de `PoliciesE2ETests` ainda falhavam de forma intermitente ao rodar a classe inteira (nunca isoladamente): `Admin_creates_a_Property_scoped_version_independent_of_the_Tenant_scope` esperando por "Versão 1" literal, e `Admin_sees_two_versions_in_history_after_editing_a_configured_value` esperando exatamente 2 linhas de histórico.

**Causa**: `PoliciesE2ETests` compartilha um único tenant/fixture (`WebE2EFixture`) entre todos os seus métodos, e três deles escrevem versões de `EARLY_CHECKIN` no escopo Tenant — sem ordem de execução garantida pelo xUnit. Um teste que já tivesse avançado a versão do Tenant (ou adicionado linhas ao histórico) antes de outro rodar invalidava qualquer suposição de número de versão absoluto ou contagem exata de linhas — a primeira vez que esta suíte realmente rodou de ponta a ponta.

**Correção**: as asserções passaram a depender do texto do motivo (único por teste) em vez de números de versão absolutos, e a verificação de histórico passou a checar cada linha especificamente (pelo motivo) e qual delas está marcada "✓" (atual) em vez de contar linhas — nenhuma das duas formas depende de quantas outras linhas outro teste possa ter adicionado. Uma tentativa intermediária (contagem por delta, capturando uma contagem "antes" e exigindo `antes + 2` depois) ainda falhava intermitentemente sob carga da suíte completa — rastreado até §13.15.

### 13.15 `LoadTenantScopeAsync`/`LoadPropertyScopeAsync` nunca esperavam o carregamento terminar antes de devolver o controle (Checkpoint 7)

**Sintoma**: a tentativa de contagem por delta em §13.14 falhava de forma intermitente mesmo isolada, sempre com a contagem "antes" lida como zero.

**Causa**: os *helpers* que clicam em "Carregar" retornavam assim que o clique era disparado, nunca esperando `PolicyDetailDialog.load()` (uma chamada HTTP assíncrona) terminar. `Locator.CountAsync()` do Playwright nunca espera automaticamente por nada — ao contrário de `WaitForAsync` em texto/elemento — então uma contagem de linhas feita logo após "Carregar" podia correr contra a busca ainda em andamento e ver a tabela vazia.

**Correção**: os dois *helpers* passaram a esperar o *spinner* (`mat-progress-spinner`, visível enquanto `PolicyDetailDialog.loading()` é verdadeiro) desaparecer do DOM antes de retornar — corrige a causa na raiz para qualquer chamador atual ou futuro desses *helpers*, não apenas o teste que primeiro dependeu da contagem. Combinado com a correção definitiva de §13.14 (que elimina a necessidade de qualquer contagem/baseline), a suíte de 7 testes de Policies passou a ser reproduzivelmente verde.

## 14. Testes

| Suíte | Resultado final (Checkpoint 7) |
|---|---|
| Arquitetura (`IHostPro.ArchitectureTests`, solução completa) | 131/131 aprovados |
| Unitários de Identity | 470/470 aprovados |
| Integração de Identity | 419/419 aprovados |
| Unitários de BuildingBlocks | 13/13 aprovados |
| Unitários de PropertyManagement | 180/180 aprovados |
| Integração de PropertyManagement | 184/184 aprovados |
| Unitários de Reservations | 50/50 aprovados |
| Integração de Reservations | 52/52 aprovados |
| Unitários de Configuration | 76/76 aprovados |
| Integração de Configuration (`ConfigurationFoundationTests` + `PolicyResolutionTests` + `ConfigurationEndpointsTests` + `PolicyCacheAndOutboxTests`, PostgreSQL/Redis/RabbitMQ reais) | 65/65 aprovados (26 fundação + 11 resolução + 23 HTTP real + 4 cache/outbox + 1 *benchmark* p95) |
| `IHostPro.Api.Tests.Integration` (`WolverineThreeStoreCompositionTests`) | 4/4 aprovados |
| **Total (suíte de backend completa, executada uma vez após todas as correções desta homologação)** | **1.643/1.643 aprovados** |
| Frontend — unitários (Vitest, `ng test --watch=false`) | 279/279 aprovados (última execução, Checkpoint 5 — sem alteração de frontend neste checkpoint) |
| Frontend — build de produção (`ng build`, Checkpoint 5) / build Release da solução completa (`dotnet build -c Release`, Checkpoint 7) | 0 erros em ambas |
| Playwright E2E — `PoliciesAuthorizationE2ETests` + `PoliciesE2ETests` (7 testes) | 7/7 aprovados, reproduzível — ver §13.12-§13.15 para os quatro defeitos reais corrigidos até chegar a este resultado |
| Playwright E2E — suíte completa (`IHostPro.Web.Tests.E2E`, 54 testes: Condomínios, Imóveis, Reservas, Usuários, Políticas) — duas execuções consecutivas | 54/54 e 54/54, zero recursos órfãos em ambas (ver §12 e §16 para o histórico de tentativas) |
| *Benchmark* de resolução de política (decisão oficial 7, `EARLY_CHECKIN`, cache aquecido, 1.000 chamadas medidas, concorrência 20, build Debug) | p50 = 4,17 ms; **p95 = 36,41 ms** (meta: ≤ 50 ms — atingida); p99 = 52,45 ms; máx = 83,84 ms; *cache miss* (diagnóstico) = 142,27 ms |
| `PolicyUpdatedWolverineDiscoveryTests` (débito preventivo, §18.2, executado após a aprovação do usuário, antes do versionamento) | 1/1 aprovado — `IHostPro.Worker.dll` real via subprocesso, um único *listener* Wolverine para `configuration.policy-updated`, sem recorrência de nenhum dos cinco defeitos de §13.7-§13.11 |

Build completo da solução (`dotnet build IHostPro.sln`), Debug e Release: 0 erros, 0 avisos em ambas as configurações, confirmado ao final do Checkpoint 7. `git diff --check`: nenhum problema em qualquer arquivo criado/modificado nesta fase — os únicos apontamentos são espaços em branco pré-existentes em `api-client.ts` (arquivo gerado pelo NSwag desde o Checkpoint 4, nunca editado manualmente) e avisos informativos de conversão de fim de linha LF→CRLF, sem relação com o conteúdo.

Validação proporcional (§14 da instrução original): a suíte completa de backend (1.643 testes) foi reexecutada integralmente uma vez, após a última alteração de código de produção desta homologação (a invalidação síncrona de cache, §13.12) — não repetida novamente depois disso, já que nenhum código de produção mudou entre essa execução e o fechamento deste checkpoint. Identity/PropertyManagement/frontend não foram alterados neste checkpoint especificamente, mas foram incluídos nessa execução única porque `TenantResolutionMiddleware.cs` (BuildingBlocks.Infrastructure, compartilhado) foi modificado (§13.8/§13.9) — validação proporcional aplicada ao *conjunto de projetos afetados pela alteração*, não apenas a Configuration isoladamente.

## 15. Inventário Git

**Nota (ver §18 para o estado atualizado)**: a tabela abaixo registra o inventário de arquivos como observado ao final do Checkpoint 7, quando nenhuma operação de `git add`/`commit`/`push` havia ainda sido realizada. Após a aprovação do usuário, esses mesmos arquivos foram distribuídos em commits — §18.3 registra os hashes completos; esta tabela permanece como está, sem reescrita retroativa, por registrar corretamente o estado no momento do Checkpoint 7.

Branch `feature/configuration-policy` (criada a partir de `master` sincronizado, sem nenhum commit próprio desta fase até o momento do Checkpoint 7). Nenhuma operação de `git add`/`commit`/`push`/`tag`/`merge`/`rebase`/staging realizada até aquele ponto — todas as alterações abaixo permaneciam como working tree não staged (`git status`), conforme restrição explícita do usuário para o período de homologação.

| Categoria | Itens |
|---|---|
| Novos diretórios (não rastreados) | `src/Contexts/Configuration/` (5 projetos); `tests/Contexts/Configuration/` (2 projetos); subpastas de checkpoints anteriores (ver versões anteriores deste documento); `frontend/IHostPro.Web/src/app/features/policies/`; `src/Contexts/Configuration/IHostPro.Contexts.Configuration.Infrastructure/Caching/`; `src/Contexts/Configuration/IHostPro.Contexts.Configuration.Infrastructure/Messaging/`; `tests/Contexts/Configuration/IHostPro.Contexts.Configuration.Tests.Unit/Resolution/` |
| Novos arquivos (não rastreados) | Checkpoints 2-5 (ver versões anteriores deste documento); Checkpoint 6: `Configuration.Contracts/PolicyUpdated.cs`; `Configuration.Application/IIntegrationEventCollector.cs`; `Configuration.Infrastructure/Persistence/IntegrationEventCollector.cs`; `Configuration.Infrastructure/Caching/` (`PolicyCacheOptions.cs`, `PolicyCacheOptionsValidator.cs`, `IPolicyValueCache.cs`, `RedisPolicyValueCache.cs`, `ConfigurationPolicyCacheExtensions.cs`); `Configuration.Infrastructure/Resolution/CachedPolicyValueResolver.cs`; `Configuration.Infrastructure/Messaging/` (`PolicyUpdatedCacheInvalidation.cs` — renomeado de `PolicyUpdatedCacheInvalidationHandler.cs` no Checkpoint 7, §13.11; `PolicyUpdatedHandler.cs`); `tests/Contexts/Configuration/IHostPro.Contexts.Configuration.Tests.Unit/Resolution/` (`FakePolicyValueResolver.cs`, `FakePolicyValueCache.cs`, `CachedPolicyValueResolverTests.cs`); `tests/Contexts/Configuration/IHostPro.Contexts.Configuration.Tests.Unit/Application/Policies/FakeIntegrationEventCollector.cs`; `tests/Contexts/Configuration/IHostPro.Contexts.Configuration.Tests.Integration/PolicyCacheAndOutboxTests.cs` (Checkpoint 6, com o *benchmark* de §14 adicionado no Checkpoint 7) |
| Arquivos modificados (rastreados pelo git, ainda não commitados) | Acumulado dos checkpoints anteriores + `documentacao do projeto/Documento 07 — Catálogo de Eventos de Domínio (Domain Events Catalogue).txt` (§28, registrado antes da implementação); `Configuration.Contracts.csproj` (referência a `BuildingBlocks.Messaging.Abstractions`); `Configuration.Infrastructure.csproj` (`WolverineFx.EntityFrameworkCore` finalmente usado; `StackExchange.Redis`; `InternalsVisibleTo` para o projeto de testes unitários); `CreatePolicyValueVersionExecutor.cs` (outbox no Checkpoint 6; invalidação síncrona de cache no Checkpoint 7, §13.12); `CreatePolicyValueVersionCommandHandler.cs`; `ConfigurationCommandDispatchExtensions.cs`; `ConfigurationModuleExtensions.cs`; `PolicyValueResolver.cs` (só comentário); `src/Host/IHostPro.Api/Program.cs` (roteamento `PolicyUpdated`); `src/Host/IHostPro.Api/appsettings.json` (`Configuration:PolicyCache`); `src/Host/IHostPro.Worker/Program.cs` (Checkpoint 6: cache + consumidor + discovery + roteamento; Checkpoint 7: `opts.ListenToRabbitQueue(...)` substitui `opts.Publish(...)`, §13.7); `src/Host/IHostPro.Worker/appsettings.json` (`Configuration:PolicyCache`); `src/Host/IHostPro.Worker/IHostPro.Worker.csproj` (referência a `Configuration.Infrastructure`); `tests/Contexts/Configuration/IHostPro.Contexts.Configuration.Tests.Unit/Application/Policies/CreatePolicyValueVersionCommandHandlerTests.cs`; `tests/Contexts/Configuration/IHostPro.Contexts.Configuration.Tests.Integration/PolicyResolutionTests.cs` (§13.6); `tests/Contexts/Configuration/IHostPro.Contexts.Configuration.Tests.Integration/ConfigurationEndpointsTests.cs` (§13.6); `tests/Contexts/Configuration/IHostPro.Contexts.Configuration.Tests.Integration/IHostPro.Contexts.Configuration.Tests.Integration.csproj` (`Testcontainers.Redis`); **Checkpoint 7**: `src/BuildingBlocks/IHostPro.BuildingBlocks.Infrastructure/Multitenancy/TenantResolutionMiddleware.cs` (§13.8/§13.9); `tools/IHostPro.MigrationRunner/Program.cs` (`.BindQueue(...)` na *exchange* `configuration-events`, §13.7); `tests/Frontend/IHostPro.Web.Tests.E2E/WebE2EFixture.cs` (Worker real + *exchange* `configuration-events` + fila, §13.13); `tests/Frontend/IHostPro.Web.Tests.E2E/PoliciesE2ETests.cs` (persona dupla, asserções independentes de ordem, §13.14/§13.15); `tests/Frontend/IHostPro.Web.Tests.E2E/ManagedProcess.cs` (parâmetro opcional `onOutputLine`, retrocompatível) |
| Arquivos novos editados novamente em checkpoints subsequentes (ainda não rastreados, sem histórico git a exibir) | `tests/IHostPro.ArchitectureTests/ConfigurationSourceConventionTests.cs`; `ConfigurationDbContext.cs` |

## 16. Riscos e débitos técnicos identificados

- Lacuna pré-existente no Documento 000 (Fase 4 nunca foi adicionada à lista de "Phase Homologation Records") identificada durante o Checkpoint 1 — não corrigida naquele momento por estar fora do escopo autorizado para o incremento (ver §6.6). **Corrigida no §18** (Documento 000 atualizado para incluir tanto a Fase 4 quanto a Fase 5 na mesma edição que registra a publicação deste incremento).
- `Category = "CHECK_IN_OUT"` no seed do catálogo é uma escolha de rotulagem própria, sem taxonomia formal documentada (ver §7.3) — sinalizado para eventual decisão futura caso uma taxonomia oficial de categorias venha a ser definida.
- **Lição registrada (§13.4)**: qualquer novo projeto `.Api` referenciado pelo Host exige uma build limpa (não incremental) antes de testar manualmente o processo real via `dotnet run` — o `MvcApplicationPartsAssemblyInfo.cs` gerado pelo SDK pode ficar obsoleto silenciosamente numa build incremental. Não afeta os testes automatizados (que hospedam o pipeline programaticamente), apenas execuções manuais/exploratórias do Host real.
- A migração `InitialCreate` do Checkpoint 2 foi validada contra o Postgres de desenvolvimento persistente real (§9.7), não apenas ambientes efêmeros.
- **Resolvido no Checkpoint 7**: a pendência acumulada dos Checkpoints 5-6 (execução real do consumidor `PolicyUpdated`/`IHostPro.Worker` contra RabbitMQ genuíno, §10.8/§11.7) foi executada de ponta a ponta — ver §13.7-§13.11 para os cinco defeitos do Worker/cache, §13.12-§13.15 para os quatro defeitos adicionais encontrados ao executar os testes Playwright de Policies pela primeira vez, e §12 para a comprovação do *round-trip*. Nove defeitos reais no total, todos encontrados exclusivamente porque este checkpoint foi a primeira execução de ponta a ponta contra infraestrutura real — nenhum jamais seria detectável por testes que usam *fakes*/mocks ou que nunca exercitam a descoberta real do Wolverine.
- **Resolvido no Checkpoint 7**: `PolicyCacheOptions.TimeToLive` (padrão 30s) não precisou de ajuste — o *benchmark* oficial da decisão 7 (§14) mediu p95 = 36,41 ms com cache aquecido, dentro da meta de 50 ms, sem qualquer otimização adicional.
- **Débito técnico registrado (§13.11)**: nenhum teste de arquitetura impede que uma futura classe de negócio implementando `IIntegrationEventHandler<TEvent>` termine seu nome em "Handler" com um método público iniciado por "Handle"/"Consume" — isso a exporia ao mesmo risco de dupla descoberta pela convenção do Wolverine (`opts.Discovery.IncludeAssembly`) já corrigido para `PolicyUpdatedCacheInvalidation`. Nenhuma correção preventiva foi implementada nesta homologação (fora do escopo aprovado); sinalizado para consideração em incremento futuro que adicionar um segundo consumidor de evento real.
- **Incidente de ambiente durante o Checkpoint 7 (sem impacto em dados ou evidências)**: o Docker Desktop parou de responder e precisou ser reiniciado no meio da homologação; todos os containers persistentes (dev e homolog) foram encontrados em estado `Exited (0)` — desligamento limpo, sem perda de dados (volumes nomeados preservados) — mas nenhum reiniciou automaticamente. Restaurados manualmente: `ihostpro-postgres`, `ihostpro-rabbitmq`, `ihostpro-redis` (necessários para este checkpoint), `ihostpro-homolog-postgres` e `n8n` (serviços não relacionados a este trabalho, restaurados por terem sido parados apenas como efeito colateral do reinício do Docker Desktop, não por decisão deliberada). Confirmado após a restauração: topologia RabbitMQ (*exchanges*/fila/*binding* de `configuration.policy-updated`) e dados PostgreSQL (as seis versões de `EARLY_CHECKIN` criadas durante o *round-trip* real) sobreviveram integralmente ao reinício; apenas o cache Redis (sem persistência configurada, por design) e os processos manuais `IHostPro.Api`/`IHostPro.Worker` foram perdidos, sem impacto na evidência já coletada do *round-trip*.
- **Instabilidade intermitente pré-existente, fora do escopo desta fase, observada durante as duas execuções consecutivas da suíte E2E completa**: `ReservationsE2ETests.Admin_edits_a_reservation` (Fase 3/4, nunca alterado nesta sessão) expirou (30s) em 2 de 5 tentativas de executar a suíte completa, sempre aprovado quando executado isoladamente (7s) — comportamento consistente com sensibilidade a carga cumulativa de recursos (memória/CPU) após muitas horas de execuções pesadas de Playwright/Docker/dotnet no mesmo ambiente nesta sessão, não uma regressão introduzida por esta fase (Configuration/Policies nunca toca Reservations). As duas execuções finais, consecutivas e sem qualquer intervenção manual entre elas, foram 100% aprovadas (54/54 cada), atendendo ao protocolo. Sinalizado como débito técnico para investigação futura (possivelmente aumentar a margem do `WaitForAsync` daquele teste especificamente, ou investigar a causa raiz da sensibilidade a carga), não corrigido nesta homologação por estar fora do escopo de Configuration & Policy.

## 17. Confirmações

- Nenhuma alteração em Reservations (domínio, contratos ou testes) foi realizada em nenhum checkpoint deste incremento — verificado estruturalmente por `ConfigurationSourceConventionTests` e pelos testes de dependência de arquitetura, e reconfirmado ao final do Checkpoint 7 (`ReservationsE2ETests`, único ponto de contato indireto por compartilhar a suíte E2E, permanece byte a byte como estava antes desta fase, exceto pela instabilidade intermitente pré-existente registrada em §16, não uma alteração de código).
- Nenhuma funcionalidade das Fases 6 em diante foi implementada.
- Nenhuma operação de versionamento (`git add`/`commit`/`push`/`tag`/`merge`/`rebase`/staging) foi realizada durante todo o período de homologação (Checkpoints 1-7) até a aprovação explícita do usuário — `git status` permaneceu mostrando a mesma working tree não staged do início ao fim da homologação. Após a aprovação, o versionamento foi realizado conforme registrado em §18; nenhum merge para `master`, tag ou force-push foi realizado em nenhum momento.
- O procedimento controlado de troca temporária do RabbitMQ+Redis (homolog→dev, §9.8/§16) preservou integralmente os containers, volumes, dados, portas e política de reinício do ambiente de homologação — confirmado por `docker start`/`docker ps` e `rabbitmq-diagnostics ping`/`redis-cli ping` reais após a restauração ao final do Checkpoint 7. Nenhum container de homologação foi removido, recriado ou teve seu volume alterado em nenhum momento.
- Nenhuma UI para Configurações/Feature Flags/Templates/regras SE-ENTÃO/vigência/edição de GLOBAL foi implementada em nenhum checkpoint (§10.9).
- Nenhum `ConfigurationDefinition`/`ConfigurationValue`, nenhum evento `ConfigurationUpdated` publicado, nenhum consumidor em Reservations ou outro Bounded Context foi implementado em nenhum checkpoint (§11.9).
- Os 7 testes Playwright E2E de Policies e o consumidor real de `PolicyUpdated` em `IHostPro.Worker` foram executados de ponta a ponta contra RabbitMQ e Redis reais no Checkpoint 7 — 7/7 aprovados de forma reproduzível (§13.12-§13.15), *round-trip* manual comprovado (§12), e o *benchmark* oficial da decisão 7 atingiu a meta de p95 ≤ 50 ms (§14). Nenhuma execução foi alegada sem ter de fato ocorrido, em nenhum ponto desta fase.
- O Incremento 1 (Policy Engine Foundation) está funcionalmente completo ao final deste documento — todos os 7 checkpoints concluídos, suíte de backend completa (1.643 testes) e suíte E2E completa (54 testes, duas execuções consecutivas) 100% aprovadas, build Debug e Release ambas limpas. O relatório final de encerramento (16 itens, conforme a instrução original) é apresentado separadamente na conversa, não neste documento.

## 18. Aprovação do usuário, débito preventivo e versionamento

### 18.1 Aprovação do usuário

O usuário aprovou tecnicamente o Incremento 1 (Policy Engine Foundation) com a seguinte instrução (resumo fiel): "O Incremento 1 — Policy Engine Foundation da Fase 5 está tecnicamente aprovado. Antes do versionamento, feche somente o débito preventivo de descoberta de handlers Wolverine descrito no relatório. Depois disso: 1. versionar e publicar o Incremento 1 na branch `feature/configuration-policy`; 2. iniciar somente a auditoria e planejamento do Incremento 2 — Configuration/Settings. Não fazer merge em master. Não criar tag. Não excluir branch. Não usar force push."

### 18.2 Débito preventivo fechado — teste de regressão contra a dupla descoberta do Wolverine (§13.11)

Antes de qualquer operação de `git add`/`commit`, foi adicionado `tests/Host/IHostPro.Api.Tests.Integration/PolicyUpdatedWolverineDiscoveryTests.cs` — um teste automatizado que prova, contra o `IHostPro.Worker.dll` real e não modificado, que:

- `PolicyUpdated` tem exatamente um ponto de entrada Wolverine (uma única linha "Started message listening at rabbitmq://queue/configuration.policy-updated" na saída do processo, nunca duas);
- esse ponto de entrada é o adaptador de transporte intencional (`PolicyUpdatedHandler`), nunca `PolicyUpdatedCacheInvalidation` (o serviço de negócio renomeado no §13.11 especificamente para deixar de ser confundido com um handler Wolverine);
- o middleware `TenantResolutionMiddleware` permanece resolvível pela geração de código do Wolverine;
- nenhuma classe interna necessária à geração de código do Worker é injetada de um jeito que a torne não materializável;
- a configuração do Worker continua gerando/compilando corretamente o *pipeline* de `PolicyUpdated`.

**Técnica escolhida**: nenhuma API pública e estável do Wolverine para inspecionar *handler chains* sem efetivamente disparar a descoberta/geração de código foi encontrada com confiança suficiente (`WolverineOptions.DescribeHandlerMatch`/`IWolverineRuntime.ExplainRoutingFor` existem e são as APIs por trás dos diagnósticos de linha de comando do Wolverine, mas exigiriam trocar `host.Run()` por `host.RunOaktonCommands(args)` em `Program.cs` — uma alteração de código de produção, expressamente vetada por esta mesma instrução de aprovação). Optou-se por reutilizar o padrão já estabelecido nesta base de código (`WolverineThreeStoreCompositionTests.RunMigrationRunnerAsync`, `WebE2EFixture.StartApiProcess`/`StartWorkerProcess`): iniciar o `IHostPro.Worker.dll` real, compilado e não modificado, como subprocesso contra PostgreSQL/RabbitMQ/Redis efêmeros reais (Testcontainers), com a topologia RabbitMQ de `configuration-events`/`configuration.policy-updated` provisionada pela mesma API pública `DeclareExchange`/`BindQueue` que `IHostPro.MigrationRunner` usa em produção — e então observar a saída real do processo (Serilog no console) em vez de qualquer API interna do Wolverine. Nenhum campo privado do *framework* foi acessado; nenhuma API de produção foi criada apenas para este teste.

**Validação proporcional executada** (nunca repetida: suíte completa de 1.643 testes, Playwright, *benchmark* ou *round-trip* manual, já válidos por nenhum código de produção ter sido alterado):

- Build de `IHostPro.Api.Tests.Integration` e de `IHostPro.Worker` (Debug): 0 erros.
- `PolicyUpdatedWolverineDiscoveryTests` (o novo teste): 1/1 aprovado — Worker real anexou exatamente um *listener* a `configuration.policy-updated`, sem nenhuma das cinco assinaturas de falha conhecidas desta homologação (`UnResolvableVariableException`, `InvalidServiceLocationException`, `error CS0128`, "Exception detected", segundo *listener* duplicado).
- Suíte completa de testes de arquitetura (`IHostPro.ArchitectureTests`): 131/131 aprovados.
- `git diff --check`: nenhum problema introduzido por este teste — os únicos apontamentos continuam sendo os espaços em branco pré-existentes em `api-client.ts` (§14), deliberadamente não corrigidos.
- Nenhuma alteração de código de produção foi necessária para este teste passar — se fosse necessária, a instrução de aprovação exigia parar e apresentar o motivo antes de versionar; isso não ocorreu.

### 18.3 Decisão de fusão dos Commits 1 e 2

O plano de seis commits aprovado pelo usuário previa um commit separado para a fundação do motor de políticas (domínio/persistência/API) e outro para o cache/evento `PolicyUpdated`/Worker. Ao preparar o *staging* seletivo, constatou-se que `ConfigurationModuleExtensions.cs` (o único ponto de composição DI do módulo) registra `IPolicyValueResolver` diretamente como o decorador de cache (`CachedPolicyValueResolver`), sem nenhuma forma "sem cache" que tenha de fato existido ou sido testada — o mesmo acoplamento aparece no `EnrollAncillaryPostgresqlOutbox` de `IHostPro.Api/Program.cs` e na declaração da *exchange* `configuration-events` em `IHostPro.MigrationRunner/Program.cs`. Uma separação fiel exigiria fabricar e comitar um estado intermediário "pré-cache" desses arquivos que nunca foi de fato exercitado pelos testes desta homologação.

Apresentadas três opções ao usuário (reconstruir o estado pré-cache; mesclar os dois commits; mover os arquivos acoplados inteiros para o commit de cache), o usuário escolheu explicitamente **mesclar os Commits 1 e 2** em um único commit. Resultado: cinco commits de código/documentação neste incremento, não seis — `IHostPro.sln` (que registra tanto os cinco projetos de código quanto os dois projetos de teste de Configuration em um único bloco gerado, sem separação prática possível) foi incluído por inteiro no Commit 1 pelo mesmo motivo (acoplamento não separável em conteúdo gerado por ferramenta, não em código de aplicação).

### 18.4 Commits realizados

| # | Hash completo | Mensagem |
|---|---|---|
| 1 | `54b454d7bfbb2e98cd611f7e0d95672e420b260c` | `feat(configuration): add policy engine foundation and cache invalidation` |
| 2 | `a0037957a043b58673ec7f5bcc5b586d1d3eb787` | `feat(frontend): add policy administration` |
| 3 | `640aa9bdf4d76b016b46e4a88fe860bef68b716e` | `test(configuration): cover policy engine workflows` |

Cada commit foi precedido de *staging* seletivo (nunca `git add .`) e revisão de `git diff --cached --name-only` para confirmar que apenas os arquivos do grupo pretendido estavam presentes, conforme a instrução de aprovação. Nenhum artefato proibido (`bin/`, `obj/`, `node_modules/`, `.angular/`, `dist/`, logs, `.env`, credenciais, `launch.json`, evidências manuais de homologação) foi versionado.

Este documento (Commit 4 — `docs(configuration): record increment 1 completion`) e o commit de fechamento de publicação (Commit 5 — `docs(configuration): close increment 1 publication`, após o push) fecham a lista de cinco commits aprovados.

### 18.5 Publicação

`git push -u origin feature/configuration-policy` executado após o Commit 4 (este documento, no estado do §18.4). Confirmado: quatro commits publicados em `origin/feature/configuration-policy` (`54b454d`, `a003795`, `640aa9b`, `9a5220a`), `git status -sb` mostrando `feature/configuration-policy...origin/feature/configuration-policy` com ahead/behind = 0/0, working tree limpa. Nenhum merge para `master`, tag ou exclusão de branch foi realizado.

Este parágrafo (§18.5) e o Commit 5 que o inclui (`docs(configuration): close increment 1 publication`) fecham a lista de cinco commits aprovados para este incremento — após este commit, um `git push origin feature/configuration-policy` final replica-o ao remoto, reconfirmando ahead/behind = 0/0.

### 18.6 Estado da Fase 5 após a publicação

A Fase 5 **continua em andamento**: este documento registra o fechamento e a publicação do Incremento 1 (Policy Engine Foundation). O Incremento 2 (Configuration/Settings) ainda não foi implementado — nenhum `ConfigurationDefinition`/`ConfigurationValue`/migração/API/frontend/teste foi criado. Apenas a auditoria e o planejamento somente leitura, sem código, começam após esta publicação (relatório de auditoria apresentado separadamente na conversa); a implementação do Incremento 2 aguarda aprovação explícita do usuário.

## 19. Incremento 2 — Configuration/Settings — Auditoria de Elegibilidade

Auditoria somente leitura executada e apresentada separadamente na conversa (inventário completo de valores hardcoded/duplicados no código real, leitura integral do Documento 08, do Documento 12 §11, do Documento 14 §28/§33, de `Architecture Principles.md` e do `Plano Executivo de Desenvolvimento por Fases.md`, e uma matriz de candidatos com doze critérios cada). O usuário aprovou formalmente a conclusão da auditoria: **nenhum candidato aprovado para implementação**. Nenhum código foi criado nesta auditoria — nenhum `ConfigurationDefinition`, nenhum `ConfigurationValue`, nenhuma migração, nenhuma API, nenhum frontend, nenhum evento `ConfigurationUpdated`, nenhum cache novo.

Critério de elegibilidade aplicado a cada candidato encontrado: só entraria no Incremento 2 um valor que tivesse simultaneamente (1) consumidor real no código; (2) variabilidade por tenant documentada; (3) natureza de configuração de comportamento, não de política de negócio nem de segurança/infraestrutura; (4) valor de negócio suficiente para justificar o motor. Nenhum candidato encontrado satisfez os quatro critérios ao mesmo tempo — motivo pelo qual o Incremento 2 se encerra como auditoria, sem catálogo implementado.

### 19.1 `DefaultPageSize` / `MaxPageSize`

Encontrados duplicados como literais (`20`/`100`) em `UserListingOptions` (Identity, já `IOptions`), e como `const int` repetido em `ListCondominiumsQueryHandler`/`ListCondominiumsQueryValidator`/`CondominiumReader`, `ListPropertiesQueryHandler`/`ListPropertiesQueryValidator`/`PropertyReader`, `ListPropertyOwnersQueryHandler`/`ListPropertyOwnersQueryValidator`/`PropertyOwnerReader` (Property Management) e `ListReservationsQueryHandler`/`ListReservationsQueryValidator`/`ReservationReader` (Reservations).

**Classificação oficial**: débito técnico de duplicação de código — não pertence ao Configuration & Policy. Nenhuma variabilidade por tenant foi documentada em nenhum dos documentos consultados (Documento 08, 12, 14). Não implementar agora. Registrado como débito técnico futuro: uma eventual correção deve unificar o valor em uma opção/constante técnica compartilhada (por exemplo, um `IOptions` em `BuildingBlocks`), nunca como `ConfigurationDefinition`. Nenhuma refatoração foi executada nesta fase.

### 19.2 Idioma padrão

Encontrado hardcoded (`defaultLang: 'pt-BR'`) em `app.config.ts` (Transloco), único idioma padrão para toda a plataforma, sem leitura de nenhuma fonte externa.

**Classificação oficial**: requisito válido e documentado para personalização por tenant (Documento 14 §33: "cada tenant poderá personalizar... idioma padrão"). Não implementar agora — depende de uma decisão futura ainda não tomada sobre como identificar o tenant antes da autenticação (a tela de login não tem nenhum tenant resolvido no momento em que o idioma da própria tela de login precisaria ser decidido). `pt-BR` permanece como comportamento atual, inalterado. Nenhuma configuração parcial que só funcionasse depois do login foi criada.

### 19.3 Fuso horário

Nenhum fuso horário hardcoded foi encontrado no código — todo tratamento de data já usa `DateTimeOffset`/UTC normalizado, sem nenhum valor fixo a substituir.

**Classificação oficial**: requisito futuro válido (Documento 14 §33: "fuso horário" entre os itens personalizáveis por tenant), mas sem consumidor funcional atual que necessite do motor de Configuration — nenhuma tela hoje formata ou exibe horários com um fuso presumido. Não implementar agora.

### 19.4 Segurança — confirmação de que permanecem configuração técnica de plataforma

Confirmado: `AccountLockoutOptions`, `Argon2Options`, `JwtOptions`, `JwtSigningKeyOptions`, `PasswordPolicyOptions` e `RefreshTokenOptions` continuam sendo configuração técnica/de segurança da plataforma (já implementadas como `IOptions` validadas com `ValidateOnStart`, section-bound em `appsettings.json`/variáveis de ambiente) e **nunca** configuração administrável por tenant. Fundamento: Documento 08 §27 exclui explicitamente "as garantias de segurança" da configurabilidade pelo administrador; `JwtSigningKeyOptions` armazena material de segredo, nunca elegível ao motor por instrução direta do usuário. Nenhuma dessas seis classes foi alterada nesta auditoria.

### 19.5 Infraestrutura — confirmação de que permanecem em deployment/appsettings/IOptions

Confirmado: Redis, RabbitMQ, connection strings, cache TTL técnico (`PolicyCacheOptions.TimeToLive`, `PermissionCacheOptions.Lifetime`, `SessionRevocationCacheOptions`), timeouts (`RabbitMqClientTimeoutOptions`), CORS, logging, observabilidade (OpenTelemetry/OTLP) e URLs internas permanecem exclusivamente em `appsettings.json`/variáveis de ambiente/`IOptions`, e não entram no motor de Configuration em nenhum momento desta fase. Nenhum desses itens foi alterado nesta auditoria.

## 20. Integração em `master`

Após a aprovação do encerramento da Fase 5 (Incremento 1 concluído e publicado; Incremento 2 encerrado como auditoria, §19), o usuário autorizou explicitamente a integração de `feature/configuration-policy` em `master`.

Procedimento executado: `git checkout master`; `git fetch origin`; `git pull --ff-only origin master` (já sincronizado, sem novos commits); `git merge --ff-only feature/configuration-policy` — fast-forward puro, sem commit de merge, confirmado por `git merge-base --is-ancestor master feature/configuration-policy` antes da operação; `git push origin master`.

Resultado confirmado: `master` avançou de `f711a9c` (fechamento da Fase 4) para `8a15368` (fechamento desta auditoria), incorporando os seis commits da Fase 5 (`54b454d`, `a003795`, `640aa9b`, `9a5220a`, `e0494cd`, `8a15368`) sem nenhum commit de merge. `git status -sb` mostrando `master...origin/master` com ahead/behind = 0/0, working tree limpa. Nenhum commit de merge, rebase, force-push ou tag foi criado em nenhum momento. `feature/configuration-policy` não foi excluída.
