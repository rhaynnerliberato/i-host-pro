# ADR-032 — Airbnb Email Bridge: Web OAuth e Conexão de Mailbox via Microsoft Graph

Status: Aceito
Data: 2026-09-18

## Contexto

O Airbnb Email Bridge é uma capability construída ao longo de várias sessões anteriores a esta ADR, sem nunca ter recebido uma ADR dedicada — esta ADR fecha essa lacuna documental, registrando formalmente uma arquitetura já implementada e agora provada de ponta a ponta, e não uma decisão nova a ser tomada.

**Importante — esta ADR não é sobre a Airbnb API/parceria.** ADR-023 (Airbnb Integration Boundary and Reservation Import) documenta a ausência de qualquer acesso à API oficial da Airbnb (`AirbnbPartnerAccessAvailable=false`) e permanece inalterada e correta: nenhum contrato de parceria Airbnb existe, nenhum cliente HTTP fala diretamente com a Airbnb, nenhum OAuth é feito **com a Airbnb**. O Email Bridge resolve um problema diferente: um tenant conecta sua própria caixa de e-mail real (Outlook/Hotmail/Microsoft 365) via **Microsoft Graph**, para onde a Airbnb já envia notificações de reserva por e-mail hoje. O sistema lê essa caixa (delegated `Mail.Read`, nunca full-mailbox), interpreta os e-mails de lembrete de reserva que a Airbnb já envia, e publica reservas usando exatamente o mesmo pipeline de domínio de uma reserva manual. O `AirbnbIntegration`/`AirbnbListingMapping` de ADR-023 é um agregado inteiramente diferente de `AirbnbEmailMailboxConnection`/`AirbnbListingTitleMapping` desta ADR — duas tabelas distintas no schema `external_integrations`, sem nenhuma relação de banco entre elas.

O motivo de existir dois caminhos de autenticação MSAL (público local + confidencial web) é operacional: a capability nasceu com um fluxo interativo local (`AirbnbEmailBridgeController.Connect`, `PublicClientApplication` + `AcquireTokenInteractive`, abre um browser no host do processo Api) — funcional apenas contra uma Api rodando localmente, nunca contra um frontend multi-tenant real implantado. Esta ADR formaliza a extensão que resolve essa lacuna: um fluxo Web OAuth self-service (Authorization Code + PKCE, `ConfidentialClientApplication`) que o próprio tenant conclui pelo navegador, sem nenhum passo de engenharia.

## Decisão

### Dois fluxos MSAL, uma única regra de seleção determinística

`MsalAirbnbEmailAuthenticator.AcquireTokenSilentAsync` (`.../Infrastructure/AirbnbEmailBridge/MsalAirbnbEmailAuthenticator.cs`) seleciona `IConfidentialClientApplication` quando `AirbnbEmailBridgeOptions.ClientSecret` está configurado, e `IPublicClientApplication` caso contrário — para **qualquer** conta em cache, independentemente de qual fluxo originalmente a conectou (local interativo ou Web). Esta é uma regra determinística e explícita, não um campo de proveniência (`AuthenticationMode`) inventado antecipadamente: a hipótese foi validada pela prova real de ponta a ponta descrita abaixo antes de ser aceita como suficiente. Se um cenário futuro provar essa regra insuficiente, a extensão correta é adicionar proveniência explícita — não presumida agora.

O fluxo local interativo (`AirbnbEmailBridgeController.Connect`, `PublicClientApplicationBuilder`, browser no host do processo) permanece intacto e é a única forma suportada de conectar uma mailbox em desenvolvimento local sem passar pelo fluxo Web — nunca removido por esta ADR.

### Fluxo Web OAuth self-service (Authorization Code + PKCE)

- `POST oauth/start` (autenticado, `AirbnbEmailWebOAuthController`) gera `state` (32 bytes aleatórios, base64url) e um PKCE `code_verifier`; persiste apenas `SHA256(state)` em hex (nunca o valor bruto — mesmo padrão de `RefreshToken.TokenHash`) e o `code_verifier` **criptografado** (AES-256-GCM, `AesGcmOAuthTransactionSecretProtector`, mesma chave `TokenCacheEncryptionKeyBase64` já usada para o cache de token) em `AirbnbEmailOAuthTransaction`, com `ExpiresAtUtc = now + 10 minutos` (`StartAirbnbEmailWebOAuthCommandHandler.StateLifetime`). Retorna a URL de autorização real da Microsoft; o browser navega para lá via `window.location.href` (nunca um iframe/popup).
- `AirbnbEmailOAuthTransaction` é deliberadamente **não** `ITenantOwned` e sua tabela **não** tem RLS — no momento do callback nenhum tenant é conhecido ainda, e o filtro global de tenant do `BaseDbContext`/RLS bloquearia qualquer leitura antes disso (circularidade identificada e corrigida antes da implementação, não descoberta em produção).
- `GET oauth/callback` (`[AllowAnonymous]`) hasheia o `state` recebido, consome a transação atomicamente via `UPDATE ... WHERE consumed_at_utc IS NULL AND expires_at_utc > now RETURNING ...` (`AirbnbEmailOAuthTransactionRepository`, nunca um SELECT seguido de UPDATE — fecha a janela de corrida de uso duplo). Só **depois** do consumo bem-sucedido é que `ITenantContext.SetTenant(tenantId)` é chamado (`AirbnbEmailWebOAuthCallbackProcessor`) — o tenant nunca vem de dado fornecido pelo cliente, sempre da transação já consumida e confiável.
- O processor do callback (camada Infrastructure) é chamado diretamente pelo controller, contornando `IExternalIntegrationsRequestDispatcher`/pipeline de Mediator — todo `IPipelineBehavior` existente assume `ITenantContext.IsResolved=true` antes do handler rodar, o que nunca é verdade para um callback anônimo. Exceção estreita e documentada aqui, não um padrão a generalizar.
- A troca do `authorization_code` por token usa `IConfidentialClientApplication.AcquireTokenByAuthorizationCode(scopes, code).WithPkceCodeVerifier(verifier).ExecuteAsync()` — API real do MSAL.NET, confirmada por compilação e pelo smoke real (não uma suposição de API).
- `WebFrontendReturnUrl` (config não-secreta nova) resolve a diferença de origem entre a Api e o frontend Angular implantados (CloudFront/S3 vs ALB em homolog) — quando ausente, o callback retorna `NotFound()` (nunca um 500).

### Persistência do estado OAuth e do cache de token

`AirbnbEmailOAuthTransaction` guarda só o necessário para o bootstrap (state hash, PKCE verifier protegido, tenant/ator alvo, expiração) — nunca o `code`/token da Microsoft. O cache de token MSAL persistido (`AirbnbEmailMailboxConnection.TokenCacheBlob`) é criptografado (AES-256-GCM) e compartilhado entre Api e Worker através da mesma `TokenCacheEncryptionKeyBase64` — sem essa chave idêntica em ambos os processos, o Worker não consegue decifrar um cache criado pela Api (e vice-versa).

### Autenticação silenciosa no Worker (Stage 5/5, prova real)

`AirbnbEmailDeltaPollingBackgroundService` (Worker, sempre registrado, no-op quando `PollingEnabled=false` ou `PollingTenantIds` vazio) chama `IAirbnbEmailDeltaSyncRunner.RunAsync` por tenant configurado, a cada `PollingIntervalSeconds` (default 120s), sempre iniciando por um tick imediato ao subir o processo. A prova real de que o cache criado pelo fluxo Web é consumível pelo processo do Worker (não só pela Api que o criou) foi obtida com um processo **novo** do Worker, `ClientSecret` configurado em ambos, e nenhuma interação humana possível (processo headless): logs reais mostraram `AcquireTokenSilent` bem-sucedido, seleção do caminho confidencial pela regra determinística, e acesso real ao Microsoft Graph (25 páginas de delta processadas, 10 mensagens por página, limite de segurança de paginação por execução atingido de forma graciosa — não uma falha).

### Interpretação de e-mail — parser v1, sem generalização especulativa

`AirbnbReservationReminderParser` (`ParserVersion = "airbnb-reservation-reminder-v1"`) interpreta exclusivamente o template de "lembrete de reserva/check-in" em português que a Airbnb já envia — não o e-mail original de nova reserva, não cancelamento, não mensagem de hóspede, não pagamento/avaliação. Extrai nome do anúncio, contagem de hóspedes, código de confirmação, datas/horários de check-in/check-out e nome do hóspede (com fallback de assunto comprovadamente pouco confiável — 0/5 amostras reais, mantido apenas como último recurso). Qualquer e-mail sem os marcadores estruturais esperados retorna `UnsupportedTemplate`, registrado como `AirbnbEmailMessageReceipt.MarkFailed` (nunca `Ignored` — `Ignored` é reservado exclusivamente ao corte histórico do auto-publish) — não existe hoje um classificador de conteúdo com evidência real suficiente para distinguir "e-mail Airbnb legítimo não é reserva" de "template de reserva genuinamente não reconhecido", e inventar essa distinção sem essa evidência não é feito por esta ADR.

### Mapeamento de anúncio → Property, resolução e publicação

`AirbnbListingTitleMapping` é um mapeamento exato (normalizado por trim/colapso de espaço, nunca fuzzy/aproximado), por tenant, criado manualmente por um admin (`Create`+`List` apenas — sem update/delete ainda, por desenho). Um título sem mapeamento resulta em `AirbnbEmailMessageReceipt.MarkNeedsReview` — nunca uma resolução adivinhada. Quando resolvido, a publicação passa por `Reservation.CreateImported(...)`, uma factory separada de `Reservation.Create(...)` (mesma distinção já registrada em ADR-023, `ReservationSource.Airbnb` vs `Manual`), idempotente por `(ReservationSource.Airbnb, ExternalReservationId)`, publicando o mesmo `ReservationCreated` consumido pelos mesmos quatro consumidores já existentes (Housekeeping/Dashboard/Workflow/Communication) — nenhum novo consumidor, nenhuma reabertura de ADR-020.

### Publicação automática — opt-in explícito com corte histórico obrigatório

`EnableAirbnbAutoPublicationCommand` exige `NotBeforeUtc` — o handler rejeita um corte ausente (`AutoPublicationCutoffRequired`), nunca assume um default. Em runtime, só mensagens com `messageReceivedAtUtc >= AutoPublishNotBeforeUtc` chegam à decisão de publicar; qualquer mensagem mais antiga é `MarkIgnored`. Como o sync runner nunca reprocessa uma mensagem já com recibo, apenas mensagens genuinamente novas após a ativação são candidatas — nenhum e-mail histórico é publicado retroativamente ao ativar o opt-in.

## Alternativas Consideradas

- **Adicionar um campo `AuthenticationMode`/proveniência de conexão desde já**: rejeitada por ora — a regra determinística (`ClientSecret` configurado → confidencial) foi validada por prova real de ponta a ponta antes de ser aceita; adicionar proveniência sem evidência de que a regra simples é insuficiente seria inventar complexidade não solicitada. Revisar apenas se um cenário futuro real provar a regra simples insuficiente.
- **UPDATE SQL direto para resolver a circularidade de RLS do bootstrap**: rejeitada — a tabela de transação OAuth é deliberadamente não-tenant-owned/sem RLS, com segurança vindo da superfície estreita do repositório (uma única operação atômica `UPDATE ... RETURNING`), não de uma exceção de acesso a uma tabela RLS-protegida.
- **Roteirizar o callback pelo pipeline normal de Mediator/`IPipelineBehavior`**: rejeitada — todo behavior existente assume tenant já resolvido; forçar isso exigiria enfraquecer essa garantia para todos os outros handlers. A chamada direta ao processor pela camada Api é uma exceção estreita e documentada, não um padrão a generalizar.
- **Classificar e-mails não-reserva (pagamento, avaliação, mensagem) como `Ignored` em vez de `Failed`**: rejeitada por falta de evidência real suficiente para distingui-los com confiança de um template de reserva genuinamente quebrado; ambos permanecem `Failed`/revisão manual até essa evidência existir.

## Consequências

### Positivas
- Conexão self-service real, sem nenhum passo de engenharia, funcional contra um frontend multi-tenant implantado — a lacuna documentada desde a criação do fluxo local interativo está fechada.
- Reaproveitamento total da infraestrutura de persistência de token/criptografia já homologada (`AesGcmPayloadCipher`, `TokenCacheEncryptionKeyBase64`) — nenhuma segunda forma de cache de token foi criada.
- Prova real de interoperabilidade entre processos (Api cria a conexão via Web OAuth; um processo Worker inteiramente novo autentica silenciosamente e acessa o Graph) — não uma suposição, um smoke real documentado nesta sessão.
- `ReservationCreated`/ADR-020 permanecem intocados; a publicação de reserva Airbnb via e-mail usa exatamente o mesmo pipeline de domínio de uma reserva manual.

### Riscos Aceitos
- `RefreshRedemptionProven=false`: a prova real do Worker (Stage 5/5) confirmou `AcquireTokenSilent` bem-sucedido e acesso real ao Graph, mas não forçou/observou explicitamente uma renovação via refresh token (o token de acesso ainda não havia expirado). Isso não bloqueia esta ADR — a renovação natural será observável quando o token expirar organicamente; manipular a expiração do cache apenas para provar isso não foi feito.
- O parser v1 cobre exclusivamente o template de lembrete de reserva em português — cancelamento, mensagens de hóspede, pagamento/avaliação e um eventual "v2" de template permanecem inteiramente fora do escopo, sem parser, sem data prevista.
- `AirbnbListingTitleMapping` é confiança explícita e manual — não existe validação cross-context contra `PropertyManagement` além do `PropertyId` informado pelo admin ao criar o mapeamento.
- `AirbnbEmailSyncState.Reset` existe mas não é invocado por nenhum fluxo ainda (o fluxo de reconexão que o chamaria não existe) — registrado como código presente sem caller real ainda.
- `ProductionReady=false` permanece válido para toda a plataforma; nada nesta ADR implica ambiente de produção real, conta AWS ativa, ou qualquer decisão de infraestrutura de produção.

## Referências
- ADR-023 (Airbnb Integration Boundary and Reservation Import) — limite explícito com este ADR: ADR-023 é sobre a ausência de API/parceria Airbnb; esta ADR é sobre OAuth com a Microsoft para leitura de mailbox.
- ADR-005 (Autenticação e Autorização) — `Argon2PasswordHasher`/precedentes de hashing e proteção de segredo reaproveitados indiretamente via `AesGcmPayloadCipher`.
- `RefreshToken.TokenHash` — precedente já homologado de nunca persistir um valor de correlação em texto puro, reaproveitado pelo `state` do OAuth transaction.
- `documentacao do projeto/Fase 12 - Hardening, Deploy e Piloto do MVP - Validacao e Homologacao.md` — registro operacional do gate "Airbnb Email Bridge Web OAuth Multi-Tenant Connect", incluindo a prova real de ponta a ponta.
