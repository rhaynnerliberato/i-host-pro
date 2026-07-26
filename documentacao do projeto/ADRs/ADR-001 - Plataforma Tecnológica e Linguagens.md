# ADR-001 — Plataforma Tecnológica e Linguagens

Status: **Atualizado** (runtime .NET revisado; decisão de linguagem/framework preservada)
Data original: 2026-07-26
Data desta revisão: 2026-07-26

## Contexto

O Documento 11 (§33) e o Documento 12 (§23) delegam ao Claude Code a escolha de linguagem de programação e frameworks base, exigindo apenas justificativa técnica. A equipe de desenvolvimento já possui expertise consolidada em .NET/C# e Angular, informação obtida diretamente do usuário durante a definição da stack.

## Decisão Original (2026-07-26)

- **Linguagem backend:** C# sobre **.NET 8 (LTS)**.
- **Framework backend:** **ASP.NET Core**.
- **Linguagem/Framework frontend:** **TypeScript sobre Angular**.

.NET 8 foi escolhido em vez do .NET 9 (mais recente no momento da decisão) por ser a versão **LTS**, alinhada ao requisito de longevidade de um SaaS B2B de longo prazo.

## Motivo da Revisão

Durante a migração do backbone de mensageria (ver ADR-004), foram descobertas três condições que, em conjunto, tornaram a decisão original insustentável:

1. **A versão mais recente do WolverineFx (linha 6.x) não suporta `net8.0`**, exigindo `net9.0` ou `net10.0` como alvo mínimo.
2. **A linha 5.x do WolverineFx (a única compatível com net8.0) é uma branch de manutenção apenas com correção de bugs**, sem novas funcionalidades, com fluxo de patches associado à janela até a disponibilidade geral da 6.0 — já ocorrida.
3. Ao avaliar as alternativas de runtime (.NET 8, .NET 9, .NET 10), verificou-se que **.NET 8 e .NET 9 encerram o suporte oficial da Microsoft na mesma data: 10 de novembro de 2026** — o .NET 9, apesar de mais recente, não oferece nenhuma vantagem de longevidade sobre o .NET 8, tornando-o uma opção estritamente inferior ao .NET 10 LTS (suporte até novembro de 2028) para qualquer decisão tomada a partir de hoje.

## Riscos Identificados na Decisão Original

- Proximidade do fim de suporte do .NET 8 (10/11/2026 — a poucos meses da implementação da Fase 0).
- Incompatibilidade estrutural entre a plataforma-base já escolhida e a linha ativamente mantida do backbone de mensageria já aprovado (Wolverine).
- Rejeição do .NET 9 como alternativa: mesmo fim de suporte do .NET 8, sem nenhum ganho real de longevidade.

## Decisão Atual

- **Linguagem backend:** C# sobre **.NET 10 (LTS)** — substitui o .NET 8.
- **Framework backend:** **ASP.NET Core** — mantido.
- **Linguagem/Framework frontend:** **TypeScript sobre Angular** — mantido.
- **Backbone de mensageria:** Wolverine, agora na linha **6.x** (estável, atualmente mantida) — ver ADR-004 atualizada.

## Impactos e Riscos da Migração

- Todos os 8 projetos da Solution tiveram o `TargetFramework` alterado para `net10.0`.
- Pacotes com forte acoplamento à versão do .NET (Entity Framework Core, Npgsql, Microsoft.Extensions.Hosting, Wolverine e extensões) foram atualizados para suas versões estáveis compatíveis com .NET 10 — nenhuma versão preview, release candidate ou nightly foi utilizada.
- O Wolverine 6.0 removeu o compilador de código em runtime do pacote principal; foi adicionado o pacote oficial `WolverineFx.RuntimeCompilation` para preservar o mesmo comportamento (`TypeLoadMode.Dynamic`) já utilizado — ajuste mecânico, sem mudança de arquitetura.
- Nenhum Bounded Context de negócio existia no momento da migração, o que manteve o esforço concentrado exclusivamente em `BuildingBlocks` e nos processos `Host`.

## Estratégia de Atualização Futura do Runtime

Para evitar repetir esta situação, a atualização do runtime .NET deverá:

1. Ser avaliada sempre que uma nova versão LTS for lançada (ciclo aproximado de 2 em 2 anos), preferindo migrar cedo dentro da janela de sobreposição de suporte entre a LTS atual e a anterior.
2. Nunca depender de uma versão STS (ciclo ímpar) como plataforma-base de longo prazo — apenas versões LTS (ciclo par) deverão ser adotadas como base estável do produto.
3. Verificar, a cada avaliação, a compatibilidade da versão do Wolverine (ou do backbone de mensageria vigente) com a nova LTS antes de migrar, dado o histórico de acoplamento estreito já observado.

## Alternativas Consideradas

- **NestJS + Next.js (TypeScript full-stack):** descartada na decisão original, motivo inalterado (alinhamento com expertise da equipe).
- **.NET 9:** descartado — mesmo fim de suporte do .NET 8 (10/11/2026), sem ganho de longevidade.
- **Permanecer em .NET 8 + Wolverine 5.x:** descartado — reproduziria o mesmo padrão de risco identificado e rejeitado no MassTransit 8.x (versão congelada, com janela de suporte encerrando), desta vez também no próprio runtime.

## Consequências

### Positivas
- Elimina a necessidade de nova migração de runtime no curto/médio prazo (suporte até novembro de 2028).
- Desbloqueia a linha ativamente mantida do Wolverine (6.x), sem comprometer o isolamento arquitetural já estabelecido (ADR-004).
- Aproveitamento integral da expertise da equipe em .NET/C#/Angular — inalterado.

### Riscos Aceitos
- .NET 10 tem menos tempo de maturidade em produção no ecossistema mais amplo do que o .NET 8 tinha em 2023 — mitigado por ser a versão atualmente recomendada pela própria Microsoft e pelo fato de nenhum Bounded Context de negócio ainda depender do runtime em produção.

## Referências
- Documento 11 §33, Documento 12 §23
- Architecture Principles §2 (Modular Monolith)
- ADR-004 (Arquitetura Orientada a Eventos e Processamento Assíncrono)
