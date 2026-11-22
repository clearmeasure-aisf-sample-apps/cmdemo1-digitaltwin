using DigitalTwin.Core.Observation;
using DigitalTwin.Core.Ports;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace DigitalTwin.Persistence;

/// <summary>
/// Rebuilds into the <c>shadow</c> schema (created from the EF model, so indexes and keys match), then swaps every
/// rebuilt table into dbo with <c>ALTER SCHEMA … TRANSFER</c> in one transaction: metadata-only and all-or-nothing.
/// Ingestion cursors are operational and never swapped.
/// </summary>
public sealed class ShadowRebuildTarget(IDbContextFactory<TwinDbContext> live, IDbContextFactory<ShadowTwinDbContext> shadow) : IRebuildTarget
{
    private const string RetiredSchema = "retired";

    public bool IsSupported
    {
        get
        {
            using var db = live.CreateDbContext();
            return db.Database.IsSqlServer();
        }
    }

    public async Task<ITwinStore> CreateAsync(CancellationToken ct)
    {
        await DiscardAsync(ct);
        await using var db = await shadow.CreateDbContextAsync(ct);
        var script = db.Database.GenerateCreateScript();
        foreach (var batch in script.Split(["\nGO", "\r\nGO"], StringSplitOptions.RemoveEmptyEntries).Select(b => b.Trim()).Where(b => b.Length > 0))
        {
            await db.Database.ExecuteSqlRawAsync(batch, ct);
        }
        return new EfTwinStore(new AsTwinContextFactory(shadow));
    }

    public async Task SwapAsync(CancellationToken ct)
    {
        await using var db = await live.CreateDbContextAsync(ct);
        var tables = SwappedTables(db);
        await DropTablesAsync(db, RetiredSchema, ct);

        var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        await using (var ensure = new SqlCommand($"IF SCHEMA_ID('{RetiredSchema}') IS NULL EXEC('CREATE SCHEMA [{RetiredSchema}]')", conn))
        {
            await ensure.ExecuteNonQueryAsync(ct);
        }
        await using (var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct))
        {
            foreach (var table in tables)
            {
                await using var retire = new SqlCommand($"ALTER SCHEMA [{RetiredSchema}] TRANSFER [dbo].[{table}]", conn, tx);
                await retire.ExecuteNonQueryAsync(ct);
                await using var promote = new SqlCommand($"ALTER SCHEMA [dbo] TRANSFER [{ShadowTwinDbContext.SchemaName}].[{table}]", conn, tx);
                await promote.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        await DropTablesAsync(db, RetiredSchema, ct);
        await DropTablesAsync(db, ShadowTwinDbContext.SchemaName, ct);
    }

    public async Task DiscardAsync(CancellationToken ct)
    {
        await using var db = await live.CreateDbContextAsync(ct);
        if (!db.Database.IsSqlServer())
        {
            return;
        }
        await DropTablesAsync(db, ShadowTwinDbContext.SchemaName, ct);
        await DropTablesAsync(db, RetiredSchema, ct);
    }

    private static List<string> SwappedTables(TwinDbContext db)
        => db.Model.GetEntityTypes()
            .Where(e => e.ClrType != typeof(IngestionCursor))
            .Select(e => e.GetTableName()!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    private static async Task DropTablesAsync(TwinDbContext db, string schema, CancellationToken ct)
    {
        var tables = await db.Database.SqlQueryRaw<string>(
            "SELECT t.name AS [Value] FROM sys.tables t WHERE t.schema_id = SCHEMA_ID({0})", schema).ToListAsync(ct);
        foreach (var table in tables)
        {
            // Names come from sys.tables in our own schemas.
            var drop = "DROP TABLE [" + schema + "].[" + table + "]";
            await db.Database.ExecuteSqlRawAsync(drop, ct);
        }
    }

    private sealed class AsTwinContextFactory(IDbContextFactory<ShadowTwinDbContext> inner) : IDbContextFactory<TwinDbContext>
    {
        public TwinDbContext CreateDbContext() => inner.CreateDbContext();
    }
}
