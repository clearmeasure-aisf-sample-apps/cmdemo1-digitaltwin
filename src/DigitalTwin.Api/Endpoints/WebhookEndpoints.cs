using System.Text;
using DigitalTwin.Api.Auth;
using DigitalTwin.Core.Intake;
using DigitalTwin.Core.Normalization;
using DigitalTwin.Core.Observation;
using DigitalTwin.Core.Ports;
using Microsoft.Extensions.Options;

namespace DigitalTwin.Api.Endpoints;

/// <summary>
/// Webhook receivers only authenticate, wrap the raw body and durably capture it. Processing happens in the
/// intake processor. 200 is returned only after the capture transaction committed; failures return 503 so the
/// sender redelivers.
/// </summary>
public static partial class WebhookEndpoints
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to durably capture {Source} webhook; returning 503 for redelivery")]
    private static partial void LogCaptureFailed(ILogger logger, Exception ex, EventSourceKind source);

    public static void MapWebhookEndpoints(this WebApplication app)
    {
        app.MapPost("/hooks/github", HandleGitHubAsync);
        app.MapPost("/hooks/octopus", HandleOctopusAsync);
        app.MapMethods("/hooks/azure", ["OPTIONS"], HandleAzureOptions);
        app.MapPost("/hooks/azure", HandleAzureAsync);
    }

    private static async Task<IResult> HandleGitHubAsync(
        HttpContext context,
        IOptions<TwinHostOptions> options,
        IIntakePublisher intake,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        var rawBytes = await ReadRawBodyAsync(context.Request, ct);
        var signature = context.Request.Headers["X-Hub-Signature-256"].FirstOrDefault();
        if (!WebhookSignatureVerifier.VerifyGitHubSignature(rawBytes, signature, options.Value.WebhookSecret))
        {
            return Results.Unauthorized();
        }

        return await CaptureAsync(context, intake, loggerFactory, EventSourceKind.GitHub, IntakeKind.GitHubWebhook, rawBytes, timeProvider, ct);
    }

    private static async Task<IResult> HandleOctopusAsync(
        HttpContext context,
        IOptions<TwinHostOptions> options,
        IIntakePublisher intake,
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
        return await CaptureAsync(context, intake, loggerFactory, EventSourceKind.Octopus, IntakeKind.OctopusWebhook, rawBytes, timeProvider, ct);
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
        IOptions<TwinHostOptions> options,
        IIntakePublisher intake,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        var rawBytes = await ReadRawBodyAsync(context.Request, ct);
        var rawJson = Encoding.UTF8.GetString(rawBytes);

        // The Event Grid subscription handshake is answered synchronously and is not an observation.
        var validation = AzureEventNormalizer.NormalizeEventGridWebhook(rawJson, timeProvider.GetUtcNow()).ValidationResponse;
        if (!string.IsNullOrWhiteSpace(validation))
        {
            return Results.Ok(new { validationResponse = validation });
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

        return await CaptureAsync(context, intake, loggerFactory, EventSourceKind.Azure, IntakeKind.AzureWebhook, rawBytes, timeProvider, ct);
    }

    private static async Task<IResult> CaptureAsync(
        HttpContext context,
        IIntakePublisher intake,
        ILoggerFactory loggerFactory,
        EventSourceKind source,
        IntakeKind kind,
        byte[] rawBytes,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        var headers = context.Request.Headers.Select(h => new KeyValuePair<string, string?>(h.Key, h.Value.ToString()));
        var raw = RawIntakeEvent.ForWebhook(source, kind, Encoding.UTF8.GetString(rawBytes), headers, timeProvider.GetUtcNow());
        try
        {
            var receipt = await intake.CaptureAsync(raw, ct);
            return Results.Ok(new { captured = true, rawEventId = receipt.RawEventId, dedupeKey = raw.DedupeKey });
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogCaptureFailed(loggerFactory.CreateLogger("WebhookIntake"), ex, source);
            return Results.Problem("Event could not be durably captured; please redeliver.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<byte[]> ReadRawBodyAsync(HttpRequest request, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await request.Body.CopyToAsync(ms, ct);
        return ms.ToArray();
    }
}
