# ADR-031 — HTTP Circuit Breaking for Anthropic and Meta

Status: Aceito
Data: 2026-09-02

## Contexto

Fase 9, Checkpoint 2.2 (WhatsApp real) e, por precedência direta, Fase 11, Checkpoint 7 (Anthropic real) decidiram deliberadamente **não** adotar Polly/`Microsoft.Extensions.Http.Resilience` para os dois únicos clientes HTTP outbound reais da plataforma — a razão declarada, em ambos os `.csproj`, era "zero automatic retry framework is deliberate". Essa decisão foi reforçada por uma `ArchitectureTest` real (`ExternalIntegrationsDependencyTests.Infrastructure_References_No_Resilience_Or_Polly_Package`), que falhava se qualquer assembly referenciado por `ExternalIntegrations.Infrastructure` contivesse "Polly" ou "Resilience" no nome — uma checagem categórica de pacote, mais ampla que a justificativa original (que falava especificamente de retry, não de resiliência em geral).

Fase 12, Checkpoint 3 (Resilience & Rate Limiting) auditou os documentos-fonte (Documento 13 §19, Documento 15 §18, Documento 19 §21) e confirmou que um circuit breaker HTTP é exigido qualitativamente para toda integração externa — algo que, até este checkpoint, não existia para Anthropic nem Meta (ambos têm apenas timeout + a política de retry já homologada, nenhum circuit breaker). Sem um circuit breaker, uma indisponibilidade real de qualquer um dos dois providers custa um timeout completo (30s/15s) por chamada, repetidamente, até o provider se recuperar — um custo real de latência/recursos que o CP3 mandato pediu para investigar e, se aprovado, corrigir.

Trazido como Decision Gate específico e isolado (nunca reabrindo o CP3 já publicado como um todo): o usuário aprovou reabrir a decisão "sem Polly", **estritamente** para HTTP circuit breaking, usando a infraestrutura oficial do ecossistema .NET (`Microsoft.Extensions.Http.Resilience`, mantido pela Microsoft/.NET Foundation) em vez de uma implementação própria (rejeitada — estado/concorrência de circuit breaker é infraestrutura sensível demais para reimplementar).

## Decisão

### Escopo estritamente limitado

`Microsoft.Extensions.Http.Resilience` é permitido **apenas** em `IHostPro.Contexts.AIAgent.Infrastructure` (Anthropic) e `IHostPro.Contexts.ExternalIntegrations.Infrastructure` (Meta), e **apenas** para chamar `AddCircuitBreaker` — nunca `AddRetry`, `AddHedging`, `AddTimeout` (o timeout continua sendo o `HttpClient.Timeout` já configurado, inalterado) ou `AddFallback`. Nenhuma outra Bounded Context, nenhuma camada Application/Domain/Contracts/Api, referencia o pacote — protegido agora por uma `ArchitectureTest` mais ampla que a original (`Only_The_Two_Approved_Infrastructure_Projects_May_Reference_Resilience_Or_Polly`, solução inteira, não apenas um projeto).

`AutomaticProviderRetryFromResiliencePipeline=false`: nenhum retry é adicionado pelo pipeline de resiliência. O único retry de Anthropic continua sendo o retry único já homologado na Fase 11 CP5 (`ConversationMessageReceivedProcessor.GenerateWithRetryAsync`, `ModelTechnicalRetryCount=1`), inalterado. Meta continua com zero retry de qualquer natureza — timeout e circuit-open ambos mapeiam para `DeliveryOutcomeUnknown`/`TransientProviderFailure`, nunca um reenvio, para nunca arriscar uma duplicidade física de WhatsApp.

### Classificação de falha

O circuit breaker conta como falha exatamente as mesmas condições já classificadas como transitórias em cada provider (nunca uma classificação nova/divergente):

- **Anthropic**: reaproveita `AnthropicModelProvider.IsPermanentFailure` — 400/401/403/404 nunca abrem o circuito; timeout, erro de rede, 429 e 5xx contam.
- **Meta**: mesma lógica de `MetaFailureCodes` (4xx permanente, 5xx transitório) — timeout, erro de rede, 429 e 5xx contam.

429 foi decidido explicitamente como contável (mandato §10): representa capacidade do provider esgotada, semanticamente equivalente a um 5xx para fins de circuit breaking, e o circuit breaker nunca gera uma tentativa adicional — apenas impede tentativas futuras por um período, o oposto de agravar o throttling.

### Configuração externa

`AIAgent:Anthropic:CircuitBreaker`/`ExternalIntegrations:WhatsApp:Meta:CircuitBreaker` — `Enabled`, `FailureRatio` (default 0.5), `MinimumThroughput` (default 4), `SamplingDuration` (default 30s), `BreakDuration` (default 15s). Nenhum valor é um SLA de Produção — `ProductionCircuitBreakerThresholdsRequired=true`, dependem de dados reais do piloto. As duas classes de opções (`HttpCircuitBreakerOptions` em AIAgent.Infrastructure, `MetaHttpCircuitBreakerOptions` em ExternalIntegrations.Infrastructure) são deliberadamente **não compartilhadas** entre os dois Bounded Contexts — cada um mantém sua própria cópia, mesma disciplina de isolamento já aplicada a toda outra dependência cross-cutting nesta plataforma.

### Telemetria

`CircuitBreakerTelemetry` (`BuildingBlocks.Infrastructure.Resilience`) — um `Meter` compartilhado (`IHostPro.Resilience`, contador `circuit_breaker.state_changes`), tags `provider`/`state` apenas, nunca URL/header/prompt/corpo/telefone. Vive em `BuildingBlocks.Infrastructure` porque seu próprio código nunca referencia um tipo Polly (métrica pura) — isso não torna Polly uma dependência transitiva de todo consumidor de `BuildingBlocks.Infrastructure`, apenas os dois projetos que já o referenciam diretamente o chamam. Rejeições (circuito já aberto) não geram uma métrica própria — cada provider já registra o outcome "CircuitOpen"/`circuit_open` no seu próprio contador de chamadas já existente (`ai_agent.model_calls`; log estruturado para Meta), reutilizando infraestrutura em vez de duplicá-la.

### Health

Deliberadamente não implementado: o estado do circuit breaker nunca é lido por `/health/ready`. Um provider externo com circuito aberto é uma feature degradada (a IA/o envio de WhatsApp momentaneamente indisponível), nunca uma incapacidade do processo de servir todo o resto do seu trabalho — a mesma separação já estabelecida para a classificação `Redis=Degraded` (Fase 12 CP2).

## Alternativas Consideradas

- **Circuit breaker hand-rolled**: rejeitada explicitamente pelo usuário — estado/concorrência de circuit breaker é infraestrutura sensível demais para implementação própria sem necessidade real, dado que uma solução oficial, madura e já usada em produção por todo o ecossistema .NET está disponível.
- **`AddStandardResilienceHandler()` (o pacote "tudo incluso" retry+timeout+circuit breaker)**: rejeitada — adicionaria retry automático, violando diretamente o escopo aprovado (`AutomaticProviderRetryFromResiliencePipeline=false`) e duplicando o retry de aplicação já homologado.
- **Remover a `ArchitectureTest` em vez de estreitá-la**: rejeitada — o mandato foi explícito ("não transformar em teste inútil"); a regra foi reescrita para ser mais ampla (solução inteira) exatamente onde ainda deveria proteger, e mais estreita apenas nos dois pontos aprovados.

## Consequências

### Positivas
- Um provider real e persistentemente indisponível para de custar um timeout completo por mensagem após o limiar configurado — falha rápida, localmente, sem I/O de rede.
- A regra arquitetural que protegia a decisão original agora protege uma superfície MAIOR (toda a solução, não um projeto), nunca menor.

### Riscos Aceitos
- Nenhum threshold de produção foi decidido — `ProductionCircuitBreakerThresholdsRequired=true` permanece um item aberto, a ser resolvido com dados reais do piloto.
- `BreakDuration` tem um mínimo de 500ms imposto pela própria validação do Polly — documentado, mas não validado no nível das próprias `Options` desta plataforma (uma configuração inválida falharia na primeira chamada real, não no startup) — registrado como possível melhoria futura, não bloqueador.

## Referências
- `documentacao do projeto/Fase 12 - Hardening, Deploy e Piloto do MVP - Validacao e Homologacao.md`, Checkpoint 3
- `tests/IHostPro.ArchitectureTests/ExternalIntegrationsDependencyTests.cs` — `Only_The_Two_Approved_Infrastructure_Projects_May_Reference_Resilience_Or_Polly`
- `tests/Contexts/AIAgent/IHostPro.Contexts.AIAgent.Tests.Unit/ModelProviders/Anthropic/AnthropicCircuitBreakerTests.cs` / `tests/Contexts/ExternalIntegrations/IHostPro.Contexts.ExternalIntegrations.Tests.Unit/Infrastructure/Meta/MetaCircuitBreakerTests.cs` — prova determinística de CLOSED/OPEN/HALF-OPEN/recovery
