# ADR-012 — Assinatura e Gestão de Chaves JWT

Status: Aceito
Data: 2026-07-27

## Contexto

A ADR-005 já decidiu JWT (access token de curta duração + refresh token rotativo revogável) como estratégia de autenticação, mas não detalhou algoritmo de assinatura, armazenamento/rotação de chaves, nem o conteúdo exato das claims — detalhamento explicitamente solicitado durante o planejamento do Incremento 2 do contexto Identity & Access. Esta ADR registra essas decisões, aprovadas pelo usuário durante o planejamento, sem reabrir a decisão macro já aceita em ADR-005.

## Decisão

- **Algoritmo de assinatura:** RS256 (RSA assimétrico, 2048 bits). Preferido a HS256 porque permite distribuir apenas a chave pública a futuros validadores (API pública — Documento 13 §14; SSO/social login futuro — ADR-005) sem expor o segredo de assinatura, e tem suporte maduro de ferramentas/JWKS no ecossistema .NET.
- **Abstração de chave de assinatura:** `IJwtSigningKeyProvider`, definida em `Identity.Infrastructure`, implementada em desenvolvimento por uma variante baseada em configuração (chave lida de User Secrets/variáveis de ambiente, nunca do código-fonte, conforme Documento 11 §24). O armazenamento de chave em produção (KMS/Key Vault/Vault) permanece uma decisão em aberto, bloqueada pela escolha de provedor de nuvem ainda não definida (ADR-011) — a abstração garante que essa troca futura não exija alterar Domain/Application/Api.
- **`kid`:** identificador curto e determinístico por chave (thumbprint da chave pública), incluído no header do JWT, suportando rotação futura com uma janela de sobreposição em que chaves anterior e atual permanecem válidas para verificação.
- **Claims do access token:** `iss`, `aud`, `sub` (id do usuário), `tenant_id` (custom, só após autenticação válida — restrição já aprovada), `session_id` (custom, permite logout localizar a sessão sem reenviar o refresh token e permite a checagem de revogação acelerada via Redis), `jti` (correlação em auditoria), `role` (array de códigos de papel), `iat`, `nbf` (= `iat`), `exp`.
- **`role` incluído no token; `permissions` não incluído.** Papéis mudam raramente e são consumidos diretamente pelo `IAuthorizationHandler` de RBAC+ABAC já decidido em ADR-005. Permissões são resolvidas no servidor a partir do(s) papel(is), via o catálogo `RolePermission` (não tenant-owned, cacheável globalmente) — evita token volumoso e evita que uma alteração de permissão fique presa até o token expirar.
- **Access token nunca é persistido, colocado em blacklist ou armazenado em qualquer tabela.** É um artefato stateless; a única forma de invalidação antes do `exp` natural é a checagem opcional de revogação por `session_id` no Redis (aceleração, nunca fonte de verdade — ver decisão de Redis abaixo).
- **Redis permanece exclusivamente cache de aceleração de revogação; PostgreSQL permanece a única fonte de verdade** para sessões, refresh tokens e estado de revogação — reafirmando restrições já aprovadas, sem alteração.
- **Refresh token é transportado no corpo JSON** da resposta/requisição (não em cookie) — decisão de contrato público explícita.
- **Versionamento inicial da API:** prefixo de rota literal `/api/v1/...`, sem pacote de versionamento formal nesta fase (primeira API HTTP real da plataforma).
- Valores concretos de expiração de token, `ClockSkew`, TTL de chaves de cache e limiares de lockout **não são objeto desta ADR** — são parâmetros de configuração com defaults seguros, ajustáveis sem nova decisão arquitetural.

## Alternativas Consideradas

- **HS256 (simétrico):** descartado como escolha primária — exigiria distribuir o segredo de assinatura a qualquer futuro validador externo, incompatível com a trajetória de API pública/SSO já registrada em ADR-005.
- **ES256 (ECDSA P-256):** alternativa tecnicamente válida (tokens menores, operações mais rápidas); não escolhida como padrão inicial por menor maturidade de ferramentas/JWKS no ecossistema consultado no momento da decisão. Pode ser reavaliada sem impacto em Domain/Application, graças ao `IJwtSigningKeyProvider`.
- **Incluir `permissions` no token:** descartado — token maior, e uma alteração de permissão (mais frequente que uma alteração de papel) ficaria presa até o token expirar.
- **Refresh token via cookie HttpOnly:** avaliado; não escolhido nesta fase por simplicidade de contrato e por não haver, ainda, um cliente Web same-origin cuja superfície de CSRF justifique a complexidade adicional.

## Consequências

### Positivas
- Chave de assinatura desacoplada do código e do provedor de infraestrutura, via `IJwtSigningKeyProvider`.
- Modelo de claims minimalista, com cada claim rastreável a uma necessidade concreta do Incremento 2.
- Nenhuma dependência nova de armazenamento para o access token — reduz superfície de dados sensíveis.

### Riscos Aceitos
- Alteração de papel de um usuário só passa a valer quando o access token atual expirar (janela limitada pelo TTL curto do access token, mesma classe de risco já aceita em ADR-005 para revogação).
- Armazenamento de chave em produção permanece indefinido até a decisão de provedor de nuvem (ADR-011); mitigado pela abstração, não eliminado.

## Referências
- ADR-005 (Autenticação e Autorização), ADR-011 (Ambiente de Desenvolvimento e CI-CD)
- Documento 11 §24-25, Documento 13 §14
- `documentacao do projeto/Fase 1 - Identity and Access - Validacao e Homologacao.md`
