# ADR-020 — Isolamento de Handler Chains do Wolverine para Fan-out entre Bounded Contexts

Status: Aceito
Data: 2026-08-18

## Contexto

**Descoberta (Fase 9, Checkpoint 1 — Comunicação e Integrações do MVP).** Durante a implementação de Communication (quarto Bounded Context a consumir `ReservationCreated` no mesmo processo `IHostPro.Worker`, junto de Housekeeping, Dashboard e Workflow), um teste real de transporte já homologado
(`CreateCleaningForReservationWorkflowRoundTripTests.ReservationCancelled_racing_the_in_flight_command_over_real_transport_never_leaves_an_active_automated_Cleaning`) passou a falhar de forma reproduzível. A investigação inicial suspeitou de uma colisão de nome de classe C# (as quatro Bounded Contexts nomeavam seu próprio adapter Wolverine `ReservationCreatedHandler`) — renomear a classe de Communication não alterou o comportamento, provando que a hipótese estava incorreta.

A causa real, confirmada por evidência estrutural direta contra o próprio modelo compilado do Wolverine (`HandlerGraph`, obtido via `host.GetRuntime().Handlers`, API pública do namespace `Wolverine.Tracking`) e por um teste de transporte real (RabbitMQ + Postgres + subprocesso `IHostPro.Worker.dll` reais): **por padrão, o Wolverine combina TODOS os handlers descobertos para o mesmo tipo CLR de mensagem em uma única `HandlerChain`, independentemente de quantas filas/listeners distintos estejam configurados para esse tipo.** Isso é comportamento documentado do próprio Wolverine (`WolverineOptions.MultipleHandlerBehavior`, padrão `Combined`; ver também a funcionalidade "Sticky Handler" introduzida na versão 3.0, [issue JasperFx/wolverine#801](https://github.com/JasperFx/wolverine/issues/801)) — não um bug do Wolverine, mas um comportamento surpreendente e não intencional nesta arquitetura, onde cada Bounded Context possui sua própria fila RabbitMQ dedicada e espera processar sua própria lógica de forma independente.

Consequência observada: com `ReservationCreated` fan-out para quatro filas reais (`housekeeping.reservation-projection`, `dashboard.reservation-projection`, `workflow.reservation-created-trigger`, `communication.reservation-created-trigger`), a primeira entrega processada por QUALQUER uma das quatro filas executava, corretamente, os handlers de TODAS as quatro Bounded Contexts (a cadeia combinada). Cada entrega SUBSEQUENTE das outras três filas — cópias físicas independentes da mesma mensagem, fruto do fan-out do próprio RabbitMQ — re-executava a MESMA cadeia combinada, repetindo a lógica de negócio de todas as quatro Bounded Contexts. Como a inserção da projeção de Dashboard (`dashboard.reservation_projection`) não é idempotente, a segunda execução violava a chave primária (`23505: duplicate key value violates unique constraint "PK_reservation_projection"`), e o Wolverine, ao capturar essa exceção, movia a mensagem real para a fila de erro (dead-letter) — perdendo silenciosamente entregas destinadas a outras Bounded Contexts.

Auditoria completa do código real (Fase 9, Checkpoint 1, investigação corretiva) identificou **16 tipos de Integration Event** com esse mesmo risco estrutural no `IHostPro.Worker`, muito além de `ReservationCreated`: `ReservationCreated`, `ReservationCancelled` (Housekeeping + Dashboard + Workflow / Housekeeping + Dashboard); os quatro eventos de `Property` — `PropertyCreated`, `PropertyActivated`, `PropertyDeactivated`, `PropertyArchived` (Housekeeping + Dashboard); e os dez eventos de ciclo de vida de `Cleaning` — `CleaningCreated`, `CleaningAssigned`, `CleaningInTransit`, `CleaningStarted`, `CleaningInspectionStarted`, `CleaningCompleted`, `CleaningInterrupted`, `CleaningNeedsHelp`, `CleaningNeedsMaterial`, `CleaningCancelled` (Reservations/Agenda + Dashboard). Esse defeito é, portanto, **anterior a Communication e transversal a Fases 6, 7 e 8** — Communication apenas o tornou determinístico ao adicionar um quarto consumidor a `ReservationCreated`; nada nesta investigação indica que o defeito nunca tenha se manifestado antes (o mecanismo depende da ordem de chegada das entregas concorrentes ao processo, não do número de consumidores por si só).

`IHostPro.Api` foi auditado separadamente e confirmado como não afetado: seu próprio `UseWolverine` usa `listen: false` e não registra nenhum handler Wolverine — publica eventos, nunca os consome.

## Dois problemas distintos, não confundir

**Problema anterior (já homologado) — colisão de resolução de DI.** `IIntegrationEventHandler<T>` podia possuir múltiplos registros não-nomeados no container de DI (ex.: `PolicyUpdated`), fazendo com que `GetRequiredService<IIntegrationEventHandler<T>>()` resolvesse de forma não-determinística qualquer que fosse o último registro. Solução já homologada: DI nomeada (`[FromKeyedServices]`) por Bounded Context.

**Problema atual (corrigido por este ADR) — combinação de handler chains do Wolverine.** O Wolverine descobre múltiplas CLASSES de handler distintas para o mesmo tipo CLR de mensagem (uma por Bounded Context, cada uma seu próprio adapter Wolverine, cada uma com seu próprio `Handle(...)`) e as combina, por padrão, em uma única cadeia lógica de execução — isso ocorre ANTES e INDEPENDENTEMENTE de qualquer resolução de DI dentro de cada handler individual.

**`keyed DI != handler-chain isolation`.** Nomear registros de DI resolve corretamente qual implementação de `IIntegrationEventHandler<T>` é injetada DENTRO de um único handler Wolverine; não impede — nem tem qualquer relação com — o Wolverine combinar handlers Wolverine DIFERENTES (classes de adapter diferentes) em uma cadeia compartilhada. Ambos os mecanismos continuam necessários enquanto a arquitetura mantiver múltiplos handlers Wolverine reais para o mesmo tipo de evento.

## Alternativas Consideradas

1. **`MultipleHandlerBehavior.Separated` global** (`opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated`). Rejeitada como primeira escolha: aplicar essa configuração afeta TODOS os tipos de mensagem no processo, não apenas os identificados como em risco, sem prova prévia de que não introduziria fan-out local adicional, filas automáticas indesejadas, ou mudança de topologia — risco desnecessário quando uma alternativa mais granular está disponível e foi confirmada como suficiente.
2. **Renomear as classes adapter para nomes únicos por Bounded Context.** Rejeitada, com prova empírica: já tentada como primeira hipótese (Communication), não teve nenhum efeito no defeito real — a combinação do Wolverine é indexada pelo TIPO da mensagem, não pelo nome da classe handler.
3. **Migrar para Durable Inbox / `MessageIdentity.IdAndDestination` / mudar `EndpointMode`.** Rejeitada nesta correção: fora de escopo — o projeto usa listeners `Inline` deliberadamente (ADR-015/016) e este defeito não tem relação com durabilidade/redelivery, apenas com a composição da cadeia de handlers em si.
4. **Reestruturar a topologia física (exchanges/routing keys/filas) para conventional routing do Wolverine.** Rejeitada: ampliaria desnecessariamente o escopo — a topologia física já é homologada e de propriedade exclusiva do `IHostPro.MigrationRunner`; o defeito está inteiramente no lado do consumo (handler-chain), não na topologia.

## Decisão

**Endpoint-specific sticky handler mapping**, via a API real e confirmada por reflexão contra o Wolverine 6.22.0 instalado — `IListenerConfiguration<T>.AddStickyHandler(Type)`, disponível no retorno de `opts.ListenToRabbitQueue(...)` (backed por `Endpoint.StickyHandlers`, `List<Type>`) — aplicada individualmente, no composition root (`IHostPro.Worker/Program.cs`), a cada fila que participa de um dos 16 tipos de evento identificados como em risco:

```csharp
opts.ListenToRabbitQueue("housekeeping.reservation-projection")
    .AddStickyHandler(typeof(ReservationCreatedHandler))
    .AddStickyHandler(typeof(ReservationCancelledHandler));
```

Cada chamada declara explicitamente qual(is) classe(s) de handler aquela fila, e apenas aquela fila, deve executar — o Wolverine, ao encontrar pelo menos uma associação sticky para um tipo de mensagem, para de combinar os handlers desse tipo em uma cadeia compartilhada e passa a manter uma `HandlerChain` independente por endpoint sticky-vinculado. Nenhuma mudança de topologia física: mesmas filas, mesmos bindings, mesmas routing keys — tudo continua provisionado exclusivamente pelo `IHostPro.MigrationRunner`.

A configuração vive inteiramente no composition root (`Program.cs`), nunca em atributos sobre as classes handler (`[StickyHandler("nome-da-fila")]` foi deliberadamente rejeitado): mantém as classes de Infrastructure/Messaging de cada Bounded Context sem qualquer conhecimento da topologia de deployment (nomes físicos de fila), consistente com o restante da arquitetura de mensageria do projeto.

Tipos de evento com exatamente um handler descoberto no processo (`ReservationUpdated`, `CleaningOccurrenceRegistered`, `PolicyUpdated`, o comando `CreateCleaningForReservation`) não receberam mapeamento sticky — nunca estiveram em risco, pois o Wolverine não tem o que combinar quando existe apenas uma classe de handler para o tipo.

## Prova

- **Estrutural** (`WolverineHandlerChainIsolationBaselineTests.cs`, `IHostPro.Api.Tests.Integration`): inspeção direta de `WolverineRuntime.Handlers` (o `HandlerGraph` real e compilado do Wolverine), obtido via `host.GetRuntime()`, contra um host mínimo reproduzindo exatamente os módulos/filas relevantes de `Program.cs` — sem depender de nenhuma mensagem publicada ou efeito colateral de banco. Confirma, antes da correção, exatamente 1 `HandlerChain` combinada por tipo de evento fan-out (`ReservationCreated`: 3 `HandlerCalls`; `PropertyCreated`: 2; `CleaningCreated`: 2). Após `AddStickyHandler`, confirma exatamente N `HandlerChain`s independentes (uma por endpoint), cada uma com exatamente 1 `HandlerCall`, nunca 0 (handler perdido) e nunca mais que 1 (ainda combinado) — total de chamadas exatamente igual ao número de filas, nunca um fan-out N×M.
- **Transporte real**: `CreateCleaningForReservationWorkflowRoundTripTests` (RabbitMQ real, Postgres real, `IHostPro.Worker.dll` real como subprocesso) — a mesma condição de corrida que expôs o defeito original agora passa de forma consistente com a correção aplicada.

## Consequências

### Positivas
- Corrige um defeito transversal, pré-existente às Fases 6/7/8, capaz de corromper projeções de Dashboard e descartar silenciosamente entregas reais destinadas a outras Bounded Contexts, sempre que múltiplas Bounded Contexts compartilham o consumo do mesmo Integration Event no mesmo processo.
- Correção cirúrgica: nenhuma mudança de topologia física, nenhuma mudança de durabilidade/`EndpointMode`, nenhuma alteração de registro de DI nomeada — escopo estritamente limitado à composição de handler chains no `IHostPro.Worker/Program.cs`.
- Mantém as classes Infrastructure/Messaging de cada Bounded Context sem conhecimento de topologia de deployment (rejeitando deliberadamente `[StickyHandlerAttribute]`).
- Cobertura estrutural (via `HandlerGraph` real) mais forte do que testes baseados apenas em efeitos colaterais de banco — reproduz e prova a correção do mecanismo em si, não apenas uma consequência indireta dele.

### Riscos Aceitos
- `AddStickyHandler` precisa ser aplicado individualmente a cada fila nova que compartilhar um tipo de evento já em risco, ou a cada novo Bounded Context que passe a consumir um evento já compartilhado — não há proteção automática contra esquecimento. Mitigado parcialmente pela prova estrutural reutilizável (`WolverineHandlerChainIsolationBaselineTests`) e pela documentação explícita, no próprio `Program.cs`, do porquê de cada chamada.
- `MultipleHandlerBehavior.Separated` (alternativa global) permanece disponível como opção futura caso o número de tipos de evento em risco cresça a ponto de tornar o mapeamento manual pouco prático — não adotada nesta correção por ausência de necessidade comprovada.

## Referências
- ADR-015 (Isolamento do Processamento de Mensagens Housekeeping da Integração EF Core do Wolverine) e ADR-016 (Tenant-safe Execution Boundary) — o mecanismo de execution scope local que este ADR não altera; a combinação de handler chains ocorre em uma camada anterior e independente.
- ADR-013 (Convenção de Roteamento RabbitMQ para Integration Events) — a topologia física preservada sem alteração por esta correção.
- [JasperFx/wolverine issue #801 — "Sticky" Message Handler configuration to a specific endpoint](https://github.com/JasperFx/wolverine/issues/801).
- `IHostPro.Worker/Program.cs` — todas as dezesseis associações `AddStickyHandler` aplicadas, uma por handler afetado, com comentário explicando o motivo em cada bloco de fila.
- `WolverineHandlerChainIsolationBaselineTests.cs` (`IHostPro.Api.Tests.Integration`) — prova estrutural (baseline combinado + isolamento pós-correção) para `ReservationCreated`, `PropertyCreated`, `CleaningCreated`.
- `CreateCleaningForReservationWorkflowRoundTripTests.cs` — o teste de transporte real que expôs originalmente o defeito e confirma a correção.
