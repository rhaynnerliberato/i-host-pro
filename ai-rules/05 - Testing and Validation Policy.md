\# Testing and Validation Policy



Version: 1.0



Status: Official



\---



\# 1. Purpose



This document defines how every code change in iHostPro shall be tested, validated and reported by the AI during development.



It does not replace the project QA strategy.



The project QA documentation defines the overall testing architecture and coverage expected for the platform.



This document defines the mandatory behavior of the AI whenever it creates, modifies or reviews code.



\---



\# 2. Core Principle



No implementation shall be considered complete without appropriate validation.



Validation must be proportional to:



\- the change performed;

\- the risk involved;

\- the affected behavior;

\- the possibility of regression.



The AI SHALL never claim that a change works without evidence.



\---



\# 3. Truthfulness



The AI SHALL clearly distinguish between:



\- tests actually executed;

\- tests reviewed but not executed;

\- tests recommended;

\- validations performed manually;

\- validations that could not be performed.



The AI SHALL never state that:



\- tests passed;

\- the build succeeded;

\- the application started;

\- a migration worked;

\- an integration responded correctly;

\- a command was executed;



unless this actually occurred.



\---



\# 4. Validation Before Implementation



Before modifying code, the AI SHALL identify:



\- the current behavior;

\- the expected behavior;

\- the affected modules;

\- the existing tests related to the change;

\- the possible regression points;

\- the external dependencies involved.



When the current behavior cannot be confirmed, the AI SHALL not assume it.



It shall inspect the relevant code, tests, documentation and available evidence before proceeding.



\---



\# 5. Test Scope



Tests shall cover only the behavior affected by the requested change and its relevant regression risks.



Do not modify unrelated tests.



Do not expand the test scope without technical necessity.



Do not create broad test suites merely to increase coverage metrics.



\---



\# 6. Creating Tests



New tests SHALL be created when the change introduces:



\- new behavior;

\- new business rules;

\- new validation;

\- new error handling;

\- new workflow transitions;

\- new integration behavior;

\- new permission behavior;

\- a bug fix that was not previously covered;

\- a regression risk not covered by existing tests.



A bug fix should normally include a test that fails before the correction and passes after it.



\---



\# 7. Updating Existing Tests



Existing tests SHALL be updated only when:



\- the approved behavior has intentionally changed;

\- a public contract has been officially modified;

\- a documented rule has changed;

\- the existing test is incorrect or obsolete;

\- implementation changes require equivalent test adjustments without weakening the assertion.



Do not update tests merely to make failing code pass.



\---



\# 8. Preserving Test Intent



Tests are executable documentation.



The AI SHALL preserve the intent of existing tests unless the underlying requirement has changed.



Never:



\- remove meaningful assertions;

\- weaken assertions;

\- ignore failing tests without investigation;

\- disable tests without explicit justification;

\- replace precise tests with superficial success checks;

\- increase timeouts merely to hide instability.



\---



\# 9. Minimum Scenarios



For every behavior modified, evaluate at least:



\- one successful scenario;

\- one invalid or failure scenario;

\- one relevant regression scenario.



Additional scenarios may be required for:



\- boundary conditions;

\- permissions;

\- tenant isolation;

\- concurrency;

\- idempotency;

\- retries;

\- integrations;

\- null or missing data;

\- invalid state transitions.



Only add scenarios that are relevant to the change.



\---



\# 10. Business Rules



Business rules SHALL be validated independently whenever technically possible.



Tests shall verify:



\- the rule itself;

\- allowed conditions;

\- denied conditions;

\- relevant exceptions;

\- configurable behavior;

\- tenant or property scope when applicable.



Avoid testing business rules only through the user interface.



\---



\# 11. Configurable Behavior



When behavior is configurable, tests SHALL avoid assuming a single fixed configuration.



Validate, when relevant:



\- default behavior;

\- enabled behavior;

\- disabled behavior;

\- overriding configuration;

\- configuration hierarchy;

\- invalid configuration.



Do not hardcode test expectations that contradict the configuration model.



\---



\# 12. Multi-Tenant Validation



Changes affecting data access, authorization or configuration SHALL verify tenant isolation.



Tests should confirm, when relevant:



\- one tenant cannot access another tenant's data;

\- configuration resolution respects tenant boundaries;

\- identifiers from another tenant are rejected;

\- events and background jobs retain tenant context;

\- cache keys and queries do not mix tenants.



Tenant isolation failures are critical defects.



\---



\# 13. State and Workflow Validation



Changes involving state machines or workflows SHALL validate:



\- valid transitions;

\- invalid transitions;

\- repeated execution;

\- idempotency;

\- generated events;

\- audit records;

\- failure handling;

\- retry behavior when applicable.



Do not validate only the final state if intermediate effects are relevant.



\---



\# 14. Integration Validation



External integrations shall not be required for the default automated test suite unless an approved test environment exists.



Prefer:



\- mocks;

\- fakes;

\- stubs;

\- emulators;

\- contract tests;

\- controlled sandbox environments.



Tests shall verify internal behavior independently from external service availability.



\---



\# 15. Database Validation



Changes involving persistence SHALL evaluate:



\- data integrity;

\- constraints;

\- transactions;

\- rollback behavior;

\- query correctness;

\- tenant filtering;

\- migration compatibility;

\- duplicated execution;

\- concurrency risks where relevant.



Tests must not depend on uncontrolled production data.



\---



\# 16. Migration Validation



Every database migration SHALL be reviewed and tested before production use.



Validate, when applicable:



\- upgrade from the previous version;

\- compatibility with existing data;

\- rollback or recovery strategy;

\- nullability;

\- default values;

\- index impact;

\- locking and execution time;

\- repeatability rules.



The AI SHALL not claim that a migration is safe without executing or reviewing the relevant validation.



\---



\# 17. Security Validation



Changes involving authentication, authorization, input, files, secrets or sensitive data SHALL include relevant security validation.



Examples:



\- unauthorized access;

\- privilege escalation;

\- tenant boundary violations;

\- malicious input;

\- unsafe file types;

\- secret exposure;

\- sensitive data in logs;

\- insecure direct object references.



Security tests shall be focused on the affected risk.



\---



\# 18. Performance Validation



Performance tests are required when a change may materially affect:



\- database queries;

\- list or search operations;

\- batch processing;

\- workflows;

\- message processing;

\- integrations;

\- file operations;

\- high-volume endpoints;

\- AI requests.



Do not perform premature performance testing for trivial changes.



When performance cannot be measured, clearly identify the risk and recommend the appropriate validation.



\---



\# 19. Frontend Validation



Frontend changes SHALL evaluate, when applicable:



\- rendering;

\- loading states;

\- empty states;

\- error states;

\- form validation;

\- accessibility;

\- responsive behavior;

\- permissions;

\- user feedback;

\- duplicated submission;

\- preservation of entered data;

\- keyboard navigation.



Avoid relying exclusively on visual inspection when automated tests are practical.



\---



\# 20. Commands and Tools



Before executing project commands, the AI SHALL inspect the repository instructions and available scripts.



Prefer project-defined commands over improvised commands.



Examples:



\- documented build scripts;

\- repository test scripts;

\- lint scripts;

\- migration commands;

\- formatter commands.



Do not introduce new tooling merely to validate a single task without justification.



\---



\# 21. Failed Tests



When a test fails, the AI SHALL determine whether the cause is:



\- the new implementation;

\- an existing defect;

\- incorrect test data;

\- environment configuration;

\- external dependency;

\- flaky behavior;

\- an outdated test.



Do not immediately change the test.



Do not ignore failures.



Report unresolved failures clearly.



\---



\# 22. Flaky Tests



Flaky tests SHALL be treated as defects.



Do not repeatedly execute a flaky test until it passes and then report success without qualification.



When instability is identified:



\- investigate the cause;

\- eliminate timing dependence where possible;

\- isolate shared state;

\- control asynchronous behavior;

\- document the unresolved risk if it cannot be fixed within scope.



\---



\# 23. Test Data



Tests SHALL use controlled and deterministic data.



Never use real personal or production data unless explicitly authorized and properly protected.



Test data shall:



\- be isolated;

\- be reproducible;

\- avoid hidden dependencies;

\- be cleaned when required;

\- not depend on execution order.



\---



\# 24. Coverage



Coverage metrics are indicators, not objectives by themselves.



The AI SHALL prioritize meaningful behavior coverage over line coverage.



Do not create low-value tests solely to increase percentages.



Critical business rules, workflows, permissions and tenant isolation deserve stronger coverage than trivial implementation details.



\---



\# 25. Validation When Tests Cannot Be Executed



When tests cannot be executed, the AI SHALL:



1\. explain why execution was not possible;

2\. identify which validations were performed instead;

3\. list the exact commands or tests that should be executed;

4\. describe the remaining risk;

5\. avoid declaring the task fully validated.



Lack of execution shall never be hidden.



\---



\# 26. Validation Report



At the end of a development task, report only relevant information.



The report should include:



\- tests executed;

\- result of each relevant command;

\- tests added or updated;

\- validations performed;

\- tests not executed;

\- known limitations or remaining risks.



Do not include empty sections or generic statements.



\---



\# 27. Scope Protection



Testing does not authorize unrelated changes.



Do not:



\- repair unrelated failing tests without approval;

\- refactor unrelated test infrastructure;

\- update unrelated snapshots;

\- reformat entire test suites;

\- replace test frameworks;

\- change global test configuration;



unless the requested task requires it and the impact is approved.



\---



\# 28. Completion Rule



A task may only be considered validated when:



\- the expected behavior was verified;

\- relevant regression scenarios were considered;

\- required tests were created or updated;

\- available tests were executed successfully;

\- non-executed validations were explicitly disclosed;

\- no known critical failure remains hidden.



\---



\# 29. Final Principle



Testing exists to produce evidence, not confidence based on assumption.



The AI SHALL never confuse:



\- code that appears correct;

\- code that compiles;

\- code that passed isolated tests;

\- code that is safe for production.



Each statement requires its own evidence.



When evidence is incomplete, state that clearly.

