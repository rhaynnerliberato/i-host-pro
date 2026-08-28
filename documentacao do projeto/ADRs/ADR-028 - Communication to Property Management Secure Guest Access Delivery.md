# ADR-028 — Communication to Property Management Secure Guest Access Delivery

Status: Aceito
Data: 2026-08-28

## Contexto

Fase 10, Checkpoint 6 (Final Homologation Read-Only Gate) auditou o escopo literal da Fase 10 e concluiu que Access Credential/"Enviar senha" (Documento 10 §11) era um requisito interno da Fase 10 ainda em aberto — não um blocker externo/de produção, mas um sub-gate que os próprios documentos (ADR-024 §A7, Fase 10 §4.7) já registravam como necessário "antes da homologação final da Fase 10 como um todo". O Checkpoint 6.1 (Access Credential & Remaining Scope Decision Gate, read-only) resolveu o significado de produto ("Fechadura" — Documento 12 §5 — é um atributo do Imóvel: senha fixa por Property, configurada manualmente, sem Smart Lock, sem geração automática), o ownership (Property Management, mesmo padrão de `FrontDeskContact`), e identificou o risco central de design: `Communication.Message.RenderedContent` é persistido em texto puro (confirmado por auditoria de código), o que tornaria uma credencial de acesso permanentemente armazenada em `communication.messages` se entregue pelo mesmo caminho do QR PIX (ADR-025).

Esta ADR resolve a lacuna: como Communication resolve o par credencial/instruções de um Property sem (a) persistir a credencial real em nenhuma tabela, (b) consultar `PropertyManagementDbContext`/o schema `property_management` diretamente, ou (c) reutilizar a convenção `*SecretReference` desenhada para segredos de provider externo (Meta/WhatsApp) para um dado de negócio tenant-owned de natureza distinta.

## Decisão

Está aprovada uma décima segunda exceção síncrona, estrita e específica: **Communication pode consultar Property Management exclusivamente para resolver a credencial de acesso e/ou as instruções de acesso ATUAIS de um Property, necessário à entrega explícita solicitada por um operador via `GuestAccessDeliveryRequested`** — nunca para qualquer outra finalidade.

A exceção obedece obrigatoriamente a:

1. **Contrato público em `PropertyManagement.Contracts`** — `IPropertyGuestAccessReader` e `PropertyGuestAccessReadResult`, mirroring a forma de `IFrontDeskContactReader`/`FrontDeskContactReadResult` (ADR-026).
2. **Implementação somente em `PropertyManagement.Infrastructure`** — `PropertyGuestAccessReader`, único implementador permitido. Resolve internamente o `IPropertyAccessCredentialProvider` (ver item 6) — o valor resolvido nunca é re-persistido em nenhuma tabela de Property Management, existe apenas no resultado retornado, em memória, pela duração da chamada.
3. **Communication não referencia** `PropertyManagement.Domain`, `PropertyManagement.Application`, `PropertyManagement.Infrastructure` ou `PropertyManagementDbContext`/o schema `property_management` diretamente — apenas `PropertyManagement.Contracts`. Regra validada automaticamente por `IHostPro.ArchitectureTests` (NetArchTest).
4. **Entrada mínima**: `TenantId`, `PropertyId`.
5. **Resposta mínima**: `AccessCredential` (string?, o valor JÁ RESOLVIDO — nunca a referência), `AccessInstructions` (string?) — nunca o agregado `PropertyAccessConfiguration`, nunca `AccessCredentialSecretReference` (a referência em si nunca cruza este boundary).
6. **`PropertyAccessConfiguration` armazena somente `AccessCredentialSecretReference`** (uma referência opaca escolhida pelo administrador, ex.: nome de uma chave de User Secrets/variável de ambiente) — nunca o valor cru. `IPropertyAccessCredentialProvider` (`PropertyManagement.Application`) resolve a referência para o valor real. **Deliberadamente uma abstração NOVA e independente — não uma reutilização de `ExternalIntegrations.Application.IWhatsAppCredentialProvider`** (CP6.1 Decision Gate item 8): aquela interface existe exclusivamente para autenticar a plataforma junto a um provider EXTERNO (Meta Graph API); uma credencial de acesso ao imóvel é dado de negócio tenant-owned, mais próxima em natureza de `Payments.Domain.PixCharge.QrCodePayload` (ADR-025) do que de um secret de provider. Nenhum `ISecretProvider`/framework de secret genérico foi criado (CP6.2 mandato item 6) — apenas o boundary estritamente necessário para esta capability.
7. **Backend de Development/Production, mesmo padrão de `IWhatsAppCredentialProvider` (ADR-012)**: `DevelopmentPropertyAccessCredentialProvider` (`PropertyManagement.Infrastructure`) resolve via `IConfiguration` (User Secrets/variáveis de ambiente), registrado somente quando `IsDevelopment()`. **Nenhum backend de Production existe neste checkpoint** — `ProductionAccessCredentialSecretBackendAvailable=false`, bloqueado pelo mesmo cloud provider ainda não decidido (ADR-011). Resolver este provider fora de Development falha alto (nenhum registro), nunca cai silenciosamente em um valor de Development.
8. **Operação somente leitura** — `GetForGuestAccessDeliveryAsync` nunca modifica estado de Property Management.
9. **Dois casos distintos, nunca colapsados um no outro** (CP6.2 mandato item 24): (a) nenhuma `PropertyAccessConfiguration` ativa existe (ausente ou `IsActive=false`) — resultado `null`, no-op ordinário de configuração, idêntico ao padrão de "nada a notificar" de ADR-026; (b) uma configuração ativa existe com `AccessCredentialSecretReference` preenchida, mas o provider não consegue resolver o valor — **isto é uma falha de infraestrutura/configuração, nunca engolida silenciosamente**: a implementação lança exceção, propagada até o handler do evento, retentada pelo Wolverine como qualquer outra falha real. `AccessCredential`/`AccessInstructions` são independentes entre si — a ausência de um nunca impede a resolução do outro.
10. **`Purpose-limited`, não uma exceção geral de leitura cross-context** — mesma cláusula estrita de ADR-026 (item 8): esta ADR autoriza exatamente um consumidor (Communication) e exatamente um propósito. Não autoriza nenhum outro Bounded Context a consultar `PropertyAccessConfiguration`, nem autoriza Communication a consultar qualquer outro dado de Property Management além deste único contrato e do já existente `IFrontDeskContactReader` (ADR-026).
11. **Não cria precedente geral para leitura cross-context de Property Management** — mesma cláusula de ADR-026 (item 9).
12. **Tenant-scoped, RLS, fail-closed** — mesmo mecanismo de `TenantAwareTransactionScope` já usado por `FrontDeskContactReader`/`PixChargeDeliveryReader` — nunca `IgnoreQueryFilters`, nunca um papel com `BYPASSRLS`.
13. **PII/segurança — a decisão central desta ADR**: a credencial resolvida NUNCA é persistida em `Communication.Message.RenderedContent` (confirmado por auditoria: essa coluna é `text` plano, sem redação/criptografia). O processador (`GuestAccessDeliveryProcessor`) renderiza o conteúdo real em memória, envia-o ao `IOutboundMessageConnector` (seu destino final legítimo — mesma razão de ADR-025 para o QR), e persiste, no lugar do conteúdo real, um marcador fixo de redação (`"[SENSITIVE CONTENT REDACTED]"`). As instruções de acesso, por não serem segredo, seguem o pipeline padrão de `Message` sem alteração — persistidas normalmente, mesmo tratamento de todo outro conteúdo não sensível desta plataforma.
14. **Dois business intents, um único evento**: `GuestAccessDeliveryRequested` (`GuestOperations.Contracts`) não carrega nem a credencial nem as instruções — apenas identificadores. `GuestAccessDeliveryProcessor` resolve ambas via este boundary, no momento exato do envio, e as trata como caminhos totalmente independentes (uma chave de idempotência própria por intent, via `TemplateKey` distinto — `GUEST_ACCESS_CREDENTIAL`/`GUEST_ACCESS_INSTRUCTIONS`) — a ausência de uma nunca bloqueia a outra.
15. **Restrição de referência verificada por arquitetura**: um `ArchitectureTest` dedicado prova que `IPropertyGuestAccessReader` é referenciado exclusivamente pelos assemblies de Communication — nenhum outro Bounded Context pode passar a usá-lo silenciosamente no futuro sem que o teste falhe.
16. **Eventual separação física dos contextos deve preservar o mesmo contrato** — mesma cláusula de ADR-014/ADR-019/ADR-026.

## Alternativas Consideradas

- **Reutilizar `IWhatsAppCredentialProvider`/a convenção `*SecretReference` de External Integrations tal como está**: rejeitada (CP6.1 Decision Gate item 8) — aquela convenção foi desenhada exclusivamente para credenciais de provider externo; uma credencial de acesso ao imóvel é dado de negócio tenant-owned de Property Management, não uma credencial para chamar nenhuma API externa. Reaproveitá-la misturaria duas categorias de segredo com riscos e ownership distintos.
- **Persistir a credencial renderizada em `Message.RenderedContent` como o QR PIX (ADR-025)**: rejeitada — o QR tem validade curta e perde valor após o pagamento; uma credencial de acesso (fixa por Property, MVP) pode permanecer válida por meses, tornando o histórico de mensagens um repositório cumulativo e persistente de segredos ainda ativos. Risco qualitativamente diferente, decisão explícita separada (CP6.1 Decision Gate item 16).
- **Criptografia de coluna para a credencial**: rejeitada — nenhum padrão desse tipo existe nesta base hoje; a credencial nunca é persistida em nenhuma coluna de negócio (apenas a referência), tornando a criptografia de coluna desnecessária para este boundary especificamente.
- **Um único evento/mensagem combinando credencial e instruções**: rejeitada (CP6.1 Decision Gate item 23) — mantidos como dois business intents independentes, permitindo que a ausência de configuração de um nunca bloqueie o outro, e isolando o tratamento de segurança da credencial sem afetar o pipeline ordinário das instruções.
- **Criar um framework de secret genérico (`ISecretStore`, adapter de KeyVault/Vault/KMS) agora**: rejeitada (CP6.2 mandato item 6) — generalização prematura; construído somente o boundary estritamente necessário para esta capability, mesmo padrão já estabelecido para `IWhatsAppCredentialProvider`/`IJwtSigningKeyProvider` (ADR-012).

## Consequências

### Positivas
- Fecha definitivamente o requisito interno de Fase 10 (Access Credential) identificado no Checkpoint 6, sem inventar Smart Lock, sem provider real, sem dinheiro/hardware envolvido.
- Resolve a lacuna crítica de segurança identificada no CP6.1 (persistência em texto puro) sem exigir nenhuma mudança ao agregado `Message` já existente — a solução é inteiramente uma decisão do processador (o que é passado para `Message.Create` vs. o que é passado para o connector).
- `GUEST_ACCESS_INSTRUCTIONS`/`GUEST_ACCESS_CREDENTIAL` reaproveitam a infraestrutura de `Configuration.Template` já existente sem nenhum código de infraestrutura novo — apenas chaves de template novas.

### Riscos Aceitos
- `Architecture Principles.md` §14 passa a ter doze exceções nomeadas.
- Backend de secret de Production continua bloqueado por ADR-011 (cloud provider não decidido) — herdado, não um blocker novo introduzido por esta ADR.
- Uma senha fixa por Property (decisão de MVP) significa que, se comprometida, permanece válida até reconfiguração manual — aceito conscientemente como decisão de MVP (CP6.1 Decision Gate item 1); Smart Lock/senha dinâmica permanecem deferred.
- `Cancelled → Confirmed` (PIX, ADR-025) e o modelo static/per-stay desta ADR são decisões independentes — nenhuma relação entre si.

## Referências
- `documentacao do projeto/Architecture Principles.md`, Seção 14 (décima segunda exceção)
- ADR-026 (Communication to Property Management Front Desk Contact Resolution) — precedente estrutural direto, mesma forma de contrato/implementação/teste
- ADR-025 (PIX Payment Boundary) — precedente e contraste direto para a decisão de persistência (item 13)
- ADR-012 (Assinatura e Gestão de Chaves JWT) — precedente original do padrão Development/Production credential provider
- ADR-024 (Guest Operations Boundary and Checkout Orchestration), §A7 — o ponto de parada que esta ADR resolve
- `Fase 10 - Check-in, Checkout e Operacoes do Hospede - Validacao e Homologacao.md`, Checkpoint 6, Checkpoint 6.1 (Decision Gate) e Checkpoint 6.2
