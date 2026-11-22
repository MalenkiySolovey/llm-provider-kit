using System.Text.Json;
using Llm.ProviderKit.Catalog;
using Llm.ProviderKit.Models;

namespace Llm.ProviderKit.Inspection;

/// <summary>Secret-free immutable plan for one allowed read-only request.</summary>
public sealed record ProviderProbePlan(
    Uri TargetUri,
    string Method,
    string AuthScheme,
    IReadOnlyDictionary<string, string>? Headers,
    IReadOnlyList<string> TrustedHosts,
    string Capability,
    string? ModelsPointer = null,
    RecipeResponseMapping? ResponseMapping = null,
    ProviderTransportOptions? TransportOptions = null);

public sealed record ProbeHttpResponse(bool Success, JsonElement? Json, int StatusCode, string? Error);

public sealed record ProviderProbePlanningResult(bool IsValid, ProviderProbePlan? Plan, ProviderDescriptor? SyntheticDescriptor, string? Error);

public interface IProviderProbePlanner
{
    ProviderProbePlanningResult Plan(ProviderDescriptor descriptor, ProviderCapabilityRecipe recipe, string baseUrl, string capability);
    ProviderProbePlanningResult PlanUnknownModels(string baseUrl);
    string JoinBaseUrlAndPath(string baseUrl, string? path, string? strategy = null);
}

public sealed class ProviderProbePlanner : IProviderProbePlanner
{
    public ProviderProbePlanningResult Plan(ProviderDescriptor descriptor, ProviderCapabilityRecipe recipe, string baseUrl, string capability)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(recipe);
        var config = recipe.Request ?? new RecipeRequest { Method = "GET", Path = "/models" };
        var method = config.Method.Trim().ToUpperInvariant();
        if (method is not ("GET" or "HEAD")) return Invalid($"Method '{method}' is not allowed. Only GET and HEAD are permitted.");
        var auth = config.Auth.Trim().ToLowerInvariant();
        var targetText = !string.IsNullOrWhiteSpace(config.Url) ? config.Url : JoinBaseUrlAndPath(baseUrl, config.Path, recipe.Strategy);
        if (NewApiTokenUsage.IsStrategy(recipe.Strategy))
        {
            if (capability != "usage" || method != "GET" || auth != "bearer" ||
                recipe.Request is null || !string.IsNullOrWhiteSpace(config.Url) ||
                config.Path?.Trim() is not { Length: > 1 } path || path[0] != '/' ||
                path[1] == '/' || path.Contains('\\') ||
                !Uri.TryCreate(baseUrl, UriKind.Absolute, out var origin) || origin.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(origin.UserInfo) || !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment))
            {
                return Invalid("New API token usage requires a bearer GET to a root-relative path on the active HTTPS origin.");
            }

            targetText = origin.GetLeftPart(UriPartial.Authority) + path;
        }

        if (!Uri.TryCreate(targetText, UriKind.Absolute, out var target)) return Invalid("Invalid target probe URL.");
        if (!string.IsNullOrEmpty(target.UserInfo) || !string.IsNullOrEmpty(target.Query) || !string.IsNullOrEmpty(target.Fragment))
            return Invalid("Probe URL must not contain credentials, query values, or a fragment.");
        if (target.Scheme != Uri.UriSchemeHttps && !(target.Scheme == Uri.UriSchemeHttp && IsLoopback(target)))
            return Invalid("Insecure HTTP is allowed only for loopback test endpoints.");
        if (!ProviderMatcher.IsHostTrusted(descriptor, target)) return Invalid($"Target host '{target.IdnHost}' is not in the exact trustedHosts allowlist.");

        if (auth is not ("none" or "bearer" or "x-api-key")) return Invalid("Unsupported credential scheme.");
        if (config.Headers?.Keys.Any(IsSensitiveHeader) == true) return Invalid("Static headers cannot supply credentials or override transport control headers.");

        var plan = new ProviderProbePlan(
            target,
            method,
            auth,
            config.Headers is null ? null : new Dictionary<string, string>(config.Headers, StringComparer.OrdinalIgnoreCase),
            descriptor.TrustedHosts.ToArray(),
            capability,
            recipe.Response?.Models?.JsonPath ?? "/data",
            recipe.Response,
            new ProviderTransportOptions());
        return new ProviderProbePlanningResult(true, plan, null, null);
    }

    public ProviderProbePlanningResult PlanUnknownModels(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var suppliedUri) ||
            !string.IsNullOrEmpty(suppliedUri.UserInfo) || !string.IsNullOrEmpty(suppliedUri.Query) || !string.IsNullOrEmpty(suppliedUri.Fragment))
            return Invalid("Base URL must be absolute and must not contain credentials, query values, or a fragment.");
        var normalized = ProviderMatcher.NormalizeBaseUrl(baseUrl);
        if (normalized is null || !Uri.TryCreate(normalized, UriKind.Absolute, out var uri)) return Invalid("Invalid base URL format.");
        var host = uri.IdnHost.ToLowerInvariant();
        var descriptor = new ProviderDescriptor
        {
            Id = "generic-unknown",
            DisplayName = host,
            Match = new ProviderMatchCriteria { ExactHosts = [host] },
            TrustedHosts = [host],
            Capabilities = new ProviderCapabilities
            {
                Models = new ProviderCapabilityRecipe
                {
                    Status = CapabilityStatus.Supported,
                    Strategy = "openai-models",
                    Request = new RecipeRequest { Method = "GET", Path = "/models", Auth = "bearer" },
                },
            },
        };
        var result = Plan(descriptor, descriptor.Capabilities.Models, normalized, "models");
        return result with { SyntheticDescriptor = descriptor };
    }

    public string JoinBaseUrlAndPath(string baseUrl, string? path, string? strategy = null)
    {
        var basePart = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        var pathPart = (path ?? string.Empty).Trim();
        if (pathPart.Length == 0) return basePart;
        if (!pathPart.StartsWith('/')) pathPart = $"/{pathPart}";
        if (basePart.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) && pathPart.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase))
            pathPart = pathPart[3..];
        else if (basePart.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) && pathPart.Equals("/v1", StringComparison.OrdinalIgnoreCase))
            pathPart = string.Empty;
        else if (strategy?.Equals("openai-models", StringComparison.OrdinalIgnoreCase) == true &&
                 !basePart.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) && pathPart.Equals("/models", StringComparison.OrdinalIgnoreCase))
            pathPart = "/v1/models";
        return basePart + pathPart;
    }

    private static bool IsSensitiveHeader(string name) =>
        name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("x-api-key", StringComparison.OrdinalIgnoreCase);

    private static bool IsLoopback(Uri uri) => uri.IsLoopback ||
        uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Equals("::1", StringComparison.OrdinalIgnoreCase);

    private static ProviderProbePlanningResult Invalid(string error) => new(false, null, null, error);
}

/// <summary>Credential supplied only for a single in-memory inspection operation.</summary>
public sealed class ProviderCredential
{
    private readonly string _value;
    private ProviderCredential(string value) => _value = value;
    internal string RevealForRequest() => _value;
    public static ProviderCredential FromSecret(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new ProviderCredential(value);
    }
    public override string ToString() => "[provider credential]";
}

public sealed record ProviderCredentialRequest(string ProviderId, string EndpointId);

public interface IProviderCredentialSource
{
    ValueTask<ProviderCredential?> GetAsync(ProviderCredentialRequest request, CancellationToken cancellationToken = default);
}

public sealed class InMemoryProviderCredentialSource : IProviderCredentialSource
{
    private readonly Dictionary<(string ProviderId, string EndpointId), ProviderCredential> _credentials;
    public InMemoryProviderCredentialSource(IEnumerable<(string ProviderId, string EndpointId, string Secret)> credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        _credentials = credentials.ToDictionary(
            item => (item.ProviderId, item.EndpointId),
            item => ProviderCredential.FromSecret(item.Secret));
    }

    public ValueTask<ProviderCredential?> GetAsync(ProviderCredentialRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_credentials.TryGetValue((request.ProviderId, request.EndpointId), out var credential) ? credential : null);
    }
}
