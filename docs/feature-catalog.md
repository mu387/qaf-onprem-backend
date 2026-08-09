# QAF-OnPrem Backend Feature Catalog

## Purpose
QAF-OnPrem Backend is an ASP.NET API that provides authentication, test data management, execution services, scheduling, and platform integrations.
It is deployed as an IIS child app under `/api` and depends on SQL Server.

## Top Enhancement To-Do (May 2026)
Lightweight collaboration awareness for shared authoring records is the current top enhancement.

Goal:
1. Prevent silent edit collisions on shared authoring flows.
2. Support frontend record presence and edit-claim status for long-running edit sessions.
3. Start with Components and Test Design before expanding to additional domains.

Phase 1 backend scope:
1. Presence tracking for active record viewers/editors.
2. Edit-claim lease support for component and test-design records.
3. Save/update enforcement so only the current valid claim owner can persist changes.

Lightweight design rules:
1. Use expiring edit claims rather than permanent locks.
2. Treat 15 minutes of user inactivity as the claim-expiry threshold.
3. Renew claim activity on meaningful edit actions, with slow background refresh instead of aggressive heartbeat traffic.
4. Allow read-only access for non-owners while a valid claim exists.
5. Keep room for lightweight handoff/request notifications without building full chat first.

Planned expansion after Phase 1:
1. Test Plans.
2. Suites.
3. Additional editable shared records only after the initial model is validated.

## Stack
- .NET API (published self-contained for win-x64)
- IIS in-process hosting (AspNetCoreModuleV2)
- SQL Server primary datastore

## Full Backend Functionality Inventory
Backend functionality is exposed mainly through these controllers:
- [src/QafOnPrem.Api/Controllers/AuthController.cs](../src/QafOnPrem.Api/Controllers/AuthController.cs)
- [src/QafOnPrem.Api/Controllers/AppDataController.cs](../src/QafOnPrem.Api/Controllers/AppDataController.cs)
- [src/QafOnPrem.Api/Controllers/ExecutionController.cs](../src/QafOnPrem.Api/Controllers/ExecutionController.cs)
- [src/QafOnPrem.Api/Controllers/HealthController.cs](../src/QafOnPrem.Api/Controllers/HealthController.cs)

### Domain groups covered by API
1. Authentication and identity
2. Authorization and permissions
3. Roles and users administration
4. Projects, test suites, test plans, and plan items
5. Components, keywords, and before/after steps
6. Variables and test configurations
7. Dashboard and defect management
8. Execution devices, pools, schedules, and queues
9. Queue item lifecycle (start, heartbeat, interrupted, finish)
10. Runner bootstrap/claim flows for local workers
11. Step status logging and batched step updates
12. Test suite close and video upload
13. Integrations (connections, mappings, sync jobs, retries)
14. Health/readiness status and SQL identity readiness

## Deployment Topology
- Frontend static site at IIS root
- Backend API as child app at `/api`
- Effective API route from browser often appears as `/api/api/...`

## Endpoint Domain Map
This map helps reproduce the full product behavior without code archaeology.

### Auth endpoints
- `/api/login`
- `/api/client/login`
- `/api/client/forgot-password`
- `/api/reset-password`
- `/api/me`
- `/api/profile`
- `/api/permissions`

### App data and admin endpoints
Large surface in `AppDataController`, including:
- Roles and users
- Components and bulk import/export
- Projects and dashboard summary
- Defects and defect statuses
- Keywords/global-keywords and before/after steps
- Variables/configuration entities
- Test plans, test suites, test runner logs
- Integrations and mappings

### Component metadata guidance support (May 2026)
Backend now supports project-scoped component metadata guidance used by the frontend component form.

Implemented backend behavior:
1. Added a lightweight component metadata catalog endpoint for a selected project.
2. Catalog returns existing page values and feature-to-page mappings already present in component data.
3. Existing exact duplicate check remains available through `/api/components/exists`.
4. Create and update component endpoints now also enforce the exact duplicate rule server-side.

Why this was added:
1. Frontend-only duplicate awareness was not sufficient as a hard-stop rule.
2. Users needed visibility into existing project naming patterns before saving.
3. The platform needed safer save enforcement without introducing separate Page or Feature master tables.

### Execution endpoints
`ExecutionController` handles:
- Device pools and devices
- Schedules and run-now
- Queue CRUD/cancel/retry/bulk-delete
- Queue item lifecycle updates
- Local queue claim and runner bootstrap

### Health endpoints
- `/health`
- `/health/ready`

## Runtime Configuration Contract
Primary config key:
- `ConnectionStrings:SqlServer`

Current expected test-style value:
- `Server=ESLTSTAWFBDBV01.lwrrsc.maxopco1.com;Database=QAF-OnPrem;TrustServerCertificate=False;Encrypt=True;`

Reference file:
- [src/QafOnPrem.Api/appsettings.json](../src/QafOnPrem.Api/appsettings.json)

## SQL Identity Startup Behavior (Critical)
`SqlIdentity` is enabled with startup validation and fail-fast behavior.
If SQL connectivity or required tables are invalid, API host startup fails and IIS returns generic 500 pages.

Key flags currently in config:
- `Enabled=true`
- `ValidateRequiredTables=true`
- `FailStartupWhenInvalid=true`

## End-to-End Functional Flows
These represent the real product operations the backend must support.

### Flow 1: Login and authorization
1. Client posts credentials.
2. Backend validates user and returns token/user payload.
3. Subsequent calls require bearer token and permission checks.

### Flow 2: Test asset lifecycle
1. CRUD for projects, components, variables, keywords, and suites.
2. Test plans bind suites and ownership.
3. System supports status updates and filtering for operational UI.

### Flow 3: Queue execution lifecycle
1. Queue created from schedules/plans.
2. Worker claims queue item.
3. Worker posts start/heartbeat/interrupted/finish.
4. Backend maintains queue item state and aggregates run outcomes.

### Flow 4: Step logging and closure
1. Worker fetches suite steps.
2. Worker submits step statuses (including batched v2 endpoint).
3. Suite is closed with final status.
4. Video evidence upload is accepted and associated.

### Shared Upload Storage To-Do (May 2026)
Background:
1. Video evidence upload is designed to use shared upload storage, not IIS node-local site folders.
2. Backend upload handling resolves `Uploads:RootPath` and then writes into `test-runner/images/{clientId}` or `test-runner/videos/{clientId}` under that root.
3. Current test-environment IIS deployment has `Uploads.RootPath` blank in deployed `appsettings.json`, which causes `/upload/testsuites/video` to return `503` before DB association runs.
4. The intended shared storage root for the test environment is the UNC path:
  `\\maxhealth.com\filecabinet\Applications\QAF-OnPrem\uploads\test`
5. Expected client-specific folders under that root include examples like:
  `\\maxhealth.com\filecabinet\Applications\QAF-OnPrem\uploads\test\test-runner\images\11\`
  `\\maxhealth.com\filecabinet\Applications\QAF-OnPrem\uploads\test\test-runner\videos\11\`

To-do:
1. Set `Uploads:RootPath` on deployed API nodes to `\\maxhealth.com\filecabinet\Applications\QAF-OnPrem\uploads\test`.
2. Confirm the required `test-runner/images/{clientId}` and `test-runner/videos/{clientId}` folders are provisioned on the share.
3. Grant the API runtime identity read/write access to the UNC share so uploads can be stored and later served back through `/uploads`.
4. Re-test video upload after configuration so recorded runner videos attach to the test run successfully.

### Flow 5: Integrations
1. Connection config and mappings are managed.
2. Sync jobs can run single, bulk, retry, or replay-failed modes.
3. Operational summary and health endpoints expose sync status.

## Integration Enhancements (May 2026)
This section captures implementation changes added to the SQL-backed integration pipeline and Azure DevOps sync behavior.

### Background processing and queue execution
1. A hosted background service now processes pending integration jobs continuously (poll + batch model).
2. Job failures now persist richer exception text for easier diagnosis.
3. Retry behavior remains backoff-driven; terminal failures are marked after max attempts.

### Auto-sync trigger behavior for test cases
1. Test case sync jobs are auto-queued after test suite save operations (create and update flows).
2. Auto-queue runs only when integration connection flags allow it:
- is_enabled = 1
- sync_test_cases = 1
- auto_sync_test_cases = 1
3. Queue idempotency for auto-sync is version-key based so unchanged versions are not endlessly re-queued.
4. Plan-link actions by themselves are not the primary auto-sync trigger; suite save is the trigger point.

### Azure test case step sync format
1. Manual steps are exported from first active dataset rows per component and serialized into payload manual_steps.
2. Steps with value SKIP are filtered out case-insensitively.
3. Azure test step description includes a visual parameter token at end of action text, for example: Launch Browser @step-1.
4. Parameter names are normalized as step-1, step-2, step-3, and so on.
5. Parameter values are stored via Azure fields:
- Microsoft.VSTS.TCM.Parameters
- Microsoft.VSTS.TCM.LocalDataSource
6. Expected output is mapped into the expected column of each Azure action step.

### Integration credentials resolution (user-owned PAT)
1. Integration job execution now supports user-owned Azure PAT selection at runtime.
2. Processor resolves credentials in this order:
- PAT from triggering user profile/preferences settings.
3. No fallback to connection-level PAT is allowed for Azure sync.
4. If user PAT is missing, job processing fails with a user-facing error and does not push data to Azure.
5. User profile/preferences JSON paths currently recognized for Azure PAT:
- integrations.azure_devops.pat
- integrations.azure.pat
- azure_devops.pat
6. This ensures sync actions always use the credentials of the user who triggered the test asset change.

### Azure run sync hardening
1. Run creation no longer attempts to create directly in Completed state.
2. New runs are created in allowed state first, then patched/result-updated as needed.

### App data mutation hardening
1. Add/remove suite-to-plan-item endpoints were stabilized for transaction/reader consistency.
2. Internal helper queries now align transaction scope to prevent intermittent 500 errors.

### Component duplicate hardening and metadata catalog
1. Component save now treats exact duplicate `Project + Page + Feature` as a server-enforced conflict, not just a frontend check.
2. Component metadata catalog is project-scoped and derived from existing component rows only.
3. Catalog is intended for guidance and reuse visibility, while save enforcement remains the exact duplicate rule.

## Session Continuation Update (May 2026)
This section captures additional integration behavior changes and validations completed after the earlier May updates.

### Ready-state sync gating for test cases
1. Auto-sync queueing for `test_case` now requires current test state to be `Ready`.
2. If a test is linked to a plan item while in `Design` (or any non-Ready state), the local link is allowed but no Azure sync job is queued.
3. When that same test is changed back to `Ready` and saved, queueing resumes and sync proceeds.

### Plan-item membership and ordering behavior
1. For Ready-state test-case sync jobs, processing order remains:
- Upsert test case in Azure first.
- Then sync/reconcile test-plan item suite membership.
2. This preserves dependency ordering so suite assignment is attempted after case identity is available.

### Shared connection and user-owned credential model
1. Integration connections remain shared by client/project scope.
2. Runtime Azure authentication remains user-owned PAT only (resolved from the triggering user's settings).
3. Connection-level PAT was removed from the persisted connection credentials payload to avoid ambiguity.

## Azure Integration Enhancement Vision (Planned)
This section captures the agreed direction for the next Azure integration enhancement phases. This is planning only and does not change the currently deployed integration behavior.

### Guiding principle
1. V1 is not a rewrite of the integration engine.
2. V1 does require targeted integration-layer changes in routing resolution and Azure target selection.
3. V1 must preserve the current queue processor, retry model, mapping model, and link persistence model unless an explicit defect forces a local change.
4. User-owned Azure PAT remains in user settings and is not moved back to shared connection credentials.

### V1 ownership model
1. Azure connection becomes the shared company/org integration record.
2. Connection retains static org-level settings and fallback routing values.
3. QAF-OnPrem Project is the business boundary for the test and remains the owner of Azure project selection.
4. Test Plan becomes the owner of Azure assignment-routing details inside that Azure project.
5. Test Plan will capture:
- area path
- iteration path
6. Unassigned ready-state test cases use connection-level fallback routing.
7. Fallback routing must include:
- area path
- iteration path
8. Test Plan membership must stay within the owning QAF-OnPrem Project boundary.

### V1 routing rules
1. Azure project for a test case comes from the owning QAF-OnPrem Project.
2. A test case linked to a Test Plan inherits area path and iteration path from that Test Plan.
3. A ready-state test case not linked to any Test Plan uses the shared connection fallback area path and iteration path.
4. Test Plan sync uses Azure project from the related QAF-OnPrem Project and area path/iteration path from the Test Plan.
5. Plan-item suite membership reconciliation continues to run inside the Azure project selected by the owning QAF-OnPrem Project.
6. Cross-project plan assignment is not a supported routing case for V1 and should be rejected by validation.

### V1 sync-state rules
1. Local test case sync trigger remains `Ready` only.
2. Local `Design` state must not trigger Azure sync.
3. Azure bootstrap behavior may still create a test case using an Azure-acceptable initial state when required by Azure workflow rules.
4. After bootstrap create, Azure state can continue advancing through the existing mapped update behavior.
5. This preserves the current Ready-gate contract while avoiding Azure create failures.

### V1 non-breaking implementation rule
1. V1 should be delivered by changing routing metadata ownership and effective-target resolution, not by replacing the integration pipeline.
2. Existing job queueing, job processing, retry, mapping, link persistence, and reconciliation behavior should remain intact where possible.
3. The main code change surface is the routing-resolution layer plus the places where Azure project, area path, and iteration path are chosen.

### V2 expansion
1. Extend integration coverage from test plans and test cases into run-level updates.
2. Update all test runs assigned to the Test Plan item.
3. Update defects assigned to those runs.
4. V2 dependency order is:
- Test Plan item
- Test runs under that item
- Defects linked to those runs

### V1 implementation tasks (reviewed draft)
1. Add backend validation so a Test Plan can only contain test cases from the same owning QAF-OnPrem Project.
2. Preserve the current integration processor, queue model, retry model, mappings, and link persistence.
3. Keep Azure project ownership on QAF-OnPrem Project.
4. Move only Azure area/iteration ownership to Test Plan plus connection fallback.
5. Keep runtime authentication resolved from user settings exactly as it works today.
6. Add an explicit effective-routing resolution step for every Azure sync that needs project, area path, or iteration path.
7. Keep existing Ready-gate behavior for local test-case queueing.
8. Treat this as targeted integration-layer change with strict scope control, not as a full rewrite.

### V1 backend work items (reviewed draft)
1. Validation layer:
- reject add-to-plan requests when the test case Project does not match the Test Plan Project
- return a user-facing validation error instead of silently allowing cross-project assignment
2. Data model update:
- keep Azure project resolution tied to QAF-OnPrem Project
- add Azure area/iteration routing fields to Test Plan storage
- retain fallback Azure area/iteration routing fields on shared connection storage
- stop relying on local Project for area/iteration routing
3. App-data contract update:
- return Project Azure project data where needed for sync resolution
- return Test Plan area/iteration routing data in test-plan read payloads
- accept Test Plan area/iteration routing data in create/update payloads
- return connection fallback area/iteration routing data in integration connection payloads
- accept connection fallback area/iteration routing data in connection create/update payloads
4. Queue/payload resolution update:
- for `test_plan`, resolve Azure project from the owning QAF-OnPrem Project and area/iteration from the Test Plan
- for `test_case`, resolve Azure project from the owning QAF-OnPrem Project and area/iteration from the linked Test Plan when linked
- for unassigned `test_case`, resolve Azure project from the owning QAF-OnPrem Project and area/iteration from connection fallback
5. Integration-layer target selection update:
- stop assuming one connection-level Azure project is the effective target for every sync
- choose Azure organization from connection config
- choose Azure project from the owning QAF-OnPrem Project
- build the Azure base URL from the resolved effective project for that sync
6. Reconciliation update:
- ensure plan-item suite reconciliation runs against the Azure project resolved for the owning QAF-OnPrem Project
- do not assume all reconciled assets under a connection belong to one Azure project
7. Non-breaking constraint:
- do not replace current job tables
- do not replace current hosted processor loop
- do not replace current mapping application model
- do not replace current integration link persistence model

### V1 backend field contract (approval draft)
Project should remain the owner of:
1. `azure_project`

Test Plan should own these Azure routing fields:
1. `azure_area_path`
2. `azure_iteration_path`

Shared connection should retain these fallback routing fields:
1. `fallback_area_path`
2. `fallback_iteration_path`

User settings remain the source of:
1. Azure PAT

### Routing precedence rules (approved-to-build draft)
1. If a `test_plan` is being synced, use Azure project from the owning QAF-OnPrem Project and area path/iteration path from that Test Plan.
2. If a `test_case` is linked to an owning Test Plan, use Azure project from the owning QAF-OnPrem Project and area path/iteration path from that Test Plan.
3. If a `test_case` is not linked to any Test Plan, use Azure project from the owning QAF-OnPrem Project and area path/iteration path from connection fallback.
4. If no valid routing target exists after applying the above rules, do not guess a target; fail validation and do not queue/send the sync.

### Approved policy decisions
1. A test case belongs to exactly one owning QAF-OnPrem Project.
2. A Test Plan also belongs to exactly one QAF-OnPrem Project.
3. Test Plans may contain only test cases from that same owning Project.
4. Multiple Test Plans are allowed only when those Test Plans remain inside the same Project boundary.
5. Cross-project reuse should be handled by clone/copy behavior, not by shared plan membership across project boundaries.

### What changes in integration code
1. Effective routing resolution must be added or centralized for Azure sync payloads.
2. Azure target selection must stop reading project solely from connection config for all entities.
3. `test_case` payload preparation must resolve Azure project from QAF-OnPrem Project and area/iteration from Test Plan or fallback.
4. `test_plan` payload preparation must resolve Azure project from QAF-OnPrem Project and area/iteration from the Test Plan itself.
5. Azure reconciliation logic must use the resolved QAF-OnPrem Project context instead of assuming connection-global project context.

### What remains untouched in V1
1. Integration job table structure.
2. Hosted polling/batch processor model.
3. Retry and failure backoff behavior.
4. Mapping application model and status-map concept.
5. User-owned PAT resolution from user settings.
6. Ready-state local queue gate for test cases.

### Approved policy decisions
1. A test case may belong to more than one Test Plan.
2. Multi-plan membership is valid because each Test Plan has its own external identity and assignment context in Azure.
3. Removing a test case from one Test Plan de-assigns it from that plan only and must not affect the same test case's assignment under other Test Plans.
4. If a synced test case becomes unassigned from one Test Plan but remains assigned elsewhere, only that removed plan relation should be updated in Azure.
5. If a ready-state test case is not assigned to any Test Plan yet, it should sync to the connection fallback Azure target and park there.
6. While parked, the Azure work item uses Azure project from the owning QAF-OnPrem Project and area path/iteration path from connection fallback.
7. When that same test case is later assigned to a Test Plan, its Azure routing should be updated from fallback area/iteration to the Test Plan area/iteration target.
8. Because Azure project comes from QAF-OnPrem Project, fallback-to-plan reassignment is expected to remain inside the same Azure project for a given test.
9. Test Plan area path and iteration path are the final truth for Azure Test Plan assignment context.
10. If a Test Plan's area path or iteration path changes after sync, Azure-side test relations/assignments for that Test Plan should be updated to match the changed Test Plan routing.

### Current non-assumption stance
1. This plan does not assume multi-plan precedence.
2. This plan does not assume automatic re-home behavior across Azure projects.
3. This plan does not assume deletion/removal behavior for Azure artifacts unless explicitly approved.

### Test case agreement (planning record)
1. Azure test case sync should use the final assembled test case as truth, matching the Preview behavior users see in the editor.
2. Local components, datasets, overrides, and variable resolution are authoring inputs; Azure should receive the assembled output, not the raw authoring structure.
3. QAF-OnPrem-only metadata may remain local when Azure does not need it.
4. A test case enters Azure only when the local test is in `Ready` state.
5. While a test case is in `Ready` state, create and update sync behavior should continue using the existing Ready-gated model.
6. If a previously synced test case changes from `Ready` to another mapped state, Azure should update the state of the existing work item and must not delete the Azure test case.
7. Before a test case is assigned to any Test Plan, it should be created in Azure and parked using:
- Azure project from the owning QAF-OnPrem Project
- area path from the shared connection fallback
- iteration path from the shared connection fallback
8. After that same test case is assigned to a Test Plan, the Azure work item should keep the same identity and update its routing to use:
- Azure project from the owning QAF-OnPrem Project
- area path from the Test Plan
- iteration path from the Test Plan
9. Test Plan routing becomes the final truth for Azure assignment context after plan membership exists.
10. This planning record does not require Azure to mirror every local field; it only requires Azure to receive the fields needed for an accurate executable test case.
11. Local `title` and `test_title` duplication is a separate cleanup candidate and is not treated as a blocker for the current Azure test case contract.

### Validation outcomes captured
1. Repeated add/update/remove test-case sync cycles were executed and converged successfully.
2. Ready-gate API validation covered:
- Ready + add => job queued.
- Non-Ready + add => no job queued.
- Back to Ready + add => job queued again.
3. Post-cleanup smoke checks confirmed integration jobs still process successfully with connection credentials set to `{}` and user PAT resolution active.

## Database Provisioning And Promotion
### Assets
- Backup file (provided for DBA):
  - `artifacts/deploy/QAF-OnPrem-backend-local-sqlserver-backup.bak`
- Promotion script:
  - `scripts/promote-local-sqlserver-to-remote-sqlserver.ps1`

### Promotion Script Contract
The script parameters are:
- `-SourceConnectionString` (defaults to local `QafOnPremDotNet`)
- `-TargetConnectionString` (mandatory)
- `-SchemaName` (default `dbo`)

Important:
1. The script does not create the database itself.
2. It creates/recreates schema objects and copies data inside existing source/target databases.
3. It refuses to run if source and target resolve to same DB identity.

## Data/DB Expectations For Full Functionality
For complete app behavior, DB must include:
1. Identity/auth tables required by SQL identity readiness checks.
2. Core app entities used by AppData and Execution services.
3. Valid seed/config data required by dashboard, planning, and queue views.

If DB is missing required tables, `/health/ready` returns non-ready and API startup can fail depending on configuration.

## DBA Operational Flow (Test)
1. Restore BAK into source DB in DBA environment.
2. Create/provide target DB (expected app DB name: `QAF-OnPrem`).
3. Run promotion script with explicit source and target connection strings.
4. Grant DB permissions to IIS app pool service account used by API.
5. Confirm final server/database/auth mode that appsettings should use.

## App Pool Permission Requirement
API app pool identity must have DB access at minimum for read/write operations required by runtime.
In local validation we granted:
- `db_datareader`
- `db_datawriter`
for the specific app pool identity.

## Build And Publish
From repo root:
```powershell
dotnet publish .\src\QafOnPrem.Api\QafOnPrem.Api.csproj -c Release -r win-x64 --self-contained true -o .\artifacts\deploy\QafOnPrem.Api.win-x64.self-contained
```

Package artifact:
```powershell
Compress-Archive -Path '.\artifacts\deploy\QafOnPrem.Api.win-x64.self-contained\*' -DestinationPath '.\artifacts\deploy\QafOnPrem.Api.win-x64.self-contained.zip' -CompressionLevel Optimal
```

## IIS Deployment Notes
- Deploy published output into API app folder
- Ensure valid `web.config` for AspNetCoreModuleV2
- If startup fails, enable temporary stdout logging to writable path and inspect logs
- Confirm child app inheritance does not override handlers incorrectly

## Functional Smoke Matrix (Backend)
Minimum backend pass criteria for full platform operation:
1. `/health` returns 200.
2. `/health/ready` returns ready state.
3. Auth login endpoint returns token.
4. Permission-protected read endpoint (for example projects) returns data with bearer token.
5. Queue list and queue lifecycle endpoints function.
6. Step status save endpoint accepts payload.
7. Integration list endpoint responds.

If any of these fail, frontend may appear partially functional while core workflows are broken.

## Health And Functional Verification
Basic health:
- `GET /api/health/live`
- `GET /api/health/ready`

Auth check:
- `POST /api/api/client/login` (for child app path topology)

Authenticated smoke endpoints after token:
- `/api/api/projects`
- `/api/api/users/settings`
- `/api/api/dashboard/summary`
- `/api/api/system-settings/health-poll`

## Known Incident Lessons Captured
1. Generic IIS 500 at `/api` + `/api/health/*` can indicate startup failure, not controller bugs.
2. SQL login failures during startup validation can present as full API outage.
3. Confirm DB name and identity grants before deeper API debugging.
4. Keep frontend route debugging and backend startup debugging separate to avoid mixed root cause analysis.

## Troubleshooting Timeline And Stakeholder Context (Apr 2026)
This section preserves the actual incident path and communication outcomes.

### What was observed
1. Frontend became accessible, but API endpoints on test returned 500.
2. `/api` and health endpoints showed generic IIS failures.
3. Startup logs (shared by Chris) showed SQL identity startup validation failure.

### Key log-level finding from Chris-provided diagnostics
- Startup failure included SQL connectivity/auth failure with message pattern equivalent to:
  `SQL identity startup validation failed ... SQL Server connection is not reachable ... Login failed for user ''.`

### Root cause direction after logs
1. Not a frontend routing bug.
2. Not an endpoint-specific controller issue.
3. Primary blocker was environment DB readiness and permissions.

### Communication and handoff outcomes
1. Chris requested DBA team engagement and verification of deployed appsettings connection string.
2. Team aligned that expected DB name is `QAF-OnPrem` for test environment appsettings target.
3. DBA was asked to:
- Restore backup BAK in their environment.
- Use promotion script where source and target are distinct.
- Confirm final target connection details.
- Grant DB access to IIS app pool service account used by API.

### Critical operator notes captured during handoff
1. Script prompt asking for `TargetConnectionString` expects a full raw connection string value, not `SqlServer=` prefix.
2. If source and target are the same restored DB, promotion script should not be used (it blocks same identity).
3. In same-DB restore scenario, restore directly to target DB (`QAF-OnPrem`), grant app pool permissions, and validate API.

### Sample target connection string used in communications
`Server=ESLTSTAWFBDBV01.lwrrsc.maxopco1.com;Database=QAF-OnPrem;Integrated Security=True;TrustServerCertificate=False;Encrypt=True;`

## Communication Transcript Summary (Apr 2026)
This is a concise, date-stamped operational transcript so future troubleshooting can resume without re-discovery.

- 2026-04-08: Frontend route loop and deep-link issues were investigated; stale IIS route artifacts and fallback mismatches were identified and corrected.
- 2026-04-08: Frontend and backend artifacts were rebuilt and repackaged for handoff.
- 2026-04-08: Chris confirmed frontend was loading on test, but API endpoints still returned 500.
- 2026-04-08: Health endpoint failures on test indicated API startup/runtime issue rather than endpoint logic defects.
- 2026-04-08: Chris shared startup diagnostics indicating SQL identity startup validation failure and SQL login/connectivity issue.
- 2026-04-08: Team aligned root cause to DB provisioning/access and moved action to DBA.
- 2026-04-08: DBA was asked to restore the provided BAK, confirm target DB naming as QAF-OnPrem, and ensure app pool identity access.
- 2026-04-08: Clarified that promotion script prompt requires full TargetConnectionString value (raw SQL connection string) and not a key-prefixed value.
- 2026-04-08: Clarified that promotion script is for distinct source and target databases; it should not be used when source and target are the same restored DB.
- 2026-04-08: Confirmed expected appsettings key is ConnectionStrings:SqlServer and expected target DB name is QAF-OnPrem for test environment.
- 2026-04-08: Stakeholder message alignment captured: involve Michael/DBA team for database restore, promotion path decision, and service-account grants.

## Rebuild From Scratch (Backend Only)
1. Clone repo and install .NET SDK/runtime as required
2. Publish backend artifact
3. Provision SQL database and permissions
4. Set `ConnectionStrings:SqlServer` for target environment
5. Deploy to IIS child app `/api`
6. Validate health endpoints, then login, then authenticated endpoints

## Full Platform Reproducibility Notes
To reproduce the backend portion of the full app without follow-up questions:
1. Use IIS child app model (`/api`) and frontend root model (`/`).
2. Keep appsettings DB target aligned with real environment DB name.
3. Ensure app pool identity has DB permissions before startup checks.
4. Validate SQL identity readiness first, then functional endpoints.
5. Use promotion script only when source and target DBs are distinct.

## Session Continuation Update (Apr 2026)
This section captures continuation details after the initial communication summary.

### Additional backend/environment validation completed
1. Local validation host checks repeatedly returned successful status for:
- Frontend routes (`/`, `/login`, `/components`)
- API health endpoints
- Login endpoint and token issuance
- Authenticated follow-up endpoints (projects/settings/dashboard/system-settings)
2. DB access for IIS app pool identity was explicitly granted in local validation DB to confirm permission path.

### Remote test environment findings (new)
1. Browser login request URL is confirmed as `https://QAF-OnPrem.test.maxhealth.com/api/api/client/login`.
2. Remote calls from clients and command-line probes fail before HTTP response.
3. DNS resolves host to `10.31.160.131`, but TCP connect to `10.31.160.131:443` times out.
4. `curl` reports timeout with exit code `28`; PowerShell `Test-NetConnection` reports `TcpTestSucceeded=False`.
5. Same pattern reproduced from multiple machines, indicating shared infra/network-path issue.

### Current root-cause status
1. Application credentials and endpoint pathing are not the active blocker.
2. Current blocker is network/service reachability to remote test host on TCP 443.

### Required next actions (infra + hosting)
1. Allow source client subnet(s) to destination `10.31.160.131:443`.
2. Verify intermediate firewall/ACL/NSG/LB path rules.
3. Verify destination server firewall allows inbound 443.
4. Verify IIS/site HTTPS binding and listener/certificate are active and healthy.

### Resume checklist for future sessions
1. Re-run `Test-NetConnection QAF-OnPrem.test.maxhealth.com -Port 443`.
2. Re-run curl POST to `/api/api/client/login` and capture HTTP status/body.
3. If HTTP response is present, continue auth-layer debugging from response payload.

## To-Do (Security Hardening)
1. Move sensitive runtime secrets out of source-controlled config files:
- Remove committed production JWT signing keys and DB connection details from `appsettings.json`.
- Use environment variables or secret stores for production values.
2. Add strict validation/allowlist for execution device host values used in backend health checks:
- Only allow approved hostnames/IP ranges.
- Block loopback, link-local, and disallowed internal ranges where appropriate.
3. Harden backend host filtering:
- Replace `AllowedHosts: "*"` with explicit allowed hostnames for deployed environments.
4. Add environment guardrails for frontend API base usage:
- Prevent accidental local/dev usage of external production API endpoints.
- Fail fast or warn clearly when non-approved API hosts are configured on dev machines.
5. Add a pre-deploy security checklist:
- Verify no secrets are committed.
- Verify endpoint targets are expected for environment.
- Verify CORS origins are environment-specific and minimal.
