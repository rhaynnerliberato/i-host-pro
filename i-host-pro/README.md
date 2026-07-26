# iHostPro — Código-Fonte da Plataforma

Este diretório é a raiz do código-fonte da aplicação iHostPro, conforme definido no `Documento 99 - Development Authorization.txt`.

A arquitetura completa está congelada e documentada em:

- `documentacao do projeto/Architecture Principles.md` — referência arquitetural principal.
- `documentacao do projeto/ADRs/` — decisões arquiteturais aprovadas (ADR-001 a ADR-011).

## Estrutura da Solution

```
IHostPro.sln
src/
  BuildingBlocks/   → primitivas genéricas reutilizáveis (ver Architecture Principles §12)
  Host/             → IHostPro.Api (composition root HTTP) e IHostPro.Worker (handlers/sagas)
  Contexts/         → um Bounded Context por subpasta, adicionado a partir da Fase 1
tools/
  IHostPro.MigrationRunner/ → aplica as migrations de todos os módulos (Architecture Principles §16)
tests/
  IHostPro.ArchitectureTests/ → valida automaticamente as regras de dependência
```

## Pré-requisitos

- .NET SDK 10.0 LTS (a Solution usa `TargetFramework=net10.0` — ver ADR-001 para o histórico da migração a partir do .NET 8)
- Docker + Docker Compose (infraestrutura local: PostgreSQL, Redis, RabbitMQ, MinIO, OpenTelemetry Collector, Prometheus, Grafana)

## Observabilidade local

Pipeline de métricas (ADR-007): `IHostPro.Api` / `IHostPro.Worker` → OTLP → OpenTelemetry Collector → Prometheus → Grafana.

| Serviço | Porta | Acesso |
|---|---|---|
| OpenTelemetry Collector | 4317 (OTLP gRPC), 4318 (OTLP HTTP) | endpoint consumido pelas aplicações (`OpenTelemetry:OtlpEndpoint`) |
| Prometheus | 9090 | http://localhost:9090 |
| Grafana | 3000 | http://localhost:3000 (usuário/senha em `.env`, ver `.env.example`) |

Configuração dos arquivos do Collector/Prometheus/Grafana em `observability/`.

## Executando localmente

```bash
docker compose up -d
dotnet build IHostPro.sln
dotnet test tests/IHostPro.ArchitectureTests/IHostPro.ArchitectureTests.csproj
```

## Estado atual (Fase 0)

Concluído nesta etapa:
- Estrutura da Solution e BuildingBlocks (Domain, Application, Infrastructure, Messaging.Abstractions).
- Processos `IHostPro.Api` e `IHostPro.Worker` com Serilog (console sink garantido, configuração via `appsettings`, suportando Development/Production), OpenTelemetry e **Wolverine + RabbitMQ** configurados (ver ADR-004 para o histórico da substituição do MassTransit).
- `IEventPublisher` (implementado por `WolverineEventPublisher`) registrado em ambos os processos — Api publica apenas, Worker publica e consome.
- `IIntegrationEventHandler<TEvent>` (`BuildingBlocks.Application`) como única abstração que um futuro Bounded Context implementará para reagir a eventos — sem qualquer referência ao Wolverine.
- `TenantResolutionMiddleware` (Wolverine) resolve o tenant por mensagem consumida no Worker; `ITenantContext` registrado em ambos os processos.
- Filtro global de isolamento por tenant (`ITenantOwned` + `BaseDbContext`) implementado, aplicado automaticamente a qualquer entidade de um futuro módulo.
- `IHostPro.MigrationRunner`, agora também com Serilog completo via `Microsoft.Extensions.Hosting` e `appsettings.json`/`appsettings.Development.json` próprios.
- Testes de arquitetura (NetArchTest) validando as regras de dependência do BuildingBlocks, incluindo a regra explícita de isolamento do Wolverine (nenhum tipo em `Domain`/`Application` pode referenciá-lo).
- `docker-compose.yml` com PostgreSQL, Redis, RabbitMQ, MinIO, OpenTelemetry Collector, Prometheus e Grafana.
- Pipeline de CI (`.github/workflows/ci.yml`, na raiz do repositório Git em `C:\git\i-host-pro`) com build + testes de arquitetura.
- Execução real (`dotnet run`) de `IHostPro.Api` e `IHostPro.Worker` validada nesta máquina: ambos inicializam corretamente, sem erro de licenciamento, com Serilog escrevendo no console; a única falha observada é a conexão recusada ao RabbitMQ (esperada, pois o daemon do Docker não está em execução neste ambiente) — Wolverine tenta reconectar automaticamente.

- **Plataforma migrada de .NET 8 para .NET 10 LTS** e **Wolverine atualizado da linha 5.x (5.40.0) para a linha 6.x (6.22.0)**, ambos em versões estáveis (sem preview/RC), incluindo o pacote `WolverineFx.RuntimeCompilation` exigido pela mudança de empacotamento do Wolverine 6.0 — ver ADR-001 e ADR-004 para o histórico completo.
- Diversos pacotes NuGet auxiliares (EF Core, Npgsql, Microsoft.Extensions.Hosting, Swashbuckle.AspNetCore, xUnit, Microsoft.NET.Test.Sdk, coverlet.collector) atualizados para suas versões estáveis mais recentes compatíveis com .NET 10.
- **Pipeline de métricas resolvido:** `OpenTelemetry.Exporter.OpenTelemetryProtocol` (estável) configurado em `IHostPro.Api` e `IHostPro.Worker`, exportando via OTLP para um OpenTelemetry Collector (novo serviço no `docker-compose.yml`), que expõe métricas em formato Prometheus para scrape; Grafana provisionado com o Prometheus como datasource automático. Endpoint OTLP configurável exclusivamente via `appsettings.json`/variável de ambiente — ver ADR-007.

Ainda não implementado (continuação da Fase 0 / início da Fase 1):
- Nenhum Bounded Context de negócio existe ainda (Identity & Access será o primeiro, na Fase 1) — portanto nenhum handler real (`IIntegrationEventHandler<TEvent>` concreto) foi exercitado ponta a ponta.
- Autenticação/autorização e auditoria concreta ainda não foram conectadas a nenhum caso de uso real (a infraestrutura está pronta, mas não exercitada).
- Os containers de infraestrutura (`docker compose up`) não puderam ser efetivamente iniciados e validados neste ambiente (o daemon do Docker Desktop não estava em execução) — todos os arquivos (`docker-compose.yml`, configuração do Collector/Prometheus/Grafana) foram validados apenas estaticamente (`docker compose config`).
- O Outbox/Inbox transacional do Wolverine (`WolverineFx.EntityFrameworkCore`) ainda não foi conectado a um `DbContext` real — não existe nenhum ainda (Fase 1).
- Backend de tracing distribuído (Jaeger/Tempo/etc.) ainda não escolhido — o Collector recebe traces via OTLP mas não os persiste (ver ADR-007).
