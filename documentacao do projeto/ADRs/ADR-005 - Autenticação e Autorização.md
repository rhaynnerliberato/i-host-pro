# ADR-005 — Autenticação e Autorização

Status: Aceito
Data: 2026-07-26

## Contexto

O Documento 09 exige RBAC combinado com ABAC (atributos como tenant, propriedade e designação). O Documento 11 (§25) exige sessões independentes do servidor (stateless). O Documento 15 (§9) antecipa integração futura com provedores externos de identidade.

## Decisão

- **Autenticação:** JWT (access token de curta duração + refresh token rotativo revogável), via middleware nativo `JwtBearer` do ASP.NET Core + **ASP.NET Core Identity** para gestão de usuários/credenciais.
- **Hashing de senha:** Argon2id, via `IPasswordHasher` customizado (substitui o PBKDF2 padrão do Identity).
- **Autorização (RBAC+ABAC):** Policy-based Authorization nativo do ASP.NET Core, com `IAuthorizationHandler` customizados combinando papel + atributos (mesmo tenant, faxineira designada, proprietário associado ao imóvel).

## Alternativas Consideradas

- **Sessão server-side (Redis-backed):** descartada — contraria a exigência explícita de sessões independentes do servidor (Documento 11 §25) e é menos natural para futura API pública/apps móveis (Documento 13 §14).
- **CASL (biblioteca JS de autorização):** não aplicável após a mudança para .NET — o próprio framework ASP.NET Core já oferece o padrão *requirements + handlers* necessário, mais idiomático que portar uma biblioteca de outro ecossistema.

## Consequências

### Positivas
- Compatível com escalabilidade horizontal desde o início.
- Caminho natural para SSO/social login futuro via extensão do pipeline de autenticação, sem alterar o domínio.

### Riscos Aceitos
- Revogação imediata de access token exige expiração curta + gestão de refresh token — implementação deve ser revisada com atenção em testes de segurança (Documento 20 §15).

## Referências
- Documento 09, Documento 11 §25, Documento 13 §14, Documento 15 §9-10
- Architecture Principles §14
