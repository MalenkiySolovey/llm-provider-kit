using System.Globalization;
using System.Text.Json;
using Llm.ProviderKit.Catalog;

namespace Llm.ProviderKit.Inspection;

public interface IProviderProbeResponseMapper
{
    (bool Success, List<string>? ModelIds, string? Error) MapModels(ProbeHttpResponse response, string? jsonPath);
    ProviderBillingFacts MapBilling(IReadOnlyDictionary<string, ProbeHttpResponse> responses, RecipeResponseMapping? mapping);
}

public sealed class ProviderProbeResponseMapper : IProviderProbeResponseMapper
{
    public (bool Success, List<string>? ModelIds, string? Error) MapModels(ProbeHttpResponse response, string? jsonPath)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (!response.Success || response.Json is null) return (false, null, response.Error ?? "No JSON response was available.");
        return JsonPointerExtractor.TryExtractStringArray(response.Json.Value, jsonPath ?? "/data", out var models)
            ? (true, models, null)
            : (true, [], null);
    }

    public ProviderBillingFacts MapBilling(IReadOnlyDictionary<string, ProbeHttpResponse> responses, RecipeResponseMapping? mapping)
    {
        ArgumentNullException.ThrowIfNull(responses);
        if (mapping is null) return new ProviderBillingFacts();
        var fields = new Dictionary<string, JsonFieldMapping?>(StringComparer.OrdinalIgnoreCase)
        {
            ["balance"] = mapping.Balance,
            ["used"] = mapping.Used,
            ["limit"] = mapping.Limit,
            ["remaining"] = mapping.Remaining,
        };
        var resolved = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var values = new Dictionary<string, decimal?>();
        foreach (var key in fields.Keys)
        {
            values[key] = TryResolve(key, fields, responses, resolved, visiting, out var value) ? value : null;
        }

        string? currency = mapping.Currency?.Literal?.Trim();
        if (string.IsNullOrEmpty(currency) && mapping.Currency?.JsonPath is { Length: > 0 } jsonPath &&
            TryGetResponse(responses, mapping.Currency.RequestId, out var response) && response.Json is { } json &&
            JsonPointerExtractor.TryExtractString(json, jsonPath, out var extracted)) currency = extracted?.Trim();

        return new ProviderBillingFacts(values["balance"], values["used"], values["limit"], values["remaining"], currency);
    }

    /// <summary>Maps legacy recipe facts into explicit, credential-free provider metrics.</summary>
    public ProviderMetricSnapshot MapMetrics(
        ProviderDescriptor descriptor,
        string capability,
        ProviderCapabilityRecipe recipe,
        IReadOnlyDictionary<string, ProbeHttpResponse> responses,
        DateTimeOffset observedAt,
        string? diagnostic = null,
        string? activeBaseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(responses);

        if (recipe.Status != CapabilityStatus.Supported)
        {
            var availability = recipe.Status == CapabilityStatus.Unsupported
                ? ProviderMetricAvailability.Unsupported
                : ProviderMetricAvailability.Unknown;
            return CapabilitySnapshot(capability, availability, observedAt, diagnostic);
        }

        if (NewApiTokenUsage.IsStrategy(recipe.Strategy))
        {
            if (responses.Count == 1 && responses.Values.Single() is { Success: true, Json: { } json } &&
                NewApiTokenUsage.Read(json) is { } usage)
            {
                return CapabilitySnapshot(capability, ProviderMetricAvailability.Live, observedAt, diagnostic) with
                {
                    Metrics = usage.ToMetrics(descriptor.Id, observedAt),
                };
            }

            var status = ClassifyFailure(responses, diagnostic, expectJson: true);
            return CapabilitySnapshot(capability, status, observedAt, diagnostic ?? "The token-usage response was invalid.");
        }

        var mapping = recipe.Outputs ?? recipe.Response;
        if (mapping is null)
        {
            var status = ClassifyFailure(responses, diagnostic, expectJson: true);
            return CapabilitySnapshot(capability, status, observedAt, diagnostic);
        }

        var facts = MapBilling(responses, mapping);
        var failedResponses = responses.Values.Where(response => !response.Success).ToArray();
        var responseFailure = failedResponses.Length == 0
            ? (ProviderMetricAvailability?)null
            : ClassifyFailure(responses, diagnostic, expectJson: true);
        var metrics = new List<ProviderMetric>();
        var expectedFields = new Dictionary<string, JsonFieldMapping?>(StringComparer.OrdinalIgnoreCase)
        {
            ["balance"] = mapping.Balance,
            ["used"] = mapping.Used,
            ["limit"] = mapping.Limit,
            ["remaining"] = mapping.Remaining,
        };
        var values = new Dictionary<string, decimal?>(StringComparer.OrdinalIgnoreCase)
        {
            ["balance"] = facts.Balance,
            ["used"] = facts.Used,
            ["limit"] = facts.Limit,
            ["remaining"] = facts.Remaining,
        };

        if (TryMapOpenRouterKey(descriptor, activeBaseUrl, recipe, mapping, facts, observedAt,
                responses.Values.Any(response => response.Success), metrics))
        {
            var availability = ResolveMetricAvailability(metrics, responseFailure,
                expectedFields.Values.Any(field => field is not null));
            var normalizedUsageAvailability = CategoryAvailability(metrics, isUsage: true, availability);
            if (normalizedUsageAvailability == ProviderMetricAvailability.Unknown) normalizedUsageAvailability = availability;
            return new ProviderMetricSnapshot
            {
                Metrics = metrics,
                ObservedAt = observedAt,
                Availability = availability,
                BalanceAvailability = ProviderMetricAvailability.Unknown,
                UsageAvailability = normalizedUsageAvailability,
                Diagnostic = diagnostic,
            };
        }

        if (TryMapHeJuUsage(descriptor, activeBaseUrl, capability, recipe, mapping, facts, observedAt,
                responses.Values.Any(response => response.Success), metrics))
        {
            var availability = ResolveMetricAvailability(metrics, responseFailure,
                expectedFields.Values.Any(field => field is not null));
            var normalizedUsageAvailability = CategoryAvailability(metrics, isUsage: true, availability);
            if (normalizedUsageAvailability == ProviderMetricAvailability.Unknown) normalizedUsageAvailability = availability;
            return new ProviderMetricSnapshot
            {
                Metrics = metrics,
                ObservedAt = observedAt,
                Availability = availability,
                BalanceAvailability = ProviderMetricAvailability.Unknown,
                UsageAvailability = normalizedUsageAvailability,
                Diagnostic = diagnostic,
            };
        }

        foreach (var (name, field) in expectedFields)
        {
            if (field is null) continue;
            if (field.Metric is { } definition)
            {
                var metric = MapExplicitMetric(name, field, definition, values[name], responses, facts.Currency, observedAt, descriptor.Id);
                metrics.Add(metric);
                continue;
            }

            if (values[name] is { } value)
            {
                var unit = TryCurrencyUnit(facts.Currency, out var currencyUnit)
                    ? currencyUnit
                    : ProviderMetricUnit.Unknown;
                metrics.Add(new ProviderMetric
                {
                    Id = $"unclassified-{name}",
                    Kind = ProviderMetricKind.Unknown,
                    Value = value,
                    Scope = ProviderMetricScope.Unknown,
                    Unit = unit,
                    Availability = ProviderMetricAvailability.Live,
                    ObservedAt = observedAt,
                    Source = descriptor.Id,
                });
            }
        }

        var hasExpectedFields = expectedFields.Values.Any(field => field is not null);
        var snapshotAvailability = ResolveMetricAvailability(metrics, responseFailure, hasExpectedFields);
        var balanceAvailability = CategoryAvailability(metrics, isUsage: false, snapshotAvailability);
        var usageAvailability = CategoryAvailability(metrics, isUsage: true, snapshotAvailability);
        if (metrics.Count == 0 && responseFailure.HasValue)
        {
            if (capability.Equals("balance", StringComparison.OrdinalIgnoreCase)) balanceAvailability = responseFailure.Value;
            if (capability.Equals("usage", StringComparison.OrdinalIgnoreCase)) usageAvailability = responseFailure.Value;
        }
        else if (metrics.Count == 0 && hasExpectedFields && snapshotAvailability == ProviderMetricAvailability.InvalidResponse)
        {
            if (capability.Equals("balance", StringComparison.OrdinalIgnoreCase)) balanceAvailability = snapshotAvailability;
            if (capability.Equals("usage", StringComparison.OrdinalIgnoreCase)) usageAvailability = snapshotAvailability;
        }

        return new ProviderMetricSnapshot
        {
            Metrics = metrics,
            ObservedAt = observedAt,
            Availability = snapshotAvailability,
            BalanceAvailability = balanceAvailability,
            UsageAvailability = usageAvailability,
            Diagnostic = diagnostic,
        };
    }

    private static ProviderMetric MapExplicitMetric(
        string name,
        JsonFieldMapping field,
        ProviderMetricDefinition definition,
        decimal? value,
        IReadOnlyDictionary<string, ProbeHttpResponse> responses,
        string? mappedCurrency,
        DateTimeOffset observedAt,
        string source)
    {
        var hasResponse = TryGetResponse(responses, field.RequestId, out var response);
        var responseAvailable = hasResponse && response.Success && response.Json.HasValue;
        var status = value.HasValue
            ? ProviderMetricAvailability.Live
            : responseAvailable ? ProviderMetricAvailability.InvalidResponse :
                response?.StatusCode is 401 or 403 ? ProviderMetricAvailability.Unauthorized :
                response is not null && (response.StatusCode == 429 || response.StatusCode >= 500 || response.StatusCode == 0)
                    ? ProviderMetricAvailability.Unavailable
                    : ProviderMetricAvailability.Unavailable;
        var code = string.IsNullOrWhiteSpace(definition.UnitCode) ? mappedCurrency : definition.UnitCode;
        var unit = CreateUnit(definition.UnitKind, code, out var unitValid);
        if (!unitValid) status = ProviderMetricAvailability.InvalidResponse;
        if (value < 0m && !definition.AllowsNegative && KindRequiresNonNegative(definition.Kind))
            status = ProviderMetricAvailability.InvalidResponse;

        DateTimeOffset? periodStart = null;
        DateTimeOffset? periodEnd = null;
        DateTimeOffset? resetAt = null;
        if (responseAvailable && response is { Json: JsonElement json })
        {
            periodStart = ReadDate(json, definition.PeriodStartPointer);
            periodEnd = ReadDate(json, definition.PeriodEndPointer);
            resetAt = ReadDate(json, definition.ResetAtPointer);
        }

        return new ProviderMetric
        {
            Id = $"{name}-metric",
            Kind = definition.Kind,
            Value = status == ProviderMetricAvailability.InvalidResponse ? null : value,
            Scope = definition.Scope,
            Unit = unit,
            Availability = status,
            ObservedAt = observedAt,
            Period = NormalizePeriod(definition.Period),
            PeriodSemantics = definition.PeriodSemantics,
            PeriodStartAt = periodStart,
            PeriodEndAt = periodEnd,
            ResetAt = resetAt,
            Source = source,
            AllowsNegative = definition.AllowsNegative,
        };
    }

    private static bool TryMapOpenRouterKey(ProviderDescriptor descriptor, string? activeBaseUrl, ProviderCapabilityRecipe recipe,
        RecipeResponseMapping mapping, ProviderBillingFacts facts, DateTimeOffset observedAt, bool hasSuccessfulResponse,
        ICollection<ProviderMetric> metrics)
    {
        if (!descriptor.Id.Equals("openrouter", StringComparison.OrdinalIgnoreCase) ||
            !IsExactRequest(descriptor, activeBaseUrl, recipe, "openrouter.ai", "/api/v1/key", exactPath: true) ||
            mapping.Used?.JsonPath != "/data/usage" || mapping.Limit?.JsonPath != "/data/limit" ||
            !string.Equals(mapping.Currency?.Literal, "USD", StringComparison.OrdinalIgnoreCase)) return false;

        AddKnownMetric(metrics, "api-key-spending", ProviderMetricKind.Spending, facts.Used,
            ProviderMetricScope.ApiKey, ProviderMetricUnit.Currency("USD"), observedAt, "openrouter:/api/v1/key", hasSuccessfulResponse);
        AddKnownMetric(metrics, "api-key-allowance", ProviderMetricKind.QuotaLimit, facts.Limit,
            ProviderMetricScope.ApiKey, ProviderMetricUnit.Currency("USD"), observedAt, "openrouter:/api/v1/key", hasSuccessfulResponse);
        return true;
    }

    private static bool TryMapHeJuUsage(ProviderDescriptor descriptor, string? activeBaseUrl, string capability, ProviderCapabilityRecipe recipe,
        RecipeResponseMapping mapping, ProviderBillingFacts facts, DateTimeOffset observedAt, bool hasSuccessfulResponse,
        ICollection<ProviderMetric> metrics)
    {
        if (!descriptor.Id.Equals("hejuapi", StringComparison.OrdinalIgnoreCase) ||
            !capability.Equals("usage", StringComparison.OrdinalIgnoreCase) ||
            !IsExactRequest(descriptor, activeBaseUrl, recipe, "www.hejuapi.com", "/dashboard/billing/usage", exactPath: false) ||
            mapping.Used?.JsonPath != "/total_usage" ||
            !string.Equals(mapping.Used.Transform, "centsToUnits", StringComparison.OrdinalIgnoreCase) ||
            mapping.Currency is not null) return false;

        AddKnownMetric(metrics, "heju-provider-usage", ProviderMetricKind.ProviderSpecificUsage, facts.Used,
            ProviderMetricScope.Unknown, ProviderMetricUnit.Provider("heju-display-units"), observedAt,
            "hejuapi:/dashboard/billing/usage", hasSuccessfulResponse);
        return true;
    }

    private static bool IsExactRequest(ProviderDescriptor descriptor, string? activeBaseUrl,
        ProviderCapabilityRecipe recipe, string host, string path, bool exactPath)
    {
        var requests = recipe.Requests is { Count: > 0 } ? recipe.Requests : recipe.Request is null ? [] : [recipe.Request];
        if (requests.Count != 1) return false;
        var request = requests[0];
        var target = !string.IsNullOrWhiteSpace(request.Url)
            ? request.Url
            : !string.IsNullOrWhiteSpace(activeBaseUrl)
                ? new ProviderProbePlanner().JoinBaseUrlAndPath(activeBaseUrl, request.Path, recipe.Strategy)
                : null;
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !ProviderMatcher.IsHostTrusted(descriptor, uri)) return false;
        var hostMatches = host.Equals("www.hejuapi.com", StringComparison.OrdinalIgnoreCase)
            ? uri.Host.Equals("www.hejuapi.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("hk.hejuapi.com", StringComparison.OrdinalIgnoreCase)
            : uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase);
        var pathMatches = exactPath
            ? uri.AbsolutePath.Equals(path, StringComparison.Ordinal)
            : uri.AbsolutePath.EndsWith(path, StringComparison.Ordinal);
        return hostMatches && pathMatches;
    }

    private static void AddKnownMetric(ICollection<ProviderMetric> metrics, string id, ProviderMetricKind kind,
        decimal? value, ProviderMetricScope scope, ProviderMetricUnit unit, DateTimeOffset observedAt, string source,
        bool hasSuccessfulResponse)
    {
        if (!value.HasValue && !hasSuccessfulResponse) return;
        metrics.Add(new ProviderMetric
        {
            Id = id,
            Kind = kind,
            Value = value,
            Scope = scope,
            Unit = unit,
            Availability = value.HasValue ? ProviderMetricAvailability.Live : ProviderMetricAvailability.InvalidResponse,
            ObservedAt = observedAt,
            Source = source,
        });
    }

    private static ProviderMetricUnit CreateUnit(ProviderMetricUnitKind kind, string? code, out bool valid)
    {
        valid = true;
        try
        {
            return kind switch
            {
                ProviderMetricUnitKind.Currency when !string.IsNullOrWhiteSpace(code) => ProviderMetricUnit.Currency(code),
                ProviderMetricUnitKind.ProviderSpecific when !string.IsNullOrWhiteSpace(code) => ProviderMetricUnit.Provider(code),
                ProviderMetricUnitKind.Requests when string.IsNullOrWhiteSpace(code) => ProviderMetricUnit.Requests,
                ProviderMetricUnitKind.Tokens when string.IsNullOrWhiteSpace(code) => ProviderMetricUnit.Tokens,
                ProviderMetricUnitKind.Unknown when string.IsNullOrWhiteSpace(code) => ProviderMetricUnit.Unknown,
                _ => InvalidUnit(out valid),
            };
        }
        catch (ArgumentException)
        {
            return InvalidUnit(out valid);
        }
    }

    private static ProviderMetricUnit InvalidUnit(out bool valid)
    {
        valid = false;
        return ProviderMetricUnit.Unknown;
    }

    private static bool KindRequiresNonNegative(ProviderMetricKind kind) => kind is
        ProviderMetricKind.AvailableBalance or ProviderMetricKind.AllocatedCredits or ProviderMetricKind.PurchasedCredits or
        ProviderMetricKind.QuotaLimit or ProviderMetricKind.QuotaRemaining or ProviderMetricKind.ProviderCredits or
        ProviderMetricKind.RequestCount or ProviderMetricKind.RequestAllowance or ProviderMetricKind.TokenCount or
        ProviderMetricKind.TokenAllowance;

    private static DateTimeOffset? ReadDate(JsonElement root, string? pointer) =>
        !string.IsNullOrWhiteSpace(pointer) && JsonPointerExtractor.TryExtractString(root, pointer, out var text) &&
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)
            ? value
            : null;

    private static string? NormalizePeriod(string? period) =>
        string.IsNullOrWhiteSpace(period) ? null : period.Trim();

    private static ProviderMetricAvailability ResolveMetricAvailability(
        IReadOnlyCollection<ProviderMetric> metrics,
        ProviderMetricAvailability? responseFailure,
        bool expectedFields)
    {
        var valid = metrics.Any(metric => metric.Value.HasValue && metric.Availability == ProviderMetricAvailability.Live);
        var invalid = metrics.Any(metric => metric.Availability == ProviderMetricAvailability.InvalidResponse);
        if (valid) return responseFailure.HasValue || invalid ? ProviderMetricAvailability.Partial : ProviderMetricAvailability.Live;
        if (responseFailure.HasValue) return responseFailure.Value;
        return expectedFields ? ProviderMetricAvailability.InvalidResponse : ProviderMetricAvailability.Unknown;
    }

    private static ProviderMetricAvailability CategoryAvailability(
        IReadOnlyCollection<ProviderMetric> metrics,
        bool isUsage,
        ProviderMetricAvailability fallback)
    {
        var category = metrics.Where(metric => isUsage ? IsUsageMetric(metric.Kind) : IsBalanceMetric(metric.Kind)).ToArray();
        if (category.Any(metric => metric.Value.HasValue && metric.Availability == ProviderMetricAvailability.Live))
            return category.Any(metric => metric.Availability == ProviderMetricAvailability.InvalidResponse)
                ? ProviderMetricAvailability.Partial
                : fallback == ProviderMetricAvailability.Partial ? fallback : ProviderMetricAvailability.Live;
        if (category.Any(metric => metric.Availability == ProviderMetricAvailability.Unauthorized))
            return ProviderMetricAvailability.Unauthorized;
        if (category.Any(metric => metric.Availability == ProviderMetricAvailability.Unavailable))
            return ProviderMetricAvailability.Unavailable;
        if (category.Any(metric => metric.Availability == ProviderMetricAvailability.InvalidResponse))
            return ProviderMetricAvailability.InvalidResponse;
        return ProviderMetricAvailability.Unknown;
    }

    private static bool IsBalanceMetric(ProviderMetricKind kind) => kind is ProviderMetricKind.AvailableBalance or
        ProviderMetricKind.AllocatedCredits or ProviderMetricKind.PurchasedCredits;

    private static bool IsUsageMetric(ProviderMetricKind kind) => kind is ProviderMetricKind.Spending or
        ProviderMetricKind.QuotaLimit or ProviderMetricKind.QuotaRemaining or ProviderMetricKind.ProviderCredits or
        ProviderMetricKind.ProviderSpecificUsage or ProviderMetricKind.RequestCount or ProviderMetricKind.RequestAllowance or
        ProviderMetricKind.TokenCount or ProviderMetricKind.TokenAllowance;

    private static ProviderMetricSnapshot CapabilitySnapshot(string capability, ProviderMetricAvailability availability,
        DateTimeOffset observedAt, string? diagnostic) => new()
        {
            ObservedAt = observedAt,
            Availability = availability,
            BalanceAvailability = capability.Equals("balance", StringComparison.OrdinalIgnoreCase)
            ? availability
            : ProviderMetricAvailability.Unknown,
            UsageAvailability = capability.Equals("usage", StringComparison.OrdinalIgnoreCase)
            ? availability
            : ProviderMetricAvailability.Unknown,
            Diagnostic = diagnostic,
        };

    private static ProviderMetricAvailability ClassifyFailure(
        IReadOnlyDictionary<string, ProbeHttpResponse> responses, string? diagnostic, bool expectJson)
    {
        var response = responses.Values.FirstOrDefault(item => !item.Success);
        if (response is not null)
        {
            if (response.StatusCode is 401 or 403) return ProviderMetricAvailability.Unauthorized;
            if (response.StatusCode == 429 || response.StatusCode >= 500 || response.StatusCode == 0)
                return ProviderMetricAvailability.Unavailable;
            if (response.Error?.Contains("malformed JSON", StringComparison.OrdinalIgnoreCase) == true)
                return ProviderMetricAvailability.InvalidResponse;
            return ProviderMetricAvailability.Unavailable;
        }

        if (!string.IsNullOrWhiteSpace(diagnostic))
        {
            if (diagnostic.Contains("401", StringComparison.OrdinalIgnoreCase) ||
                diagnostic.Contains("403", StringComparison.OrdinalIgnoreCase)) return ProviderMetricAvailability.Unauthorized;
            if (diagnostic.Contains("malformed", StringComparison.OrdinalIgnoreCase) ||
                diagnostic.Contains("invalid", StringComparison.OrdinalIgnoreCase)) return ProviderMetricAvailability.InvalidResponse;
            return ProviderMetricAvailability.Unavailable;
        }
        if (expectJson && responses.Count == 0) return ProviderMetricAvailability.Unknown;
        return ProviderMetricAvailability.InvalidResponse;
    }

    private static bool TryCurrencyUnit(string? code, out ProviderMetricUnit unit)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            unit = ProviderMetricUnit.Unknown;
            return false;
        }

        try
        {
            unit = ProviderMetricUnit.Currency(code);
            return true;
        }
        catch (ArgumentException)
        {
            unit = ProviderMetricUnit.Unknown;
            return false;
        }
    }

    private static bool TryResolve(string name, IReadOnlyDictionary<string, JsonFieldMapping?> fields,
        IReadOnlyDictionary<string, ProbeHttpResponse> responses, IDictionary<string, decimal> resolved,
        ISet<string> visiting, out decimal value)
    {
        value = 0m;
        if (resolved.TryGetValue(name, out value)) return true;
        if (!fields.TryGetValue(name, out var mapping) || mapping is null || !visiting.Add(name)) return false;
        try
        {
            var operation = string.IsNullOrWhiteSpace(mapping.Operation) ? "direct" : mapping.Operation.Trim().ToLowerInvariant();
            var sources = mapping.Sources ?? [];
            if (operation == "direct")
            {
                if (sources.Count != 0 || !TryGetResponse(responses, mapping.RequestId, out var response) || response.Json is not { } json ||
                    !JsonPointerExtractor.TryExtractDecimal(json, mapping, out value)) return false;
            }
            else if (operation == "scale")
            {
                decimal source;
                if (sources.Count == 1)
                {
                    if (!TryResolve(sources[0], fields, responses, resolved, visiting, out source)) return false;
                }
                else if (sources.Count == 0 && TryGetResponse(responses, mapping.RequestId, out var sourceResponse) && sourceResponse.Json is { } sourceJson &&
                         JsonPointerExtractor.TryExtractDecimal(sourceJson, mapping, out source)) { }
                else return false;
                value = checked(source * (mapping.ScaleFactor ?? 1m));
            }
            else if (operation is "add" or "subtract")
            {
                if (sources.Count != 2 || !TryResolve(sources[0], fields, responses, resolved, visiting, out var left) ||
                    !TryResolve(sources[1], fields, responses, resolved, visiting, out var right)) return false;
                value = operation == "add" ? checked(left + right) : checked(left - right);
            }
            else return false;

            resolved[name] = value;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
        finally
        {
            visiting.Remove(name);
        }
    }

    private static bool TryGetResponse(IReadOnlyDictionary<string, ProbeHttpResponse> responses, string? requestId, out ProbeHttpResponse response)
    {
        response = default!;
        if (!string.IsNullOrWhiteSpace(requestId))
            return responses.TryGetValue(requestId, out response!) && response.Success && response.Json.HasValue;
        if (responses.Count != 1) return false;
        response = responses.Values.First();
        return response.Success && response.Json.HasValue;
    }
}

public sealed record ProviderBillingFacts(decimal? Balance = null, decimal? Used = null, decimal? Limit = null, decimal? Remaining = null, string? Currency = null);
