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
