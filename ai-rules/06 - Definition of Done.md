\# Definition of Done



Version: 1.0



Status: Official



\---



\# 1. Purpose



This document defines the mandatory criteria that a task must satisfy before it can be considered complete in the iHostPro project.



The Definition of Done applies to:



\- new features;

\- bug fixes;

\- refactorings;

\- integrations;

\- database changes;

\- infrastructure changes;

\- documentation changes;

\- security corrections;

\- performance improvements.



A task SHALL NOT be declared complete merely because code was written.



\---



\# 2. Core Principle



A task is done only when the requested outcome has been implemented, validated, documented when necessary and delivered without hidden risks or unresolved critical issues.



“Implemented” and “completed” are not synonyms.



\---



\# 3. Scope Compliance



Before completion, verify that:



\- the approved scope was fully implemented;

\- no unrequested functionality was added;

\- no approved requirement was omitted;

\- no unrelated behavior was changed;

\- no unauthorized architectural decision was introduced;

\- no business rule was inferred or invented.



Any unresolved ambiguity prevents completion of the affected part.



\---



\# 4. Requirement Compliance



The implementation SHALL conform to:



\- the explicit user request;

\- approved ADRs;

\- relevant project documentation;

\- applicable `ai-rules`;

\- existing public contracts;

\- established project conventions.



If the implementation conflicts with any approved source of truth, the task is not complete.



\---



\# 5. Functional Correctness



The expected behavior SHALL be verified.



At minimum, confirm:



\- the main success scenario;

\- relevant failure scenarios;

\- relevant edge cases;

\- regression risks directly related to the change.



The validation must be supported by evidence, not assumption.



\---



\# 6. Code Quality



The affected code SHALL be:



\- readable;

\- cohesive;

\- maintainable;

\- testable;

\- consistent with the project;

\- free from unnecessary duplication;

\- free from avoidable complexity;

\- limited to the requested scope.



Do not use task completion as justification for unrelated cleanup.



\---



\# 7. Architecture Compliance



The implementation SHALL respect the architecture already approved for the project.



Verify that:



\- responsibilities remain in the correct layer or module;

\- business logic is not moved into infrastructure or presentation;

\- dependencies follow approved boundaries;

\- module contracts remain explicit;

\- no prohibited coupling was introduced;

\- configuration is used where required by the project documentation.



Architectural changes requiring approval must be documented through an ADR before implementation.



\---



\# 8. Business Rules



When a task affects business behavior, confirm that:



\- the rule was explicitly approved;

\- the rule was implemented in the appropriate domain location;

\- configurable behavior was not hardcoded;

\- relevant exceptions were covered;

\- the rule is independently testable;

\- the documentation remains accurate.



\---



\# 9. Public Contracts



When the change affects any public contract, verify:



\- API compatibility;

\- request and response schemas;

\- events;

\- commands;

\- database contracts;

\- integration payloads;

\- exported interfaces;

\- configuration contracts.



Breaking changes require explicit approval, impact assessment and migration planning.



\---



\# 10. Error Handling



The affected flow SHALL handle relevant failures correctly.



Verify that:



\- errors are not silently ignored;

\- error messages are meaningful;

\- sensitive details are not exposed;

\- partial failure does not leave invalid state;

\- retries do not duplicate effects;

\- recoverable failures remain recoverable;

\- unrecoverable failures are observable.



\---



\# 11. Security



For changes involving sensitive operations, confirm that:



\- authentication remains enforced;

\- authorization remains correct;

\- tenant isolation is preserved;

\- inputs are validated;

\- secrets are not exposed;

\- logs do not contain sensitive data;

\- file and data access remain restricted;

\- no existing control was weakened.



Any known critical security issue prevents task completion.



\---



\# 12. Data Integrity



For changes involving persistence, confirm that:



\- data remains consistent;

\- transactions are appropriate;

\- constraints are respected;

\- tenant context is preserved;

\- duplicate execution is handled where relevant;

\- migrations are compatible with existing data;

\- rollback or recovery was considered.



\---



\# 13. Concurrency and Idempotency



When the affected flow may execute concurrently or repeatedly, verify:



\- duplicate requests do not create duplicate effects;

\- race conditions were considered;

\- state transitions remain valid;

\- retries are safe;

\- background processing preserves consistency;

\- concurrency controls are proportional to the risk.



\---



\# 14. Performance



When the change may affect performance, confirm that:



\- no obvious unnecessary queries were introduced;

\- no avoidable repeated processing exists;

\- pagination is used where necessary;

\- large collections are not loaded unnecessarily;

\- blocking operations are avoided when inappropriate;

\- cache behavior remains correct;

\- relevant performance risks were measured or documented.



Trivial changes do not require unnecessary performance work.



\---



\# 15. Observability



Relevant operational behavior SHALL be observable.



When applicable, verify:



\- meaningful logs exist;

\- errors include useful context;

\- correlation identifiers are preserved;

\- metrics are updated;

\- tracing remains intact;

\- audit records are generated;

\- sensitive information is excluded.



Avoid logs without operational value.



\---



\# 16. Tests



Before completion, verify that:



\- required tests were created or updated;

\- existing relevant tests remain intact;

\- tests were executed when execution was possible;

\- test results were reported truthfully;

\- failures were investigated;

\- no assertion was weakened merely to achieve success;

\- regression coverage is proportional to the risk.



Follow `05 - Testing and Validation Policy.md`.



\---



\# 17. Build and Static Validation



When applicable, execute the project-defined commands for:



\- build;

\- compilation;

\- type checking;

\- lint;

\- formatting validation;

\- static analysis;

\- dependency validation.



Do not claim success for commands that were not executed.



\---



\# 18. Documentation



Before completion, verify whether the change affects documentation.



When affected:



\- update the authoritative document;

\- avoid creating duplicate documentation;

\- update cross-references when necessary;

\- update version or status information when applicable;

\- create an ADR for approved architectural decisions;

\- remove or correct obsolete information.



Follow `04 - Documentation Policy.md`.



\---



\# 19. Database Migrations



A task involving database changes is complete only when:



\- the migration is versioned;

\- upgrade behavior was reviewed;

\- compatibility with existing data was considered;

\- constraints and defaults are correct;

\- index impact was evaluated;

\- rollback or recovery strategy is known;

\- affected documentation is updated;

\- relevant tests or validations were performed.



\---



\# 20. External Integrations



A task involving an external integration is complete only when:



\- the connector or adapter respects the approved architecture;

\- authentication and credentials are handled securely;

\- timeout behavior is defined;

\- retries are safe;

\- idempotency was considered;

\- external errors are translated appropriately;

\- observability is available;

\- test doubles or sandbox validation exist when applicable;

\- no undocumented external assumption remains.



\---



\# 21. Frontend Changes



A frontend task is complete only when relevant items have been verified:



\- main flow;

\- loading state;

\- empty state;

\- failure state;

\- validation messages;

\- permission behavior;

\- responsive behavior;

\- accessibility;

\- keyboard usage;

\- duplicate submission prevention;

\- preservation of user input;

\- consistency with the design system.



Only evaluate items applicable to the change.



\---



\# 22. Infrastructure and Configuration



A task involving infrastructure or configuration is complete only when:



\- changes are reproducible;

\- environment-specific values remain outside source code;

\- secrets are protected;

\- default values are safe;

\- rollback is possible;

\- monitoring is updated when necessary;

\- operational documentation is updated;

\- no manual undocumented step is required.



\---



\# 23. Dependency Changes



When adding, removing or updating a dependency, verify:



\- the change is necessary;

\- compatibility is confirmed;

\- security risk is acceptable;

\- license is compatible;

\- maintenance status is acceptable;

\- build and tests remain valid;

\- lock files are updated consistently;

\- unused dependencies are not introduced;

\- the impact is documented when relevant.



\---



\# 24. Code Review Readiness



Before declaring completion, review the final diff.



Confirm that:



\- every changed file is necessary;

\- no unrelated formatting change exists;

\- no debug code remains;

\- no commented-out code remains;

\- no temporary workaround remains hidden;

\- no secret or local path was committed;

\- naming is consistent;

\- comments remain accurate;

\- generated files are handled according to repository rules.



\---



\# 25. Known Limitations



Any known limitation SHALL be reported before completion.



Examples:



\- tests not executed;

\- unavailable external environment;

\- pending business decision;

\- unresolved non-critical risk;

\- partial browser coverage;

\- migration not tested with production-scale data.



Limitations shall never be hidden.



A task with an unresolved critical limitation is not complete.



\---



\# 26. Blocked Tasks



When completion is blocked, report:



\- what was completed;

\- what remains;

\- why it is blocked;

\- which information or access is required;

\- the risk of continuing without resolution.



Do not mark a blocked task as done.



\---



\# 27. Completion Report



At the end of each task, provide a concise and factual report containing only applicable items:



\- what was changed;

\- what behavior was preserved;

\- tests and commands executed;

\- documentation updated;

\- known limitations;

\- remaining risks.



Do not include generic claims such as “everything is working correctly” without evidence.



\---



\# 28. Definition of Done Checklist



Use this checklist internally before concluding a task:



\## Scope



\- \[ ] The requested scope was fully addressed.

\- \[ ] No unrelated changes were introduced.

\- \[ ] No requirement was invented.

\- \[ ] No required approval was bypassed.



\## Implementation



\- \[ ] The implementation follows project conventions.

\- \[ ] Business logic is placed correctly.

\- \[ ] Existing behavior outside the scope was preserved.

\- \[ ] Error and edge cases were handled.

\- \[ ] No temporary or debug code remains.



\## Quality



\- \[ ] The code is readable and maintainable.

\- \[ ] Duplication and unnecessary complexity were avoided.

\- \[ ] Security risks were considered.

\- \[ ] Performance risks were considered.

\- \[ ] Tenant isolation and data integrity were preserved when applicable.



\## Validation



\- \[ ] Relevant tests were added or updated.

\- \[ ] Available tests and validation commands were executed.

\- \[ ] Results were reported truthfully.

\- \[ ] Relevant regression scenarios were considered.

\- \[ ] Known limitations were disclosed.



\## Documentation



\- \[ ] Relevant documentation was reviewed.

\- \[ ] Affected documentation was updated.

\- \[ ] No redundant document was created.

\- \[ ] ADRs were created or updated when applicable.



\---



\# 29. Final Rule



A task is done only when another senior engineer can review the result and understand:



\- what changed;

\- why it changed;

\- how it was validated;

\- what was preserved;

\- which risks remain.



Code delivery without validation, traceability and accurate documentation is incomplete.

