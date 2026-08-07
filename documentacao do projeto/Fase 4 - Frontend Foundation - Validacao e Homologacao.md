# Fase 4 — Frontend Foundation — Validação e Homologação

Versão: 1.4

Status: **Fase 4 (Frontend Foundation) encerrada e integrada em `master`** (Seção 14) — os 4 incrementos (Fundação/Autenticação — Seção 10; Administração de Usuários — Seções 11.11–11.12; Gestão de Condomínios e Imóveis — Seções 12.9–12.10; Gestão de Reservas — Seções 13.13–13.14) aprovados, versionados e publicados em `feature/frontend-foundation`, depois integrados em `master` por fast-forward. `origin/master` sincronizado.

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

### 10.5 Publicação

`git push -u origin feature/frontend-foundation` executado com sucesso — branch remota criada, upstream configurado. Confirmado após o push: `git status` reporta "up to date with 'origin/feature/frontend-foundation'"; `git rev-list --left-right --count origin/feature/frontend-foundation...feature/frontend-foundation` retorna `0 0`; working tree limpa. Os cinco commits (Seção 10.2 + o commit deste registro) estão publicados na branch remota.

### 10.6 Estado neste momento

Incremento 1 concluído, commitado e publicado. Fase 4 continua em andamento — branch `feature/frontend-foundation` não integrada em `master` (nenhum merge realizado). Incremento 2 (Administração de Usuários) em andamento.

## 11. Incremento 2 — Administração de Usuários

### 11.1 Escopo

Interface administrativa de usuários (rota `/users`, hoje um placeholder — Checkpoint 4) consumindo exclusivamente os endpoints reais já existentes em `UserAdministrationController`/`RolesController` (Identity, Incremento 3): listagem paginada com busca/filtro por status, criação, edição (nome/e-mail), bloqueio/desbloqueio, redefinição administrativa de senha, atribuição/remoção de papel (usando o catálogo real de papéis). Protegida por `permissionGuard` + `USERS:MANAGE`.

### 11.2 Fora de escopo

Gerenciamento das próprias sessões, alteração da própria senha, CRUD de papéis, CRUD de permissões, condomínios, imóveis, reservas, dashboard completo, qualquer funcionalidade das Fases 5+ (Documento 00, Plano Executivo).

### 11.3 Permissões reais

Confirmadas em `IdentityPermissionCodes.cs` e no controller real, nunca inferidas: `USERS:MANAGE` (todas as 9 ações administrativas de usuário) e `ROLES:READ` (catálogo de papéis, necessário para o seletor de papel usado na criação/atribuição). Ambas já expostas por `GET /api/v1/users/me` → `roles`/permissões efetivas resolvidas no backend (nunca decodificação de JWT no cliente, mesma regra do Checkpoint 3).

### 11.4 Endpoints consumidos (`UserAdministrationController` + `RolesController`, confirmados no código-fonte)

| Método | Rota | Permissão | Request | Response |
|---|---|---|---|---|
| POST | `/api/v1/users` | `USERS:MANAGE` | `CreateUserRequest(fullName, email, initialPassword, roleCode)` | `UserResponse` (201) |
| GET | `/api/v1/users` | `USERS:MANAGE` | query: `page, pageSize, search, status` | `PagedUserResponse` |
| GET | `/api/v1/users/{userId}` | `USERS:MANAGE` | — | `UserResponse` |
| PATCH | `/api/v1/users/{userId}` | `USERS:MANAGE` | `UpdateUserRequest(fullName?, email?)` | `UserResponse` |
| POST | `/api/v1/users/{userId}/roles` | `USERS:MANAGE` | `AssignRoleRequest(roleCode)` | 204 |
| DELETE | `/api/v1/users/{userId}/roles/{roleCode}` | `USERS:MANAGE` | — | 204 |
| POST | `/api/v1/users/{userId}/block` | `USERS:MANAGE` | — | 204 |
| POST | `/api/v1/users/{userId}/unblock` | `USERS:MANAGE` | — | 204 |
| POST | `/api/v1/users/{userId}/reset-password` | `USERS:MANAGE` | `ResetPasswordRequest(newPassword)` | 204 |
| GET | `/api/v1/roles` | `ROLES:READ` | — | `RoleResponse[]` (`code, name, permissionCodes`) |

Nenhum endpoint declara hoje `[ProducesResponseType]` (mesma lacuna documentada na Seção 5.5) — corrigido no Gate do OpenAPI (Seção 11.5) apenas para os endpoints listados aqui.

Códigos de erro reais (confirmados em `ResultHttpMapper.cs`, mapeamento fechado e centralizado): 404 (`UserNotFound`, `RoleNotFound`); 409 (`EmailAlreadyInUse`, `RoleAlreadyAssigned`, `RoleNotAssigned`, `UserMustHaveAtLeastOneRole`, `LastActiveAdministrator`, `UserAlreadyBlocked`, `UserAlreadyActive`, `UserConcurrencyConflict`, `AdminCannotResetOwnPassword`); 400 com `ProblemDetails.Extensions["codes"]` (validação, `NoChangesProvided`).

### 11.5 Checkpoints

1. Gate do OpenAPI: `[ProducesResponseType]` nos 10 endpoints acima, regeneração NSwag determinística. **Concluído** (Seção 11.7 — ampliado para incluir também `GET /api/v1/users/me`, cujo contrato mudou).
2. Listagem: rota `/users`, tabela Material (ordenação/paginação/busca/filtro de status conforme Documento 14 §14), estados vazio/carregando/erro. **Concluído.**
3. Criação e edição de usuário (diálogos Material). **Concluído.**
4. Bloqueio/desbloqueio com confirmação (ação sensível, Documento 14 §17). **Concluído** (bloqueio exige confirmação; desbloqueio, por não ser destrutivo, não exige — decisão de UX consistente com a ausência de exigência explícita de confirmação para essa ação específica).
5. Atribuição/remoção de papel usando `GET /api/v1/roles` real. **Concluído** (atribuição não exige confirmação — ação aditiva, reversível por uma remoção; remoção exige confirmação — ação destrutiva).
6. Redefinição administrativa de senha (diálogo, ação sensível). **Concluído.**
7. Testes unitários frontend + Playwright (fluxos principais). **Concluído** (Seções 11.9–11.10).

### 11.6 Critérios de aceite

Todas as ações usam exclusivamente o cliente NSwag regenerado (nenhum contrato manual); rota protegida por `permissionGuard`/`USERS:MANAGE` (navegação oculta/rota bloqueada sem a permissão, nunca por nome de papel, nunca por JWT decodificado); toda ação sensível (bloquear, redefinir senha, remover papel) exige confirmação; toda ação produz feedback (sucesso/erro); nenhum CRUD de papéis/permissões; nenhuma funcionalidade de Fases 5+; testes unitários e Playwright cobrindo os fluxos principais aprovados.

### 11.7 Conflito de autorização descoberto durante a implementação — permissões efetivas ausentes do perfil próprio

**Conflito encontrado**: `permissionGuard`/navegação precisam decidir se o usuário autenticado possui `USERS:MANAGE` — mas `GET /api/v1/users/me` (`OwnProfileResponse`, Checkpoint 3) retornava apenas `roles` (nomes de papéis), nunca as permissões efetivas. Derivar permissões no frontend cruzando papéis contra `GET /api/v1/roles` é impossível para um usuário não-administrador: esse próprio endpoint exige `ROLES:READ`, que (confirmado em `IdentityCatalogSeed.cs`) só `ADMIN` possui. Três alternativas foram levantadas e todas rejeitadas pelo usuário: usar `ADMIN` como substituto de `USERS:MANAGE`; derivar permissões no cliente cruzando papéis com o catálogo; depender apenas de um 403 reativo do backend para descobrir a autorização.

**Decisão do usuário**: corrigir minimamente o contrato de `GET /api/v1/users/me` para retornar as permissões efetivas do usuário, calculadas no backend, nunca no cliente.

**Correção aplicada**: `OwnProfileResult`/`OwnProfileResponse` ganharam um campo `Permissions: IReadOnlyCollection<string>`, calculado em `GetOwnProfileQueryHandler` reutilizando `IPermissionReader` — a mesma infraestrutura já usada por `PermissionAuthorizationHandler` para aplicar `[Authorize(Policy = ...)]` em toda a API; nenhuma lógica de autorização duplicada. As permissões retornadas são a união distinta e ordenada (ordinal) dos códigos de permissão de todos os papéis do usuário. Nenhuma nova permissão foi criada para o próprio endpoint — permanece protegido apenas por `[Authorize]` (autenticação), já que expõe apenas o perfil do próprio chamador.

**Frontend**: `UserProfileService.permissions` (computed signal) e `hasPermission(code)` tornaram-se a única fonte de autorização — `roles` permanece disponível apenas para exibição. `permissionGuard` passou a ler `route.data['permissions']` (antes `'roles'`) e comparar contra `hasPermission`, nunca contra nome de papel. O item de navegação "Usuários" (`AdminLayout`) passou a ser filtrado por `requiredPermission: 'USERS:MANAGE'` via `hasPermission`, computado reativamente. Fail-closed by construction: `permissions` cai para array vazio sempre que não há perfil carregado (ainda não logado, limpo no logout, ou falha de refresh) — `hasPermission` sobre array vazio é sempre `false`, nunca "assume liberado".

**Testes de backend**: `GetOwnProfileQueryHandlerTests` (7/7, unitário) — inclui união deduplicada/ordenada de múltiplos papéis e papel sem nenhuma permissão retornando coleção vazia. `UsersEndpointsTests` (20/20, integração, 8 novos) — ADMIN inclui `USERS:MANAGE`; usuário com múltiplos papéis recebe a união; permissões compartilhadas por mais de um papel aparecem uma única vez; ordem determinística; papel sem permissão produz coleção vazia; nenhuma permissão de papel não atribuído vaza; permissões refletem apenas o papel do próprio tenant do chamador; a resposta expõe somente strings de código, sem metadados internos.

**Regeneração NSwag**: `npm run generate:api` executado após a mudança de contrato — `OwnProfileResponse.permissions?: string[]` presente no cliente gerado; segunda geração confirmada byte-a-byte idêntica (determinismo).

### 11.8 Defeito real de infraestrutura de teste E2E — processo órfão do Windows mascarando execuções já concluídas como travadas

**Defeito real encontrado**: `WebE2EFixture.StartWebProcess()` iniciava o `ng serve` via `cmd.exe /c npm.cmd start -- --port {porta}` (uma correção anterior para um problema de resolução de módulo do próprio `npm.cmd` quando invocado diretamente por `Process.Start` no Windows). Essa cadeia de múltiplos processos (`cmd.exe` → `npm.cmd` → `npm` → `ng serve`) quebra `Process.Kill(entireProcessTree: true)` — usado em `DisposeAsync()` para encerrar o processo do Angular ao final de cada execução —, porque nem todo processo intermediário permanece como ancestral direto e rastreável do processo real do `ng serve`. O `node.exe` real do `ng serve` (e seu filho `esbuild.exe`) sobrevivia como órfão, segurando aberto o handle de saída padrão herdado do processo de teste — impedindo que qualquer leitor externo (`tail`, captura de log) jamais visse EOF, mesmo com o `dotnet test` já encerrado havia muito tempo. O sintoma observado repetidas vezes durante a sessão: execuções que na realidade já haviam terminado (com resultado real, positivo ou negativo, já obtido) permaneciam "penduradas" por dezenas de minutos do ponto de vista de qualquer monitoramento externo, levando a diagnósticos incorretos de ambiente/timing quando o problema real era este.

**Correção aplicada**: `StartWebProcess()` passou a invocar `node "<node_modules>/@angular/cli/bin/ng.js" serve --port {porta}` diretamente — sem `cmd.exe`, sem `npm.cmd`, sem hop intermediário. O processo rastreado pelo `.NET Process` é, agora, o próprio `node.exe` do Angular CLI; `Kill(entireProcessTree: true)` alcança corretamente ele e seu único filho (`esbuild.exe`). Verificado isoladamente (fora da suíte, via `Process.Start`/`Kill` diretos): a árvore de processo é rasa (um pai, um filho) e ambos são encerrados de forma confiável.

**Validação da correção**: sem ela, uma execução isolada de um único teste permanecia com o processo `dotnet test` "vivo" por mais de 20 minutos. Com ela, a classe completa `UsersManagementE2ETests` (9 testes) e depois o assembly completo (19 testes: 6 `AuthenticationE2ETests` + 4 `UsersAuthorizationE2ETests` + 9 `UsersManagementE2ETests`, todos compartilhando uma única instância de `WebE2EFixture` via `WebE2EFixtureCollection` — ver Seção 11.10) executaram em primeiro plano, sem intervenção manual, em 1 minuto e 6,48 segundos.

### 11.9 Testes unitários frontend (Incremento 2)

17 arquivos de spec, **108/108 aprovados** (`ng test --watch=false`), cobrindo:

- `UserProfileService` (permissões computadas, `hasPermission`, fail-closed sem perfil, `clear()`).
- `permissionGuard` (reescrito para `permissions`, não `roles`) e `AdminLayout` (item de navegação mostrado/ocultado por `hasPermission`, fail-closed com permissões vazias, itens sem permissão exigida sempre visíveis).
- `AuthService` (login carrega o perfil real; logout limpa permissões mesmo com falha do backend; `restoreSession` recarrega o perfil ao restaurar sessão e limpa tudo se o refresh falhar).
- `UsersService` (delegação 1:1 de cada método para o `Client` gerado).
- `classifyUserActionError` (mesmo padrão de duck-typing de `login-error.ts`, aplicado às ações de usuário).
- `ConfirmDialog`, `UserFormDialog` (modo criação vs. edição, validação, prevenção de submissão duplicada, classificação de erro 409/400/genérico), `RoleManagementDialog` (papéis atribuíveis excluem os já atribuídos, atribuição/remoção com confirmação, prevenção de chamada concorrente), `ResetPasswordDialog` (mesmo padrão), `UsersList` (estados carregando/vazio/erro, filtro de status sem debounce, criação/edição com recarga condicionada ao resultado do diálogo, bloqueio com confirmação, desbloqueio direto).

### 11.10 Testes E2E Playwright (.NET) — Incremento 2

Todas as classes de teste E2E deste projeto passaram a compartilhar uma única instância de `WebE2EFixture` via `[Collection(WebE2EFixtureCollection.Name)]`/`ICollectionFixture` — necessário porque, com mais de uma classe usando `IClassFixture<WebE2EFixture>`, o xUnit paraleliza coleções por padrão, e duas instâncias do fixture disputariam a mesma porta fixa do RabbitMQ (5672) simultaneamente (reproduzido e confirmado durante a investigação: erro real do Docker "port is already allocated"). A coleção compartilhada garante execução sequencial e um único boot de Postgres/RabbitMQ/Redis/API/Angular para todo o assembly.

**Autorização (`UsersAuthorizationE2ETests`, 4 testes — obrigatórios antes de continuar o restante do incremento, conforme instrução do usuário)**: ADMIN vê o item "Usuários" e acessa `/users`; usuário autenticado sem `USERS:MANAGE` (papel `OPERATOR`, seedado especificamente para este fim em `WebE2EFixture`, que nunca recebe essa permissão conforme `IdentityCatalogSeed.cs`) não vê o item de navegação; esse mesmo usuário, navegando diretamente para `/users`, é redirecionado para `/forbidden`; chamada direta e real à API com o token do OPERATOR (capturado da requisição real de login, nunca fabricado) recebe 403 do backend — prova que a aplicação da regra não depende do frontend. **4/4 aprovados.**

**Administração de usuários (`UsersManagementE2ETests`, 9 testes)**: listagem exibe os usuários reais seedados (ADMIN e OPERATOR); criação via diálogo aparece na lista com mensagem de sucesso; edição atualiza o nome exibido; bloqueio exige confirmação e atualiza a coluna de status; desbloqueio não exige confirmação e restaura o status; atribuição de papel não exige confirmação e mostra o novo chip; remoção de papel exige confirmação e remove o chip; redefinição de senha mostra mensagem de sucesso; duplo clique rápido no botão "Salvar" da criação cria o usuário uma única vez (botão desabilitado pelo signal `submitting` no primeiro clique). **9/9 aprovados.**

Dois defeitos reais de teste (não de produto) encontrados e corrigidos durante a estabilização: (1) `Removing_a_role...` usava dado de teste inválido — um usuário com um único papel, que o backend corretamente rejeita remover (`UserMustHaveAtLeastOneRole`, Seção 11.7); corrigido seedando um segundo papel antes da remoção. O mesmo teste também tinha dois locators não escopados ao diálogo aberto, que também casavam com a tabela de usuários ao fundo (ainda presente no DOM sob o modal) — corrigidos escopando ao `role="dialog"`. (2) `Submitting_the_create_form_twice...` verificava a tabela imediatamente após o snackbar de sucesso, mas o recarregamento da lista (`loadUsers()`) é disparado depois do snackbar e é assíncrono — corrigido aguardando a própria linha da tabela aparecer antes de contar.

**Autenticação (`AuthenticationE2ETests`, 6 testes, Incremento 1 — revalidados nesta rodada por terem sido afetados pela correção da Seção 11.8, que altera código de fixture compartilhado)**: sem alteração de comportamento; **6/6 aprovados.**

**Resultado final consolidado**: assembly completo, execução única, fixture única compartilhada — **19/19 aprovados**, 1 minuto e 6,48 segundos. Stack homolog (`ihostpro-homolog-postgres`/`-rabbitmq`/`-redis`) parado antes de cada execução (porta 5672 fixa do RabbitMQ efêmero do teste) e restaurado ao estado exato anterior (mesmas portas, mesma restart policy `no`, mesmos volumes nomeados preservados) ao final de cada rodada. Nenhum container Testcontainers/Ryuk órfão; nenhum processo `dotnet`/`node`/`npm`/`ng serve` órfão — confirmado após cada rodada.

### 11.11 Encerramento do Incremento 2

**Aprovação**: Incremento 2 aprovado tecnicamente pelo usuário e autorizado para versionamento e publicação na branch `feature/frontend-foundation`.

**Commits** (três commits funcionais, cada um isolado ao seu próprio grupo de arquivos, revisado via `git diff --cached` antes de cada commit — nunca `git add .`):

| # | Hash completo | Mensagem | Escopo |
|---|---|---|---|
| 1 | `1c30117c26b3c27a4f5b97486ce730cc19afc0de` | `feat(identity): expose effective permissions in own profile` | `OwnProfileResult`/`OwnProfileResponse.permissions`, cálculo via `IPermissionReader` em `GetOwnProfileQueryHandler`, `[ProducesResponseType]` nos controllers consumidos pelo Incremento 2, testes backend diretamente relacionados (Seção 11.7). 8 arquivos. |
| 2 | `9c8a60831138efb5415993fb5fbfd7d4a0d552a6` | `feat(frontend): add user administration` | Cliente NSwag regenerado, `UserProfileService`/`permissionGuard` reescritos para `USERS:MANAGE` (nunca nome de papel, nunca JWT decodificado), navegação condicionada, rota `/users`, feature `features/users/` completa (listagem, 4 diálogos, serviço, classificação de erro), traduções, testes unitários frontend. 35 arquivos. |
| 3 | `6a1ce90d7c5b7251633ca87eebed5811ed9b1e0c` | `test(frontend): cover user administration workflows` | `UsersAuthorizationE2ETests`, `UsersManagementE2ETests`, `WebE2EFixtureCollection` (fixture compartilhada, Seção 11.10), correção do `WebE2EFixture` (inicialização do Angular via `node ng.js` direto, Seção 11.8), ajuste mínimo em `AuthenticationE2ETests`. 5 arquivos. |

**Resumo do que cada commit registra, para referência rápida**:
- `OwnProfileResponse.permissions` — permissões efetivas do usuário autenticado, calculadas no backend (Seção 11.7).
- Cálculo via `IPermissionReader` — mesma infraestrutura de `PermissionAuthorizationHandler`, nenhuma lógica de autorização duplicada.
- Código `USERS:MANAGE` — única permissão usada para proteger a rota `/users` e condicionar a navegação.
- **Nenhuma autorização baseada em nome de papel ou JWT decodificado** em nenhum ponto do código de produção ou dos testes (confirmado por revisão de todo o código tocado neste incremento).
- Funcionalidades concluídas: listagem (busca/filtro/paginação/estados), criação, edição, bloqueio/desbloqueio, redefinição de senha, atribuição/remoção de papel — todas com feedback e confirmação onde aplicável (Seções 11.5–11.6).
- Testes unitários: **108/108**. `AuthenticationE2ETests`: **6/6**. `UsersAuthorizationE2ETests`: **4/4**. `UsersManagementE2ETests`: **9/9**. Execução combinada (fixture única): **19/19**.
- `npm run generate:api`: determinístico (duas gerações idênticas byte-a-byte).
- `ng build --configuration production`: aprovado.
- Correção do processo órfão no Windows (`cmd.exe`/`npm.cmd` → `node ng.js` direto): Seção 11.8.
- Stack homolog restaurado ao estado anterior após cada execução que exigiu pará-lo.

**Incremento 2 concluído.** No momento deste registro (antes da publicação — Seção 11.12 abaixo): push ainda pendente; Incremento 3 ainda não iniciado.

### 11.12 Publicação do Incremento 2

Dois pushes: `git push origin feature/frontend-foundation` (`315e1ca..2f701db`, quatro commits: os três funcionais + `docs(frontend): record increment 2 completion`), seguido do commit documental de encerramento `docs(frontend): close increment 2 publication` (`69b2c9a`) e um segundo push (`2f701db..69b2c9a`). Confirmado após cada push: `git status -sb` reporta apenas o cabeçalho da branch (working tree limpa, nada modificado/não rastreado remanescente); `git rev-list --left-right --count origin/feature/frontend-foundation...feature/frontend-foundation` retorna `0 0`.

**Incremento 2 encerrado.** Fase 4 continua não integrada em `master` (nenhum merge realizado, nenhuma tag criada, nenhuma branch excluída, nenhum force push usado).

## 12. Incremento 3 — Gestão de Condomínios e Imóveis

### 12.1 Escopo

Interface administrativa de condomínios e imóveis (rotas `/condominiums` e `/properties`, hoje placeholders — Checkpoint 4 do Incremento 1) consumindo exclusivamente os endpoints reais já existentes em `CondominiumsController`/`PropertiesController` (Property Management, Fase 2): listagem paginada (sem busca/filtro — o backend não oferece), criação, edição de condomínios; listagem paginada, criação, edição e ciclo de vida (ativar/desativar/arquivar) de imóveis; vínculo/remoção de proprietário. Protegida por `permissionGuard` + `PROPERTIES:MANAGE` (única permissão real que existe para este contexto — Seção 12.3).

### 12.2 Fora de escopo

`MyPropertiesController` (portal do proprietário — usado apenas para validar que o contrato existe, se necessário); exclusão de condomínio/imóvel (endpoint inexistente); restauração de imóvel arquivado (endpoint inexistente); busca/filtro em qualquer listagem (backend não oferece); nome/e-mail do proprietário vinculado (endpoint de ownership não os retorna — apenas `OwnerUserId`); Reservas, Agenda, dashboard completo, Housekeeping, Portal da Faxineira, Comunicação, WhatsApp, workflows, IA, qualquer funcionalidade das Fases 5+ (Documento 00, Plano Executivo).

### 12.3 Permissões reais

Confirmadas em `IdentityCatalogSeed.cs` e nos controllers reais, nunca inferidas: **`PROPERTIES:MANAGE`** é a única permissão que protege toda ação administrativa deste incremento — criação/edição/listagem de condomínios, criação/edição/listagem/lifecycle/ownership de imóveis. Não existe permissão específica de condomínio (`CONDOMINIUM*`) nem de ownership (`*OWNER*MANAGE`) no catálogo — ambos reaproveitam `PROPERTIES:MANAGE`. `PROPERTIES:READ` (concedida a OPERATOR/HOUSEKEEPER/AI_AGENT) existe no catálogo mas **nenhum endpoint a exige hoje** — não há capacidade real de "somente leitura" neste contexto; qualquer usuário sem `PROPERTIES:MANAGE` é bloqueado de toda a área, mesmo para apenas visualizar. `PROPERTIES:READ:OWN_OWNER` (PROPERTY_OWNER) protege apenas `MyPropertiesController`, fora de escopo (Seção 12.2).

Verificado contra Documento 09 (§12 Recursos, §15 Matriz Simplificada): documento conceitual, sem códigos literais de permissão — apenas confirma o recurso "Imóveis" com Admin=Controle total (X), Operador/Faxineira/Proprietário=Leitura (L), consistente com o catálogo real (`PROPERTIES:MANAGE`→ADMIN, `PROPERTIES:READ`→OPERATOR/HOUSEKEEPER, `PROPERTIES:READ:OWN_OWNER`→PROPERTY_OWNER). **Nenhuma divergência encontrada** entre documentação e seed real.

### 12.4 Endpoints consumidos (`CondominiumsController` + `PropertiesController`, confirmados no código-fonte)

| Método | Rota | Permissão | Request | Response |
|---|---|---|---|---|
| POST | `/api/v1/condominiums` | `PROPERTIES:MANAGE` | `CreateCondominiumRequest(name, address)` | `CondominiumDetailResponse` (201) |
| GET | `/api/v1/condominiums` | `PROPERTIES:MANAGE` | query: `page, pageSize` | `PagedCondominiumResponse` |
| GET | `/api/v1/condominiums/{condominiumId}` | `PROPERTIES:MANAGE` | — | `CondominiumDetailResponse` |
| PATCH | `/api/v1/condominiums/{condominiumId}` | `PROPERTIES:MANAGE` | `UpdateCondominiumRequest(name?, address?)` | `CondominiumDetailResponse` |
| POST | `/api/v1/properties` | `PROPERTIES:MANAGE` | `CreatePropertyRequest(code, name, capacity, condominiumId?, address?)` | `PropertyDetailResponse` (201) |
| GET | `/api/v1/properties` | `PROPERTIES:MANAGE` | query: `page, pageSize` | `PagedPropertyResponse` |
| GET | `/api/v1/properties/{propertyId}` | `PROPERTIES:MANAGE` | — | `PropertyDetailResponse` |
| PATCH | `/api/v1/properties/{propertyId}` | `PROPERTIES:MANAGE` | `UpdatePropertyRequest` (campos `Optional<T>` — omitido = não alterar) | `PropertyDetailResponse` |
| POST | `/api/v1/properties/{propertyId}/activate` | `PROPERTIES:MANAGE` | — | `PropertyDetailResponse` (200) |
| POST | `/api/v1/properties/{propertyId}/deactivate` | `PROPERTIES:MANAGE` | — | `PropertyDetailResponse` (200) |
| POST | `/api/v1/properties/{propertyId}/archive` | `PROPERTIES:MANAGE` | — | `PropertyDetailResponse` (200) |
| POST | `/api/v1/properties/{propertyId}/owners` | `PROPERTIES:MANAGE` | `LinkPropertyOwnerRequest(ownerUserId)` | `PropertyOwnerResponse` (201) |
| DELETE | `/api/v1/properties/{propertyId}/owners/{ownerUserId}` | `PROPERTIES:MANAGE` | — | 204 |
| GET | `/api/v1/properties/{propertyId}/owners` | `PROPERTIES:MANAGE` | query: `page, pageSize` | `PagedPropertyOwnerResponse` |

Nenhum endpoint declara hoje `[ProducesResponseType]` — o cliente NSwag atual tipa todos os 14 métodos acima como `Observable<void>` (nenhuma interface de resposta é gerada: `CondominiumDetailResponse`, `PropertyDetailResponse` etc. não existem no `api-client.ts` atual). Corrigido no Gate do OpenAPI (Seção 12.5).

**Lifecycle de imóvel** (`PropertyStatus`: `Draft → Active → Inactive`, `Draft/Inactive → Archived`, terminal — sem restauração): `Activate` permitido de `Draft`/`Inactive`; requer endereço efetivo resolvido (próprio ou do condomínio) — falha com `PropertyAddressRequired`/`CondominiumNotFound` se ausente. `Deactivate` permitido apenas de `Active`. `Archive` permitido de `Draft`/`Inactive` (rejeitado a partir de `Active` — deve desativar primeiro). Imóvel `Archived` rejeita qualquer `PATCH` (`ArchivedPropertyCannotBeModified`), inclusive um PATCH sem mudanças reais.

**Ownership**: vínculo verifica elegibilidade no Identity (usuário existe no tenant, `Status=Active`, possui papel `PROPERTY_OWNER`) antes de abrir a própria transação — falhas retornam `OwnerUserNotFound`/`OwnerUserNotEligible`. Vínculo duplicado retorna `PropertyOwnerAlreadyLinked` (aplicado também por índice único no banco). Remoção de vínculo inexistente retorna `PropertyOwnerNotLinked`. A resposta de ownership **não inclui nome/e-mail do proprietário** — apenas `OwnerUserId`; a tela precisará exibir o GUID cru ou (fora de escopo, a menos que trivial) resolver via um endpoint de Identity já existente.

**Códigos de erro reais** (`PropertyManagementErrorCodes.cs`, mapeamento fechado via `PropertyManagementResultHttpMapper` — mapper próprio deste contexto, não compartilhado com o de Identity): 404 (`CondominiumNotFound`, `PropertyNotFound`, `OwnerUserNotFound`, `PropertyOwnerNotLinked`); 409 (`CondominiumConcurrencyConflict`, `PropertyCodeAlreadyExists`, `PropertyConcurrencyConflict`, `PropertyAlreadyActive`, `PropertyAlreadyInactive`, `PropertyAlreadyArchived`, `InvalidPropertyStatusTransition`, `ArchivedPropertyCannotBeModified`, `OwnerUserNotEligible`, `PropertyOwnerAlreadyLinked`); 400 com `ProblemDetails.Extensions["codes"]` (validação FluentValidation + `NoChangesProvided` + os literais `condominium_name_invalid`/`condominium_address_invalid`/`property_code_invalid`/`property_name_invalid`/`property_address_invalid`).

### 12.5 Checkpoints

1. Gate de permissões e Gate do OpenAPI: confirmação do código `PROPERTIES:MANAGE` (Seção 12.3), `[ProducesResponseType]` nos 14 endpoints acima, regeneração NSwag determinística.
2. Checkpoint 1 — Condomínios: rota `/condominiums`, listagem (paginação apenas — sem busca/filtro, backend não oferece), criação, edição, estados vazio/carregando/erro.
3. Checkpoint 2 — Imóveis: rota `/properties`, listagem (paginação apenas), criação, edição, vínculo com condomínio, estados vazio/carregando/erro.
4. Checkpoint 3 — Lifecycle: ativar/desativar/arquivar, exibição do estado atual, apenas ações válidas para o estado atual, confirmação para ações sensíveis, tratamento de 409.
5. Checkpoint 4 — Ownership: visualizar/vincular/remover proprietários, confirmação para remoção, tratamento de usuário inelegível (`OwnerUserNotEligible`) e conflito (`PropertyOwnerAlreadyLinked`).
6. Testes unitários frontend + Playwright (fluxos principais).

### 12.6 Critérios de aceite

Todas as ações usam exclusivamente o cliente NSwag regenerado (nenhum contrato manual); rotas protegidas por `permissionGuard`/`PROPERTIES:MANAGE` (navegação oculta/rota bloqueada sem a permissão, nunca por nome de papel, nunca por JWT decodificado); toda ação sensível (arquivar, remover proprietário) exige confirmação; toda ação produz feedback (sucesso/erro); nenhuma funcionalidade inexistente no backend é inventada (exclusão, restauração, busca/filtro, nome do proprietário); nenhum trabalho em Reservas/Agenda/dashboard/Housekeeping/Fases 5+; testes unitários e Playwright cobrindo os fluxos principais aprovados.

### 12.7 Dependências

Fase 2 (Property Management) homologada, commitada e publicada (pré-condição já satisfeita — `Fase 2 - Property Management - Validacao e Homologacao.md`). Infraestrutura de autorização por permissão real (`OwnProfileResponse.permissions`, `permissionGuard`, Incremento 2) já implementada e reaproveitada sem alteração. Fixture E2E compartilhada (`WebE2EFixtureCollection`) e inicialização direta do Angular via `node ng.js` (Seção 11.8) já estabilizadas e reaproveitadas.

### 12.8 Encerramento do Incremento 3

**Arquitetura entregue**: `frontend/IHostPro.Web/src/app/features/property-management/` — `condominiums.service.ts`, `properties.service.ts`, `property-management-error.ts` (mesmo padrão duck-typing de `users/user-error.ts`), subpastas `condominiums/` (`condominiums-list`, `condominium-form-dialog`), `properties/` (`properties-list`, `property-form-dialog`), `ownership/` (`property-owners-dialog`). Rotas `/condominiums` e `/properties` substituindo os placeholders, protegidas por `permissionGuard` + `PROPERTIES:MANAGE`; item de navegação correspondente também gated. Nenhuma dependência nova (sem NgRx, sem store global, sem biblioteca de tabela/visual nova) — apenas Angular Material + Transloco + o cliente NSwag regenerado, conforme Seção 12.6.

**Testes unitários frontend**: 8 arquivos `.spec.ts` novos (`property-management-error`, `condominiums.service`, `properties.service`, `condominiums-list`, `condominium-form-dialog`, `properties-list`, `property-form-dialog`, `property-owners-dialog`) cobrindo loading/empty/error, criação/edição, paginação, prevenção de duplo-submit, lifecycle (guards `canActivate`/`canDeactivate`/`canArchive`/`canEdit`), vínculo/remoção de proprietário e classificação de erro (404/409/400/genérico). Suíte completa do frontend (incluindo Incrementos 1 e 2, sem regressão): **192/192 aprovados, 25 arquivos de teste**.

**Testes Playwright (.NET)**: 2 classes novas — `PropertyManagementAuthorizationE2ETests` (3 fluxos: acesso negado a Condomínios sem `PROPERTIES:MANAGE`, acesso negado a Imóveis sem `PROPERTIES:MANAGE`, ADMIN acessa ambos) e `PropertyManagementE2ETests` (7 fluxos: listar+criar condomínio, editar condomínio, criar imóvel vinculado a condomínio, editar imóvel, executar as transições de lifecycle permitidas em sequência com verificação de menu por estado, vincular proprietário, remover proprietário). **10/10 aprovados**, reaproveitando a fixture/coleção compartilhada e a inicialização `node ng.js` já estabilizadas (Seção 11.8).

**Defeitos reais encontrados e corrigidos durante a homologação em navegador e a execução dos testes Playwright** (nenhum destes existia antes desta verificação real — nenhum foi presumido):

1. **`condominiums-list.ts`, `openCreateDialog()`** — não passava `data: {}` ao abrir o diálogo de criação, deixando `MAT_DIALOG_DATA` como `null`; `this.data.condominium` lançava `TypeError`. Confirmado via console do navegador. Corrigido adicionando `data: {}`.
2. **`property-owners-dialog.ts`/`.html`** — o `<form (ngSubmit)="linkOwner()">` do formulário de vínculo de proprietário não declara `[formGroup]` (não usa `FormGroup`, apenas um `FormControl` avulso) e o componente importa apenas `ReactiveFormsModule`, nunca `FormsModule`. Sem `FormsModule`, nenhuma diretiva Angular declara a saída `ngSubmit` para um `<form>` sem `[formGroup]` — Angular não sinaliza isso como erro de compilação (bindings de evento não reconhecidos não são validados como bindings de propriedade), então o binding simplesmente nunca dispara. O clique no botão `type="submit"` caía no comportamento nativo do HTML (submissão da página, recarregando o SPA inteiro) — nenhuma requisição HTTP de vínculo era emitida. Descoberto ao testar manualmente o vínculo de proprietário no navegador (o diálogo "desaparecia" sem motivo aparente); causa raiz confirmada comparando com os outros 3 diálogos do incremento, todos usando `[formGroup]` (que ativa `ReactiveFormsModule`'s `FormGroupDirective`, a fonte real de `ngSubmit` nesses casos). Corrigido importando `FormsModule` (que fornece `NgForm`, cujo seletor casa com qualquer `<form>` sem `[formGroup]`) e adicionando `novalidate` ao elemento, alinhando com o padrão dos demais formulários.
3. **Contrato OpenAPI de `UpdatePropertyRequest` incorreto para os campos `Optional<T>`** (defeito mais sério encontrado, afetando 100% das edições de imóvel) — o `OptionalJsonConverter<T>` real (Property Management, já existente) lê/escreve, no JSON, o valor bruto de `T` (ou `null` explícito) diretamente na posição da própria propriedade — nunca um objeto `{isSet, value}`; "isSet" é expresso pela presença ou ausência da chave JSON, nunca por um wrapper aninhado. A geração de schema padrão do Swashbuckle, porém, ignora esse `JsonConverter` customizado e reflete a forma pública do struct `Optional<T>` (`IsSet`/`Value`) — produzindo um schema OpenAPI incorreto. O NSwag gerado a partir desse schema errado produzia, portanto, um cliente que serializava `{ code: { isSet: true, value: "..." } }` em vez de `{ code: "..." }`, e todo `PATCH /api/v1/properties/{id}` retornava `400 Bad Request` ("The JSON value could not be converted to System.String. Path: $"). Descoberto ao testar manualmente a edição de um imóvel no navegador. Corrigido na origem — não com um contrato manual no frontend — adicionando `OptionalSchemaFilter` (`src/Host/IHostPro.Api/Swagger/`, único lugar onde Swashbuckle está referenciado) que substitui o schema gerado de qualquer `Optional<T>` pelo schema do próprio `T` (resolvendo também o caso em que `T` é um tipo complexo já registrado como componente nomeado, como `AddressRequest`, via lookup no `SchemaRepository`). NSwag regenerado (determinismo confirmado, duas execuções idênticas); `properties.service.ts.update()` ajustado para enviar valores diretamente (incluindo `null` explícito, preservado via um cast documentado — o parâmetro `nswag.json`'s `"nullValue": "Undefined"`, já existente e usado por todo o resto do cliente, não expõe `| null` nos tipos gerados, mas o valor `null` em tempo de execução continua sendo serializado corretamente pelo `JSON.stringify`). Verificado manualmente nos dois ramos (imóvel com endereço próprio e imóvel usando o endereço do condomínio) e via Playwright.
4. **`admin-layout.spec.ts`, teste pré-existente do Incremento 1** — um teste ("always shows nav items that declare no required permission...") presumia que o item de navegação "Condomínios" nunca exigia permissão (verdade quando a rota era um placeholder); a gate de permissão real deste incremento (`PROPERTIES:MANAGE`) quebrou essa asserção. Corrigido trocando a asserção para `/reservations` (item real ainda sem gate) e adicionando dois testes dedicados para a gate de `PROPERTIES:MANAGE` em "Condomínios"/"Imóveis".
5. **Três defeitos nos testes Playwright novos, não no produto** — `GetByLabel("Condomínio")` sem `Exact: true` casava por substring com o rótulo do checkbox "Endereço próprio (diferente do condomínio)"; `GetByRole(Button, Name: "Remover")` sem escopo casava tanto o botão de remoção por proprietário quanto o botão de confirmação do diálogo empilhado; `GetByRole(Menuitem, Name: "Ativar")` sem `Exact: true` casava por substring com "Desativar" (o único item presente no estado Ativo), fazendo a asserção de ausência falhar persistentemente mesmo com o backend e o frontend corretos — confirmado via diagnóstico direto (payload real da API, `outerHTML` da linha, conteúdo do menu reaberto) antes da correção. Todos os três corrigidos com `Exact: true` e/ou escopo ao diálogo mais recente.

**Validação proporcional executada**: build completo da solução (`dotnet build IHostPro.sln`, 0 erros); testes unitários de Property Management focados (180/180); testes de arquitetura (120/120, cobrindo o novo `OptionalSchemaFilter` no Host); build do frontend (`ng build`, limpo); testes unitários do frontend (192/192); suíte completa dos 10 fluxos Playwright deste incremento (10/10); `git diff --check` sem erros de espaço em branco reais (apenas avisos benignos de LF/CRLF). Não foram re-executadas as suítes completas de Identity/Reservations/Wolverine/arquitetura de outros contextos — nenhuma mudança neste incremento as afeta, exceto a arquitetura (executada por precaução, dado o novo arquivo no Host).

**Débito técnico registrado**: `PROPERTIES:READ` existe no catálogo de permissões mas nenhum endpoint o exige hoje — não há capacidade real de "somente leitura" neste contexto (já documentado na Seção 12.3, não é uma decisão tomada nesta implementação). O mesmo defeito de schema do `Optional<T>` (item 3 acima) existe potencialmente em Reservations (`ReservationsInt32Optional`, mesmo padrão de conversor), mas está fora do escopo deste incremento — o frontend de Reservas ainda não foi construído (Seção 12.2) — e não foi corrigido aqui.

**Confirmações finais**: nenhuma autorização foi feita por nome de papel ou por JWT decodificado em nenhum ponto — exclusivamente `OwnProfileResponse.permissions`/`permissionGuard`, backend como autoridade final. Nenhum trabalho de Reservas (frontend), Agenda, dashboard completo, Housekeeping, Portal da Faxineira, Comunicação, WhatsApp, workflows, IA ou qualquer funcionalidade de Fase 5+ foi realizado. Nenhuma operação de versionamento (commit, push, merge, tag) foi realizada após o início deste incremento — o repositório permanece exatamente como estava ao final do Incremento 2, apenas com as mudanças deste incremento não commitadas na árvore de trabalho.

### 12.9 Encerramento do Incremento 3

**Aprovação**: Incremento 3 aprovado tecnicamente pelo usuário e autorizado para versionamento e publicação na branch `feature/frontend-foundation`.

**Commits** (três commits funcionais, cada um isolado ao seu próprio grupo de arquivos, revisado via `git diff --cached` antes de cada commit — nunca `git add .`):

| # | Hash completo | Mensagem | Escopo |
|---|---|---|---|
| 1 | `ae814f0387b8f67b2ce1c1a5343ab57921e40d82` | `fix(openapi): describe property management contracts` | `[ProducesResponseType]` nos 14 endpoints de `CondominiumsController`/`PropertiesController`; `OptionalSchemaFilter` (novo, `src/Host/IHostPro.Api/Swagger/`) e seu registro em `Program.cs` — corrige o schema OpenAPI de `Optional<T>` na origem (Seção 12.8, item 3). 4 arquivos. |
| 2 | `267c939e59b5b004e7d4cf3c67895208d7c2b5c2` | `feat(frontend): add property management` | Cliente NSwag regenerado; rotas `/condominiums`/`/properties` e navegação condicionadas a `PROPERTIES:MANAGE`; feature `features/property-management/` completa (serviços, listagens, diálogos de condomínio/imóvel/ownership, classificação de erro); traduções; testes unitários frontend. 32 arquivos. |
| 3 | `d188f7e602e71c3a82c274bdbfa5b6b9d949edd7` | `test(frontend): cover property management workflows` | `PropertyManagementAuthorizationE2ETests` (3 fluxos), `PropertyManagementE2ETests` (7 fluxos) — nenhum ajuste na fixture compartilhada foi necessário desta vez. 2 arquivos. |

**Resumo do que cada commit registra, para referência rápida**:
- Código `PROPERTIES:MANAGE` — única permissão real usada para proteger `/condominiums`, `/properties` e condicionar a navegação (Seção 12.3).
- **Nenhuma autorização baseada em nome de papel ou JWT decodificado** em nenhum ponto do código de produção ou dos testes (confirmado por revisão de todo o código tocado neste incremento).
- 14 endpoints reais consumidos (Seção 12.4), nenhum inventado.
- `OptionalSchemaFilter` — corrige a raiz do defeito de contrato descrito na Seção 12.8, item 3 (nunca um contrato manual no frontend).
- Funcionalidades concluídas: Condomínios (listar/criar/editar), Imóveis (listar/criar/editar/vínculo com condomínio), lifecycle (ativar/desativar/arquivar com confirmação), ownership (vincular/remover proprietário) — todas com feedback e confirmação onde aplicável (Seções 12.5–12.6).
- Testes unitários de Property Management (backend): **180/180**. Testes de arquitetura: **120/120**. Testes unitários frontend (25 arquivos, incluindo Incrementos 1–2 sem regressão): **192/192**. `PropertyManagementAuthorizationE2ETests` + `PropertyManagementE2ETests`: **10/10**.
- `npm run generate:api`: determinístico (duas gerações idênticas byte-a-byte).
- `dotnet build IHostPro.sln`: aprovado. `ng build`: aprovado.
- `git diff --check`: sem erros reais (apenas avisos benignos de LF/CRLF).
- Cinco defeitos reais encontrados e corrigidos durante a homologação, detalhados na Seção 12.8.
- Stack homolog restaurado ao estado anterior após cada execução que exigiu pará-lo.

**Incremento 3 concluído.** No momento deste registro (antes da publicação): push ainda pendente; Incremento 4 ainda não iniciado.

### 12.10 Publicação do Incremento 3

Push `git push origin feature/frontend-foundation` (`69b2c9a..fe6d5c9`, quatro commits: os três funcionais desta seção + `docs(frontend): record increment 3 completion`, hash `fe6d5c9`). Confirmado após o push: `git status -sb` reporta apenas o cabeçalho da branch (working tree limpa); `git rev-list --left-right --count origin/feature/frontend-foundation...feature/frontend-foundation` retorna `0 0`.

**Incremento 3 encerrado.** Fase 4 continua não integrada em `master` (nenhum merge realizado, nenhuma tag criada, nenhuma branch excluída, nenhum force push usado).

## 13. Incremento 4 — Gestão de Reservas

### 13.1 Escopo

Interface administrativa de reservas manuais (rota `/reservations`, hoje um placeholder — Checkpoint 4 do Incremento 1) consumindo exclusivamente os 5 endpoints reais já existentes em `ReservationsController` (Reservations, Fase 3, homologada e publicada em `master`): listagem paginada com os filtros reais que o backend oferece (`propertyId`, `status`, `from`/`to` por interseção de período — ordenação é fixa no backend, por `checkInAt` depois `id`, sem controle de ordenação no frontend), detalhe, criação, atualização (PATCH presence-aware — campo omitido nunca altera, `guestPhone` explicitamente `null` remove o telefone), cancelamento com confirmação. Protegida por `permissionGuard` + `RESERVATIONS:MANAGE` (única permissão real que este controller exige — Seção 13.3).

**Decisão de UX registrada**: o campo `PropertyId` do formulário de criação/edição usa um campo de texto para o GUID do imóvel (mesmo padrão já usado para `ownerUserId` no diálogo de ownership do Incremento 3), em vez de um seletor dependente de `GET /api/v1/properties`. Motivo: `GET /api/v1/properties` exige `PROPERTIES:MANAGE`, permissão que o papel `OPERATOR` **não possui** (Seção 12.3) — mas o Documento 09 (§6) e o seed real confirmam que `OPERATOR` pode "Cadastrar reservas manuais" via `RESERVATIONS:MANAGE`, que ele possui. Um seletor dependente de `PROPERTIES:MANAGE` bloquearia essa capacidade real do papel `OPERATOR`. Decisão dentro da autoridade técnica do Incremento (Constituição de Engenharia §13 — detalhe de implementação interno, não altera contrato público nem regra de negócio), registrada aqui para transparência.

### 13.2 Fora de escopo

Agenda unificada/calendário (Documento 14 §11, explicitamente fora deste incremento); dashboard operacional; check-in/checkout operacional; Housekeeping; Portal da Faxineira; Comunicação/WhatsApp; workflows; IA; qualquer funcionalidade das Fases 5+ (Plano Executivo). `RESERVATIONS:READ` e `RESERVATIONS:READ:OWN_OWNER` existem no catálogo de permissões mas nenhum endpoint de `ReservationsController` os exige hoje — sem capacidade real de "somente leitura" separada neste contexto (mesmo padrão já registrado para `PROPERTIES:READ` na Seção 12.3). Retry automático em conflito de concorrência (`ReservationConcurrencyConflict`) — apenas feedback e nova tentativa manual do usuário.

### 13.3 Permissão real

Confirmada em `IdentityCatalogSeed.cs` e no `ReservationsController` real: **`RESERVATIONS:MANAGE`** é a única permissão que protege todas as 5 ações (`Create`, `List`, `GetById`, `Update`, `Cancel` — todas usam exatamente a mesma policy, não há policy separada por ação). Concedida a `ADMIN` e `OPERATOR`; nenhum outro papel a possui. Verificado contra Documento 09 (§15 Matriz Simplificada: Reservas — Admin=X, Operador=X; §6 Operador: "Cadastrar reservas manuais"; §7 Faxineira: explicitamente "Não poderá: Visualizar reservas"; §8 Proprietário: "Poderá consultar: Reservas" mas "Não poderá: Alterar reservas"). **Nenhuma divergência encontrada** entre documentação e seed real — Documento 09 não contém nenhum código literal de permissão (documento puramente conceitual), apenas a Fase 3 (homologação real) confirma o código `RESERVATIONS:MANAGE`.

### 13.4 Endpoints consumidos (`ReservationsController`, confirmados no código-fonte)

| Método | Rota | Permissão | Request | Response |
|---|---|---|---|---|
| POST | `/api/v1/reservations` | `RESERVATIONS:MANAGE` | `CreateReservationRequest(propertyId, guestName, guestPhone?, checkInAt, checkOutAt, guestCount)` | `ReservationDetailResponse` (201) |
| GET | `/api/v1/reservations` | `RESERVATIONS:MANAGE` | query: `propertyId?, status?, from?, to?, page?, pageSize?` | `PagedReservationResponse` |
| GET | `/api/v1/reservations/{reservationId}` | `RESERVATIONS:MANAGE` | — | `ReservationDetailResponse` |
| PATCH | `/api/v1/reservations/{reservationId}` | `RESERVATIONS:MANAGE` | `UpdateReservationRequest` (campos `Optional<T>` — omitido = não alterar; `guestPhone` explícito `null` = remover) | `ReservationDetailResponse` |
| POST | `/api/v1/reservations/{reservationId}/cancel` | `RESERVATIONS:MANAGE` | — (sem corpo) | `ReservationDetailResponse` (200) |

`ReservationDetailResponse`: `id, propertyId, guestName, guestPhone?, checkInAt, checkOutAt, guestCount, status, createdAt, updatedAt`. `ReservationSummaryResponse` (usado na listagem): mesmos campos exceto `guestPhone` (nunca exposto na listagem). `status` é sempre o código estável em minúsculas: `"confirmed"` ou `"cancelled"` (nunca o nome do enum C#). `Cancelled` é terminal — não existe restauração.

**Nenhum endpoint declarava `[ProducesResponseType]` antes deste incremento** — o cliente NSwag anterior tipava todos os 5 métodos como `Observable<void>` (o corpo da resposta era descartado; `ReservationDetailResponse`/`ReservationSummaryResponse`/`PagedReservationResponse` não existiam em `api-client.ts`). Corrigido no Gate do OpenAPI (Seção 13.5).

**Contrato de data/timezone**: `checkInAt`/`checkOutAt` exigem offset explícito no JSON (rejeitado com 400 se ausente — `RequireExplicitOffsetDateTimeOffsetConverter`, aplicado tanto em `CreateReservationRequest` quanto dentro do `Optional<DateTimeOffset>` de `UpdateReservationRequest`); o backend normaliza para UTC antes de persistir e a resposta sempre retorna o valor já em UTC.

**Concorrência**: dois mecanismos independentes — conflito de agenda (duas reservas sobrepostas para o mesmo imóvel, `pg_advisory_xact_lock`, retorna `ReservationDateConflict`/409) e concorrência otimista na edição (`xmin`, retorna `ReservationConcurrencyConflict`/409, **sem retry automático**).

**Códigos de erro reais** (`ReservationsErrorCodes.cs` + dois códigos ad-hoc não catalogados, mapeamento via `ReservationsResultHttpMapper`, mapper próprio deste contexto): 404 (`ReservationNotFound`, `PropertyNotFound`); 409 (`ReservationDateConflict`, `ReservationAlreadyCancelled`, `CancelledReservationCannotBeModified`, `ReservationConcurrencyConflict`); 400 com `ProblemDetails.Extensions["codes"]` (validação FluentValidation + `PropertyNotActive` + `PropertyCapacityExceeded` + `NoChangesProvided` + os literais ad-hoc `guest_name_invalid`/`reservation_schedule_invalid`).

**Defeito confirmado, já conhecido do Incremento 3**: Reservations possui sua própria cópia independente de `Optional<T>`/`OptionalJsonConverter`/`OptionalJsonConverterFactory` (`IHostPro.Contexts.Reservations.Application`/`.Api.Http` — deliberadamente não compartilhada com Property Management, por design arquitetural, Architecture Principles §4), com exatamente o mesmo defeito de schema OpenAPI já corrigido na Seção 12.8, item 3 (confirmado no cliente NSwag anterior: `ReservationsGuidOptional`/`ReservationsInt32Optional`/`ReservationsDateTimeOffsetOptional`/`ReservationsStringOptional` com a forma incorreta `{isSet, value}`). Corrigido no Gate do OpenAPI (Seção 13.5) estendendo o `OptionalSchemaFilter` já aprovado — nunca duplicando-o nem criando um contrato manual no frontend.

### 13.5 Gate do OpenAPI — correções aplicadas

`[ProducesResponseType]` adicionado às 5 ações de `ReservationsController` (nenhuma existia antes). `OptionalSchemaFilter` (`src/Host/IHostPro.Api/Swagger/`) estendido para reconhecer também `IHostPro.Contexts.Reservations.Application.Optional<T>` (tipo genérico independente do de Property Management, verificado explicitamente contra ambas as definições de tipo fechado — nunca por correspondência de nome). NSwag regenerado (determinismo confirmado, duas execuções idênticas byte-a-byte).

### 13.6 Checkpoints

1. Gate de permissões e Gate do OpenAPI: confirmação do código `RESERVATIONS:MANAGE` (Seção 13.3), correções da Seção 13.5.
2. Checkpoint 1 — Listagem e detalhe: rota `/reservations`, listagem paginada com os filtros reais (`propertyId`, `status`, `from`/`to`), estados vazio/carregando/erro, detalhe.
3. Checkpoint 2 — Criação: formulário com os campos reais, tratamento de `PropertyNotFound`/`PropertyNotActive`/`PropertyCapacityExceeded`/`ReservationDateConflict`/validação, prevenção de duplo-submit.
4. Checkpoint 3 — Atualização: PATCH presence-aware real (nunca reenviar todos os campos como no Incremento 3 — aqui o backend distingue omitido de alterado), remoção explícita de `guestPhone` via `null`, tratamento de 404/409/400.
5. Checkpoint 4 — Cancelamento: confirmação obrigatória, tratamento de cancelamento duplicado (409 `ReservationAlreadyCancelled`).
6. Testes unitários frontend + Playwright (10 fluxos).

### 13.7 Critérios de aceite

Todas as ações usam exclusivamente o cliente NSwag regenerado (nenhum contrato manual); rota protegida por `permissionGuard`/`RESERVATIONS:MANAGE` (navegação oculta/rota bloqueada sem a permissão, nunca por nome de papel, nunca por JWT decodificado); toda ação sensível (cancelar) exige confirmação; toda ação produz feedback; datas sempre enviadas com offset explícito, nunca com parsing ambíguo do navegador; nenhuma funcionalidade inexistente no backend é inventada (agenda, calendário, check-in/checkout, retry automático de concorrência); nenhum trabalho de Agenda/dashboard/Housekeeping/Fases 5+; testes unitários e Playwright cobrindo os fluxos principais aprovados.

### 13.8 Dependências

Fase 3 (Reservations) homologada, commitada e publicada em `master` (pré-condição já satisfeita — `Fase 3 - Reservation Management - Validacao e Homologacao.md`). ADR-014 (exceção síncrona Reservations→Property Management para elegibilidade) já implementada no backend, sem alteração necessária no frontend além de tratar os erros reais que ela documenta (`PropertyNotFound`/`PropertyNotActive`/`PropertyCapacityExceeded`). Infraestrutura de autorização por permissão real (`OwnProfileResponse.permissions`, `permissionGuard`) e `OptionalSchemaFilter` (Incremento 3) já implementadas e reaproveitadas. Fixture E2E compartilhada (`WebE2EFixtureCollection`) e inicialização direta do Angular via `node ng.js` já estabilizadas e reaproveitadas.

### 13.9 Defeitos reais encontrados durante a implementação

**`MatChipsModule` importado mas não registrado no array `imports` do componente**: `reservations-list.ts` continha a instrução `import { MatChipsModule } from '@angular/material/chips'` mas não adicionava `MatChipsModule` ao array `imports: [...]` do `@Component`. Compilava sem erro no editor, mas `ng build`/`ng test` falhavam com `NG8001: 'mat-chip-set' is not a known element` — a chip de status na coluna `status` da listagem. Corrigido adicionando `MatChipsModule` ao array de imports do componente.

**`toLocalInputValue()` (diálogo de edição) lançava `TypeError: date.getFullYear is not a function` ao abrir uma reserva para edição**: causa raiz — o `Client` gerado pelo NSwag (`api-client.ts`) deixa `jsonParseReviver` indefinido (`protected jsonParseReviver: ... = undefined`), então **todo** campo de data retornado por **qualquer** endpoint deste projeto é, em tempo de execução, uma string ISO simples, apesar da interface TypeScript gerada declarar `Date`. Esse defeito é pré-existente e abrange o projeto inteiro (não introduzido neste incremento) — nunca havia se manifestado porque todo uso anterior de datas passava direto pelo `DatePipe` do Angular, que aceita string transparentemente. `reservation-form-dialog.ts` foi o primeiro ponto do código a chamar um método próprio (`getFullYear()`, `getMonth()` etc.) diretamente sobre um valor de data vindo da API. Corrigido localmente ampliando a assinatura do parâmetro de `toLocalInputValue` para `Date | string` e normalizando com `value instanceof Date ? value : new Date(value)`, com comentário explicando a causa raiz para não ser re-diagnosticado incorretamente no futuro. **Não corrigido de forma global** (fora do escopo deste incremento — nenhum outro ponto do código hoje depende de métodos de instância de `Date`, apenas de `DatePipe`); registrado aqui como característica conhecida do projeto.

**Defeito de schema OpenAPI `Optional<T>` também presente em Reservations**: já registrado e corrigido na Seção 13.4/13.5 (extensão do `OptionalSchemaFilter` já aprovado no Incremento 3) — apenas referenciado aqui para o índice de defeitos ficar completo.

**Defeito real de infraestrutura de teste E2E — processos órfãos do `WebE2EFixture` (corrigido definitivamente, ver Seção 13.10)**: a suíte Playwright deste incremento falhou de forma não-determinística mais de uma vez porque um `dotnet.exe` (porta 5140) e/ou um `node.exe` (porta 4200) de uma execução anterior sobreviviam como processos órfãos, ocupando as portas fixas que `WebE2EFixture` precisa. Diferente do defeito já corrigido na Seção 11.8 (que tratava apenas da árvore de processos do `ng serve`), a causa raiz aqui era estrutural: `WebE2EFixture.InitializeAsync` não tinha `try/catch` — se qualquer etapa falhasse após containers/processos já terem sido iniciados, nada os limpava, e o xUnit não garante chamar `DisposeAsync` quando `InitializeAsync` lança uma exceção. Corrigido nesta mesma etapa (Seção 13.10), não apenas registrado como risco.

### 13.10 Correção definitiva do lifecycle do `WebE2EFixture`

**Causa raiz confirmada**: `InitializeAsync` executava toda a sequência de inicialização (containers, migrações, processo da API, processo do Angular, browser) sem nenhum `try/catch` — qualquer falha após uma etapa já ter iniciado um recurso real deixava esse recurso sem dono. Separadamente, mesmo quando `DisposeAsync` era de fato chamado, `StopProcessAsync` (antigo) não tinha timeout limitado nem verificava se a porta do sistema operacional realmente havia sido liberada após `Kill(entireProcessTree: true)` — um `taskkill` incompleto no Windows (um processo-neto que escapou da árvore rastreada) podia falhar silenciosamente.

**Correção aplicada**: extraído `ManagedProcess` (`tests/Frontend/IHostPro.Web.Tests.E2E/ManagedProcess.cs`) — encapsula um processo filho com `StopAsync` idempotente, espera limitada (15s), e um fallback `taskkill /PID <pid> /T /F` escopado exatamente ao PID que esta instância iniciou (nunca por nome). `WebE2EFixture` passou a ter uma única rotina de limpeza idempotente (`CleanupAsync`, guardada por `Interlocked.Exchange`) chamada tanto pelo `catch` de `InitializeAsync` (a limpeza roda e a exceção original é relançada sem alteração) quanto por `DisposeAsync` (que agora falha ruidosamente se algo ficou para trás — inclusive uma porta ainda ocupada após o processo dono reportar-se encerrado, verificado via `IPGlobalProperties.GetActiveTcpListeners()`). Nenhum código de produção foi alterado — toda a correção está em `tests/Frontend/IHostPro.Web.Tests.E2E/`.

**Teste preventivo**: `ManagedProcessTests.cs` (5 testes, contra processos reais leves — nunca mocados) prova: `StopAsync` mata o processo e libera a porta que ele mantinha; `StopAsync` é idempotente; `StopAsync` em um processo já encerrado é no-op; uma falha simulada após dois passos de inicialização limpa ambos preservando a exceção original; uma falha simulada após apenas o primeiro passo nunca inicia o segundo. `WebE2EFixtureCleanupTests.cs` (2 testes, contra o `WebE2EFixture` real, nunca inicializado) prova: `DisposeAsync` não falha quando nenhuma infraestrutura foi criada; `DisposeAsync` chamado duas vezes é seguro. **7/7 aprovados.**

### 13.11 Reconstrução do teste de conflito de concorrência

Durante a validação desta correção, o teste `A_concurrency_conflict_is_presented_without_automatic_retry` (versão original, que disparava as duas submissões PATCH através de dois cliques reais em duas abas do navegador) produziu, uma vez em nove execuções, `statusA=200 statusB=200` — as duas requisições concorrentes aceitas. Investigação da causa raiz (leitura de `UpdateReservationCommandHandler`, `ReservationsOutboxTransactionExecutor`, `ReservationRepository`, `ReservationReader`) não encontrou nenhum caminho no backend capaz de produzir esse resultado sob concorrência genuína — mas também não foi possível provar, a partir da versão em UI, que as duas requisições de fato se sobrepuseram no servidor: `Task.WhenAll` sobre dois `ClickAsync()` garante apenas que os cliques são disparados perto um do outro, nunca que o processamento HTTP resultante realmente se sobrepõe.

Por instrução do usuário, o teste foi reconstruído para disparar as duas chamadas PATCH diretamente contra a API real (`page.Context.APIRequest.PatchAsync`, `Task.WhenAll`, duas sessões ADMIN independentes, cada uma com seu próprio token real), eliminando o navegador como fonte de incerteza. O teste agora repete o experimento 5 vezes por execução (reserva nova a cada iteração), verificando em cada uma: exatamente uma resposta 200 e uma 409; a alteração vencedora é a única persistida (`GET` final); exatamente um registro em `reservation_audit_log` para a ação `reservation_updated` (prova de que a requisição perdedora não gerou auditoria nem evento — ambos são gravados atomicamente com o `SaveChanges`/checagem de `xmin` que a rejeita). Como a API colapsa todo código de conflito 409 em um `ProblemDetails` genérico sem a extensão `codes` (só o ramo 400 do `ReservationsResultHttpMapper` a adiciona), o cenário exclui os outros três códigos de conflito por construção: o PATCH altera somente `guestName` (nunca as datas, então `ReservationDateConflict` não pode disparar — `HasConflictingReservationAsync` também exclui a própria reserva) e a reserva usada nunca é cancelada por nada neste teste.

Dois defeitos reais foram encontrados e corrigidos durante essa reconstrução — ambos em código de teste, nunca em código de produção:
1. **`CountReservationAuditEntriesAsync` (novo helper em `WebE2EFixture`) retornava 0 mesmo com o registro de auditoria realmente persistido** — causa raiz: a tabela `reservation_audit_log` é protegida por RLS, e a conexão do migrador não a contorna automaticamente; a consulta precisa, adicionalmente, definir `app.tenant_id` na sessão. Corrigido mirando exatamente o padrão já usado por `SeedTenantAndAdminAsync` (conexão da aplicação, `TenantContext` com o tenant real, `SELECT set_config('app.tenant_id', ..., true)` dentro de uma transação explícita antes da consulta).
2. **Reutilização das mesmas datas em todas as 5 iterações do laço** causava `ReservationDateConflict` (409) já na criação da segunda reserva de teste, contra a reserva (com data inalterada) da iteração anterior. Corrigido usando uma janela de datas própria e não sobreposta por iteração.

**Resultado final, após as duas correções — 3 execuções independentes do teste (15 corridas de concorrência no total): 3/3 aprovadas**, todas com exatamente um vencedor, o estado final correto e exatamente um registro de auditoria. Nenhum código de produção foi alterado; nenhuma flag de teste temporária permaneceu; a falha nunca foi classificada como flakiness conhecida — foi investigada até a causa raiz real (uma limitação da versão em UI do teste, não do backend).

### 13.12 Testes unitários frontend e Playwright (Incremento 4)

**Testes unitários** (`ng test --watch=false`): 4 novos arquivos de spec — `reservation-error.spec.ts`, `reservations.service.spec.ts`, `reservations-list/reservations-list.spec.ts`, `reservation-form-dialog/reservation-form-dialog.spec.ts` —, cobrindo autorização (delegada aos testes de `admin-layout.spec.ts`, que ganharam dois casos novos para o item de navegação "Reservas"), carregamento/vazio/erro, filtros, paginação, criação, edição, `guestPhone` omitido (POST) e explicitamente `null` (PATCH), capacidade excedida, conflito de período, cancelamento e cancelamento repetido (409), prevenção de submissão duplicada, e tratamento de datas. **Suíte completa do projeto: 239/239 aprovados** (192 pré-existentes + 47 novos).

**Playwright (.NET) — assembly completa, duas execuções consecutivas**: `ReservationsAuthorizationE2ETests` (2 testes) e `ReservationsE2ETests` (9 testes, incluindo a reconstrução da Seção 13.11) somam-se às classes já existentes (`AuthenticationE2ETests`, `UsersAuthorizationE2ETests`, `UsersManagementE2ETests`, `PropertyManagementAuthorizationE2ETests`, `PropertyManagementE2ETests`, mais `ManagedProcessTests`/`WebE2EFixtureCleanupTests` da Seção 13.10) — **47 testes no total**. Executada a assembly completa duas vezes consecutivas, sem qualquer intervenção manual entre as rodadas (apenas inspeção): **47/47 aprovados em ambas**, mesma contagem total nas duas, **zero processos órfãos, zero portas presas, zero containers efêmeros remanescentes** confirmado após cada rodada (`Get-NetTCPConnection`, `docker ps -a`).

Como o papel `OPERATOR` seedado em `WebE2EFixture` **possui** `RESERVATIONS:MANAGE` (diferente de `PROPERTIES:MANAGE`, Seção 12.3), o teste de "usuário sem a permissão" usa um usuário `PROPERTY_OWNER` descartável, criado via API real (mesmo padrão de `CreateEligibleOwnerUserViaApiAsync` do Incremento 3) — o único papel real seedado no catálogo que nunca recebe `RESERVATIONS:MANAGE` (Documento 09 §8, apenas `RESERVATIONS:READ:OWN_OWNER`).

O teste de cancelamento repetido cancela a reserva via API real com o menu de ações da linha ainda aberto (e agora desatualizado) na UI, depois clica no item "Cancelar reserva" desatualizado — reproduzindo deterministicamente, sem depender de corrida, o cenário de 409 `ReservationAlreadyCancelled`.

**Validação proporcional** (Seção 18 da instrução do usuário): nenhuma alteração de contrato OpenAPI desde o Gate da Seção 13.5 — NSwag não regenerado novamente. `ng build` (produção) limpo. `git diff --check` sem erros de espaço em branco. Nenhuma suíte completa de Identity/PropertyManagement/Wolverine/arquitetura re-executada (nenhuma regressão concreta identificada que a justificasse). Artefatos locais (`.claude/launch.json`, log manual da API) removidos; container `ihostpro-homolog-rabbitmq` restaurado ao estado anterior (mesmas portas, mesma restart policy, volumes preservados) após cada rodada que exigiu pará-lo.

### 13.13 Encerramento do Incremento 4

**Aprovação**: Incremento 4 aprovado tecnicamente pelo usuário e autorizado para versionamento e publicação na branch `feature/frontend-foundation`, condicionado à correção definitiva do lifecycle do `WebE2EFixture` (Seção 13.10) e à validação por duas execuções consecutivas e limpas da assembly E2E completa (Seção 13.12) — ambas condições satisfeitas antes de qualquer commit.

**Commits** (três commits funcionais, cada um isolado ao seu próprio grupo de arquivos, revisado via `git diff --cached` antes de cada commit — nunca `git add .`):

| # | Hash completo | Mensagem | Escopo |
|---|---|---|---|
| 1 | `14cde37d5411cd60af6d2e3ae24b276a566a536d` | `fix(openapi): describe reservations contracts` | `[ProducesResponseType]` nas 5 ações de `ReservationsController`; `OptionalSchemaFilter` estendido para `Reservations.Application.Optional<T>`. 2 arquivos. |
| 2 | `b700602ae6c3dbc2271ea2e0dc63c8797d2f4331` | `feat(frontend): add reservation management` | Cliente NSwag regenerado; rota/navegação `/reservations` condicionadas a `RESERVATIONS:MANAGE`; feature `features/reservations/` completa; traduções; testes unitários frontend. 18 arquivos. |
| 3 | `57e3bb249bd2ff1a61f95c275fa2dd7d6e6eb2ff` | `test(frontend): cover reservations and harden e2e cleanup` | `ReservationsAuthorizationE2ETests`, `ReservationsE2ETests`; `ManagedProcess`, `ManagedProcessTests`, `WebE2EFixtureCleanupTests`; correção definitiva de `WebE2EFixture`. 6 arquivos. |

**Resumo do que cada commit registra, para referência rápida**:
- Código `RESERVATIONS:MANAGE` — única permissão real usada para proteger `/reservations` e condicionar a navegação (Seção 13.3). Nenhuma autorização baseada em nome de papel ou JWT decodificado.
- 5 endpoints reais consumidos (Seção 13.4), nenhum inventado.
- `OptionalSchemaFilter` estendido — nunca duplicado, nunca contrato manual no frontend.
- Funcionalidades concluídas: listagem/detalhe com filtros reais, criação, edição presence-aware (`guestPhone` `null` explícito), cancelamento com confirmação — todas com tratamento dos erros reais que o backend distingue (Seções 13.6–13.7).
- Testes unitários frontend: **239/239**. Playwright — assembly completa, duas execuções consecutivas: **47/47 em ambas**, zero recursos órfãos.
- Defeito de infraestrutura de teste (processos órfãos do `WebE2EFixture`) corrigido definitivamente, não apenas contornado (Seção 13.10).
- Teste de conflito de concorrência reconstruído com chamadas HTTP diretas após identificar que a versão em UI não conseguia provar sobreposição real de requisições; resultado final inequívoco em 15 corridas (Seção 13.11).
- `dotnet build IHostPro.sln`/projeto E2E: aprovado. `ng build`: aprovado.
- `git diff --check`: sem erros reais (apenas avisos benignos de LF/CRLF).
- Stack homolog restaurado ao estado anterior após cada execução que exigiu pará-lo.

**Incremento 4 concluído.** No momento deste registro (antes da publicação): push ainda pendente. Fase 4 funcionalmente concluída (todos os 4 incrementos implementados e homologados) — ainda não integrada em `master`.

### 13.14 Publicação do Incremento 4

Push `git push origin feature/frontend-foundation` (`7b9e7fe..15b98df`, quatro commits: os três funcionais da Seção 13.13 + `docs(frontend): record increment 4 completion`, hash `15b98df`). Confirmado após o push: `git status -sb` reporta apenas o cabeçalho da branch (working tree limpa); `git rev-list --left-right --count origin/feature/frontend-foundation...feature/frontend-foundation` retorna `0 0`.

**Incremento 4 encerrado. Fase 4 (Frontend Foundation) encerrada — todos os 4 incrementos aprovados, versionados e publicados em `feature/frontend-foundation`.** Integração em `master` registrada na Seção 14.

## 14. Encerramento da Fase 4 — integração em `master`

**Aprovação final**: Fase 4 (Frontend Foundation), com seus 4 incrementos (Fundação/Autenticação; Administração de Usuários; Gestão de Condomínios e Imóveis; Gestão de Reservas) aprovados e publicados individualmente (Seções 10, 11.11–11.12, 12.9–12.10, 13.13–13.14), aprovada para integração em `master`.

**Integração fast-forward**: `git checkout master`; `git fetch origin`; `git pull --ff-only origin master` (`Already up to date`, sem divergência); `git merge --ff-only feature/frontend-foundation` (`Updating 116abbc..0cf8c5b`, `Fast-forward`, 194 arquivos alterados) — nenhum merge commit criado, nenhum rebase, nenhum force push, nenhuma tag.

**Push de `master`**: `git push origin master` (`116abbc..0cf8c5b`). Confirmado: `git status -sb` reporta apenas o cabeçalho da branch (working tree limpa); `git rev-list --left-right --count origin/master...master` retorna `0 0`. `master` contém, em ordem, os commits dos 4 incrementos da Fase 4 (verificado via `git log --oneline master`), encadeados sobre a Fase 3 (Reservations backend) já publicada.

**Fase 4 (Frontend Foundation) encerrada.**

**Débitos remanescentes** (nenhum bloqueia o encerramento desta fase — registrados para avaliação futura, nenhum corrigido nesta etapa por estar fora do escopo homologado):

1. **`jsonParseReviver` do `Client` gerado pelo NSwag não é configurado globalmente** (Seção 13.9) — todo campo de data de qualquer resposta da API é, em tempo de execução, uma string ISO, apesar da interface TypeScript gerada declarar `Date`. Contornado localmente onde já necessário (`reservation-form-dialog.ts`); nenhum outro ponto do código hoje depende de métodos de instância de `Date` sobre um valor vindo da API.
2. **Controllers reais ainda sem metadata OpenAPI (`[ProducesResponseType]`)**: `PermissionsController` (1 ação) e `MyPropertiesController` (2 ações) — nenhum dos dois é consumido pelo frontend atual, por isso ficaram fora do gate de cada incremento (que corrigia apenas os endpoints realmente consumidos, nunca especulativamente). `UsersController` tem cobertura parcial (2 de 4 ações).
3. **Vulnerabilidades `npm` já classificadas, não eliminadas** — Seção 10.3 (Incremento 1): classificação original registrada; reavaliar quando o Angular/dependências forem atualizados.
4. **Débito de retry-safety herdado de Identity** (pré-existente à Fase 4, não introduzido por ela): `LogoutExecutor`/`RevokeOwnSessionExecutor` (`src/Contexts/Identity/IHostPro.Contexts.Identity.Infrastructure/Persistence/`) ainda têm a lacuna de limpeza em retry já conhecida antes do Checkpoint 6 da Fase 2 — deliberadamente adiada, não relacionada ao frontend.

Fase 5 (Configuration & Policy) ainda não iniciada — a branch `feature/configuration-policy` e um plano de leitura (sem implementação, sem commit) são tratados fora deste documento, no relatório final desta etapa.
