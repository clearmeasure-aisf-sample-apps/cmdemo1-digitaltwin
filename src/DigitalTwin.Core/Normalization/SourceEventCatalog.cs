namespace DigitalTwin.Core.Normalization;

public enum SourceSystem
{
    GitHub,
    Octopus,
    AzureEventGrid,
    AzureResourceGraph,
    Twin
}

/// <summary>
/// One row of the domain event mapping: an exact source event (GitHub event + action, Octopus audit category, Event Grid
/// event type) and the twin event it becomes. <see cref="Condition"/> narrows the row by a payload field ("path = value").
/// </summary>
public sealed record SourceEventMapping(
    SourceSystem Source,
    string SourceEvent,
    string? Action,
    string? Condition,
    string TwinEventType,
    string DomainEvent,
    string BoundedContext);

/// <summary>
/// Mapping from each twin domain event to the exact source events that produce it (DDD v0.2). Names are verified
/// against the snapshots in <c>docs/sources</c>, and a test runs every row through the real normalizer.
/// </summary>
public static class SourceEventCatalog
{
    public const string Observation = "Observation";
    public const string Work = "Work";
    public const string SourceAndChange = "Source & Change";
    public const string BuildAndArtifact = "Build & Artifact";
    public const string ReleaseAndDelivery = "Release & Delivery";
    public const string RuntimeAndPlatform = "Runtime & Platform";
    public const string ReliabilityAndDrift = "Reliability & Drift";

    /// <summary>Unmapped GitHub events are still journaled (hooks subscribe to every event) under this generic type.</summary>
    public const string GitHubFallback = "twin.github.{event}.{action}";
    public const string OctopusFallback = "twin.octopus.audit.{category}";

    private static readonly string[] PullRequestUpdateActions =
    [
        "assigned", "auto_merge_disabled", "auto_merge_enabled", "converted_to_draft", "demilestoned", "dequeued", "edited",
        "enqueued", "labeled", "locked", "milestoned", "ready_for_review", "review_request_removed", "review_requested",
        "stacked", "synchronize", "unassigned", "unlabeled", "unlocked"
    ];

    private static readonly string[] IssueUpdateActions =
    [
        "assigned", "deleted", "demilestoned", "edited", "field_added", "field_removed", "labeled", "locked", "milestoned",
        "pinned", "reopened", "status_changed", "transferred", "typed", "unassigned", "unlabeled", "unlocked", "unpinned", "untyped"
    ];

    public static IReadOnlyList<SourceEventMapping> Mappings { get; } =
    [
        .. GitHub("ping", null, "twin.github.webhook.ping", "WebhookVerified", Observation),
        .. GitHub("push", null, "dev.cdevents.repository.modified", "CodePushed", SourceAndChange),
        .. GitHub("pull_request", ["opened", "reopened"], "dev.cdevents.change.created", "ChangeProposed", SourceAndChange),
        .. GitHub("pull_request", ["closed"], "dev.cdevents.change.merged", "ChangeMerged", SourceAndChange, "pull_request.merged = true"),
        .. GitHub("pull_request", ["closed"], "dev.cdevents.change.abandoned", "ChangeAbandoned", SourceAndChange, "pull_request.merged = false"),
        .. GitHub("pull_request", PullRequestUpdateActions, "dev.cdevents.change.updated", "ChangeUpdated", SourceAndChange),
        .. GitHub("pull_request_review", ["submitted", "edited", "dismissed"], "dev.cdevents.change.reviewed", "ChangeReviewed", SourceAndChange),
        .. GitHub("create", null, "dev.cdevents.branch.created", "BranchCreated", SourceAndChange, "ref_type = branch"),
        .. GitHub("create", null, "dev.cdevents.tag.created", "TagCreated", SourceAndChange, "ref_type = tag"),
        .. GitHub("delete", null, "dev.cdevents.branch.deleted", "BranchDeleted", SourceAndChange, "ref_type = branch"),
        .. GitHub("delete", null, "dev.cdevents.tag.deleted", "TagDeleted", SourceAndChange, "ref_type = tag"),
        .. GitHub("workflow_run", ["requested"], "dev.cdevents.pipelineRun.queued", "PipelineRunQueued", BuildAndArtifact),
        .. GitHub("workflow_run", ["in_progress"], "dev.cdevents.pipelineRun.started", "PipelineRunStarted", BuildAndArtifact),
        .. GitHub("workflow_run", ["completed"], "dev.cdevents.pipelineRun.finished", "PipelineRunFinished", BuildAndArtifact),
        .. GitHub("workflow_job", ["queued"], "dev.cdevents.taskRun.queued", "TaskRunQueued", BuildAndArtifact),
        .. GitHub("workflow_job", ["waiting"], "dev.cdevents.taskRun.waiting", "TaskRunWaiting", BuildAndArtifact),
        .. GitHub("workflow_job", ["in_progress"], "dev.cdevents.taskRun.started", "TaskRunStarted", BuildAndArtifact),
        .. GitHub("workflow_job", ["completed"], "dev.cdevents.taskRun.finished", "TaskRunFinished", BuildAndArtifact),
        .. GitHub("check_run", ["created"], "dev.cdevents.build.created", "CheckRunCreated", BuildAndArtifact),
        .. GitHub("check_run", ["rerequested"], "dev.cdevents.build.rerequested", "CheckRunRerequested", BuildAndArtifact),
        .. GitHub("check_run", ["requested_action"], "dev.cdevents.build.requested_action", "CheckRunActionRequested", BuildAndArtifact),
        .. GitHub("check_run", ["completed"], "dev.cdevents.build.finished", "BuildFinished", BuildAndArtifact),
        .. GitHub("check_suite", ["requested"], "dev.cdevents.build.requested", "CheckSuiteRequested", BuildAndArtifact),
        .. GitHub("check_suite", ["rerequested"], "dev.cdevents.build.rerequested", "CheckSuiteRerequested", BuildAndArtifact),
        .. GitHub("check_suite", ["completed"], "dev.cdevents.build.finished", "BuildFinished", BuildAndArtifact),
        .. GitHub("release", ["created", "edited", "prereleased", "published", "released"], "dev.cdevents.artifact.published", "ArtifactPublished", BuildAndArtifact),
        .. GitHub("release", ["deleted", "unpublished"], "dev.cdevents.artifact.deleted", "ArtifactWithdrawn", BuildAndArtifact),
        .. GitHub("package", ["published", "updated"], "dev.cdevents.artifact.published", "ArtifactPublished", BuildAndArtifact),
        .. GitHub("registry_package", ["published", "updated"], "dev.cdevents.artifact.published", "ArtifactPublished", BuildAndArtifact),
        .. GitHub("deployment", ["created"], "twin.github.deployment", "GitHubDeploymentCreated", ReleaseAndDelivery),
        .. GitHub("deployment_status", ["created"], "twin.github.deployment_status", "GitHubDeploymentStatusReported", ReleaseAndDelivery),
        .. GitHub("issues", ["opened"], "dev.cdevents.ticket.created", "WorkItemOpened", Work),
        .. GitHub("issues", ["closed"], "dev.cdevents.ticket.closed", "WorkItemClosed", Work),
        .. GitHub("issues", IssueUpdateActions, "dev.cdevents.ticket.updated", "WorkItemUpdated", Work),
        .. GitHub("issue_comment", ["created", "edited", "deleted", "pinned", "unpinned"], "dev.cdevents.ticket.updated", "WorkItemUpdated", Work),

        Octopus("DeploymentQueued", "twin.octopus.deployment.queued", "DeploymentQueued"),
        Octopus("DeploymentStarted", "twin.octopus.deployment.started", "DeploymentStarted"),
        Octopus("DeploymentSucceeded", "dev.cdevents.service.deployed", "ServiceDeployed"),
        Octopus("DeploymentFailed", "twin.octopus.deployment.failed", "DeploymentFailed"),
        Octopus("DeploymentCanceled", "twin.octopus.deployment.canceled", "DeploymentCanceled"),
        Octopus("DeploymentResumed", "twin.octopus.deployment.resumed", "DeploymentResumed"),
        Octopus("DeploymentPreconditionEvaluated", "twin.octopus.deployment.preconditionEvaluated", "DeploymentPreconditionEvaluated"),
        Octopus("ManualInterventionInterruptionRaised", "twin.octopus.interruption.manual", "ManualInterventionRequested"),
        Octopus("GuidedFailureInterruptionRaised", "twin.octopus.interruption.guidedFailure", "GuidedFailureRaised"),
        Octopus("AutoDeployTriggerSucceeded", "twin.octopus.autoDeploy.succeeded", "AutoDeployTriggered"),
        Octopus("AutoDeployTriggerBlocked", "twin.octopus.autoDeploy.blocked", "AutoDeployBlocked"),
        Octopus("CompliancePolicyEvaluatedCompliant", "twin.octopus.compliance.evaluated", "CompliancePolicyEvaluated"),
        Octopus("CompliancePolicyEvaluatedNonCompliantBlocked", "twin.octopus.compliance.evaluated", "CompliancePolicyEvaluated"),
        Octopus("CompliancePolicyEvaluatedNonCompliantWarned", "twin.octopus.compliance.evaluated", "CompliancePolicyEvaluated"),
        Octopus("RunbookRunQueued", "twin.octopus.runbook.queued", "RunbookRunQueued"),
        Octopus("RunbookRunStarted", "twin.octopus.runbook.started", "RunbookRunStarted"),
        Octopus("RunbookRunSucceeded", "twin.octopus.runbook.finished", "RunbookRunFinished"),
        Octopus("RunbookRunFailed", "twin.octopus.runbook.finished", "RunbookRunFinished"),
        Octopus("RunbookSnapshotPublished", "twin.octopus.runbook.snapshotPublished", "RunbookSnapshotPublished"),
        Octopus("TaskCanceled", "twin.octopus.task.canceled", "TaskCanceled"),
        Octopus("PackageAdded", "dev.cdevents.artifact.published", "ArtifactPublished", BuildAndArtifact),
        Octopus("PackageDeleted", "dev.cdevents.artifact.deleted", "ArtifactWithdrawn", BuildAndArtifact),
        Octopus("BuildInformationCreated", "twin.octopus.buildInformation.created", "BuildProvenanceRecorded", BuildAndArtifact),
        Octopus("PackageDeletedByRetention", "twin.octopus.retention.deleted", "RetentionApplied"),
        Octopus("PackageDeletedByRepositoryReIndexing", "twin.octopus.retention.deleted", "RetentionApplied"),
        Octopus("ReleaseDeletedByRetention", "twin.octopus.retention.deleted", "RetentionApplied"),
        Octopus("RunbookRunDeletedByRetention", "twin.octopus.retention.deleted", "RetentionApplied"),
        Octopus("RunbookSnapshotDeletedByRetention", "twin.octopus.retention.deleted", "RetentionApplied"),
        Octopus("EphemeralEnvironmentCreationSucceeded", "dev.cdevents.environment.created", "EnvironmentCreated", RuntimeAndPlatform),
        Octopus("EphemeralEnvironmentCreationFailed", "twin.octopus.ephemeral.failed", "EnvironmentCreationFailed", RuntimeAndPlatform),
        Octopus("Created", "twin.octopus.document.created", "DeliveryConfigurationCreated"),
        Octopus("Modified", "twin.octopus.document.modified", "DeliveryConfigurationModified"),
        Octopus("Deleted", "twin.octopus.document.deleted", "DeliveryConfigurationDeleted"),
        Octopus("RateLimitExceeded", "twin.octopus.rateLimit.exceeded", "RateLimitExceeded", Observation),

        Azure("Microsoft.Resources.ResourceWriteSuccess", "dev.cdevents.environment.modified", "ResourceChanged"),
        Azure("Microsoft.Resources.ResourceDeleteSuccess", "dev.cdevents.environment.deleted", "ResourceDeleted"),
        Azure("Microsoft.Resources.ResourceActionSuccess", "twin.azure.resource.action", "ResourceActionPerformed"),
        Azure("Microsoft.Resources.ResourceWriteFailure", "twin.azure.resource.failed", "ResourceOperationFailed"),
        Azure("Microsoft.Resources.ResourceDeleteFailure", "twin.azure.resource.failed", "ResourceOperationFailed"),
        Azure("Microsoft.Resources.ResourceActionFailure", "twin.azure.resource.failed", "ResourceOperationFailed"),
        Azure("Microsoft.Resources.ResourceWriteCancel", "twin.azure.resource.canceled", "ResourceOperationCanceled"),
        Azure("Microsoft.Resources.ResourceDeleteCancel", "twin.azure.resource.canceled", "ResourceOperationCanceled"),
        Azure("Microsoft.Resources.ResourceActionCancel", "twin.azure.resource.canceled", "ResourceOperationCanceled"),

        new(SourceSystem.AzureResourceGraph, "resourcechanges", "Create", null, "twin.azure.resource.create", "ResourceCreated", RuntimeAndPlatform),
        new(SourceSystem.AzureResourceGraph, "resourcechanges", "Update", null, "twin.azure.resource.update", "ResourceChanged", RuntimeAndPlatform),
        new(SourceSystem.AzureResourceGraph, "resourcechanges", "Delete", null, "twin.azure.resource.delete", "ResourceDeleted", RuntimeAndPlatform),
        new(SourceSystem.AzureResourceGraph, "Container App revision poll", null, null, "twin.azure.revision.changed", "WorkloadRevisionChanged", RuntimeAndPlatform),

        new(SourceSystem.Twin, "drift evaluation", null, null, "twin.drift.detected", "DriftDetected", ReliabilityAndDrift),
        new(SourceSystem.Twin, "poller observation", null, null, "twin.state.observed", "StateObserved", Observation)
    ];

    private static IEnumerable<SourceEventMapping> GitHub(string sourceEvent, string[]? actions, string twinType, string domainEvent, string context, string? condition = null)
        => actions is null
            ? [new SourceEventMapping(SourceSystem.GitHub, sourceEvent, null, condition, twinType, domainEvent, context)]
            : actions.Select(a => new SourceEventMapping(SourceSystem.GitHub, sourceEvent, a, condition, twinType, domainEvent, context));

    private static SourceEventMapping Octopus(string category, string twinType, string domainEvent, string context = ReleaseAndDelivery)
        => new(SourceSystem.Octopus, category, null, null, twinType, domainEvent, context);

    private static SourceEventMapping Azure(string eventType, string twinType, string domainEvent)
        => new(SourceSystem.AzureEventGrid, eventType, null, null, twinType, domainEvent, RuntimeAndPlatform);
}
