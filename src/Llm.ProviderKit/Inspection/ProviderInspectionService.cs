using Llm.ProviderKit.Catalog;

namespace Llm.ProviderKit.Inspection;

public sealed record ProviderInspectionResult(
    bool Succeeded,
    IReadOnlyList<string> ModelIds,
    IReadOnlyDictionary<string, CapabilityStatus> Capabilities,
    ProviderBillingFacts Billing,
    DateTimeOffset ObservedAt,
    string? Error)
{
    public ProviderMetricSnapshot MetricSnapshot { get; init; } = new() { ObservedAt = ObservedAt };
}

/// <summary>Executes bounded declarative GET/HEAD probes without owning credentials or application state.</summary>
public sealed class ProviderInspectionService : IDisposable
{
    private const int MaxRequestsPerRecipe = 8;
    private const int MaxGenericBillingStrategies = 8;
    private readonly IProviderProbePlanner _planner;
    private readonly ISafeProviderHttpTransport _transport;
    private readonly ProviderProbeResponseMapper _mapper;
    private readonly IProviderCredentialSource? _credentials;
    private readonly IReadOnlyList<IGenericProviderBillingStrategy> _genericBillingStrategies;

    public ProviderInspectionService(IProviderProbePlanner planner, ISafeProviderHttpTransport transport,
        ProviderProbeResponseMapper mapper, IProviderCredentialSource? credentials = null,
        IEnumerable<IGenericProviderBillingStrategy>? genericBillingStrategies = null)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        _credentials = credentials;
        _genericBillingStrategies = (genericBillingStrategies ?? [])
            .Where(strategy => strategy is not null)
            .OrderBy(strategy => strategy.Order)
            .ThenBy(strategy => strategy.Id, StringComparer.Ordinal)
            .Take(MaxGenericBillingStrategies)
            .ToArray();
    }

    public ProviderInspectionService(IProviderCredentialSource? credentials = null)
        : this(new ProviderProbePlanner(), new SafeProviderHttpTransport(), new ProviderProbeResponseMapper(), credentials) { }

    public async Task<ProviderInspectionResult> InspectAsync(ProviderDescriptor descriptor, string endpointId, string baseUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        var capabilities = new Dictionary<string, CapabilityStatus>(StringComparer.OrdinalIgnoreCase)
        {
            ["models"] = descriptor.Capabilities.Models.Status,
            ["balance"] = descriptor.Capabilities.Balance.Status,
            ["usage"] = descriptor.Capabilities.Usage.Status,
        };
        var credential = _credentials is null ? null : await _credentials.GetAsync(
            new ProviderCredentialRequest(descriptor.Id, endpointId), cancellationToken).ConfigureAwait(false);
        var errors = new List<string>();
        var cache = new Dictionary<string, ProbeHttpResponse>(StringComparer.Ordinal);

        async Task<ProbeHttpResponse> SendOnceAsync(ProviderProbePlan plan)
        {
            var key = CacheKey(plan);
            if (!cache.TryGetValue(key, out var response))
            {
                response = await _transport.SendAsync(plan, descriptor, credential, cancellationToken).ConfigureAwait(false);
                cache[key] = response;
            }

            return response;
        }

        var models = new List<string>();
        var modelsRecipe = descriptor.Capabilities.Models;
        if (modelsRecipe.Status == CapabilityStatus.Supported)
        {
            var planned = _planner.Plan(descriptor, modelsRecipe, baseUrl, "models");
            if (planned.Plan is null)
            {
                capabilities["models"] = CapabilityStatus.Unknown;
                errors.Add(planned.Error ?? "Models probe was rejected.");
            }
            else
            {
                var response = await SendOnceAsync(planned.Plan).ConfigureAwait(false);
                var mapped = _mapper.MapModels(response, planned.Plan.ModelsPointer);
                capabilities["models"] = mapped.Success ? CapabilityStatus.Supported : CapabilityStatus.Unknown;
                if (mapped.ModelIds is not null) models.AddRange(mapped.ModelIds);
                if (mapped.Error is not null) errors.Add(mapped.Error);
            }
        }

        decimal? balance = null;
        decimal? used = null;
        decimal? limit = null;
        decimal? remaining = null;
        string? currency = null;
        var metricSnapshots = new List<ProviderMetricSnapshot>();
        var recipes = new List<(string Name, string Category, ProviderCapabilityRecipe Recipe)>
        {
            ("balance", "balance", descriptor.Capabilities.Balance),
            ("usage", "usage", descriptor.Capabilities.Usage),
        };
        if (descriptor.Capabilities.KeyUsage is { } keyUsage)
        {
            recipes.Add(("keyUsage", "usage", keyUsage));
            capabilities["keyUsage"] = keyUsage.Status;
        }

        foreach (var (name, category, recipe) in recipes)
        {
            if (recipe.Status != CapabilityStatus.Supported)
            {
                metricSnapshots.Add(_mapper.MapMetrics(descriptor, category, recipe,
                    new Dictionary<string, ProbeHttpResponse>(), DateTimeOffset.UtcNow, activeBaseUrl: baseUrl));
                continue;
            }

            var execution = await ExecuteRecipeAsync(descriptor, baseUrl, recipe, category, SendOnceAsync, cancellationToken).ConfigureAwait(false);
            var observedAt = DateTimeOffset.UtcNow;
            var mappedMetrics = _mapper.MapMetrics(descriptor, category, recipe, execution.Responses, observedAt, execution.Error, baseUrl);
            metricSnapshots.Add(mappedMetrics);
            if (NewApiTokenUsage.IsStrategy(recipe.Strategy))
            {
                if (mappedMetrics.UsageAvailability != ProviderMetricAvailability.Live)
                {
                    capabilities[name] = CapabilityStatus.Unknown;
                    if (mappedMetrics.Diagnostic is not null) errors.Add(mappedMetrics.Diagnostic);
                }

                continue;
            }

            if (!execution.Valid)
            {
                capabilities[name] = CapabilityStatus.Unknown;
                if (execution.Error is not null) errors.Add(execution.Error);
                continue;
            }

            var facts = _mapper.MapBilling(execution.Responses, recipe.Outputs ?? recipe.Response);
            balance ??= facts.Balance;
            used ??= facts.Used;
            limit ??= facts.Limit;
            remaining ??= facts.Remaining;
            currency ??= facts.Currency;
            if (!execution.Responses.Values.Any(response => response.Success))
            {
                capabilities[name] = CapabilityStatus.Unknown;
                if (execution.Error is not null) errors.Add(execution.Error);
            }
        }

        var hasSuccess = cache.Values.Any(response => response.Success);
        var finishedAt = DateTimeOffset.UtcNow;
        return new ProviderInspectionResult(hasSuccess, models, capabilities,
            new ProviderBillingFacts(balance, used, limit, remaining, currency), finishedAt,
            errors.FirstOrDefault())
        {
            MetricSnapshot = MergeMetricSnapshots(metricSnapshots, finishedAt, errors.FirstOrDefault()),
        };
    }

    /// <summary>Refreshes only the declared model list; this operation never invokes billing recipes.</summary>
    public async Task<ProviderInspectionResult> DiscoverModelsAsync(ProviderDescriptor descriptor, string endpointId, string baseUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        var recipe = descriptor.Capabilities.Models;
        if (recipe.Status != CapabilityStatus.Supported)
        {
            return new ProviderInspectionResult(false, [],
                new Dictionary<string, CapabilityStatus>(StringComparer.OrdinalIgnoreCase) { ["models"] = recipe.Status },
                new ProviderBillingFacts(), DateTimeOffset.UtcNow, "Provider does not declare a supported model discovery endpoint.");
        }

        var planned = _planner.Plan(descriptor, recipe, baseUrl, "models");
        if (planned.Plan is null)
        {
            return new ProviderInspectionResult(false, [],
                new Dictionary<string, CapabilityStatus>(StringComparer.OrdinalIgnoreCase) { ["models"] = CapabilityStatus.Unknown },
                new ProviderBillingFacts(), DateTimeOffset.UtcNow, planned.Error ?? "Model discovery was rejected.");
        }

        var credential = _credentials is null ? null : await _credentials.GetAsync(
            new ProviderCredentialRequest(descriptor.Id, endpointId), cancellationToken).ConfigureAwait(false);
        var response = await _transport.SendAsync(planned.Plan, descriptor, credential, cancellationToken).ConfigureAwait(false);
        var mapped = _mapper.MapModels(response, planned.Plan.ModelsPointer);
        return new ProviderInspectionResult(mapped.Success, mapped.ModelIds ?? [],
            new Dictionary<string, CapabilityStatus>(StringComparer.OrdinalIgnoreCase)
            {
                ["models"] = mapped.Success ? CapabilityStatus.Supported : CapabilityStatus.Unknown,
            }, new ProviderBillingFacts(), DateTimeOffset.UtcNow, mapped.Error);
    }

    public async Task<ProviderInspectionResult> DiscoverUnknownModelsAsync(string endpointId, string baseUrl,
        string? providerIdentity = "generic-unknown", CancellationToken cancellationToken = default)
    {
        var planned = _planner.PlanUnknownModels(baseUrl);
        if (planned.Plan is null || planned.SyntheticDescriptor is null)
            return Failure(planned.Error ?? "Base URL was rejected.");
        var credential = _credentials is null ? null : await _credentials.GetAsync(
            new ProviderCredentialRequest(providerIdentity ?? "generic-unknown", endpointId), cancellationToken).ConfigureAwait(false);
        var response = await _transport.SendAsync(planned.Plan, planned.SyntheticDescriptor, credential, cancellationToken).ConfigureAwait(false);
        var mapped = _mapper.MapModels(response, planned.Plan.ModelsPointer);
        return new ProviderInspectionResult(mapped.Success, mapped.ModelIds ?? [],
            new Dictionary<string, CapabilityStatus>(StringComparer.OrdinalIgnoreCase)
            {
                ["models"] = mapped.Success ? CapabilityStatus.Supported : CapabilityStatus.Unknown,
                ["balance"] = CapabilityStatus.Unknown,
                ["usage"] = CapabilityStatus.Unknown,
            }, new ProviderBillingFacts(), DateTimeOffset.UtcNow, mapped.Error);
    }

    /// <summary>Inspects an unknown OpenAI-compatible host without guessing billing paths.</summary>
    public async Task<ProviderInspectionResult> InspectUnknownAsync(string endpointId, string rawBaseUrl,
        string providerIdentity = "generic-unknown", CancellationToken cancellationToken = default)
    {
        var planned = _planner.PlanUnknownModels(rawBaseUrl);
        if (planned.Plan is null || planned.SyntheticDescriptor is null)
            return Failure(planned.Error ?? "Base URL was rejected.");

        var descriptor = planned.SyntheticDescriptor;
        var credential = _credentials is null ? null : await _credentials.GetAsync(
            new ProviderCredentialRequest(providerIdentity, endpointId), cancellationToken).ConfigureAwait(false);
        var responses = new Dictionary<string, ProbeHttpResponse>(StringComparer.Ordinal);
        async Task<ProbeHttpResponse> SendOnceAsync(ProviderProbePlan plan)
        {
            var key = CacheKey(plan);
            if (!responses.TryGetValue(key, out var response))
            {
                response = await _transport.SendAsync(plan, descriptor, credential, cancellationToken).ConfigureAwait(false);
                responses[key] = response;
            }

            return response;
        }

        var modelResponse = await SendOnceAsync(planned.Plan).ConfigureAwait(false);
        var mapped = _mapper.MapModels(modelResponse, planned.Plan.ModelsPointer);
        var capabilities = new Dictionary<string, CapabilityStatus>(StringComparer.OrdinalIgnoreCase)
        {
            ["models"] = mapped.Success ? CapabilityStatus.Supported : CapabilityStatus.Unknown,
            ["balance"] = CapabilityStatus.Unknown,
            ["usage"] = CapabilityStatus.Unknown,
        };
        decimal? balance = null;
        decimal? used = null;
        decimal? limit = null;
        decimal? remaining = null;
        string? currency = null;
        string? billingError = null;
        var metricSnapshots = new List<ProviderMetricSnapshot>();
        var origin = new Uri(planned.Plan.TargetUri.GetLeftPart(UriPartial.Authority));

        foreach (var strategy in _genericBillingStrategies)
        {
            ProviderCapabilityRecipe? recipe;
            try
            {
                recipe = strategy.CreateRecipe(origin);
            }
            catch (Exception)
            {
                billingError ??= "A generic billing strategy could not create its read-only recipe.";
                continue;
            }

            if (recipe?.Status != CapabilityStatus.Supported) continue;
            var outputMapping = recipe.Outputs ?? recipe.Response;
            if (outputMapping?.Balance is not null) capabilities["balance"] = CapabilityStatus.Supported;
            if (outputMapping?.Used is not null || outputMapping?.Limit is not null || outputMapping?.Remaining is not null)
                capabilities["usage"] = CapabilityStatus.Supported;

            var execution = await ExecuteRecipeAsync(descriptor, rawBaseUrl, recipe, "generic-billing", SendOnceAsync,
                cancellationToken).ConfigureAwait(false);
            var observedAt = DateTimeOffset.UtcNow;
            var mappedMetrics = _mapper.MapMetrics(descriptor, "generic-billing", recipe, execution.Responses,
                observedAt, execution.Error, rawBaseUrl);
            metricSnapshots.Add(mappedMetrics);
            if (execution.Valid)
            {
                var facts = _mapper.MapBilling(execution.Responses, outputMapping);
                balance ??= facts.Balance;
                used ??= facts.Used;
                limit ??= facts.Limit;
                remaining ??= facts.Remaining;
                currency ??= facts.Currency;
            }

            if (balance.HasValue || used.HasValue || limit.HasValue || remaining.HasValue)
            {
                billingError = null;
                break;
            }

            billingError ??= execution.Error;
            if (execution.Error?.Contains("HTTP 401", StringComparison.OrdinalIgnoreCase) == true ||
                execution.Error?.Contains("HTTP 403", StringComparison.OrdinalIgnoreCase) == true) break;
        }

        var finishedAt = DateTimeOffset.UtcNow;
        return new ProviderInspectionResult(mapped.Success, mapped.ModelIds ?? [], capabilities,
            new ProviderBillingFacts(balance, used, limit, remaining, currency), finishedAt,
            billingError ?? mapped.Error ?? modelResponse.Error)
        {
            MetricSnapshot = MergeMetricSnapshots(metricSnapshots, finishedAt, billingError),
        };
    }

    private async Task<RecipeExecution> ExecuteRecipeAsync(ProviderDescriptor descriptor, string baseUrl,
        ProviderCapabilityRecipe recipe, string capability, Func<ProviderProbePlan, Task<ProbeHttpResponse>> send,
        CancellationToken cancellationToken)
    {
        if (NewApiTokenUsage.IsStrategy(recipe.Strategy) && recipe.Requests is not null)
            return RecipeExecution.Invalid("New API token usage requires one request.");
        if (recipe.Requests is { Count: 0 }) return RecipeExecution.Invalid("Inspection recipe declares an empty request list.");
        var requests = recipe.Requests is { Count: > 0 } ? recipe.Requests : recipe.Request is null ? [] : [recipe.Request];
        if (requests.Count == 0) return RecipeExecution.Invalid("Supported inspection recipe is missing a request.");
        if (requests.Count > MaxRequestsPerRecipe) return RecipeExecution.Invalid("Inspection recipe exceeds its request limit.");

        var plans = new List<(string Id, ProviderProbePlan Plan)>(requests.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < requests.Count; index++)
        {
            var request = requests[index];
            var id = request.Id?.Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                if (requests.Count > 1) return RecipeExecution.Invalid("Every request in a multi-request recipe must have an id.");
                id = "default";
            }

            if (!IsSafeId(id) || !ids.Add(id)) return RecipeExecution.Invalid("Request ids must be unique safe identifiers.");
            var single = new ProviderCapabilityRecipe { Status = recipe.Status, Strategy = recipe.Strategy, Request = request, Response = recipe.Outputs ?? recipe.Response };
            var plan = _planner.Plan(descriptor, single, baseUrl, capability);
            if (plan.Plan is null) return RecipeExecution.Invalid(plan.Error ?? "Inspection request was rejected.");
            plans.Add((id, plan.Plan));
        }

        var responses = new Dictionary<string, ProbeHttpResponse>(StringComparer.Ordinal);
        string? error = null;
        foreach (var (id, plan) in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await send(plan).ConfigureAwait(false);
            responses[id] = response;
            if (!response.Success) error ??= response.Error;
        }

        return new RecipeExecution(true, responses, error);
    }

    private static bool IsSafeId(string id) => id.Length is > 0 and <= 64 && char.IsAsciiLetter(id[0]) &&
        id.Skip(1).All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static string CacheKey(ProviderProbePlan plan)
    {
        var headers = plan.Headers is null ? string.Empty : string.Join("\n", plan.Headers.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{pair.Key.Trim().ToLowerInvariant()}:{pair.Value.Trim()}"));
        return $"{plan.Method}\n{plan.TargetUri.AbsoluteUri}\n{plan.AuthScheme}\n{headers}";
    }

    private static ProviderInspectionResult Failure(string error) => new(false, [], new Dictionary<string, CapabilityStatus>(), new ProviderBillingFacts(), DateTimeOffset.UtcNow, error);

    private static ProviderMetricSnapshot MergeMetricSnapshots(
        IReadOnlyList<ProviderMetricSnapshot> snapshots,
        DateTimeOffset observedAt,
        string? diagnostic)
    {
        var metrics = snapshots.SelectMany(snapshot => snapshot.Metrics)
            .GroupBy(metric => metric.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(metric => metric.ObservedAt).First())
            .ToArray();
        var availability = MergeAvailability(snapshots.Select(snapshot => snapshot.Availability),
            metrics.Any(metric => metric.Value.HasValue && metric.Availability == ProviderMetricAvailability.Live));
        return new ProviderMetricSnapshot
        {
            Metrics = metrics,
            ObservedAt = observedAt,
            Availability = availability,
            BalanceAvailability = MergeAvailability(snapshots.Select(snapshot => snapshot.BalanceAvailability),
                metrics.Any(metric => metric.Value.HasValue && metric.Availability == ProviderMetricAvailability.Live &&
                    metric.Kind is ProviderMetricKind.AvailableBalance or ProviderMetricKind.AllocatedCredits or ProviderMetricKind.PurchasedCredits)),
            UsageAvailability = MergeAvailability(snapshots.Select(snapshot => snapshot.UsageAvailability),
                metrics.Any(metric => metric.Value.HasValue && metric.Availability == ProviderMetricAvailability.Live &&
                    metric.Kind is ProviderMetricKind.Spending or ProviderMetricKind.QuotaLimit or
                    ProviderMetricKind.QuotaRemaining or ProviderMetricKind.ProviderCredits or ProviderMetricKind.ProviderSpecificUsage or
                    ProviderMetricKind.RequestCount or ProviderMetricKind.RequestAllowance or ProviderMetricKind.TokenCount or
                    ProviderMetricKind.TokenAllowance)),
            Diagnostic = diagnostic,
        };
    }

    private static ProviderMetricAvailability MergeAvailability(IEnumerable<ProviderMetricAvailability> source, bool hasLiveMetrics)
    {
        var statuses = source.ToArray();
        var hasFailure = statuses.Any(status => status is ProviderMetricAvailability.Unauthorized or
            ProviderMetricAvailability.Unavailable or ProviderMetricAvailability.InvalidResponse);
        if (hasLiveMetrics) return hasFailure || statuses.Contains(ProviderMetricAvailability.Partial)
            ? ProviderMetricAvailability.Partial
            : ProviderMetricAvailability.Live;
        if (statuses.Contains(ProviderMetricAvailability.Unauthorized)) return ProviderMetricAvailability.Unauthorized;
        if (statuses.Contains(ProviderMetricAvailability.Unavailable)) return ProviderMetricAvailability.Unavailable;
        if (statuses.Contains(ProviderMetricAvailability.InvalidResponse)) return ProviderMetricAvailability.InvalidResponse;
        if (statuses.Length > 0 && statuses.All(status => status == ProviderMetricAvailability.Unsupported))
            return ProviderMetricAvailability.Unsupported;
        return ProviderMetricAvailability.Unknown;
    }

    public void Dispose()
    {
        if (_transport is IDisposable disposable) disposable.Dispose();
    }

    private sealed record RecipeExecution(bool Valid, IReadOnlyDictionary<string, ProbeHttpResponse> Responses, string? Error)
    {
        public static RecipeExecution Invalid(string error) => new(false, new Dictionary<string, ProbeHttpResponse>(), error);
    }
}
