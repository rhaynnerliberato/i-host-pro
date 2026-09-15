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

O ambiente local não depende de nenhum recurso AWS — a API e o Worker nunca
chamam o AWS Secrets Manager quando `ASPNETCORE_ENVIRONMENT=Development`
(padrão do `launchSettings.json`), e `appsettings.Development.json` já aponta
para os serviços do `docker-compose.yml`.

```bash
# 1. Sobe a infraestrutura (PostgreSQL, Redis, RabbitMQ, MinIO, observabilidade)
docker compose up -d

# 2. Compila e valida as regras de arquitetura
dotnet build IHostPro.sln
dotnet test tests/IHostPro.ArchitectureTests/IHostPro.ArchitectureTests.csproj

# 3. Aplica as migrations de todos os módulos no Postgres local
dotnet run --project tools/IHostPro.MigrationRunner

# 4. Sobe a API e o Worker (em terminais separados)
dotnet run --project src/Host/IHostPro.Api
dotnet run --project src/Host/IHostPro.Worker

# 5. Sobe o frontend Angular
cd frontend/IHostPro.Web && npm install && npm start
```

Para ter um usuário para login local, habilite o seed de desenvolvimento
(`Identity:DevelopmentSeed:Enabled=true` em `appsettings.Development.json` ou
via `dotnet user-secrets`, definindo também `Identity:DevelopmentSeed:AdminPassword`)
antes do passo 4 — ele cria um Tenant + usuário Admin idempotentemente ao
iniciar a Api/Worker (`DevelopmentIdentitySeeder`).

**Limitação conhecida:** esse seed de desenvolvimento não atribui nenhuma
Role ao usuário criado (por desenho — ver `DevelopmentIdentitySeeder.cs`), então
ele nasce sem permissões. `tools/IHostPro.TenantProvisioning` resolve isso
(atribui a Role ADMIN), mas exige AWS Secrets Manager e por isso **não funciona
localmente** — é a ferramenta usada para provisionar tenants no Homolog/produção,
não para desenvolvimento local. Atribuir a Role ADMIN a um usuário local hoje
exige um passo manual (fora do escopo desta atualização de documentação).

**Nota (2026-09):** a conta AWS de Homolog foi desativada por decisão do time
(ver `documentacao do projeto/ADRs`); o desenvolvimento local acima não depende
dela em nada. O job de deploy do CI (`.github/workflows/ci.yml`, branch
`master`) para Homolog é esperado falhar até que a estratégia de nuvem seja
retomada — os demais jobs (build, testes de arquitetura, unitários, integração,
E2E de frontend) não têm nenhuma dependência de AWS.

## Estado atual (Fase 0)

> Esta seção descreve apenas o estado inicial do projeto (Fase 0). O código
> evoluiu muito além disso — consulte `documentacao do projeto/ADRs/` e os
> documentos de Fase em `documentacao do projeto/` para o estado atual de cada
> Bounded Context.

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
