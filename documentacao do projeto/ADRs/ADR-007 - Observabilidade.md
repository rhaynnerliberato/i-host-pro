# ADR-007 — Observabilidade

Status: **Atualizado** (mecanismo de exportação de métricas definido; decisão original preservada)
Data original: 2026-07-26
Data desta revisão: 2026-07-26

## Contexto

O Documento 15 (§14-17) e o Documento 21 (§15-19) exigem logs estruturados, métricas, tracing distribuído e alertas, com proibição explícita de registrar segredos/dados sensíveis em log.

## Decisão Original (2026-07-26)

- **Logs estruturados:** Serilog.
- **Tracing e métricas:** OpenTelemetry (vendor-neutral), exportado para Prometheus/Grafana.
- **Rastreamento de erros:** Sentry.
- Toda requisição correlacionável de ponta a ponta via `CorrelationId`, propagado também nos Integration Events (Documento 07 §3).

## Pendência Identificada Durante a Migração para .NET 10

O pacote `OpenTelemetry.Exporter.Prometheus.AspNetCore`, que permitiria expor métricas via scrape direto do Prometheus a partir da própria aplicação, **nunca teve uma versão estável publicada** (todas as versões existentes, de 1.4.0 a 1.17.0, são alpha/beta/rc). O pacote foi removido do código por violar a proibição de dependências preview, deixando o mecanismo concreto de exportação de métricas indefinido até esta revisão.

## Decisão Atual — Mecanismo de Exportação de Métricas

Adotado o padrão:

```
IHostPro.Api / IHostPro.Worker → OTLP → OpenTelemetry Collector → Prometheus → Grafana
```

- **Exportador nas aplicações:** `OpenTelemetry.Exporter.OpenTelemetryProtocol` (estável), configurado em `IHostPro.Api` e `IHostPro.Worker` — as duas aplicações que produzem telemetria continuamente. `IHostPro.MigrationRunner` não recebeu essa instrumentação por ser um processo de execução curta (segundos), onde não há valor em expor métricas/traces para scrape antes do processo encerrar; permanece apenas com Serilog.
- **Endpoint OTLP:** configurado exclusivamente via `appsettings.json` (`OpenTelemetry:OtlpEndpoint`) e variável de ambiente (`OpenTelemetry__OtlpEndpoint`) — nunca hardcoded no código-fonte.
- **OpenTelemetry Collector:** recebe telemetria via OTLP (gRPC/HTTP) e expõe métricas em formato Prometheus através do exportador `prometheus` **do próprio Collector** (componente maduro do `opentelemetry-collector-contrib`, distinto do pacote .NET descartado acima).
- **Prometheus:** faz scrape do endpoint exposto pelo Collector — nunca das aplicações diretamente.
- **Grafana:** consome o Prometheus como datasource, provisionado automaticamente (sem passo manual na UI).
- **Traces:** o Collector recebe traces via OTLP, mas ainda não os encaminha a nenhum backend de rastreamento distribuído (Jaeger/Tempo/etc.) — não há backend de tracing aprovado até o momento; o pipeline atual apenas registra os traces recebidos (exportador `debug`) sem persisti-los. A escolha de um backend de tracing permanece uma decisão em aberto, fora do escopo desta revisão.

## Alternativas Consideradas

- **APM proprietário (Datadog/New Relic):** descartado — motivo original mantido (custo recorrente elevado, Documento 15 §32).
- **Aguardar a estabilização do `OpenTelemetry.Exporter.Prometheus.AspNetCore`:** descartado — sem previsão pública de estabilização; bloquearia indefinidamente a exportação de métricas.
- **Scrape direto das aplicações pelo Prometheus (sem Collector):** descartado — exigiria o pacote sem versão estável; o Collector intermediário resolve isso sem abrir mão do formato Prometheus para o Grafana consumir.

## Consequências

### Positivas
- Nenhuma dependência preview/RC/nightly no caminho de observabilidade.
- Collector desacopla as aplicações do backend de métricas — trocar Prometheus por outro backend no futuro não exige alterar `IHostPro.Api`/`IHostPro.Worker`.
- Boa integração nativa do .NET 10 LTS (ver ADR-001) com OpenTelemetry, reduzindo esforço de instrumentação.

### Riscos Aceitos
- O Collector é mais um componente de infraestrutura a operar — aceito por ser o único caminho, hoje, para exportação de métricas em formato Prometheus sem depender de um pacote sem versão estável.
- Backend de tracing distribuído ainda não escolhido — traces são recebidos pelo Collector mas não persistidos/visualizáveis; risco de baixa observabilidade de tracing até essa decisão ser tomada, sem impacto na exportação de métricas (pipelines independentes no Collector).

## Referências
- Documento 15 §14-17, Documento 21 §15-19
- Architecture Principles (transversal a todas as seções)
- ADR-001 (.NET 10 LTS)
