namespace Llm.ProviderKit.Catalog;

public sealed record ProviderCatalogSnapshot(ProviderCatalog Catalog, DateTimeOffset LoadedAt, string Source);

/// <summary>Small in-memory catalog facade; filesystem, signature and app-version policy remain host adapters.</summary>
public sealed class ProviderCatalogService
{
    private readonly object _gate = new();
    private ProviderCatalogSnapshot? _current;

    public ProviderCatalogSnapshot Current
    {
        get
        {
            lock (_gate) return _current ?? throw new InvalidOperationException("No validated provider catalog has been loaded.");
        }
    }

    public ProviderCatalogSnapshot Load(ReadOnlySpan<byte> bytes, string source = "memory")
    {
        var result = ProviderCatalogValidator.ParseAndValidate(bytes);
        if (!result.IsValid || result.Catalog is null) throw new InvalidDataException(result.Error ?? "Provider catalog is invalid.");
        var snapshot = new ProviderCatalogSnapshot(result.Catalog, DateTimeOffset.UtcNow, source);
        lock (_gate) _current = snapshot;
        return snapshot;
    }

    public ProviderDescriptor? GetDescriptor(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return null;
        return Current.Catalog.Providers.FirstOrDefault(provider => provider.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));
    }

    public ProviderDescriptor? MatchDescriptor(string? hostOrUrl) => ProviderMatcher.FindBestMatch(Current.Catalog, hostOrUrl);
}
