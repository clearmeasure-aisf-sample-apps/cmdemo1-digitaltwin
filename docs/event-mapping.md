# Domain Event Mapping (DDD v0.2)

This maps every twin domain event to the exact source events that produce it, across the eight bounded contexts in the README. The same table lives in code as `SourceEventCatalog` (`src/DigitalTwin.Core/Normalization`), and `EventMappingTests` keeps the two in step:

- Every row runs through the real normalizer and must produce its twin event type.
- Every source event, action and category name must appear in the official-name snapshots in `docs/sources`.
- Every documented action of a mapped GitHub event must have a row, so no action silently falls through.
- The tables below are generated from the catalog; the test fails if they drift.

## Where the names were verified (2026-11-22)

| Source | Official reference | Snapshot |
|---|---|---|
| GitHub webhooks | [Webhook events and payloads](https://docs.github.com/en/webhooks/webhook-events-and-payloads): 73 events, every action, read from the docs site's own webhook data API | `docs/sources/github-webhook-events.json` |
| Octopus Deploy | `GET /api/events/categories` and `GET /api/events/groups` on our instance (clearmeasure.octopus.app), read-only: 115 categories, 22 event groups | `docs/sources/octopus-event-categories.json` |
| Azure Event Grid | [Azure resource group as an Event Grid source](https://learn.microsoft.com/en-us/azure/event-grid/event-schema-resource-groups): 9 `Microsoft.Resources.*` event types | `docs/sources/azure-resource-event-types.json` |

## What the live twin received (journal on 2026-11-22, about one day of history)

Before this mapping pass, these source events reached the journal only as generic fallback types. They now map to the domain events in the tables below:

| Source | Generic type in the journal | Count | Now |
|---|---|---|---|
| Octopus | `twin.octopus.audit.Created` / `Modified` / `Deleted` | 742 / 153 / 66 | `twin.octopus.document.*` |
| Octopus | `twin.octopus.audit.CompliancePolicyEvaluatedCompliant` | 184 | `twin.octopus.compliance.evaluated` |
| Octopus | `twin.octopus.audit.DeploymentPreconditionEvaluated` | 132 | `twin.octopus.deployment.preconditionEvaluated` |
| Octopus | `twin.octopus.audit.*DeletedByRetention` | 86 | `twin.octopus.retention.deleted` |
| Octopus | `twin.octopus.audit.RunbookSnapshotPublished`, `PackageDeleted`, `RateLimitExceeded` | 16, 10, 8 | explicit mappings |
| Azure | `twin.azure.microsoft.resources.resourceactionsuccess` | 74 | `twin.azure.resource.action` |
| GitHub | `twin.github.code_scanning_alert.*`, `repository_ruleset.edited`, `label.created` | 10, 1, 1 | still generic (see below) |

## Issues found and fixed

- GitHub `release` with action `deleted` or `unpublished` was journaled as `dev.cdevents.artifact.published`. It is now `dev.cdevents.artifact.deleted`.
- Azure `ResourceDeleteFailure` and the three `*Cancel` types had no explicit mapping. They now map to `twin.azure.resource.failed` and `twin.azure.resource.canceled`.

## High-value events now used (phase 3)

- **Octopus `Modified` / `Created`**: these carry the real change in their related documents. Deployment process edits (`deploymentprocess-*`) and variable sets (`variableset-*`, `LibraryVariableSets-*`) become `twin.octopus.process.changed` and `twin.octopus.variables.changed`. Interruption sign-offs and assignments, with the person and their notes, become `twin.octopus.interruption.submitted` and `.assigned`. Release creation, runbook, trigger and project setting changes get their own types too. `GET /api/config-changes` and MCP `get_config_changes` list the configuration changes.
- **Octopus deployment failures and manual interventions** (`DeploymentFailed`, `GuidedFailureInterruptionRaised`, `ManualInterventionInterruptionRaised`) were already subscribed and mapped. They now also attach to change-chain stages as signals.
- **GitHub check runs, reviews and workflow jobs**: the hooks already receive them (`*`). Reviews, failed jobs and failed checks now attach to the chain's PR and build stages, correlated by `pr:` and `sha:` chain ids.

## Issues flagged, not changed

- Octopus `DailyUsageCaptured` belongs to the `UsageDeclaration` event group, but the subscription excludes categories by name prefix, so it still arrives (journaled generically, about one a day). Excluding by event group instead would change the live subscription, so that is left for a deliberate change.
- Several twin event types use CDEvents-style names that the CDEvents spec does not define: `taskRun.queued`/`waiting`, `build.created`/`requested`/`rerequested`/`requested_action`. They are kept so existing journal history stays queryable, and are listed here as twin extensions.

## Mapping

<!-- generated:start (EventMappingTests; TWIN_UPDATE_DOCS=1 regenerates) -->
### GitHub webhooks (`X-GitHub-Event` + `action`)

| Source event | Actions / condition | Twin event type | Domain event | Bounded context |
|---|---|---|---|---|
| `ping` | (no action) | `twin.github.webhook.ping` | WebhookVerified | Observation |
| `push` | (no action) | `dev.cdevents.repository.modified` | CodePushed | Source & Change |
| `pull_request` | `opened`, `reopened` | `dev.cdevents.change.created` | ChangeProposed | Source & Change |
| `pull_request` | `closed` when `pull_request.merged = true` | `dev.cdevents.change.merged` | ChangeMerged | Source & Change |
| `pull_request` | `closed` when `pull_request.merged = false` | `dev.cdevents.change.abandoned` | ChangeAbandoned | Source & Change |
| `pull_request` | `assigned`, `auto_merge_disabled`, `auto_merge_enabled`, `converted_to_draft`, `demilestoned`, `dequeued`, `edited`, `enqueued`, `labeled`, `locked`, `milestoned`, `ready_for_review`, `review_request_removed`, `review_requested`, `stacked`, `synchronize`, `unassigned`, `unlabeled`, `unlocked` | `dev.cdevents.change.updated` | ChangeUpdated | Source & Change |
| `pull_request_review` | `submitted`, `edited`, `dismissed` | `dev.cdevents.change.reviewed` | ChangeReviewed | Source & Change |
| `create` | (no action) when `ref_type = branch` | `dev.cdevents.branch.created` | BranchCreated | Source & Change |
| `create` | (no action) when `ref_type = tag` | `dev.cdevents.tag.created` | TagCreated | Source & Change |
| `delete` | (no action) when `ref_type = branch` | `dev.cdevents.branch.deleted` | BranchDeleted | Source & Change |
| `delete` | (no action) when `ref_type = tag` | `dev.cdevents.tag.deleted` | TagDeleted | Source & Change |
| `workflow_run` | `requested` | `dev.cdevents.pipelineRun.queued` | PipelineRunQueued | Build & Artifact |
| `workflow_run` | `in_progress` | `dev.cdevents.pipelineRun.started` | PipelineRunStarted | Build & Artifact |
| `workflow_run` | `completed` | `dev.cdevents.pipelineRun.finished` | PipelineRunFinished | Build & Artifact |
| `workflow_job` | `queued` | `dev.cdevents.taskRun.queued` | TaskRunQueued | Build & Artifact |
| `workflow_job` | `waiting` | `dev.cdevents.taskRun.waiting` | TaskRunWaiting | Build & Artifact |
| `workflow_job` | `in_progress` | `dev.cdevents.taskRun.started` | TaskRunStarted | Build & Artifact |
| `workflow_job` | `completed` | `dev.cdevents.taskRun.finished` | TaskRunFinished | Build & Artifact |
| `check_run` | `created` | `dev.cdevents.build.created` | CheckRunCreated | Build & Artifact |
| `check_run` | `rerequested` | `dev.cdevents.build.rerequested` | CheckRunRerequested | Build & Artifact |
| `check_run` | `requested_action` | `dev.cdevents.build.requested_action` | CheckRunActionRequested | Build & Artifact |
| `check_run` | `completed` | `dev.cdevents.build.finished` | BuildFinished | Build & Artifact |
| `check_suite` | `requested` | `dev.cdevents.build.requested` | CheckSuiteRequested | Build & Artifact |
| `check_suite` | `rerequested` | `dev.cdevents.build.rerequested` | CheckSuiteRerequested | Build & Artifact |
| `check_suite` | `completed` | `dev.cdevents.build.finished` | BuildFinished | Build & Artifact |
| `release` | `created`, `edited`, `prereleased`, `published`, `released` | `dev.cdevents.artifact.published` | ArtifactPublished | Build & Artifact |
| `release` | `deleted`, `unpublished` | `dev.cdevents.artifact.deleted` | ArtifactWithdrawn | Build & Artifact |
| `package` | `published`, `updated` | `dev.cdevents.artifact.published` | ArtifactPublished | Build & Artifact |
| `registry_package` | `published`, `updated` | `dev.cdevents.artifact.published` | ArtifactPublished | Build & Artifact |
| `deployment` | `created` | `twin.github.deployment` | GitHubDeploymentCreated | Release & Delivery |
| `deployment_status` | `created` | `twin.github.deployment_status` | GitHubDeploymentStatusReported | Release & Delivery |
| `issues` | `opened` | `dev.cdevents.ticket.created` | WorkItemOpened | Work |
| `issues` | `closed` | `dev.cdevents.ticket.closed` | WorkItemClosed | Work |
| `issues` | `assigned`, `deleted`, `demilestoned`, `edited`, `field_added`, `field_removed`, `labeled`, `locked`, `milestoned`, `pinned`, `reopened`, `status_changed`, `transferred`, `typed`, `unassigned`, `unlabeled`, `unlocked`, `unpinned`, `untyped` | `dev.cdevents.ticket.updated` | WorkItemUpdated | Work |
| `issue_comment` | `created`, `edited`, `deleted`, `pinned`, `unpinned` | `dev.cdevents.ticket.updated` | WorkItemUpdated | Work |

### Octopus audit events (category; event groups from `/api/events/groups`)

| Category | Event groups | Twin event type | Domain event | Bounded context |
|---|---|---|---|---|
| `DeploymentQueued` | Deployment | `twin.octopus.deployment.queued` | DeploymentQueued | Release & Delivery |
| `DeploymentStarted` | Deployment | `twin.octopus.deployment.started` | DeploymentStarted | Release & Delivery |
| `DeploymentSucceeded` | Deployment | `dev.cdevents.service.deployed` | ServiceDeployed | Release & Delivery |
| `DeploymentFailed` | Deployment, DeploymentCritical | `twin.octopus.deployment.failed` | DeploymentFailed | Release & Delivery |
| `DeploymentCanceled` | - | `twin.octopus.deployment.canceled` | DeploymentCanceled | Release & Delivery |
| `DeploymentResumed` | - | `twin.octopus.deployment.resumed` | DeploymentResumed | Release & Delivery |
| `DeploymentPreconditionEvaluated` | - | `twin.octopus.deployment.preconditionEvaluated` | DeploymentPreconditionEvaluated | Release & Delivery |
| `ManualInterventionInterruptionRaised` | Deployment, DeploymentCritical | `twin.octopus.interruption.manual` | ManualInterventionRequested | Release & Delivery |
| `GuidedFailureInterruptionRaised` | Deployment, DeploymentCritical | `twin.octopus.interruption.guidedFailure` | GuidedFailureRaised | Release & Delivery |
| `AutoDeployTriggerSucceeded` | AutoDeploy | `twin.octopus.autoDeploy.succeeded` | AutoDeployTriggered | Release & Delivery |
| `AutoDeployTriggerBlocked` | AutoDeploy, AutoDeployCritical | `twin.octopus.autoDeploy.blocked` | AutoDeployBlocked | Release & Delivery |
| `CompliancePolicyEvaluatedCompliant` | CompliancePolicyEvaluated | `twin.octopus.compliance.evaluated` | CompliancePolicyEvaluated | Release & Delivery |
| `CompliancePolicyEvaluatedNonCompliantBlocked` | CompliancePolicyEvaluated | `twin.octopus.compliance.evaluated` | CompliancePolicyEvaluated | Release & Delivery |
| `CompliancePolicyEvaluatedNonCompliantWarned` | CompliancePolicyEvaluated | `twin.octopus.compliance.evaluated` | CompliancePolicyEvaluated | Release & Delivery |
| `RunbookRunQueued` | - | `twin.octopus.runbook.queued` | RunbookRunQueued | Release & Delivery |
| `RunbookRunStarted` | - | `twin.octopus.runbook.started` | RunbookRunStarted | Release & Delivery |
| `RunbookRunSucceeded` | - | `twin.octopus.runbook.finished` | RunbookRunFinished | Release & Delivery |
| `RunbookRunFailed` | - | `twin.octopus.runbook.finished` | RunbookRunFinished | Release & Delivery |
| `RunbookSnapshotPublished` | - | `twin.octopus.runbook.snapshotPublished` | RunbookSnapshotPublished | Release & Delivery |
| `TaskCanceled` | - | `twin.octopus.task.canceled` | TaskCanceled | Release & Delivery |
| `PackageAdded` | - | `dev.cdevents.artifact.published` | ArtifactPublished | Build & Artifact |
| `PackageDeleted` | - | `dev.cdevents.artifact.deleted` | ArtifactWithdrawn | Build & Artifact |
| `BuildInformationCreated` | BuildInformation | `twin.octopus.buildInformation.created` | BuildProvenanceRecorded | Build & Artifact |
| `PackageDeletedByRetention` | - | `twin.octopus.retention.deleted` | RetentionApplied | Release & Delivery |
| `PackageDeletedByRepositoryReIndexing` | - | `twin.octopus.retention.deleted` | RetentionApplied | Release & Delivery |
| `ReleaseDeletedByRetention` | - | `twin.octopus.retention.deleted` | RetentionApplied | Release & Delivery |
| `RunbookRunDeletedByRetention` | - | `twin.octopus.retention.deleted` | RetentionApplied | Release & Delivery |
| `RunbookSnapshotDeletedByRetention` | - | `twin.octopus.retention.deleted` | RetentionApplied | Release & Delivery |
| `EphemeralEnvironmentCreationSucceeded` | EphemeralEnvironments | `dev.cdevents.environment.created` | EnvironmentCreated | Runtime & Platform |
| `EphemeralEnvironmentCreationFailed` | EphemeralEnvironments | `twin.octopus.ephemeral.failed` | EnvironmentCreationFailed | Runtime & Platform |
| `Modified` when `related = deploymentprocess-Projects-1` | Document | `twin.octopus.process.changed` | DeploymentProcessChanged | Release & Delivery |
| `Modified` when `related = variableset-Projects-1` | Document | `twin.octopus.variables.changed` | VariablesChanged | Release & Delivery |
| `Modified` when `related = LibraryVariableSets-1` | Document | `twin.octopus.variables.changed` | VariablesChanged | Release & Delivery |
| `Modified` when `related = Interruptions-1; message = Submitted interruption Sign-off with notes: ok` | Document | `twin.octopus.interruption.submitted` | ManualInterventionSignedOff | Release & Delivery |
| `Modified` when `related = Interruptions-1; message = Took responsibility for interruption Sign-off` | Document | `twin.octopus.interruption.assigned` | ManualInterventionAssigned | Release & Delivery |
| `Modified` when `related = Runbooks-1` | Document | `twin.octopus.runbook.changed` | RunbookChanged | Release & Delivery |
| `Modified` when `related = ProjectTriggers-1` | Document | `twin.octopus.trigger.changed` | TriggerChanged | Release & Delivery |
| `Modified` when `related = Projects-1` | Document | `twin.octopus.project.changed` | ProjectSettingsChanged | Release & Delivery |
| `Created` when `related = Releases-1` | Document | `twin.octopus.release.created` | ReleaseCreated | Release & Delivery |
| `Created` when `related = deploymentprocess-Projects-1` | Document | `twin.octopus.process.changed` | DeploymentProcessChanged | Release & Delivery |
| `Created` when `related = variableset-Projects-1` | Document | `twin.octopus.variables.changed` | VariablesChanged | Release & Delivery |
| `Created` | Document | `twin.octopus.document.created` | DeliveryConfigurationCreated | Release & Delivery |
| `Modified` | Document | `twin.octopus.document.modified` | DeliveryConfigurationModified | Release & Delivery |
| `Deleted` | Document | `twin.octopus.document.deleted` | DeliveryConfigurationDeleted | Release & Delivery |
| `RateLimitExceeded` | - | `twin.octopus.rateLimit.exceeded` | RateLimitExceeded | Observation |

### Azure (Event Grid resource-group events, Resource Graph changes) and twin-internal events

| Source | Source event | Twin event type | Domain event | Bounded context |
|---|---|---|---|---|
| AzureEventGrid | `Microsoft.Resources.ResourceWriteSuccess` | `dev.cdevents.environment.modified` | ResourceChanged | Runtime & Platform |
| AzureEventGrid | `Microsoft.Resources.ResourceDeleteSuccess` | `dev.cdevents.environment.deleted` | ResourceDeleted | Runtime & Platform |
| AzureEventGrid | `Microsoft.Resources.ResourceActionSuccess` | `twin.azure.resource.action` | ResourceActionPerformed | Runtime & Platform |
| AzureEventGrid | `Microsoft.Resources.ResourceWriteFailure` | `twin.azure.resource.failed` | ResourceOperationFailed | Runtime & Platform |
| AzureEventGrid | `Microsoft.Resources.ResourceDeleteFailure` | `twin.azure.resource.failed` | ResourceOperationFailed | Runtime & Platform |
| AzureEventGrid | `Microsoft.Resources.ResourceActionFailure` | `twin.azure.resource.failed` | ResourceOperationFailed | Runtime & Platform |
| AzureEventGrid | `Microsoft.Resources.ResourceWriteCancel` | `twin.azure.resource.canceled` | ResourceOperationCanceled | Runtime & Platform |
| AzureEventGrid | `Microsoft.Resources.ResourceDeleteCancel` | `twin.azure.resource.canceled` | ResourceOperationCanceled | Runtime & Platform |
| AzureEventGrid | `Microsoft.Resources.ResourceActionCancel` | `twin.azure.resource.canceled` | ResourceOperationCanceled | Runtime & Platform |
| AzureResourceGraph | `resourcechanges` `changeType = Create` | `twin.azure.resource.create` | ResourceCreated | Runtime & Platform |
| AzureResourceGraph | `resourcechanges` `changeType = Update` | `twin.azure.resource.update` | ResourceChanged | Runtime & Platform |
| AzureResourceGraph | `resourcechanges` `changeType = Delete` | `twin.azure.resource.delete` | ResourceDeleted | Runtime & Platform |
| AzureResourceGraph | `Container App revision poll` | `twin.azure.revision.changed` | WorkloadRevisionChanged | Runtime & Platform |
| Twin | `drift evaluation` | `twin.drift.detected` | DriftDetected | Reliability & Drift |
| Twin | `poller observation` | `twin.state.observed` | StateObserved | Observation |

### Source events without a domain mapping

**GitHub, 56 of 73 documented events.** The hooks subscribe to every event, so these are received and journaled under the generic type `twin.github.{event}.{action}`, but no read model or domain rule uses them:

`branch_protection_configuration`, `branch_protection_rule`, `code_scanning_alert`, `commit_comment`, `custom_property`, `custom_property_values`, `dependabot_alert`, `deploy_key`, `deployment_protection_rule`, `deployment_review`, `discussion`, `discussion_comment`, `fork`, `github_app_authorization`, `gollum`, `installation`, `installation_repositories`, `installation_target`, `issue_dependencies`, `issue_relates_to`, `label`, `marketplace_purchase`, `member`, `membership`, `merge_group`, `meta`, `milestone`, `org_block`, `organization`, `page_build`, `personal_access_token_request`, `project`, `project_card`, `project_column`, `public`, `pull_request_review_comment`, `pull_request_review_thread`, `repository`, `repository_advisory`, `repository_dispatch`, `repository_import`, `repository_ruleset`, `repository_vulnerability_alert`, `secret_scanning_alert`, `secret_scanning_alert_location`, `secret_scanning_scan`, `security_advisory`, `security_and_analysis`, `sponsorship`, `star`, `status`, `sub_issues`, `team`, `team_add`, `watch`, `workflow_dispatch`.

**Octopus, dropped: 44 of 115 categories are never requested.** The subscription excludes instance-level prefixes (`Login`, `License`, `UsageDeclaration`, `ApiKey`, `Certificate`, `SigningKey`, `McpOAuth`):

`ApiKeyExpired`, `ApiKeyExpiryFirstWarning`, `ApiKeyExpirySecondWarning`, `CertificateExpired`, `CertificateExpiryFirstWarning`, `CertificateExpirySecondWarning`, `CertificatePrivateKeyExported`, `CertificateReplaced`, `LicenseKeyExpired`, `LicenseKeyExpiryFirstWarning`, `LicenseKeyExpirySecondWarning`, `LicenseUsageAtLimit`, `LicenseUsageFirstWarning`, `LicenseUsageSecondWarning`, `LicenseUsageThirdWarning`, `LoginBanned`, `LoginChangedIdentityClaims`, `LoginCouldNotChangeIdentityClaims`, `LoginFailed`, `LoginMatchedByClaimSetThroughTheApi`, `LoginRefusedForConflictingUsers`, `LoginRefusedForUserLinkedToAnotherSubject`, `LoginRelinkedUserLinkedToAnotherSubject`, `LoginSucceeded`, `LoginTrustedUnverifiedEmail`, `McpOAuthAuthorizationCodeIssuanceFailed`, `McpOAuthAuthorizationCodeIssued`, `McpOAuthAuthorizationRequestRefused`, `McpOAuthTokenIssuanceFailed`, `McpOAuthTokenIssued`, `McpOAuthTokenRevocationRefused`, `McpOAuthTokenRevoked`, `SigningKeyRotationFirstWarning`, `SigningKeyRotationSecondWarning`, `SigningKeyRotationThirdWarning`, `UsageDeclarationDownloaded`, `UsageDeclarationDueFirstWarning`, `UsageDeclarationDueSecondWarning`, `UsageDeclarationDueThirdWarning`, `UsageDeclarationOverdue`, `UsageDeclarationProcessed`, `UsageDeclarationReceiptForUnknownDeclaration`, `UsageDeclarationReceiptVerificationFailed`, `UsageDeclarationUsageDataAltered`.

**Octopus, generic: 37 subscribed categories have no domain mapping.** They are journaled as `twin.octopus.audit.{category}`:

`ArgoCDApplicationSyncInterruptionRaised`, `ArtifactContentModified`, `AttachmentsDeleted`, `BuildInformationDeleted`, `BuildInformationModified`, `Comment`, `DailyUsageCaptured`, `Downloaded`, `EphemeralEnvironmentDeploymentCreationFailed`, `Exported`, `Imported`, `IpAddressBanned`, `KubernetesResourceDeletionFailed`, `KubernetesResourceDeletionSucceeded`, `KubernetesResourceMonitoringStopped`, `KubernetesResourceVerificationInterruptionRaised`, `MachineAdded`, `MachineCleanupFailed`, `MachineDeleted`, `MachineDeploymentRelatedPropertyWasUpdated`, `MachineDisabled`, `MachineEnabled`, `MachineHasWarnings`, `MachineHealthy`, `MachineUnavailable`, `MachineUnhealthy`, `OAuthRedirectUriNotAllowed`, `PackageReplaced`, `ProjectExportCompleted`, `ProjectExportQueued`, `ProjectImportCompleted`, `ProjectImportQueued`, `PullRequestCompletionInterruptionRaised`, `RunbookRunResumed`, `UserPasswordChanged`, `WebhookTriggerRejected`, `WebhookTriggeredRunbookRunFailed`.

**Octopus, dropped by message filter:** audit events whose message contains `Ran task Process subscriptions` or `ServerTaskProcessExecution`, and `OIDC token exchanged` events that do not mention cmdemo1 (the twin's own subscription and token traffic).

**Azure:** none. The Event Grid subscriptions deliver exactly the nine documented resource-group event types, and all nine are mapped.
<!-- generated:end -->
