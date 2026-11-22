using System.Text.Json;
using DigitalTwin.Core.Hierarchy;
using DigitalTwin.Core.Observation;

namespace DigitalTwin.Core.Normalization;

public static class GitHubEventNormalizer
{
    public static ObservedEvent NormalizeWebhook(
        string eventName,
        string? deliveryId,
        string rawJson,
        DateTimeOffset receivedAt)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(rawJson) ? "{}" : rawJson);
        var root = doc.RootElement;

        var repoFullName = GetString(root, "repository", "full_name") ?? $"{Cmdemo1PlatformCatalog.GitHubOrg}/cmdemo1-system";
        var repoShortName = GetString(root, "repository", "name") ?? repoFullName.Split('/').Last();
        var pipelineId = Cmdemo1PlatformCatalog.ResolvePipelineIdFromRepository(repoShortName);
        var deployable = Cmdemo1PlatformCatalog.ResolveDeployableFromRepository(repoShortName);
        var actor = GetString(root, "sender", "login") ?? GetString(root, "pusher", "name") ?? "github";
        var action = GetString(root, "action");
        var eventId = string.IsNullOrWhiteSpace(deliveryId) ? Guid.NewGuid().ToString("N") : deliveryId;

        var (eventType, subjectType, subjectId, predicate, outcome, summary, url, chainId, stageName, occurredAt) =
            eventName.ToLowerInvariant() switch
            {
                "ping" => (
                    "twin.github.webhook.ping",
                    "repository",
                    repoFullName,
                    "pinged",
                    EventOutcome.Success,
                    $"GitHub webhook active for {repoShortName}",
                    GetString(root, "repository", "html_url"),
                    $"gh:{repoShortName}:ping",
                    null,
                    receivedAt),

                "push" => ParsePush(root, repoShortName, repoFullName, receivedAt),
                "pull_request" => ParsePullRequest(root, action, repoShortName, receivedAt),
                "pull_request_review" => ParsePullRequestReview(root, action, repoShortName, receivedAt),
                "workflow_run" => ParseWorkflowRun(root, action, repoShortName, receivedAt),
                "workflow_job" => ParseWorkflowJob(root, action, repoShortName, receivedAt),
                "check_run" or "check_suite" => ParseCheck(root, eventName, action, repoShortName, receivedAt),
                "release" => ParseRelease(root, action, repoShortName, receivedAt),
                "deployment" or "deployment_status" => ParseDeployment(root, eventName, repoShortName, receivedAt),
                "issues" or "issue_comment" => ParseIssue(root, eventName, action, repoShortName, receivedAt),
                "package" or "registry_package" => ParsePackage(root, action, repoShortName, receivedAt),
                "create" or "delete" => ParseRefChange(root, eventName, repoShortName, receivedAt),
                _ => (
                    $"twin.github.{eventName}{(string.IsNullOrEmpty(action) ? "" : $".{action}")}",
                    "repository",
                    repoFullName,
                    action ?? eventName,
                    EventOutcome.None,
                    $"GitHub {eventName}{(string.IsNullOrEmpty(action) ? "" : $" {action}")} on {repoShortName}",
                    GetString(root, "repository", "html_url"),
                    $"gh:{repoShortName}:{eventName}",
                    null,
                    receivedAt)
            };

        return new ObservedEvent
        {
            EventId = eventId,
            IdempotencyKey = $"github:{eventId}",
            Source = EventSourceKind.GitHub,
            SourceEventId = eventId,
            EventType = eventType,
            SubjectType = subjectType,
            SubjectId = subjectId,
            Predicate = predicate,
            OccurredAt = occurredAt,
            IngestedAt = receivedAt,
            Channel = IngestionChannel.Webhook,
            CorrelationId = chainId,
            ChainId = chainId,
            PipelineId = pipelineId,
            StageName = stageName,
            DeployableName = deployable,
            Actor = actor,
            Outcome = outcome,
            Summary = summary,
            SourceUrl = url,
            PayloadJson = rawJson
        };
    }

    private static (string, string, string, string, EventOutcome, string, string?, string, string?, DateTimeOffset)
        ParsePush(JsonElement root, string repoShortName, string repoFullName, DateTimeOffset fallbackTime)
    {
        var gitRef = GetString(root, "ref") ?? "refs/heads/main";
        var branch = gitRef.Replace("refs/heads/", "", StringComparison.Ordinal);
        var sha = GetString(root, "after") ?? GetString(root, "head_commit", "id") ?? "";
        var shortSha = sha.Length >= 7 ? sha[..7] : sha;
        var message = (GetString(root, "head_commit", "message") ?? "Push").Split('\n')[0];
        var url = GetString(root, "head_commit", "url") ?? GetString(root, "compare") ?? GetString(root, "repository", "html_url");
        var at = ParseDate(GetString(root, "head_commit", "timestamp")) ?? fallbackTime;

        string? stage = null;
        if (repoShortName.Equals("cmdemo1-system", StringComparison.OrdinalIgnoreCase) &&
            message.StartsWith("Pin ", StringComparison.OrdinalIgnoreCase))
        {
            var parts = message.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var inIndex = Array.IndexOf(parts, "in");
            if (inIndex >= 0 && inIndex + 1 < parts.Length)
            {
                stage = parts[inIndex + 1];
            }
        }

        return (
            "dev.cdevents.repository.modified",
            "branch",
            $"{repoFullName}:{branch}",
            "pushed",
            EventOutcome.Success,
            $"[{repoShortName}/{branch}] {shortSha}: {Truncate(message, 90)}",
            url,
            string.IsNullOrEmpty(sha) ? $"gh:{repoShortName}:{branch}" : $"sha:{sha}",
            stage,
            at);
    }

    private static (string, string, string, string, EventOutcome, string, string?, string, string?, DateTimeOffset)
        ParsePullRequest(JsonElement root, string? action, string repoShortName, DateTimeOffset fallbackTime)
    {
        var prNum = GetInt(root, "pull_request", "number") ?? GetInt(root, "number") ?? 0;
        var title = GetString(root, "pull_request", "title") ?? $"PR #{prNum}";
        var merged = GetBool(root, "pull_request", "merged") == true;
        var sha = GetString(root, "pull_request", "merge_commit_sha") ?? GetString(root, "pull_request", "head", "sha");
        var url = GetString(root, "pull_request", "html_url");
        var at = ParseDate(GetString(root, "pull_request", "updated_at")) ?? fallbackTime;

        var predicate = action switch
        {
            "closed" when merged => "merged",
            "closed" => "abandoned",
            "opened" or "reopened" => "created",
            _ => "updated"
        };
        var outcome = predicate switch
        {
            "merged" => EventOutcome.Success,
            "abandoned" => EventOutcome.Canceled,
            _ => EventOutcome.None
        };

        return (
            $"dev.cdevents.change.{predicate}",
            "change",
            $"{repoShortName}#{prNum}",
            predicate,
            outcome,
            $"[{repoShortName}] PR #{prNum} {predicate}: {Truncate(title, 80)}",
            url,
            string.IsNullOrEmpty(sha) ? $"pr:{repoShortName}#{prNum}" : $"sha:{sha}",
            null,
            at);
    }

    private static (string, string, string, string, EventOutcome, string, string?, string, string?, DateTimeOffset)
        ParsePullRequestReview(JsonElement root, string? action, string repoShortName, DateTimeOffset fallbackTime)
    {
        var prNum = GetInt(root, "pull_request", "number") ?? 0;
        var state = GetString(root, "review", "state") ?? action ?? "reviewed";
        var url = GetString(root, "review", "html_url") ?? GetString(root, "pull_request", "html_url");
        var at = ParseDate(GetString(root, "review", "submitted_at")) ?? fallbackTime;
        var outcome = state.Equals("approved", StringComparison.OrdinalIgnoreCase)
            ? EventOutcome.Success
            : state.Equals("changes_requested", StringComparison.OrdinalIgnoreCase) ? EventOutcome.Failure : EventOutcome.None;

        return (
            "dev.cdevents.change.reviewed",
            "change",
            $"{repoShortName}#{prNum}",
            "reviewed",
            outcome,
            $"[{repoShortName}] PR #{prNum} review ({state})",
            url,
            $"pr:{repoShortName}#{prNum}",
            null,
            at);
    }

    private static (string, string, string, string, EventOutcome, string, string?, string, string?, DateTimeOffset)
        ParseWorkflowRun(JsonElement root, string? action, string repoShortName, DateTimeOffset fallbackTime)
    {
        var runId = GetLong(root, "workflow_run", "id") ?? 0;
        var name = GetString(root, "workflow_run", "name") ?? "Workflow";
        var status = GetString(root, "workflow_run", "status") ?? action ?? "updated";
        var conclusion = GetString(root, "workflow_run", "conclusion");
        var sha = GetString(root, "workflow_run", "head_sha");
        var branch = GetString(root, "workflow_run", "head_branch") ?? "";
        var url = GetString(root, "workflow_run", "html_url");
        var at = ParseDate(GetString(root, "workflow_run", "updated_at")) ?? fallbackTime;

        var predicate = action switch
        {
            "requested" => "queued",
            "in_progress" => "started",
            "completed" => "finished",
            _ => status
        };

        var outcome = conclusion?.ToLowerInvariant() switch
        {
            "success" => EventOutcome.Success,
            "failure" or "timed_out" => EventOutcome.Failure,
            "cancelled" => EventOutcome.Canceled,
            "skipped" => EventOutcome.Skipped,
            _ => predicate == "started" ? EventOutcome.Started : predicate == "queued" ? EventOutcome.Queued : EventOutcome.None
        };

        return (
            $"dev.cdevents.pipelineRun.{predicate}",
            "pipelineRun",
            $"{repoShortName}:run:{runId}",
            predicate,
            outcome,
            $"[{repoShortName}/{branch}] Workflow '{name}' {predicate}{(string.IsNullOrEmpty(conclusion) ? "" : $" ({conclusion})")}",
            url,
            string.IsNullOrEmpty(sha) ? $"run:{repoShortName}:{runId}" : $"sha:{sha}",
            null,
            at);
    }

    private static (string, string, string, string, EventOutcome, string, string?, string, string?, DateTimeOffset)
        ParseWorkflowJob(JsonElement root, string? action, string repoShortName, DateTimeOffset fallbackTime)
    {
        var jobId = GetLong(root, "workflow_job", "id") ?? 0;
        var name = GetString(root, "workflow_job", "name") ?? "Job";
        var conclusion = GetString(root, "workflow_job", "conclusion");
        var sha = GetString(root, "workflow_job", "head_sha");
        var url = GetString(root, "workflow_job", "html_url");
        var at = ParseDate(GetString(root, "workflow_job", "completed_at"))
            ?? ParseDate(GetString(root, "workflow_job", "started_at"))
            ?? fallbackTime;

        var predicate = action switch
        {
            "queued" => "queued",
            "in_progress" => "started",
            "completed" => "finished",
            _ => action ?? "updated"
        };
        var outcome = conclusion?.ToLowerInvariant() switch
        {
            "success" => EventOutcome.Success,
            "failure" or "timed_out" => EventOutcome.Failure,
            "cancelled" => EventOutcome.Canceled,
            "skipped" => EventOutcome.Skipped,
            _ => predicate == "started" ? EventOutcome.Started : EventOutcome.Queued
        };

        return (
            $"dev.cdevents.taskRun.{predicate}",
            "taskRun",
            $"{repoShortName}:job:{jobId}",
            predicate,
            outcome,
            $"[{repoShortName}] Job '{name}' {predicate}{(string.IsNullOrEmpty(conclusion) ? "" : $" ({conclusion})")}",
            url,
            string.IsNullOrEmpty(sha) ? $"job:{repoShortName}:{jobId}" : $"sha:{sha}",
            null,
            at);
    }

    private static (string, string, string, string, EventOutcome, string, string?, string, string?, DateTimeOffset)
        ParseCheck(JsonElement root, string eventName, string? action, string repoShortName, DateTimeOffset fallbackTime)
    {
        var objName = eventName == "check_run" ? "check_run" : "check_suite";
        var id = GetLong(root, objName, "id") ?? 0;
        var name = GetString(root, objName, "name") ?? eventName;
        var conclusion = GetString(root, objName, "conclusion");
        var sha = GetString(root, objName, "head_sha");
        var url = GetString(root, objName, "html_url");
        var predicate = action == "completed" ? "finished" : action ?? "updated";
        var outcome = conclusion?.ToLowerInvariant() switch
        {
            "success" => EventOutcome.Success,
            "failure" or "timed_out" => EventOutcome.Failure,
            "cancelled" => EventOutcome.Canceled,
            _ => EventOutcome.None
        };

        return (
            $"dev.cdevents.build.{predicate}",
            "build",
            $"{repoShortName}:{eventName}:{id}",
            predicate,
            outcome,
            $"[{repoShortName}] {name} {predicate}{(string.IsNullOrEmpty(conclusion) ? "" : $" ({conclusion})")}",
            url,
            string.IsNullOrEmpty(sha) ? $"check:{id}" : $"sha:{sha}",
            null,
            fallbackTime);
    }

    private static (string, string, string, string, EventOutcome, string, string?, string, string?, DateTimeOffset)
        ParseRelease(JsonElement root, string? action, string repoShortName, DateTimeOffset fallbackTime)
    {
        var tag = GetString(root, "release", "tag_name") ?? "release";
        var url = GetString(root, "release", "html_url");
        var at = ParseDate(GetString(root, "release", "published_at")) ?? fallbackTime;
        var removed = action is "deleted" or "unpublished";
        return (
            removed ? "dev.cdevents.artifact.deleted" : "dev.cdevents.artifact.published",
            "artifact",
            $"{repoShortName}:{tag}",
            action ?? "published",
            EventOutcome.Success,
            $"[{repoShortName}] Release {tag} {action ?? "published"}",
            url,
            $"release:{repoShortName}:{tag}",
            null,
            at);
    }

    private static (string, string, string, string, EventOutcome, string, string?, string, string?, DateTimeOffset)
        ParseDeployment(JsonElement root, string eventName, string repoShortName, DateTimeOffset fallbackTime)
    {
        var env = GetString(root, "deployment", "environment") ?? "unknown";
        var sha = GetString(root, "deployment", "sha");
        var state = eventName == "deployment_status" ? GetString(root, "deployment_status", "state") : "created";
        var url = GetString(root, "deployment_status", "target_url") ?? GetString(root, "deployment", "url");
        var outcome = state?.ToLowerInvariant() switch
        {
            "success" => EventOutcome.Success,
            "failure" or "error" => EventOutcome.Failure,
            "in_progress" or "queued" => EventOutcome.Started,
            _ => EventOutcome.None
        };
        return (
            $"twin.github.{eventName}",
            "deployment",
            $"{repoShortName}:{env}",
            state ?? "created",
            outcome,
            $"[{repoShortName}] GitHub deployment ({env}) {state}",
            url,
            string.IsNullOrEmpty(sha) ? $"gh-deploy:{repoShortName}:{env}" : $"sha:{sha}",
            env,
            fallbackTime);
    }

    private static (string, string, string, string, EventOutcome, string, string?, string, string?, DateTimeOffset)
        ParseIssue(JsonElement root, string eventName, string? action, string repoShortName, DateTimeOffset fallbackTime)
    {
        var num = GetInt(root, "issue", "number") ?? 0;
        var title = GetString(root, "issue", "title") ?? $"Issue #{num}";
        var url = GetString(root, "comment", "html_url") ?? GetString(root, "issue", "html_url");
        var at = ParseDate(GetString(root, "comment", "created_at"))
            ?? ParseDate(GetString(root, "issue", "updated_at"))
            ?? fallbackTime;
        var predicate = eventName == "issue_comment" ? "updated" : action switch
        {
            "opened" => "created",
            "closed" => "closed",
            _ => "updated"
        };
        return (
            $"dev.cdevents.ticket.{predicate}",
            "ticket",
            $"{repoShortName}#{num}",
            predicate,
            predicate == "closed" ? EventOutcome.Success : EventOutcome.None,
            eventName == "issue_comment"
                ? $"[{repoShortName}] Comment on #{num}: {Truncate(title, 70)}"
                : $"[{repoShortName}] Issue #{num} {action}: {Truncate(title, 70)}",
            url,
            $"issue:{repoShortName}#{num}",
            null,
            at);
    }

    private static (string, string, string, string, EventOutcome, string, string?, string, string?, DateTimeOffset)
        ParsePackage(JsonElement root, string? action, string repoShortName, DateTimeOffset fallbackTime)
    {
        var pkg = GetString(root, "package", "name") ?? GetString(root, "registry_package", "name") ?? "package";
        var ver = GetString(root, "package", "package_version", "version")
            ?? GetString(root, "registry_package", "package_version", "version") ?? "";
        var url = GetString(root, "package", "html_url") ?? GetString(root, "registry_package", "html_url");
        return (
            "dev.cdevents.artifact.published",
            "artifact",
            $"{pkg}:{ver}",
            action ?? "published",
            EventOutcome.Success,
            $"[{repoShortName}] Package {pkg} {ver} {action}",
            url,
            $"pkg:{pkg}:{ver}",
            null,
            fallbackTime);
    }

    private static (string, string, string, string, EventOutcome, string, string?, string, string?, DateTimeOffset)
        ParseRefChange(JsonElement root, string eventName, string repoShortName, DateTimeOffset fallbackTime)
    {
        var refType = GetString(root, "ref_type") ?? "branch";
        var refName = GetString(root, "ref") ?? "";
        var predicate = eventName == "create" ? "created" : "deleted";
        return (
            $"dev.cdevents.{refType}.{predicate}",
            refType,
            $"{repoShortName}:{refName}",
            predicate,
            EventOutcome.None,
            $"[{repoShortName}] {refType} '{refName}' {predicate}",
            GetString(root, "repository", "html_url"),
            $"ref:{repoShortName}:{refName}",
            null,
            fallbackTime);
    }

    private static string? GetString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() : current.ToString();
    }

    private static int? GetInt(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }
        return current.ValueKind == JsonValueKind.Number && current.TryGetInt32(out var v) ? v : null;
    }

    private static long? GetLong(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }
        return current.ValueKind == JsonValueKind.Number && current.TryGetInt64(out var v) ? v : null;
    }

    private static bool? GetBool(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }
        return current.ValueKind is JsonValueKind.True or JsonValueKind.False ? current.GetBoolean() : null;
    }

    private static DateTimeOffset? ParseDate(string? value)
        => DateTimeOffset.TryParse(value, out var parsed) ? parsed.ToUniversalTime() : null;

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : $"{text[..(max - 1)]}…";
}
