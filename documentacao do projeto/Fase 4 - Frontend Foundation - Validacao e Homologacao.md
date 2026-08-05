# Fase 4 — Frontend Foundation — Validação e Homologação

Versão: 0.3

Status: Incremento 1 **aprovado e versionado** na branch `feature/frontend-foundation` (quatro commits funcionais, ver Seção 10). Push ainda pendente neste momento. Fase 4 continua em andamento — não integrada em `master`. Incremento 2 (Administração de Usuários) ainda não iniciado.

---

## 1. Objetivo

Este documento registra a validação e homologação real do Incremento 1 da Fase 4 (Frontend Foundation), conforme `Plano Executivo de Desenvolvimento por Fases.md` e ADR-008 (Frontend e Design System).

Este documento não repete decisões arquiteturais já registradas em ADR-001, ADR-005, ADR-008, ADR-010 — apenas registra as decisões tomadas durante este incremento (quando não cobertas por ADR existente), a evidência de validação e o histórico de defeitos reais encontrados durante a homologação, conforme `ai-rules/06 - Definition of Done.md`.

## 2. Escopo do Incremento 1

Fundação Angular do frontend administrativo: scaffold do projeto, configuração de runtime externa, Angular Material, Transloco (i18n), cliente HTTP tipado gerado via NSwag, autenticação (login/refresh/logout) com guards de rota, layout responsivo com navegação principal, home autenticada exibindo o usuário corrente, e testes E2E (Playwright para .NET) cobrindo os fluxos de autenticação essenciais.

## 3. Fora de escopo (não alterado nesta fase)

CRUDs completos de usuários/imóveis/condomínios/reservas, dashboard operacional completo, Housekeeping, Agenda, WhatsApp, IA — conforme `Plano Executivo de Desenvolvimento por Fases.md`, Fases 5 em diante.

## 4. Decisões arquiteturais já cobertas pelo usuário (não decididas por este agente)

### 4.1 Tokens de autenticação

Access token somente em memória (signal); refresh token em `sessionStorage`; nunca `localStorage`; restauração de sessão ao recarregar a página usando o refresh token; substituição imediata do refresh token após rotação; refresh single-flight para requisições concorrentes; cada requisição pode ser repetida no máximo uma vez; sem tentativa de refresh para login/refresh/logout; 403 nunca dispara refresh; logout sempre limpa o estado local mesmo se o backend falhar.

### 4.2 Runtime configuration

Arquivo JSON externo (`public/config.json`), carregado e validado antes do bootstrap via `provideAppInitializer`; `apiBaseUrl` obrigatória; troca de ambiente sem recompilar; nenhum segredo/token/credencial/dado de tenant; API Angular oficial vigente, sem APIs obsoletas.

## 5. Decisões técnicas locais tomadas neste incremento (não arquiteturais, sem necessidade de ADR)

### 5.1 Angular 21.2.19 em vez da versão mais recente (22.1.2)

O ambiente possui Node.js v22.19.0. O Angular 22 exige Node `^22.22.3 || ^24.15.0 || >=26.0.0` — incompatível. O Angular 21 exige `^20.19.0 || ^22.12.0 || >=24.0.0`, satisfeito pela versão instalada. Angular 21 é uma versão estável oficial (não preview/RC), atendendo à exigência explícita do usuário ("Angular estável oficialmente suportado, nunca preview"). Aplicação standalone (sem `NgModule`) confirmada como padrão oficial do `ng new` nesta versão.

### 5.2 Vitest como test runner

Padrão atual do Angular CLI 21 para testes unitários (substituiu Karma/Jasmine) — usado sem alteração, por ser o próprio padrão oficial vigente da ferramenta, não uma escolha manual.

### 5.3 Locales Transloco corrigidos para `pt-BR`/`en`

O scaffold padrão do schematic Transloco gera `en`/`es` como placeholders. Corrigido para `pt-BR` (idioma principal do produto, conforme toda a documentação existente) e `en` (secundário).

### 5.4 `CustomSchemaIds` no Swashbuckle (`Program.cs`)

**Defeito real encontrado**: `PropertyManagement.Application.Optional<T>` e `Reservations.Application.Optional<T>` são tipos genéricos independentes, cada um definido em seu próprio Bounded Context. O algoritmo padrão de nomeação de schema do Swashbuckle (nome do tipo + nomes dos argumentos genéricos, sem namespace) gera o mesmo `schemaId` (`StringOptional`) para ambos, causando `SwaggerGeneratorException` e impedindo completamente a geração do documento OpenAPI assim que os três Bounded Contexts (Identity, PropertyManagement, Reservations) coexistem no mesmo host — condição nunca antes exercida em uma geração real de swagger.json, já que este é o primeiro incremento a consumir o contrato OpenAPI combinado dos três.

**Correção**: `SwaggerSchemaIdSelector` (método estático em `Program.cs`) reproduz o algoritmo padrão do Swashbuckle, mas prefixa o `schemaId` de tipos genéricos com o segmento de namespace do Bounded Context (`IHostPro.Contexts.<Contexto>.*`) quando aplicável. Tipos não genéricos (a grande maioria dos DTOs) permanecem com o nome padrão, inalterado. Nenhuma mudança de comportamento HTTP — apenas nomenclatura interna do documento OpenAPI/cliente gerado.

### 5.5 `[ProducesResponseType]` restrito a `AuthController` e `UsersController.GetOwnProfile`

**Defeito real encontrado**: nenhum controller de toda a API (9 controllers, Identity/PropertyManagement/Reservations) declarava `[ProducesResponseType]` ou usava `ActionResult<T>` — todas as ações retornam `IActionResult` puro. Consequência: o Swashbuckle não conseguia documentar nenhum corpo de resposta (apenas os `*Request` de entrada, inferidos automaticamente do parâmetro `[FromBody]`). O cliente NSwag gerado a partir desse contrato incompleto tipava toda resposta como `Observable<void>`, incluindo `login()`, que deveria retornar `AuthTokensResponse`.

**Decisão do usuário**: corrigir apenas o necessário para os Checkpoints 3 e 4 deste incremento — `AuthController` (`login`/`refresh`/`logout`) e `UsersController.GetOwnProfile` (`GET /api/v1/users/me`). PropertyManagement e Reservations (7 controllers) permanecem com a lacuna, propositalmente não tocados por pertencerem a fases já fechadas e não serem consumidos por nenhuma tela deste incremento — ficam para quando a Fase 5+ efetivamente precisar do contrato tipado desses endpoints.

**Correção**: `[ProducesResponseType(typeof(AuthTokensResponse), StatusCodes.Status200OK)]` + `[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]` em `Login`/`Refresh`; `[ProducesResponseType(StatusCodes.Status204NoContent)]` + `[ProducesResponseType(StatusCodes.Status401Unauthorized)]` em `Logout`; `[ProducesResponseType(typeof(OwnProfileResponse), StatusCodes.Status200OK)]` + `[ProducesResponseType(StatusCodes.Status401Unauthorized)]` em `GetOwnProfile`. Nenhuma mudança de comportamento HTTP real — apenas metadados de documentação OpenAPI, cuja ausência já era uma lacuna pré-existente das Fases 1-2.

### 5.6 `injectionTokenType: InjectionToken` (não `OpaqueToken`) na configuração do NSwag

Configuração inicial do `nswag.json` usou por engano `"injectionTokenType": "OpaqueToken"` — API removida do Angular há várias versões majors, geraria `import { OpaqueToken } from '@angular/core'`, inexistente no Angular 21 (erro de compilação). Corrigido para `InjectionToken`, a API oficial vigente, antes de qualquer geração ser consumida pelo restante da aplicação.

### 5.7 Permission guard baseado em `GET /users/me`, nunca em JWT decodificado

`permissionGuard` (`core/auth/permission.guard.ts`) lê os papéis do usuário exclusivamente de `UserProfileService.roles()`, populado por uma chamada real a `GET /api/v1/users/me` após login/restauração de sessão — o token de acesso nunca é decodificado no cliente para fins de autorização. Rotas sem `data: { roles: [...] }` comportam-se como o `authGuard` simples (exige apenas autenticação).

### 5.8 Dois defeitos reais de bootstrap encontrados durante a homologação em navegador do Checkpoint 3

Ambos só se manifestaram ao carregar a aplicação real em um navegador (build de produção servido estaticamente) — nenhum teste unitário isolado os detectava, pois dependem da ordem real de inicialização do Angular. A aplicação carregava como página em branco, sem nenhum erro visível na tela.

**Defeito 1 — injeção antecipada e circular do `Client` dentro do interceptor HTTP.** `authInterceptor` injetava `AuthService` incondicionalmente no topo da função, para toda requisição. Como `RuntimeConfigService.load()` busca `public/config.json` via `HttpClient`, essa própria requisição também passava pelo interceptor — construindo `AuthService` (e, por trás dele, o `Client` gerado pelo NSwag) **antes** de `RuntimeConfigService.config` estar disponível, disparando `RuntimeConfigService.config accessed before load() completed`. Corrigido injetando `AuthService` apenas de forma preguiçosa, dentro do branch de tratamento de 401 (via `Injector` capturado no topo + `runInInjectionContext`) — nunca no caminho comum de toda requisição.

**Defeito 2 — `inject()` após `await` no `provideAppInitializer`.** O inicializador combinava `await runtimeConfig.load()` seguido de `inject(AuthService)` na linha seguinte — `inject()` só é válido de forma síncrona; após um `await`, a execução retoma fora do contexto de injeção original, produzindo `NG0203`. Corrigido capturando `Injector` de forma síncrona no topo do inicializador e usando `runInInjectionContext(injector, () => inject(AuthService))` **depois** do `await`, mantendo ao mesmo tempo a garantia de que `AuthService`/`Client` só são construídos após `RuntimeConfigService.load()` ter terminado (mesma razão do Defeito 1).

Ambos confirmados corrigidos por verificação real em navegador (build de produção, `ng build --configuration production`, servido estaticamente): console sem erros, URL redirecionada corretamente para `/login?redirectTo=%2F`, formulário de login renderizado com Angular Material e rótulos Transloco em pt-BR.

### 5.9 Resolução ambígua do tipo base `DbContext` no Host combinado (defeito transversal, descoberto durante a homologação do Checkpoint 4)

**Defeito real encontrado**: durante a verificação manual do login real em navegador (Checkpoint 4), `GET /api/v1/users/me` retornava incorretamente 404. Causa raiz: Identity, PropertyManagement e Reservations registravam, cada um, `services.AddScoped<DbContext>(sp => sp.GetRequiredService<XxxDbContext>())` para permitir que sua própria pipeline tenant-aware resolvesse o tipo base `DbContext`. No Host combinado (`IHostPro.Api`), essas três registrações competem pelo mesmo tipo não chaveado — a última registrada sempre vencia para qualquer consumidor do `DbContext` base, independentemente de qual Bounded Context estava realmente em execução. Concretamente, `GetOwnProfileQuery` (Identity) abria sua transação com Row-Level Security (`SET LOCAL app.tenant_id`) contra `ReservationsDbContext`, enquanto a consulta real rodava em `IdentityDbContext` numa conexão diferente — o RLS do PostgreSQL retornava silenciosamente zero linhas (fail-closed), surfaceando como 404 incorreto. Nenhum teste isolado por Bounded Context jamais exercitou essa colisão, pois cada um registra apenas seu próprio módulo — somente a composição real dos três no mesmo Host reproduz o defeito.

**Correção autorizada pelo usuário**: refatoração transversal eliminando toda resolução do tipo base `DbContext`. `TenantAwareUnitOfWork`, `TenantTransactionBehavior` e `TenantBootstrapBehavior` tornados genéricos sobre o `TDbContext` concreto (ex.: `TenantAwareUnitOfWork<TDbContext>`), registrados como generic aberto (`services.AddScoped(typeof(TenantAwareUnitOfWork<>))`, nunca fechado/ambíguo). Todas as registrações `AddScoped<DbContext>(...)` removidas dos três módulos. Cerca de 35 arquivos ajustados nos três Bounded Contexts (readers/writers/executors que injetavam `DbContext` bruto passaram a injetar o tipo concreto do seu próprio contexto). Teste de arquitetura preventivo criado (`TenantAwareDbContextResolutionTests.cs`, 3 testes) para impedir recorrência: nenhum tipo de Infrastructure pode injetar `DbContext` bruto; o Host combinado não registra `DbContext` bruto; cada pipeline tenant-aware está ligada ao `DbContext` concreto do seu próprio contexto. Não é uma alteração retroativa de decisão arquitetural já aprovada — é a correção de um defeito de composição nunca antes exercido; nenhum novo ADR foi necessário.

**Validação**: `dotnet build` (solução completa) — 0 erros. `IHostPro.ArchitectureTests` — 120/120 aprovados (117 pré-existentes + 3 novos). `IHostPro.Contexts.Identity.Tests.Unit` — 468/468 aprovados. `IHostPro.Contexts.Identity.Tests.Integration` — 411/411 aprovados (após ajuste de uma regressão pontual em `LoginCommandHandlerTests`, decorrente da mudança de assinatura de `ITenantAwareUnitOfWork` para `TenantAwareUnitOfWork<IdentityDbContext>`). `IHostPro.Contexts.PropertyManagement.Tests.Integration` — 184/184 aprovados. `IHostPro.Contexts.Reservations.Tests.Integration` — 52/52 aprovados. `GET /api/v1/users/me` no host combinado real — HTTP 200, perfil correto retornado.

### 5.10 Corrida em `WolverineThreeStoreCompositionTests` (defeito do próprio teste, não de produção)

Durante a revalidação da suíte após o defeito 5.9, `MigrationRunner_provisions_rabbitmq_topology_idempotently_and_the_real_host_delivers_through_it` (cenário de outage/recovery do RabbitMQ) passou a falhar de forma esporádica. Investigado, por instrução explícita do usuário, como possível corrida no próprio teste — não como regressão presumida de produção.

**Causa raiz confirmada**: a fila-sonda de diagnóstico do pós-outage era declarada (durável, vinculada às exchanges `identity-events`/`property-management-events`) somente depois da recuperação do broker — correndo contra a própria redelivery do outbox do Wolverine, que não é controlada por nenhum código deste teste e pode publicar assim que detecta o broker acessível novamente, potencialmente antes do round-trip AMQP de declaração/binding da fila terminar (com `mandatory=false`, uma publicação não roteável é descartada silenciosamente, sem erro).

**Primeira tentativa de correção (não definitiva)**: declarar a fila durável antes do início do outage, mas também antes da criação do usuário-alvo do teste — a fila passou a capturar o evento `UserRoleAssigned` inicial (papel OPERATOR, legítimo, publicado normalmente enquanto o broker ainda está de pé), além do evento real do outage (papel HOUSEKEEPER), quebrando a asserção de exatamente um evento.

**Correção definitiva aplicada**: as duas chamadas de declaração da fila durável foram reposicionadas para o intervalo entre a confirmação de que os eventos de setup do usuário-alvo foram entregues (outbox vazio) e a chamada que efetivamente interrompe o RabbitMQ — eliminando ambas as corridas (nada publicado antes desse ponto pode ainda chegar à fila; nada publicado a partir daí pode vencer o binding já existente). Adicionalmente, a asserção foi reforçada com verificação de payload além de contagem/tipo: `UserRoleAssigned` pertence ao usuário-alvo do outage com papel `HOUSEKEEPER`; `CondominiumCreated` corresponde ao condomínio criado durante o outage — alteração restrita a `DrainQueueAsync`/`TestQueueMessage` (infraestrutura test-only deste arquivo de teste, que passou a capturar também o corpo da mensagem), sem qualquer alteração de contrato ou código de produção.

**Validação**: 3/3 execuções isoladas do teste anteriormente falho aprovadas consecutivamente (23s, 28s, 28s) + 1 execução completa da classe `WolverineThreeStoreCompositionTests` — 4/4 aprovados, sem falhas, sem dependência de ordem, sem perda ou duplicação de eventos (1m46s). Ambiente Docker (`ihostpro-homolog-rabbitmq`, parado durante a validação para liberar a porta 5672 para o container efêmero do próprio teste) restaurado ao estado anterior ao final.

### 5.11 Classificação incorreta de erro de login (defeito real, encontrado pelo próprio teste Playwright novo)

**Defeito real encontrado**: o cliente `Client` gerado pelo NSwag (`api-client.ts`) nunca repassa um `HttpErrorResponse` do Angular para o chamador em uma resposta não-2xx — ele próprio lê o corpo da resposta e lança o objeto `ProblemDetails` desserializado (quando o backend retorna um, como `Login` retorna) ou sua própria `ApiException`; nunca `HttpErrorResponse`. `login.ts` classificava o erro com `error instanceof HttpErrorResponse && error.status === 401`, condição que nunca era verdadeira nesse ponto — todo login inválido exibia a mensagem genérica (`auth.login.genericError`, "Não foi possível entrar. Tente novamente.") em vez da mensagem específica de credenciais inválidas (`auth.login.invalidCredentials`), independentemente da causa real. Os testes unitários existentes (`auth.interceptor.spec.ts`) não detectavam isso porque testam o interceptor isoladamente — nesse ponto do pipeline HTTP, ANTES da própria transformação do NSwag, o erro realmente é um `HttpErrorResponse`. Só o teste Playwright novo, exercitando a cadeia real completa (`AuthService.login()` → `Client` gerado → componente), expôs a divergência: a resposta HTTP real era 401 (confirmado via `page.RunAndWaitForResponseAsync`), mas o texto exibido era o genérico.

**Correção aplicada**: `isInvalidCredentialsError(error: unknown): boolean` (`features/auth/login/login-error.ts`, novo arquivo, mesmo padrão de helper isolado e testável já usado por `isSafeRedirectPath`/`redirect-url.ts`) — verifica por duck-typing um campo numérico `status === 401`, sem depender de `HttpErrorResponse`, sem depender do tipo concreto `ApiException`, funcionando tanto para o `ProblemDetails` desserializado quanto para `ApiException`. `login.ts` passou a usar esse helper; import de `HttpErrorResponse` removido por ter ficado sem uso. Nenhuma alteração no cliente NSwag gerado, no backend ou em contratos HTTP — correção isolada ao componente de login.

**Testes adicionados**: `login-error.spec.ts` (5 casos) — objeto `ProblemDetails`-like com `status` 401 (credenciais inválidas); objeto `ApiException`-like (subclasse de `Error` com campo `status`) com `status` 401 (credenciais inválidas); `status` 500 (mensagem genérica); objeto sem campo `status` (mensagem genérica); `null`/`undefined`/string (mensagem genérica, sem lançar exceção).

**Validação**: `ng test --watch=false` — 28/28 aprovados (23 pré-existentes + 5 novos). Teste Playwright de login inválido, isolado — aprovado: resposta HTTP real 401 confirmada, mensagem exibida "E-mail, senha ou empresa inválidos." confirmada, formulário permanece em `/login`. Suíte Playwright completa (Seção 6.4) — 6/6 aprovados.

## 6. Checkpoints executados

### 6.1 Checkpoint 1 — Scaffold, runtime configuration, Angular Material, Transloco, CORS

- Projeto Angular 21.2.19 standalone criado em `frontend/IHostPro.Web` (`ng new --routing --style=scss --ssr=false`).
- Angular Material 21.2.14 instalado via `ng add`, tema Material 3 (`mat.theme(...)`) em `styles.scss`, densidade 0, sem `@angular/animations` (não exigido pela versão baseada em variáveis CSS).
- Transloco 8.4.0 (`@jsverse/transloco`) instalado via `ng add`, idiomas `pt-BR`/`en`, `TranslocoHttpLoader` via `HttpClient`.
- `RuntimeConfigService` (`core/config/runtime-config.service.ts`) carrega e valida `public/config.json` (`apiBaseUrl` obrigatória, string não vazia) via `provideAppInitializer`, antes do bootstrap do restante da aplicação.
- CORS no backend (`Program.cs`): política nomeada `Frontend`, origens lidas de `Cors:AllowedOrigins` (padrão `http://localhost:4200`, sem wildcard), `AllowAnyHeader().AllowAnyMethod()`, sem `AllowCredentials()` (o frontend anexa o Bearer token manualmente por requisição, nunca via cookie).
- Validação: `dotnet build` (Host) — 0 erros, 0 avisos. `ng build` (frontend) — bundle gerado com sucesso (244,60 kB / 65,59 kB estimado de transferência).

### 6.2 Checkpoint 2 — NSwag e infraestrutura HTTP

- Pacote `nswag@14.7.1` instalado como devDependency; `nswag.json` configurado com template `Angular`, `MultipleClientsFromOperationId` (o backend não produz `operationId` prefixado por tag, então o NSwag consolidou em uma única classe `Client` — comportamento padrão da ferramenta, não uma escolha manual em contrário), saída em `src/app/core/api/generated/api-client.ts`.
- Script `npm run generate:api` adicionado (`nswag run nswag.json`), apontando para `http://localhost:5140/swagger/v1/swagger.json` (documento OpenAPI real, obtido a partir do host real em execução — nunca escrito à mão).
- Dois defeitos reais de backend encontrados e corrigidos durante a primeira geração real (Seções 5.4 e 5.5) — sem eles, a geração do swagger.json falhava (schemaId colidindo) ou produzia um cliente sem tipagem de resposta.
- Cliente gerado (2279 linhas) validado: `login()`/`refresh()` retornam `Observable<AuthTokensResponse>`; `logout()` retorna `Observable<void>` (204, correto); `me()` retorna `Observable<OwnProfileResponse>`; demais métodos permanecem `Observable<void>` (lacuna aceita e documentada, Seção 5.5).
- `API_BASE_URL` (token do cliente gerado) conectado a `RuntimeConfigService.config.apiBaseUrl` via `useFactory` em `app.config.ts` — resolvido de forma preguiçosa (lazy), seguro porque o `provideAppInitializer` já garante que `RuntimeConfigService.load()` terminou antes de qualquer injeção do `Client`.
- A lógica real do interceptor HTTP (anexação do access token, refresh single-flight, retry único) pertence ao Checkpoint 3 (autenticação) — não antecipada aqui para evitar implementação parcial/especulativa.
- Validação: `dotnet build` (Host, após 5.4/5.5) — 0 erros, 0 avisos. `ng build` — sucesso, cliente gerado incluído no bundle (277,67 kB / 67,58 kB estimado). `ng test --watch=false` — 2/2 testes aprovados (spec padrão do scaffold). `IHostPro.ArchitectureTests` — 117/117 aprovados. Testes de integração de Identity — ver Seção 7.

### 6.3 Checkpoint 3 — Autenticação, refresh, guards

- `AuthStateService` (`core/auth/auth-state.service.ts`): access token em signal (memória apenas, nunca persistido), refresh token em `sessionStorage` sob a chave `ihostpro.refreshToken` (nunca `localStorage`), `setTokens` sempre sobrescreve o refresh token com o recém-rotacionado.
- `AuthService` (`core/auth/auth.service.ts`): `login`/`refresh`/`logout` sobre o `Client` gerado; `refreshAccessToken` é single-flight (`shareReplay` + `finalize`, um único request compartilhado entre chamadores concorrentes); `restoreSession` tenta restaurar a sessão a partir do refresh token do `sessionStorage` ao recarregar a página; `logout` sempre limpa o estado local mesmo se a chamada ao backend falhar (best-effort remoto, limpeza local incondicional).
- `UserProfileService` (`core/auth/user-profile.service.ts`): única fonte de papéis/autorização, populada por uma chamada real a `GET /api/v1/users/me` (nunca por decodificação do JWT) — carregada após login e após restauração de sessão bem-sucedidas, limpa no logout.
- `authInterceptor` (`core/auth/auth.interceptor.ts`): anexa `Authorization: Bearer` a toda requisição quando há access token; em um 401 (exceto para login/refresh/logout e exceto para uma requisição já reexecutada uma vez), dispara refresh single-flight e reexecuta a requisição original exatamente uma vez com o novo token; nunca dispara refresh para 403; em falha do refresh, limpa a sessão local e propaga o erro do refresh.
- `authGuard` e `permissionGuard` (`core/auth/auth.guard.ts`, `permission.guard.ts`): o primeiro exige apenas autenticação; o segundo adicionalmente exige um papel de `route.data['roles']` presente em `UserProfileService.roles()` — ambos redirecionam para `/login` preservando a rota original em `?redirectTo=`, o segundo redireciona para `/forbidden` quando autenticado mas sem o papel exigido.
- `isSafeRedirectPath` (`core/auth/redirect-url.ts`): valida o parâmetro `redirectTo` (controlável pelo usuário via URL) antes de navegar — rejeita URLs absolutas e truques de redirecionamento aberto (`//evil`, `/\evil`), prevenindo open redirect.
- Página de login (`features/auth/login/`): Reactive Forms com validação (empresa/e-mail/senha obrigatórios, e-mail com formato válido), bloqueio de submits simultâneos (`submitting` signal), feedback de credenciais inválidas (`auth.login.invalidCredentials`, 401) vs. erro genérico (`auth.login.genericError`), navegação segura de volta à rota originalmente solicitada após login.
- Página de acesso negado (`features/forbidden/`) e home mínima autenticada (`features/home/`, exibe `OwnProfileResponse.fullName` — layout completo fica para o Checkpoint 4).
- Dois defeitos reais de bootstrap encontrados e corrigidos (Seção 5.8).
- Validação: `dotnet build` — sem alterações de backend neste checkpoint. `ng build` — sucesso. `ng build --configuration production` — sucesso, verificado em navegador real (Seção 5.8). `ng test --watch=false` — 23/23 aprovados (6 arquivos: `auth-state.service`, `auth.interceptor` incluindo 401/403/single-flight/retry único, `auth.guard`, `permission.guard`, `redirect-url`, `app`).

### 6.4 Checkpoint 4 — Layout administrativo, navegação, testes Playwright para .NET

- `AdminLayout` (`layout/admin-layout/`): `mat-sidenav-container` com toolbar (título, nome do usuário autenticado via `UserProfileService.profile()`, botão "Sair") e navegação lateral (Início/Usuários/Condomínios/Imóveis/Reservas, ícones Material, `routerLinkActive`). Responsivo via `BreakpointObserver`/`Breakpoints.Handset` do `@angular/cdk/layout`: abaixo do breakpoint, sidenav em modo `over` (sobreposto, fechado por padrão, com botão hambúrguer, fecha automaticamente ao navegar); acima, modo `side` (permanentemente visível, sem hambúrguer). Tema claro/escuro automático conforme preferência do sistema/navegador, herdado da configuração `mat.theme()` + `color-scheme: light dark` já estabelecida no Checkpoint 1 (Material 3 resolve os tokens `--mat-sys-*` por `color-scheme`; nenhum alternador manual foi especificado como requisito).
- Rotas placeholder (`features/placeholder/`): `Usuários`/`Condomínios`/`Imóveis`/`Reservas` — componente único, título vindo de `route.data['titleKey']`, sem qualquer CRUD (Fase 5+, fora de escopo).
- `logout()` do `AdminLayout` chama `AuthService.logout()` e navega para `/login` no `subscribe()` — reaproveita a garantia já existente (Seção 6.3) de que a sessão local é sempre limpa mesmo se a chamada ao backend falhar.
- Validado em navegador real (não apenas testes automatizados): login real contra o backend real, navegação por todas as 5 rotas (Início + 4 placeholders), comportamento responsivo confirmado em duas larguras de viewport distintas (mobile: hambúrguer + sidenav sobreposto que fecha ao navegar; desktop: sidenav permanente, sem hambúrguer), logout, guard bloqueando rota protegida após logout — console sem erros em nenhuma etapa.
- **Testes E2E Playwright para .NET** (`tests/Frontend/IHostPro.Web.Tests.E2E/`, novo projeto): suíte real, sem mocks — `WebE2EFixture` sobe PostgreSQL/RabbitMQ/Redis efêmeros via Testcontainers (RabbitMQ em porta fixa 5672, mesma restrição já documentada em `WolverineThreeStoreCompositionTests`, já que a conexão RabbitMQ do Wolverine não tem override de porta), aplica as mesmas migrações/provisionamento do `IHostPro.MigrationRunner`, semeia um tenant/usuário ADMIN real via EF direto (mesmo padrão de `WolverineThreeStoreCompositionTests`), sobe `IHostPro.Api` como subprocesso real (porta 5140, a mesma que `public/config.json` já assume) e `ng serve` como subprocesso real (porta 4200, a mesma do `Cors:AllowedOrigins` padrão) — nunca `WebApplicationFactory`/TestServer em memória, que um navegador Playwright real não conseguiria alcançar pela rede. Chromium real via `Microsoft.Playwright`, uma `IBrowserContext` isolada por teste. Seis cenários: login válido, login inválido (Seção 5.11), rota protegida sem autenticação, logout, restauração de sessão via refresh token após reload, refresh inválido redirecionando para login.
- Validação: `ng test --watch=false` — 28/28 aprovados. Playwright — 6/6 aprovados. `ihostpro-homolog-rabbitmq` restaurado ao estado anterior após cada execução que exigiu pará-lo.

## 7. Testes

_Preenchido ao final de cada checkpoint conforme executado — ver Seção 8 para o resultado consolidado mais recente._

## 8. Homologação (consolidado até o momento)

| Etapa | Resultado |
|---|---|
| `dotnet build` (Host, pós Checkpoint 1) | 0 erros, 0 avisos |
| `dotnet build` (Host, pós correção `CustomSchemaIds`) | 0 erros, 0 avisos |
| `dotnet build` (Host, pós `[ProducesResponseType]` escopo mínimo) | 0 erros, 0 avisos |
| Geração real do swagger.json (host real, stack homolog Docker pré-existente: PostgreSQL/RabbitMQ/Redis) | HTTP 200, 30 rotas, schemas sem colisão |
| `npm run generate:api` (cliente NSwag) | Sucesso, `api-client.ts` gerado (2279 linhas) |
| `ng build` (frontend, Checkpoint 1) | Sucesso — 244,60 kB / 65,59 kB |
| `ng build` (frontend, Checkpoint 2, cliente gerado + `API_BASE_URL`) | Sucesso — 277,67 kB / 67,58 kB |
| `ng test --watch=false` (Checkpoint 2) | 2/2 aprovados |
| `IHostPro.ArchitectureTests` | 117/117 aprovados |
| `IHostPro.Contexts.Identity.Tests.Integration` (pós `[ProducesResponseType]` escopo mínimo) | 411/411 aprovados, 0 falhas — nenhuma regressão |
| `ng build` (frontend, Checkpoint 3, auth completa) | Sucesso |
| `ng build --configuration production` (Checkpoint 3) | Sucesso |
| Verificação real em navegador (build de produção servido estaticamente) | Console sem erros; `/` redireciona para `/login?redirectTo=%2F`; formulário renderizado (Material + Transloco pt-BR); validação client-side confirmada (3 mensagens de campo obrigatório ao submeter vazio) |
| `ng test --watch=false` (Checkpoint 3) | 23/23 aprovados (6 arquivos) |
| Refatoração transversal de resolução de `DbContext` (Seção 5.9) — `dotnet build` (solução completa) | 0 erros |
| `IHostPro.ArchitectureTests` (pós refatoração `DbContext`, incl. `TenantAwareDbContextResolutionTests`) | 120/120 aprovados |
| `IHostPro.Contexts.Identity.Tests.Unit` (pós refatoração `DbContext`) | 468/468 aprovados |
| `IHostPro.Contexts.Identity.Tests.Integration` (pós refatoração `DbContext` e regressão de `LoginCommandHandlerTests`) | 411/411 aprovados |
| `IHostPro.Contexts.PropertyManagement.Tests.Integration` (pós refatoração `DbContext`) | 184/184 aprovados |
| `IHostPro.Contexts.Reservations.Tests.Integration` (pós refatoração `DbContext`) | 52/52 aprovados |
| `GET /api/v1/users/me` (host combinado real, pós refatoração `DbContext`) | HTTP 200, perfil correto |
| `WolverineThreeStoreCompositionTests` — teste de outage/recovery, 3 execuções isoladas após correção da corrida (Seção 5.10) | 3/3 aprovados (23s, 28s, 28s) |
| `WolverineThreeStoreCompositionTests` — classe completa, execução única | 4/4 aprovados, 1m46s |
| Validação manual em navegador do Checkpoint 4 (`AdminLayout`, navegação, responsivo, logout, guard) | Sem erros de console; todas as rotas e comportamento responsivo confirmados |
| `IHostPro.Web.Tests.E2E` (Playwright, 6 cenários) — 1ª/2ª/3ª rodadas de iteração | 4/6, 4/6, 5/6 (defeitos de teste e o defeito real da Seção 5.11 corrigidos entre rodadas) |
| `IHostPro.Web.Tests.E2E` (Playwright) — teste de login inválido isolado, pós-correção da Seção 5.11 | 1/1 aprovado |
| `IHostPro.Web.Tests.E2E` (Playwright) — suíte completa, execução única final | 6/6 aprovados |
| `npm ci` | Sucesso (487 pacotes; 5 vulnerabilidades identificadas pelo `npm audit` nesta auditoria — classificação completa na Seção 10.3) |
| `npm run generate:api` — 1ª geração pós-`npm ci` | Sem diferença em relação ao `api-client.ts` já commitado (contrato do backend inalterado) |
| `npm run generate:api` — 2ª geração (determinismo) | Byte-a-byte idêntica à 1ª geração |
| `ng build --configuration production` (rodada final) | Sucesso — 333,95 kB / 85,52 kB estimado |
| Lint | Não configurado no projeto (sem script `lint`, sem config ESLint) — etapa não aplicável |
| `ng test --watch=false` (rodada final, pós-`npm ci`) | 28/28 aprovados |
| Teste focado de CORS (`curl` OPTIONS real contra a API) | Origem permitida (`http://localhost:4200`) recebe `Access-Control-Allow-Origin`; origem não permitida (`http://evil.example`) não recebe o cabeçalho |
| `git diff --check` (rodada final) | Sem erros (apenas avisos de normalização LF/CRLF, já presentes antes desta tarefa) |

## 9. Pendências para os próximos checkpoints

Nenhuma pendência do Checkpoint 4 permanece — layout administrativo responsivo, identificação do usuário, logout, rotas placeholder, tema claro/escuro (automático, herdado do Checkpoint 1) e testes E2E Playwright para .NET (6/6) estão concluídos e validados. Incremento 1 aprovado e versionado — ver Seção 10.

Fora de escopo deste incremento, para Fases 5+: CRUDs completos de usuários/imóveis/condomínios/reservas, dashboard operacional completo, Housekeeping, Agenda, WhatsApp, IA (Seção 3).

## 10. Encerramento do Incremento 1

### 10.1 Aprovação

Incremento 1 aprovado tecnicamente pelo usuário após a rodada final de validação (Seção 8) e autorizado para versionamento e publicação na branch `feature/frontend-foundation`.

### 10.2 Commits

Quatro commits funcionais, cada um isolado ao seu próprio grupo de arquivos (revisado via `git diff --cached` antes de cada commit — nunca `git add .`):

| # | Hash completo | Mensagem | Escopo |
|---|---|---|---|
| 1 | `86d60f9e294cd4c2836070326b87b469cccfc206` | `fix(infrastructure): disambiguate tenant DbContext resolution` | Refatoração transversal de `DbContext` (Seção 5.9): BuildingBlocks + Identity/PropertyManagement/Reservations Infrastructure, correção de `LoginCommandHandlerTests`/`IdentityCommandDispatchExtensionsTests`, `TenantAwareDbContextResolutionTests.cs` (novo). 37 arquivos. |
| 2 | `321cc0e6f6555935b28185a083ce721d45428eec` | `test(host): stabilize outage recovery verification` | Correção determinística das filas durable de `WolverineThreeStoreCompositionTests` (Seção 5.10), incluindo a verificação de payload adicionada junto à correção de timing. 1 arquivo. |
| 3 | `debb0df01976233da895d5f4a1880a97cdc82acb` | `feat(frontend): add foundation and authentication` | Projeto Angular completo (`frontend/IHostPro.Web/`), `CustomSchemaIds`/CORS/`[ProducesResponseType]` no backend (Seções 5.4/5.5/6.1). 66 arquivos. |
| 4 | `4f1d50a05393e4f8150320609f8f8a94d9f83904` | `test(frontend): add authentication end-to-end coverage` | Projeto Playwright C# (`tests/Frontend/IHostPro.Web.Tests.E2E/`), registro no `IHostPro.sln`. 4 arquivos. |

Nenhum `node_modules/`, `.angular/`, `dist/`, `bin/`, `obj/`, trace/screenshot/vídeo do Playwright, log, resultado de teste, `.env`, credencial, token ou senha foi versionado — confirmado por inspeção de cada `git diff --cached --stat` antes de cada commit. `.claude/launch.json` confirmado ausente do disco e nunca rastreado.

### 10.3 Classificação das vulnerabilidades npm

`npm audit --omit=dev`: **0 vulnerabilidades** — nenhuma dependência de produção afetada.

`npm audit` (todas as dependências): 5 vulnerabilidades, todas em devDependencies (ferramental de build, nunca alcançam o bundle servido ao navegador):

| Pacote | Severidade | Direto/Transitivo | Prod/Dev | Correção disponível | Breaking? |
|---|---|---|---|---|---|
| `@angular/build` | moderate | direto | dev | sim | sim — downgrade para 20.3.33 (major anterior) |
| `@angular/cli` | moderate | direto | dev | sim | sim — 21.0.4 (major) |
| `@hono/node-server` | moderate (path traversal via backslash codificado no Windows, `serve-static`) | transitivo (via `@angular/cli` → `@modelcontextprotocol/sdk`) | dev | sim (via `@angular/cli` 21.0.4) | sim |
| `@modelcontextprotocol/sdk` | moderate | transitivo (via `@angular/cli`) | dev | sim (via `@angular/cli` 21.0.4) | sim |
| `undici` | **high** (CVSS 7.4 — divulgação de informação entre usuários e falha de parsing via diretivas de cache degeneradas) + 3 CVEs moderate adicionais no mesmo pacote | transitivo (via `@angular/build`) | dev | sim (via `@angular/build` 20.3.33 — downgrade de major) | sim |

Critério aplicado: existe uma vulnerabilidade `high` (`undici`), mas exclusivamente em dependência de desenvolvimento — nenhuma dependência de produção é afetada (confirmado por `npm audit --omit=dev` retornando zero). Por não haver `high`/`critical` em dependência de produção, o commit não foi interrompido; a classificação completa fica registrada aqui, sem chamar as vulnerabilidades de "preexistentes" (o projeto frontend foi criado neste incremento). `npm audit fix` não foi executado (exigiria downgrades de major do `@angular/cli`/`@angular/build`, alteração de dependência fora do escopo autorizado desta etapa).

### 10.4 Resultados finais consolidados

| Suíte | Resultado |
|---|---|
| `IHostPro.ArchitectureTests` | 120/120 |
| `IHostPro.Contexts.Identity.Tests.Unit` | 468/468 |
| `IHostPro.Contexts.Identity.Tests.Integration` | 411/411 |
| `IHostPro.Contexts.PropertyManagement.Tests.Integration` | 184/184 |
| `IHostPro.Contexts.Reservations.Tests.Integration` | 52/52 |
| `WolverineThreeStoreCompositionTests` | 4/4 |
| `ng test --watch=false` (frontend) | 28/28 |
| `IHostPro.Web.Tests.E2E` (Playwright) | 6/6 |
| `npm run generate:api` (determinismo) | Confirmado — geração 1 e 2 idênticas |
| `ng build --configuration production` | Aprovado |
| CORS (teste focado) | Aprovado |

### 10.5 Estado neste momento

Incremento 1 concluído e commitado (4 commits funcionais). **Push ainda pendente.** Fase 4 continua em andamento — branch `feature/frontend-foundation` não integrada em `master`. Incremento 2 (Administração de Usuários) ainda não iniciado.
