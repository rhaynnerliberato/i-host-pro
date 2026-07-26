# ADR-006 — Cache e Armazenamento de Arquivos

Status: **Atualizado** (situação do MinIO Community Edition documentada; decisão original preservada)
Data original: 2026-07-26
Data desta revisão: 2026-07-26

## Contexto

O Documento 08 (§25) exige resolução de configuração efetiva em menos de 50ms. O Documento 11 (§18) e o Documento 12 (§15) exigem que arquivos (fotos, vídeos, documentos) nunca sejam armazenados na base transacional.

## Decisão Original (2026-07-26)

- **Cache:** Redis — usado para cache de configuração/política resolvida, revogação de refresh token e rate limiting. Não é usado como fila (papel absorvido pelo backbone de mensageria, ver ADR-004).
- **Armazenamento de arquivos:** Object Storage compatível com S3 — **AWS S3 em produção**, **MinIO em desenvolvimento/homologação** — acessado via um contrato público do contexto **Files**, nunca implementado individualmente por outros contextos.

## Situação Encontrada: Encerramento do MinIO Community Edition

Durante a correção de reprodutibilidade da Fase 0 (fixação de tag de imagem Docker), constatou-se que:

- O repositório open-source do MinIO foi colocado em modo de manutenção em dezembro de 2025 e **arquivado formalmente em 25 de abril de 2026** — somente leitura, sem novas releases, sem revisão de patches.
- O MinIO **parou de publicar imagens Docker no Docker Hub e no Quay.io em outubro de 2025**, sem aviso prévio.
- A última imagem oficial publicada é `minio/minio:RELEASE.2025-09-07T16-13-09Z` (confirmado via API do Docker Hub — mesmo digest da antiga tag `latest`) — **não haverá nenhuma nova versão oficial no futuro**.
- O código-fonte permanece nominalmente AGPLv3 (não houve mudança de licença), mas a distribuição binária/imagem passou a depender de builds próprios ou de forks comunitários não-oficiais.

## Impacto para Ambientes Locais

- Afeta **exclusivamente os ambientes de desenvolvimento e homologação** — a imagem MinIO é usada apenas no `docker-compose.yml` local.
- **A produção nunca utilizou nem utilizará o MinIO** — o Documento 11/ADR-006 original já definia AWS S3 como a solução de produção; isso permanece inalterado.
- Nenhum código da aplicação referencia o MinIO diretamente (acesso sempre via contrato do contexto Files, compatível com a API S3) — uma eventual substituição futura não exigiria alteração de código de negócio, apenas da configuração de infraestrutura local.

## Justificativa para Manter Temporariamente a Última Versão Oficial

- A imagem `RELEASE.2025-09-07T16-13-09Z` continua funcional, compatível com a API S3 já utilizada, e atende integralmente à necessidade atual (armazenamento de objetos em ambiente local, sem tráfego real de produção).
- Não há, hoje, nenhum caso de uso implementado que dependa de uma funcionalidade do MinIO não coberta por essa versão.
- Trocar a ferramenta de storage local agora, sem necessidade concreta, seria uma alteração de infraestrutura especulativa — contrário ao princípio de mudança mínima e proporcional (Engineering Constitution §14-16).

## Riscos Conhecidos

- **Ausência definitiva de correções de segurança futuras** para a imagem fixada — diferente de outras dependências já revisadas neste projeto (ex.: MassTransit 8.x, Wolverine 5.x), que ainda tinham janelas de manutenção; o MinIO CE não terá nenhuma atualização futura pela fonte oficial.
- Risco de a imagem eventualmente ser removida do Docker Hub (já ocorreu remoção de publicação de novas tags; não há garantia de que tags antigas permaneçam disponíveis indefinidamente).
- Divergência crescente, ao longo do tempo, entre o comportamento do MinIO local (congelado) e o comportamento real da AWS S3 em produção (que continua evoluindo) — risco de a paridade dev/produção se degradar gradualmente.
- Risco de a comunidade migrar para forks não-oficiais sem o mesmo nível de confiança/auditoria de um projeto mantido oficialmente.

## Critérios que Motivarão uma Futura Substituição

A ferramenta de storage local deverá ser reavaliada quando **qualquer** um dos critérios abaixo ocorrer (não são gatilhos automáticos — apenas sinais documentados para orientar uma decisão futura):

- A imagem `RELEASE.2025-09-07T16-13-09Z` deixar de estar disponível para pull no Docker Hub.
- Uma vulnerabilidade de segurança relevante for identificada nessa versão, sem possibilidade de correção.
- A divergência de comportamento entre o MinIO local e a AWS S3 real causar um bug ou retrabalho concreto em desenvolvimento/homologação.
- A equipe precisar de uma funcionalidade S3 não suportada por essa versão congelada.

## Alternativas Candidatas (não avaliadas em profundidade — apenas registradas para quando houver necessidade real)

- **SeaweedFS** — object storage distribuído open-source, compatível com API S3, ativamente mantido.
- **Garage** — object storage distribuído, leve, compatível com API S3, focado em simplicidade operacional.
- **LocalStack (S3)** — emulador de serviços AWS, incluindo S3, amplamente usado especificamente para paridade de desenvolvimento local com a AWS real.
- **Build próprio do MinIO a partir do código-fonte (AGPLv3)** — preserva a ferramenta já conhecida pela equipe, ao custo de manter o processo de build internamente.

Nenhuma dessas alternativas foi comparada tecnicamente nesta revisão — a comparação (licença, maturidade, esforço de migração, fidelidade à API S3) deverá ser conduzida apenas quando um dos critérios de substituição acima for atingido.

## Decisão Atual

- Manter o MinIO na versão fixa `RELEASE.2025-09-07T16-13-09Z` para desenvolvimento/homologação.
- **Não há urgência para substituição neste momento.**
- A discussão sobre uma alternativa permanece formalmente em aberto, documentada nesta ADR, a ser retomada apenas quando um dos critérios de substituição for atingido — não antecipadamente.
- **AWS S3 em produção permanece inalterado e não é afetado por nenhuma parte desta revisão.**

## Consequências

### Positivas
- Nenhuma mudança de infraestrutura especulativa; esforço de engenharia permanece concentrado nos Bounded Contexts de negócio.
- Redis com responsabilidade enxuta e focada, reduzindo risco de contenção de recursos (decisão original inalterada).
- Isolamento do contexto Files já protege a plataforma de uma eventual troca futura da ferramenta local.

### Riscos Aceitos
- Ver seção "Riscos Conhecidos" acima — aceitos conscientemente por afetarem apenas ambientes não-produtivos e por não haver alternativa oficial mantida disponível hoje.

## Referências
- Documento 08 §24-26, Documento 11 §16, §18, Documento 12 §15, Documento 19 §14
- Architecture Principles §12
