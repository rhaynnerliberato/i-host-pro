# Fase 12 — Runbook Operacional do Piloto Manual Airbnb

Versão: 1.0
Status: Aprovado no F12 CP6 Final Cutover Precheck — pendente apenas da seleção real de Owner/Property/Operador do Stage 1 (ver §10).

## 1. Propósito e escopo

Este documento é o procedimento operacional que o operador do piloto deve seguir para reconciliar manualmente as reservas que se originam no Airbnb com o iHostPro, durante o Stage 1 do piloto (`PilotOwnerCount=1`, `PilotPropertyCount=1`, `PilotOperatorCount=1`).

**O que este piloto É:**
- `BusinessChannelOrigin=AIRBNB` — a reserva comercialmente se origina no Airbnb, e o Airbnb continua sendo a fonte de verdade comercial da reserva.
- `TechnicalIntegration=MANUAL_ASSISTED` — um operador humano reconcilia manualmente cada reserva, alteração e cancelamento do Airbnb para dentro do iHostPro, usando o fluxo administrativo já existente (`ReservationsController`, `api/v1/reservations`).

**O que este piloto NÃO É** (nunca descrever de outra forma):
- Não existe integração técnica real com a API do Airbnb.
- Não existe OAuth, client HTTP, webhook, polling ou qualquer sincronização automática com o Airbnb (ADR-023, `AirbnbIntegration.cs` — `IsEnabled` permanentemente `false`, sem `Enable()`/`Disable()`).
- Não existe SLA de sincronia (nunca "sincroniza em 5 minutos", "sincroniza em tempo real", ou qualquer prazo comercial) — reconciliação manual não é integração automática, e nenhum prazo numérico foi aprovado.
- Não existe `ExternalReservationReference` ou qualquer campo técnico que vincule uma reserva do iHostPro a um identificador do Airbnb — foi uma decisão explícita não construir isso agora (ver §5).

## 2. Janela operacional e checkpoints do operador

O operador não precisa cobertura 24×7. É exigida verificação nos seguintes momentos:
1. Início da janela operacional do dia.
2. Antes de check-ins previstos para o dia.
3. Na janela do check-in em si.
4. Imediatamente após qualquer mudança de reserva percebida no Airbnb.
5. Imediatamente após qualquer notificação de cancelamento no Airbnb.
6. Imediatamente após qualquer alerta crítico do sistema (ver §7 — os 3 alarmes CloudWatch e o e-mail operacional já configurado).

## 3. Criação de reserva (nova reserva vista no Airbnb)

1. Verificar novas reservas no painel do Airbnb.
2. Identificar o imóvel (Property) correspondente no iHostPro.
3. **Antes de criar, executar a verificação de duplicidade (§4).** Nunca pular esta etapa.
4. Abrir o frontend administrativo do iHostPro.
5. Criar a reserva com exatamente os campos que `CreateReservationCommand`/`CreateReservationRequest` aceitam — nenhum campo além destes existe:
   - `PropertyId` (o imóvel do Stage 1)
   - `GuestName`
   - `GuestPhone` (se disponível no Airbnb; campo opcional)
   - `CheckInAt` / `CheckOutAt` (com offset de fuso horário explícito)
   - `GuestCount`
6. Confirmar que a criação retornou sucesso (HTTP 201, reserva nasce com status `Confirmed`).
7. Verificar o comportamento posterior esperado (§6.1).

## 4. Verificação de duplicidade (obrigatória antes de qualquer criação)

Procedimento aprovado:
1. Consultar as reservas já existentes para o mesmo imóvel e período — via `GET /api/v1/reservations?propertyId=<PropertyId>&from=<CheckInAt>&to=<CheckOutAt>` (ou a tela equivalente no frontend, que usa o mesmo endpoint).
2. Comparar: mesmo `PropertyId` + sobreposição de `CheckInAt`/`CheckOutAt` + similaridade do nome do hóspede (`GuestName`).
3. Se houver correspondência provável, **não criar uma nova reserva** até confirmar manualmente se a reserva já existe ou se é de fato uma nova reserva.

**Limitação a ser sempre comunicada de forma honesta:** não existe prevenção automática de duplicidade para reservas do Airbnb. Não existe `ExternalReservationReference` nem qualquer mecanismo técnico que impeça duas entradas manuais equivalentes de coexistirem. Isso é um risco aceito explicitamente para o Stage 1 (`ManualDuplicateRiskAccepted=HIGH_PILOT_RISK`, não bloqueador para 1 propriedade) — mitigado apenas pela disciplina operacional deste procedimento, nunca por controle de software.

## 5. Atualização de reserva (mudança vista no Airbnb)

1. Detectar a mudança no Airbnb (datas, hóspede, etc.).
2. Localizar a reserva correspondente no iHostPro (`GET /api/v1/reservations/{reservationId}` ou pela listagem).
3. Comparar os valores atuais da reserva no iHostPro com os novos valores do Airbnb.
4. Atualizar via `PATCH /api/v1/reservations/{reservationId}` (mesmo fluxo do frontend/UI), usando somente os campos que `UpdateReservationRequest` aceita (`PropertyId`, `GuestName`, `GuestPhone`, `CheckInAt`, `CheckOutAt`, `GuestCount`).
5. Verificar o estado final da reserva após a atualização.
6. Verificar consistência downstream quando aplicável (§6.2).

## 6. Cancelamento de reserva (cancelamento visto no Airbnb)

1. Detectar o cancelamento no Airbnb.
2. Localizar a reserva no iHostPro.
3. Cancelar via `POST /api/v1/reservations/{reservationId}/cancel` (mesmo fluxo do frontend/UI — este endpoint não aceita corpo de requisição).
4. Confirmar que o status da reserva reflete o cancelamento.
5. Confirmar os efeitos downstream esperados do cancelamento (§6.3).

## 6.1 Verificação após criação

Conforme a visibilidade disponível no frontend/dashboard, confirmar:
- a reserva existe;
- o imóvel está correto;
- as datas estão corretas;
- os dados do hóspede estão corretos;
- o estado esperado em Housekeeping (a faxina automática associada à reserva) está presente;
- o estado esperado em Guest Operations está presente.

## 6.2 Verificação após atualização

Confirmar que os dados finais da reserva no iHostPro batem exatamente com o Airbnb.

## 6.3 Verificação após cancelamento

Confirmar que a reserva está no estado cancelado esperado e que nenhuma etapa crítica (faxina, acesso do hóspede) permanece incorretamente ativa.

## 7. Priorização de mudanças críticas

Tratar com prioridade máxima (verificar e reconciliar assim que percebido, não esperar o próximo checkpoint de rotina):
- reserva com check-in no mesmo dia;
- cancelamento com check-in no mesmo dia;
- mudança de data de check-in/check-out;
- mudança de contato do hóspede;
- mudança na quantidade de hóspedes;
- qualquer check-in iminente (próximas horas).

## 8. Níveis de incidente

- **INFO** — reconciliação normal ou variação esperada. Nenhuma ação além do registro implícito da própria reconciliação.
- **OPERATIONAL_WARNING** — atraso na reconciliação, possível duplicidade detectada, crescimento de fila (DLQ), discrepância manual entre Airbnb e iHostPro, problema de processamento não crítico. Resolver no próprio fluxo operacional, sem pausar o piloto.
- **CRITICAL** — API indisponível, Worker indisponível, preocupação de integridade de dados, reserva ausente, cancelamento incorreto, falha crítica de acesso do hóspede, acúmulo de fila não gerenciável. Aciona §9 (pausa do piloto).

Os 3 alarmes CloudWatch já configurados e entregues por e-mail (`ihostpro-homolog-api-unavailable`, `ihostpro-homolog-worker-unavailable`, `ihostpro-homolog-high-error-rate`) cobrem uma parte dos gatilhos CRITICAL — o restante depende da observação direta do operador.

## 9. Pausa do piloto

Um incidente CRITICAL pode exigir pausar a operação do piloto — especialmente quando: o operador fica indisponível, há preocupação de integridade de dados, uma reserva não pode ser reconciliada com segurança, há falha crítica de acesso do hóspede, ou a indisponibilidade do serviço persiste.

**Escalação:**
1. Parar de criar/atualizar qualquer nova atividade do piloto enquanto a segurança da operação estiver incerta.
2. Registrar o incidente.
3. Preservar evidências (não apagar logs, não tentar "corrigir" silenciosamente).
4. Escalar o problema técnico.
5. Retomar somente após a confiança operacional ser restabelecida.

## 10. Fallback manual

Enquanto o piloto estiver pausado, o Airbnb continua sendo a fonte de verdade comercial e operacional da reserva — o operador continua atendendo o hóspede normalmente pelo Airbnb, sem depender de nenhuma automação do iHostPro, até a retomada ser autorizada.

## 11. Critério de conclusão do Stage 1

O Stage 1 não se conclui apenas porque um prazo passou. São exigidos, cumulativamente:
- `MinimumInitialObservationWindow = 48 horas` de observação ativa; **e**
- pelo menos um ciclo completo de reserva real (criação → estadia → check-out/encerramento) observado com sucesso.

## 12. Limite de expansão

Este piloto não deve ser expandido além de 1 owner + 1 property + 1 operador sem uma nova avaliação explícita. Não expandir "só porque está funcionando".

## 13. Seleção real do Stage 1 (pendente)

Estes três itens são decisões de negócio que este documento não define e que precisam ser confirmadas explicitamente antes da execução do cutover:
- `PilotOperator` — quem operará (existe um candidato já provisionado como admin do tenant, `rhay_liberato@hotmail.com`, com login funcional e permissão `ReservationsManage` já comprovada — mas a seleção como operador do Stage 1 exige confirmação explícita, não é assumida por este documento).
- `PilotOwner` — o owner real do Stage 1.
- `PilotProperty` — o imóvel real do Stage 1.

Nenhum dos três usa a fixture sintética `NON_CUSTOMER_DATA` (`IHostPro.HomologScenarioProvisioning`) como piloto real — aquela fixture existe apenas para fins de prova técnica de observabilidade, não é um candidato a Stage 1.
