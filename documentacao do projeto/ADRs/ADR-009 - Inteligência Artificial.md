# ADR-009 — Inteligência Artificial

Status: Aceito
Data: 2026-07-26

## Contexto

O Documento 16 exige que o Agente IA seja tratado como um módulo da plataforma, nunca com chamadas diretas dispersas ao modelo de linguagem, e que o modelo utilizado não fique fixo na arquitetura (§26).

## Decisão

- **Bounded Context dedicado:** AI Agent, contendo AI Gateway, Context Builder e Tools.
- **Provedor inicial:** Anthropic Claude.
- **Mecanismo de integração:** chamada direta à API REST da Anthropic via `HttpClient`, **sem SDK de terceiros**, dado que não existe SDK oficial da Anthropic para C# (apenas pacotes não-oficiais, com risco de defasagem de manutenção).
- **Abstração:** interface `IModelProvider`, permitindo adicionar outros provedores (ex.: OpenAI) como implementações alternativas sem alterar o domínio.
- O AI Agent nunca acessa domínio/infraestrutura de outros contextos diretamente — interage exclusivamente através de **Tools**, adapters finos que invocam o Application Service público do contexto correspondente (Documento 13 §30).

## Alternativas Consideradas

- **SDK de terceiros não-oficial para Anthropic em C#:** descartado pelo risco de manutenção inconsistente em um componente central da plataforma.
- **Modelo fixo na arquitetura:** descartado — viola exigência explícita do Documento 16 §26.

## Consequências

### Positivas
- Nenhuma dependência de pacote não-oficial no caminho crítico do produto.
- Troca de provedor de IA é uma mudança de configuração/implementação isolada, não uma mudança arquitetural.

### Riscos Aceitos e Divulgação de Conflito de Interesse
- A recomendação do Anthropic Claude como provedor padrão foi feita por uma IA da própria Anthropic — este é um conflito de interesse potencial, explicitamente divulgado durante a análise de stack. Recomenda-se validação independente de custo/benchmark antes de qualquer decisão de reforço desta escolha em produção.

## Referências
- Documento 13 §30, Documento 16
- Architecture Principles §14
