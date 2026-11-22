using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Llm.ProviderKit.Catalog;

namespace Llm.ProviderKit.Inspection;

/// <summary>Meaning of a normalized provider billing or usage value.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProviderMetricKind>))]
public enum ProviderMetricKind
{
    Unknown = 0,
    AvailableBalance = 1,
    AllocatedCredits = 2,
    PurchasedCredits = 3,
    Spending = 4,
    QuotaLimit = 5,
    QuotaRemaining = 6,
    ProviderCredits = 7,
    ProviderSpecificUsage = 8,
    RequestCount = 9,
    RequestAllowance = 10,
    TokenCount = 11,
    TokenAllowance = 12,
}

/// <summary>Who or what the provider says the metric belongs to.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProviderMetricScope>))]
public enum ProviderMetricScope
{
    Unknown = 0,
    ApiKey = 1,
    Account = 2,
    Organization = 3,
    Workspace = 4,
    Team = 5,
    Subscription = 6,
}

[JsonConverter(typeof(JsonStringEnumConverter<ProviderMetricUnitKind>))]
public enum ProviderMetricUnitKind
{
    Unknown = 0,
    Currency = 1,
    ProviderSpecific = 2,
    Requests = 3,
    Tokens = 4,
}

public sealed record ProviderMetricUnit(ProviderMetricUnitKind Kind, string? Code = null)
{
    public static ProviderMetricUnit Unknown { get; } = new(ProviderMetricUnitKind.Unknown);
    public static ProviderMetricUnit Requests { get; } = new(ProviderMetricUnitKind.Requests);
    public static ProviderMetricUnit Tokens { get; } = new(ProviderMetricUnitKind.Tokens);

    public static ProviderMetricUnit Currency(string code) =>
        new(ProviderMetricUnitKind.Currency, NormalizeCurrencyCode(code));

    public static ProviderMetricUnit Provider(string code) =>
        new(ProviderMetricUnitKind.ProviderSpecific, NormalizeUnitCode(code));

    private static string NormalizeCurrencyCode(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var normalized = code.Trim().ToUpperInvariant();
        if (normalized.Length != 3 || normalized.Any(character => !char.IsAsciiLetter(character)))
            throw new ArgumentException("Currency codes must use the three-letter ISO 4217 form.", nameof(code));
        return normalized;
    }

    private static string NormalizeUnitCode(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var normalized = code.Trim();
        if (normalized.Length > 64 || normalized.Any(char.IsControl))
            throw new ArgumentException("Provider-specific unit codes must be short printable text.", nameof(code));
        return normalized;
    }
}

/// <summary>Describes the time window represented by a metric when its source provides one.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProviderMetricPeriodSemantics>))]
public enum ProviderMetricPeriodSemantics
{
    Unknown = 0,
    Cumulative = 1,
    BoundedInterval = 2,
    Lifetime = 3,
    CalendarDay = 4,
    CalendarWeek = 5,
    CalendarMonth = 6,
    BillingCycle = 7,
    RollingWindow = 8,
}

/// <summary>Freshness or retrieval state of a metric or snapshot.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProviderMetricAvailability>))]
public enum ProviderMetricAvailability
{
    Unknown = 0,
    Live = 1,
    Partial = 2,
    Cached = 3,
    Stale = 4,
    Unsupported = 5,
    Unavailable = 6,
    Unauthorized = 7,
    InvalidResponse = 8,
}

/// <summary>A catalog-qualified display amount for this metric, with the same scope as its raw value.</summary>
public sealed record ProviderMetricDisplay(decimal Value, string Currency, string Symbol)
{
    [JsonIgnore]
    public bool IsValid => Value >= 0m && QuotaDisplayConversion.IsValidDenomination(Currency, Symbol);
}

/// <summary>A normalized, credential-free billing or usage observation.</summary>
public sealed record ProviderMetric
{
    public string Id { get; init; } = string.Empty;
    public ProviderMetricKind Kind { get; init; } = ProviderMetricKind.Unknown;
    public decimal? Value { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProviderMetricDisplay? Display { get; init; }
    public ProviderMetricScope Scope { get; init; } = ProviderMetricScope.Unknown;
    public ProviderMetricUnit Unit { get; init; } = ProviderMetricUnit.Unknown;
    public ProviderMetricAvailability Availability { get; init; } = ProviderMetricAvailability.Unknown;
    public DateTimeOffset ObservedAt { get; init; }
    public string? Period { get; init; }
    public ProviderMetricPeriodSemantics PeriodSemantics { get; init; } = ProviderMetricPeriodSemantics.Unknown;
    public DateTimeOffset? PeriodStartAt { get; init; }
    public DateTimeOffset? PeriodEndAt { get; init; }
    public DateTimeOffset? ResetAt { get; init; }
    /// <summary>Provider-stated expiry of a key, credit, or subscription metric.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
    /// <summary>
    /// True only when the provider explicitly reports that this quota has no finite limit.
    /// In that case <see cref="Value"/> must stay null; a sentinel zero is not a balance.
    /// </summary>
    public bool IsUnlimited { get; init; }
    public string? Source { get; init; }
    public bool AllowsNegative { get; init; }
}

/// <summary>Billing facts and capability states from one provider inspection.</summary>
public sealed record ProviderMetricSnapshot
{
    public IReadOnlyList<ProviderMetric> Metrics { get; init; } = Array.Empty<ProviderMetric>();
    public DateTimeOffset ObservedAt { get; init; }
    public ProviderMetricAvailability Availability { get; init; } = ProviderMetricAvailability.Unknown;
    public ProviderMetricAvailability BalanceAvailability { get; init; } = ProviderMetricAvailability.Unknown;
    public ProviderMetricAvailability UsageAvailability { get; init; } = ProviderMetricAvailability.Unknown;
    public string? Diagnostic { get; init; }
}

public static class ProviderMetricNormalizer
{
    private static readonly NumberStyles DecimalStyles = NumberStyles.AllowLeadingSign |
        NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent |
        NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite;

    /// <summary>
    /// Derives an allowance remaining value only when a provider documents the relationship
    /// and both observations have identical units, scopes, windows, and live freshness.
    /// </summary>
    public static bool TryDeriveRemaining(
        ProviderMetric limit,
        ProviderMetric used,
        bool relationshipDocumented,
        out ProviderMetric? remaining,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(limit);
        ArgumentNullException.ThrowIfNull(used);
        remaining = null;
        error = null;

        if (!relationshipDocumented)
            return Fail("The source does not document that the limit and usage form one allowance.", out error);
        if (limit.Kind != ProviderMetricKind.QuotaLimit || used.Kind is not (ProviderMetricKind.Spending or
                ProviderMetricKind.ProviderCredits or ProviderMetricKind.ProviderSpecificUsage or
                ProviderMetricKind.RequestCount or ProviderMetricKind.TokenCount))
            return Fail("The metric kinds are not a documented limit and usage pair.", out error);
        if (limit.IsUnlimited || limit.Value is null || used.Value is null)
            return Fail("Both limit and usage values are required.", out error);
        if (limit.Availability != ProviderMetricAvailability.Live || used.Availability != ProviderMetricAvailability.Live)
            return Fail("Only live observations can be used to derive remaining allowance.", out error);
        if (limit.Scope != used.Scope || !UnitsEqual(limit.Unit, used.Unit) ||
            limit.PeriodSemantics != used.PeriodSemantics || !string.Equals(limit.Period, used.Period, StringComparison.Ordinal) ||
            limit.PeriodStartAt != used.PeriodStartAt || limit.PeriodEndAt != used.PeriodEndAt || limit.ResetAt != used.ResetAt)
            return Fail("The limit and usage observations have incompatible scope, unit, or period.", out error);
        if ((!limit.AllowsNegative && limit.Value < 0m) || (!used.AllowsNegative && used.Value < 0m))
            return Fail("Negative limit or usage values are not documented for these metrics.", out error);
        if (used.Value > limit.Value)
            return Fail("Usage exceeds the limit, so remaining allowance is inconsistent.", out error);

        try
        {
            remaining = new ProviderMetric
            {
                Id = $"{limit.Id}-remaining",
                Kind = ProviderMetricKind.QuotaRemaining,
                Value = checked(limit.Value.Value - used.Value.Value),
                Scope = limit.Scope,
                Unit = limit.Unit,
                Availability = ProviderMetricAvailability.Live,
                ObservedAt = limit.ObservedAt > used.ObservedAt ? limit.ObservedAt : used.ObservedAt,
                Period = limit.Period,
                PeriodSemantics = limit.PeriodSemantics,
                PeriodStartAt = limit.PeriodStartAt,
                PeriodEndAt = limit.PeriodEndAt,
                ResetAt = limit.ResetAt,
                ExpiresAt = limit.ExpiresAt,
                Source = limit.Source,
            };
            return true;
        }
        catch (OverflowException)
        {
            return Fail("The remaining allowance exceeded the supported decimal range.", out error);
        }
    }

    /// <summary>
    /// Maps an explicitly documented array of balance records without summing across currencies.
    /// Each record may provide total, allocated/granted, and purchased/topped-up values.
    /// </summary>
    public static ProviderMetricSnapshot MapCurrencyBalanceRecords(
        JsonElement root,
        string jsonPointer,
        DateTimeOffset observedAt,
        ProviderMetricScope scope,
        string source,
        IReadOnlySet<string>? allowedCurrencies = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonPointer);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        if (!JsonPointerExtractor.TryResolvePointer(root, jsonPointer, out var array) || array.ValueKind != JsonValueKind.Array)
            return InvalidBalanceSnapshot(observedAt, "The currency balance collection is missing or malformed.");

        var metrics = new List<ProviderMetric>();
        var invalidRecords = false;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !TryGetProperty(item, "currency", out var currencyElement) ||
                currencyElement.ValueKind != JsonValueKind.String)
            {
                invalidRecords = true;
                continue;
            }

            var currency = currencyElement.GetString()?.Trim().ToUpperInvariant();
            if (!IsCurrencyCode(currency) || (allowedCurrencies is not null && !allowedCurrencies.Contains(currency!)))
            {
                invalidRecords = true;
                continue;
            }

            AddBalanceMetric(item, "total_balance", "available-balance", ProviderMetricKind.AvailableBalance,
                currency!, scope, observedAt, source, required: true, metrics, ref invalidRecords);
            AddBalanceMetric(item, "granted_balance", "allocated-credits", ProviderMetricKind.AllocatedCredits,
                currency!, scope, observedAt, source, required: false, metrics, ref invalidRecords);
            AddBalanceMetric(item, "topped_up_balance", "purchased-credits", ProviderMetricKind.PurchasedCredits,
                currency!, scope, observedAt, source, required: false, metrics, ref invalidRecords);
        }

        var hasBalance = metrics.Any(metric => metric.Kind == ProviderMetricKind.AvailableBalance && metric.Value.HasValue);
        var availability = metrics.Count == 0
            ? ProviderMetricAvailability.InvalidResponse
            : invalidRecords ? ProviderMetricAvailability.Partial : ProviderMetricAvailability.Live;
        return new ProviderMetricSnapshot
        {
            Metrics = metrics,
            ObservedAt = observedAt,
            Availability = availability,
            BalanceAvailability = hasBalance ? availability : ProviderMetricAvailability.InvalidResponse,
            UsageAvailability = ProviderMetricAvailability.Unknown,
            Diagnostic = invalidRecords ? "One or more currency balance records were incomplete or invalid." : null,
        };
    }

    public static bool TryParseDecimal(JsonElement element, out decimal value)
    {
        value = 0m;
        if (element.ValueKind == JsonValueKind.Number) return element.TryGetDecimal(out value);
        return element.ValueKind == JsonValueKind.String &&
               decimal.TryParse(element.GetString(), DecimalStyles, CultureInfo.InvariantCulture, out value);
    }

    public static bool UnitsEqual(ProviderMetricUnit left, ProviderMetricUnit right) =>
        left.Kind == right.Kind && string.Equals(left.Code, right.Code, StringComparison.OrdinalIgnoreCase);

    private static void AddBalanceMetric(
        JsonElement item,
        string propertyName,
        string id,
        ProviderMetricKind kind,
        string currency,
        ProviderMetricScope scope,
        DateTimeOffset observedAt,
        string source,
        bool required,
        List<ProviderMetric> metrics,
        ref bool invalidRecords)
    {
        if (!TryGetProperty(item, propertyName, out var amountElement))
        {
            if (required) invalidRecords = true;
            return;
        }

        if (!TryParseDecimal(amountElement, out var value) || value < 0m)
        {
            invalidRecords = true;
            return;
        }

        metrics.Add(new ProviderMetric
        {
            Id = $"{id}-{currency.ToLowerInvariant()}",
            Kind = kind,
            Value = value,
            Scope = scope,
            Unit = ProviderMetricUnit.Currency(currency),
            Availability = ProviderMetricAvailability.Live,
            ObservedAt = observedAt,
            Source = source,
        });
    }

    private static ProviderMetricSnapshot InvalidBalanceSnapshot(DateTimeOffset observedAt, string diagnostic) => new()
    {
        ObservedAt = observedAt,
        Availability = ProviderMetricAvailability.InvalidResponse,
        BalanceAvailability = ProviderMetricAvailability.InvalidResponse,
        UsageAvailability = ProviderMetricAvailability.Unknown,
        Diagnostic = diagnostic,
    };

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value)) return true;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool IsCurrencyCode(string? value) => value is { Length: 3 } &&
        value.All(char.IsAsciiLetter) && string.Equals(value, value.ToUpperInvariant(), StringComparison.Ordinal);

    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
    }
}
