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

## ADR-020: Source Event Catalog Verified Against Official Names

- **Context**: domain events were defined only implicitly by the normalizers. Nothing checked that the source event names exist, or showed which source events the twin drops.
- **Decision**:
  - `SourceEventCatalog` (Core) lists each exact source event (GitHub event + action, Octopus category, Event Grid event type, Resource Graph change type) with its twin event type, DDD domain event and bounded context.
  - Snapshots of the official names (GitHub docs, our Octopus instance's `/api/events/categories` and `/api/events/groups`, Microsoft Learn) live in `docs/sources`. Tests check catalog names against them, run each row through the normalizer, and generate the tables in `docs/event-mapping.md`.
  - Octopus categories journaled in volume under the generic fallback now have explicit mappings, as do the remaining Azure resource event types.
  - GitHub `release` deletion maps to `artifact.deleted`, not `artifact.published`.
- **Consequence**: the first rebuild after this change re-normalizes those journal entries under their new types. Read models are unaffected, which the two-pass rebuild check confirms.

## ADR-021: A Rebuild Never Drops State the Raw Store Cannot Reproduce

- **Context**: the first live rebuild that actually cleared tables (2026-11-22 01:57 UTC) came back with fewer rows than it started with, for example 1,216 to 282 pipeline runs. Two causes:
  - The baseline seeder used "no projection versions yet" as its "not seeded" signal. Webhook-driven refreshes had projected state before it ran, so rows from before event sourcing never reached the raw store.
  - Eight backfill batches were dead-lettered while an earlier rebuild held the projection gate (before deferral existed), so they were never processed.
- **Decision**:
  - The seeder records an explicit `projection-baseline` marker. It publishes only rows whose content hash differs from the projection, so running it late is safe.
  - Every rebuild first folds the raw store's state observations in memory, using the time-travel fold, and compares entity keys per kind with the live read models. It aborts without clearing anything if a live row cannot be reproduced, unless `force=true`.
  - `GET /api/admin/rebuild/preflight` exposes the same comparison read-only.
- **Recovery**: the lost rows are re-observed by the deploy's full backfill and the dead-letter replay. History older than the sources' API windows is no longer in the twin's read models, though the sources still hold it.

## ADR-022: Raw Intake Retention With a Blob Archive

- **Context**: the raw intake table is the system of record for rebuilds and grows by roughly 20k events a day. The database stays on the Basic tier, capped at 2 GB.
- **Decision**:
  - A daily retention job moves whole UTC days of processed raw events older than 30 days into a private, cool-tier Blob container (`stcmdemo1twin*`, `raw-intake-archive`), one gzip JSON-lines file per day.
  - Each day file is merged with any earlier file for that day, written completely before it replaces the old one, then read back and checked. Only after that are its rows deleted from SQL, so an interrupted run never loses events.
  - `RawIntakeHistory` feeds rebuilds and preflights from the archive first, then SQL, reading each event once.
  - Retention and rebuild never overlap: they share a `MaintenanceLock`.
- **Credentials**: the deploy principal has no `roleAssignments/write`, so Bicep reads the account key with `listKeys()` and passes it only as a Container App secret. It never appears in logs or the repository.
- **Locally**: the archive is a directory (`FileSystem` provider), so integration tests need no Azure.
- **Size budget (follow-up)**: measured live, the raw table grows by roughly 120 MB of compressed events a day, so 30 days can't stay in a 2 GB database. Retention therefore also archives the oldest complete days while the raw table exceeds `MaxRawMb` (500). The 30-day window is an upper bound, and the archive keeps the full history either way.
- **State volume (follow-up)**: most state observations on 2026-11-22 were churn, not change:
  - Work items alternated between two producers: the board sync and the `workitems.json` fallback.
  - Azure tags came back from Resource Graph with nondeterministic key order.
  - `containers.json` embeds a `generated` timestamp.
  - Webhook `lastDeliveryAt` changes on every delivery.

  The fallback now keeps the board's fields, payloads are canonicalized (`StableJson`), and `lastDeliveryAt` is volatile.

## ADR-023: Side-Table Rebuilds With an Atomic Swap

- **Context**: rebuilding in place paused live intake for the whole rebuild (about 7 minutes per pass on the Basic tier). Projections went stale meanwhile, every poll re-published changed state against stale hashes, and a deploy that landed mid-rebuild left tables half rebuilt.
- **Decision**:
  - The rebuild writes a fresh copy of the journal and read models into the `shadow` schema, created from the EF model (`ShadowTwinDbContext`), so keys and indexes match.
  - Live intake keeps applying to dbo. Rows processed meanwhile are replayed into the shadow in catch-up rounds; journal appends are earliest-wins and projections latest-wins, so order doesn't matter.
  - The final round runs under the projection gate, compares shadow and live fingerprints, and swaps all rebuilt tables into dbo with `ALTER SCHEMA … TRANSFER` in one transaction. Ingestion cursors stay put.
  - The swap happens only when the read models are identical, or with `force`; otherwise the shadow is discarded. This supersedes the preflight as the safety guard; the preflight remains available read-only.
- **Interruptions**: a cancel, an exception or a restart before the swap leaves live state untouched. The startup discards leftover shadow tables, and CI defers deploys while a rebuild runs.
- **Follow-up (live run 2026-11-22)**: intake kept flowing for the whole 27-minute rebuild, and the dashboard answered every request. The final step then timed out at the 30 s default: the journal fingerprint scanned about 300 MB of payloads, and the swap waited on readers. The shadow was discarded and live was untouched. The journal fingerprint now reads only two narrow covering indexes (idempotency keys, which embed the content hash for state observations; type, subject and time). Final-step commands allow 5 minutes.

## ADR-024: Signals and Configuration Changes From High-Value Source Events

- **Decision**: Octopus document events map by their related document type (deployment process, variable sets, interruptions, releases, runbooks, triggers, project settings). Journal events that explain a change attach to its chain stages as `ChainSignal`s at query time, matched by the journal's chain ids, so read models and rebuilds are unaffected. Octopus configuration changes are listed by `GET /api/config-changes` and MCP `get_config_changes`.
- **Shadow swap (phase 2 follow-up)**: the live and shadow fingerprints under the gate now run in parallel. The verified live run held the gate for 130 s (live 84 s, shadow 43 s).

## ADR-025: Repository Issue Sweep and Daily Reconciliation

- **Finding**: cmdemo1-dashboard#1011 (closed 2026-11-22 00:42 UTC, PR #1018) was missing from work items. It is on no GitHub Project (`projectItems` is empty), and work items came only from Project #679 and `workitems.json`, which covers cmdemo1-workorders and its first 150 items.
- **Decision**:
  - The GitHub poller also sweeps each observed repository's issues every 10 minutes (updated-since cursor, ETag-cached).
  - `ISourceReconciler`s in the GitHub and Octopus adapters compare the twin with each source daily and trigger a targeted refresh where it drifted.
  - The report is exposed by API, MCP and the dashboard.
- **Tests**: integration tests are offline. The test host routes the `github`, `github-raw` and `octopus` HTTP clients to an unavailable stub and blanks GitHub tokens, so tests never reach external APIs even when the environment has credentials.

## ADR-026: DORA Keys From Change Chains

- **Decision**: DORA keys are computed from change chains (`ChainDora`), not from raw deployment rows. A chain is one change, from commit to prod. Lead time is commit to prod; a failure is any failed prod attempt or a prod failure signal. Time to restore runs to the next successful prod delivery of that deployable. Bands follow the DORA State of DevOps thresholds. The older delivery-based scorecard stays in the summary for continuity; the dashboard's strip shows the chain-based keys.

## ADR-027: Complete Source Event Coverage

- **Decision**: the catalog covers every documented source event. Hand-written rows carry the domain semantics the twin uses; everything else gets a generated row with a deterministic type, domain event and bounded context (`SourceEventTypes`). That means `twin.github.{event}.{action}` for GitHub, and `twin.octopus.{area}.{verb}` for Octopus, replacing `twin.octopus.audit.{category}`. The name lists (`SourceEventNames`) are generated from the official snapshots, and a test keeps them equal.
- **Not subscribed**: the 44 instance-level Octopus categories (logins, licences, API keys, certificates, signing keys, usage declarations, MCP OAuth) stay out of the space subscription. They describe the Octopus instance rather than delivery, and carry user names and IP addresses. They are typed if they ever arrive.

## ADR-028: Service Bus Switch Kept Behind Configuration

- **Attempt (2026-11-22)**: `az provider register -n Microsoft.ServiceBus` with the existing deploy principal returned `AuthorizationFailed` (no `Microsoft.ServiceBus/register/action`). The provider already showed `Registered`. Per instructions, no other credentials or rights were sought.
- **Decision**: the intake stays on the SQL Server transport (`deployServiceBus=false`). The switch stays implemented: managed identity, or a connection string (shared access key or emulator). It is validated at startup and covered by tests. `serviceBusAuth=SharedAccessKey` gives an Owner a Contributor-compatible option (no role assignment) besides the managed-identity mode, which needs `roleAssignments/write`.
- **Open**: the opt-in emulator test (`TWIN_TEST_SERVICEBUS=1`) could not run in the cloud agent sandbox, where the emulator container crashes when connecting to its SQL engine.

## ADR-029: Alerts Through a Configurable Outgoing Webhook

- **Decision**: alert rules (`AlertRules`) are a pure function of change chains, intake health and the latest reconciliation. `AlertService` sends each new condition once, after recording a baseline on the first evaluation. Delivery goes through the `IAlertSink` port, implemented by a new `DigitalTwin.Notifications` adapter that posts Slack, Teams (MessageCard) or generic JSON payloads to one incoming-webhook URL. The URL is empty by default, which means alerts are off; it is set only as a secure Bicep parameter and Container App secret. No channel was created and nothing is posted to a real channel.

## ADR-030: Dashboard Step-Through, Replay Speed and Before/After From the Journal

- **Decision**: the chain player, replay speed/step controls and before/after comparison are built on read models the twin already has: change chains with their signals, and point-in-time state folded from the journal (ADR-021). The comparison is two `asOf` folds diffed on the server (`TwinQueryService.CompareAsync`). It is public because it exposes only what the public dashboard already shows: versions, revisions, resource names, drift and board counts.
- **Superseded changes are not stalled**: a stalled-change alert now needs the chain to be the newest change of its deployable at that stage or beyond. Older versions skipped by a newer promotion are superseded, not stuck. The first live evaluation showed 26 such false positives at tdd.

## ADR-031: The Twin's Pipeline as Code, Applied by the Octopus Owner

- **Decision**: the deploy logic lives in `deploy/scripts`, run by both GitHub Actions and the Octopus process. Each `main` build produces a release candidate: an ACR image, plus an Octopus package with the compiled ARM template so workers need no Bicep. The Octopus project, environment, lifecycle, variables and process are Terraform in `deploy/octopus/terraform`, following cmdemo1-system's Octopus Terraform. CI checks it with `fmt` and `validate` only.
- **Not applied here**: Octopus space `Spaces-355` belongs to cmdemo1's owner, so the definitions are documented (docs/octopus-apply.md) rather than applied. Pushing packages and creating releases (`TWIN_OCTOPUS_RELEASES`) and handing deploys to Octopus (`TWIN_DEPLOY_VIA_OCTOPUS`) are repository variables, unset by default. Neither path promotes.
- **Self-observation**: the twin's own Octopus project maps to the `twin-observer` pipeline, so its deployments never count as cmdemo1 changes.

## ADR-032: One Platform Definition, Selected at Startup

- **Decision**: everything specific to cmdemo1 moved into a `PlatformDefinition`:
  - organization and repositories, the Octopus space, the subscription and resource groups;
  - pipelines and their repository and Octopus project maps;
  - stages, hosting boundaries, Azure stage rules, GitOps status feeds and automation users.

  It is loaded from JSON (`Twin:Platform:Json` or `Twin:Platform:File`) and validated, and the twin refuses to start on an invalid definition. Without one, the twin observes cmdemo1; `docs/platforms/cmdemo1.platform.json` is that default, and a test keeps them identical.
- **Static, chosen once**: normalizers, the chain builder and drift rules are static, pure functions. They read the definition through `PlatformCatalog`, which the composition root sets before the host starts, rather than through DI. Tests that switch platforms run in a non-parallel collection and restore the default afterwards.
- **Stages**: a change chain keeps three delivery slots (first, optional middle, production), mapped from the platform's two or three active stages in order. The API keeps the slot names `Tdd`, `Uat` and `Prod` as stable identifiers; the dashboard shows the platform's own stage names. Production is the last stage, which is where DORA metrics are measured.
- **Infrastructure**: Bicep names every twin resource from `systemSlug`. The default `cmdemo1` gives exactly today's names and unique suffix, so the live twin is unchanged. The workflow, deploy scripts, hook registration and Octopus Terraform take the same settings from repository variables or Octopus project variables.

## ADR-033: The Twin Deploys Through Octopus (tdd, uat, prod)

- **Decision**: with the owner's approval, the Terraform in `deploy/octopus/terraform` is applied by CI to Spaces-355, using the existing `ai-ops` key. It creates only the twin's own project, lifecycle, variables and process. The existing tdd, uat and prod environments, the project group, the feeds and the worker pool are looked up, not changed.
- **One instance, three environments**: the twin is a single platform-level deployment. tdd and uat are gates (the image exists, a Bicep what-if deletes nothing, the live twin is healthy); prod rolls the release out and verifies it serves the release number. The lifecycle promotes automatically after each success, so a release reaches prod with no manual step, and any failed gate stops it.
- **Not platform changes**: the twin's deployments go through cmdemo1's environment names, so the summary, DORA and stage cards exclude observer-pipeline deliveries. The twin's own pipeline row still shows them.
- **State**: Terraform state is in a private container of the twin's storage account, because it holds the sensitive project variables.

## ADR-034: Intake on Azure Service Bus With a Shared Access Key, SQL Server as Fallback

- **Decision**: `Microsoft.ServiceBus` is registered, so the Bicep now deploys `sb-cmdemo1-twin` (Standard, queue `twin-intake`) and the intake listens to it. Authentication is a Send+Listen shared access key passed as a Container App secret, because the deploy principal is Contributor and cannot create role assignments. Managed identity remains a Bicep option.
- **Fallback**: SQL Server stays the documented fallback, set by one setting (`intakeTransport`, `TWIN_INTAKE_TRANSPORT` or `Twin.IntakeTransport`) without removing the namespace. Durability does not depend on the queue: raw rows, the outbox and inbox, and dead letters are in SQL.
- **Stranded messages**: a transport switch leaves messages in the queue nobody listens to. `StrandedIntakeRecovery` re-sends raw events still unprocessed 10 minutes after capture; processing is idempotent, so a re-send is harmless.

## ADR-035: Archive and Compact the Event Journal, Keep History Complete

- **Context**: on the Basic tier (2 GB) the journal reached 620 MB in about 2.5 days: 112,855 events. Payload by kind: work items 155 MB (mostly a flapping bug, fixed separately), Azure resource sets 96.5 MB, desired config 34 MB, lifecycle events about 40 MB, plus about 2.4 KB per row of keys and indexes. A rebuild recreates every table beside the live ones, so its peak was the live database plus the whole journal again: 1,473 MB on Nov 23.
- **Decision**: once a day, after raw retention, whole UTC days (of occurrence) of the journal move to the archive. That is the same private container as the raw archive, holding one gzip JSON-lines file per day, merged with any earlier file and read back before anything is removed. Each archived day also gets a **checkpoint**: every subject's newest state observation at the end of that day, the exact input a point-in-time fold needs. SQL is then compacted:
  - State observations superseded by a newer observation of the same subject before the end of the archived day are removed. Ties are kept, and **drift history stays whole**, because its fold needs every observation.
  - Lifecycle events leave SQL unless change chains, DORA or config views read them: reviews, interventions, guided failures and failed deployments with a chain id; failed builds and jobs with a chain id; config changes.
- **Window**: `Twin:JournalRetention:Days = 14`, two weeks of every event for investigations, kept as whole days. It is paired with a size budget, `MaxJournalMb = 250`. While the journal is over budget, everything except the last hour is archived (`ArchivedThrough` in `journal-meta.json`); the day that boundary falls in is merged again, and re-checkpointed, by the next run. A single busy day therefore cannot hold the database above budget until midnight. On Nov 23 most of the 640 MB journal was that day's own data. At current volume the budget, not the window, decides, as with raw intake. Folds before `ArchivedThrough` read the archive; folds at or after it read SQL. Compaction runs in batches of 500 with low deadlock priority, so live intake writes win. Raw intake's budget drops to 350 MB, so the database targets about 650 MB and a rebuild peak well under 1,500 MB.
- **History stays complete**:
  - **`asOf` at or after the archive boundary** folds SQL alone. The removed rows are each superseded by a kept one, so the newest observation per subject at any later instant is unchanged.
  - **`asOf` inside an archived day** folds the checkpoint of the day before, that day's file, and SQL.
  - **Every other view** (change chains, DORA, before/after, state-at, MCP) is built on those folds plus the lifecycle events SQL keeps.
  - **History start** is recorded in the archive (`journal-meta.json`), because compaction can remove the very first observations.
  - **Raw journal browsing** of archived days (every webhook or poll event, not only the kept ones) reads the day files. `/api/events` answers from SQL.
- **Late rows**: lifecycle events can arrive with old timestamps (backfills). A run finds archived days that still hold removable rows, merges them into the day's file and compacts again. Re-archiving a day (late rows, the open day, or a run cut short before compaction finished) reads from SQL only the rows the day file lacks.
- **Compaction on Basic**: a `DELETE TOP (500) … WHERE EXISTS (newer observation)` read the whole clustered index once per batch. Payloads are in-row, about 53k page reads for 500 rows, so physical IO sat at 100% for hours on Nov 23. Superseded ids now come from the narrow state index, and rows are deleted by clustered key in chunks of 500. Basic allows only about 15–20 physical reads a second (`IO_QUEUE_LIMIT`), so a window function over that index (sort or spool in tempdb) still outlived its timeout when the index was not cached. The index is therefore streamed in its own (subject, time) order, and "newest per subject" is worked out in the app: one ordered seek, no sort, no statement that must finish within a timeout. The point-in-time fold (`state-at`, checkpoints) reads it the same way. Progress is visible on `POST /api/admin/journal-retention/run?wait=false`.
- **Verified by** `JournalArchiveTests`. It records three days of observations, lifecycle events and drift, archives two, and requires identical state-at answers and read-model dumps at six instants before and after, plus "as of now" equal to the live tables. It fails if archived days are not read.

