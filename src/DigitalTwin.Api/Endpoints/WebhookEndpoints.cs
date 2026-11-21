using System.Text;
using DigitalTwin.Application.Abstractions;
using DigitalTwin.Application.Services;
using DigitalTwin.Domain.Normalization;
using DigitalTwin.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace DigitalTwin.Api.Endpoints;

public static partial class WebhookEndpoints
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Background webhook follow-up refresh failed for {Source}")]
    private static partial void LogFollowUpFailed(ILogger logger, Exception ex, string source);

    public static void MapWebhookEndpoints(this WebApplication app)
    {
        app.MapPost("/hooks/github", HandleGitHubAsync);
        app.MapPost("/hooks/octopus", HandleOctopusAsync);
        app.MapMethods("/hooks/azure", ["OPTIONS"], HandleAzureOptions);
        app.MapPost("/hooks/azure", HandleAzureAsync);
    }

    private static async Task<IResult> HandleGitHubAsync(
        HttpContext context,
        IOptions<TwinOptions> options,
        EventIngestionService ingestion,
        IGitHubSyncService gitHubSync,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        var rawBytes = await ReadRawBodyAsync(context.Request, ct);
        var signature = context.Request.Headers["X-Hub-Signature-256"].FirstOrDefault();
        var secret = options.Value.WebhookSecret;

        if (!WebhookSignatureVerifier.VerifyGitHubSignature(rawBytes, signature, secret))
        {
            return Results.Unauthorized();
        }

        var eventName = context.Request.Headers["X-GitHub-Event"].FirstOrDefault() ?? "unknown";
        var deliveryId = context.Request.Headers["X-GitHub-Delivery"].FirstOrDefault();
        var rawJson = Encoding.UTF8.GetString(rawBytes);
        var now = timeProvider.GetUtcNow();

        var normalized = GitHubEventNormalizer.NormalizeWebhook(eventName, deliveryId, rawJson, now);
        var accepted = await ingestion.IngestAsync(normalized, ct);

        if (eventName is "push" or "pull_request" or "workflow_run" or "issues" or "release" or "deployment" or "deployment_status")
        {
            var repoName = normalized.SubjectId.Split(':', '#')[0].Split('/').LastOrDefault();
            var logger = loggerFactory.CreateLogger("WebhookRefresh");
            _ = Task.Run(async () =>
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(repoName))
                    {
                        await gitHubSync.SyncRepositoryAsync(repoName, CancellationToken.None);
                    }
                    await ingestion.EvaluateAndRecordDriftAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    LogFollowUpFailed(logger, ex, "GitHub");
                }
            }, CancellationToken.None);
        }

        return Results.Ok(new { accepted, eventId = normalized.EventId, idempotencyKey = normalized.IdempotencyKey });
    }

    private static async Task<IResult> HandleOctopusAsync(
        HttpContext context,
        IOptions<TwinOptions> options,
        EventIngestionService ingestion,
        IOctopusSyncService octopusSync,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        var provided = context.Request.Headers["X-Webhook-Secret"].FirstOrDefault()
            ?? context.Request.Headers.Authorization.FirstOrDefault()
            ?? context.Request.Query["token"].FirstOrDefault();

        if (!WebhookSignatureVerifier.VerifySharedSecret(provided, options.Value.WebhookSecret))
        {
            return Results.Unauthorized();
        }

        var rawBytes = await ReadRawBodyAsync(context.Request, ct);
        var rawJson = Encoding.UTF8.GetString(rawBytes);
        var now = timeProvider.GetUtcNow();

        var normalized = OctopusEventNormalizer.NormalizeSubscriptionPayload(rawJson, null, null, now);
        if (normalized is null)
        {
            return Results.BadRequest(new { error = "Unrecognized Octopus subscription payload." });
        }

        var accepted = await ingestion.IngestAsync(normalized, ct);
        var logger = loggerFactory.CreateLogger("WebhookRefresh");
        _ = Task.Run(async () =>
        {
            try
            {
                await octopusSync.SyncAsync(fullBackfill: false, CancellationToken.None);
                await ingestion.EvaluateAndRecordDriftAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                LogFollowUpFailed(logger, ex, "Octopus");
            }
        }, CancellationToken.None);

        return Results.Ok(new { accepted, eventId = normalized.EventId, idempotencyKey = normalized.IdempotencyKey });
    }

    private static IResult HandleAzureOptions(HttpContext context)
    {
        var origin = context.Request.Headers["WebHook-Request-Origin"].FirstOrDefault() ?? "*";
        context.Response.Headers["WebHook-Allowed-Origin"] = origin;
        context.Response.Headers["WebHook-Allowed-Rate"] = "*";
        return Results.Ok();
    }

    private static async Task<IResult> HandleAzureAsync(
        HttpContext context,
        IOptions<TwinOptions> options,
        EventIngestionService ingestion,
        IAzureSyncService azureSync,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        var rawBytes = await ReadRawBodyAsync(context.Request, ct);
        var rawJson = Encoding.UTF8.GetString(rawBytes);
        var now = timeProvider.GetUtcNow();

        var parsed = AzureEventNormalizer.NormalizeEventGridWebhook(rawJson, now);
        if (!string.IsNullOrWhiteSpace(parsed.ValidationResponse))
        {
            return Results.Ok(new { validationResponse = parsed.ValidationResponse });
        }

        if (!string.IsNullOrWhiteSpace(options.Value.WebhookSecret))
        {
            var provided = context.Request.Headers["X-Webhook-Secret"].FirstOrDefault()
                ?? context.Request.Query["token"].FirstOrDefault();
            if (!WebhookSignatureVerifier.VerifySharedSecret(provided, options.Value.WebhookSecret))
            {
                return Results.Unauthorized();
            }
        }

        var ingested = await ingestion.IngestBatchAsync(parsed.Events, ct);
        if (parsed.Events.Count > 0)
        {
            var logger = loggerFactory.CreateLogger("WebhookRefresh");
            _ = Task.Run(async () =>
            {
                try
                {
                    await azureSync.SyncAsync(fullBackfill: false, CancellationToken.None);
                    await ingestion.EvaluateAndRecordDriftAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    LogFollowUpFailed(logger, ex, "Azure");
                }
            }, CancellationToken.None);
        }

        return Results.Ok(new { ingested, received = parsed.Events.Count });
    }

    private static async Task<byte[]> ReadRawBodyAsync(HttpRequest request, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await request.Body.CopyToAsync(ms, ct);
        return ms.ToArray();
    }
}
