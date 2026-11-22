using System.Text.Json;

namespace Llm.ProviderKit.Catalog;

public sealed record ProviderCatalogValidationResult(bool IsValid, ProviderCatalog? Catalog, string? Error)
{
    public static ProviderCatalogValidationResult Invalid(string error) => new(false, null, error);
}

/// <summary>Parses bounded catalog JSON and validates the generic provider structure.</summary>
public static class ProviderCatalogValidator
{
    public const int MaxCatalogSizeBytes = 5 * 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        MaxDepth = 64,
    };

    public static ProviderCatalogValidationResult ParseAndValidate(ReadOnlySpan<byte> json)
    {
        if (json.Length > MaxCatalogSizeBytes)
        {
            return ProviderCatalogValidationResult.Invalid($"Catalog exceeds the {MaxCatalogSizeBytes}-byte limit.");
        }

        try
        {
            var catalog = JsonSerializer.Deserialize<ProviderCatalog>(json, Options);
            if (catalog is null)
            {
                return ProviderCatalogValidationResult.Invalid("Catalog JSON is empty.");
            }

            return Validate(catalog) is { } error
                ? ProviderCatalogValidationResult.Invalid(error)
                : new ProviderCatalogValidationResult(true, catalog, null);
        }
        catch (JsonException)
        {
            return ProviderCatalogValidationResult.Invalid("Catalog JSON is malformed or does not match the supported schema.");
        }
    }

    public static string? Validate(ProviderCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.SchemaVersion != 1)
        {
            return $"Unsupported provider catalog schema version {catalog.SchemaVersion}.";
        }

        if (catalog.Providers is null || catalog.Providers.Count == 0)
        {
            return "Catalog contains no providers.";
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in catalog.Providers)
        {
            var error = Validate(provider);
            if (error is not null)
            {
                return error;
            }

            if (!ids.Add(provider.Id.Trim()))
            {
                return $"Catalog contains duplicate provider ID '{provider.Id}'.";
            }
        }

        return null;
    }

    public static string? Validate(ProviderDescriptor provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (string.IsNullOrWhiteSpace(provider.Id)) return "Provider ID cannot be empty.";
        if (string.IsNullOrWhiteSpace(provider.DisplayName)) return $"Provider '{provider.Id}' has empty display name.";
        var exactHosts = provider.Match?.ExactHosts;
        if (exactHosts is not { Count: > 0 }) return $"Provider '{provider.Id}' must define at least one exactHost in match criteria.";
        var trustedHosts = provider.TrustedHosts;
        if (trustedHosts is not { Count: > 0 }) return $"Provider '{provider.Id}' must define at least one trustedHost.";
        var routes = provider.Routes;
        if (routes is not { Count: > 0 }) return $"Provider '{provider.Id}' must define at least one route.";

        foreach (var host in exactHosts.Concat(trustedHosts))
        {
            if (string.IsNullOrWhiteSpace(host) || ProviderMatcher.NormalizeHost(host) is null)
            {
                return $"Provider '{provider.Id}' contains an invalid host entry.";
            }
        }

        foreach (var route in routes)
        {
            if (string.IsNullOrWhiteSpace(route.Id) ||
                !Uri.TryCreate(route.BaseUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback) ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            {
                return $"Provider '{provider.Id}' has an invalid route with missing ID or safe BaseUrl.";
            }
        }

        if (provider.Capabilities is { } capabilities)
        {
            if (capabilities.Models?.QuotaDisplay is not null || capabilities.Balance?.QuotaDisplay is not null ||
                capabilities.Usage?.QuotaDisplay is not null)
            {
                return $"Provider '{provider.Id}' can only declare quotaDisplay on keyUsage.";
            }

            if (capabilities.KeyUsage is { QuotaDisplay: { } conversion } keyUsage &&
                (!conversion.IsValid || keyUsage.Status != CapabilityStatus.Supported ||
                    !Inspection.NewApiTokenUsage.IsStrategy(keyUsage.Strategy)))
            {
                return $"Provider '{provider.Id}' has an invalid API-key quota display conversion.";
            }
        }

        return null;
    }
}
