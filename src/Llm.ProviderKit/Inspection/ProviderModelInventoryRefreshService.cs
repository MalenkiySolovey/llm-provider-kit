using Llm.ProviderKit.Catalog;
using Llm.ProviderKit.Models;

namespace Llm.ProviderKit.Inspection;

/// <summary>Coordinates safe model discovery and exact-ID inventory merging.</summary>
public sealed class ProviderModelInventoryRefreshService
{
    private readonly ProviderInspectionService _inspection;

    public ProviderModelInventoryRefreshService(ProviderInspectionService inspection) =>
        _inspection = inspection ?? throw new ArgumentNullException(nameof(inspection));

    public async Task<ModelInventoryRefreshResult> RefreshAsync(ProviderDescriptor provider, string endpointId,
        string baseUrl, ProviderModelInventory inventory, DateTimeOffset? observedAt = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(inventory);
        var before = inventory.Models.Select(model => new InventoryEntryState(model.Id, model.Enabled, model.DiscoverySource,
            model.Availability, model.LastSeenAt)).ToArray();
        var priorDiscoveryStatus = inventory.DiscoveryStatus;
        var priorDiscoveryTime = inventory.LastDiscoveryAt;
        var observation = observedAt ?? DateTimeOffset.UtcNow;
        var discovery = await _inspection.DiscoverModelsAsync(provider, endpointId, baseUrl, cancellationToken).ConfigureAwait(false);
        if (!discovery.Succeeded)
        {
            return new ModelInventoryRefreshResult(false, 0, 0, 0,
                inventory.Models.Count(model => model.DiscoverySource is ModelDiscoverySource.Manual or ModelDiscoverySource.Mixed),
                false, discovery.Error, DateTimeOffset.UtcNow);
        }

        var merge = inventory.MergeDiscoveredModels(discovery.ModelIds, observation);
        var after = inventory.Models.Select(model => new InventoryEntryState(model.Id, model.Enabled, model.DiscoverySource,
            model.Availability, model.LastSeenAt)).ToArray();
        var changed = priorDiscoveryStatus != inventory.DiscoveryStatus || priorDiscoveryTime != inventory.LastDiscoveryAt ||
            !before.SequenceEqual(after);
        return new ModelInventoryRefreshResult(true, merge.ReportedCount, merge.ModelsAdded, merge.ModelsUpdated,
            merge.ManualModelsPreserved, changed, null, observation);
    }

    private sealed record InventoryEntryState(string Id, bool Enabled, ModelDiscoverySource Source,
        ModelAvailability Availability, DateTimeOffset? LastSeenAt);
}
