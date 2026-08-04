# Fase 2 — Property Management — Validação e Homologação

Versão: 1.3

Status: Oficial — Checkpoint 6 (Homologação final da Fase 2) aprovado tecnicamente; inventário Git consolidado (Seção 5.17); aguardando autorização final do usuário para o commit único

---

## 1. Objetivo

Este documento registra a validação e homologação real dos incrementos da Fase 2 (Property Management) a partir do ponto em que este documento foi criado.

Este documento não repete decisões arquiteturais já registradas em `Architecture Principles.md`, no Documento 07 (Catálogo de Eventos de Domínio) ou nas ADRs — apenas registra a evidência de validação e o histórico de correções encontradas durante a homologação, conforme `ai-rules/06 - Definition of Done.md`.

**Nota sobre escopo retroativo**: os Checkpoints 1 (Fundação: Domain, migration, RLS, MigrationRunner, outbox) e 2 (CRUD de Condomínios) da Fase 2 já foram implementados, validados e aprovados antes da criação deste documento, mas seu histórico de validação não foi reconstruído retroativamente aqui — por decisão explícita do usuário, este documento começa a partir do Checkpoint 3. Futuros checkpoints da Fase 2 devem adicionar sua própria seção a este documento, não criar um novo arquivo.

---

## 2. Checkpoint 3 — Imóveis administrativos (Create/Update/List/Detail)

### 2.1 Escopo homologado

- Quatro endpoints administrativos de Imóvel (`POST/GET/PATCH /api/v1/properties`, `GET /api/v1/properties/{id}`), protegidos por `PROPERTIES:MANAGE` — mesma política central já registrada em `AddIdentityAuthorization()` desde o Checkpoint 1, sem duplicação.
- Todo Imóvel nasce em `Draft`; código (`PropertyCode`) único por tenant após normalização (`uq_properties_tenant_normalized_code`); endereço próprio obrigatório apenas quando não há Condomínio vinculado (CHECK `ck_properties_effective_address_source`, já existente desde o Checkpoint 1 — nenhuma migration nova neste checkpoint).
- Endereço efetivo resolvido em runtime (próprio > do Condomínio), nunca copiado para a linha do Imóvel; listagem nunca retorna endereço próprio ou efetivo, apenas o detalhe.
- PATCH presence-aware: tipo `Optional<T>` (Application, sem dependência de framework) + `OptionalJsonConverter<T>`/`OptionalJsonConverterFactory` (Api, `System.Text.Json`) distinguindo campo omitido (mantém valor atual) de campo explicitamente `null` (remove condomínio/endereço próprio) de campo suprido com valor — omitido nunca invoca o converter; `null` explícito invoca via `HandleNull = true`.
- Idempotência: cada campo comparado após normalização contra o valor atual; `ChangedFields` na ordem `code`, `name`, `capacity`, `condominium_id`, `address`; no-op não gera auditoria, evento, nem bump de `UpdatedAt`.
- Concorrência otimista via `xmin` (sem retry automático); unicidade de código traduzida exclusivamente da constraint exata (nunca `DbUpdateException` genérica).
- Dois novos Integration Events (`PropertyCreated`/`PropertyUpdated`), outbox durável (`property_management_messaging`), mesmo exchange `property-management-events` já usado por Condomínios — documentados no Documento 07 §14.

### 2.2 Fora de escopo (autorizado explicitamente para checkpoints futuros)

Lifecycle (ativar/desativar/arquivar), Ownership, rotas `mine`, Grupos, Portarias, exclusão de Imóvel.

### 2.3 Ambiente de execução

Docker Desktop, PostgreSQL 16 (`postgres:16`) e RabbitMQ 3 (`rabbitmq:3-management-alpine`) via Testcontainers (containers efêmeros), .NET SDK 10.0.302. Idêntico ao ambiente já usado nos Checkpoints 1/2 da Fase 2 e no Incremento 2 da Fase 1.

### 2.4 Problema real encontrado e corrigido (código de teste)

- **Sintoma**: `PropertiesEndpointsTests.Two_concurrent_updates_of_the_same_property_allow_only_one_to_succeed_with_409` falhava de forma determinística (5/5 execuções) com ambas as requisições HTTP concorrentes retornando `200`, nunca `409`.
- **Investigação**: confirmado que o mesmo padrão (duas requisições HTTP via `Task.WhenAll`, sem sincronização explícita, contra um único `TestServer` in-memory) já existe, com a mesma falha determinística, no teste equivalente já aprovado de Condomínios (`CondominiumsEndpointsTests.Two_concurrent_updates_of_the_same_condominium_allow_only_one_to_succeed_with_409`) — ou seja, não é uma regressão introduzida por este checkpoint, é uma característica pré-existente do padrão de teste: sem uma barreira real, as duas requisições não necessariamente colidem no mesmo instante no banco. Fora do escopo deste checkpoint alterar o teste de Condomínios (débito antigo, não corrigido).
- **Correção** (apenas código de teste novo deste checkpoint — nenhuma mudança em código de produção): `PropertiesEndpointsTests.BuildHostAsync` passou a aceitar um `overrides` opcional; o teste de concorrência HTTP passou a usar dois hosts `TestServer` separados, cada um com um `IPropertyAuditWriter` substituído por uma implementação que sincroniza via `Barrier(2)` compartilhado — a mesma técnica já usada com sucesso no teste de concorrência real ao nível de `PropertyCommandHandlerTests` (via `ISender` direto). Confirmado com 3 execuções consecutivas após a correção: `409` reproduzido de forma determinística nas 3.
- **Arquivo**: `PropertiesEndpointsTests.cs` (apenas código de teste).

### 2.5 Evidência de validação — build Release e suítes completas, duas execuções consecutivas da suíte de integração

| Suíte | Total | Execução 1 | Execução 2 |
|---|---|---|---|
| Arquitetura (`IHostPro.ArchitectureTests`) | 76 | 76/76 aprovados | — (executada uma vez; sem estado externo, não sujeita a flakiness de infraestrutura) |
| Unitários (Property Management) | 116 | 116/116 aprovados | — (idem) |
| Integração (Property Management — PostgreSQL + RabbitMQ reais via Testcontainers) | 85 | 85/85 aprovados | 85/85 aprovados |

Build Release (`dotnet build IHostPro.sln -c Release`): 0 erros, 0 avisos, todos os projetos da solução.

Novidades desta contagem em relação ao estado anterior a este checkpoint: Application (+41 testes unitários: `CreatePropertyCommandHandlerTests` [12], `UpdatePropertyCommandHandlerTests` [22], `ListPropertiesQueryHandlerTests` [4], `GetPropertyDetailQueryHandlerTests` [3]); Integração (+40: `PropertyCommandHandlerTests` [12] incluindo concorrência real via `Barrier` para update e para criação com código duplicado, `PropertyIntegrationEventsTests` [3] PostgreSQL+RabbitMQ, `PropertiesEndpointsTests` [25] HTTP); Arquitetura (+5: `PropertyManagementPropertiesEndpointsArchitectureTests`, mais 1 teste atualizado — não novo — em `PropertyManagementCondominiumsEndpointsArchitectureTests` para refletir a existência aprovada de `PropertiesController`).

### 2.6 Critérios objetivos de aceite

- [x] Build Release 0 erros/0 avisos.
- [x] Nenhuma migration criada ou alterada — schema já aprovado no Checkpoint 1 (`ck_properties_effective_address_source`, `uq_properties_tenant_normalized_code`, FK composta tenant-aware) já suportava tudo o que este checkpoint precisava.
- [x] Application permanece sem dependência de ASP.NET Core, EF Core, Wolverine, Identity, `PropertyManagement.Infrastructure` ou `PropertyManagement.Api`.
- [x] `PropertyManagement.Api` continua a única camada com referência a `Identity.Contracts`; nenhuma string literal `PROPERTIES:MANAGE` duplicada.
- [x] Somente os 4 endpoints administrativos aprovados existem — nenhum endpoint de lifecycle/Ownership/`mine`/Grupo/Portaria.
- [x] PATCH presence-aware (`Optional<T>`) distingue omitido/`null` explícito/valor suprido, validado via JSON bruto em testes HTTP (não via round-trip do próprio contrato).
- [x] Idempotência, unicidade de código (case-insensitive) e concorrência real (`xmin`, sem retry) validadas contra PostgreSQL real, incluindo concorrência de criação com o mesmo código normalizado.
- [x] Endereço efetivo resolvido corretamente (próprio vs. condomínio) em Create/Update/Detail; listagem nunca retorna endereço.
- [x] Dois novos Integration Events com outbox durável, mesmo exchange de Condomínios, schema isolado de `identity_messaging` — documentados no Documento 07 §14.
- [x] Suíte de integração aprovada em duas execuções consecutivas.
- [x] Nenhum commit, push, tag ou merge realizado.

### 2.7 Status desta etapa

**Checkpoint 3 — Imóveis administrativos (Create/Update/List/Detail): Implementação concluída · Homologação concluída · Status aprovado · Nenhum bloqueador pendente.**

---

## 3. Checkpoint 4 — Lifecycle de Imóveis (Activate/Deactivate/Archive)

### 3.1 Escopo homologado

- Três endpoints de transição de estado (`POST /api/v1/properties/{id}/activate`, `.../deactivate`, `.../archive`), sem body, protegidos por `PROPERTIES:MANAGE` — mesma política central, sem duplicação.
- Máquina de estados: `Draft`/`Inactive` → `Active` (Activate); `Active` → `Inactive` (Deactivate); `Draft`/`Inactive` → `Archived` (Archive, terminal — sem restauração). Transições implementadas como métodos explícitos no agregado `Property` (`Activate`/`Deactivate`/`Archive`), nunca um setter genérico de status — cada método preserva suas próprias invariantes e só atualiza `UpdatedAt` em sucesso.
- Semântica de operações repetidas com códigos estáveis distintos por cenário: `PropertyAlreadyActive`, `PropertyAlreadyInactive`, `PropertyAlreadyArchived` (estado já no destino) vs. `InvalidPropertyStatusTransition` (transição estruturalmente impossível, ex.: `Draft`→`Inactive`, `Active`→`Archived`) — todos HTTP 409, verificados antes de qualquer mutação/auditoria/evento.
- Ativação exige endereço efetivo válido (próprio ou do Condomínio, revalidado dentro da mesma transação tenant-aware) — `PropertyAddressRequired`/`CondominiumNotFound` reaproveitados do Checkpoint 3.
- `PATCH /api/v1/properties/{id}` passou a rejeitar qualquer alteração em Imóvel `Archived` (`ArchivedPropertyCannotBeModified`, 409) — inclusive um PATCH cujos valores já coincidem com os atuais, verificado antes de qualquer avaliação de no-op.
- Três novos Integration Events (`PropertyActivated`/`PropertyDeactivated`/`PropertyArchived`), mesmo exchange `property-management-events`, mesmo outbox durável — documentados no Documento 07 §14. `PropertyUpdated` não foi alterado para representar lifecycle.
- Concorrência otimista via `xmin` reaproveitada (sem retry) através de um executor/behavior compartilhado pelas três transições (`ILifecyclePropertyExecutor`/`LifecyclePropertyTenantAwareBehavior<TCommand>`, genérico mas registrado como três generics fechados) — nunca precisa traduzir unicidade de código, já que nenhuma transição toca `Code`.

### 3.2 Fora de escopo (autorizado explicitamente para checkpoints futuros)

Ownership, rotas `mine`, Grupos, Portarias, contrato síncrono com Identity, restauração de Imóvel arquivado, exclusão física/lógica adicional, novo estado, agendamento de transição.

### 3.3 Ambiente de execução

Idêntico à Seção 2.3.

### 3.4 Débito técnico registrado no Checkpoint 3/4 — corrigido no Checkpoint 5 (Seção 4.11)

Conforme aprovação do Checkpoint 3 (Seção 2.4), `CondominiumsEndpointsTests.Two_concurrent_updates_of_the_same_condominium_allow_only_one_to_succeed_with_409` carecia de sincronização determinística (duas requisições HTTP via `Task.WhenAll`, sem `Barrier`, contra um único `TestServer`) e falhava de forma não-determinística (ora `200`/`200`, ora aprovado por sorte de timing). Confirmado nos Checkpoints 3/4 que o padrão correspondente para Imóveis (`PropertiesEndpointsTests.Two_concurrent_updates_of_the_same_property_...`) já havia sido corrigido no Checkpoint 3 com sincronização real via `Barrier` compartilhado entre dois hosts. Nos Checkpoints 3 e 4, por instrução explícita do usuário, o teste de Condomínios permaneceu como estava. Na aprovação parcial do Checkpoint 5, o usuário autorizou expressamente a correção, estritamente limitada a este teste — ver Seção 4.11 para a causa raiz, a correção aplicada e a evidência completa. **Este débito técnico está resolvido desde o Checkpoint 5.**

### 3.5 Evidência de validação — build Release e suítes completas, duas execuções consecutivas da suíte de integração

| Suíte | Total | Execução 1 | Execução 2 |
|---|---|---|---|
| Arquitetura (`IHostPro.ArchitectureTests`) | 85 | 85/85 aprovados | — (executada uma vez; sem estado externo, não sujeita a flakiness de infraestrutura) |
| Unitários (Property Management) | 153 | 153/153 aprovados | — (idem) |
| Integração (Property Management — PostgreSQL + RabbitMQ reais via Testcontainers) | 126 | 126/126 aprovados | 126/126 aprovados |

Build Release (`dotnet build IHostPro.sln -c Release`): 0 erros, 0 avisos, todos os projetos da solução.

Novidades desta contagem em relação ao Checkpoint 3 (Seção 2.5): Unitários (+37: `ActivatePropertyCommandHandlerTests` [12], `DeactivatePropertyCommandHandlerTests` [9], `ArchivePropertyCommandHandlerTests` [10], mais 6 regressões de "Archived bloqueia PATCH" adicionadas a `UpdatePropertyCommandHandlerTests`); Integração (+41: `PropertyLifecycleCommandHandlerTests` [17] incluindo concorrência real via `Barrier` para ativações concorrentes e para ativação concorrente com atualização cadastral, `PropertyLifecycleIntegrationEventsTests` [5] PostgreSQL+RabbitMQ, `PropertiesLifecycleEndpointsTests` [19] HTTP); Arquitetura (+9: novo arquivo `PropertyManagementPropertiesLifecycleArchitectureTests`, teste de conjunto de ações atualizado — não novo — em `PropertyManagementPropertiesEndpointsArchitectureTests` para as sete ações aprovadas, mais dois testes novos de convenção de código-fonte em `PropertyManagementSourceConventionTests` — ausência de Grupo/Portaria, migration única).

### 3.6 Explicação da máquina de estados

Quatro estados (`Draft`, `Active`, `Inactive`, `Archived`), cinco transições permitidas (`Draft→Active`, `Active→Inactive`, `Inactive→Active`, `Draft→Archived`, `Inactive→Archived`), `Archived` terminal. Cada transição é um método nomeado explícito no agregado (`Property.Activate`/`Deactivate`/`Archive`), nunca um setter genérico — cada um valida seu próprio conjunto de estados de origem via guard clause e lança `InvalidOperationException` (defesa em profundidade) se violado; a tradução para o código estável HTTP correto (já-no-destino vs. transição inválida) acontece no handler, que consulta o status atual ANTES de chamar o método de domínio, já que o mesmo estado de origem inválido pode significar coisas diferentes dependendo da transição solicitada (ex.: `Archived` sempre significa "já arquivado" para as três operações; `Draft` significa "transição inválida" para Deactivate mas "sucesso" para Activate/Archive).

### 3.7 Explicação da concorrência sem retry

Todas as três transições compartilham `ILifecyclePropertyExecutor`, que envolve `IPropertyManagementTransactionExecutor` e traduz exclusivamente `DbUpdateConcurrencyException` (nunca unicidade de código, que nenhuma transição pode violar) para `PropertyConcurrencyConflict` — sem loop de retry, sem repetição automática. Validado com barreira real (`Barrier(2)`) em três cenários: (1) duas ativações concorrentes do mesmo `Draft` — exatamente uma confirma; (2) `Deactivate`+`Archive` concorrentes em um Imóvel `Active` — `Archive` é estruturalmente inválido a partir de `Active` independente da ordem de execução, nunca confirma um salto direto `Active→Archived`; (3) `Activate` de um `Inactive` concorrente com uma atualização cadastral (`PATCH`) do mesmo Imóvel — exatamente uma confirma, baseada na versão `xmin` original, sem sobrescrita silenciosa.

### 3.8 Confirmação: atualização bloqueada em Archived

`UpdatePropertyCommandHandler` verifica `property.Status == PropertyStatus.Archived` imediatamente após buscar o Imóvel, antes de computar o estado final prospectivo, antes de qualquer comparação de idempotência/no-op e antes de qualquer diffing de campo — retornando `ArchivedPropertyCannotBeModified` (409) incondicionalmente. Confirmado por teste unitário dedicado que mesmo um PATCH cujos valores já coincidem exatamente com os atuais (que seria um no-op bem-sucedido em qualquer outro estado) é rejeitado da mesma forma quando o Imóvel está arquivado, e por teste de integração HTTP real (`PATCH` de um Imóvel arquivado → `409`).

### 3.9 Arquivos criados

**Domain**: `Property.cs` (métodos `Activate`/`Deactivate`/`Archive` adicionados).

**Application** (`PropertyManagement.Application/Properties/`): `ActivatePropertyCommand.cs`, `ActivatePropertyCommandHandler.cs`, `DeactivatePropertyCommand.cs`, `DeactivatePropertyCommandHandler.cs`, `ArchivePropertyCommand.cs`, `ArchivePropertyCommandHandler.cs`, `ILifecyclePropertyExecutor.cs`, `PropertyEffectiveAddressResolver.cs`.

**Contracts**: `PropertyActivated.cs`, `PropertyDeactivated.cs`, `PropertyArchived.cs`.

**Infrastructure** (`PropertyManagement.Infrastructure/Persistence/`): `LifecyclePropertyExecutor.cs`, `LifecyclePropertyTenantAwareBehavior.cs`.

**Tests**: `Application/Properties/{ActivatePropertyCommandHandlerTests,DeactivatePropertyCommandHandlerTests,ArchivePropertyCommandHandlerTests}.cs`; `Integration/{PropertyLifecycleCommandHandlerTests,PropertyLifecycleIntegrationEventsTests,PropertiesLifecycleEndpointsTests}.cs`; `ArchitectureTests/PropertyManagementPropertiesLifecycleArchitectureTests.cs`.

**Documentação**: `Documento 07` (seção 14 estendida com os três novos eventos/routing keys); este documento (nova Seção 3).

### 3.10 Arquivos modificados

`PropertyManagementErrorCodes.cs` (+5 códigos: `PropertyAlreadyActive`, `PropertyAlreadyInactive`, `PropertyAlreadyArchived`, `InvalidPropertyStatusTransition`, `ArchivedPropertyCannotBeModified`); `UpdatePropertyCommandHandler.cs` (bloqueio de `Archived`); `PropertyManagementCommandDispatchExtensions.cs` (registro dos três Commands/behaviors); `PropertiesController.cs` (três ações novas); `PropertyManagementResultHttpMapper.cs` (+5 códigos de conflito); `Program.cs` (Host — três novas rotas de evento); `PropertyManagementPropertiesEndpointsArchitectureTests.cs` (conjunto de ações atualizado para sete, três novos testes de "sem body"); `PropertyManagementSourceConventionTests.cs` (dois novos testes: ausência de Grupo/Portaria, migration única).

### 3.11 Confirmação: nenhuma migration foi criada

Confirmado via inspeção direta do diretório `Persistence/Migrations/` (apenas `20260730024157_InitialCreate.cs`/`.Designer.cs`, datados do Checkpoint 1, sem novos arquivos) e via teste de arquitetura dedicado (`PropertyManagementSourceConventionTests.Exactly_one_migration_exists_and_no_new_migration_was_created_this_checkpoint`). O schema aprovado no Checkpoint 1 (`status`, `xmin`, endereço próprio, `CondominiumId`, auditoria, outbox) já suportava integralmente este checkpoint.

### 3.12 Critérios objetivos de aceite

- [x] Build Release 0 erros/0 avisos.
- [x] Nenhuma migration criada ou alterada.
- [x] Máquina de estados implementada por métodos explícitos no agregado, nunca setter genérico (`ChangeStatus`/`SetStatus`/`UpdateStatus`) — confirmado por teste de arquitetura via reflexão.
- [x] Todos os códigos de erro estáveis distintos por cenário (já-no-destino vs. transição inválida vs. concorrência vs. endereço ausente vs. Condomínio inexistente), mapeados corretamente para 400/404/409.
- [x] Ativação valida endereço efetivo dentro da transação tenant-aware, revalidando o Condomínio associado.
- [x] `PATCH` de Imóvel arquivado sempre rejeitado, inclusive no-op.
- [x] Três novos Integration Events, exatamente um por transição confirmada, nenhum em rejeição/conflito, sem dados sensíveis, outbox durável, exchange correto, `FailuresBeforeCircuitBreaks = 1`, schema isolado de `identity_messaging`.
- [x] `PropertyUpdated` não foi repurposed para lifecycle.
- [x] Concorrência real validada com barreira explícita nos três cenários do plano (mesma transição, transições diferentes, transição vs. atualização cadastral).
- [x] Somente as sete ações administrativas aprovadas existem em `PropertiesController` — nenhuma rota de Ownership/`mine`/Grupo/Portaria.
- [x] Débito técnico do teste HTTP de concorrência de Condomínios registrado, não corrigido (Seção 3.4).
- [x] Suíte de integração aprovada em duas execuções consecutivas.
- [x] Identity não foi alterado neste checkpoint — regressão de Identity não executada (não aplicável).
- [x] Nenhum commit, push, tag ou merge realizado.

### 3.13 Status desta etapa

**Checkpoint 4 — Lifecycle de Imóveis (Activate/Deactivate/Archive): Implementação concluída · Homologação concluída · Status aprovado · Débito técnico pré-existente registrado (Seção 3.4) · Nenhum bloqueador pendente.**

---

## 4. Checkpoint 5 — Ownership e leitura dos próprios Imóveis

### 4.1 Escopo homologado

- Vínculo/desvínculo entre um usuário do contexto Identity & Access e um Imóvel (`POST`/`DELETE /api/v1/properties/{propertyId}/owners(/{ownerUserId})`, `PROPERTIES:MANAGE`) e listagem administrativa dos proprietários vinculados (`GET /api/v1/properties/{propertyId}/owners`, mesma política).
- Leitura pelo próprio proprietário de seus Imóveis (`GET /api/v1/properties/mine`, `GET /api/v1/properties/mine/{propertyId}`, política própria `PROPERTIES:READ:OWN_OWNER`), em um controller administrativo separado (`MyPropertiesController`), mesma separação já usada por Identity (`UserAdministrationController` vs. `UsersController`).
- Elegibilidade validada de forma síncrona contra Identity através de um contrato público novo em `Identity.Contracts` (`IIdentityUserEligibilityReader`, implementado apenas em `Identity.Infrastructure`) — retorna somente `UserId`/`IsActive`/`HasRequiredRole`, nunca nome/e-mail/papéis completos/hash/sessões/tokens. Papel canônico `PROPERTY_OWNER` centralizado em `IdentityRoleCodes.PropertyOwner` (fonte única, validada por teste de arquitetura).
- Fronteira transacional sem transação distribuída: a checagem de elegibilidade em Identity é concluída e sua conexão fechada ANTES de a transação de escrita de Property Management ser aberta — `LinkPropertyOwnerCommandHandler` é o único handler deste Bounded Context sem pipeline behavior de abertura de transação; o próprio handler injeta `ILinkPropertyOwnerExecutor` e só o invoca depois da checagem de Identity. A pequena janela de TOCTOU daí resultante (papel/status do proprietário pode mudar entre a checagem e o commit) é aceita explicitamente: o vínculo confirma mesmo assim, o RBAC continua a proteger o acesso `mine` depois, nenhuma trava entre contextos, nenhum retry da checagem.
- Vínculo/desvínculo permitido para Imóvel em qualquer status (`Draft`/`Active`/`Inactive`/`Archived`) — `ArchivedPropertyCannotBeModified` nunca se aplica a esta operação; nenhuma transição de status é causada por vínculo/desvínculo; rotas `mine` retornam Imóveis de qualquer status.
- Sem FK física para Identity: `PropertyOwnerLink.OwnerUserId` é um `Guid` opaco (mesmo padrão de `ActorId` em toda a base). Filtro ABAC (`OwnerUserId`) é parte de primeira classe da própria Query (`ListMyPropertiesQuery`/`GetMyPropertyDetailQuery`), nunca só do controller — validado por teste de arquitetura.
- Dois novos Integration Events (`PropertyOwnerLinked`/`PropertyOwnerUnlinked`), mesmo exchange `property-management-events`, mesmo outbox durável — documentados no Documento 07 §14.3.
- Tradução de constraint exata: apenas a violação da constraint `uq_property_owners_tenant_property_owner` é traduzida para `PropertyOwnerAlreadyLinked` (409); um `DELETE` concorrente do mesmo vínculo é traduzido para `PropertyOwnerNotLinked` (404, não 409 — não há coluna de versão em `property_owners`, "o perdedor vê não-encontrado" é a semântica aprovada, deliberadamente diferente de todo outro executor deste Bounded Context).
- Nenhuma migration nova — reaproveita a tabela `property_management.property_owners` já criada no Checkpoint 1.

### 4.2 Fora de escopo (autorizado explicitamente para checkpoints futuros)

Grupos, Portarias, exclusão de Imóvel, novos estados, endpoint de atualização/detalhe individual de vínculo, `ownerUserId` nas rotas `mine`, sincronização automática de vínculo, correção de débitos técnicos antigos.

### 4.3 Ambiente de execução

Idêntico à Seção 2.3, com a adição de que os testes deste checkpoint que envolvem `LinkPropertyOwnerCommand` provisionam AMBOS os schemas (`identity` e `property_management`) no mesmo container PostgreSQL efêmero e registram ambos os módulos (`AddIdentityModule` + `AddPropertyManagementModule`) no mesmo host de teste — a checagem de elegibilidade é uma chamada síncrona real contra Identity, não simulada.

### 4.4 Descoberta relevante: comportamento de "tenant ausente" difere de todo outro Command deste Bounded Context

Diferente dos Commands de lifecycle (que lançam `TenantContextNotResolvedException` a partir do próprio pipeline behavior de abertura de transação), `LinkPropertyOwnerCommand` não tem pipeline behavior algum — a primeira leitura dependente de tenant que ele executa é a checagem de elegibilidade em Identity. Neste host composto (Identity e Property Management registrados no mesmo container de DI), o `IdentityDbContext` e o `ITenantContext` ambiente de Property Management são a MESMA instância por escopo, e o Global Query Filter obrigatório de `BaseDbContext` (`entity.TenantId == _tenantContext.TenantId`) falha fechado para zero linhas — nunca uma exceção — quando esse tenant ambiente está não resolvido. Um tenant ausente neste fluxo, portanto, se manifesta como `OwnerUserNotFound` (a leitura de elegibilidade não encontra ninguém), não como uma exceção lançada — a requisição ainda falha fechada, apenas mais cedo e por um sinal diferente do usado pelos Commands de lifecycle. Confirmado e coberto por teste de integração dedicado (`PropertyOwnerCommandHandlerTests.Absent_tenant_context_fails_closed_on_link_via_OwnerUserNotFound`). `UnlinkPropertyOwnerCommand`, por não chamar Identity, mantém o comportamento padrão (`TenantContextNotResolvedException` via seu próprio pipeline behavior).

### 4.5 Evidência de validação — build Release e suítes completas, duas execuções consecutivas da suíte de integração

| Suíte | Total | Execução 1 | Execução 2 |
|---|---|---|---|
| Arquitetura (`IHostPro.ArchitectureTests`) | 97 | 97/97 aprovados | — (executada uma vez; sem estado externo, não sujeita a flakiness de infraestrutura) |
| Unitários (Identity) | 468 | 468/468 aprovados | — (Identity foi alterado neste checkpoint — suíte de integração de Identity também executada, ver abaixo) |
| Unitários (Property Management) | 180 | 180/180 aprovados | — (idem) |
| Integração (Identity — PostgreSQL real via Testcontainers) | 411 | 411/411 aprovados | — (executada uma vez, conforme plano — Identity foi alterado apenas para adicionar o contrato de elegibilidade, sem alterar nenhum fluxo existente) |
| Integração (Property Management — PostgreSQL + RabbitMQ + Identity reais via Testcontainers) | 184 | 184/184 aprovados | 184/184 aprovados |

Build Release (`dotnet build IHostPro.sln -c Release`): 0 erros, 0 avisos, todos os projetos da solução.

**Nota histórica**: a primeira rodada de validação deste checkpoint registrou uma falha isolada e não-determinística na Execução 2 (`CondominiumsEndpointsTests.Two_concurrent_updates_of_the_same_condominium_...`), correspondente ao débito técnico então ainda pendente da Seção 3.4. Nenhum dos 58 testes novos deste checkpoint falhou naquela rodada. O usuário aprovou parcialmente o Checkpoint 5 e autorizou a correção estritamente desse teste — ver Seção 4.11 para causa raiz, correção e evidência completa (10 execuções focadas + classe completa + as duas execuções completas acima, já refletindo o teste corrigido).

Novidades desta contagem em relação ao Checkpoint 4 (Seção 3.5): Unitários PM (+27: `LinkPropertyOwnerCommandHandlerTests` [10], `UnlinkPropertyOwnerCommandHandlerTests` [6], `ListPropertyOwnersQueryHandlerTests` [4], `ListMyPropertiesQueryHandlerTests` [4], `GetMyPropertyDetailQueryHandlerTests` [3]); Integração PM (+58, de 126 para 184: `PropertyOwnerCommandHandlerTests` [28] incluindo concorrência real via `Barrier` para vínculo e desvínculo e o cenário determinístico de TOCTOU, `PropertyOwnerIntegrationEventsTests` [5] PostgreSQL+RabbitMQ+Identity, `PropertyOwnerEndpointsTests` [25] HTTP); Identity: +1 arquivo de teste de integração novo (`IdentityUserEligibilityReaderTests` [8]), sem alterar a contagem total pré-existente de Identity além dessas 8 adições; Arquitetura (+12: `PropertyManagementMyPropertiesEndpointsArchitectureTests` novo [4], `PropertyManagementDependencyTests` +2 testes novos, `IdentityAuthorizationCatalogConsistencyTests` +1 teste novo de fonte canônica de `IdentityRoleCodes`, `PropertyManagementCondominiumsEndpointsArchitectureTests`/`PropertyManagementPropertiesEndpointsArchitectureTests`/`PropertyManagementSourceConventionTests` com testes atualizados/novos para refletir `MyPropertiesController` e as dez ações aprovadas em `PropertiesController`).

### 4.6 Critérios objetivos de aceite

- [x] Build Release 0 erros/0 avisos.
- [x] Nenhuma migration criada ou alterada — tabela `property_owners` já existia desde o Checkpoint 1.
- [x] `Identity.Contracts` permanece sem dependência de ASP.NET Core/EF Core; `IIdentityUserEligibilityReader` implementado apenas em `Identity.Infrastructure`.
- [x] `PropertyManagement.Application` referencia somente `Identity.Contracts`, nunca `Identity.Application`/`Identity.Infrastructure`/`Identity.Api` — validado por teste de arquitetura dedicado.
- [x] Nenhum acesso de Property Management a `IdentityDbContext`/tabelas do schema `identity`/Redis de Identity.
- [x] Transação de escrita de Property Management nunca aberta enquanto a checagem de elegibilidade em Identity está em andamento.
- [x] Vínculo/desvínculo permitido em qualquer status de Imóvel; rotas `mine` retornam Imóveis de qualquer status.
- [x] Filtro ABAC (`OwnerUserId`) é parte da Query, não apenas do controller — validado por teste de arquitetura via reflexão.
- [x] Nenhuma rota aceita `ownerUserId` nas rotas `mine`; nenhuma consulta de dado pessoal do proprietário exposta.
- [x] Dois novos Integration Events, exatamente um por vínculo/desvínculo efetivamente confirmado, nenhum em rejeição, sem dados pessoais, outbox durável, exchange correto, schema isolado de `identity_messaging`.
- [x] Concorrência real validada com barreira explícita: vínculo concorrente do mesmo par (uma confirma, outra `PropertyOwnerAlreadyLinked`); desvínculo concorrente do mesmo vínculo (uma confirma, outra `PropertyOwnerNotLinked`).
- [x] Cenário de TOCTOU coberto de forma determinística (papel removido entre a checagem de elegibilidade e o commit — vínculo ainda confirma; acesso `mine` já reflete a perda do papel via novo token).
- [x] Perda do papel `PROPERTY_OWNER` bloqueia novo acesso `mine` (via token reemitido), mas nunca remove o vínculo já persistido; restauração do papel recupera o acesso.
- [x] Somente as dez ações administrativas aprovadas existem em `PropertiesController`; `MyPropertiesController` expõe somente as duas ações aprovadas.
- [x] Suíte de integração de Property Management aprovada em duas execuções consecutivas: 184/184 em ambas (após a correção do débito técnico registrado na Seção 3.4/4.11). Suíte de integração de Identity aprovada uma vez (Identity foi alterado neste checkpoint).
- [x] Débito técnico do Checkpoint 3/4 (teste HTTP de concorrência de Condomínios) corrigido nesta etapa, com autorização explícita do usuário e escopo estritamente limitado ao teste (Seção 4.11) — nenhum código de produção alterado.
- [x] Nenhum commit, push, tag ou merge realizado.

### 4.7 Arquivos criados

**Identity.Contracts**: `Authorization/IdentityRoleCodes.cs`, `IdentityUserEligibility.cs`, `IIdentityUserEligibilityReader.cs`.

**Identity.Infrastructure**: `Authorization/IdentityUserEligibilityReader.cs`.

**PropertyManagement.Application** (`Owners/`): `PropertyOwnerResult.cs`, `IPropertyOwnerReader.cs`, `IPropertyOwnerWriter.cs`, `ILinkPropertyOwnerExecutor.cs`, `IUnlinkPropertyOwnerExecutor.cs`, `LinkPropertyOwnerCommand.cs`, `LinkPropertyOwnerCommandValidator.cs`, `LinkPropertyOwnerCommandHandler.cs`, `UnlinkPropertyOwnerCommand.cs`, `UnlinkPropertyOwnerCommandHandler.cs`, `ListPropertyOwnersQuery.cs`, `ListPropertyOwnersQueryValidator.cs`, `ListPropertyOwnersQueryHandler.cs`; (`Properties/`): `ListMyPropertiesQuery.cs`, `ListMyPropertiesQueryValidator.cs`, `ListMyPropertiesQueryHandler.cs`, `GetMyPropertyDetailQuery.cs`, `GetMyPropertyDetailQueryHandler.cs`.

**PropertyManagement.Contracts**: `PropertyOwnerLinked.cs`, `PropertyOwnerUnlinked.cs`.

**PropertyManagement.Infrastructure** (`Persistence/`): `PropertyOwnerReader.cs`, `PropertyOwnerWriter.cs`, `LinkPropertyOwnerExecutor.cs`, `UnlinkPropertyOwnerExecutor.cs`, `UnlinkPropertyOwnerTenantAwareBehavior.cs`.

**PropertyManagement.Api**: `Contracts/{LinkPropertyOwnerRequest,PropertyOwnerResponse,PagedPropertyOwnerResponse}.cs`, `Controllers/MyPropertiesController.cs`.

**Tests**: `Identity.Tests.Integration/IdentityUserEligibilityReaderTests.cs`; `PropertyManagement.Tests.Unit/Application/Owners/{LinkPropertyOwnerCommandHandlerTests,UnlinkPropertyOwnerCommandHandlerTests,ListPropertyOwnersQueryHandlerTests,Fake*}.cs`, `Application/Properties/{ListMyPropertiesQueryHandlerTests,GetMyPropertyDetailQueryHandlerTests}.cs`; `PropertyManagement.Tests.Integration/{PropertyOwnerCommandHandlerTests,PropertyOwnerIntegrationEventsTests,PropertyOwnerEndpointsTests}.cs`; `ArchitectureTests/PropertyManagementMyPropertiesEndpointsArchitectureTests.cs`.

**Documentação**: `Documento 07` (seção 14 estendida com os dois novos eventos/routing keys, nova subseção 14.3); este documento (nova Seção 4).

### 4.8 Arquivos modificados

`IdentityCatalogSeed.cs` (literal `"PROPERTY_OWNER"` substituído por `IdentityRoleCodes.PropertyOwner`); `IdentityModuleExtensions.cs` (registro de `IIdentityUserEligibilityReader`); `PropertyManagement.Application.csproj` (referência a `Identity.Contracts`); `PropertyManagementErrorCodes.cs` (+4 códigos: `OwnerUserNotFound`, `OwnerUserNotEligible`, `PropertyOwnerAlreadyLinked`, `PropertyOwnerNotLinked`); `IPropertyReader.cs`/`PropertyReader.cs` (+`ListMineAsync`/`GetMineDetailAsync`); `PropertyManagementCommandDispatchExtensions.cs` (registro dos novos Commands/Queries/executores/behaviors); `PropertiesController.cs` (três ações administrativas novas de Ownership); `PropertyManagementResultHttpMapper.cs` (+4 códigos, 2 em 404 e 2 em 409); `Program.cs` (Host — duas novas rotas de evento); `PropertyManagementDependencyTests.cs`, `IdentityAuthorizationCatalogConsistencyTests.cs`, `PropertyManagementCondominiumsEndpointsArchitectureTests.cs`, `PropertyManagementPropertiesEndpointsArchitectureTests.cs`, `PropertyManagementSourceConventionTests.cs` (testes de arquitetura atualizados/novos).

### 4.9 Confirmação: nenhuma migration foi criada

Confirmado via inspeção direta do diretório `Persistence/Migrations/` (apenas a migration `InitialCreate` do Checkpoint 1, sem novos arquivos) e via teste de arquitetura dedicado (`PropertyManagementSourceConventionTests.Exactly_one_migration_exists_and_no_new_migration_was_created_this_checkpoint`, inalterado desde o Checkpoint 4 — não hardcoda o número do checkpoint). A tabela `property_management.property_owners` (incluindo a constraint `uq_property_owners_tenant_property_owner`) já existia desde o Checkpoint 1.

### 4.11 Correção do débito técnico registrado na Seção 3.4 (concorrência HTTP de Condomínios)

Autorizada explicitamente na aprovação parcial do Checkpoint 5, com escopo estritamente limitado a `CondominiumsEndpointsTests.Two_concurrent_updates_of_the_same_condominium_allow_only_one_to_succeed_with_409` e, quando indispensável, infraestrutura exclusiva de teste (`BuildHostAsync`). Nenhum código de produção foi alterado — investigação confirmou que `UpdateCondominiumExecutor`/`UpdateCondominiumCommandHandler` já continham exatamente o mesmo mecanismo, correto e já comprovado, de `UpdatePropertyExecutor`/`UpdatePropertyCommandHandler` (concorrência otimista via `xmin`, tradução exclusiva de `DbUpdateConcurrencyException` para `CondominiumConcurrencyConflict`, sem retry).

**Causa raiz**: o teste original despachava as duas requisições `PATCH` concorrentes via `Task.WhenAll`, sem qualquer ponto de sincronização, contra clientes do MESMO `TestServer` in-memory. Sem uma barreira real, nada garantia que as duas requisições lessem a mesma versão original (`xmin`) antes de uma delas já ter concluído leitura+escrita+commit — nada além de timing determinava se a segunda requisição colidiria (produzindo `409`) ou simplesmente processaria depois, sobre a versão já atualizada (produzindo `200`/`200`). O padrão equivalente para Imóveis já havia sido corrigido desta forma no Checkpoint 3 (`PropertiesEndpointsTests`); o de Condomínios nunca foi.

**Mecanismo determinístico aplicado**: mesmo padrão já usado com sucesso em `PropertiesEndpointsTests`/`PropertiesLifecycleEndpointsTests`/`PropertyOwnerEndpointsTests` (Checkpoints 3-5) — dois hosts `TestServer` **separados** (nunca dois clientes do mesmo host), cada um com `IPropertyAuditWriter` substituído por uma implementação que persiste a entrada de auditoria (idêntico ao `PropertyAuditWriter` real) e então bloqueia em um `Barrier(2)` compartilhado entre os dois hosts. Como `UpdateCondominiumCommandHandler` só chama `_auditWriter.Record(...)` depois de já ter lido a linha original e computado o diff, mas **antes** de `SaveChangesAsync` (que só ocorre depois que `operation()` retorna, dentro de `IPropertyManagementTransactionExecutor.ExecuteAsync`), a barreira garante que as duas requisições já leram a MESMA versão `xmin` original antes de qualquer uma prosseguir para a persistência — reproduzindo genuinamente a colisão, nunca por sorte de timing. A condomínio é semeado por um **terceiro** host/cliente, nunca um dos dois hosts com barreira (que travaria a própria criação esperando um segundo participante inexistente).

Nenhum `Task.Delay`, `Thread.Sleep`, timing probabilístico, ordem presumida do scheduler ou polling foi usado.

**Garantias verificadas pelo teste corrigido**:
1. as duas requisições leem a mesma versão original — garantido pela barreira antes do `SaveChangesAsync`;
2. ambas chegam ao ponto de persistência — garantido pelo `Barrier(2)` (exige exatamente 2 sinalizações);
3. a barreira libera as duas tentativas simultaneamente;
4. exatamente uma confirma (`responses.Count(r => r.StatusCode == OK) == 1`);
5. a outra retorna `409` — o corpo da resposta de conflito nunca expõe o código interno do erro (`PropertyManagementResultHttpMapper` retorna um `ProblemDetails{Title="conflict"}` genérico para todo código de conflito, mesmo padrão de toda resposta de conflito deste Bounded Context); a prova de que é especificamente `CondominiumConcurrencyConflict` — e não outro motivo de conflito — vem das asserções de banco abaixo: nenhuma outra causa de `409` é alcançável a partir de duas requisições `PATCH` válidas e sem colisão de código sobre o mesmo recurso;
6. sem retry — confirmado por inspeção direta de `UpdateCondominiumExecutor` (nenhum loop de repetição existe);
7. o estado persistido corresponde exatamente à operação vencedora — verificado lendo o Condomínio diretamente do PostgreSQL e comparando `Name` com o corpo da resposta vencedora;
8. existe somente uma auditoria `condominium_updated` — verificado via `PropertyAuditLog` real;
9. o evento `CondominiumUpdated` é enfileirado no MESMO trecho de código, dentro do MESMO bloco condicional e do MESMO commit de Unit of Work que a auditoria (`UpdateCondominiumCommandHandler`, ambos dentro de `if (changedFields.Count > 0)`) — uma única auditoria persistida é, portanto, prova por construção de código de que exatamente um evento foi enfileirado, nunca dois nem zero. A contagem direta do envelope no outbox não foi adicionada a este arquivo especificamente: `CondominiumsEndpointsTests` nunca registra RabbitMQ/regra de publicação (por desenho já existente, mesmo padrão de todo outro `*EndpointsTests.cs` deste Bounded Context — nenhum deles verifica contagem de envelope; isso é responsabilidade de `CondominiumIntegrationEventsTests`, que já cobre a persistência do envelope de `CondominiumUpdated` para o caso não-concorrente). Adicionar um container RabbitMQ real a este arquivo só para observar uma contagem de envelope seria infraestrutura além do indispensável para esta correção;
10. nenhum envelope, auditoria ou alteração de estado da tentativa perdedora — garantido pelo `catch (DbUpdateConcurrencyException)` de `UpdateCondominiumExecutor`, que limpa o `ChangeTracker` e drena o coletor de eventos do perdedor antes de retornar a falha traduzida.

**Evidência**:
- 10 execuções focadas consecutivas de `Two_concurrent_updates_of_the_same_condominium_allow_only_one_to_succeed_with_409`: 10/10 aprovadas.
- Classe completa `CondominiumsEndpointsTests`: 16/16 aprovados.
- Suíte completa de integração de Property Management, duas execuções consecutivas após a correção: 184/184 e 184/184.
- Build Release (`dotnet build IHostPro.sln -c Release`): 0 erros, 0 avisos.

**Arquivo alterado nesta correção**: apenas `tests/Contexts/PropertyManagement/IHostPro.Contexts.PropertyManagement.Tests.Integration/CondominiumsEndpointsTests.cs` (código de teste). `BuildHostAsync` passou a aceitar um parâmetro opcional `overrides` (mesma assinatura já usada por `PropertiesEndpointsTests`/`PropertiesLifecycleEndpointsTests`/`PropertyOwnerEndpointsTests`); adicionadas a classe privada `BarrierPropertyAuditWriter` e três métodos privados de verificação (`SetPostgresTenantAsync`/`CreateMigratorDbContextWithTenant`, mais duas `using` novas). Nenhum arquivo de produção (`src/`) foi tocado nesta rodada. Nenhuma migration foi criada ou alterada.

`git diff --check`: sem erros de espaço em branco (apenas avisos de conversão de final de linha LF→CRLF, pré-existentes na configuração do repositório, não introduzidos por esta alteração).

Nenhum commit, push, tag ou merge foi realizado.

### 4.12 Status desta etapa

**Checkpoint 5 — Ownership e leitura dos próprios Imóveis: Implementação concluída · Homologação concluída · Status aprovado · Débito técnico pré-existente do Checkpoint 3/4 (Seção 3.4/4.11) corrigido, com escopo estritamente limitado a código de teste · Duas execuções completas consecutivas da suíte de integração de Property Management aprovadas (184/184 em ambas) · Nenhum bloqueador pendente.**

---

## 5. Checkpoint 6 — Homologação final da Fase 2

### 5.1 Escopo desta etapa

Homologação final de ponta a ponta de toda a Fase 2 (Checkpoints 1-5, Property Management completo) contra um host real (não `TestServer` in-memory), incluindo composição simultânea com o contexto Identity & Access, mais a investigação e correção de cinco defeitos reais de composição do Wolverine descobertos durante essa homologação — nenhum dos quais havia sido exercitado pelos testes automatizados anteriores, todos eles em código de composição/infraestrutura (`Program.cs`, `DbContext`, executores de outbox, `MigrationRunner`), nunca em regra de negócio de Property Management ou Identity.

### 5.2 Contexto: por que estes defeitos não apareceram antes

Todos os testes de integração aprovados nos Checkpoints 1-5 (Seções 2-4) registram cada Bounded Context isoladamente, com Wolverine configurado com um único store (`AddDbContextWithWolverineIntegration` já correto nesses hosts de teste dedicados) ou sem RabbitMQ real. A homologação deste Checkpoint 6 foi a primeira vez que o `Program.cs` real e completo — 1 Main Store + 2 Ancillary Stores (Identity, Property Management), ambos os módulos registrados no mesmo processo, RabbitMQ real, dois dispatchers por contexto — foi exercitado de ponta a ponta. Os cinco defeitos abaixo são exclusivamente de composição da raiz (`Program.cs`) e do próprio framework Wolverine 6.22.0, nunca de lógica de aplicação/domínio de Property Management ou Identity — por isso nenhum teste unitário ou de integração por contexto (Seções 2-4 acima) os teria capturado.

### 5.3 Os cinco defeitos reais encontrados e corrigidos (nesta ordem)

1. **Ausência de Main Store do Wolverine**: `Program.cs` registrava apenas os dois Ancillary Stores (`identity_messaging`/`property_management_messaging`) sem nenhum Main Store — Wolverine exige um Main Store mesmo quando todo o tráfego passa por stores Ancillary. **Correção**: `platform_messaging` registrado como Main Store, schema isolado, sem tabela de domínio nenhuma — existe apenas para satisfazer o requisito estrutural do Wolverine.
2. **Conflito global de `ISender`**: o gerador de código-fonte do `Mediator` (`Mediator.SourceGenerator`) gera um tipo `Mediator.Mediator` por assembly; com Identity e Property Management registrados no mesmo host, a resolução de `ISender` por DI se tornava ambígua entre os dois. **Correção**: dois dispatchers próprios por contexto (`IIdentityRequestDispatcher`/`IPropertyManagementRequestDispatcher`), cada um resolvendo o `ISender` do assembly correto; todos os 8 controllers migrados para o dispatcher do seu próprio contexto.
3. **Envelopes persistidos inicialmente no Main Store em vez do Ancillary Store correto**: `IdentityDbContext`/`PropertyManagementDbContext` estavam registrados via `AddDbContext<T>()` simples (não `AddDbContextWithWolverineIntegration<T>()`), então `DbContext.IsWolverineEnabled()` retornava `false`, forçando o caminho de fallback ADO cru de `EfCoreEnvelopeTransaction`, que resolve o store sempre a partir de `context.Storage` (sempre o Main). **Correção**: `ModelBuilder.MapWolverineEnvelopeStorage(schemaName)` adicionado em `OnModelCreating` de ambos os `DbContext`, mapeando as tabelas de envelope no próprio modelo EF de cada contexto, atrelado ao schema correto.
4. **DELETE pós-publicação executado no store incorreto**: mesmo após a correção do item 3, o construtor de `Wolverine.Runtime.MessageBus` (herdado por `DbContextOutbox<T>`) fixa incondicionalmente `Storage = runtime.Storage` (o Main); `DelegatingMessageOutbox.DeleteOutgoingAsync` roteia o DELETE via `envelope.Store` — ou seja, toda confirmação pós-publicação (RabbitMQ) tentava apagar a linha do store errado, silenciosamente (0 linhas afetadas, sem exceção). **Correção**: `MessageContext.OverrideStorage(IMessageStore)` chamado no construtor de `IdentityOutboxTransactionExecutor`/`PropertyManagementOutboxTransactionExecutor`, resolvendo o store Ancillary correto via `IWolverineRuntime.FindAncillaryStoreForMarkerType`; falha rápida (`InvalidOperationException`) caso o cast para `MessageContext` alguma vez deixe de ser válido.
5. **Exchanges do RabbitMQ nunca provisionadas**: `AutoProvision=false` (nunca habilitado, por desenho: a API nunca deve criar topologia de broker) e `mandatory=false` fixo no `RabbitMqSender` fazem com que uma mensagem para uma exchange inexistente seja simplesmente descartada pelo broker, sem `BasicReturn`, sem exceção. **Correção**: `IHostPro.MigrationRunner` estendido para provisionar a topologia (`identity-events`, `property-management-events`, ambas topic/durável) de forma idempotente via `RabbitMqTransportExpression.DeclareExchange()` + `host.SetupResources()`, com verificação explícita pós-setup (`IBrokerEndpoint.CheckAsync()`) — a API continua nunca criando exchanges; essa responsabilidade é exclusiva do `MigrationRunner`.

Hipóteses investigadas e **descartadas** durante a investigação (não são débito técnico, não estão pendentes): ausência de agente de recuperação (`recovery agent`) nos Ancillary Stores; problema no `owner_id`; falha do `RetryBlock`; necessidade de reinício obrigatório da API; fila ausente como causa do DELETE incorreto.

### 5.4 Testes de regressão permanentes criados

`tests/Host/IHostPro.Api.Tests.Integration/WolverineThreeStoreCompositionTests.cs` — três fatos permanentes, todos contra o host real completo (`Program.cs` sem modificação):

1. `The_real_host_starts_with_both_dispatchers_and_processes_HTTP_requests_from_both_contexts_correctly` — 1 Main + 2 Ancillary Stores sobem corretamente; ambos os dispatchers processam requisições HTTP reais de Identity e Property Management simultaneamente.
2. `Each_context_transaction_executor_overrides_storage_to_its_own_ancillary_store_never_the_main_store` — resolve ambos os executores via DI real, lê `MessageContext.Storage` diretamente, confirma que cada um aponta para seu próprio Ancillary Store e nunca para o Main (regressão estrutural direta do defeito 4).
3. `MigrationRunner_provisions_rabbitmq_topology_idempotently_and_the_real_host_delivers_through_it` — confirma ausência das exchanges antes do provisionamento; executa o `MigrationRunner` duas vezes (idempotência); publica com filas de teste vinculadas às exchanges reais confirmando entrega e routing keys corretos; interrompe e restaura o RabbitMQ real, confirmando recuperação completa sem envelope perdido ou preso permanentemente (regressão direta dos defeitos 3, 4 e 5 em conjunto, sob indisponibilidade real do broker).

### 5.5 Ambiente de homologação E2E

Ambiente Docker efêmero de homologação reaproveitado sem recriação de containers (PostgreSQL `15432`, RabbitMQ `5672`/mgmt `15674`, Redis `6379`), API real iniciada com variáveis de ambiente explícitas (nunca `appsettings.Homolog.json`, removido nesta etapa — Seção 5.8). Bootstrap: `Identity:DevelopmentSeed` (Tenant A + Tenant B, um administrador cada, deliberadamente sem papel atribuído) seguido de uma única atribuição de papel via SQL direto (quebra do ciclo administrador-sem-papel, único passo fora da API oficial) — toda criação de usuário subsequente (operador, dois proprietários, um proprietário bloqueado, um usuário sem permissão) feita exclusivamente via API HTTP oficial.

### 5.6 Cenários E2E executados (representativos por seção, host real)

| Seção | Cenários | Resultado |
|---|---|---|
| Ambiente | Serviços saudáveis; API real inicia com 1 Main + 2 Ancillary Stores; `MigrationRunner` executado contra homolog (primeira aplicação real, exchanges confirmadas) | Correto |
| Autorização | Sem token→401; sem permissão→403; Admin com `PROPERTIES:MANAGE` acessa rotas administrativas; `PROPERTY_OWNER`+`PROPERTIES:READ:OWN_OWNER` acessa somente `mine`; owner não acessa rotas administrativas; Admin sem vínculo não ganha acesso a `mine`; usuário bloqueado não autentica; remoção do papel→403; restauração do papel recupera acesso ao vínculo preservado | 8/8 corretos (inclui descoberta de regra de negócio legítima — Seção 5.7) |
| Condomínios | Criação válida→201+Location; listagem/detalhe→200; update efetivo→200; no-op→200 com `UpdatedAt` inalterado; PATCH vazio→400; cross-tenant→404 | 6/6 corretos |
| Imóveis | Imóvel independente com endereço próprio; Imóvel em Condomínio herdando endereço; detalhe com `effectiveAddressSource` correto; código duplicado com casing diferente→409; PATCH presence-aware (campo omitido preserva; `address:null` remove endereço próprio quando Condomínio permanece; `condominiumId:null` remove vínculo quando endereço próprio permanece); estado final sem origem de endereço→400; cross-tenant→404 | 6/6 corretos |
| Lifecycle | Cadeia completa `Draft→Active→Inactive→Active→Inactive→Archived`; `Active→Archived` rejeitado (409, só `Inactive→Archived` é permitido); `Archived` terminal (ativar→409); PATCH em `Archived` (mesmo no-op)→409 | 8/8 transições corretas (5 confirmadas + 3 rejeições corretas) |
| Ownership | Vínculo de proprietário elegível→201 (sem alterar status do Imóvel); vínculo duplicado→409; proprietário bloqueado→409; usuário sem papel `PROPERTY_OWNER`→409; listagem administrativa sem nome/e-mail; proprietário vinculado acessa `mine` (lista+detalhe)→200; proprietário não vinculado→404 no detalhe; cross-tenant→404; desvínculo→204 (sem alterar status do Imóvel); acesso desaparece imediatamente; desvínculo repetido→404 | 11/11 corretos |

### 5.7 Descoberta durante a homologação (não é defeito)

Ao remover o papel `PROPERTY_OWNER` de `owner1` (que possuía apenas esse papel), a API retornou `409` em vez do `403` inicialmente esperado. Investigação do código-fonte (`RemoveRoleCommandHandler`) confirmou regra de negócio deliberada e documentada: o último papel de um usuário não pode ser removido (`UserMustHaveAtLeastOneRoleError`) — uma troca de papel deve primeiro atribuir o novo papel. Não é defeito; o cenário de teste foi ajustado (atribuição de um segundo papel antes da remoção), sem qualquer alteração em código de produção.

### 5.8 Verificação direta no PostgreSQL

Com `SET LOCAL app.tenant_id` explícito em toda consulta a tabela tenant-owned (evitando falso-negativo de RLS):

- **`property_management`**: isolamento por tenant confirmado; `normalized_code` sempre maiúsculo; status corretos em todos os 4 Imóveis de Tenant A; endereço próprio nunca copiado para Imóvel vinculado a Condomínio (`has_own_address = false` nos dois vinculados, `true` nos dois independentes); vínculo N:N (`property_owners`) refletindo exatamente o estado esperado após vínculo/desvínculo; nenhuma relação cross-tenant; `property_audit_log` é append-only (8 linhas para o Imóvel testado — criação, vínculo, update, 5 transições de lifecycle confirmadas — zero linhas para as 3 tentativas rejeitadas); `changed_fields` 100% em snake_case (`address`, `capacity`, `condominium_id`, `name`, `owner_user_id`, `status`).
- **Mensageria**: `platform_messaging` (Main) com zero envelopes de entrada/saída — nenhum evento de domínio jamais chega ao Main Store; `identity_messaging`/`property_management_messaging` com zero envelopes pendentes e zero *dead letters* com o broker saudável — confirma em produção real (não só no teste dedicado) que os defeitos 3 e 4 estão corrigidos; nenhuma contaminação cruzada entre schemas de mensageria.
- Confirmado por leitura de código (`LinkPropertyOwnerCommandHandler`, eventos em `PropertyManagement.Contracts`) que Property Management nunca referencia `IdentityDbContext`/tabelas do schema `identity` — a elegibilidade é obtida exclusivamente via `IIdentityUserEligibilityReader`, porta implementada dentro do próprio assembly de Identity.

### 5.9 Segurança e privilégios

Verificação representativa com a role de aplicação de menor privilégio (`ihostpro_app`, não o `ihostpro_migrator`):

- Tenant A não vê nenhuma linha de Tenant B (0 linhas vazadas em consulta direta).
- Ausência de `app.tenant_id` é fail-closed: 0 linhas retornadas, nunca erro, nunca vazamento.
- `CREATE TABLE`/`DROP TABLE` negados (`permission denied for schema`/`must be owner of table`).
- `UPDATE`/`DELETE` em `property_audit_log` negados (`permission denied for table`) — auditoria append-only garantida a nível de privilégio de banco, não apenas de aplicação.
- API nunca cria exchanges (confirmado por desenho: `AutoProvision=false`, nunca habilitado em `Program.cs`; responsabilidade exclusiva do `MigrationRunner`, Seção 5.3/item 3).
- Nenhuma credencial, chave privada ou valor de parâmetro SQL em texto claro nos logs da API (EF Core não registra valores de parâmetro — `sensitive data logging` desabilitado; parâmetros aparecem como `'?'`).

### 5.10 Varredura final

- `git status --short`/`git diff --check`: sem artefatos temporários fora do esperado (apenas este documento e o código já revisado); avisos de `git diff --check` são exclusivamente de normalização LF→CRLF pré-existente na configuração do repositório, não erros de espaço em branco reais.
- Nenhum `TODO`/`FIXME`/`HACK` nos 238 arquivos `.cs` novos/modificados desta Fase.
- Nenhuma credencial hardcoded real — as três ocorrências de `*Password = "..."` encontradas são constantes de teste (`KnownPassword`/`AppRolePassword`/`MigratorRolePassword`) para containers PostgreSQL efêmeros via Testcontainers, nunca credenciais de ambiente real.
- `appsettings.Homolog.json` confirmado removido de `src/Host/IHostPro.Api/`.
- Nenhum arquivo `.bak`/`.tmp`/`.orig` ou artefato de depuração no repositório.

### 5.11 Evidência de validação — suíte automatizada final

| Suíte | Total | Resultado |
|---|---|---|
| Build Release (`dotnet build IHostPro.sln -c Release`) | — | 0 erros, 0 avisos, todos os projetos |
| Arquitetura (`IHostPro.ArchitectureTests`) | 97 | 97/97 aprovados |
| Unitários (Identity) | 468 | 468/468 aprovados |
| Unitários (Property Management) | 180 | 180/180 aprovados |
| Unitários (BuildingBlocks) | 13 | 13/13 aprovados |
| Host — composição Wolverine (`WolverineThreeStoreCompositionTests`, 3 regressões permanentes) | 3 | 3/3 aprovados |
| Integração (Property Management — PostgreSQL + RabbitMQ + Identity reais) | 184 | 184/184 aprovados (execução única, conforme plano — nenhuma inconsistência) |
| Integração (Identity — PostgreSQL real) | 411 | 411/411 aprovados, 18 min 30 s (execução única, conforme plano) |

**Nota de execução**: durante a execução isolada dos 3 testes focados de Host, a suíte precisou da porta `5672` livre para seu próprio container RabbitMQ efêmero (Testcontainers), configurado deliberadamente com porta fixa para exercitar o `Program.cs` real sem modificação — o RabbitMQ do ambiente de homologação (também na porta `5672`) foi parado antes da execução e reiniciado imediatamente depois, sem qualquer impacto nos dados já coletados nas Seções 5.6-5.9 (todas concluídas antes desta pausa). Tratado como artefato de ambiente, não como defeito, conforme critério de tratamento de problemas definido para esta homologação.

### 5.12 Critérios objetivos de aceite

- [x] Build Release 0 erros/0 avisos.
- [x] Nenhuma migration nova criada nesta etapa.
- [x] Os cinco defeitos reais de composição do Wolverine identificados, corrigidos e cobertos por teste de regressão permanente.
- [x] Nenhuma hipótese descartada registrada como débito técnico pendente.
- [x] Host real (não `TestServer`) validado com 1 Main + 2 Ancillary Stores, dois dispatchers por contexto, RabbitMQ real.
- [x] Homologação E2E completa da Fase 2 (Autorização, Condomínios, Imóveis, Lifecycle, Ownership) sem nenhum novo defeito de produção.
- [x] Isolamento de tenant, fail-closed sem tenant, RLS e privilégios de menor acesso confirmados diretamente no PostgreSQL.
- [x] Auditoria append-only garantida a nível de privilégio de banco (não apenas de aplicação).
- [x] Nenhum dado sensível/pessoal em evento de domínio, log ou payload de mensageria.
- [x] `appsettings.Homolog.json` removido; nenhum artefato temporário remanescente.
- [x] Suíte automatizada final aprovada integralmente, sem repetição desnecessária de suítes já aprovadas.
- [x] Nenhum commit, push, tag ou merge realizado.

### 5.13 Arquivos criados/modificados especificamente pela investigação Wolverine desta etapa

**Atenção — escopo desta subseção**: as listas abaixo cobrem exclusivamente o que a investigação e correção dos cinco defeitos Wolverine (Seção 5.3) tocou diretamente. **Não são o inventário completo do incremento.** O incremento completo — toda a fundação do contexto Property Management (Checkpoints 1-5: Condomínios, Imóveis, lifecycle, Ownership, integração pública com Identity, testes e documentação) mais esta etapa — ainda não foi commitado e soma 242 arquivos (26 modificados + 1 removido + 215 novos). O inventário Git completo, correto e consolidado está na Seção 5.17.

**Criados** (escopo Wolverine): `tests/Host/IHostPro.Api.Tests.Integration/WolverineThreeStoreCompositionTests.cs` (3 regressões permanentes de composição — arquivo já existia com escopo diagnóstico mais amplo; consolidado a estas 3 permanentes nesta etapa, com o bloco de recuperação-por-reinício, diagnóstico de uma hipótese já descartada, removido); este documento (nova Seção 5).

**Documentação**: este documento (nova Seção 5).

### 5.14 Arquivos modificados especificamente pela investigação Wolverine desta etapa

`src/Contexts/Identity/IHostPro.Contexts.Identity.Infrastructure/Persistence/IdentityDbContext.cs` (defeito 3); `src/Contexts/PropertyManagement/IHostPro.Contexts.PropertyManagement.Infrastructure/Persistence/PropertyManagementDbContext.cs` (defeito 3); `src/Contexts/Identity/IHostPro.Contexts.Identity.Infrastructure/Persistence/IdentityOutboxTransactionExecutor.cs` (defeito 4); `src/Contexts/PropertyManagement/IHostPro.Contexts.PropertyManagement.Infrastructure/Persistence/PropertyManagementOutboxTransactionExecutor.cs` (defeito 4); `src/BuildingBlocks/IHostPro.BuildingBlocks.Infrastructure/Messaging/WolverineConfigurationExtensions.cs` (retorno de `UseIHostProRabbitMq` ampliado para reuso pelo `MigrationRunner`, defeito 5); `tools/IHostPro.MigrationRunner/Program.cs` (provisionamento de topologia, defeito 5); `tools/IHostPro.MigrationRunner/appsettings.json` (configuração `RabbitMq` padrão de desenvolvimento).

Os defeitos 1 (Main Store) e 2 (dispatchers por contexto) foram corrigidos em etapa anterior a este Checkpoint 6 (já registrados como aprovados antes do início desta homologação) — não repetidos aqui como novidade de arquivo, apenas listados na Seção 5.3 pela ordem cronológica de descoberta exigida.

### 5.15 Confirmação: nenhuma migration foi criada

Nenhuma alteração de schema foi necessária para nenhum dos cinco defeitos — todos são de composição Wolverine/DI (`OnModelCreating` apenas mapeia tabelas de envelope já geridas pelo próprio Wolverine, nunca uma migration EF) ou de infraestrutura externa ao banco (topologia RabbitMQ). Confirmado por inspeção direta dos diretórios `Persistence/Migrations/` de ambos os contextos (inalterados) e pela suíte de arquitetura (97/97, incluindo os testes dedicados de migration única por contexto).

### 5.16 Débitos técnicos remanescentes

Nenhum débito técnico novo foi introduzido nesta etapa. Um débito técnico pré-existente permanece deliberadamente não corrigido, por decisão já registrada em etapa anterior a este documento: `LogoutExecutor`/`RevokeOwnSessionExecutor` (Identity) ainda usam o padrão de retry anterior ao Checkpoint 6 de Identity, em que a última tentativa esgotada de `DbUpdateConcurrencyException` não drena `ISessionRevocationSignal` nesse caminho específico — impacto prático nulo hoje (o sinal é descartado junto com o escopo da requisição quando as tentativas se esgotam), correção já mapeada e explicitamente adiada para quando esses executores forem tocados por outro motivo autorizado.

### 5.17 Inventário Git completo do incremento (correção pós-relatório)

**Motivo desta seção**: o relatório funcional inicial deste Checkpoint 6 (Seções 5.13/5.14) listava apenas os arquivos tocados pela investigação Wolverine, o que foi corretamente identificado como incompleto para representar o incremento inteiro — o incremento completo (Checkpoints 1-6, nada commitado ainda) soma 242 arquivos, não 2. Esta seção consolida o inventário Git real e completo, coletado sem `git add` (nenhum arquivo foi movido para a staging area), como evidência definitiva antes do commit único.

**Totais exatos** (`git diff --name-status` + `git ls-files --others --exclude-standard`):

| Status | Total |
|---|---|
| Modificados (`M`) | 26 |
| Removidos (`D`) | 1 |
| Novos não rastreados (`??`) | 215 |
| **Total geral do incremento** | **242** |

**Verificação de artefatos**: os 215 arquivos novos foram inspecionados individualmente — nenhum `bin/`, `obj/`, `TestResults/`, cobertura, dump, log, `.trx`, arquivo publicado, arquivo temporário, `appsettings.Homolog.json`, `.env`, chave/credencial ou artefato de diagnóstico descartável entre eles (todos já filtrados por `--exclude-standard`, que respeita o `.gitignore` da raiz — `[Bb]in/`, `[Oo]bj/`, `TestResults/`, `*.trx`, `.env*`, `*.pfx`, `*.pem`, `*.key`). Uma única ocorrência de `appsettings.Homolog.json` existe no repositório, mas dentro de `src/Host/IHostPro.Api/bin/Debug/net10.0/` — artefato de build regenerado a cada compilação, já ignorado pelo Git, nunca elegível para commit; o arquivo-fonte permanece removido, conforme já confirmado na Seção 5.10. Nenhum arquivo foi removido nesta verificação — nenhum artefato temporário inequívoco foi encontrado para remover.

**Inventário agrupado (242 arquivos):**

| Grupo | Novos | Modificados | Removidos |
|---|---|---|---|
| Identity (Api/Application/Infrastructure/Contracts) | 7 | 11 | 1 |
| Identity — testes de integração | 1 | 2 | — |
| Property Management — Domain | 8 | — | — |
| Property Management — Application | 72 | — | — |
| Property Management — Contracts | 11 | — | — |
| Property Management — Infrastructure (inclui 3 arquivos de migration gerados automaticamente) | 33 | — | — |
| Property Management — Api | 25 | — | — |
| Property Management — testes unitários | 35 | — | — |
| Property Management — testes de integração | 14 | — | — |
| Host (`IHostPro.Api`) | — | 3 | — |
| Host — testes de composição Wolverine | 2 | — | — |
| MigrationRunner | — | 3 | — |
| BuildingBlocks | — | 1 | — |
| Solução (`IHostPro.sln`) | — | 1 | — |
| Testes de arquitetura | 6 | 4 | — |
| Documentação | 1 | 1 | — |
| **Total** | **215** | **26** | **1** |

**Detalhamento por grupo:**

- **Identity** — novos: `Identity.Application/{IIdentityRequestDispatcher,IdentityRequestDispatcher}.cs`, `Identity.Contracts/Authorization/{IdentityPermissionCodes,IdentityRoleCodes}.cs`, `Identity.Contracts/{IIdentityUserEligibilityReader,IdentityUserEligibility}.cs`, `Identity.Infrastructure/Authorization/IdentityUserEligibilityReader.cs`. Modificados: `Identity.Api/Authorization/IdentityAuthorizationExtensions.cs`, `Identity.Api/Controllers/{AuthController,PermissionsController,RolesController,UserAdministrationController,UsersController}.cs`, `Identity.Application/IdentityApplicationMediatorExtensions.cs`, `Identity.Infrastructure/IdentityModuleExtensions.cs`, `Identity.Infrastructure/Persistence/{IdentityDbContext,IdentityOutboxTransactionExecutor}.cs`, `Identity.Infrastructure/Seed/IdentityCatalogSeed.cs`. Removido: `Identity.Application/Authorization/IdentityPermissionCodes.cs` (movido para `Identity.Contracts/Authorization/`, recriado como novo acima).
- **Identity — testes de integração** — novo: `IdentityUserEligibilityReaderTests.cs`. Modificados: `IdentityAuthorizationExtensionsTests.cs`, `PermissionAuthorizationEndToEndTests.cs`.
- **Property Management — Domain** (8 novos): `Condominium.cs`, `Enums/PropertyStatus.cs`, `Property.cs`, `PropertyAuditEntry.cs`, `PropertyOwnerLink.cs`, `ValueObjects/{Address,PropertyCode}.cs`, `.csproj`.
- **Property Management — Application** (72 novos): pastas `Condominiums/` (12 arquivos), `Owners/` (13), `Properties/` (28), mais `AssemblyReference.cs`, `Errors/PropertyManagementErrorCodes.cs`, `IIntegrationEventCollector.cs`, `IPropertyAuditWriter.cs`, `IPropertyManagementRequestDispatcher.cs`, `IPropertyManagementTransactionExecutor.cs`, `Optional.cs`, `PropertyManagementApplicationMediatorExtensions.cs`, `PropertyManagementRequestDispatcher.cs`, `.csproj`.
- **Property Management — Contracts** (11 novos): `AssemblyReference.cs`, `CondominiumCreated.cs`, `CondominiumUpdated.cs`, `PropertyActivated.cs`, `PropertyArchived.cs`, `PropertyCreated.cs`, `PropertyDeactivated.cs`, `PropertyOwnerLinked.cs`, `PropertyOwnerUnlinked.cs`, `PropertyUpdated.cs`, `.csproj`.
- **Property Management — Infrastructure** (33 novos): pasta `Persistence/` completa (leitores/escritores/executores/behaviors de Condomínio, Imóvel, lifecycle e Ownership; `Mappings/` com 4 arquivos de configuração EF; `Migrations/` com os 3 arquivos gerados automaticamente pelo EF Core — `20260730024157_InitialCreate.cs`/`.Designer.cs`/`PropertyManagementDbContextModelSnapshot.cs`, já identificados e aprovados como a única migration do contexto, Checkpoint 1), mais `PropertyManagementCommandDispatchExtensions.cs`, `PropertyManagementModuleExtensions.cs`, `.csproj`.
- **Property Management — Api** (25 novos): `AssemblyReference.cs`, `Contracts/` (14 DTOs de request/response), `Controllers/{CondominiumsController,MyPropertiesController,PropertiesController}.cs`, `Http/` (5 arquivos — identidade autenticada, conversores JSON `Optional<T>`, leitor de claims, mapeador HTTP de resultado), `.csproj`.
- **Property Management — testes unitários** (35 novos): `Application/{Condominiums,Owners,Properties}/` (handlers + fakes), `Domain/` (4 arquivos de teste de agregado/VO), `Infrastructure/FixedTimeProvider.cs`, `.csproj`.
- **Property Management — testes de integração** (14 novos): `CondominiumCommandHandlerTests.cs`, `CondominiumIntegrationEventsTests.cs`, `CondominiumsEndpointsTests.cs`, `PropertiesEndpointsTests.cs`, `PropertiesLifecycleEndpointsTests.cs`, `PropertyCommandHandlerTests.cs`, `PropertyIntegrationEventsTests.cs`, `PropertyLifecycleCommandHandlerTests.cs`, `PropertyLifecycleIntegrationEventsTests.cs`, `PropertyManagementFoundationTests.cs`, `PropertyOwnerCommandHandlerTests.cs`, `PropertyOwnerEndpointsTests.cs`, `PropertyOwnerIntegrationEventsTests.cs`, `.csproj`.
- **Host** — modificados: `IHostPro.Api.csproj`, `Program.cs`, `appsettings.json`.
- **Host — testes de composição Wolverine** (2 novos): `WolverineThreeStoreCompositionTests.cs` (3 regressões permanentes, Seção 5.4), `.csproj`.
- **MigrationRunner** — modificados: `IHostPro.MigrationRunner.csproj`, `Program.cs` (provisionamento de topologia, defeito 5), `appsettings.json`.
- **BuildingBlocks** — modificado: `WolverineConfigurationExtensions.cs` (defeito 5).
- **Solução** — modificado: `IHostPro.sln` (referências dos novos projetos Property Management).
- **Testes de arquitetura** — novos: `PropertyManagementCondominiumsEndpointsArchitectureTests.cs`, `PropertyManagementDependencyTests.cs`, `PropertyManagementMyPropertiesEndpointsArchitectureTests.cs`, `PropertyManagementPropertiesEndpointsArchitectureTests.cs`, `PropertyManagementPropertiesLifecycleArchitectureTests.cs`, `PropertyManagementSourceConventionTests.cs`. Modificados: `IHostPro.ArchitectureTests.csproj`, `IdentityAuthorizationCatalogConsistencyTests.cs`, `IdentityCatalogEndpointsArchitectureTests.cs`, `IdentityUserAdministrationEndpointsArchitectureTests.cs`.
- **Documentação** — novo: este documento (`Fase 2 - Property Management - Validacao e Homologacao.md`, criado no Checkpoint 3 e mantido desde então). Modificado: `Documento 07 — Catálogo de Eventos de Domínio` (estendido nos Checkpoints 3-5 com os nove eventos de Property Management; inalterado nesta etapa — nenhum evento novo neste checkpoint, Seção 5.7).

**`git diff --check`**: sem erros reais de espaço em branco (apenas avisos de normalização LF→CRLF, pré-existentes na configuração do repositório, não introduzidos por este incremento).

### 5.18 Mensagem de commit (corrigida — representa todo o incremento)

A mensagem sugerida originalmente na Seção 5 descrevia apenas as correções Wolverine, mas o commit único cobre a Fase 2 — Incremento 1 completo. Mensagem corrigida:

```
feat(property-management): implementa contexto inicial de imóveis e condomínios

Implementa a fundação do bounded context Property Management, incluindo
Condomínios, Imóveis, lifecycle, Ownership, rotas administrativas e
self-service, RLS, auditoria e eventos transacionais.

Adiciona integração pública de elegibilidade com Identity, dispatchers
específicos por contexto e composição Wolverine com Main e Ancillary
Stores isolados.

Provisiona a topologia RabbitMQ pelo MigrationRunner e adiciona testes de
regressão para dispatch, outbox, stores e recuperação após indisponibilidade
do broker.
```

### 5.19 Status desta etapa

**Checkpoint 6 — Homologação final da Fase 2: Investigação e correção dos cinco defeitos reais de composição do Wolverine concluída · Testes de regressão permanentes criados e aprovados · Homologação E2E completa da Fase 2 (Checkpoints 1-5) concluída sem novo defeito de produção · Suíte automatizada final aprovada integralmente · Nenhum débito técnico novo · Inventário Git completo consolidado (242 arquivos: 26 modificados, 1 removido, 215 novos) — Seção 5.17 · Mensagem de commit corrigida para representar o incremento completo — Seção 5.18 · Nenhum bloqueador identificado · Aprovado tecnicamente pelo usuário · Aguardando autorização final para o commit único.**
