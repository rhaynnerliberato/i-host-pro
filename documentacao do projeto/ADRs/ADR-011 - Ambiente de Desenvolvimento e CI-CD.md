# ADR-011 — Ambiente de Desenvolvimento e CI/CD

Status: Aceito
Data: 2026-07-26

## Contexto

O Documento 21 exige ambientes reproduzíveis, pipeline automatizado (build → testes → análise estática → deploy) e infraestrutura como código. A organização ainda não definiu onde o código será hospedado nem o provedor de nuvem final.

## Decisão

- **Ambiente de desenvolvimento local:** Docker Compose, orquestrando PostgreSQL, Redis, RabbitMQ e MinIO. Código da aplicação roda no host para hot-reload rápido.
- **CI/CD:** GitHub Actions, como padrão reversível — condicionado à hospedagem do código no GitHub. Caso a organização defina posteriormente hospedagem/nuvem baseada em Azure DevOps/Azure, esta ADR deverá ser revisada com uma nova ADR, sem impacto na arquitetura da aplicação.

## Alternativas Consideradas

- **Azure DevOps:** alternativa igualmente válida caso a organização opte por hospedar o código em Azure Repos — decisão pendente de confirmação futura, não bloqueante para a Fase 0.
- **Dev Containers:** oferecido como opção complementar não obrigatória, para não amarrar a equipe a um único editor.

## Consequências

### Positivas
- Ambiente local idêntico em composição de serviços ao de produção (paridade dev/prod).

### Riscos Aceitos
- Escolha de CI/CD e provedor de nuvem final permanece uma decisão em aberto, a ser confirmada antes da Fase 12 (deploy real) — não bloqueia o desenvolvimento das fases anteriores.

## Referências
- Documento 21
- Architecture Principles (infraestrutura de suporte)
