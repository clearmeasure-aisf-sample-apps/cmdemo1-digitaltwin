# Teaching Guide: Building a DevOps Digital Twin of a Platform

This guide uses the cmdemo1 twin to teach how to model, observe and reason about a software delivery platform. Every chapter points at real code in this repository. The exercises run locally with .NET 11 and Docker, with no Azure, GitHub or Octopus credentials.

Audience: engineers and architects who know Git, CI/CD and one cloud, and want to learn event-driven observation, event sourcing and delivery metrics on a real example.

## 1. What a Platform Twin Is

A digital twin is a live model of something real, kept current by observing it. Here the real thing is a **platform**: a system's repositories, pipelines, release tool, environments and cloud resources. The twin answers questions the separate tools cannot answer alone:
- "Where is the change for issue #1011 right now?"
- "What was in prod last Tuesday at 14:00?"
- "How long does a change take from commit to prod?"
- "Does what is running match what was declared?"

The twin sits **one level above** the platform. It observes and never deploys the platform's software. Its own repository, pipeline and Azure resources are separate, and its own deployments never count as platform changes: see the `twin-observer` pipeline in [`docs/platforms/cmdemo1.platform.json`](../platforms/cmdemo1.platform.json).

## 2. The Model

The hierarchy is platform, then system, then pipelines (one per deployable), then stages, then hosting boundaries. It is defined in `src/DigitalTwin.Core/Hierarchy/PlatformDefinition.cs` and loaded from a platform definition file (chapter 9).

The domain is split into bounded contexts, one folder each under `src/DigitalTwin.Core`:
- **SourceAndChange**: repositories, PRs, work items.
- **BuildAndArtifact**: workflow runs, release candidates.
- **ReleaseAndDelivery**: Octopus releases, deployments, runbooks, freezes.
- **RuntimeAndPlatform**: environments, workloads, Azure resources.
- **ReliabilityAndDrift**: probes, drift.
- **Measurement**: DORA metrics.

The README's "Domain Vocabulary" section has the ubiquitous language.

**Exercise 2.1.** Open `docs/platforms/cmdemo1.platform.json`. Which pipeline does the repository `cmdemo1-video` belong to, and what deployable does it ship? Check your answer with `TheCmdemo1DefaultResolvesRepositoriesAsTheTwinAlwaysHas` in `tests/DigitalTwin.Tests/PlatformDefinitionTests.cs`.

## 3. Getting Events In: Hooks, Polls and Durable Intake

Facts arrive two ways:
- **Pushed**: GitHub webhooks, an Octopus subscription and Azure Event Grid post to `/hooks/*` (`src/DigitalTwin.Api/Endpoints/WebhookEndpoints.cs`).
- **Polled**: reconciliation pollers in the GitHub, Octopus and Azure adapters read the same sources on a schedule, staying within a 1,500 calls an hour GitHub budget.

Either way, the raw body is first written to `twin.raw_intake_events`, together with a durable message in the same transaction (Wolverine outbox). Only then does the hook return 202. A crash after that point loses nothing: the message is retried, and after three failures it is dead-lettered and can be replayed.

**Exercise 3.1.** Run the twin locally, following the README's "Local Development" section. Send the signed ping from the README and watch `/api/intake/status` count it as captured and then processed. Then send it again with the same `X-GitHub-Delivery`. Why is the second copy harmless? Hint: look for the idempotency key in `IntakeEventApplier`.

## 4. From Raw Payloads to Facts: Normalization

Normalizers in `src/DigitalTwin.Core/Normalization` turn each source's payload into journal events. Lifecycle events use CDEvents-style types such as `dev.cdevents.change.merged` or `dev.cdevents.environment.deployed`. Every other source event gets a deterministic type, `twin.github.<event>.<action>` or `twin.octopus.<area>.<verb>`. Every source event the platform can send is listed, with its mapping, in [`docs/event-mapping.md`](../event-mapping.md), and a test fails if one is missing.

**Exercise 4.1.** Pick an Octopus event category from `docs/sources/octopus-event-categories.json`. Find its row in `docs/event-mapping.md`, then find the code in `OctopusEventNormalizer` that produces it.

## 5. Event Sourcing: State as a Fold of Observations

Pollers never write tables. They publish `twin.state.observed` events through `StateObservationPublisher` (`src/DigitalTwin.Core/Projections`): one per changed entity, or one per scope for complete sets. A content hash suppresses unchanged state, so nothing churns. Projections fold these events into the read models.

Two consequences follow:
- **Rebuild**: the whole twin can be rebuilt from raw history (Blob archive plus SQL) into shadow tables while live intake keeps flowing. It swaps in atomically only if the result is identical (`IntakeRebuildService`).
- **Time travel**: state at any instant is the same fold, stopped at that instant (`TwinTimeTravel`). `asOf` works on every read endpoint and MCP tool, and the dashboard's time slider and before/after comparison use it.

**Exercise 5.1.** Read `AsOfQueriesReplayStateAtEachInstantAndNowMatchesLiveReadModels` in `tests/DigitalTwin.Tests/TimeTravelTests.cs`. Why must "as of just now" equal the live tables exactly? What would it mean if it did not?

## 6. Following a Change: Change Chains and DORA

A **change chain** (`src/DigitalTwin.Core/ChangeChains`) links the stages of one change:

| Stage | Source |
| --- | --- |
| Work item | GitHub issue or project board |
| PR | GitHub |
| Build | GitHub Actions |
| Release candidate | GitHub Actions / registry |
| Octopus release | Octopus |
| Deployments to each stage | Octopus |

Each stage has a status, timings, attempts and the journal signals that explain it, such as reviews, failed jobs, manual interventions and sign-offs. DORA metrics come straight from chains (`ChainDoraMetrics.cs`):
- deployment frequency;
- lead time from commit to production;
- change failure rate;
- time to restore.

**Exercise 6.1.** On the dashboard, open a change in the chain player and step through it. Compare the stage durations with the lead time shown. Which stage dominates?

## 7. Trusting the Twin: Reconciliation, Drift and Alerts

- **Reconciliation** (`ReconciliationService`) compares the twin with GitHub and Octopus daily and reports drift: things the twin missed or still holds.
- **Drift** (`DriftEvaluator`) compares declared state (GitOps `versions.json`) with what is running.
- **Alerts** (`AlertService`) cover failed deploys, stalled changes, dead letters and reconciliation drift. They go to an outgoing webhook that is off until a URL is configured. A change superseded by a newer version is not "stalled"; this rule was learned from live data (ADR-030).

**Exercise 7.1.** Write a unit test in the style of `AlertRuleTests` for a change that failed in its first stage and then succeeded on retry. Should it raise an alert? Why?

## 8. Architecture: Onion Layers and Tests That Keep Them

- **Core**: the domain, using only the base class library.
- **Application**: use cases.
- **Adapters**: GitHub, Octopus, Azure, Probes, Persistence, Messaging, Archive and Notifications, each depending only on Core.
- **Api**: the composition root.

`tests/DigitalTwin.Tests/ArchitectureTests.cs` fails the build if a layer reaches the wrong way. Integration tests use real SQL Server through Testcontainers and never touch Azure.

**Exercise 8.1.** Add a `ProjectReference` from `src/DigitalTwin.GitHub` to `src/DigitalTwin.Octopus` and run `dotnet test --filter FullyQualifiedName~Architecture`. Read why `IntegrationProjectsReferenceOnlyCore` fails, then revert. How should two adapters share something instead?

## 9. Pointing the Twin at Another Platform

Nothing about cmdemo1 is hard-coded outside its platform definition. [`docs/setup/README.md`](../setup/README.md) walks through observing another client's platform. [`docs/platforms/contoso.platform.json`](../platforms/contoso.platform.json) is a two-stage example (dev, prod); `AnotherPlatformTests` boots the twin on it and follows a change to production.

**Exercise 9.1.** Write a platform definition for a fictional client with three stages (`test`, `staging`, `live`), two applications and an infra repository. Validate it with `PlatformDefinition.FromJson` in a new test. Make it fail validation on purpose (for example, map a repository to a pipeline that does not exist) and read the error.

## 10. Operating the Twin

- **Retention**: the database stays on Azure SQL Basic (2 GB). Processed raw events older than 30 days, or beyond a 500 MB budget, are archived to Blob by day and only then deleted from SQL. Rebuilds read the archive first.
- **Pipeline as code**: the twin's own release pipeline is GitHub Actions plus an Octopus project defined in Terraform ([`docs/octopus-apply.md`](../octopus-apply.md)). Both run the same scripts in `deploy/scripts`.
- **Decisions**: every significant trade-off is an ADR in [`docs/decisions.md`](../decisions.md).

## Glossary

| Term | Meaning |
| --- | --- |
| Platform | Everything that builds, ships and runs a system: repositories, pipelines, release tool, environments, cloud resources |
| System | The software the platform delivers (cmdemo1: the work-orders demo) |
| Deployable | A unit that is released and deployed on its own (ui, dashboard, system) |
| Release candidate (RC) | A versioned build ready for release |
| Change chain | One change followed from work item to production |
| Observation | A `twin.state.observed` event: the twin saw an entity in a given state at a given time |
| Drift | A difference between declared and observed state, or between the twin and its sources |
| Dead letter | An intake message that failed three times and waits for replay |
