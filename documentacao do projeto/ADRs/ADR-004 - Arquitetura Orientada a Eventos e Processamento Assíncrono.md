# ADR-004 — Arquitetura Orientada a Eventos e Processamento Assíncrono

Status: **Atualizado** (tecnologia de mensageria revisada; decisão conceitual original preservada)
Data original: 2026-07-26
Data desta revisão: 2026-07-26

## Contexto

O Documento 11 (§3, §9) estabelece Event-Driven Architecture como princípio não-negociável. O Documento 17 exige um Motor de Workflows com estados, retries e reprocessamento. O Documento 07 define o catálogo formal de eventos de domínio. Era necessário escolher a tecnologia de mensageria/processamento assíncrono e formalizar a separação conceitual entre Domain Events, Integration Events, Outbox Pattern e Event Bus.

## Decisão Original (2026-07-26)

A decisão original selecionou **MassTransit** como backbone único de mensageria sobre **RabbitMQ**. Durante a implementação da Fase 0, ao executar a aplicação em tempo real (não apenas compilar), foi descoberto que a versão resolvida pelo NuGet (9.1.2) **exige licença comercial** (`SetLicense`/`MT_LICENSE`), decorrente de uma mudança de modelo de negócio anunciada pela mantenedora em abril de 2025, com lançamento oficial da v9 comercial em Q1 de 2026. A alternativa gratuita (MassTransit 8.x, Apache 2.0) tem suporte de segurança garantido apenas **até pelo menos o final de 2026** — horizonte incompatível com a exigência de manutenibilidade de 10 anos do produto (Documento 03 §11, Documento 15 §32).

## Motivo da Substituição

Foi conduzida, com o usuário, uma sequência de três análises técnicas antes de qualquer alteração de código:

1. **Comparação ampla** entre MassTransit 8.x, MassTransit 9.x, Wolverine, Rebus, NServiceBus, Brighter e implementação própria sobre `RabbitMQ.Client`, avaliando licença, custo, maturidade, comunidade, suporte a Outbox/Sagas/retries/delayed messages, curva de aprendizado, risco de lock-in e aderência à arquitetura já aprovada.
2. **Diligência técnica aprofundada sobre o Wolverine e sua mantenedora (JasperFx Software)**, incluindo dados objetivos extraídos diretamente da API do GitHub (idade do projeto, contribuidores, cadência de releases, tempo médio de resolução de issues) e pesquisa sobre a sustentabilidade financeira da organização mantenedora.
3. **Análise de custo-benefício entre depender de um framework completo e construir a infraestrutura internamente** (RabbitMQ.Client + Outbox próprio + BackgroundService + Polly + state machine própria), com estimativa objetiva de ~3.200 a 5.100 linhas de código crítico de infraestrutura distribuída a construir e manter indefinidamente.

A conclusão das três análises, aprovada explicitamente pelo usuário, foi substituir o MassTransit pelo **Wolverine**.

## Riscos Identificados no Wolverine

- **Bus factor extremo:** um único mantenedor (Jeremy Miller) responde por mais de 97% das contribuições históricas rastreáveis do projeto (3.222 contribuições, contra 77 do segundo colocado, em uma base de 193 contribuidores).
- **Projeto jovem:** ~4 anos de existência (desde 2022), contra quase 19 anos do MassTransit.
- **Ritmo de major version acelerado:** o projeto avançou de v3.x a v6.x em aproximadamente 18 meses — maior probabilidade de mudanças de API relevantes ao longo de uma janela de 10 anos do que um projeto com cadência mais lenta.
- **Adoção pública documentada limitada:** não foram encontradas evidências públicas robustas de adoção por terceiros nomeados em produção, além do uso do próprio criador (MedeAnalytics).
- **Risco organizacional estrutural comparável ao que precedeu o fechamento do núcleo do MassTransit:** a JasperFx Software já opera parcialmente por monetização (consultoria, ferramentas avançadas pagas como o CritterWatch); embora hoje esse modelo open-core esteja declarado publicamente desde a fundação da empresa (2023) — diferente do MassTransit, cuja mudança foi uma reversão não anunciada previamente —, não há garantia de que o núcleo gratuito permaneça assim indefinidamente.

## Estratégia de Mitigação

1. **Isolamento arquitetural completo** (detalhado abaixo): nenhum Bounded Context de negócio referencia tipos do Wolverine.
2. **Testes de arquitetura automatizados (NetArchTest)** validando esse isolamento continuamente, não apenas por convenção documental.
3. **Plano de substituição futura documentado** (abaixo), em vez de dependência silenciosa e não monitorada.
4. Ferramentas complementares de baixíssimo risco de lock-in (Polly — membro da .NET Foundation, co-mantido pela Microsoft; OpenTelemetry — padrão aberto) adotadas independentemente da escolha de mensageria, reduzindo a superfície total de dependência crítica concentrada em um único fornecedor.

## Isolamento Arquitetural Adotado

- Os contratos de mensageria já existentes (`IEventPublisher`, `IntegrationEvent`, em `BuildingBlocks.Messaging.Abstractions`) permanecem **inalterados** pela migração — é precisamente essa abstração que tornou a substituição possível sem impacto em Bounded Contexts.
- Um novo contrato, **`IIntegrationEventHandler<TEvent>`**, definido em `BuildingBlocks.Application`, passa a ser a única abstração que um Bounded Context deverá implementar para reagir a um evento consumido. Nenhum tipo do Wolverine aparece nessa assinatura.
- Toda integração concreta com o Wolverine (publicação, descoberta/dispatch de handlers, configuração de transporte, resolução de tenant por mensagem) fica confinada a `BuildingBlocks.Infrastructure` e ao `Host` (`IHostPro.Api`/`IHostPro.Worker`) — os únicos projetos autorizados a referenciar pacotes `WolverineFx.*`.
- Cada futuro módulo de negócio expõe, em sua própria camada de `Infrastructure` (nunca em `Application`/`Domain`), um adaptador mínimo e mecânico — sem lógica de negócio — que o Wolverine descobre por convenção e que apenas delega para a implementação de `IIntegrationEventHandler<TEvent>` resolvida via injeção de dependência.
- Regra validada automaticamente por teste de arquitetura: nenhum tipo em `BuildingBlocks.Domain` ou `BuildingBlocks.Application` pode depender de um assembly cujo nome comece com `Wolverine`.

## Plano de Eventual Substituição Futura

Graças ao isolamento acima, uma futura substituição do Wolverine exigiria alterar apenas:
- A implementação de `IEventPublisher` em `BuildingBlocks.Infrastructure`;
- O(s) adaptador(es) de descoberta/dispatch de handlers;
- A configuração de transporte e o registro de DI no `Host`.

Nenhum Bounded Context de negócio precisaria ser alterado, pois todos dependerão exclusivamente de `IEventPublisher`, `IIntegrationEventHandler<TEvent>` e `IntegrationEvent`.

Sinais documentados que deverão motivar uma nova reavaliação (critérios de referência, não gatilhos automáticos): anúncio de mudança no modelo de licenciamento do núcleo do Wolverine; ausência de releases por período prolongado (ex.: mais de 6 meses consecutivos); saída de Jeremy Miller do projeto sem sucessor claramente estabelecido.

## Alternativas Consideradas (resumo)

O detalhamento completo de cada alternativa está registrado no histórico da análise conduzida com o usuário. Resumo das conclusões:

- **MassTransit 8.x:** descartado — suporte de segurança garantido apenas até pelo menos o final de 2026.
- **MassTransit 9.x:** descartado — licenciamento comercial no componente mais crítico da arquitetura.
- **NServiceBus:** descartado — mesmo problema estrutural de licenciamento comercial, com custo não-público.
- **Implementação própria sobre `RabbitMQ.Client`:** descartada — estimativa de ~3.200 a 5.100 linhas de código crítico de infraestrutura distribuída a construir e manter indefinidamente, com risco de bugs concentrado exatamente no Motor de Workflows (Bounded Context Core do produto).
- **Rebus:** descartado — Outbox manual (não nativo) e Sagas apenas básicas; exigiria mais retrabalho que o Wolverine para atingir a paridade funcional exigida pelo Documento 17.
- **Brighter:** descartado — Outbox nativo e bem avaliado, porém sem suporte de primeira classe a Sagas/state machines.
- **Wolverine: escolhido** — núcleo MIT (mensageria, Outbox, Sagas, transporte RabbitMQ), ativamente mantido, tecnicamente aderente ao que este ADR sempre exigiu, com os riscos organizacionais identificados mitigados pelo isolamento arquitetural descrito acima.

Mantêm-se descartadas, pelos motivos já registrados na versão original desta ADR: **Hangfire** e **Quartz.NET** (não resolvem o problema central de mensageria orientada a eventos) e **transporte in-memory** (não sobrevive a múltiplas instâncias).

## Decisão Atual

- **Backbone único de mensageria:** Wolverine, cobrindo publicação/consumo de eventos, Outbox/Inbox transacional (via `WolverineFx.EntityFrameworkCore`), retries/redelivery, mensagens agendadas e Sagas/state machines para o Motor de Workflows (Documento 17).
- **Transporte:** RabbitMQ, mantido desde a Fase 0.
- **Tarefas cronológicas não disparadas por evento:** `BackgroundService` leve com `Cronos` — inalterado pela migração.
- Separação formal de responsabilidades (Domain Events / Integration Events / Outbox Pattern / Event Bus) — **inalterada conceitualmente**; muda apenas a biblioteca que implementa o Event Bus e o Outbox.

## Consequências

### Positivas
- Elimina custo de licenciamento e risco de mudança de modelo comercial no componente mais central da arquitetura.
- Introduz um isolamento arquitetural mais explícito do que o originalmente implementado com MassTransit (`IIntegrationEventHandler<TEvent>` como contrato formal, testado automaticamente).
- Preserva a possibilidade de nova substituição futura com impacto limitado a poucos arquivos de infraestrutura.

### Riscos Aceitos
- Dependência de um projeto com bus factor concentrado em uma única pessoa — mitigada, não eliminada, pelo isolamento arquitetural e pelo plano de reavaliação documentado.
- Retrabalho da infraestrutura de mensageria já implementada na Fase 0 — aceito como custo único e não recorrente, pois nenhum Bounded Context de negócio existia ainda no momento da substituição.

## Referências
- Documento 03 §11, Documento 07, Documento 11 §3, §9, §29, Documento 15 §32, Documento 17
- Architecture Principles §8, §9, §11, §12, §14
- ADR-002 (Arquitetura da Solução)
