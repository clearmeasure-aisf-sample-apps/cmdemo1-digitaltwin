using DigitalTwin.Core.BuildAndArtifact;
using DigitalTwin.Core.Observation;
using DigitalTwin.Core.ReleaseAndDelivery;
using DigitalTwin.Core.ReliabilityAndDrift;
using DigitalTwin.Core.RuntimeAndPlatform;
using DigitalTwin.Core.SourceAndChange;
using DigitalTwin.Core.Work;
using Microsoft.EntityFrameworkCore;

namespace DigitalTwin.Persistence;

public class TwinDbContext : DbContext
{
    public TwinDbContext(DbContextOptions<TwinDbContext> options) : base(options)
    {
    }

    protected TwinDbContext(DbContextOptions options) : base(options)
    {
    }

    /// <summary>Schema of every table; null is the provider default (dbo). Shadow rebuilds use their own schema.</summary>
    protected virtual string? Schema => null;

    public const string StateAsOfIndexName = "IX_ObservedEvents_StateAsOf";
    public const string ChainIndexName = "IX_ObservedEvents_ChainId";

    public DbSet<ObservedEvent> ObservedEvents => Set<ObservedEvent>();
    public DbSet<IngestionCursor> IngestionCursors => Set<IngestionCursor>();
    public DbSet<WebhookRegistrationStatus> WebhookRegistrationStatuses => Set<WebhookRegistrationStatus>();
    public DbSet<RepositorySnapshot> Repositories => Set<RepositorySnapshot>();
    public DbSet<SourceChangeSnapshot> SourceChanges => Set<SourceChangeSnapshot>();
    public DbSet<PipelineRunSnapshot> PipelineRuns => Set<PipelineRunSnapshot>();
    public DbSet<ArtifactPackageSnapshot> Artifacts => Set<ArtifactPackageSnapshot>();
    public DbSet<WorkItemSnapshot> WorkItems => Set<WorkItemSnapshot>();
    public DbSet<ReleaseSnapshot> Releases => Set<ReleaseSnapshot>();
    public DbSet<DeliveryExecutionSnapshot> Deliveries => Set<DeliveryExecutionSnapshot>();
    public DbSet<RunbookExecutionSnapshot> RunbookExecutions => Set<RunbookExecutionSnapshot>();
    public DbSet<DeploymentFreezeSnapshot> DeploymentFreezes => Set<DeploymentFreezeSnapshot>();
    public DbSet<DesiredSystemConfigSnapshot> DesiredSystemConfigs => Set<DesiredSystemConfigSnapshot>();
    public DbSet<EnvironmentInstanceSnapshot> EnvironmentInstances => Set<EnvironmentInstanceSnapshot>();
    public DbSet<WorkloadRevisionSnapshot> WorkloadRevisions => Set<WorkloadRevisionSnapshot>();
    public DbSet<AzureResourceSnapshot> AzureResources => Set<AzureResourceSnapshot>();
    public DbSet<HealthProbeSnapshot> HealthProbes => Set<HealthProbeSnapshot>();
    public DbSet<DriftFinding> DriftFindings => Set<DriftFinding>();
    public DbSet<ProjectionVersion> ProjectionVersions => Set<ProjectionVersion>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        if (Schema is not null)
        {
            modelBuilder.HasDefaultSchema(Schema);
        }
        modelBuilder.Entity<ObservedEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
            e.HasIndex(x => x.OccurredAt);
            e.HasIndex(x => new { x.Source, x.OccurredAt });
            e.HasIndex(x => new { x.EventType, x.SubjectType, x.SubjectId, x.OccurredAt }).HasDatabaseName(StateAsOfIndexName);
            e.HasIndex(x => x.ChainId).HasDatabaseName(ChainIndexName);
            e.Property(x => x.IdempotencyKey).HasMaxLength(256);
            e.Property(x => x.EventId).HasMaxLength(128);
            e.Property(x => x.SourceEventId).HasMaxLength(200);
            e.Property(x => x.EventType).HasMaxLength(160);
            e.Property(x => x.SubjectType).HasMaxLength(80);
            e.Property(x => x.SubjectId).HasMaxLength(200);
            e.Property(x => x.Predicate).HasMaxLength(120);
            e.Property(x => x.PipelineId).HasMaxLength(80);
            e.Property(x => x.StageName).HasMaxLength(64);
            e.Property(x => x.DeployableName).HasMaxLength(64);
            e.Property(x => x.Actor).HasMaxLength(128);
            e.Property(x => x.CorrelationId).HasMaxLength(200);
            e.Property(x => x.ChainId).HasMaxLength(200);
            e.Property(x => x.Summary).HasMaxLength(1000);
        });

        modelBuilder.Entity<IngestionCursor>(e =>
        {
            e.HasKey(x => x.SourceKey);
            e.Property(x => x.SourceKey).HasMaxLength(120);
            e.Property(x => x.Status).HasMaxLength(40);
        });

        modelBuilder.Entity<WebhookRegistrationStatus>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(160);
        });

        modelBuilder.Entity<RepositorySnapshot>(e =>
        {
            e.HasKey(x => x.Name);
            e.Property(x => x.Name).HasMaxLength(120);
        });

        modelBuilder.Entity<SourceChangeSnapshot>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(160);
        });

        modelBuilder.Entity<PipelineRunSnapshot>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(160);
        });

        modelBuilder.Entity<ArtifactPackageSnapshot>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(240);
        });

        modelBuilder.Entity<WorkItemSnapshot>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(160);
        });

        modelBuilder.Entity<ReleaseSnapshot>(e =>
        {
            e.HasKey(x => x.ReleaseId);
            e.Property(x => x.ReleaseId).HasMaxLength(120);
        });

        modelBuilder.Entity<DeliveryExecutionSnapshot>(e =>
        {
            e.HasKey(x => x.DeploymentId);
            e.Property(x => x.DeploymentId).HasMaxLength(120);
        });

        modelBuilder.Entity<RunbookExecutionSnapshot>(e =>
        {
            e.HasKey(x => x.TaskId);
            e.Property(x => x.TaskId).HasMaxLength(120);
        });

        modelBuilder.Entity<DeploymentFreezeSnapshot>(e =>
        {
            e.HasKey(x => x.FreezeId);
            e.Property(x => x.FreezeId).HasMaxLength(120);
        });

        modelBuilder.Entity<DesiredSystemConfigSnapshot>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
        });

        modelBuilder.Entity<EnvironmentInstanceSnapshot>(e =>
        {
            e.HasKey(x => x.StageName);
            e.Property(x => x.StageName).HasMaxLength(64);
        });

        modelBuilder.Entity<WorkloadRevisionSnapshot>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(160);
        });

        modelBuilder.Entity<AzureResourceSnapshot>(e =>
        {
            e.HasKey(x => x.ResourceId);
            e.Property(x => x.ResourceId).HasMaxLength(512);
        });

        modelBuilder.Entity<HealthProbeSnapshot>(e =>
        {
            e.HasKey(x => x.TargetKey);
            e.Property(x => x.TargetKey).HasMaxLength(160);
        });

        modelBuilder.Entity<ProjectionVersion>(e =>
        {
            e.ToTable("TwinProjectionVersions");
            e.HasKey(x => new { x.Kind, x.Key });
            e.Property(x => x.Kind).HasMaxLength(64);
            e.Property(x => x.Key).HasMaxLength(256);
            e.Property(x => x.EventKey).HasMaxLength(256);
            e.Property(x => x.ContentHash).HasMaxLength(32);
        });

        modelBuilder.Entity<DriftFinding>(e =>
        {
            e.HasKey(x => x.DriftKey);
            e.Property(x => x.DriftKey).HasMaxLength(200);
        });
    }
}

/// <summary>The same model in the <c>shadow</c> schema: a rebuild writes here, then swaps the tables into dbo.</summary>
public sealed class ShadowTwinDbContext(DbContextOptions<ShadowTwinDbContext> options) : TwinDbContext(options)
{
    public const string SchemaName = "shadow";

    protected override string Schema => SchemaName;
}
