using System.Text.Json;
using DigitalTwin.Domain.ReliabilityAndDrift;
using DigitalTwin.Domain.RuntimeAndPlatform;
using DigitalTwin.Domain.SourceAndChange;

namespace DigitalTwin.Domain.ReliabilityAndDrift;

public sealed record DesiredDeployableSpec(
    string Name,
    string Hosting,
    bool Retired,
    IReadOnlyList<string>? Environments);

public static class DriftEvaluator
{
    public static IReadOnlyList<DriftFinding> Evaluate(
        DesiredSystemConfigSnapshot? desiredConfig,
        IReadOnlyList<WorkloadRevisionSnapshot> workloads,
        IReadOnlyList<HealthProbeSnapshot> probes,
        IReadOnlyList<AzureResourceSnapshot> azureResources,
        IReadOnlyList<RepositorySnapshot> repositories,
        DateTimeOffset now)
    {
        var findings = new List<DriftFinding>();

        if (desiredConfig is null || string.IsNullOrWhiteSpace(desiredConfig.SystemJson))
        {
            findings.Add(new DriftFinding
            {
                DriftKey = "desired-state:missing",
                Category = DriftCategory.MissingResource,
                Severity = DriftSeverity.Warning,
                StageName = "system",
                ExpectedValue = "system.json loaded",
                ObservedValue = "missing",
                Summary = "Desired state from cmdemo1-system/system.json has not been synced yet.",
                RemediationHint = "Trigger backfill to read cmdemo1-system/system.json.",
                IsActive = true,
                DetectedAt = now
            });
            return findings;
        }

        var pinnedByStage = ParsePinnedVersions(desiredConfig.PinnedVersionsJson);
        var specs = ParseDeployableSpecs(desiredConfig.SystemJson);
        var activeStages = new[] { "tdd", "uat", "prod" };

        // 1. Compare pinned versions (GitOps versions.json) vs observed running versions (Container App image tag / SWA build-facts)
        foreach (var stage in activeStages)
        {
            pinnedByStage.TryGetValue(stage, out var stagePins);
            stagePins ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var spec in specs)
            {
                var appliesToStage = !spec.Retired &&
                    (spec.Environments is null || spec.Environments.Count == 0 ||
                     spec.Environments.Contains(stage, StringComparer.OrdinalIgnoreCase));

                var workload = workloads.FirstOrDefault(w =>
                    w.StageName.Equals(stage, StringComparison.OrdinalIgnoreCase) &&
                    w.DeployableName.Equals(spec.Name, StringComparison.OrdinalIgnoreCase));

                if (!appliesToStage)
                {
                    if (workload is not null)
                    {
                        findings.Add(new DriftFinding
                        {
                            DriftKey = $"retired-present:{stage}:{spec.Name}",
                            Category = DriftCategory.RetiredResourcePresent,
                            Severity = DriftSeverity.Warning,
                            StageName = stage,
                            DeployableName = spec.Name,
                            ExpectedValue = "absent (retired/unplaced)",
                            ObservedValue = $"{workload.ResourceName} ({workload.RunningVersion ?? "unknown"})",
                            Summary = $"Retired or unplaced deployable '{spec.Name}' still has runtime resource '{workload.ResourceName}' in {stage}.",
                            RemediationHint = "Run cmdemo1-system 'Apply environment' to prune unmanaged stack resources.",
                            IsActive = true,
                            DetectedAt = now
                        });
                    }
                    continue;
                }

                if (workload is null)
                {
                    findings.Add(new DriftFinding
                    {
                        DriftKey = $"missing-workload:{stage}:{spec.Name}",
                        Category = DriftCategory.MissingResource,
                        Severity = DriftSeverity.Critical,
                        StageName = stage,
                        DeployableName = spec.Name,
                        ExpectedValue = $"Running {spec.Hosting} for {spec.Name}",
                        ObservedValue = "not found in Azure Resource Graph",
                        Summary = $"Expected {spec.Hosting} workload for '{spec.Name}' in {stage} was not observed in Azure.",
                        RemediationHint = "Check deployment stack and Octopus system release for this stage.",
                        IsActive = true,
                        DetectedAt = now
                    });
                    continue;
                }

                if (stagePins.TryGetValue(spec.Name, out var expectedVersion) && !string.IsNullOrWhiteSpace(expectedVersion))
                {
                    var probeForTarget = probes.FirstOrDefault(p =>
                        p.StageName.Equals(stage, StringComparison.OrdinalIgnoreCase) &&
                        p.DeployableName.Equals(spec.Name, StringComparison.OrdinalIgnoreCase));

                    var observedVersion = !string.IsNullOrWhiteSpace(workload.RunningVersion)
                        ? workload.RunningVersion
                        : probeForTarget?.ReportedVersion;

                    if (!string.IsNullOrWhiteSpace(observedVersion) &&
                        !string.Equals(expectedVersion, observedVersion, StringComparison.OrdinalIgnoreCase))
                    {
                        findings.Add(new DriftFinding
                        {
                            DriftKey = $"version-mismatch:{stage}:{spec.Name}",
                            Category = DriftCategory.VersionMismatch,
                            Severity = DriftSeverity.Critical,
                            StageName = stage,
                            DeployableName = spec.Name,
                            ExpectedValue = expectedVersion,
                            ObservedValue = observedVersion,
                            Summary = $"{spec.Name} in {stage} is pinned at {expectedVersion} in GitOps, but Azure is serving {observedVersion}.",
                            RemediationHint = "Check in-flight Octopus deployment or re-run 'Update deployable' / 'Revert pin'.",
                            IsActive = true,
                            DetectedAt = now
                        });
                    }
                }

                // Revision & provisioning state health check for Container Apps
                if (workload.HostingType.Equals("containerapp", StringComparison.OrdinalIgnoreCase))
                {
                    var degraded = string.Equals(workload.ProvisioningState, "Failed", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(workload.HealthState, "Unhealthy", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(workload.RunningState, "Failed", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(workload.RunningState, "Degraded", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(workload.RunningState, "ActivationFailed", StringComparison.OrdinalIgnoreCase);

                    if (degraded)
                    {
                        findings.Add(new DriftFinding
                        {
                            DriftKey = $"revision-degraded:{stage}:{spec.Name}",
                            Category = DriftCategory.RevisionDegraded,
                            Severity = DriftSeverity.Critical,
                            StageName = stage,
                            DeployableName = spec.Name,
                            ExpectedValue = "Provisioned / Running / Healthy",
                            ObservedValue = $"{workload.ProvisioningState}/{workload.RunningState}/{workload.HealthState}",
                            Summary = $"Container App {workload.ResourceName} revision {workload.ActiveRevisionName} is {workload.RunningState ?? workload.HealthState}.",
                            RemediationHint = "Inspect Container App revision replica logs in Azure Portal or Octopus.",
                            IsActive = true,
                            DetectedAt = now
                        });
                    }
                }
            }

            // Pending promotion check (uat/prod behind tdd)
            if (!stage.Equals("tdd", StringComparison.OrdinalIgnoreCase) &&
                pinnedByStage.TryGetValue("tdd", out var tddPins))
            {
                foreach (var (depName, tddVersion) in tddPins)
                {
                    if (stagePins.TryGetValue(depName, out var stageVersion) &&
                        !string.Equals(tddVersion, stageVersion, StringComparison.OrdinalIgnoreCase))
                    {
                        findings.Add(new DriftFinding
                        {
                            DriftKey = $"pending-promotion:{stage}:{depName}",
                            Category = DriftCategory.PendingPromotion,
                            Severity = DriftSeverity.Info,
                            StageName = stage,
                            DeployableName = depName,
                            ExpectedValue = $"{tddVersion} (pinned in tdd)",
                            ObservedValue = stageVersion,
                            Summary = $"{depName} in {stage} ({stageVersion}) trails tdd ({tddVersion}).",
                            RemediationHint = "Normal when awaiting sign-off/auto-promote; promote in Octopus when ready.",
                            IsActive = true,
                            DetectedAt = now
                        });
                    }
                }
            }
        }

        // 2. Endpoint health probe failures
        foreach (var probe in probes.Where(p => !p.IsHealthy))
        {
            findings.Add(new DriftFinding
            {
                DriftKey = $"unhealthy-endpoint:{probe.StageName}:{probe.DeployableName}",
                Category = DriftCategory.UnhealthyEndpoint,
                Severity = DriftSeverity.Critical,
                StageName = probe.StageName,
                DeployableName = probe.DeployableName,
                ExpectedValue = "HTTP 200 Healthy",
                ObservedValue = $"HTTP {probe.HttpStatusCode} ({probe.ErrorMessage ?? "unhealthy"})",
                Summary = $"{probe.DeployableName} endpoint in {probe.StageName} failed health probe ({probe.Url}{probe.HealthPath}).",
                RemediationHint = "Check Container App replicas, SQL availability, or Static Web App status.",
                IsActive = true,
                DetectedAt = now
            });
        }

        // 3. Retired Azure resources lingering in RGs
        foreach (var res in azureResources.Where(r => !r.IsTwinOwnResource))
        {
            var lower = res.Name.ToLowerInvariant();
            if (lower.Contains("-aisf", StringComparison.Ordinal) ||
                lower.Contains("-fworker", StringComparison.Ordinal) ||
                lower.Contains("fwrunner", StringComparison.Ordinal) ||
                lower.Contains("app2-ui", StringComparison.Ordinal))
            {
                findings.Add(new DriftFinding
                {
                    DriftKey = $"retired-resource:{res.Name}",
                    Category = DriftCategory.RetiredResourcePresent,
                    Severity = DriftSeverity.Warning,
                    StageName = res.StageName ?? "unknown",
                    ResourceId = res.ResourceId,
                    ExpectedValue = "deleted",
                    ObservedValue = res.Name,
                    Summary = $"Retired legacy resource '{res.Name}' ({res.Type}) still exists in {res.ResourceGroup}.",
                    RemediationHint = "Clean up via cmdemo1-system stack apply or cleanup script.",
                    IsActive = true,
                    DetectedAt = now
                });
            }
        }

        // 4. Repository webhook coverage gaps
        foreach (var repo in repositories.Where(r => !r.WebhookRegistered))
        {
            findings.Add(new DriftFinding
            {
                DriftKey = $"webhook-gap:{repo.Name}",
                Category = DriftCategory.WebhookGap,
                Severity = DriftSeverity.Warning,
                StageName = "github",
                DeployableName = repo.Name,
                ExpectedValue = "Active repo webhook -> /hooks/github",
                ObservedValue = "missing or inactive",
                Summary = $"GitHub repository '{repo.Name}' does not have an active digital twin webhook registered.",
                RemediationHint = "Run webhook registration step in cmdemo1-digitaltwin workflow.",
                IsActive = true,
                DetectedAt = now
            });
        }

        return findings;
    }

    public static IReadOnlyDictionary<string, Dictionary<string, string>> ParsePinnedVersions(string? pinnedVersionsJson)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(pinnedVersionsJson))
        {
            return result;
        }

        try
        {
            using var doc = JsonDocument.Parse(pinnedVersionsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return result;
            }
            foreach (var envProp in doc.RootElement.EnumerateObject())
            {
                if (envProp.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var appProp in envProp.Value.EnumerateObject())
                {
                    if (appProp.Value.ValueKind == JsonValueKind.String && appProp.Value.GetString() is { } v)
                    {
                        map[appProp.Name] = v;
                    }
                }
                result[envProp.Name] = map;
            }
        }
        catch (JsonException)
        {
            return result;
        }
        return result;
    }

    public static IReadOnlyList<DesiredDeployableSpec> ParseDeployableSpecs(string systemJson)
    {
        var list = new List<DesiredDeployableSpec>();
        try
        {
            using var doc = JsonDocument.Parse(systemJson);
            if (!doc.RootElement.TryGetProperty("deployables", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                return list;
            }
            foreach (var item in arr.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }
                var hosting = item.TryGetProperty("hosting", out var h) ? h.GetString() ?? "containerapp" : "containerapp";
                var retired = item.TryGetProperty("retired", out var r) && r.ValueKind == JsonValueKind.True;
                List<string>? envs = null;
                if (item.TryGetProperty("environments", out var e) && e.ValueKind == JsonValueKind.Array)
                {
                    envs = [.. e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)];
                }
                list.Add(new DesiredDeployableSpec(name, hosting, retired, envs));
            }
        }
        catch (JsonException)
        {
            return list;
        }
        return list;
    }
}
