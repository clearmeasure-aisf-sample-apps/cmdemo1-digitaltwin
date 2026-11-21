using System.Text.Json.Serialization;
using DigitalTwin.Api.Auth;
using DigitalTwin.Api.Endpoints;
using DigitalTwin.Api.Mcp;
using DigitalTwin.Api.Realtime;
using DigitalTwin.Application;
using DigitalTwin.Azure;
using DigitalTwin.Core.Ports;
using DigitalTwin.GitHub;
using DigitalTwin.Messaging;
using DigitalTwin.Octopus;
using DigitalTwin.Persistence;
using DigitalTwin.Probes;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(opts =>
{
    opts.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddSignalR()
    .AddJsonProtocol(opts =>
    {
        opts.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });
builder.Services.AddSingleton<RealtimeEventPublisher>();
builder.Services.AddSingleton<IRealtimeEventPublisher>(sp => sp.GetRequiredService<RealtimeEventPublisher>());

// Composition root: the only place where outer adapters are bound to Core ports.
builder.Services.Configure<TwinHostOptions>(opts =>
{
    opts.TwinApiKey = FirstNonEmpty(builder.Configuration["TWIN_API_KEY"], builder.Configuration["Twin:TwinApiKey"]);
    opts.WebhookSecret = FirstNonEmpty(builder.Configuration["WEBHOOK_SECRET"], builder.Configuration["Twin:WebhookSecret"]);
    opts.TwinPublicUrl = FirstNonEmpty(
        builder.Configuration["TWIN_PUBLIC_URL"],
        string.IsNullOrWhiteSpace(builder.Configuration["CONTAINER_APP_HOSTNAME"]) ? null : $"https://{builder.Configuration["CONTAINER_APP_HOSTNAME"]!.Trim()}",
        builder.Configuration["Twin:TwinPublicUrl"]);
});
builder.Services
    .AddTwinApplication(builder.Configuration)
    .AddTwinPersistence(builder.Configuration)
    .AddTwinGitHub(builder.Configuration)
    .AddTwinOctopus(builder.Configuration)
    .AddTwinAzure(builder.Configuration)
    .AddTwinHealthProbes();
builder.AddTwinMessaging();

builder.Services.AddMcpServer(opts =>
{
    opts.ServerInfo = new()
    {
        Name = "cmdemo1-digitaltwin",
        Version = "1.0.0"
    };
})
.WithHttpTransport(httpOpts => httpOpts.Stateless = true)
.WithTools<TwinMcpTools>();

var app = builder.Build();

// Order matters: model tables first (EF EnsureCreated is a no-op once any table exists), then raw intake;
// Wolverine creates its own schema when the host starts.
var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
await app.Services.InitializeTwinPersistenceAsync(startupLogger);
await app.Services.InitializeTwinMessagingAsync();

app.UseDefaultFiles();
app.UseStaticFiles();

// Protect /mcp with X-Twin-Key
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase))
    {
        var opts = context.RequestServices.GetRequiredService<IOptions<TwinHostOptions>>().Value;
        if (!TwinKeyAuth.IsAuthorized(context, opts, required: true))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Unauthorized: valid X-Twin-Key header required for MCP." });
            return;
        }
    }
    await next();
});

app.MapHub<TwinHub>("/hubs/twin");
app.MapWebhookEndpoints();
app.MapTwinEndpoints();
app.MapMcp("/mcp");

app.Run();

public partial class Program
{
    private static string FirstNonEmpty(params string?[] candidates)
        => candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? string.Empty;
}
