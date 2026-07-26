# ADR-010 — Estratégia de Testes

Status: Aceito
Data: 2026-07-26

## Contexto

O Documento 20 exige uma pirâmide de testes (unitários, integração, E2E), testes específicos para regras de negócio, workflows e IA, e validação automatizada como parte do CI. O Architecture Principles §14 exige validação automática das regras de dependência entre Bounded Contexts.

## Decisão

- **Testes unitários/integração:** xUnit + FluentAssertions.
- **Testes E2E:** Playwright for .NET (binding oficial Microsoft), mantendo todo o código de teste em C#.
- **Testes de arquitetura:** NetArchTest, validando automaticamente as regras de dependência entre camadas (Domain não referencia Infrastructure) e entre Bounded Contexts (nenhum contexto referencia Domain/Application/Infrastructure de outro), executados no pipeline de CI.
- Testes localizados dentro de cada contexto (`<Contexto>.Tests.Unit`, `<Contexto>.Tests.Integration`); testes E2E e de arquitetura na raiz da solução, por cruzarem múltiplos contextos.

## Alternativas Consideradas

- **Jest/Supertest/Playwright (Node):** não aplicável após a mudança para .NET.

## Consequências

### Positivas
- Testes de arquitetura tornam as regras do Architecture Principles auto-aplicáveis, não apenas documentadas.

### Riscos Aceitos
- **FluentAssertions é fixado explicitamente na major version 7.x** (`--version "7.*"`) em todos os projetos de teste. A partir da versão 8, a biblioteca passou a exigir licença comercial paga para uso por entidades acima de determinado porte — o mesmo tipo de risco identificado e evitado para o MediatR (ADR-002). Esta restrição de versão deverá ser revisada periodicamente e nunca deverá ser removida silenciosamente (ex.: por uma atualização automática de dependências) sem nova avaliação de licenciamento.

## Referências
- Documento 20
- Architecture Principles §14, §16
