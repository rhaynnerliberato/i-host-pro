\# Git and Change Management



Version: 1.0



Status: Official



\---



\# 1. Purpose



This document defines how source code changes shall be planned, implemented, reviewed, versioned and delivered in the iHostPro project.



Its objective is to reduce regression risk, preserve traceability and ensure that every change remains understandable, reviewable and reversible.



This document does not replace the project DevOps strategy.



It defines the mandatory behavior expected during day-to-day development and source control operations.



\---



\# 2. Core Principle



Every change shall be:



\- intentional;

\- scoped;

\- traceable;

\- reviewable;

\- testable;

\- reversible whenever practical.



Large, mixed or unclear changes shall be avoided.



\---



\# 3. Scope Isolation



Each change shall address one cohesive objective.



Do not combine unrelated items in the same implementation.



Avoid mixing:



\- feature development;

\- bug fixes;

\- refactoring;

\- dependency upgrades;

\- formatting changes;

\- documentation restructuring;

\- infrastructure changes;



unless they are directly required by the same approved task.



\---



\# 4. Minimal Change Strategy



Prefer the smallest correct change that satisfies the approved requirement.



Do not:



\- rewrite entire files when a focused modification is sufficient;

\- reorganize unrelated code;

\- rename unrelated symbols;

\- reformat unaffected areas;

\- change project-wide conventions during a local task;

\- replace working implementations solely by preference.



Minimal change does not justify poor quality.



The implementation must remain correct, maintainable and consistent.



\---



\# 5. Change Planning



Before modifying the repository, identify:



\- the requested outcome;

\- the affected files;

\- the affected modules;

\- the expected behavior;

\- the current behavior;

\- possible regression points;

\- required tests;

\- required documentation updates;

\- whether an ADR or approval is required.



When the scope is unclear, request clarification before implementing the affected point.



\---



\# 6. Working Tree Inspection



Before starting a change, inspect the repository state when tools are available.



Identify:



\- current branch;

\- uncommitted changes;

\- untracked files;

\- pending merge or rebase;

\- unrelated modifications already present;

\- repository instructions.



Never overwrite or discard existing user changes without explicit authorization.



\---



\# 7. Existing User Changes



Uncommitted changes may belong to the user or another task.



The AI SHALL:



\- preserve them;

\- avoid reverting them;

\- avoid replacing affected files wholesale;

\- distinguish its own changes from pre-existing changes;

\- report conflicts that prevent a safe implementation.



Do not use destructive commands to obtain a clean repository.



\---



\# 8. Branch Strategy



The repository branch strategy shall follow the process approved for the project.



The AI SHALL NOT invent or change the branching model.



Before creating or using branches, verify the repository documentation or ask the user when the strategy is undefined.



Branch names should be:



\- descriptive;

\- concise;

\- related to one task;

\- consistent with the established convention.



Examples may include:



```text

feature/property-registration

fix/cleaning-schedule-conflict

chore/update-test-tooling



These examples are not mandatory conventions unless approved for the project.



9\. Protected Branches



Do not commit directly to protected or production branches unless the approved workflow explicitly allows it.



Do not:



bypass review requirements;

bypass branch protection;

force-push protected branches;

disable required checks;

merge failed pipelines;

alter repository protections without authorization.

10\. Commit Cohesion



Each commit shall represent one coherent change.



A reviewer should be able to understand:



what changed;

why it changed;

which behavior is affected;

how it can be validated.



Avoid commits that contain unrelated modifications.



11\. Commit Size



Prefer small and reviewable commits.



A commit should not be artificially split when its parts cannot work independently.



A commit should also not combine multiple independent changes merely for convenience.



The correct size is the smallest cohesive unit that preserves repository consistency.



12\. Commit Messages



Commit messages shall describe intent rather than implementation noise.



Use the repository's established convention.



When no convention exists, prefer a clear imperative summary.



Examples:



Add property-level check-in policy resolution

Fix duplicate cleaning task creation

Update reservation cancellation documentation



Avoid vague messages such as:



Changes

Fix

Update files

Work in progress



Do not claim behavior in the commit message that was not implemented or validated.



13\. Conventional Commits



Do not introduce Conventional Commits as a project requirement unless it is approved or already established.



If the repository already uses it, follow the existing pattern consistently.



Examples:



feat: add property registration

fix: prevent duplicate cleaning schedules

docs: update workflow catalogue



Do not mix different commit message standards without reason.



14\. Commit Content



Before committing, verify that the change does not contain:



credentials;

tokens;

private keys;

personal data;

production data;

local machine paths;

editor-specific temporary files;

debug output;

temporary scripts;

build artifacts not intended for version control;

unrelated generated files.

15\. Secrets



Secrets SHALL NEVER be committed.



When a secret is found in the repository:



do not reproduce it in responses;

do not move it to another tracked file;

report the risk;

recommend rotation when exposure may have occurred;

use the approved secret-management mechanism.



Removing a secret from the latest file does not remove it from Git history.



History rewriting or credential rotation requires explicit coordination.



16\. Generated Files



Generated files shall follow repository rules.



Before modifying or committing generated content, determine:



whether the file is source-controlled;

which tool generates it;

whether manual editing is prohibited;

whether regeneration is required;

whether the tool version is fixed.



Do not manually edit generated files when the source definition should be changed instead.



17\. Dependency Files



When dependencies change, update only the required files.



Verify consistency between:



package manifests;

lock files;

project files;

dependency configuration;

container definitions;

build scripts.



Do not regenerate lock files unnecessarily.



Do not upgrade unrelated dependencies during a feature or bug-fix task.



18\. Database Changes



Database schema changes SHALL be versioned through the approved migration mechanism.



Do not apply undocumented manual schema changes.



A database change shall include, when applicable:



migration;

model update;

persistence update;

validation;

tests;

documentation;

rollback or recovery consideration.



Do not rewrite applied migrations unless the approved project process explicitly permits it and no environment depends on them.



19\. Public Contract Changes



Changes to public contracts require explicit impact analysis.



Examples:



APIs;

event schemas;

webhook payloads;

database contracts;

public interfaces;

configuration schemas;

integration messages.



Before implementation, determine:



whether the change is backward compatible;

which consumers are affected;

whether versioning is required;

whether a migration period is necessary;

whether documentation must be updated.



Breaking changes require explicit approval.



20\. Refactoring



Refactoring is allowed only when it is:



directly related to the requested task;

required to implement the change safely;

necessary to preserve or improve the quality of affected code;

behavior-preserving;

adequately validated.



Do not perform opportunistic refactoring in unrelated areas.



Broader refactoring shall be proposed as a separate task.



21\. Formatting Changes



Avoid repository-wide formatting during unrelated tasks.



Formatting should be limited to:



changed code;

files required by the approved formatter;

explicit formatting tasks.



Do not mix extensive formatting with behavioral changes when it would make review difficult.



22\. File Renaming and Movement



Rename or move files only when required by:



the approved change;

an approved architectural decision;

a necessary correction;

an established repository convention.



Before moving files, evaluate:



imports;

references;

build configuration;

scripts;

tests;

documentation;

case sensitivity across operating systems;

deployment impact.



Do not move files solely for aesthetic preference.



23\. Deleting Code



Do not delete code merely because it appears unused.



Before removal, verify:



static references;

dynamic loading;

reflection;

configuration references;

external consumers;

scheduled jobs;

migrations;

backward compatibility;

documentation;

deployment scripts.



When purpose cannot be confirmed, ask before deleting.



24\. Conflict Resolution



Merge conflicts SHALL be resolved by understanding both changes.



Do not automatically choose:



current version;

incoming version;

larger block;

newer-looking code.



Review:



intent;

documentation;

tests;

affected behavior;

related commits.



When both changes represent valid but incompatible decisions, request clarification.



25\. Rebase and History Rewriting



Do not rewrite shared history without explicit authorization.



Actions requiring special caution include:



force push;

interactive rebase;

commit amendment after publication;

history filtering;

squashing shared commits;

deleting remote branches.



Prefer safe, non-destructive operations.



26\. Destructive Commands



The AI SHALL NOT execute destructive Git or file-system commands without explicit authorization and clear impact assessment.



Examples include:



git reset --hard

git clean -fd

git checkout -- .

git restore .

git push --force

git branch -D

rm -rf



Before any authorized destructive operation:



explain what will be removed or rewritten;

identify whether recovery is possible;

confirm the target;

obtain explicit approval.

27\. Stashing



Do not stash user changes automatically unless necessary and authorized.



Stashes can hide work and create confusion.



When stashing is required:



explain why;

use a descriptive message;

preserve the stash until restoration is confirmed;

report the final state.

28\. Pull Requests



A pull request should contain one cohesive change.



Its description should include only applicable information:



objective;

implementation summary;

affected behavior;

tests executed;

documentation updated;

migration or deployment notes;

known limitations;

screenshots for relevant UI changes.



Do not write generic or exaggerated claims.



29\. Reviewability



Prepare every change for efficient review.



A reviewer should not need to separate:



unrelated formatting;

hidden refactoring;

generated noise;

multiple independent features;

undocumented design changes.



When a diff becomes difficult to review, reduce or separate the scope.



30\. Review Feedback



Treat review feedback as a technical input, not an automatic command.



Before applying feedback, verify that it:



respects requirements;

respects architecture;

does not introduce regression;

does not conflict with approved decisions;

remains within scope.



When feedback conflicts with project rules, report the conflict instead of applying it silently.



31\. CI Validation



Before merge, required CI checks shall pass according to the approved workflow.



Do not:



disable checks;

bypass pipelines;

remove tests to obtain success;

reduce quality thresholds without approval;

ignore failed steps.



When CI fails, investigate and report the actual cause.



32\. Documentation Changes



Documentation changes shall be committed with the implementation they describe whenever practical.



Do not leave affected documentation for an unspecified future task.



Documentation-only commits are appropriate when:



correcting existing documentation;

creating an approved ADR;

updating process rules;

producing an explicitly requested document.



Follow 04 - Documentation Policy.md.



33\. ADR Changes



Approved architectural decisions shall generate a new ADR.



Do not rewrite the historical outcome of an accepted ADR.



When a decision changes:



create a new ADR;

reference the previous ADR;

mark the previous decision as superseded when appropriate;

explain migration consequences.



Draft ADRs may use a proposed status until approval.



34\. Tags and Releases



Do not create tags or releases without explicit authorization or an established automated process.



Release identifiers shall follow the approved versioning strategy.



Before releasing, verify:



required checks;

migration readiness;

documentation;

deployment notes;

rollback plan;

known limitations.

35\. Versioning



Do not choose or change the project's versioning strategy without approval.



When semantic versioning is adopted:



patch represents backward-compatible corrections;

minor represents backward-compatible functionality;

major represents breaking changes.



Do not apply these rules unless semantic versioning is the approved strategy.



36\. Rollback Awareness



Every change shall consider how it can be reversed.



Before delivery, evaluate:



code rollback;

migration compatibility;

event compatibility;

configuration rollback;

external side effects;

data already transformed;

messages already sent;

integrations already triggered.



A code rollback may not reverse business side effects.



Irreversible changes require explicit risk communication.



37\. Change Traceability



Significant changes should be traceable to their origin when the repository process supports it.



Possible references include:



issue;

task;

user request;

ADR;

incident;

pull request.



Do not invent identifiers that do not exist.



38\. Change Report



At the end of a task, report concisely:



files or areas changed;

behavior added or corrected;

unrelated behavior preserved;

tests and commands executed;

documentation updated;

repository actions performed;

known limitations or pending actions.



Do not claim that a commit, push, pull request or merge occurred unless it actually occurred.



39\. AI Authorization Boundaries



The AI MAY autonomously:



inspect repository status;

inspect diffs;

create or modify files within the approved task;

execute non-destructive validation commands;

propose commit messages;

prepare a change for review.



The AI SHALL require explicit authorization before:



committing, when the user has not requested a commit;

pushing;

creating or merging pull requests;

deleting branches;

force-pushing;

rewriting history;

discarding changes;

creating releases or tags;

bypassing repository protections.



When the user's request explicitly includes one of these actions, no additional confirmation is required unless the target or impact remains ambiguous.



40\. Final Checklist



Before presenting a change as ready, verify:



Repository state

&#x20;Pre-existing user changes were preserved.

&#x20;No unrelated file was modified.

&#x20;No secret or local artifact was introduced.

&#x20;Generated files follow repository rules.

Change quality

&#x20;The diff is cohesive and reviewable.

&#x20;Scope is limited to the approved task.

&#x20;Public contracts were preserved or explicitly approved.

&#x20;Refactoring is directly related and justified.

&#x20;Database and dependency changes are complete when applicable.

Validation

&#x20;Relevant tests and checks were executed when possible.

&#x20;Failures were investigated.

&#x20;Results were reported truthfully.

&#x20;Documentation was updated when affected.

Delivery

&#x20;No destructive operation was performed without approval.

&#x20;No commit, push, merge, tag or release was claimed without execution.

&#x20;Remaining risks and pending actions were disclosed.

41\. Final Principle



Source control is not only a storage mechanism.



It is the historical record of the project's evolution.



Every change shall make that history clearer, safer and easier to understand.



Never sacrifice traceability or user work for speed or convenience.





Com esse arquivo, os quatro documentos restantes da pasta `ai-rules` estão completos:



```text

04 - Documentation Policy.md

05 - Testing and Validation Policy.md

06 - Definition of Done.md

07 - Git and Change Management.md

