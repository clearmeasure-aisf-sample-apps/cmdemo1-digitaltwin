using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Xunit;

namespace DigitalTwin.Tests.Infrastructure;

/// <summary>
/// One SQL Server container (Testcontainers) per test run; each test host gets its own fresh database.
/// Set TWIN_TEST_SQLSERVER to an existing server connection string to skip Docker.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private MsSqlContainer? _container;
    private string _serverConnectionString = string.Empty;

    public async Task InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable("TWIN_TEST_SQLSERVER");
        if (!string.IsNullOrWhiteSpace(external))
        {
            _serverConnectionString = external;
            return;
        }

        _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
        await _container.StartAsync();
        _serverConnectionString = _container.GetConnectionString();
    }

    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"twin_test_{Guid.NewGuid():N}";
        await using (var conn = new SqlConnection(_serverConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new SqlCommand($"CREATE DATABASE [{name}]", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        return new SqlConnectionStringBuilder(_serverConnectionString)
        {
            InitialCatalog = name,
            TrustServerCertificate = true
        }.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class SqlServerTests : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "sqlserver";
}
