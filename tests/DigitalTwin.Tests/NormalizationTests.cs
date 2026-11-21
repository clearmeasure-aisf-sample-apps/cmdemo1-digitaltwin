using System.Security.Cryptography;
using System.Text;
using DigitalTwin.Domain.Normalization;
using DigitalTwin.Domain.Observation;
using Xunit;

namespace DigitalTwin.Tests;

public sealed class NormalizationTests
{
    [Fact]
    public void VerifyGitHubSignatureAcceptsValidHmacAndRejectsInvalid()
    {
        const string secret = "super-secret-key";
        var body = Encoding.UTF8.GetBytes("{\"zen\":\"Keep it logically awesome.\"}");
        var sigHex = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

        Assert.True(WebhookSignatureVerifier.VerifyGitHubSignature(body, $"sha256={sigHex}", secret));
        Assert.False(WebhookSignatureVerifier.VerifyGitHubSignature(body, $"sha256={new string('0', 64)}", secret));
        Assert.False(WebhookSignatureVerifier.VerifyGitHubSignature(body, "invalid", secret));
        Assert.False(WebhookSignatureVerifier.VerifyGitHubSignature(body, null, secret));
    }

    [Fact]
    public void GitHubNormalizerExtractsPinStageFromSystemRepoPush()
    {
        var json = """
            {
              "ref": "refs/heads/main",
              "after": "9820b3949c3648be4fb0737717ff3b2c0a112233",
              "repository": { "name": "cmdemo1-system", "full_name": "clearmeasure-aisf-sample-apps/cmdemo1-system", "html_url": "https://github.com/clearmeasure-aisf-sample-apps/cmdemo1-system" },
              "sender": { "login": "cmdemo1-system[bot]" },
              "head_commit": {
                "id": "9820b3949c3648be4fb0737717ff3b2c0a112233",
                "message": "Pin ui 2.4.2664 in uat (Deployments-58839)",
                "timestamp": "2026-11-21T04:29:10Z",
                "url": "https://github.com/clearmeasure-aisf-sample-apps/cmdemo1-system/commit/9820b394"
              }
            }
            """;

        var ev = GitHubEventNormalizer.NormalizeWebhook("push", "deliv-1", json, DateTimeOffset.UtcNow);

        Assert.Equal("github:deliv-1", ev.IdempotencyKey);
        Assert.Equal("system-gitops", ev.PipelineId);
        Assert.Equal("uat", ev.StageName);
        Assert.Equal("dev.cdevents.repository.modified", ev.EventType);
        Assert.Equal(EventOutcome.Success, ev.Outcome);
    }

    [Fact]
    public void GitHubNormalizerMapsMergedPullRequestAndFailedWorkflowRun()
    {
        var prJson = """
            {
              "action": "closed",
              "number": 1941,
              "repository": { "name": "cmdemo1-workorders", "full_name": "clearmeasure-aisf-sample-apps/cmdemo1-workorders" },
              "sender": { "login": "cm-ai-ops-bot" },
              "pull_request": {
                "number": 1941,
                "title": "Add DST tests (#1941)",
                "merged": true,
                "merge_commit_sha": "9d4a43165eb9d090ab0847bbb4cc6398f1d29699",
                "updated_at": "2026-11-21T03:51:42Z",
                "html_url": "https://github.com/clearmeasure-aisf-sample-apps/cmdemo1-workorders/pull/1941"
              }
            }
            """;

        var prEv = GitHubEventNormalizer.NormalizeWebhook("pull_request", "deliv-pr", prJson, DateTimeOffset.UtcNow);
        Assert.Equal("dev.cdevents.change.merged", prEv.EventType);
        Assert.Equal("app-ui", prEv.PipelineId);
        Assert.Equal("ui", prEv.DeployableName);
        Assert.Equal(EventOutcome.Success, prEv.Outcome);

        var runJson = """
            {
              "action": "completed",
              "repository": { "name": "cmdemo1-dashboard", "full_name": "clearmeasure-aisf-sample-apps/cmdemo1-dashboard" },
              "sender": { "login": "github-actions" },
              "workflow_run": {
                "id": 44865762661,
                "name": "Build",
                "status": "completed",
                "conclusion": "failure",
                "head_branch": "master",
                "head_sha": "1f7af364f9110b98c988b07f16a58eed298bba12",
                "updated_at": "2026-11-21T04:23:34Z"
              }
            }
            """;

        var runEv = GitHubEventNormalizer.NormalizeWebhook("workflow_run", "deliv-run", runJson, DateTimeOffset.UtcNow);
        Assert.Equal("dev.cdevents.pipelineRun.finished", runEv.EventType);
        Assert.Equal("app-dashboard", runEv.PipelineId);
        Assert.Equal(EventOutcome.Failure, runEv.Outcome);
    }

    [Fact]
    public void OctopusNormalizerParsesSubscriptionPayload()
    {
        var json = """
            {
              "Timestamp": "2026-11-21T04:37:25Z",
              "EventType": "SubscriptionPayload",
              "Payload": {
                "ServerUri": "https://clearmeasure.octopus.app",
                "Event": {
                  "Id": "Events-1915690",
                  "Category": "DeploymentSucceeded",
                  "Message": "Deploy to uat succeeded for cmdemo1-ui release 2.4.2664 to uat",
                  "Occurred": "2026-11-21T04:37:25.729+00:00",
                  "Username": "system",
                  "RelatedDocumentIds": [
                    "Deployments-58839",
                    "Projects-981",
                    "Releases-52228",
                    "Environments-622",
                    "ServerTasks-12304684"
                  ]
                }
              }
            }
            """;

        var projects = new Dictionary<string, string> { ["Projects-981"] = "cmdemo1-ui" };
        var envs = new Dictionary<string, string> { ["Environments-622"] = "uat" };
        var ev = OctopusEventNormalizer.NormalizeSubscriptionPayload(json, projects, envs, DateTimeOffset.UtcNow);

        Assert.NotNull(ev);
        Assert.Equal("octopus:Events-1915690", ev.IdempotencyKey);
        Assert.Equal("dev.cdevents.service.deployed", ev.EventType);
        Assert.Equal("app-ui", ev.PipelineId);
        Assert.Equal("uat", ev.StageName);
        Assert.Equal("ui", ev.DeployableName);
        Assert.Equal(EventOutcome.Success, ev.Outcome);
    }

    [Fact]
    public void AzureNormalizerHandlesValidationHandshakeAndResourceWrites()
    {
        var validationJson = """
            [
              {
                "id": "eg-val-1",
                "eventType": "Microsoft.EventGrid.SubscriptionValidationEvent",
                "data": { "validationCode": "512d38b6-c7b8-40c8-89fe-f46f9e9622b6" },
                "eventTime": "2026-11-21T04:00:00Z"
              }
            ]
            """;

        var valResult = AzureEventNormalizer.NormalizeEventGridWebhook(validationJson, DateTimeOffset.UtcNow);
        Assert.Equal("512d38b6-c7b8-40c8-89fe-f46f9e9622b6", valResult.ValidationResponse);
        Assert.Empty(valResult.Events);

        var writeJson = """
            [
              {
                "id": "eg-write-1",
                "eventType": "Microsoft.Resources.ResourceWriteSuccess",
                "subject": "/subscriptions/4a4dfa6d/resourceGroups/rg-cmdemo1-nonprod/providers/Microsoft.App/containerApps/ca-cmdemo1-tdd-ui",
                "eventTime": "2026-11-21T04:12:55Z",
                "data": {
                  "operationName": "Microsoft.App/containerApps/write",
                  "resourceUri": "/subscriptions/4a4dfa6d/resourceGroups/rg-cmdemo1-nonprod/providers/Microsoft.App/containerApps/ca-cmdemo1-tdd-ui",
                  "correlationId": "corr-999"
                }
              }
            ]
            """;

        var writeResult = AzureEventNormalizer.NormalizeEventGridWebhook(writeJson, DateTimeOffset.UtcNow);
        var ev = Assert.Single(writeResult.Events);
        Assert.Equal("azure:eg-write-1", ev.IdempotencyKey);
        Assert.Equal("tdd", ev.StageName);
        Assert.Equal("ui", ev.DeployableName);
        Assert.Equal(EventOutcome.Success, ev.Outcome);
    }
}
