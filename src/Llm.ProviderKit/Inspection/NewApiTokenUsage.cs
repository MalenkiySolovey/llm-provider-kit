using System.Text.Json;

namespace Llm.ProviderKit.Inspection;

/// <summary>Normalizes the documented New API token-usage response, independent of its deployment.</summary>
internal sealed record NewApiTokenUsage(decimal? Limit, decimal Used, decimal? Remaining, DateTimeOffset? ExpiresAt)
{
    public const string Strategy = "new-api-token-usage";
    private static readonly ProviderMetricUnit QuotaUnit = ProviderMetricUnit.Provider("quota-units");

    public static bool IsStrategy(string? strategy) =>
        string.Equals(strategy, Strategy, StringComparison.OrdinalIgnoreCase);

    public static NewApiTokenUsage? Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !IsSuccess(root) ||
            !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("unlimited_quota", out var unlimited) ||
            unlimited.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !TryReadQuota(data, "total_used", out var used))
        {
            return null;
        }

        var expiresAt = ReadExpiry(data);
        if (unlimited.GetBoolean()) return new(null, used, null, expiresAt);
        if (!TryReadQuota(data, "total_granted", out var limit) ||
            !TryReadQuota(data, "total_available", out var remaining) ||
            remaining > limit || used != limit - remaining)
        {
            return null;
        }

        return new(limit, used, remaining, expiresAt);
    }

    public IReadOnlyList<ProviderMetric> ToMetrics(string providerId, DateTimeOffset observedAt)
    {
        var allowance = Metric("api-key-quota-limit", ProviderMetricKind.QuotaLimit, Limit) with { IsUnlimited = Limit is null };
        var used = Metric("api-key-quota-used", ProviderMetricKind.ProviderSpecificUsage, Used);
        return Remaining.HasValue
            ? [allowance, used, Metric("api-key-quota-remaining", ProviderMetricKind.QuotaRemaining, Remaining)]
            : [allowance, used];

        ProviderMetric Metric(string id, ProviderMetricKind kind, decimal? value) => new()
        {
            Id = id,
            Kind = kind,
            Value = value,
            Scope = ProviderMetricScope.ApiKey,
            Unit = QuotaUnit,
            Availability = ProviderMetricAvailability.Live,
            ObservedAt = observedAt,
            PeriodSemantics = ProviderMetricPeriodSemantics.Cumulative,
            ExpiresAt = ExpiresAt,
            Source = providerId,
        };
    }

    private static bool IsSuccess(JsonElement root)
    {
        var hasCode = root.TryGetProperty("code", out var code);
        var hasSuccess = root.TryGetProperty("success", out var success);
        return (hasCode || hasSuccess) &&
               (!hasCode || code.ValueKind == JsonValueKind.True) &&
               (!hasSuccess || success.ValueKind == JsonValueKind.True);
    }

    private static bool TryReadQuota(JsonElement data, string name, out decimal value)
    {
        value = 0;
        return data.TryGetProperty(name, out var element) &&
               ProviderMetricNormalizer.TryParseDecimal(element, out value) && value >= 0;
    }

    private static DateTimeOffset? ReadExpiry(JsonElement data) =>
        data.TryGetProperty("expires_at", out var expiry) && expiry.ValueKind == JsonValueKind.Number &&
        expiry.TryGetInt64(out var seconds) && seconds is > 0 and <= 253402300799
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
}
