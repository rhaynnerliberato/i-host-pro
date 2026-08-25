# ADR-022 — WhatsApp Webhook Security and Tenant Routing Boundary

Status: Aceito
Data: 2026-08-19

## Contexto

Fase 9, Checkpoint 2.3.0 (auditoria read-only) cruzou a documentação oficial da Meta para webhooks (`X-Hub-Signature-256`, verificação GET via `hub.verify_token`/`hub.challenge`, retries por até 7 dias com possível duplicação, `entry[].changes[].value.statuses[]` como formato de status de entrega) com o estado real do código (ADR-015/016, ADR-020, ADR-021, `WhatsAppIntegration`, `ExternalIntegrations.Api`) e encontrou dois problemas estruturais que nenhuma decisão anterior cobria:

1. **Bootstrap de tenant**: `WhatsAppIntegrationRepository.GetForCurrentTenantAsync()` já depende de um `TenantId` resolvido (Global Query Filter/RLS) — não existe hoje nenhuma forma de descobrir "qual tenant" a partir de um `PhoneNumberId` recebido de fora, sem já ter um `TenantId`. Resolver isso com `IgnoreQueryFilters`/BYPASSRLS/superuser foi explicitamente rejeitado pelo usuário.
2. **Durabilidade**: `ExternalIntegrations` nunca foi consumidor/publicador Wolverine — não há outbox Postgres provisionado para este contexto (todo outro contexto tem `EnrollAncillaryPostgresqlOutbox` no `MigrationRunner`; este não tem). Publicar um Integration Event real sem essa infraestrutura arriscaria perda entre commit e publish.

Diante disso, o Checkpoint 2.3 foi dividido em sub-checkpoints (2.3.1 segurança de ingress, 2.3.2 tenant routing, 2.3.3 lifecycle/outbox/eventos, 2.3.4 sandbox real). Esta ADR registra TODAS as decisões arquiteturais já aprovadas para o CP2.3 como um todo — mesmo que o Checkpoint 2.3.1 implemente apenas a fatia de segurança — para que a fronteira completa fique documentada de uma vez, sem decisões implícitas descobertas checkpoint a checkpoint.

## Decisão

1. **Ownership do webhook = External Integrations.** Nenhum outro Bounded Context implementa ou conhece o mecanismo do webhook (mesmo ACL já registrado em ADR-021).
2. **Host físico = `IHostPro.Api`.** `ExternalIntegrations.Api` é uma biblioteca de controllers hospedada dentro do processo `IHostPro.Api` (`AddExternalIntegrationsModule` já registrado em `Program.cs`) — não é um processo/host separado. "Ownership do endpoint" (External Integrations) e "onde ele roda fisicamente" (`IHostPro.Api`) são fatos distintos, ambos registrados aqui para nunca serem confundidos.
3. **O endpoint nunca usa JWT humano.** Ele é alcançado pela própria Meta, não por um usuário autenticado da plataforma — `[AllowAnonymous]` explícito, mesmo não havendo hoje uma `FallbackPolicy` global de autenticação (`AddIdentityJwtBearerAuthentication` não configura uma) — documentado aqui para blindar contra uma futura fallback policy quebrar este endpoint silenciosamente.
4. **GET verification usa Verify Token** (`hub.mode`/`hub.verify_token`/`hub.challenge`, contrato oficial Meta) — resposta é o valor de `hub.challenge` cru (texto puro), nunca JSON, apenas quando `hub.mode == "subscribe"` e o token bate.
5. **POST usa App Secret / `X-Hub-Signature-256`** (HMAC-SHA256 sobre os bytes exatos do corpo recebido) — nunca reserializado antes de verificar.
6. **O corpo bruto é verificado antes de qualquer desserialização confiável.** Nenhum dado do payload é tratado como confiável antes da assinatura ser validada.
7. **A assinatura é verificada antes de qualquer resolução de tenant.** Nenhuma consulta a dado tenant-owned acontece antes desse ponto.
8. **App Secret e Verify Token são app/deployment-level — nunca tenant-level.** Pertencem ao Meta App configurado para esta implantação, não a uma integração de tenant individual. Isso é uma decisão de negócio explícita (não uma inferência do schema existente): o desenho atual de `WhatsAppIntegration` (cada tenant com seu próprio `AccessTokenSecretReference`) não implica automaticamente que App Secret/Verify Token sigam o mesmo padrão — a verificação de assinatura pertence ao endpoint do Meta App, que é compartilhado, não ao tenant.
9. **Os campos `AppSecretSecretReference`/`VerifyTokenSecretReference` já existentes em `WhatsAppIntegration` (CP2.1) não são usados pelo webhook do CP2.3.** Permanecem no agregado (não removidos, sem nova migration), mas o ingress do webhook resolve App Secret/Verify Token exclusivamente via uma abstração nova, app-level — nunca via `IWhatsAppCredentialProvider`/`WhatsAppIntegration` tenant-owned, porque antes de verificar o webhook ainda não existe um `TenantId` confiável para escolher qual integração consultar.
10. **`PhoneNumberId` será a fonte confiável de routing depois da assinatura verificada** (CP2.3.2) — presente em todo payload de status (`metadata.phone_number_id`), nunca antes.
11. **Um routing directory global, deliberadamente não tenant-owned** (`PhoneNumberId → TenantId`) será criado no CP2.3.2 para resolver o bootstrap do item 1 do Contexto — sem BYPASSRLS/`IgnoreQueryFilters`/superuser/desabilitar FORCE RLS. Não implementado neste checkpoint (2.3.1).
12. **Nenhuma exceção de RLS é criada por esta ADR.** BYPASSRLS, `IgnoreQueryFilters`, roles superuser e desabilitar FORCE RLS continuam proibidos em qualquer solução para o item 11 — a tabela de routing resolve o problema por ser, ela mesma, fora do escopo de RLS (não é dado de tenant), nunca por enfraquecer o mecanismo de RLS existente.
13. **Outbox de `ExternalIntegrations` é obrigatório antes do primeiro Integration Event real ser publicado** (CP2.3.3) — este contexto não tem hoje nenhuma infraestrutura de outbox Wolverine (confirmado por auditoria: `MigrationRunner` nunca chama `EnrollAncillaryPostgresqlOutbox` para `ExternalIntegrations`). Publicar sem isso arriscaria perda de evento entre commit e publish.
14. **O evento de status futuro (CP2.3.3) é provider-neutral e PII-safe** — nunca telefone/corpo/secret/payload bruto do webhook (mesmo princípio já registrado em ADR-021, item 10).
15. **A máquina de estados futura (CP2.3.3) é monotônica e idempotente** — uma transição repetida é no-op, nunca um erro.
16. **`Sent → Failed` e `Delivered → Failed` são aprovados** como transições futuras de `Message` (CP2.3.3) — a Meta pode aceitar o envio e reportar falha de entrega depois, de forma assíncrona, e `Delivered` não é considerado terminal: uma notificação `failed` do provedor após `delivered` é informação nova genuína, não descartável. `Read → Failed` é tratado como Regressão/no-op — `Read` é terminal para o lifecycle atual, e um `failed` tardio após leitura confirmada é um callback regressivo, não uma transição real. Hoje `MarkFailed` só permite `Queued|Sending → Failed`, o que precisará ser estendido nesse checkpoint futuro para também aceitar `Delivered → Failed`. Decisão corrigida no Checkpoint 2.3.2.1 — ver nota pós-publicação abaixo.
17. **`Sent → Read` direto é aprovado** — comportamento oficialmente documentado pela Meta (quando "entregue" e "lida" acontecem simultaneamente, o webhook de "entregue" é omitido).
18. **`played` (reprodução de mensagem de voz) é deferido** — CP2.3 MVP é text-only; não modelado sem requisito real.
19. **Nenhuma mensagem inbound de convidado é processada no MVP** — o CP2.3 cobre apenas status de mensagens enviadas pela própria plataforma.

## Alternativas Consideradas

- **Resolver o bootstrap de tenant com `IgnoreQueryFilters()`/BYPASSRLS**: rejeitada explicitamente pelo usuário — enfraqueceria a mesma garantia que ADR-015/016 existem para proteger, e criaria um precedente perigoso de bypass de RLS "só desta vez".
- **App Secret/Verify Token tenant-scoped** (reaproveitar os campos já existentes em `WhatsAppIntegration`): rejeitada para o CP2.3 — exigiria resolver `TenantId` antes de verificar a assinatura, exatamente o problema de bootstrap que este ADR evita; também descasa da natureza real do App Secret (pertence ao Meta App/endpoint, não a uma integração individual).
- **Publicar o Integration Event de status sem outbox, aceitando o risco de perda**: rejeitada — mesma classe de risco que todo outro contexto já mitiga com outbox; não há motivo para `ExternalIntegrations` ser a exceção.
- **Não permitir `Sent → Failed`** (tratar qualquer status pós-Sent como estado terminal já fechado): rejeitada — a Meta genuinamente reporta falha assíncrona depois do aceite síncrono; ignorar esse status descartaria informação real e deixaria `Message` permanentemente com um estado que não reflete a realidade.

## Consequências

### Positivas
- O bootstrap de tenant é resolvido sem enfraquecer RLS em nenhum ponto — a tabela de routing é, por design, fora do escopo de dado tenant-owned.
- App Secret/Verify Token app-level evita reabrir a pergunta de tenant antes de verificar a assinatura — sequência de segurança fica linear e auditável (raw body → assinatura → só então tenant).
- Registrar todas as 19 decisões de uma vez (mesmo cobrindo 2.3.2/2.3.3, ainda não implementados) evita que cada sub-checkpoint futuro precise redescobrir/reabrir a mesma pergunta arquitetural.

### Riscos Aceitos
- O routing directory (item 11) e o outbox (item 13) são pré-requisitos reais para CP2.3.2/2.3.3 — nenhum dos dois existe ainda; esta ADR autoriza a abordagem, não a implementação (que permanece bloqueada até os checkpoints correspondentes serem explicitamente iniciados).
- App Secret/Verify Token app-level pressupõe um único Meta App por implantação — se o modelo de negócio real precisar de múltiplos Meta Apps por implantação (não confirmado nem descartado), esta decisão precisaria ser revisitada antes do CP2.3.4.

## Nota pós-publicação (Checkpoint 2.3.1.1)

Não é uma nova decisão arquitetural — reforça explicitamente uma consequência já implícita nos itens 8/9 acima: Verify Token, App Secret, a assinatura `X-Hub-Signature-256` completa e o corpo bruto do webhook **nunca podem aparecer em nenhum log gerado pelo host** (`IHostPro.Api`), não apenas nas linhas de auditoria estruturada do próprio controller. O fechamento do CP2.3.1 encontrou uma violação real desse princípio: o log embutido `Microsoft.AspNetCore.Hosting.Diagnostics` ("Request starting"/"Request finished") registra a URL completa, incluindo query string — expondo `hub.verify_token` no handshake GET. Não existe mecanismo oficial do ASP.NET Core para suprimir esse log especificamente por rota (confirmado via investigação e documentação oficial da Microsoft) — apenas configuração de `LogLevel` por categoria, necessariamente global. Correção aplicada: `Microsoft.AspNetCore.Hosting.Diagnostics` elevado para `Warning` (mais específico que o `Microsoft.AspNetCore: Warning` já existente na base, o que garante precedência sobre o override de Development que o reabria para `Information`) — em `src/Host/IHostPro.Api/appsettings.json`, aplicando-se a todos os ambientes. Ver `Fase 9 - Comunicacao e Integracoes do MVP - Validacao e Homologacao.md`, Checkpoint 2.3.1.1, para o registro completo da investigação e da prova real de pipeline.

## Nota pós-publicação (Checkpoint 2.3.2)

Não é uma nova decisão arquitetural — confirma empiricamente que o item 11/12 acima (routing directory global, sem RLS, mesmo `ExternalIntegrationsDbContext`) era realmente implementável sem hack, conforme o item 6 desta ADR exigia verificar antes de prosseguir. Confirmado lendo `BaseDbContext.OnModelCreating`: o Global Query Filter tenant é aplicado apenas a tipos que implementam `ITenantOwned` — um tipo que não implementa essa interface simplesmente nunca recebe o filtro, sem qualquer alteração em `BaseDbContext`. Confirmado também que RLS/`CREATE POLICY` são SQL explícito por tabela, escrito na própria migration (nunca aplicado globalmente por convenção automática) — logo `whatsapp_tenant_routes` nunca recebe `ENABLE ROW LEVEL SECURITY` simplesmente por não incluir essas linhas na sua migration. Nenhum segundo `DbContext` foi necessário; nenhum `BYPASSRLS`/`IgnoreQueryFilters`/superuser foi usado em nenhum momento — provado por teste real contra Postgres (`Row_Level_Security_is_NOT_enabled_on_whatsapp_tenant_routes`, `A_route_created_for_one_tenant_is_visible_from_a_DIFFERENT_tenants_context`). Ver `Fase 9 - Comunicacao e Integracoes do MVP - Validacao e Homologacao.md`, Checkpoint 2.3.2, para o registro completo.

## Nota pós-publicação (Checkpoint 2.3.2.1)

Não é uma nova decisão arquitetural sobre o webhook em si — é uma correção de governança sobre o item 16 acima. O relatório final do Checkpoint 2.3.2 publicou `WhatsAppStatusTransitionClassifier` classificando `Delivered → Failed` e `Read → Failed` ambos como Regressão, mas o próprio relatório reconheceu essa regra como "um default raciocinado, não uma regra já aprovada explicitamente" — o mandato do CP2.3.2 exigia PARAR antes de codificar semântica definitiva quando isso ocorresse, o que não foi respeitado antes da publicação. O código permaneceu publicado (sem rollback/rebase/force-push), pendente de homologação corretiva.

Decisão explícita tomada agora: `Sent → Failed` = Forward (inalterado); `Delivered → Failed` = Forward (corrigido — `Delivered` não é terminal); `Read → Failed` = Regressão/no-op (`Read` é terminal para o lifecycle atual). `Failed` continua terminal em todas as direções — nenhuma transição a partir de `Failed` avança. `WhatsAppStatusTransitionClassifier` foi corrigido fix-forward (mesmo tipo, sem novo classifier, sem novo `ProviderMessageStatus`) e sua matriz completa de 13 combinações foi provada por teste determinístico. Nenhuma mudança em routing table, migrations, RLS, webhook ingress, assinatura, credential provider, contrato de API, Wolverine ou outbox — o classifier ainda não está conectado a nenhum estado persistido (isso é CP2.3.3). Ver `Fase 9 - Comunicacao e Integracoes do MVP - Validacao e Homologacao.md`, Checkpoint 2.3.2.1, para o registro completo, incluindo a cronologia honesta do gate não respeitado.

## Nota pós-publicação (Checkpoint 2.3.3)

Não é uma revisão das 19 decisões originais — confirma sua implementação (outbox item 13, routing/event provider-neutral item 14) e registra UM achado arquitetural real, não previsto explicitamente pelos itens originais: um único webhook da Meta pode legitimamente lotear entradas de status de MÚLTIPLOS tenants no mesmo payload (consequência direta do item 8 — App Secret/Verify Token são app/deployment-level, então vários tenants compartilham o mesmo endpoint). O design inicial do publisher do evento reutilizava o `ITenantContext` escopado por requisição — mas essa abstração recusa deliberadamente ser re-setada para um tenant diferente na mesma instância (proteção contra contaminação cross-tenant, não um defeito). Corrigido antes de qualquer commit: `WhatsAppWebhookStatusEventPublisher` abre um child DI scope novo por outcome, mesmo padrão já estabelecido por `CommunicationMessageExecutionScope` (ADR-016) — é agora a única classe em External Integrations autorizada a segurar `IServiceScopeFactory`, garantido por ArchitectureTest dedicado. Provado por teste unitário processando dois outcomes de tenants diferentes na mesma instância do publisher sem exceção.

Decisão de governança adicional, tomada explicitamente antes de codificar (mandato do CP2.3.3 exigia isso como gate): quando o consumer de Communication não encontra `Message` para um `ProviderMessageId`, o comportamento é falha retriável (nunca no-op permanente) — o commit de `Sent`+`ProviderMessageId` só acontece depois do round-trip HTTP com a Meta completar, sem garantia de ordenação com a chegada do webhook, uma race genuína confirmada por leitura do código real, não hipotética.

Ver `Fase 9 - Comunicacao e Integracoes do MVP - Validacao e Homologacao.md`, Checkpoint 2.3.3 (§12.6), para o registro completo.

## Nota pós-publicação (Checkpoint 2.3.3.1) — Correção da política de retry do missing-Message

Não é uma nova decisão de negócio — a política em si (falha retriável, nunca no-op permanente) permanece a mesma, decidida explicitamente antes da implementação. O que foi corrigido foi o COMPORTAMENTO REAL por trás dela: o relatório final do CP2.3.3 afirmou que "a política padrão de retry/DLQ do Wolverine trata a race transitória... sem loop infinito" — essa afirmação nunca foi verificada empiricamente e estava incorreta.

Auditoria corretiva, via duas reproduções diretas e isoladas (nunca simuladas/mockadas — um host Wolverine mínimo real, primeiro com transporte local, depois repetido com a mesma configuração `PersistMessagesWithPostgresql` que a produção usa): o default real do Wolverine 6.22.0 para uma exceção não tratada em um handler, sem nenhuma política customizada (confirmado por busca em todo o código-fonte), é **exatamente UMA tentativa, seguida de movimentação IMEDIATA e permanente para a tabela de dead letters** — nunca um retry com backoff. A cifra "MaximumAttempts=3, delays 5s/30s/5min" citada anteriormente (de uma busca na web) não se aplica a este cenário — não foi possível determinar a que ela realmente se refere, mas duas reproduções diretas concordam entre si e contradizem essa cifra.

Isso significa que a política pretendida (auto-recuperação via retry) nunca foi de fato realizada: uma race genuína caía na tabela de dead letters já na primeira tentativa, exigindo replay manual em vez de auto-cura.

Decisão corretiva (Wolverine's own native handler-chain policy API, mandato §3 — "usar a política Wolverine normal/existente", nunca inventar uma arquitetura de retry customizada): `WhatsAppMessageStatusChangedHandler` (Communication.Infrastructure.Messaging) ganhou um método estático `Configure(HandlerChain chain)` — três retries com backoff curto (250ms/1s/3s, ~4,25s no total, quatro tentativas), escopado a este ÚNICO handler chain (nenhuma política global). Proporcional à janela real da race (um round-trip HTTP + um commit de uma única linha — segundos, não minutos): curto o suficiente para que um `ProviderMessageId` permanentemente órfão ainda chegue ao handling terminal do Wolverine em segundos, longo o suficiente para dar à race uma chance real de se resolver sozinha.

Reconfirmado empiricamente (mesma técnica de reprodução direta, agora usando o handler real): quatro tentativas registradas, depois movido para a fila de erro — exatamente o comportamento pretendido.

Ver `Fase 9 - Comunicacao e Integracoes do MVP - Validacao e Homologacao.md`, Checkpoint 2.3.3.1, para o registro completo, incluindo a cronologia honesta.

## Nota pós-publicação (Checkpoint 2.3.3.1, segunda correção) — Escopo da exceção específica demais amplo

A primeira correção acima escopou a política a `chain.OnException<InvalidOperationException>()` — amplo demais: qualquer `InvalidOperationException` que este método viesse a lançar por qualquer outro motivo, presente ou futuro, receberia silenciosamente o mesmo tratamento de retry, mesmo sem relação com a race missing-Message.

Corrigido fix-forward: `WhatsAppMessageNotYetAvailableException` (`Communication.Application`) — tipo próprio, dedicado exclusivamente a "ProviderMessageId válido recebido, porém Message ainda não está visível". `WhatsAppMessageStatusCommunicationProcessor` agora lança exclusivamente esse tipo para o caso missing-Message; `WhatsAppMessageStatusChangedHandler.Configure` foi reescopado para `chain.OnException<WhatsAppMessageNotYetAvailableException>()`. Números de retry inalterados (250ms/1s/3s, quatro tentativas). Nenhuma outra mudança.

Provado por teste real (Wolverine host real, sem mock, sem inspeção de internals): a exceção específica recebe as quatro tentativas configuradas; uma `InvalidOperationException` genérica e não relacionada, de uma handler chain completamente diferente, recebe apenas uma tentativa — o comportamento default do Wolverine, confirmando que a política não vaza para fora do escopo pretendido.

## Referências
- ADR-012 (precedente de abstração de credencial Development-only, sem fallback de Production)
- ADR-015, ADR-016 (limite tenant-safe para consumers que tocam `BaseDbContext`)
- ADR-020 (isolamento de handler chain Wolverine)
- ADR-021 (External Integrations ACL e fronteira síncrona; separação de eventos PII-safe já catalogada)
- `Fase 9 - Comunicacao e Integracoes do MVP - Validacao e Homologacao.md`, Checkpoint 2.3.0 (auditoria que originou esta ADR) e Checkpoint 2.3.1 (primeira implementação real)
- `Documento 07` §10/§16 (catálogo de eventos)
- `Documento 06` §9 (máquina de estados conceitual)
