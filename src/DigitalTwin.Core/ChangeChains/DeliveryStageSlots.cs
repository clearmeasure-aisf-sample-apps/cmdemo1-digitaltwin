using DigitalTwin.Core.Hierarchy;

namespace DigitalTwin.Core.ChangeChains;

/// <summary>
/// Maps the platform's delivery stages onto the chain's three delivery slots: the first stage, an optional middle stage and
/// production (the last). On cmdemo1 those are tdd, uat and prod.
/// </summary>
public static class DeliveryStageSlots
{
    public static ChainStageKind? KindOf(string? stageName)
    {
        var names = PlatformCatalog.DeliveryStages;
        var index = names.ToList().FindIndex(n => string.Equals(n, stageName, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return null;
        }

        return index == names.Count - 1 ? ChainStageKind.Prod : index == 0 ? ChainStageKind.Tdd : ChainStageKind.Uat;
    }

    public static IReadOnlyList<ChainStageKind> Kinds => PlatformCatalog.DeliveryStages.Select(n => KindOf(n)!.Value).ToList();

    /// <summary>The middle stage when the platform has three, else null.</summary>
    public static string? MiddleStage => PlatformCatalog.DeliveryStages.Count == 3 ? PlatformCatalog.DeliveryStages[1] : null;
}
