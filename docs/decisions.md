# Architectural Decisions — `cmdemo1-digitaltwin`

This document records the architectural, infrastructure, and modeling decisions made while building and deploying the `cmdemo1` DevOps Digital Twin.

---

## ADR-001: Target Framework — .NET 11.0 GA (`net11.0`)

- **Context**: Prompt specifies `.NET 11` (newest SDK available, or preview if not GA).
- **Observation**: `.NET 11.0.100` (runtime `11.0.0`, released 2026-11-10) is GA along with `Microsoft.EntityFrameworkCore.* 11.0.0`, `Microsoft.AspNetCore.Mvc.Testing 11.0.0`, and `mcr.microsoft.com/dotnet/{sdk,aspnet}:11.0` container images.
- **Decision**: Target `net11.0` across all projects (`global.json` pins `11.0.100` with `rollForward: latestFeature`, `Directory.Build.props` enables `TreatWarningsAsErrors` and `AnalysisLevel=latest-recommended`).
- **Consequences**: Zero fallback to .NET 10 needed; full C# 14 / .NET 11 minimal APIs, `TimeProvider`, `[LoggerMessage]` source generators, and `ModelContextProtocol.AspNetCore 2.4.0` support.

---

## ADR-002: Twin Placement One Level Above `cmdemo1`

- **Context**: Jeffrey Palermo's architecture intent requires the digital twin to sit **one level above** `cmdemo1` so it observes `cmdemo1` rather than becoming a deployable inside `cmdemo1-system` (avoiding a circular "twin of a twin").
- **Decision**:
  - Model the explicit hierarchy: `Platform` (`Clear Measure Demo Platform`) > `System` (`cmdemo1`) > `Pipelines` (`app-ui`, `app-dashboard`, `system-gitops`, `measurement-scorecard`, `factory-agentic`) > `DeploymentStages` (`tdd`, `uat`, `prod`, `pool`) > `HostingBoundaries` (`cae-cmdemo1-tdd`, `cae-cmdemo1-prod-ekdi`, `swa-centralus`) > `Workloads / Revisions / AzureResources`.
  - Model `cmdemo1-digitaltwin` as `IsTwinObserver = true` (`PlatformObserver` directly under `Platform`), explicitly outside `cmdemo1`'s deployable list.
  - Still register a GitHub repo webhook on `cmdemo1-digitaltwin` so pushes/CI runs on this repository are immediately observable on the Platform Observer strip and event ticker.

---

## ADR-003: Standalone Container Apps Environment (`cae-cmdemo1-twin`) Due to Deployment Stack Deny Assignments

- **Context**: Live inventory of `rg-cmdemo1-nonprod` revealed that `stack-cmdemo1-tdd` and `stack-cmdemo1-uat` apply `denyWriteAndDelete` deny assignments to `cae-cmdemo1-tdd`, `sql-cmdemo1-tdd-5tu77`, `sql-cmdemo1-uat-72l7x`, and all stack-owned resources, excluding only `id-cmdemo1-deploy-nonprod` (`a806a2b6-...`). Our deployment service principal is `Contributor` on `rg-cmdemo1-nonprod` and `rg-cmdemo1-prod` and is **not** in those `excludedPrincipals`.
- **Decision**:
  - Deploy the twin into its own dedicated Consumption Container Apps environment `cae-cmdemo1-twin` and Log Analytics workspace `log-cmdemo1-twin` inside `rg-cmdemo1-nonprod`, using incremental ARM deployment (`deploy-cmdemo1-twin`) rather than joining `cae-cmdemo1-tdd` or altering `cmdemo1` deployment stacks.
  - Pin `minReplicas: 1, maxReplicas: 1` on `ca-cmdemo1-twin` so it continuously receives webhooks and runs background reconciliation timers.

---

## ADR-004: Persistence — Dedicated Azure SQL (`Basic` 5 DTU) + EF Core

- **Context**: Storage must persist across Container App restarts and support full backfill/rebuild from live sources. Options considered:
  1. New DB on existing `sql-cmdemo1-tdd-5tu77`: blocked by `stack-cmdemo1-tdd` `denyWriteAndDelete`.
  2. SQLite on Azure Files SMB volume: `PRAGMA journal_mode=WAL` cannot safely run over Azure Files CIFS without file-locking risk (`SQLITE_BUSY` / I/O errors).
  3. Azure SQL Serverless free offer: `cmdemo1-system/infra/modules/sql.bicep` notes that an always-on polling workload exhausts the 100,000 vCore-seconds/month free allowance in ~2 days and then **auto-pauses** until next month unless set to `BillOverUsage`.
  4. Dedicated Azure SQL logical server `sql-cmdemo1-twin-<suffix>` (`centralus`, since `southcentralus` has subscription SQL provisioning restrictions) with `Basic` 5 DTU / 2 GB (`sqldb-cmdemo1-twin`).
- **Decision**: Use Option 4 — dedicated Azure SQL `Basic` (fixed ~$5/month, always-on, zero auto-pause risk, zero stack deny conflicts) with EF Core (`Microsoft.EntityFrameworkCore.SqlServer`) in production, and SQLite (`Microsoft.EntityFrameworkCore.Sqlite`) for fast local development and xUnit tests.
- **Secret Bootstrap without Owner RBAC**: Because the service principal has `Contributor` only (cannot create `Microsoft.Authorization/roleAssignments`), store the generated SQL admin password in an access-policy Key Vault (`kvcmdemo1twin<suffix>`, `enableRbacAuthorization: false`) so CI/CD stays 100% idempotent without manual secrets.

---

## ADR-005: Container Registry Auth via ACR Admin Credentials (`acrcmdemo1chu4gq`)

- **Context**: `acrcmdemo1chu4gq` is Basic SKU (ACR scope-map tokens require Premium SKU) and not protected by a deployment stack deny assignment. Our `Contributor` service principal cannot create `AcrPull` `roleAssignments` for a Managed Identity. Pushing to GHCR creates private packages by default with no REST API to mark them public.
- **Decision**: Push `cmdemo1/digitaltwin:<version>` to `acrcmdemo1chu4gq.azurecr.io`, enable the ACR admin user (`az acr update --admin-enabled true`), and pass the ACR password as a Container Apps secret (`acr-password`). Existing `cmdemo1` apps continue using their Managed Identities unaffected.

---

## ADR-006: Azure Event Grid Registration Limitation & Resource Graph Reconciliation Backstop

- **Context**: Live capability probing showed `Microsoft.EventGrid` is `NotRegistered` on subscription `4a4dfa6d-d434-4b9e-8a88-63bbf61cfb69`, and our RG-scoped `Contributor` service principal receives `AuthorizationFailed` (`Microsoft.EventGrid/register/action` over `/subscriptions/...`). Additionally, Azure Container Apps does not emit its own Event Grid system-topic events.
- **Decision**:
  - Implement `/hooks/azure` (including Event Grid `SubscriptionValidationEvent` handshake, CloudEvents `OPTIONS`, and `ResourceWrite/Delete/Action` normalization) AND maintain `infra/eventgrid.bicep` + `scripts/register-hooks.sh` so Event Grid system topics are automatically deployed whenever `Microsoft.EventGrid` is registered.
  - Treat **Azure Resource Graph (`resources` + `resourcechanges` 14-day timeline)** plus **ARM Container Apps & Revisions polling** as the guaranteed primary Azure observer. If a subscription Owner runs `az provider register --namespace Microsoft.EventGrid` and re-runs the deploy workflow, Event Grid push delivery activates automatically with zero code changes.

---

## ADR-007: DDD Bounded Contexts, Vocabulary, and State Axes

Following the domain research in the prompt's prior-art references:
- **Vocabulary**: Always use **platform** consistently across all domain concepts, UI copy, and APIs. Separate `DeploymentStage` (`tdd`, `uat`, `prod`, `pool`) from `HostingBoundary` (`cae-cmdemo1-tdd`, `cae-cmdemo1-prod-ekdi`) from `EnvironmentInstance`. Separate `SourceChange` (PR) from `DeliveryExecution` (Octopus deployment) from `Release` (Octopus snapshot).
- **Envelope & Idempotency**: Store every observation in an append-only `ObservedEvents` journal using a CloudEvents/CDEvents-style envelope keyed uniquely by `IdempotencyKey = "{source}:{sourceEventId}"`.
- **DORA Rules**: Match Octopus Insights semantics — a deployment with `HasGuidedFailure == true` counts as a failure in Change Fail Rate even when the server task ends `Success`; exclude `Pull requests` channel runs from production DORA metrics; compute Failed Deployment Recovery Time from failed completion to next success in the same project + stage.
- **Auth Boundaries**: Keep the dashboard (`/`, `/api/dashboard/state`, `/api/twin/summary`, `/api/twin/graph`, `/hubs/twin`, `/api/stream`, `/llms.txt`) public read for zero-friction wallboard viewing; protect query/admin endpoints (`/api/events`, `/api/pipelines`, `/api/environments/*`, `/api/drift`, `/api/workitems`, `/api/deployments`, `/api/metrics`, `/api/explain`, `/api/admin/*`) and the Streamable HTTP MCP endpoint (`/mcp`) with `X-Twin-Key: <TWIN_API_KEY>`; protect ingestion hooks with `WEBHOOK_SECRET` (HMAC-SHA256 for GitHub, `X-Webhook-Secret` header for Octopus, secret token for Azure Event Grid).

---

## ADR-008: GitHub Shared-Token Rate Budget (`<= 1,500 REST calls/hr`) & Conditional Caching

- **Context**: `GH_PAT` (`cm-ai-ops-bot`) is shared with `cmdemo1-workorders`, `cmdemo1-dashboard`, `cmdemo1-system`, and other automation. Early naive polling across 10 repositories + full Project #679 GraphQL queries every 60-120s risked exhausting the 5,000/hr core REST and GraphQL pools.
- **Decision**:
  1. **Webhooks First**: Rely on `/hooks/github` as the primary real-time feed. Webhook follow-up refreshes coalesce per-repository (`15s` window) and sync only that repository rather than polling all 10 repos.
  2. **Zero-Quota Public GitOps Reads**: Fetch `cmdemo1-system` files (`system.json`, `versions.json`, `status/*.json`) from `raw.githubusercontent.com` using `If-None-Match` ETags (0 `api.github.com` quota consumed).
  3. **Conditional ETag Requests**: Every `api.github.com` GET sends `If-None-Match`; `304 Not Modified` responses consume zero GitHub rate-limit quota and reuse the cached body.
  4. **GraphQL Batching**: Combine repository metadata, default branch head commits, recent PRs, open issue counts, and recent GitHub deployments across all repositories into a single aliased GraphQL query rather than ~40 separate REST calls.
  5. **Conservative Intervals**: Full backfill once at startup, incremental repo/actions reconciliation every **10 minutes** (`600s`, stretched 3x in backoff mode), and slim Project #679 GraphQL board reconciliation every **5 minutes** (`300s`).
  6. **Automatic Backoff & Pause**: Read `X-RateLimit-*` headers on every response. Below `1,000` remaining (or >= 900 twin calls/hr), enter `backoff` mode; below `500` remaining (or >= 1,400 twin calls/hr), enter `paused` mode until reset without clearing cached repository/webhook projections. Expose live quota metrics in `/api/twin/summary` (`gitHubQuota`) and the dashboard top bar.
  7. **CI/CD Restraint**: `scripts/register-hooks.sh` checks `/rate_limit` first, skips `PATCH` when an active hook already targets `/hooks/github`, and avoids tight polling loops in deployment scripts.

---

## ADR-009: Onion Architecture Project Layout

- **Context**: The original `Domain` / `Application` / `Infrastructure` split put every adapter (EF Core, GitHub, Octopus, Azure) in one project, and ports lived in Application, so nothing stopped the domain or use cases from depending on integration code.
- **Decision**:
  - `DigitalTwin.Core` holds the domain model, the deterministic normalizers and domain services, the intake model, and every port. It has no project or package references (BCL only).
  - `DigitalTwin.Application` holds use cases and references only Core (plus `Microsoft.Extensions.*` abstractions).
  - Each external dependency gets its own adapter project referencing only Core: `GitHub`, `Octopus`, `Azure`, `Probes` (HTTP probes of cmdemo1 endpoints), `Persistence` (EF Core), and `Messaging` (Wolverine).
  - `DigitalTwin.Api` is the only composition root. Each adapter exposes an `AddTwinXxx` registration plus its own options class (`GitHubOptions`, `OctopusOptions`, …), replacing the shared `TwinOptions`.
  - `ArchitectureTests` checks both the csproj references and the compiled assembly references. It fails the build if Core references anything, if Application references an adapter or an integration package, if adapters reference each other, or if a new `src/` project isn't classified.
- **Consequences**: The Application use cases depend only on ports, so the Wolverine handler calls the `IIntakeEventApplier` port (implemented in Application) instead of referencing Application from Messaging.

---

## ADR-010: Guaranteed Event Capture — Raw Intake Table + Wolverine Transactional Outbox

- **Context**: Before this change, webhooks normalized and wrote straight into the journal and acknowledged afterwards. A crash, bug or redeploy between receipt and write lost the event, and webhook senders only redeliver on a non-2xx response.
- **Decision**:
  - Webhook receivers only verify the signature or shared secret, wrap the raw body in a `RawIntakeEvent` (headers allowlisted, so signatures and secrets are never stored), and call `IIntakePublisher`.
  - `WolverineIntakePublisher` opens one SQL transaction. It inserts the gzipped raw row into `twin.raw_intake_events`, enlists Wolverine's outbox in the same transaction (`DatabaseEnvelopeTransaction`), publishes `TwinIntakeCaptured(rawEventId)`, and commits. The HTTP response is `200` only after the commit; any exception returns `503`.
  - Pollers, probes and drift reconciliation publish through the same path via `ObservedEventIntake`, which first drops idempotency keys already in the journal so raw volume and SQL load stay small.
  - `TwinIntakeCapturedHandler` → `IntakeEventApplier` normalizes deterministically, appends with dedupe on `IdempotencyKey`, pushes SignalR/SSE, and schedules coalesced follow-up refreshes (20s minimum per target, which keeps the GitHub rate budget unchanged).
  - Failures get 3 retries with cooldown (1s, 5s, 15s), then `MoveToErrorQueue` (Wolverine dead-letter table); the raw row records attempts and `dead_lettered_at`. Admin endpoints replay dead letters and rebuild the journal from the raw table.
- **Order independence**: when observations share an idempotency key, the journal keeps the earliest one (`ObservedEventPrecedence`: lowest `IngestedAt`, then a content tie-break). Live processing (parallel, retries, replays) and rebuild-in-capture-order therefore produce the same journal.
- **Consequences**: The raw table is the system of record and the event journal is a pure projection of it. Reconciled snapshots (repos, runs, deployments, Azure resources) remain current-state reads written by pollers; losing one is self-healing on the next poll, so they are not part of the rebuild.

---

## ADR-011: Transport — SQL Server Transport Now, Azure Service Bus by Configuration Later

- **Context**: `Microsoft.ServiceBus` is not registered on the subscription, and the deployment service principal is `Contributor` on the two resource groups only (it can't register providers or create role assignments).
- **Decision**: `Twin:Messaging:Transport` selects the transport:
  - `SqlServer`: Wolverine's SQL Server transport, a `wolverine.wolverine_queue_twin_intake` table in `sqldb-cmdemo1-twin`, polled every 1s. This is what the deployed twin uses.
  - `LocalDurable`: an in-process durable local queue backed by the same SQL message store, for dev and integration tests.
  - `AzureServiceBus`: queue `twin-intake` on `sb-cmdemo1-twin`, using a user-assigned managed identity (`id-cmdemo1-twin`, Service Bus Data Owner). `infra/servicebus.bicep` is deployed only when `deployServiceBus=true`, which also switches the container app's transport and identity. The emulator can be used through `ServiceBusConnectionString`.
  - The outbox, inbox, raw table and dead letters stay in SQL Server for every transport. The message only carries the raw-row ID, so switching transports needs no code change.
- **Consequences**: Enabling Service Bus requires a subscription Owner to run `az provider register --namespace Microsoft.ServiceBus` and grant `roleAssignments/write` (or pre-create the role assignment), then deploy with `deployServiceBus=true`.

---

## ADR-012: One Container App with Separated Hosted Services (Not Split Yet)

- **Context**: Splitting intake and processor into two container apps in `cae-cmdemo1-twin` was considered.
- **Decision**: Keep a single `ca-cmdemo1-twin` app. Intake (webhook endpoints), the processor (Wolverine listener), pollers (`TwinPollingHostedService`) and the follow-up scheduler run as separate hosted services, and `Twin:Messaging:ProcessorEnabled` / `Twin:EnableBackgroundWorkers` let the same image run as intake-only or processor-only.
- **Why**:
  - SignalR/SSE fan-out originates in the processor. A split would need a backplane (Azure SignalR or Redis, which means more providers and resources we can't register) or a notification queue back to the web app.
  - The webhook URL registered on 9 GitHub repos and in Octopus belongs to the web app.
  - The durability guarantee comes from commit-before-ack plus the SQL-backed queue, not from process isolation: a crashed processor loses nothing.
  - A second always-on app roughly doubles compute cost.
- **Consequences**: The split is configuration-only later (same image, two apps, different roles, `SqlServer` or `AzureServiceBus` transport). The integration test "processor down while webhooks arrive" already runs an intake-only host followed by a processor-only host against one database.

---

## ADR-013: Integration Tests on Real SQL Server via Testcontainers

- **Decision**: The integration tests use Testcontainers SQL Server (one container per run, a fresh database per test host) and Wolverine tracked sessions (`TrackActivity().ExecuteAndWaitAsync`) to wait for processing. They run with no Azure credentials, and `TWIN_TEST_SQLSERVER` can point them at an existing server. EF unit tests still use in-memory SQLite.
- **Covered**: duplicate delivery and webhook/poll overlap (captured 3 times, applied once); processor down while webhooks arrive (SQL Server transport, processed later); forced failure → 3 retries → dead letter → replay; rebuild from raw yields an identical journal; `503` when capture can't commit.

---

## ADR-014: Startup and Journal Hardening After the First Durable-Intake Deploy

- **What happened**: The first deploy of the durable intake (image `1.0.15`) crash-looped. Wolverine's envelope-storage schema migration exceeded the default 30s SQL command timeout on the Basic-tier database (DTU stayed below 60%, and there was no blocking). Each restart repeated the migration, so the old revision kept serving and the post-deploy checks ran against it. With a 600s timeout the same image migrated in about 110s and then started normally. Once live, the intake dead-lettered 3 poller batches with `DbUpdateException`: some observations (for example Octopus audit messages) exceeded the journal's column limits. The previous code's row-by-row fallback had silently dropped those observations.
- **Decision**:
  - Wolverine uses its own connection string with a minimum 300s command timeout (`Twin:Messaging:WolverineCommandTimeoutSeconds`). Intake capture keeps the normal timeout, so a stuck insert still returns `503` quickly.
  - The duplicate JasperFx resource-setup pass is removed, leaving one Wolverine storage migration per startup. The startup probe allows up to about 7.5 minutes.
  - The journal fits values to column limits using EF model metadata. Over-long idempotency keys get a deterministic SHA-256 suffix (collision-free and rebuild-stable), and full detail stays in `PayloadJson`. Dead-letter records keep the innermost exception message.
  - CI waits until the latest revision is ready and `/health` reports the build version before verifying, dumps revision logs if it isn't, and replays dead letters after a successful deploy.
  - The `Diagnose Digital Twin Container App` workflow (manual, read-only, secrets masked) is kept for revision status, logs, SQL tier/DTU and intake failures.

---

## ADR-015: Event Grid System Topics Managed in Code

- **Context**: `Microsoft.EventGrid` was registered on the subscription (by a subscription Owner) between 06:03 and 15:08 UTC on 2026-11-21. `register-hooks.sh` deployed `infra/eventgrid.bicep` automatically whenever it saw the provider as `Registered`, so the 15:08 and 15:40 deploys created and updated the Event Grid system topics and subscriptions in `rg-cmdemo1-nonprod` and `rg-cmdemo1-prod`, even though the instruction for this work was not to run Event Grid infra.
- **Decision**: The deploy now only reports the provider state and the existing system topics (read-only). Deploying Event Grid infra requires `ENABLE_EVENT_GRID_DEPLOY=true`, and the script no longer attempts `az provider register`. The topics and subscriptions that already exist were left in place: they deliver to `/hooks/azure`, and those events go through the durable intake like every other webhook.
- **Decision (Jeffrey, 2026-11-21)**: Keep `eg-twin-rg-cmdemo1-nonprod` and `eg-twin-rg-cmdemo1-prod` and manage them in code. `ENABLE_EVENT_GRID_DEPLOY` now defaults to `true` in `ci-deploy.yml` (a repo variable `ENABLE_EVENT_GRID_DEPLOY=false` turns it off). Before each deploy, `register-hooks.sh` runs `what-if` and refuses any Create/Delete of an existing topic. Afterwards it checks that the topic's resource ID and `metricResourceId` (assigned at creation) are unchanged (no recreate) and prints the subscription state and 24h delivery metrics. CI also proves the path end to end: a harmless tag change on the twin-owned `log-cmdemo1-twin` in `rg-cmdemo1-nonprod` must arrive through `/hooks/azure` and appear in the event journal.

---

## ADR-016: Event-Sourced Read Models (Pollers Are Event Producers Only)

- **Context**: Pollers wrote current-state tables directly, so a rebuild could restore only the event journal, and state history was lost.
- **Decision**:
  - The `ITwinStore` port no longer exposes read-model writes, and an architecture test enforces that.
  - Pollers report what they observe through `StateObservationPublisher`, which compares each entity's content hash with the projection's last hash (volatile fields excluded per kind in `StateKinds`). Only material changes become `twin.state.observed` events, captured through the durable intake like webhooks.
  - Upsert kinds emit one event per entity. Replace-set kinds (Azure resources, workloads, freezes, drift, webhook statuses) emit one event per scope carrying the complete set.
  - The projector in Persistence applies events latest-wins on (observation time, event key), tracked in `TwinProjectionVersions`. Re-applying is idempotent, so a crash between journal append and projection is harmless.
  - The intake listeners run sequentially, so live application order equals capture order, which is the order a rebuild replays.
  - On the first start, existing rows are seeded into the raw store as baseline state observations, so history outside the pollers' windows survives a rebuild.
- **Rebuild**: `/api/admin/rebuild` runs under a gate that pauses live application. It fingerprints every read model and the journal, clears them, replays the processed raw rows, fingerprints again, and reports `identical`. Rows still queued behind the gate are left for live processing. The rebuild runs detached, so an HTTP timeout cannot abort it.
- **Not projected**: ingestion cursors and the GitHub quota are operational telemetry.

## ADR-017: Time Travel by Folding State Observations

- **Context**: Point-in-time questions ("what was in uat at 14:00?", "which resources existed yesterday?") need state history. ADR-016 makes every read-model change an event, so the journal already holds that history.
- **Decision**:
  - Read models are reached through `ITwinReadModel`, implemented by the live store and by `PointInTimeReadModel`.
  - For `asOf`, `ITwinStore.GetLatestStateEventsAsync` returns the newest state observation per (kind, subject) at or before that instant: a `RANK()` window over the new `IX_ObservedEvents_StateAsOf` index on SQL Server. Drift additionally folds its full history, because the live projection keeps detection and resolution times.
  - The fold reuses the projection's latest-wins rule (`StateObservations.IsNewer`), so as-of-now equals the live tables, which a test checks.
  - `TwinQueryService` resolves `asOf` once per request into a `TwinReadScope` (model plus "now"), so DORA windows and relative ages are computed at that instant.
  - Built models are cached briefly per instant, which keeps a dashboard replay cheap.
- **Not historical**: ingestion cursors (empty as of a past instant), the GitHub quota and the intake queue are live operational telemetry.
- **Rebuild interplay**: live intake messages that cannot enter the projection gate within 20 seconds (a rebuild is running) throw `IntakeApplyDeferredException`, which Wolverine reschedules indefinitely instead of dead-lettering. A rebuild replays raw rows page by page: appending is earliest-wins and projecting is latest-wins, so batching gives the identical result with far fewer database round trips.

## ADR-018: Change Chains Anchored on Release Candidates

- **Context**: the dashboard showed each pipeline's current versions but not how one change moved through work, PR, build, release and the three stages, or where it failed.
- **Decision**:
  - `ChangeChainBuilder` (Core, pure) links read models into chains. The anchor is a release candidate: a pipeline's Octopus release or versioned build (`^\d+(\.\d+)+$`, so `-pr` environment builds are excluded).
  - cmdemo1 builds release candidates from PR branches before merge, so PRs link by merge commit or head branch. Work items link by the PR's linked issue, the branch's issue number, or the RC version.
  - Each stage keeps its latest attempt plus counts of attempts and failures, so a recovered failure stays visible.
  - The chain's correlation ids are the journal's own chain ids. A new `IX_ObservedEvents_ChainId` index serves the timeline lookup.
  - Chains are computed from whichever read model the request resolves to, so `asOf` applies unchanged.
  - The twin's own pipeline is excluded. The dashboard shows at most six chains, favoring ones that reached a build.

## ADR-019: Bounded Journal Payloads and a Cheap Rebuild on the Basic Tier

- **Context**: on the 5 DTU / 2 GB Basic database the journal grew to 668 MB within a day (71k events), almost all of it raw GitHub webhook bodies copied uncompressed into `PayloadJson`. A live rebuild then spent more than 20 minutes just streaming those payloads for its fingerprint, with live intake paused the whole time.
- **Decision**:
  - The journal keeps source payloads up to 4 KB. Larger ones become a stub with their size and SHA-256, because the raw intake table already keeps every original, compressed. Nothing reads source payloads from the journal; state observations keep theirs.
  - The rebuild clears tables with `TRUNCATE` instead of fully logged deletes.
  - The journal fingerprint is computed in the database, hashing payloads up to 16 KB and taking only the length of larger ones, so it never reads off-row LOB pages.
  - The public dashboard state no longer carries event payloads.
  - The live rebuild check runs only on a manual `verify_rebuild` run, in two passes: read models identical, then identical everywhere.
- **Open**: the raw intake table has no retention yet (about 86 MB after a day); it is the source of truth for rebuilds, so retention needs snapshots first.

## ADR-021: A Rebuild Never Drops State the Raw Store Cannot Reproduce

- **Context**: the first live rebuild that actually cleared tables (2026-11-22 01:57 UTC) came back with fewer rows than it started with, for example 1,216 to 282 pipeline runs. Two causes:
  - The baseline seeder used "no projection versions yet" as its "not seeded" signal. Webhook-driven refreshes had projected state before it ran, so rows from before event sourcing never reached the raw store.
  - Eight backfill batches were dead-lettered while an earlier rebuild held the projection gate (before deferral existed), so they were never processed.
- **Decision**:
  - The seeder records an explicit `projection-baseline` marker. It publishes only rows whose content hash differs from the projection, so running it late is safe.
  - Every rebuild first folds the raw store's state observations in memory, using the time-travel fold, and compares entity keys per kind with the live read models. It aborts without clearing anything if a live row cannot be reproduced, unless `force=true`.
  - `GET /api/admin/rebuild/preflight` exposes the same comparison read-only.
- **Recovery**: the lost rows are re-observed by the deploy's full backfill and the dead-letter replay. History older than the sources' API windows is no longer in the twin's read models, though the sources still hold it.

