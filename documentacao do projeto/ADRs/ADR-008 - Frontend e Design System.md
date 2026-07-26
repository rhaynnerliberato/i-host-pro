# ADR-008 — Frontend e Design System

Status: Aceito
Data: 2026-07-26

## Contexto

O Documento 14 exige interface mobile-first, responsiva, consistente, com suporte a múltiplos idiomas em runtime e personalização visual por tenant. A equipe de desenvolvimento possui expertise consolidada em Angular (ADR-001).

## Decisão

- **Framework frontend:** Angular.
- **Componentes UI:** Angular Material.
- **Internacionalização:** Transloco (runtime, por tenant/usuário) — não o i18n nativo do Angular, que é compile-time e incompatível com troca de idioma em runtime exigida pelo Documento 14 §32.
- **Cliente HTTP tipado:** gerado automaticamente via NSwag a partir do contrato OpenAPI exposto pelo backend, mantendo sincronia de contrato sem esforço manual.

## Alternativas Consideradas

- **Next.js/React + Tailwind + shadcn/ui:** tecnicamente sólida e com maior flexibilidade de personalização visual por tenant, mas descartada em favor do alinhamento com a expertise já existente na equipe.
- **i18n nativo do Angular:** descartado por exigir builds separados por idioma, incompatível com troca de idioma em runtime por tenant/usuário.

## Consequências

### Positivas
- Angular Material entrega tabelas, data-grids, formulários reativos e calendários maduros, acelerando a construção das telas operacionais complexas exigidas pelo Documento 14 (§11-14).
- Framework opinativo reduz divergência de padrões entre desenvolvedores ao longo dos anos.

### Riscos Aceitos
- Personalização visual profunda por tenant (Documento 14 §33) exigirá mais esforço de customização de tema do que uma stack baseada em Tailwind — aceito como tradeoff pela produtividade geral.

## Referências
- Documento 14
- Architecture Principles §15
