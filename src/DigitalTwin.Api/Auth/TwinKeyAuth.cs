using DigitalTwin.Core.Normalization;
using Microsoft.Extensions.Options;

namespace DigitalTwin.Api.Auth;

public static class TwinKeyAuth
{
    public const string HeaderName = "X-Twin-Key";

    public static bool IsAuthorized(HttpContext context, TwinHostOptions options, bool required)
    {
        var provided = context.Request.Headers[HeaderName].FirstOrDefault()
            ?? context.Request.Headers.Authorization.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(options.TwinApiKey))
        {
            return !required;
        }

        if (string.IsNullOrWhiteSpace(provided))
        {
            return !required;
        }

        return WebhookSignatureVerifier.VerifySharedSecret(provided, options.TwinApiKey);
    }

    public static IEndpointFilter RequireTwinKey()
    {
        return new TwinKeyEndpointFilter();
    }

    private sealed class TwinKeyEndpointFilter : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var opts = context.HttpContext.RequestServices.GetRequiredService<IOptions<TwinHostOptions>>().Value;
            if (!IsAuthorized(context.HttpContext, opts, required: true))
            {
                return Results.Json(new { error = "Unauthorized: valid X-Twin-Key header required." }, statusCode: StatusCodes.Status401Unauthorized);
            }
            return await next(context);
        }
    }

    public static IEndpointFilter ValidateOptionalTwinKey()
    {
        return new OptionalTwinKeyEndpointFilter();
    }

    private sealed class OptionalTwinKeyEndpointFilter : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var opts = context.HttpContext.RequestServices.GetRequiredService<IOptions<TwinHostOptions>>().Value;
            if (!IsAuthorized(context.HttpContext, opts, required: false))
            {
                return Results.Json(new { error = "Unauthorized: invalid X-Twin-Key header." }, statusCode: StatusCodes.Status401Unauthorized);
            }
            return await next(context);
        }
    }
}
