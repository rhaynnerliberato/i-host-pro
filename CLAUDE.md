\# iHostPro — Instruções obrigatórias para o Claude Code



Antes de realizar qualquer tarefa neste workspace, leia obrigatoriamente:



`/ai-rules/00 - Engineering Constitution.md`



Em seguida, leia os demais documentos aplicáveis existentes no diretório:



`/ai-rules`



Não é necessário ler todos os arquivos da pasta `ai-rules` em todas as tarefas. Leia obrigatoriamente os documentos relacionados ao tipo de atividade que será executada.



Antes de alterar código, consulte também a documentação relevante existente em:



`/documentacao do projeto`



Leia os documentos relacionados ao módulo, requisito, regra de negócio, integração, arquitetura, segurança, testes ou operação afetados pela tarefa.



\## Regras obrigatórias



\- Siga todas as regras definidas em `/ai-rules`.

\- Respeite os ADRs aprovados existentes em `/documentacao do projeto/ADRs`.

\- Nunca implemente funcionalidades assumindo requisitos ausentes.

\- Nunca invente regras de negócio, fluxos, permissões, comportamentos, integrações, dados ou configurações.

\- Nunca altere o escopo solicitado silenciosamente.

\- Nunca altere contratos públicos, arquitetura, banco de dados, dependências, APIs, schemas ou regras de negócio sem autorização quando a decisão exigir aprovação.

\- Preserve todo comportamento existente que não faça parte da solicitação.

\- Priorize alterações pequenas, isoladas, explícitas e de baixo risco.

\- Não faça refatorações oportunistas ou alterações em áreas não relacionadas à tarefa.

\- Nunca afirme que comandos, testes, builds, migrações ou validações foram executados quando isso não ocorreu.

\- Nunca esconda dúvidas, incertezas, limitações ou riscos relevantes.



\## Informações insuficientes ou contraditórias



Caso a documentação, o código ou a solicitação sejam insuficientes, ambíguos ou contraditórios:



1\. interrompa a implementação somente no ponto afetado;

2\. identifique objetivamente a dúvida ou o conflito;

3\. informe quais decisões dependem dessa informação;

4\. solicite esclarecimento antes de continuar.



Não escolha silenciosamente uma interpretação.



Caso duas instruções entrem em conflito e a prioridade não esteja expressamente definida, informe o conflito e solicite orientação.



\## Documentação



Toda alteração deverá manter sincronizados:



\- código;

\- testes;

\- documentação diretamente afetada;

\- ADRs, quando houver decisão arquitetural relevante aprovada.



Quando uma documentação necessária ainda não existir, crie-a conforme o projeto evoluir.



Quando uma documentação existente for afetada, atualize-a na mesma tarefa.



Não crie documentação:



\- redundante;

\- especulativa;

\- sem utilidade prática;

\- que repita conteúdo já definido em outro documento;

\- sobre funcionalidades ou decisões ainda não aprovadas.



\## Decisões arquiteturais



Quando uma decisão arquitetural relevante ainda não estiver definida:



1\. analise as alternativas;

2\. apresente vantagens, desvantagens, riscos e impactos;

3\. recomende uma opção;

4\. aguarde aprovação antes de implementar;

5\. após aprovação, registre a decisão em um ADR.



\## Validação final



Antes de considerar uma tarefa concluída, confirme que:



\- o escopo solicitado foi atendido;

\- nenhum comportamento fora do escopo foi alterado;

\- as regras do projeto foram respeitadas;

\- os testes necessários foram executados ou claramente informados como não executados;

\- a documentação afetada foi atualizada;

\- não foram introduzidas suposições não aprovadas.



Nunca ignore as regras de engenharia deste projeto.

