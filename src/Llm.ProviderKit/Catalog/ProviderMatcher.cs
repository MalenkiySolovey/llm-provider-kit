namespace Llm.ProviderKit.Catalog;

/// <summary>Strict exact-host and exact-base-URL matcher. It never uses suffix or substring matching.</summary>
public static class ProviderMatcher
{
    public static string? NormalizeHost(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        input = input.Trim();
        if (Uri.TryCreate(input, UriKind.Absolute, out var uri)) return uri.IdnHost.ToLowerInvariant();
        if (!input.Contains("://", StringComparison.Ordinal) && Uri.TryCreate($"https://{input}", UriKind.Absolute, out var fallback))
        {
            return fallback.IdnHost.ToLowerInvariant();
        }

        return Uri.CheckHostName(input) is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6
            ? input.ToLowerInvariant()
            : null;
    }

    public static string? NormalizeBaseUrl(string? input)
    {
        if (string.IsNullOrWhiteSpace(input) || !Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri)) return null;
        var host = uri.IdnHost.ToLowerInvariant();
        var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        var path = uri.AbsolutePath.TrimEnd('/');
        return $"{uri.Scheme.ToLowerInvariant()}://{host}{port}{path}";
    }

    public static bool Matches(ProviderDescriptor descriptor, string? input)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (string.IsNullOrWhiteSpace(input)) return false;
        var inputHost = NormalizeHost(input);
        if (inputHost is null) return false;
        if (descriptor.Match.ExactHosts.Any(host => string.Equals(inputHost, NormalizeHost(host), StringComparison.OrdinalIgnoreCase))) return true;

        var inputBase = NormalizeBaseUrl(input);
        return inputBase is not null && descriptor.Match.ExactBaseUrls?.Any(baseUrl =>
            string.Equals(inputBase, NormalizeBaseUrl(baseUrl), StringComparison.OrdinalIgnoreCase)) == true;
    }

    public static ProviderDescriptor? FindBestMatch(ProviderCatalog catalog, string? input)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Providers.FirstOrDefault(provider => Matches(provider, input));
    }

    public static bool IsHostTrusted(ProviderDescriptor descriptor, Uri targetUri)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(targetUri);
        var host = targetUri.IdnHost.ToLowerInvariant();
        return descriptor.TrustedHosts.Any(trusted => string.Equals(host, NormalizeHost(trusted), StringComparison.OrdinalIgnoreCase));
    }
}
