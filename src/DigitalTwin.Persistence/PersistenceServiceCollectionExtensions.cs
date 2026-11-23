using DigitalTwin.Core.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DigitalTwin.Persistence;

public static partial class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddTwinPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = ResolveConnectionString(configuration);
        var useSqlServer = IsSqlServer(connectionString);

        void Configure(DbContextOptionsBuilder dbOpts)
        {
            if (useSqlServer)
            {
                dbOpts.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure(5, TimeSpan.FromSeconds(10), null));
            }
            else
            {
                dbOpts.UseSqlite(connectionString);
            }
        }
        services.AddDbContextFactory<TwinDbContext>(Configure);
        // Rebuild batches write thousands of rows per statement on a 5 DTU tier: allow minutes, not 30 s.
        services.AddDbContextFactory<ShadowTwinDbContext>(dbOpts =>
        {
            Configure(dbOpts);
            if (useSqlServer)
            {
                dbOpts.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure(5, TimeSpan.FromSeconds(10), null).CommandTimeout(300));
            }
        });
        services.AddSingleton<ITwinStore, EfTwinStore>();
        services.AddSingleton<IRebuildTarget, ShadowRebuildTarget>();
        return services;
    }

    public static string ResolveConnectionString(IConfiguration configuration)
        => new[]
        {
            configuration.GetConnectionString("TwinDb"),
            configuration["TWIN_DB_CONNECTION_STRING"],
            configuration["Twin:ConnectionString"]
        }.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "Data Source=twin.db";

    /// <summary>SQLite only for file (*.db) or in-memory data sources; everything else is SQL Server.</summary>
    public static bool IsSqlServer(string connectionString)
        => !(connectionString.Contains(".db", StringComparison.OrdinalIgnoreCase)
            || connectionString.Contains(":memory:", StringComparison.OrdinalIgnoreCase)
            || connectionString.Contains("Mode=Memory", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Creates the twin model tables. Must run before any other component (Wolverine, raw intake) creates
    /// tables, because EnsureCreated is a no-op once a database contains any table.
    /// </summary>
    public static async Task InitializeTwinPersistenceAsync(this IServiceProvider services, ILogger logger, CancellationToken ct = default)
    {
        var dbFactory = services.GetRequiredService<IDbContextFactory<TwinDbContext>>();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                await db.Database.EnsureCreatedAsync(ct);
                if (db.Database.IsSqlServer())
                {
                    // Tables added after the first deploy: EnsureCreated never alters an existing database.
                    await db.Database.ExecuteSqlRawAsync(ProjectionVersionsDdl, ct);
                    await db.Database.ExecuteSqlRawAsync(StateAsOfIndexDdl, ct);
                    await db.Database.ExecuteSqlRawAsync(ChainIndexDdl, ct);
                    await db.Database.ExecuteSqlRawAsync(TwinDbContext.RecentJournalIndexDdl("dbo"), ct);
                    // A rebuild interrupted by a restart left its shadow behind; live tables were never touched.
                    await services.GetRequiredService<IRebuildTarget>().DiscardAsync(ct);
                }
                return;
            }
            catch (Exception ex) when (attempt < 6)
            {
                LogDatabaseRetry(logger, attempt, ex);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private const string ProjectionVersionsDdl = """
        IF OBJECT_ID('dbo.TwinProjectionVersions') IS NULL
        CREATE TABLE dbo.TwinProjectionVersions (
            [Kind] nvarchar(64) NOT NULL,
            [Key] nvarchar(256) NOT NULL,
            [ObservedAt] datetimeoffset NOT NULL,
            [EventKey] nvarchar(256) NOT NULL,
            [ContentHash] nvarchar(32) NOT NULL,
            CONSTRAINT PK_TwinProjectionVersions PRIMARY KEY ([Kind], [Key])
        );
        """;

    private const string StateAsOfIndexDdl = $"""
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = '{TwinDbContext.StateAsOfIndexName}' AND object_id = OBJECT_ID('dbo.ObservedEvents'))
        CREATE INDEX [{TwinDbContext.StateAsOfIndexName}] ON dbo.ObservedEvents ([EventType], [SubjectType], [SubjectId], [OccurredAt]);
        """;

    private const string ChainIndexDdl = $"""
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = '{TwinDbContext.ChainIndexName}' AND object_id = OBJECT_ID('dbo.ObservedEvents'))
        CREATE INDEX [{TwinDbContext.ChainIndexName}] ON dbo.ObservedEvents ([ChainId]);
        """;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Twin database initialization attempt {Attempt} failed; retrying in 5s")]
    private static partial void LogDatabaseRetry(ILogger logger, int attempt, Exception ex);
}
