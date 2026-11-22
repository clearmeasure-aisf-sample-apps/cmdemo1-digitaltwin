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

Before the mapping passes, these source events reached the journal only as generic fallback types. They now map to the domain events in the tables below:

| Source | Generic type in the journal | Count | Now |
|---|---|---|---|
| Octopus | `twin.octopus.audit.Created` / `Modified` / `Deleted` | 742 / 153 / 66 | `twin.octopus.document.*` |
| Octopus | `twin.octopus.audit.CompliancePolicyEvaluatedCompliant` | 184 | `twin.octopus.compliance.evaluated` |
| Octopus | `twin.octopus.audit.DeploymentPreconditionEvaluated` | 132 | `twin.octopus.deployment.preconditionEvaluated` |
| Octopus | `twin.octopus.audit.*DeletedByRetention` | 86 | `twin.octopus.retention.deleted` |
| Octopus | `twin.octopus.audit.RunbookSnapshotPublished`, `PackageDeleted`, `RateLimitExceeded` | 16, 10, 8 | explicit mappings |
| Azure | `twin.azure.microsoft.resources.resourceactionsuccess` | 74 | `twin.azure.resource.action` |
| GitHub | `twin.github.code_scanning_alert.*`, `repository_ruleset.edited`, `label.created` | 10, 1, 1 | typed by event and action, with domain events and contexts (phase 6) |

## Issues found and fixed

- GitHub `release` with action `deleted` or `unpublished` was journaled as `dev.cdevents.artifact.published`. It is now `dev.cdevents.artifact.deleted`.
- Azure `ResourceDeleteFailure` and the three `*Cancel` types had no explicit mapping. They now map to `twin.azure.resource.failed` and `twin.azure.resource.canceled`.

## High-value events now used (phase 3)

- **Octopus `Modified` / `Created`**: these carry the real change in their related documents. Deployment process edits (`deploymentprocess-*`) and variable sets (`variableset-*`, `LibraryVariableSets-*`) become `twin.octopus.process.changed` and `twin.octopus.variables.changed`. Interruption sign-offs and assignments, with the person and their notes, become `twin.octopus.interruption.submitted` and `.assigned`. Release creation, runbook, trigger and project setting changes get their own types too. `GET /api/config-changes` and MCP `get_config_changes` list the configuration changes.
- **Octopus deployment failures and manual interventions** (`DeploymentFailed`, `GuidedFailureInterruptionRaised`, `ManualInterventionInterruptionRaised`) were already subscribed and mapped. They now also attach to change-chain stages as signals.
- **GitHub check runs, reviews and workflow jobs**: the hooks already receive them (`*`). Reviews, failed jobs and failed checks now attach to the chain's PR and build stages, correlated by `pr:` and `sha:` chain ids.

## Complete coverage (phase 6)

Every documented GitHub event and action and every Octopus category now has a catalog row that the normalizers reproduce; `EveryDocumentedSourceEventHasAMapping` enforces it against the snapshots.
- Events without a hand-written mapping get deterministic types: `twin.github.{event}.{action}` (domain event `<Event><Action>`) and `twin.octopus.{area}.{verb}` (domain event = category). Each has a bounded context; repository and instance administration falls under **Platform Administration**.
- Octopus categories that used the old `twin.octopus.audit.{category}` fallback are now typed. The next rebuild re-types their history.

## Issues flagged, not changed

- Octopus `DailyUsageCaptured` belongs to the `UsageDeclaration` event group, but the subscription excludes categories by name prefix, so it still arrives (about one a day). It is now typed `twin.octopus.usage.captured`. Excluding by event group instead would change the live subscription, so that is left for a deliberate change.
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

#### Other GitHub events (typed by event and action)

Typed as `twin.github.{event}.{action}` with domain event `<Event><Action>` (for example `CodeScanningAlertFixed`). The hooks subscribe to every event.

| Source event | Actions | Bounded context |
|---|---|---|
| `branch_protection_configuration` | `disabled`, `enabled` | Platform Administration |
| `branch_protection_rule` | `created`, `deleted`, `edited` | Platform Administration |
| `code_scanning_alert` | `appeared_in_branch`, `closed_by_user`, `created`, `fixed`, `reopened`, `reopened_by_user`, `updated_assignment` | Reliability & Drift |
| `commit_comment` | `created` | Source & Change |
| `custom_property` | `created`, `deleted`, `promote_to_enterprise`, `updated` | Platform Administration |
| `custom_property_values` | `updated` | Platform Administration |
| `dependabot_alert` | `assignees_changed`, `auto_dismissed`, `auto_reopened`, `created`, `dismissed`, `fixed`, `reintroduced`, `reopened`, `withdrawn` | Reliability & Drift |
| `deploy_key` | `created`, `deleted` | Platform Administration |
| `deployment_protection_rule` | `requested` | Release & Delivery |
| `deployment_review` | `approved`, `rejected`, `requested` | Release & Delivery |
| `discussion` | `answered`, `category_changed`, `closed`, `created`, `deleted`, `edited`, `labeled`, `locked`, `pinned`, `reopened`, `transferred`, `unanswered`, `unlabeled`, `unlocked`, `unpinned` | Work |
| `discussion_comment` | `created`, `deleted`, `edited` | Work |
| `fork` | (no action) | Source & Change |
| `github_app_authorization` | `revoked` | Platform Administration |
| `gollum` | (no action) | Source & Change |
| `installation` | `created`, `deleted`, `new_permissions_accepted`, `suspend`, `unsuspend` | Platform Administration |
| `installation_repositories` | `added`, `removed` | Platform Administration |
| `installation_target` | `renamed` | Platform Administration |
| `issue_dependencies` | `blocked_by_added`, `blocked_by_removed`, `blocking_added`, `blocking_removed` | Work |
| `issue_relates_to` | `relates_to_added`, `relates_to_removed` | Platform Administration |
| `label` | `archived`, `created`, `deleted`, `edited`, `unarchived` | Work |
| `marketplace_purchase` | `cancelled`, `changed`, `pending_change`, `pending_change_cancelled`, `purchased` | Platform Administration |
| `member` | `added`, `edited`, `removed` | Platform Administration |
| `membership` | `added`, `removed` | Platform Administration |
| `merge_group` | `checks_requested`, `destroyed` | Build & Artifact |
| `meta` | `deleted` | Platform Administration |
| `milestone` | `closed`, `created`, `deleted`, `edited`, `opened` | Work |
| `org_block` | `blocked`, `unblocked` | Platform Administration |
| `organization` | `deleted`, `member_added`, `member_invited`, `member_removed`, `renamed` | Platform Administration |
| `page_build` | (no action) | Build & Artifact |
| `personal_access_token_request` | `approved`, `cancelled`, `created`, `denied` | Platform Administration |
| `project` | `closed`, `created`, `deleted`, `edited`, `reopened` | Work |
| `project_card` | `converted`, `created`, `deleted`, `edited`, `moved` | Work |
| `project_column` | `created`, `deleted`, `edited`, `moved` | Work |
| `public` | (no action) | Platform Administration |
| `pull_request_review_comment` | `created`, `deleted`, `edited` | Source & Change |
| `pull_request_review_thread` | `resolved`, `unresolved` | Source & Change |
| `repository` | `archived`, `created`, `deleted`, `edited`, `privatized`, `publicized`, `renamed`, `transferred`, `unarchived` | Platform Administration |
| `repository_advisory` | `published`, `reported` | Reliability & Drift |
| `repository_dispatch` | (no action) | Platform Administration |
| `repository_import` | (no action) | Platform Administration |
| `repository_ruleset` | `created`, `deleted`, `edited` | Platform Administration |
| `repository_vulnerability_alert` | `create`, `dismiss`, `reopen`, `resolve` | Reliability & Drift |
| `secret_scanning_alert` | `assigned`, `created`, `metadata_created`, `metadata_removed`, `publicly_leaked`, `reopened`, `resolved`, `unassigned`, `validated` | Reliability & Drift |
| `secret_scanning_alert_location` | `created` | Reliability & Drift |
| `secret_scanning_scan` | `completed` | Reliability & Drift |
| `security_advisory` | `published`, `updated`, `withdrawn` | Reliability & Drift |
| `security_and_analysis` | (no action) | Reliability & Drift |
| `sponsorship` | `cancelled`, `created`, `edited`, `pending_cancellation`, `pending_tier_change`, `tier_changed` | Platform Administration |
| `star` | `created`, `deleted` | Platform Administration |
| `status` | (no action) | Source & Change |
| `sub_issues` | `parent_issue_added`, `parent_issue_removed`, `sub_issue_added`, `sub_issue_removed` | Work |
| `team` | `added_to_repository`, `created`, `deleted`, `edited`, `removed_from_repository` | Platform Administration |
| `team_add` | (no action) | Platform Administration |
| `watch` | `started` | Platform Administration |
| `workflow_dispatch` | (no action) | Build & Artifact |

### Octopus audit events (category; event groups from `/api/events/groups`)

| Category | Event groups | Twin event type | Domain event | Bounded context |
|---|---|---|---|---|
| `DeploymentSucceeded` | Deployment | `dev.cdevents.service.deployed` | ServiceDeployed | Release & Delivery |
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

#### Other Octopus categories (typed by area and verb)

Typed as `twin.octopus.{area}.{verb}` with the category as the domain event. Instance-level categories are not requested by the space subscription (see below), but are typed if they arrive.

| Category | Event groups | Twin event type | Bounded context | Subscribed |
|---|---|---|---|---|
| `DeploymentQueued` | Deployment | `twin.octopus.deployment.queued` | Release & Delivery | yes |
| `DeploymentStarted` | Deployment | `twin.octopus.deployment.started` | Release & Delivery | yes |
| `DeploymentFailed` | Deployment, DeploymentCritical | `twin.octopus.deployment.failed` | Release & Delivery | yes |
| `DeploymentCanceled` | - | `twin.octopus.deployment.canceled` | Release & Delivery | yes |
| `DeploymentResumed` | - | `twin.octopus.deployment.resumed` | Release & Delivery | yes |
| `DeploymentPreconditionEvaluated` | - | `twin.octopus.deployment.preconditionEvaluated` | Release & Delivery | yes |
| `TaskCanceled` | - | `twin.octopus.task.canceled` | Release & Delivery | yes |
| `RateLimitExceeded` | - | `twin.octopus.rateLimit.exceeded` | Observation | yes |
| `ApiKeyExpired` | ApiKeyExpiry | `twin.octopus.apiKey.expired` | Platform Administration | no (instance-level) |
| `ApiKeyExpiryFirstWarning` | ApiKeyExpiry | `twin.octopus.apiKey.expiryFirstWarning` | Platform Administration | no (instance-level) |
| `ApiKeyExpirySecondWarning` | ApiKeyExpiry | `twin.octopus.apiKey.expirySecondWarning` | Platform Administration | no (instance-level) |
| `ArgoCDApplicationSyncInterruptionRaised` | - | `twin.octopus.argoCD.syncInterruptionRaised` | Runtime & Platform | yes |
| `ArtifactContentModified` | - | `twin.octopus.artifact.contentModified` | Build & Artifact | yes |
| `AttachmentsDeleted` | - | `twin.octopus.attachment.deleted` | Release & Delivery | yes |
| `BuildInformationDeleted` | BuildInformation | `twin.octopus.buildInformation.deleted` | Build & Artifact | yes |
| `BuildInformationModified` | BuildInformation | `twin.octopus.buildInformation.modified` | Build & Artifact | yes |
| `CertificateExpired` | CertificateExpiry | `twin.octopus.certificate.expired` | Platform Administration | no (instance-level) |
| `CertificateExpiryFirstWarning` | CertificateExpiry | `twin.octopus.certificate.expiryFirstWarning` | Platform Administration | no (instance-level) |
| `CertificateExpirySecondWarning` | CertificateExpiry | `twin.octopus.certificate.expirySecondWarning` | Platform Administration | no (instance-level) |
| `CertificatePrivateKeyExported` | - | `twin.octopus.certificate.privateKeyExported` | Platform Administration | no (instance-level) |
| `CertificateReplaced` | - | `twin.octopus.certificate.replaced` | Platform Administration | no (instance-level) |
| `Comment` | - | `twin.octopus.document.comment` | Release & Delivery | yes |
| `DailyUsageCaptured` | UsageDeclaration | `twin.octopus.usage.captured` | Platform Administration | yes |
| `Downloaded` | - | `twin.octopus.document.downloaded` | Release & Delivery | yes |
| `EphemeralEnvironmentDeploymentCreationFailed` | - | `twin.octopus.ephemeralDeployment.creationFailed` | Runtime & Platform | yes |
| `Exported` | - | `twin.octopus.document.exported` | Release & Delivery | yes |
| `Imported` | - | `twin.octopus.document.imported` | Release & Delivery | yes |
| `IpAddressBanned` | AuthenticationBanned | `twin.octopus.ipAddress.banned` | Platform Administration | yes |
| `KubernetesResourceDeletionFailed` | - | `twin.octopus.kubernetes.deletionFailed` | Runtime & Platform | yes |
| `KubernetesResourceDeletionSucceeded` | - | `twin.octopus.kubernetes.deletionSucceeded` | Runtime & Platform | yes |
| `KubernetesResourceMonitoringStopped` | - | `twin.octopus.kubernetes.monitoringStopped` | Runtime & Platform | yes |
| `KubernetesResourceVerificationInterruptionRaised` | - | `twin.octopus.kubernetes.verificationInterruptionRaised` | Runtime & Platform | yes |
| `LicenseKeyExpired` | LicenseKeyExpiry | `twin.octopus.license.expired` | Platform Administration | no (instance-level) |
| `LicenseKeyExpiryFirstWarning` | LicenseKeyExpiry | `twin.octopus.license.expiryFirstWarning` | Platform Administration | no (instance-level) |
| `LicenseKeyExpirySecondWarning` | LicenseKeyExpiry | `twin.octopus.license.expirySecondWarning` | Platform Administration | no (instance-level) |
| `LicenseUsageAtLimit` | LicenseUsage | `twin.octopus.license.atLimit` | Platform Administration | no (instance-level) |
| `LicenseUsageFirstWarning` | LicenseUsage | `twin.octopus.license.firstWarning` | Platform Administration | no (instance-level) |
| `LicenseUsageSecondWarning` | LicenseUsage | `twin.octopus.license.secondWarning` | Platform Administration | no (instance-level) |
| `LicenseUsageThirdWarning` | LicenseUsage | `twin.octopus.license.thirdWarning` | Platform Administration | no (instance-level) |
| `LoginBanned` | AuthenticationBanned | `twin.octopus.login.banned` | Platform Administration | no (instance-level) |
| `LoginChangedIdentityClaims` | - | `twin.octopus.login.changedIdentityClaims` | Platform Administration | no (instance-level) |
| `LoginCouldNotChangeIdentityClaims` | - | `twin.octopus.login.couldNotChangeIdentityClaims` | Platform Administration | no (instance-level) |
| `LoginFailed` | - | `twin.octopus.login.failed` | Platform Administration | no (instance-level) |
| `LoginMatchedByClaimSetThroughTheApi` | - | `twin.octopus.login.matchedByClaimSetThroughTheApi` | Platform Administration | no (instance-level) |
| `LoginRefusedForConflictingUsers` | - | `twin.octopus.login.refusedForConflictingUsers` | Platform Administration | no (instance-level) |
| `LoginRefusedForUserLinkedToAnotherSubject` | - | `twin.octopus.login.refusedForUserLinkedToAnotherSubject` | Platform Administration | no (instance-level) |
| `LoginRelinkedUserLinkedToAnotherSubject` | - | `twin.octopus.login.relinkedUserLinkedToAnotherSubject` | Platform Administration | no (instance-level) |
| `LoginSucceeded` | - | `twin.octopus.login.succeeded` | Platform Administration | no (instance-level) |
| `LoginTrustedUnverifiedEmail` | - | `twin.octopus.login.trustedUnverifiedEmail` | Platform Administration | no (instance-level) |
| `MachineAdded` | Machine | `twin.octopus.machine.added` | Runtime & Platform | yes |
| `MachineCleanupFailed` | Machine, MachineCritical | `twin.octopus.machine.cleanupFailed` | Runtime & Platform | yes |
| `MachineDeleted` | Machine | `twin.octopus.machine.deleted` | Runtime & Platform | yes |
| `MachineDeploymentRelatedPropertyWasUpdated` | Machine | `twin.octopus.machine.deploymentRelatedPropertyWasUpdated` | Runtime & Platform | yes |
| `MachineDisabled` | Machine, MachineUnavailableForDeployment | `twin.octopus.machine.disabled` | Runtime & Platform | yes |
| `MachineEnabled` | Machine, MachineAvailableForDeployment | `twin.octopus.machine.enabled` | Runtime & Platform | yes |
| `MachineHasWarnings` | Machine, MachineAvailableForDeployment, MachineHealthChanged | `twin.octopus.machine.hasWarnings` | Runtime & Platform | yes |
| `MachineHealthy` | Machine, MachineAvailableForDeployment, MachineHealthChanged | `twin.octopus.machine.healthy` | Runtime & Platform | yes |
| `MachineUnavailable` | Machine, MachineCritical, MachineHealthChanged, MachineUnavailableForDeployment | `twin.octopus.machine.unavailable` | Runtime & Platform | yes |
| `MachineUnhealthy` | Machine, MachineHealthChanged, MachineUnavailableForDeployment | `twin.octopus.machine.unhealthy` | Runtime & Platform | yes |
| `McpOAuthAuthorizationCodeIssuanceFailed` | McpOAuth, McpOAuthCritical | `twin.octopus.mcpOAuth.authorizationCodeIssuanceFailed` | Platform Administration | no (instance-level) |
| `McpOAuthAuthorizationCodeIssued` | McpOAuth | `twin.octopus.mcpOAuth.authorizationCodeIssued` | Platform Administration | no (instance-level) |
| `McpOAuthAuthorizationRequestRefused` | McpOAuth, McpOAuthCritical | `twin.octopus.mcpOAuth.authorizationRequestRefused` | Platform Administration | no (instance-level) |
| `McpOAuthTokenIssuanceFailed` | McpOAuth, McpOAuthCritical | `twin.octopus.mcpOAuth.tokenIssuanceFailed` | Platform Administration | no (instance-level) |
| `McpOAuthTokenIssued` | McpOAuth | `twin.octopus.mcpOAuth.tokenIssued` | Platform Administration | no (instance-level) |
| `McpOAuthTokenRevocationRefused` | McpOAuth, McpOAuthCritical | `twin.octopus.mcpOAuth.tokenRevocationRefused` | Platform Administration | no (instance-level) |
| `McpOAuthTokenRevoked` | McpOAuth | `twin.octopus.mcpOAuth.tokenRevoked` | Platform Administration | no (instance-level) |
| `OAuthRedirectUriNotAllowed` | McpOAuth | `twin.octopus.oauth.redirectUriNotAllowed` | Platform Administration | yes |
| `PackageReplaced` | - | `twin.octopus.package.replaced` | Build & Artifact | yes |
| `ProjectExportCompleted` | - | `twin.octopus.projectExport.completed` | Release & Delivery | yes |
| `ProjectExportQueued` | - | `twin.octopus.projectExport.queued` | Release & Delivery | yes |
| `ProjectImportCompleted` | - | `twin.octopus.projectImport.completed` | Release & Delivery | yes |
| `ProjectImportQueued` | - | `twin.octopus.projectImport.queued` | Release & Delivery | yes |
| `PullRequestCompletionInterruptionRaised` | - | `twin.octopus.pullRequest.interruptionRaised` | Release & Delivery | yes |
| `RunbookRunResumed` | - | `twin.octopus.runbookRun.resumed` | Release & Delivery | yes |
| `SigningKeyRotationFirstWarning` | SigningKeyRotation | `twin.octopus.signingKey.firstWarning` | Platform Administration | no (instance-level) |
| `SigningKeyRotationSecondWarning` | SigningKeyRotation | `twin.octopus.signingKey.secondWarning` | Platform Administration | no (instance-level) |
| `SigningKeyRotationThirdWarning` | SigningKeyRotation | `twin.octopus.signingKey.thirdWarning` | Platform Administration | no (instance-level) |
| `UsageDeclarationDownloaded` | UsageDeclaration | `twin.octopus.usageDeclaration.downloaded` | Platform Administration | no (instance-level) |
| `UsageDeclarationDueFirstWarning` | UsageDeclaration | `twin.octopus.usageDeclaration.dueFirstWarning` | Platform Administration | no (instance-level) |
| `UsageDeclarationDueSecondWarning` | UsageDeclaration | `twin.octopus.usageDeclaration.dueSecondWarning` | Platform Administration | no (instance-level) |
| `UsageDeclarationDueThirdWarning` | UsageDeclaration | `twin.octopus.usageDeclaration.dueThirdWarning` | Platform Administration | no (instance-level) |
| `UsageDeclarationOverdue` | UsageDeclaration | `twin.octopus.usageDeclaration.overdue` | Platform Administration | no (instance-level) |
| `UsageDeclarationProcessed` | UsageDeclaration | `twin.octopus.usageDeclaration.processed` | Platform Administration | no (instance-level) |
| `UsageDeclarationReceiptForUnknownDeclaration` | UsageDeclaration | `twin.octopus.usageDeclaration.receiptForUnknownDeclaration` | Platform Administration | no (instance-level) |
| `UsageDeclarationReceiptVerificationFailed` | UsageDeclaration | `twin.octopus.usageDeclaration.receiptVerificationFailed` | Platform Administration | no (instance-level) |
| `UsageDeclarationUsageDataAltered` | UsageDeclaration | `twin.octopus.usageDeclaration.usageDataAltered` | Platform Administration | no (instance-level) |
| `UserPasswordChanged` | - | `twin.octopus.user.changed` | Platform Administration | yes |
| `WebhookTriggerRejected` | - | `twin.octopus.webhookTrigger.rejected` | Release & Delivery | yes |
| `WebhookTriggeredRunbookRunFailed` | - | `twin.octopus.webhookTrigger.failed` | Release & Delivery | yes |

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

### Coverage and what is dropped

**GitHub:** all 73 documented events and every documented action have a mapping. Nothing is dropped.

**Octopus:** all 115 categories have a mapping. 44 instance-level categories are not requested by the space subscription (`Login`, `License`, `UsageDeclaration`, `ApiKey`, `Certificate`, `SigningKey`, `McpOAuth`): they are about the Octopus instance rather than delivery, and carry user names and IP addresses.

**Octopus, dropped by message filter:** audit events whose message contains `Ran task Process subscriptions` or `ServerTaskProcessExecution`, and `OIDC token exchanged` events that do not mention cmdemo1 (the twin's own subscription and token traffic).

**Azure:** none. The Event Grid subscriptions deliver exactly the nine documented resource-group event types, and all nine are mapped.
<!-- generated:end -->
