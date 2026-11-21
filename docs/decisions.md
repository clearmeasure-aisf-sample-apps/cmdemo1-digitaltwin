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
