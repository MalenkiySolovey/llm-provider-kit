using System.Text.Json;
using System.Text.RegularExpressions;

namespace Llm.ProviderKit.Models;

public enum ModelDiscoverySource { CatalogKnown = 0, Discovered = 1, Manual = 2, Mixed = 3 }
public enum ModelAvailability { Unknown = 0, Reported = 1, NotReported = 2 }
public enum ModelDiscoveryStatus { Unknown = 0, Discovered = 1, Manual = 2, Mixed = 3 }

/// <summary>Cosmetic rendering only; the exact model ID remains the identity key.</summary>
public static partial class ModelDisplayName
{
    public static string FromId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var display = UppercaseBoundaryRegex().Replace(id.Replace('-', ' ').Replace('_', ' '), " ");
        return string.Join(' ', display.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(word =>
        {
            if (word.StartsWith(':')) return word;
            if (word.Equals("deepseek", StringComparison.OrdinalIgnoreCase)) return "DeepSeek";
            if (word.Length > 1 && char.ToLowerInvariant(word[0]) == 'v' && char.IsDigit(word[1])) return $"v{word[1..]}";
            if (word.Length == 1 && char.IsLetter(word[0])) return word.ToUpperInvariant();
            return char.ToUpperInvariant(word[0]) + word[1..];
        }));
    }

    [GeneratedRegex(@"(?<!^)(?=[A-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex UppercaseBoundaryRegex();
}

public sealed record ModelInventoryMergeResult(int ModelsAdded, int ModelsUpdated, int ManualModelsPreserved, int ReportedCount);

/// <summary>Explicit model metadata and extension facts. Unknown facts remain unknown.</summary>
public sealed class ProviderModelMetadata
{
    public long? ContextWindow { get; set; }
    public string? ContextEvidence { get; set; }
    public Dictionary<string, ModelCapabilityEvidence> Capabilities { get; set; } = new(StringComparer.Ordinal);
}

public enum ModelCapabilityEvidenceState { Unknown = 0, Documented = 1, Declared = 2, ProbePassed = 3, ProbeFailed = 4 }

public sealed record ModelCapabilityEvidence(
    ModelCapabilityEvidenceState State,
    string Source,
    DateTimeOffset Timestamp,
    string? Detail = null)
{
    public bool? Supported => State switch
    {
        ModelCapabilityEvidenceState.Documented or ModelCapabilityEvidenceState.Declared or ModelCapabilityEvidenceState.ProbePassed => true,
        ModelCapabilityEvidenceState.ProbeFailed => false,
        _ => null,
    };
}

/// <summary>Application-neutral override bag; consumers own interpretation of each exact key.</summary>
public sealed class ProviderModelOverrides
{
    public Dictionary<string, JsonElement> Values { get; set; } = new(StringComparer.Ordinal);
}

public sealed class ProviderModelItem
{
    public string Id { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public bool Enabled { get; set; } = true;
    public ModelDiscoverySource DiscoverySource { get; set; } = ModelDiscoverySource.CatalogKnown;
    public ModelAvailability Availability { get; set; } = ModelAvailability.Unknown;
    public DateTimeOffset? LastSeenAt { get; set; }
    public ProviderModelMetadata Metadata { get; set; } = new();
    public ProviderModelOverrides Overrides { get; set; } = new();
}

/// <summary>Deterministic inventory merge preserving manual settings and disabled state.</summary>
public sealed class ProviderModelInventory
{
    public List<ProviderModelItem> Models { get; set; } = [];
    public string? SelectedModelId { get; set; }
    public ModelDiscoveryStatus DiscoveryStatus { get; set; } = ModelDiscoveryStatus.Unknown;
    public DateTimeOffset? LastDiscoveryAt { get; set; }

    public IEnumerable<ProviderModelItem> EnabledModels => Models.Where(model => model.Enabled);

    public ModelInventoryMergeResult MergeDiscoveredModels(IEnumerable<string>? discoveredIds, DateTimeOffset? observedAt = null)
    {
        if (discoveredIds is null)
        {
            return new ModelInventoryMergeResult(0, 0, Models.Count(IsManual), 0);
        }

        var at = observedAt ?? DateTimeOffset.UtcNow;
        var byId = new Dictionary<string, ProviderModelItem>(StringComparer.Ordinal);
        foreach (var item in Models)
        {
            if (!string.IsNullOrEmpty(item.Id)) byId.TryAdd(item.Id, item);
        }

        DiscoveryStatus = ModelDiscoveryStatus.Discovered;
        LastDiscoveryAt = at;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var added = 0;
        var updated = 0;
        foreach (var id in discoveredIds)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            seen.Add(id);
            if (byId.TryGetValue(id, out var existing))
            {
                if (existing.Availability != ModelAvailability.Reported) updated++;
                existing.Availability = ModelAvailability.Reported;
                existing.LastSeenAt = at;
                if (existing.DiscoverySource == ModelDiscoverySource.Manual) existing.DiscoverySource = ModelDiscoverySource.Mixed;
                continue;
            }

            var discovered = new ProviderModelItem
            {
                Id = id,
                DisplayName = ModelDisplayName.FromId(id),
                Enabled = true,
                DiscoverySource = ModelDiscoverySource.Discovered,
                Availability = ModelAvailability.Reported,
                LastSeenAt = at,
            };
            Models.Add(discovered);
            byId.Add(id, discovered);
            added++;
        }

        foreach (var item in Models)
        {
            if (!seen.Contains(item.Id)) item.Availability = ModelAvailability.NotReported;
        }

        return new ModelInventoryMergeResult(added, updated, Models.Count(IsManual), seen.Count);
    }

    public IReadOnlyList<ProviderModelItem> GetDeterministicSnapshot() => Models
        .OrderBy(model => model.Id, StringComparer.Ordinal)
        .ToArray();

    private static bool IsManual(ProviderModelItem model) =>
        model.DiscoverySource is ModelDiscoverySource.Manual or ModelDiscoverySource.Mixed;
}

/// <summary>Transport limits for safe read-only provider inspection.</summary>
public sealed record ProviderTransportOptions(
    TimeSpan? Timeout = null,
    int? MaxResponseBytes = null,
    int? MaxRedirects = null)
{
    public TimeSpan EffectiveTimeout => Timeout.HasValue && Timeout.Value > TimeSpan.Zero && Timeout.Value <= TimeSpan.FromMinutes(1)
        ? Timeout.Value
        : TimeSpan.FromSeconds(15);
    public int EffectiveMaxResponseBytes => MaxResponseBytes is > 0 and <= 5 * 1024 * 1024 ? MaxResponseBytes.Value : 512 * 1024;
    public int EffectiveMaxRedirects => MaxRedirects is >= 0 and <= 5 ? MaxRedirects.Value : 3;
}

/// <summary>Portable endpoint configuration; credentials stay behind an injected credential source.</summary>
public sealed record ProviderEndpointProfile(
    string Id,
    string DisplayName,
    Uri BaseUri,
    string? ProviderId = null,
    string? RouteLabel = null,
    string? CatalogRouteId = null,
    ProviderTransportOptions? Transport = null,
    int InventoryRevision = 0);

public sealed record ModelInventoryRefreshResult(
    bool Succeeded,
    int ReportedCount,
    int Added,
    int Updated,
    int ManualPreserved,
    bool Changed,
    string? Error,
    DateTimeOffset ObservedAt);

/// <summary>Small, thread-safe cache keyed by endpoint, route and revision.</summary>
public sealed class ProviderModelCache
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ProviderModelCacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public bool TryGet(string endpointId, string route, int revision, out IReadOnlyList<string> modelIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        lock (_gate)
        {
            if (_entries.TryGetValue(MakeKey(endpointId, route, revision), out var entry))
            {
                modelIds = entry.ModelIds;
                return true;
            }
        }

        modelIds = [];
        return false;
    }

    public void Set(string endpointId, string route, int revision, IReadOnlyList<string> modelIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        ArgumentNullException.ThrowIfNull(modelIds);
        lock (_gate) _entries[MakeKey(endpointId, route, revision)] = new ProviderModelCacheEntry(endpointId, route, revision,
            Array.AsReadOnly(modelIds.ToArray()), DateTimeOffset.UtcNow);
    }

    public void Invalidate(string endpointId, string? route = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        lock (_gate)
        {
            var prefix = route is null ? $"{endpointId}:" : $"{endpointId}:{route}:";
            foreach (var key in _entries.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                _entries.Remove(key);
            }
        }
    }

    public IReadOnlyList<string>? GetLatest(string endpointId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        lock (_gate) return _entries.Values.Where(entry => entry.EndpointId.Equals(endpointId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.CachedAt).FirstOrDefault()?.ModelIds;
    }

    private static string MakeKey(string endpointId, string route, int revision) =>
        $"{endpointId.Trim().ToLowerInvariant()}:{route.Trim().ToLowerInvariant()}:{revision}";
}

public sealed record ProviderModelCacheEntry(string EndpointId, string Route, int Revision, IReadOnlyList<string> ModelIds, DateTimeOffset CachedAt);
