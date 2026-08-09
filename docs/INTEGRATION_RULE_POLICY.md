# Integration Rule Policy

This document records the agreed Azure DevOps integration policy for QAF-OnPrem. It is a planning and implementation policy document. It does not describe the current code as fully complete; it captures the target behavior that implementation should follow.

Azure integration design and implementation must align with how Azure DevOps actually works, what its APIs accept, and the patterns Microsoft documents and recommends. QAF-OnPrem should adapt to Azure DevOps domain rules rather than forcing Azure DevOps data into a local model that conflicts with Azure expectations.

## Scope

This document currently covers:

1. Azure connection ownership
2. Azure routing ownership
3. Test case sync rules
4. Ready-state gating
5. Missing-routing behavior
6. Critical implementation constraints
7. Overall integration direction
8. Future entity policy sections for phased agreement

## Overall integration direction

The long-term direction is full Azure-aligned synchronization across the major QAF-OnPrem entities that map to Azure DevOps concepts or Azure-supported related artifacts.

The intended scope includes:

1. Test cases
2. Test plans
3. Test suites
4. Test points/configurations where Azure expects them
5. Test runs
6. Test results
7. Defects/bugs
8. Comments where Azure supports them on the relevant entity
9. Step narratives, expected/actual content, and related execution detail where Azure supports them
10. Screenshots, files, and other evidence attachments where Azure supports them

The goal is not blind field mirroring. The goal is an Azure-compatible, supportable, and predictable sync model that respects Azure entity boundaries, routing rules, lifecycle rules, and API constraints.

## Azure alignment and compliance principle

All Azure integration rules should be evaluated against these principles:

1. Use Azure DevOps entities the way Azure defines them
2. Use Azure APIs for their intended artifact type instead of forcing one artifact into another
3. Follow Azure-required field shapes, workflow rules, and attachment/linking patterns
4. Preserve Azure work item classification semantics separately from test-management membership semantics
5. Prefer explicit routing and explicit relationships over hidden fallback behavior
6. Avoid local rules that would create invalid, misleading, or non-replayable Azure state

When QAF-OnPrem and Azure concepts do not match one-to-one, QAF-OnPrem should use a clearly documented translation rule rather than an implicit shortcut.

## Azure topology assumption

The agreed Azure DevOps structure is:

1. One Azure DevOps organization, for example `maximhealthcare`
2. One shared Azure DevOps project inside that organization, for example `Maxim`
3. Many Azure area paths and iteration paths under that shared Azure DevOps project

This means QAF-OnPrem should not assume one Azure DevOps project per QAF-OnPrem project.

## Ownership model

### Azure connection

The Azure connection is shared across all QAF-OnPrem projects that use Azure DevOps.

The Azure connection owns only shared Azure system information:

1. Azure organization
2. Azure DevOps project
3. Provider enablement and sync flags

The Azure connection must not store web-app-specific or business-project-specific routing data.

### User settings

User settings remain the source of:

1. Azure PAT

PAT stays user-owned and is not moved back into shared connection credentials.

### QAF-OnPrem project

The QAF-OnPrem project owns the stable Azure ownership routing for tests that belong to that web/app project.

The QAF-OnPrem project should store:

1. Azure area path

This value is treated as static for the QAF-OnPrem project unless the project ownership structure changes.

### Test case

The test case owns the Azure iteration path used before or outside of explicit plan-level execution context.

The test case should store:

1. Azure iteration path

This is stored at test level because iteration changes over time and is not stable enough to treat as static project configuration.

### Test plan

The test plan owns its own Azure test-management context, but plan assignment must not be treated as proof that Azure work item iteration must always be rewritten.

The test plan may store routing values needed for plan-level operations, but Azure test plan membership and Azure work item classification are separate concepts.

## Test case source of truth

Azure test case sync must use the final assembled test case as truth.

That means:

1. The Preview-equivalent assembled output is the business truth for sync
2. Components, datasets, overrides, and variable resolution are authoring inputs
3. Azure should receive the assembled executable/manual test case output, not the raw authoring structure alone

### Implemented test case assembly behavior

The current implementation now follows this rule more closely.

For test case sync payload generation:

1. Azure payload assembly no longer uses a reduced first-dataset-only SQL projection as the source of truth
2. The sync payload now walks active test components in order
3. It includes active datasets for those components rather than assuming only one dataset matters
4. It applies persisted override narrative values such as step description and expected output when present
5. It resolves variable tokens using the same backend variable generation and substitution rules used for runnable manual test preparation
6. The resulting `manual_steps` payload is intended to mirror the final assembled business truth more closely before Azure fields such as steps, parameters, and local data source XML are built

This is the critical implementation correction that moved Azure testcase sync toward preview/manual-execution-equivalent truth.

### Remaining interpretation rule

The governing rule remains:

1. Authoring structure is local composition input
2. Final assembled step output is the sync contract
3. Any future changes to preview or manual execution assembly should be evaluated against the Azure testcase payload builder so the two do not drift apart again

## Test case sync gate

The sync gate remains:

1. Local `Ready` state triggers Azure create/update eligibility
2. Local non-`Ready` states do not create a new Azure test case

If a test case was already synced while `Ready` and later changes to another mapped state:

1. Update the existing Azure work item state
2. Do not delete the Azure work item

## Routing policy for test cases

For Azure-managed tests:

1. Azure DevOps project comes from the shared Azure connection
2. Azure area path comes from the owning QAF-OnPrem project
3. Azure iteration path comes from the test case itself

This applies to the unassigned/default test case routing policy.

### Implemented ownership changes in the app

The app has now been adjusted to match this routing policy.

1. Azure area path is exposed and persisted from the QAF-OnPrem project page rather than being hidden behind conditional Azure UI state
2. Azure iteration path is exposed and persisted on the testcase editor so testcase-level routing can be authored directly where the test is maintained
3. Azure routing fields were removed from the shared integration connection form because they do not belong to the connection ownership model
4. Connection save behavior now clears legacy project-scoped connection routing so the shared connection remains generic
5. The connection UI now reflects shared scope instead of encouraging one connection per QAF-OnPrem project

## Missing iteration policy

If all of the following are true:

1. The owning project uses Azure as test management
2. The test case is being saved in `Ready` state
3. Azure iteration path is missing on the test case

Then the correct behavior is:

1. Allow local save
2. Show a warning that the test will not sync to Azure until iteration path is provided
3. Do not queue Azure sync for that test case yet

The system should not silently sync a `Ready` Azure-managed test case without iteration path.

### Implemented save and queue behavior

This behavior is now reflected in the current application flow.

1. A testcase can still be saved locally without Azure iteration path
2. If the owning project is Azure-managed and the testcase is moved into `Ready` without required routing, Azure sync is suppressed instead of silently queued
3. The local authoring experience remains available while Azure sync readiness is enforced separately

## Area path policy

Azure area path is considered stable enough to live at QAF-OnPrem project level.

Implications:

1. All tests under the same QAF-OnPrem project share the same Azure area path by default
2. This represents ownership/team/application association inside Azure
3. This is not expected to vary test by test under normal operation

## Iteration path policy

Azure iteration path is not treated as static project configuration.

Implications:

1. Iteration can change every sprint
2. It should not be modeled as a fixed project-level value by default
3. It should be carried by the test case for pre-plan or default work item placement when Azure management is enabled

## Warning and validation behavior

Implementation should distinguish local authoring validity from Azure sync readiness.

### Local validity

The user should still be able to save a test locally even if Azure routing data is incomplete.

### Azure sync readiness

Azure sync readiness requires the routing values needed by policy.

For the agreed test case model, Azure sync readiness requires:

1. Shared Azure connection available
2. Azure organization configured
3. Azure DevOps project configured
4. QAF-OnPrem project Azure area path configured
5. Test case Azure iteration path configured when the test is `Ready`

### Implemented UI accommodations

To support this validation model, the UI was cleaned up in the following ways:

1. Project create and edit now consistently show Azure area path so the owning team routing field is not hidden from the user
2. The testcase editor now includes Azure iteration path in the field panel and save payload so testcase-level routing is visible and editable with the rest of testcase metadata
3. The integrations page no longer shows QAF-OnPrem web-app project names as if the Azure connection were scoped per app project
4. The integrations page no longer asks the user to assign project-level routing values inside the shared connection modal
5. The connection list now distinguishes shared connections from legacy scoped rows so the UI matches the agreed architecture

## Explicit non-goals

This policy does not assume:

1. One Azure DevOps project per QAF-OnPrem project
2. Connection-level storage of web-app-specific routing data
3. Automatic deletion of Azure test cases when local state changes away from `Ready`
4. Forced rewriting of Azure work item iteration path simply because a test case is assigned to a plan

This policy also does not assume:

5. That every QAF-OnPrem field must be duplicated in Azure if Azure has no proper home for it
6. That Azure comments, attachments, bugs, runs, and work items all follow the same API pattern
7. That local convenience should override Azure validation or lifecycle requirements

## Critical implementation notes

Implementation should preserve these principles:

1. Keep the integration connection generic and shared
2. Keep PAT user-owned
3. Do not rely on implicit Azure defaults when required routing data is missing
4. Warn clearly when a test is locally valid but not Azure-sync-ready
5. Do not mix Azure test plan membership rules with Azure work item classification rules

### Additional implementation notes from completed work

1. Testcase routing ownership is now split intentionally across shared connection, QAF-OnPrem project, and testcase records rather than being concentrated in the connection
2. The backend auto-sync gate checks Azure readiness before queueing testcase sync work
3. The testcase payload builder must continue to assemble Azure work item steps from the same business truth used by preview/manual execution semantics rather than from a convenience SQL shortcut

## Agreed Azure-native implementation progression

The agreed implementation progression must follow Azure DevOps artifact boundaries and lifecycle expectations rather than local convenience ordering.

The implementation order is:

1. Shared Azure connection and routing foundation
2. Test case work item synchronization
3. Azure test-management structure synchronization:
	1. Test plan creation and update
	2. Suite creation and update
	3. Testcase assignment into plans and suites
	4. Configuration and test point alignment
4. Azure execution synchronization:
	1. Test runs
	2. Test results
5. Defect creation and execution-aware defect linking
6. Comments, narrative, screenshots, files, and other evidence on the correct Azure artifact
7. Optional status or reporting rollups only where Azure has a true native concept for that state

This ordering is intentional.

Azure execution should not be treated as complete until the testcase exists in the correct Azure test-management context. In practice that means the testcase work item must already exist, the Azure test plan must exist, the relevant suite must exist, the testcase must be a member of that suite, and the configuration/test point model must be valid for the intended execution context.

## Current gap assessment for the next phase

The current implementation is ahead on testcase work item sync and partially ahead on test-management sync, but it is not yet complete enough to treat Azure execution as fully modeled.

The current gap assessment is:

1. Test plan sync exists, but the Azure test plan payload is still relatively shallow compared to the full local QAF-OnPrem plan model
2. Azure suite creation and testcase suite membership sync exist, but they are still the first version of the relationship model rather than the final Azure-aligned model
3. The most important remaining gap is configuration and test point alignment
4. Current assignment logic still relies too heavily on testcase-level configuration assumptions in places where Azure expects point-based plan-plus-suite-plus-case-plus-configuration behavior
5. Current run sync depends on test plan and testcase links, but it should not be treated as the next primary implementation slice until suite membership and point/configuration semantics are trustworthy
6. Comments, attachments, expected/actual narratives, screenshots, videos, and execution evidence should not be finalized until the owning Azure artifact boundary is settled by the run/result and bug-link model

The main practical conclusion is:

1. The next implementation phase should focus on test plan, suite, testcase assignment, configuration, and test point correctness
2. Test runs come after that foundation is hardened
3. Defects come after runs/results are modeled cleanly enough to support Azure-native linking
4. Evidence and comments come after the owning Azure artifact is explicitly defined

## Agreed implementation approach

The implementation approach for the remaining Azure work must follow these rules.

### Artifact-boundary rule

QAF-OnPrem must synchronize each concern to the Azure artifact that Azure expects.

1. Work item concerns go to Azure work items
2. Test-management membership concerns go to Azure plans, suites, cases, and points
3. Execution concerns go to Azure runs and results
4. Bug concerns go to Azure bug work items and Azure-supported execution links
5. Evidence and comments go only to Azure surfaces that properly own them

### Foundation-before-execution rule

Execution work must not outrun test-management structure.

1. Do not treat run creation as the next primary milestone if testcase membership and point/configuration behavior are still ambiguous
2. Do not model defects as execution-linked Azure artifacts before the run/result model is trustworthy
3. Do not model evidence placement before the owning Azure execution or bug artifact is agreed

### Point-based execution rule

Where Azure expects point-based execution semantics, QAF-OnPrem must use point-based semantics rather than shortcut testcase-only semantics.

Implications:

1. Suite membership should be evaluated together with configuration assignment
2. Duplicate prevention should consider testcase-plus-suite-plus-configuration behavior rather than treating all membership as a single flat case link
3. Reconciliation should converge on Azure's expected point state rather than merely detecting that a testcase exists somewhere in the suite

### Explicit gap handling rule

If Azure does not provide a correct home for a local concept, the system should document the limitation explicitly instead of forcing a misleading mapping.

This applies especially to:

1. Plan status semantics
2. Local-only authoring narratives with no true Azure equivalent
3. Convenience rollups that do not correspond to Azure-native state

## Current agreed field ownership summary

### Connection

1. Azure organization
2. Azure DevOps project

### QAF-OnPrem project

1. Azure area path

### Test case

1. Azure iteration path

### User settings

1. Azure PAT

## Duplication, identity, and de-duplication policy

Sync design must avoid uncontrolled duplication across Azure artifacts.

Core rules:

1. Each Azure-managed QAF-OnPrem entity should have a durable identity mapping to its Azure counterpart when Azure has accepted and created that counterpart
2. Sync should prefer idempotent update behavior when a mapped Azure artifact already exists
3. Create operations should be guarded so retries do not silently create duplicate Azure artifacts
4. Relationship sync should check existing Azure links, memberships, and assignments before adding new ones
5. Attachment sync should distinguish new evidence from already-synced evidence when durable identifiers or stable fingerprints are available
6. Comment sync should avoid reposting the same logical comment during retries or replay unless the business rule explicitly allows duplicates
7. De-duplication rules must be entity-specific because Azure work items, plans, suites, runs, results, comments, and attachments do not all expose the same identity model

The target outcome is convergent sync behavior: repeated processing of the same stable QAF-OnPrem state should converge on the same Azure state instead of drifting into duplicates.

## Entity policy sections for the next implementation phases

The sections below record the agreed direction for the remaining phases. They are still subject to detailed implementation refinement, but they are no longer purely open-ended.

### Test plan and suite policy direction

Test plans and suites should be synchronized in a way that matches Azure test-management structure, not work item structure.

Direction:

1. Test plan rules should align to Azure test plan APIs and lifecycle rules
2. Suite rules should align to Azure suite hierarchy and membership behavior
3. Test case classification fields such as area path and iteration path must not be conflated with suite membership
4. Plan and suite sync must include duplicate prevention and relationship reconciliation
5. Local QAF-OnPrem plan records should map to Azure test plans only through Azure test-plan concepts rather than through work-item shortcuts
6. Local QAF-OnPrem plan items should map to Azure suites only through Azure suite concepts rather than by reusing unrelated work-item semantics

### Test point and configuration policy direction

Test point/configuration behavior should align to Azure's assignment and execution model.

Direction:

1. Test points should be treated as plan-plus-suite-plus-case-plus-configuration execution units where Azure expects that model
2. Configuration sync should respect Azure-supported configuration identity and association rules
3. QAF-OnPrem should not invent alternate point semantics that conflict with Azure execution behavior
4. Point-based configuration assignment is the critical next hardening area before execution sync is expanded further
5. The implementation should move toward testcase membership reconciliation that is configuration-aware rather than relying on testcase-level assumptions alone

### Test run and test result policy direction

Runs and results should align to Azure execution containers and result records.

Direction:

1. Test runs should represent execution sessions in the way Azure expects
2. Test results should represent per-point or per-test execution facts in the way Azure expects
3. Result updates should use Azure result APIs for status, timing, outcome, comments, and attachments where supported
4. Run/result sync must be retry-safe and duplication-aware
5. Run/result expansion should begin only after the testcase is known to exist in the correct Azure plan/suite/point context for the intended execution flow

### Defect and bug-link policy direction

Defect synchronization should align to Azure bug/work-item behavior and Azure-supported test-result associations.

Direction:

1. Bugs should be created and updated as Azure work items where Azure expects that artifact type
2. Linked defects originating from execution should use Azure-supported result or test relationships where appropriate
3. Standalone defects should not pretend to be execution-linked artifacts when they are not
4. Bug creation and linking must include duplicate checks and durable cross-reference storage
5. Execution-linked bug policy depends on a settled run/result model and should not be finalized ahead of that prerequisite

### Comment, narrative, and evidence policy direction

Narrative detail and evidence should be synchronized only to Azure surfaces that properly support them.

Direction:

1. Test case comments should use the Azure mechanism that belongs to the test case work item when applicable
2. Execution comments should use the Azure mechanism that belongs to runs or results when applicable
3. Step-level narrative, expected/actual detail, and evidence should map to the closest valid Azure-supported execution or work-item surface
4. Screenshots and files should use Azure attachment APIs or linked attachment patterns appropriate to the owning artifact
5. If Azure does not provide a correct home for a local detail, that gap should be documented explicitly instead of hidden by an incorrect mapping
6. Evidence implementation should follow, not precede, the agreement on whether the owning Azure artifact is the testcase work item, run, result, or linked bug

## Future review items

These items remain open for later phases:

1. Whether test plan routing should also influence Azure work item area path by explicit business rule
2. Whether per-test optional area-path override is ever needed
3. Whether plan-level execution artifacts should carry their own Azure routing independent of work item routing
4. The final detailed rules for suite/point/run/result/defect/comment/evidence synchronization
5. Entity-specific de-duplication rules and replay behavior per Azure artifact type