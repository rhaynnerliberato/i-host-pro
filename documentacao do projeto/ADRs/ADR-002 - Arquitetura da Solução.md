# ADR-002 — Arquitetura da Solução

Status: Aceito
Data: 2026-07-26

## Contexto

O Documento 11 exige Clean Architecture, DDD, SOLID e Event-Driven Architecture, organizados por domínio de negócio (não por camada técnica), evitando tanto um monólito acoplado quanto uma decomposição prematura em microsserviços (Documento 11 §4). Era necessário definir precisamente como esses princípios se traduzem em uma estrutura de solução concreta.

## Decisão

- Estilo arquitetural: **Modular Monolith**, decomposto em **16 Bounded Contexts** (listados no Architecture Principles §3), cada um seguindo internamente **Clean Architecture** (Domain/Application/Infrastructure/Api).
- Padrão interno de aplicação: **CQRS**, com Commands/Queries despachados via a biblioteca **`Mediator`** (Martin Othamar, baseada em source generators, licença MIT) — substitui o pacote `MediatR` clássico.
- Comunicação entre contextos: assíncrona por padrão, via Integration Events (ver ADR-004), com duas exceções controladas de consulta síncrona (Identity & Access e Configuration & Policy).
- Estrutura de projetos, convenções de nomenclatura e regras de dependência: conforme Architecture Principles §3, §4, §14, §15.

## Alternativas Consideradas

- **Monólito não-modularizado:** descartado — viola a exigência explícita de baixo acoplamento/alta coesão do Documento 05.
- **Microsserviços desde o início:** descartado — complexidade operacional desproporcional ao estágio atual (Documento 11 §31).
- **MediatR (Jimmy Bogard):** descartado por risco de licenciamento comercial em versões recentes, incompatível com um produto SaaS de longo prazo. Ver análise completa na revisão arquitetural aprovada.
- **Organização por camada técnica (Controllers/Services/Repositories globais):** descartada — Documento 11 §28 exige organização por domínio de negócio.

## Consequências

### Positivas
- Fronteiras de módulo já preparadas para eventual extração futura em microsserviços (Architecture Principles §18), sem necessidade de redesenho.
- Testes de arquitetura automatizados (NetArchTest) impedem violação das regras de dependência ao longo do tempo.

### Riscos Aceitos
- Curva de aprendizado inicial de Sagas/mensageria assíncrona para a equipe (ver ADR-004 para a tecnologia concreta e seu histórico de revisão) — mitigada por documentação dedicada e pelo isolamento do backbone de mensageria em `BuildingBlocks.Infrastructure`.

## Referências
- Documento 05, Documento 11
- Architecture Principles §2 a §6, §14 a §16
