# ADR-015 — Isolamento do Processamento de Mensagens Housekeeping da Integração EF Core do Wolverine

Status: Aceito
Data: 2026-08-11

## Contexto

Housekeeping (Fase 6) segue o modelo de multi-tenancy já estabelecido pelo projeto desde a Fase 1 (ADR-003): `ITenantContext` escopado por request/mensagem, `BaseDbContext` com Global Query Filter tenant-aware (`entity.TenantId == _tenantContext.TenantId`, fail-closed quando não resolvido), e Row-Level Security no PostgreSQL via `SET LOCAL app.tenant_id`. Esse modelo já está em produção para Identity, Property Management, Reservations e Configuration.

Wolverine 6.22 é o backbone de mensageria (ADR-004), responsável por transporte (RabbitMQ), durable inbox/outbox e retry/redelivery. `IHostPro.Worker/Program.cs` precisa registrar `PersistMessagesWithPostgresql` (Main store), `EnrollAncillaryPostgresqlOutbox<HousekeepingDbContext>` (Ancillary store) e `UseEntityFrameworkCoreTransactions()` — as três chamadas juntas são estritamente necessárias para que `IDbContextOutbox<HousekeepingDbContext>` seja registrado no container; sem `UseEntityFrameworkCoreTransactions()`, a resolução do handler falha com `System.NotSupportedException: Cannot build service type IIntegrationEventHandler<...>` (Defeito A, corrigido — Checkpoint 6).

Corrigido o Defeito A, uma homologação real (`ReservationCancelledWorkerRoundTripTests`, RabbitMQ real, Postgres real, Worker real subprocess) revelou um segundo defeito: durante o processamento real de uma mensagem pela cadeia Wolverine, `HousekeepingDbContext` é materializado com uma instância de `ITenantContext` **diferente** da que `TenantResolutionMiddleware` efetivamente resolveu para aquela mensagem — confirmado por instrumentação temporária de identidade de objeto (hash codes), removida após a coleta de evidência:

```
[DIAG-EXECUTOR]  tenantContext hash=54333391 isResolved=True  tenantId=<real>
[DIAG-DBCONTEXT-CTOR] tenantContext hash=5998118  isResolved=False tenantId=<vazio>
```

O efeito funcional: o filtro global de tenant do EF Core (fail-closed por design) produz `WHERE FALSE` em toda query contra `HousekeepingDbContext`, e a reação a `ReservationCancelled` silenciosamente não encontra nem cancela a Cleaning vinculada — sem exceção, sem log de erro.

Investigação por API pública (documentação oficial + reflection pública sobre `Wolverine.EntityFrameworkCore.dll`/`Wolverine.dll` 6.22.0 instalados) confirmou: sempre que `HousekeepingDbContext` é alcançável, mesmo transitivamente, a partir do grafo de dependências de construtor que o Wolverine resolve para uma cadeia de mensagem, o Wolverine intercepta sua construção através de seu próprio mecanismo de persistência (`Wolverine.EntityFrameworkCore.Internals.DbContextUsageSource<T>`/`UntrackedDbContextDiscovery`, tipos públicos confirmados por reflection), independentemente de como o `IDbContextOutbox` é conectado a esse DbContext.

## Alternativas Rejeitadas

Quatro alternativas foram avaliadas; três foram implementadas como experimentos controlados, testadas com evidência real (Worker real, RabbitMQ real, Postgres real) e revertidas integralmente após refutação:

1. **Remover `UseEntityFrameworkCoreTransactions()` do Worker.** Refutada: o Defeito A original volta integralmente (`Cannot build service type IIntegrationEventHandler<...>`). Essa chamada é estritamente necessária para o registro de `IDbContextOutbox<HousekeepingDbContext>`, não uma causa do Defeito B.
2. **Acessar `IDbContextOutbox<HousekeepingDbContext>.DbContext`** em vez de um `HousekeepingDbContext` injetado diretamente no construtor (adapter fino). Refutada: `IDbContextOutbox<T>.DbContext` retorna exatamente a mesma instância que o Wolverine já materializa — não existem dois caminhos de resolução; existe um único `HousekeepingDbContext` por mensagem, gerenciado inteiramente pelo Wolverine, com o mesmo defeito de identidade.
3. **`[NonTransactional]` + `IDbContextOutbox` não genérico + `Enroll(dbContext)` dentro da própria cadeia Wolverine**, com `HousekeepingDbContext` ainda resolvido como parâmetro de construtor puro dentro do grafo do handler chain. Refutada: resultado idêntico às tentativas anteriores — `SET LOCAL` usa o tenant correto, mas as queries do `HousekeepingDbContext` continuam com `WHERE FALSE`. Confirma que a mera presença de `HousekeepingDbContext` como parâmetro de construtor alcançável pelo Wolverine, independente da forma de conexão com o outbox, já é suficiente para acionar a materialização divergente.
4. **Wolverine Conjoined Tenancy** (`AddDbContextWithWolverineManagedConjoinedTenancy`, `ConjoinedTenancyOptions`, `TenantStampingInterceptor`) — mecanismo nativo de multi-tenancy do Wolverine, descoberto durante a investigação mas **não implementado**. Rejeitada nesta fase por decisão explícita: substituiria/sobreporia responsabilidades arquiteturais já estabelecidas (`ITenantContext`, `BaseDbContext`, filtro de query EF, RLS PostgreSQL, `SET LOCAL`) em vez de corrigir localmente o processamento de mensagens do Housekeeping. Qualquer avaliação formal exigiria comparação de segurança, RLS, migrations, schema, API, Worker e impacto nas Fases 1–5 — fora do escopo desta correção pontual.

## Decisão

Wolverine e Housekeeping passam a ter fronteiras de responsabilidade estritamente separadas para mensagens consumidas:

**Wolverine é responsável por:** transporte (RabbitMQ), retry/error handling, routing, metadados de envelope/mensagem — **não** por um durable inbox para estes consumers especificamente; ver nota de precisão abaixo.

**Housekeeping é responsável por:** escopo de execução por tenant, `HousekeepingDbContext`, RLS, transação de negócio, audit, idempotência de negócio, outbox transacional de saída.

Nenhum `HousekeepingDbContext` (nem qualquer serviço que o exponha como parâmetro de construtor — `IHousekeepingTransactionExecutor`, `IHousekeepingAuditWriter`, os processors de negócio) fica visível no grafo de dependências resolvido pelos entrypoints Wolverine. Cada adapter Wolverine (`ReservationCancelledHandler`, `ReservationCreatedHandler`, `PropertyCreatedHandler`, `PropertyActivatedHandler`, `PropertyDeactivatedHandler`, `PropertyArchivedHandler`) depende exclusivamente de `IHousekeepingMessageExecutionScope` (mais `MessageContext`/`CancellationToken`, elementos puramente de transporte).

`IHousekeepingMessageExecutionScope` (única implementação autorizada a reter `IServiceScopeFactory` em todo o Housekeeping — confirmado, não apenas pretendido, por `HousekeepingMessageExecutionScopeArchitectureTests`, um teste de arquitetura NetArchTest que falha o build caso qualquer outra classe do Housekeeping passe a depender de `IServiceScopeFactory`) abre um child scope Microsoft DI comum por mensagem, resolve `ITenantContext` **desse** scope e o resolve para o `tenantId` explícito (obtido do próprio contrato `IntegrationEvent.TenantId`, nunca do `ITenantContext` ambiente que o Wolverine mutou) **antes** de resolver o processor de negócio (`IIntegrationEventHandler<TMessage>`, já existente, reaproveitado sem alteração). `HousekeepingDbContext`/o transaction executor, resolvidos depois, a partir do mesmo child scope, observam portanto a mesma instância de `ITenantContext` — inteiramente fora da resolução de DI por-mensagem do Wolverine, que é onde a divergência foi rastreada.

O `MessageId` do envelope Wolverine (`Envelope.Id`, API pública) é capturado pelo adapter e propagado ao execution scope para diagnóstico/correlação/testes de redelivery futuros — sem duplicar o outbox/inbox do Wolverine e sem persistir o envelope completo.

`PolicyUpdated`/`PolicyUpdatedCacheInvalidation` permanece com o desenho atual (não depende de `DbContext`, não sofre o defeito) — não migrado para este boundary.

### Precisão: `opts.CodeGeneration.AlwaysUseServiceLocationFor<IHousekeepingMessageExecutionScope>()` não é um Service Locator na aplicação

Esta chamada, registrada uma única vez em `IHostPro.Worker/Program.cs`, é **configuração de codegen do Wolverine no composition root** (API pública, `JasperFx.CodeGeneration.GenerationRules`) — instrui o gerador de código do Wolverine a resolver `IHousekeepingMessageExecutionScope` via `IServiceProvider.GetService` em vez de tentar inline-construí-lo estaticamente (necessário porque `HousekeepingMessageExecutionScope` depende de `IServiceScopeFactory`, um `Singleton`, e o codegen estrito do Wolverine rejeita isso por padrão com `InvalidServiceLocationException`). Isso **não introduz o anti-padrão Service Locator na aplicação**: nenhum código de Housekeeping (Domain/Application/Infrastructure/Api) chama `IServiceProvider.GetService`/`GetRequiredService` fora do próprio `HousekeepingMessageExecutionScope` (que já precisa de `IServiceScopeFactory.CreateAsyncScope()` para abrir seu child scope, de qualquer forma) — é uma instrução de geração de código pontual, escopada a exatamente um tipo, nunca uma prática de resolução de dependências em tempo de execução espalhada pela aplicação. Confirmado: nenhum outro chain Wolverine (`PolicyUpdated` incluso) tem sua verificação estrita de codegen enfraquecida por esta chamada.

### Precisão: o "durable inbox" do Wolverine não se aplica a estes consumers — descoberto na homologação real (Checkpoint 6)

Investigação real (Fase 6, Checkpoint 6, §10.8 do documento de homologação) descobriu que `opts.UseEntityFrameworkCoreTransactions()` — necessária, globalmente, para todo este boundary — configura *todo* listener RabbitMQ do processo (`housekeeping.reservation-projection`, `housekeeping.property-projection`, e também `configuration.policy-updated` da Fase 5, sob a mesma composição) como `EndpointMode.Inline`, nunca `Durable`. Confirmado por inspeção pública de `IWolverineRuntime.Options.Transports.AllEndpoints()` e por polling real contra `wolverine_incoming_envelopes` (nenhuma linha jamais aparece para estes consumers).

**Consequência real, não teórica**: não existe uma tabela de inbox durável no Postgres protegendo estes consumers contra redelivery — a única rede de segurança é (a) o próprio RabbitMQ redeliverando uma mensagem cujo ack nunca chegou, e (b) idempotência de domínio. Isso foi comprovado empiricamente em `ReservationCancelledRedeliveryTests`: uma redelivery real do MESMO envelope Wolverine, após o processamento bem-sucedido do original, **é aceita e reprocessada** pelo handler (não rejeitada por nenhum mecanismo de deduplicação do Wolverine) — só não produz efeito duplicado porque a query da reação (`status IN ('Pending','Assigned')`) não encontra mais nada para agir. **Decisão do usuário**: `Inline` é aceito como o comportamento correto/atual; nenhuma mudança de configuração de mensageria foi feita. Isto substitui a formulação anterior deste ADR ("Wolverine é responsável por... durable inbox"), que presumia incorretamente que o inbox durável protegia estes consumers.

**Precisão sobre atomicidade**: em modo `Inline` não existe uma tabela de inbox para formar transação conjunta com a transação de negócio — não há uma escrita de inbox para compor com nada. O que de fato ocorre: a mensagem é processada de forma síncrona dentro da própria operação de recebimento (transporte → adapter → `IHousekeepingMessageExecutionScope` → transação de negócio), e o ack ao RabbitMQ só é enviado **depois** que essa transação de negócio já comitou com sucesso. Isso é o que torna a redelivery possível (crash/desconexão entre o commit e o ack) e é exatamente por isso que a idempotência de domínio, não um inbox durável, é a proteção real.

## Consequências

### Positivas
- Preserva a arquitetura RLS/`ITenantContext`/`BaseDbContext` já existente sem alteração, para Housekeeping e para todos os demais contextos.
- Não migra Fases 1–5 para o modelo de tenancy do Wolverine.
- Wolverine continua responsável apenas por durabilidade de transporte — seu papel original.
- Housekeeping controla integralmente seu próprio escopo de tenant e sua própria fronteira transacional.
- `HousekeepingDbContext` nunca é materializado pela cadeia de resolução do Wolverine — boundary explícito, testável por arquitetura (NetArchTest) e por prova de identidade de objeto em ambiente real.

### Riscos Aceitos
- **Não existe garantia de "exactly-once" entre o transporte RabbitMQ e a transação de negócio do Housekeeping — confirmado real, não apenas teórico (Checkpoint 6).** Estes consumers rodam em `EndpointMode.Inline` (consequência de `UseEntityFrameworkCoreTransactions()` ser global ao processo — ver nota de precisão acima), sem inbox durável no Postgres. Uma redelivery real do mesmo envelope, após o processamento original já ter sido concluído com sucesso, É aceita e reprocessada pelo handler — comprovado em `ReservationCancelledRedeliveryTests` via redelivery real do mesmo envelope Wolverine. **Handlers de negócio devem ser idempotentes por invariante de domínio** (já o caso: `ReservationCancelled` é idempotente por construção — uma Cleaning já `Cancelled` não gera efeito duplicado nem `CleaningCancelled` duplicado; confirmado também de forma isolada do transporte em `HousekeepingEventProjectionTests`). Decisão do usuário: aceito como está, sem introduzir uma tabela de mensagens processadas — nenhum caso concreto de efeito não-idempotente foi encontrado.
- Um child DI scope deliberado por mensagem introduz `IServiceScopeFactory` em um único ponto do Housekeeping — superfície nova, mitigada por teste de arquitetura restringindo seu uso a essa única classe (`HousekeepingMessageExecutionScopeArchitectureTests`, implementado e verde).
- Mais código de integração do que o caminho "feliz" padrão do Wolverine (handler direto). Aceito como custo necessário para preservar o modelo de tenancy/RLS já estabelecido.

## Referências
- `documentacao do projeto/Architecture Principles.md`, Seções 7 (Multi-Tenant), 11 (Isolamento do Wolverine)
- ADR-003 (Persistência e Multi-Tenant), ADR-004 (Arquitetura Orientada a Eventos)
- `Fase 6 - Housekeeping - Validacao e Homologacao.md` — narrativa cronológica completa do Defeito A, Defeito B, três experimentos refutados, esta decisão e a generalização/gate de mensageria completo (§10.1–§10.9)
- `IHousekeepingMessageExecutionScope.cs`, `HousekeepingMessageExecutionScope.cs`, `ReservationCancelledHandler.cs`, `ReservationCreatedHandler.cs`, `PropertyCreatedHandler.cs`, `PropertyActivatedHandler.cs`, `PropertyDeactivatedHandler.cs`, `PropertyArchivedHandler.cs`
- `HousekeepingMessageExecutionScopeArchitectureTests.cs`, `HousekeepingWolverineDiscoveryTests.cs`, `ReservationCancelledRedeliveryTests.cs`, `HousekeepingListenerDurabilityModeTests.cs` (nome corrigido — o teste prova que os listeners NÃO são duráveis, o nome anterior, `HousekeepingDurableInboxTests`, sugeria o oposto), `HousekeepingOutboxOutageRecoveryTests.cs`, `CleaningCancelledRoutingParityTests.cs`, `PolicyUpdatedRegressionTests.cs`, `HousekeepingWolverineAdapterTests.cs` — evidência real de todos os pontos afirmados neste ADR
- Reflection pública sobre `Wolverine.EntityFrameworkCore.dll`/`Wolverine.dll` 6.22.0 (`IDbContextOutbox`/`IDbContextOutbox<T>`, `NonTransactionalAttribute`/`TransactionalAttribute`, `DbContextUsageSource<T>`, `ConjoinedTenancyOptions`, `EndpointMode`, `IWolverineRuntime.Options.Transports.AllEndpoints()`) — evidência da investigação, não parte da API pública utilizada na solução final
