using System.Net;
using System.Globalization;
using System.Text.Json;
using Llm.ProviderKit.Catalog;
using Llm.ProviderKit.Inspection;
using Xunit;

namespace Llm.ProviderKit.Tests;

public sealed class ProviderMetricTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 11, 20, 10, 15, 0, TimeSpan.Zero);

    [Fact]
    public void ExplicitMappingCreatesTypedMonetaryBalanceWithScopeAndPeriod()
    {
        using var document = JsonDocument.Parse("""{"data":{"balance":"12.500000000000000001","reset_at":"2026-12-01T00:00:00Z"}}""");
        var mapping = new RecipeResponseMapping
        {
            Balance = new JsonFieldMapping
            {
                JsonPath = "/data/balance",
                Metric = new ProviderMetricDefinition
                {
                    Kind = ProviderMetricKind.AvailableBalance,
                    Scope = ProviderMetricScope.Account,
                    UnitKind = ProviderMetricUnitKind.Currency,
                    UnitCode = "USD",
                    Period = "monthly",
                    PeriodSemantics = ProviderMetricPeriodSemantics.BoundedInterval,
                    ResetAtPointer = "/data/reset_at",
                },
            },
        };

        var snapshot = new ProviderProbeResponseMapper().MapMetrics(Descriptor("account", "api.account.test"), "balance",
            SupportedRecipe(mapping), Response(document.RootElement), ObservedAt, activeBaseUrl: "https://api.account.test/v1");

        var metric = Assert.Single(snapshot.Metrics);
        Assert.Equal(ProviderMetricKind.AvailableBalance, metric.Kind);
        Assert.Equal(12.500000000000000001m, metric.Value);
        Assert.Equal(ProviderMetricScope.Account, metric.Scope);
        Assert.Equal(ProviderMetricUnitKind.Currency, metric.Unit.Kind);
        Assert.Equal("USD", metric.Unit.Code);
        Assert.Equal("monthly", metric.Period);
        Assert.Equal(ProviderMetricPeriodSemantics.BoundedInterval, metric.PeriodSemantics);
        Assert.Equal(DateTimeOffset.Parse("2026-12-01T00:00:00Z", CultureInfo.InvariantCulture), metric.ResetAt);
    }

    [Fact]
    public void SignedCatalogMetricMetadataParsesStringEnums()
    {
        const string json = """{"status":"Supported","strategy":"json-http","response":{"balance":{"pointer":"/data/available","metric":{"kind":"AvailableBalance","scope":"Account","unitKind":"Currency","unitCode":"USD","period":"billing-cycle","periodSemantics":"BillingCycle","resetAtPointer":"/data/reset_at"}}}}""";

        var recipe = JsonSerializer.Deserialize<ProviderCapabilityRecipe>(json);

        Assert.NotNull(recipe);
        var metric = Assert.IsType<ProviderMetricDefinition>(recipe!.Response!.Balance!.Metric);
        Assert.Equal(ProviderMetricKind.AvailableBalance, metric.Kind);
        Assert.Equal(ProviderMetricScope.Account, metric.Scope);
        Assert.Equal(ProviderMetricUnitKind.Currency, metric.UnitKind);
        Assert.Equal(ProviderMetricPeriodSemantics.BillingCycle, metric.PeriodSemantics);
        Assert.Equal("/data/reset_at", metric.ResetAtPointer);
    }

    [Fact]
    public async Task SignedOpenRouterStyleRecipeProducesKeySpendingAndAllowanceButNoAccountBalance()
    {
        var handler = new JsonHandler(_ => (HttpStatusCode.OK, """{"data":{"usage":25.50,"limit":100}}"""));
        using var client = new HttpClient(handler);
        using var transport = new SafeProviderHttpTransport(client);
        var credentials = new InMemoryProviderCredentialSource([("openrouter", "endpoint", "synthetic-token")]);
        using var inspector = new ProviderInspectionService(new ProviderProbePlanner(), transport,
            new ProviderProbeResponseMapper(), credentials);
        var descriptor = Descriptor("openrouter", "openrouter.ai", "https://openrouter.ai/api/v1");
        var keyRecipe = SupportedRecipe(new RecipeResponseMapping
        {
            Used = new JsonFieldMapping { JsonPath = "/data/usage" },
            Limit = new JsonFieldMapping { JsonPath = "/data/limit" },
            Currency = new CurrencyMapping { Literal = "USD" },
        }, url: "https://openrouter.ai/api/v1/key");
        descriptor.Capabilities.Balance = keyRecipe;
        descriptor.Capabilities.Usage = keyRecipe;

        var result = await inspector.InspectAsync(descriptor, "endpoint", "https://openrouter.ai/api/v1");

        Assert.Single(handler.Requests); // The duplicated signed recipe request is coalesced.
        Assert.Null(result.Billing.Balance);
        Assert.Null(result.Billing.Remaining);
        Assert.Equal(25.50m, result.Billing.Used);
        Assert.Equal(100m, result.Billing.Limit);
        Assert.Equal(ProviderMetricAvailability.Unknown, result.MetricSnapshot.BalanceAvailability);
        Assert.Equal(ProviderMetricAvailability.Live, result.MetricSnapshot.UsageAvailability);
        var spending = Assert.Single(result.MetricSnapshot.Metrics, metric => metric.Kind == ProviderMetricKind.Spending);
        var allowance = Assert.Single(result.MetricSnapshot.Metrics, metric => metric.Kind == ProviderMetricKind.QuotaLimit);
        Assert.Equal(ProviderMetricScope.ApiKey, spending.Scope);
        Assert.Equal(ProviderMetricScope.ApiKey, allowance.Scope);
        Assert.Equal(ProviderMetricUnit.Currency("USD"), spending.Unit);
        Assert.Equal(ProviderMetricUnit.Currency("USD"), allowance.Unit);
        Assert.DoesNotContain(result.MetricSnapshot.Metrics, metric => metric.Kind == ProviderMetricKind.AvailableBalance);
        Assert.DoesNotContain(result.MetricSnapshot.Metrics, metric => metric.Kind == ProviderMetricKind.QuotaRemaining);
    }

    [Fact]
    public void CurrencyBalanceRecordsKeepCurrenciesSeparateAndMapAccountScope()
    {
        using var document = JsonDocument.Parse("""{"balance_infos":[{"currency":"CNY","total_balance":"110.00","granted_balance":"10.00","topped_up_balance":"100.00"},{"currency":"USD","total_balance":"7.25","granted_balance":"0","topped_up_balance":"7.25"}]}""");

        var snapshot = ProviderMetricNormalizer.MapCurrencyBalanceRecords(document.RootElement, "/balance_infos", ObservedAt,
            ProviderMetricScope.Account, "deepseek balance fixture", new HashSet<string>(StringComparer.Ordinal) { "CNY", "USD" });

        Assert.Equal(ProviderMetricAvailability.Live, snapshot.Availability);
        var balances = snapshot.Metrics.Where(metric => metric.Kind == ProviderMetricKind.AvailableBalance).ToArray();
        Assert.Equal(2, balances.Length);
        Assert.Contains(balances, metric => metric.Value == 110m && metric.Unit.Code == "CNY");
        Assert.Contains(balances, metric => metric.Value == 7.25m && metric.Unit.Code == "USD");
        Assert.All(snapshot.Metrics, metric => Assert.Equal(ProviderMetricScope.Account, metric.Scope));
        Assert.Contains(snapshot.Metrics, metric => metric.Kind == ProviderMetricKind.AllocatedCredits && metric.Value == 10m);
        Assert.Contains(snapshot.Metrics, metric => metric.Kind == ProviderMetricKind.PurchasedCredits && metric.Value == 100m);
        Assert.Contains(snapshot.Metrics, metric => metric.Kind == ProviderMetricKind.AllocatedCredits && metric.Value == 0m);
        Assert.DoesNotContain(snapshot.Metrics, metric => metric.Id == "combined-balance");
    }

    [Fact]
    public void CurrencyBalanceNormalizerPreservesZeroAndDoesNotTurnNullIntoZero()
    {
        using var document = JsonDocument.Parse("""{"balance_infos":[{"currency":"USD","total_balance":0},{"currency":"CNY","total_balance":null}]}""");

        var snapshot = ProviderMetricNormalizer.MapCurrencyBalanceRecords(document.RootElement, "/balance_infos", ObservedAt,
            ProviderMetricScope.Account, "synthetic fixture", new HashSet<string>(StringComparer.Ordinal) { "USD", "CNY" });

        Assert.Equal(ProviderMetricAvailability.Partial, snapshot.Availability);
        var balance = Assert.Single(snapshot.Metrics);
        Assert.Equal("USD", balance.Unit.Code);
        Assert.Equal(0m, balance.Value);
        Assert.DoesNotContain(snapshot.Metrics, metric => metric.Unit.Code == "CNY");
    }

    [Fact]
    public void CurrencyBalanceNormalizerMarksInvalidCurrencyAndKeepsValidCurrenciesPartial()
    {
        using var document = JsonDocument.Parse("""{"balance_infos":[{"currency":"USD","total_balance":"0"},{"currency":"XYZ","total_balance":"4"}]}""");

        var snapshot = ProviderMetricNormalizer.MapCurrencyBalanceRecords(document.RootElement, "/balance_infos", ObservedAt,
            ProviderMetricScope.Account, "fixture", new HashSet<string>(StringComparer.Ordinal) { "USD", "CNY" });

        Assert.Equal(ProviderMetricAvailability.Partial, snapshot.Availability);
        Assert.Single(snapshot.Metrics);
        Assert.Equal(0m, snapshot.Metrics[0].Value);
    }

    [Fact]
    public void NumericStringsPreserveDecimalPrecisionAndRejectAmbiguousGrouping()
    {
        using var document = JsonDocument.Parse("""{"exact":"1234567890.123456789012345678","grouped":"1,234.50"}""");
        var mapper = new ProviderProbeResponseMapper();
        var exact = mapper.MapBilling(Response(document.RootElement), new RecipeResponseMapping
        {
            Used = new JsonFieldMapping { JsonPath = "/exact" },
        });
        var grouped = mapper.MapBilling(Response(document.RootElement), new RecipeResponseMapping
        {
            Used = new JsonFieldMapping { JsonPath = "/grouped" },
        });

        Assert.Equal(1234567890.123456789012345678m, exact.Used);
        Assert.Null(grouped.Used);
    }

    [Fact]
    public void ProviderCreditsUseProviderUnitAndNeverAcquireCurrency()
    {
        using var document = JsonDocument.Parse("""{"credits":"3.75"}""");
        var recipe = SupportedRecipe(new RecipeResponseMapping
        {
            Used = new JsonFieldMapping
            {
                JsonPath = "/credits",
                Metric = new ProviderMetricDefinition
                {
                    Kind = ProviderMetricKind.ProviderCredits,
                    Scope = ProviderMetricScope.Workspace,
                    UnitKind = ProviderMetricUnitKind.ProviderSpecific,
                    UnitCode = "vendor-credit",
                },
            },
        }, path: "/billing");

        var snapshot = new ProviderProbeResponseMapper().MapMetrics(Descriptor("credit-provider", "api.credit.test"),
            "usage", recipe, Response(document.RootElement), ObservedAt, activeBaseUrl: "https://api.credit.test/v1");

        var metric = Assert.Single(snapshot.Metrics);
        Assert.Equal(ProviderMetricKind.ProviderCredits, metric.Kind);
        Assert.Equal(ProviderMetricUnitKind.ProviderSpecific, metric.Unit.Kind);
        Assert.Equal("vendor-credit", metric.Unit.Code);
        Assert.Null(metric.Unit.Code is "USD" or "CNY" ? metric.Unit.Code : null);
        Assert.Equal(ProviderMetricScope.Workspace, metric.Scope);
    }

    [Fact]
    public void RequestAndTokenQuotaMappingsRetainTheirDistinctUnits()
    {
        using var document = JsonDocument.Parse("""{"request_used":12,"request_limit":50,"token_used":"4500","token_limit":10000}""");
        var mapper = new ProviderProbeResponseMapper();
        var requestSnapshot = mapper.MapMetrics(Descriptor("request-provider", "api.quota.test"), "usage",
            SupportedRecipe(new RecipeResponseMapping
            {
                Used = new JsonFieldMapping
                {
                    JsonPath = "/request_used",
                    Metric = new ProviderMetricDefinition { Kind = ProviderMetricKind.RequestCount, Scope = ProviderMetricScope.ApiKey, UnitKind = ProviderMetricUnitKind.Requests },
                },
                Limit = new JsonFieldMapping
                {
                    JsonPath = "/request_limit",
                    Metric = new ProviderMetricDefinition { Kind = ProviderMetricKind.RequestAllowance, Scope = ProviderMetricScope.ApiKey, UnitKind = ProviderMetricUnitKind.Requests },
                },
            }, path: "/billing"), Response(document.RootElement), ObservedAt, activeBaseUrl: "https://api.quota.test/v1");
        var tokenSnapshot = mapper.MapMetrics(Descriptor("token-provider", "api.quota.test"), "usage",
            SupportedRecipe(new RecipeResponseMapping
            {
                Used = new JsonFieldMapping
                {
                    JsonPath = "/token_used",
                    Metric = new ProviderMetricDefinition { Kind = ProviderMetricKind.TokenCount, Scope = ProviderMetricScope.Team, UnitKind = ProviderMetricUnitKind.Tokens },
                },
                Limit = new JsonFieldMapping
                {
                    JsonPath = "/token_limit",
                    Metric = new ProviderMetricDefinition { Kind = ProviderMetricKind.TokenAllowance, Scope = ProviderMetricScope.Team, UnitKind = ProviderMetricUnitKind.Tokens },
                },
            }, path: "/billing"), Response(document.RootElement), ObservedAt, activeBaseUrl: "https://api.quota.test/v1");

        Assert.Contains(requestSnapshot.Metrics, metric => metric.Kind == ProviderMetricKind.RequestCount && metric.Unit.Kind == ProviderMetricUnitKind.Requests);
        Assert.Contains(requestSnapshot.Metrics, metric => metric.Kind == ProviderMetricKind.RequestAllowance && metric.Value == 50m);
        Assert.Contains(tokenSnapshot.Metrics, metric => metric.Kind == ProviderMetricKind.TokenCount && metric.Value == 4500m && metric.Unit.Kind == ProviderMetricUnitKind.Tokens);
        Assert.Contains(tokenSnapshot.Metrics, metric => metric.Kind == ProviderMetricKind.TokenAllowance && metric.Value == 10000m);
    }

    [Fact]
    public void DerivesRemainingOnlyForLiveCompatibleDocumentedMetricsAndPreservesZero()
    {
        var limit = Metric("key-limit", ProviderMetricKind.QuotaLimit, 100m, ProviderMetricScope.ApiKey,
            ProviderMetricUnit.Currency("USD"), "monthly", ProviderMetricPeriodSemantics.CalendarMonth);
        var used = Metric("key-spend", ProviderMetricKind.Spending, 100m, ProviderMetricScope.ApiKey,
            ProviderMetricUnit.Currency("USD"), "monthly", ProviderMetricPeriodSemantics.CalendarMonth);

        Assert.True(ProviderMetricNormalizer.TryDeriveRemaining(limit, used, relationshipDocumented: true, out var remaining, out _));
        Assert.Equal(0m, remaining?.Value);
        Assert.Equal(ProviderMetricKind.QuotaRemaining, remaining?.Kind);
    }

    [Fact]
    public void RejectsDerivationForUndocumentedOrIncompatibleMetricPairs()
    {
        var limit = Metric("limit", ProviderMetricKind.QuotaLimit, 10m, ProviderMetricScope.ApiKey,
            ProviderMetricUnit.Currency("USD"), "daily", ProviderMetricPeriodSemantics.CalendarDay);
        var usedDifferentScope = Metric("used", ProviderMetricKind.Spending, 2m, ProviderMetricScope.Account,
            ProviderMetricUnit.Currency("USD"), "daily", ProviderMetricPeriodSemantics.CalendarDay);
        var usedDifferentCurrency = usedDifferentScope with
        {
            Scope = ProviderMetricScope.ApiKey,
            Unit = ProviderMetricUnit.Currency("CNY"),
        };
        var usedDifferentPeriod = usedDifferentScope with
        {
            Scope = ProviderMetricScope.ApiKey,
            Unit = ProviderMetricUnit.Currency("USD"),
            Period = "monthly",
            PeriodSemantics = ProviderMetricPeriodSemantics.CalendarMonth,
        };

        Assert.False(ProviderMetricNormalizer.TryDeriveRemaining(limit, usedDifferentScope, true, out _, out _));
        Assert.False(ProviderMetricNormalizer.TryDeriveRemaining(limit, usedDifferentCurrency, true, out _, out _));
        Assert.False(ProviderMetricNormalizer.TryDeriveRemaining(limit, usedDifferentPeriod, true, out _, out _));
        Assert.False(ProviderMetricNormalizer.TryDeriveRemaining(limit, usedDifferentScope, false, out _, out _));
    }

    [Fact]
    public void UnlimitedQuotaCannotBeDerivedIntoANumericRemainingValue()
    {
        var limit = new ProviderMetric
        {
            Id = "unlimited-key-quota",
            Kind = ProviderMetricKind.QuotaLimit,
            Scope = ProviderMetricScope.ApiKey,
            Unit = ProviderMetricUnit.Provider("provider-quota-units"),
            Availability = ProviderMetricAvailability.Live,
            ObservedAt = ObservedAt,
            IsUnlimited = true,
        };
        var used = new ProviderMetric
        {
            Id = "key-used",
            Kind = ProviderMetricKind.ProviderSpecificUsage,
            Value = 10m,
            Scope = ProviderMetricScope.ApiKey,
            Unit = limit.Unit,
            Availability = ProviderMetricAvailability.Live,
            ObservedAt = ObservedAt,
        };

        Assert.False(ProviderMetricNormalizer.TryDeriveRemaining(limit, used, relationshipDocumented: true,
            out var remaining, out _));
        Assert.Null(remaining);
    }

    [Theory]
    [InlineData(401, ProviderMetricAvailability.Unauthorized)]
    [InlineData(403, ProviderMetricAvailability.Unauthorized)]
    [InlineData(503, ProviderMetricAvailability.Unavailable)]
    public async Task InspectionDistinguishesUnauthorizedFromUnavailable(int statusCode, ProviderMetricAvailability expected)
    {
        var handler = new JsonHandler(_ => ((HttpStatusCode)statusCode, "{}"));
        using var client = new HttpClient(handler);
        using var transport = new SafeProviderHttpTransport(client);
        using var inspector = new ProviderInspectionService(new ProviderProbePlanner(), transport,
            new ProviderProbeResponseMapper(), new InMemoryProviderCredentialSource([("test", "endpoint", "synthetic-token")]));
        var provider = Descriptor("test", "api.test.test");
        provider.Capabilities.Balance = SupportedRecipe(new RecipeResponseMapping
        {
            Balance = new JsonFieldMapping
            {
                JsonPath = "/balance",
                Metric = new ProviderMetricDefinition
                {
                    Kind = ProviderMetricKind.AvailableBalance,
                    Scope = ProviderMetricScope.Account,
                    UnitKind = ProviderMetricUnitKind.Currency,
                    UnitCode = "USD",
                }
            },
        }, path: "/billing");

        var result = await inspector.InspectAsync(provider, "endpoint", "https://api.test.test/v1");

        Assert.Equal(expected, result.MetricSnapshot.BalanceAvailability);
        var metric = Assert.Single(result.MetricSnapshot.Metrics);
        Assert.Null(metric.Value);
        Assert.Equal(expected, metric.Availability);
        Assert.Null(result.Billing.Balance);
    }

    [Fact]
    public async Task InspectionKeepsPartialMetricWhenAnotherRecipeRequestFails()
    {
        var handler = new JsonHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/usage", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, """{"amount":"4.125"}""")
            : (HttpStatusCode.ServiceUnavailable, "{}"));
        using var client = new HttpClient(handler);
        using var transport = new SafeProviderHttpTransport(client);
        using var inspector = new ProviderInspectionService(new ProviderProbePlanner(), transport,
            new ProviderProbeResponseMapper(), new InMemoryProviderCredentialSource([("test", "endpoint", "synthetic-token")]));
        var provider = Descriptor("test", "api.test.test");
        provider.Capabilities.Usage = new ProviderCapabilityRecipe
        {
            Status = CapabilityStatus.Supported,
            Strategy = "json-http",
            Requests =
            [
                new RecipeRequest { Id = "usage", Method = "GET", Path = "/usage", Auth = "bearer" },
                new RecipeRequest { Id = "limit", Method = "GET", Path = "/limit", Auth = "bearer" },
            ],
            Response = new RecipeResponseMapping
            {
                Used = new JsonFieldMapping
                {
                    RequestId = "usage",
                    JsonPath = "/amount",
                    Metric = new ProviderMetricDefinition
                    {
                        Kind = ProviderMetricKind.Spending,
                        Scope = ProviderMetricScope.ApiKey,
                        UnitKind = ProviderMetricUnitKind.Currency,
                        UnitCode = "USD",
                    }
                },
                Limit = new JsonFieldMapping
                {
                    RequestId = "limit",
                    JsonPath = "/amount",
                    Metric = new ProviderMetricDefinition
                    {
                        Kind = ProviderMetricKind.QuotaLimit,
                        Scope = ProviderMetricScope.ApiKey,
                        UnitKind = ProviderMetricUnitKind.Currency,
                        UnitCode = "USD",
                    }
                },
            },
        };

        var result = await inspector.InspectAsync(provider, "endpoint", "https://api.test.test/v1");

        Assert.Equal(ProviderMetricAvailability.Partial, result.MetricSnapshot.Availability);
        Assert.Equal(ProviderMetricAvailability.Partial, result.MetricSnapshot.UsageAvailability);
        var spending = Assert.Single(result.MetricSnapshot.Metrics, metric => metric.Kind == ProviderMetricKind.Spending);
        Assert.Equal(4.125m, spending.Value);
        var missingLimit = Assert.Single(result.MetricSnapshot.Metrics, metric => metric.Kind == ProviderMetricKind.QuotaLimit);
        Assert.Null(missingLimit.Value);
        Assert.Equal(ProviderMetricAvailability.Unavailable, missingLimit.Availability);
    }

    [Fact]
    public void MissingAndUnsupportedCapabilitiesStayDistinct()
    {
        var mapper = new ProviderProbeResponseMapper();
        var descriptor = Descriptor("test", "api.test.test");
        var unsupported = new ProviderCapabilityRecipe { Status = CapabilityStatus.Unsupported };
        var unknown = new ProviderCapabilityRecipe { Status = CapabilityStatus.Unknown };

        var unsupportedSnapshot = mapper.MapMetrics(descriptor, "balance", unsupported,
            new Dictionary<string, ProbeHttpResponse>(), ObservedAt);
        var unknownSnapshot = mapper.MapMetrics(descriptor, "usage", unknown,
            new Dictionary<string, ProbeHttpResponse>(), ObservedAt);

        Assert.Equal(ProviderMetricAvailability.Unsupported, unsupportedSnapshot.BalanceAvailability);
        Assert.Equal(ProviderMetricAvailability.Unknown, unknownSnapshot.UsageAvailability);
    }

    [Fact]
    public async Task NewApiRecipeReadsOnlyTheDeclaredSameOriginPathAndKeepsAccountMetricsSeparate()
    {
        var handler = new JsonHandler(request => request.RequestUri!.AbsolutePath == "/management/token-usage"
            ? (HttpStatusCode.OK, """{"code":true,"data":{"total_granted":2500000,"total_used":0,"total_available":2500000,"unlimited_quota":false,"expires_at":1795314482}}""")
            : (HttpStatusCode.OK, """{"wallet":"12.5"}"""));
        using var client = new HttpClient(handler);
        using var transport = new SafeProviderHttpTransport(client);
        using var inspector = new ProviderInspectionService(new ProviderProbePlanner(), transport,
            new ProviderProbeResponseMapper(), new InMemoryProviderCredentialSource([("new-api-test", "endpoint", "synthetic-token")]));
        var provider = Descriptor("new-api-test", "api.test.test");
        provider.Capabilities.Usage = TokenUsageRecipe("/management/token-usage");
        provider.Capabilities.Balance = SupportedRecipe(new RecipeResponseMapping
        {
            Balance = new JsonFieldMapping
            {
                JsonPath = "/wallet",
                Metric = new ProviderMetricDefinition
                {
                    Kind = ProviderMetricKind.AvailableBalance,
                    Scope = ProviderMetricScope.Account,
                    UnitKind = ProviderMetricUnitKind.Currency,
                    UnitCode = "USD",
                },
            },
        }, path: "/wallet");

        var result = await inspector.InspectAsync(provider, "endpoint", "https://api.test.test/v1");

        Assert.Contains(new Uri("https://api.test.test/management/token-usage"), handler.Requests);
        var keyMetrics = result.MetricSnapshot.Metrics.Where(metric => metric.Scope == ProviderMetricScope.ApiKey).ToArray();
        var quota = Assert.Single(keyMetrics, metric => metric.Kind == ProviderMetricKind.QuotaLimit);
        Assert.Equal(2_500_000m, quota.Value);
        Assert.Equal(2_500_000m, Assert.Single(keyMetrics, metric => metric.Kind == ProviderMetricKind.QuotaRemaining).Value);
        Assert.All(keyMetrics, metric => Assert.Equal(ProviderMetricUnit.Provider("quota-units"), metric.Unit));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1795314482), quota.ExpiresAt);
        Assert.Null(quota.ResetAt);
        var account = Assert.Single(result.MetricSnapshot.Metrics, metric => metric.Scope == ProviderMetricScope.Account);
        Assert.Equal(12.5m, account.Value);
        Assert.Equal(ProviderMetricUnit.Currency("USD"), account.Unit);
        Assert.Equal(ProviderMetricAvailability.Live, result.MetricSnapshot.UsageAvailability);
        Assert.Null(result.Billing.Limit);
    }

    [Fact]
    public async Task SameOriginKeysHaveIndependentFiniteAndUnlimitedUsage()
    {
        var handler = new JsonHandler(request => request.Headers.Authorization?.Parameter switch
        {
            "synthetic-limited" => (HttpStatusCode.OK, """{"code":true,"data":{"total_granted":2500000,"total_used":200000,"total_available":2300000,"unlimited_quota":false}}"""),
            "synthetic-unlimited" => (HttpStatusCode.OK, """{"success":true,"data":{"total_granted":0,"total_used":37,"total_available":0,"unlimited_quota":true,"expires_at":0}}"""),
            _ => (HttpStatusCode.Unauthorized, "{}"),
        });
        using var client = new HttpClient(handler);
        using var transport = new SafeProviderHttpTransport(client);
        using var inspector = new ProviderInspectionService(new ProviderProbePlanner(), transport,
            new ProviderProbeResponseMapper(), new InMemoryProviderCredentialSource(
            [("new-api-test", "limited", "synthetic-limited"), ("new-api-test", "unlimited", "synthetic-unlimited")]));
        var provider = Descriptor("new-api-test", "api.test.test");
        provider.Capabilities.Usage = TokenUsageRecipe("/api/usage/token");

        var finite = await inspector.InspectAsync(provider, "limited", "https://api.test.test/v1");
        var unlimited = await inspector.InspectAsync(provider, "unlimited", "https://api.test.test/v1");

        Assert.Equal(2_300_000m, Assert.Single(finite.MetricSnapshot.Metrics,
            metric => metric.Kind == ProviderMetricKind.QuotaRemaining).Value);
        var quota = Assert.Single(unlimited.MetricSnapshot.Metrics, metric => metric.Kind == ProviderMetricKind.QuotaLimit);
        Assert.True(quota.IsUnlimited);
        Assert.Null(quota.Value);
        Assert.Null(quota.ExpiresAt);
        Assert.DoesNotContain(unlimited.MetricSnapshot.Metrics, metric => metric.Kind == ProviderMetricKind.QuotaRemaining);
        Assert.Equal(37m, Assert.Single(unlimited.MetricSnapshot.Metrics,
            metric => metric.Kind == ProviderMetricKind.ProviderSpecificUsage).Value);
        Assert.Equal(ProviderMetricAvailability.Live, unlimited.MetricSnapshot.UsageAvailability);
        Assert.Equal(ProviderMetricAvailability.Unknown, unlimited.MetricSnapshot.BalanceAvailability);
        Assert.Null(unlimited.Billing.Balance);
        Assert.Null(unlimited.Billing.Currency);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(200, """{"code":false,"data":{"unlimited_quota":true,"total_used":0}}""", ProviderMetricAvailability.InvalidResponse)]
    [InlineData(200, """{"code":true,"data":{"unlimited_quota":false,"total_granted":5,"total_used":0,"total_available":3}}""", ProviderMetricAvailability.InvalidResponse)]
    [InlineData(200, """{"success":true,"data":{"total_granted":0,"total_used":0,"total_available":0}}""", ProviderMetricAvailability.InvalidResponse)]
    [InlineData(401, "{}", ProviderMetricAvailability.Unauthorized)]
    [InlineData(429, "{}", ProviderMetricAvailability.Unavailable)]
    public async Task NewApiRecipeDoesNotTurnErrorsIntoZero(int statusCode, string body, ProviderMetricAvailability expected)
    {
        using var client = new HttpClient(new JsonHandler(_ => ((HttpStatusCode)statusCode, body)));
        using var inspector = new ProviderInspectionService(new ProviderProbePlanner(), new SafeProviderHttpTransport(client),
            new ProviderProbeResponseMapper(), new InMemoryProviderCredentialSource([("new-api-test", "endpoint", "synthetic-token")]));
        var provider = Descriptor("new-api-test", "api.test.test");
        provider.Capabilities.Usage = TokenUsageRecipe("/api/usage/token");

        var result = await inspector.InspectAsync(provider, "endpoint", "https://api.test.test/v1");

        Assert.Empty(result.MetricSnapshot.Metrics);
        Assert.Equal(expected, result.MetricSnapshot.UsageAvailability);
        Assert.Equal(CapabilityStatus.Unknown, result.Capabilities["usage"]);
    }

    [Fact]
    public void NewApiRecipeCannotSendAnApiKeyToAnotherTrustedOrigin()
    {
        var provider = Descriptor("new-api-test", "api.test.test");
        provider.TrustedHosts.Add("other.test.test");
        var recipe = TokenUsageRecipe("/api/usage/token");
        recipe.Request!.Url = "https://other.test.test/api/usage/token";

        var plan = new ProviderProbePlanner().Plan(provider, recipe, "https://api.test.test/v1", "usage");

        Assert.Null(plan.Plan);
    }

    private static ProviderCapabilityRecipe TokenUsageRecipe(string path) => new()
    {
        Status = CapabilityStatus.Supported,
        Strategy = "new-api-token-usage",
        Request = new RecipeRequest { Method = "GET", Path = path, Auth = "bearer" },
    };


    [Fact]
    public async Task DeepSeekCandidateFixtureDoesNotActivateCatalogBillingEndpoint()
    {
        var handler = new JsonHandler(_ => (HttpStatusCode.OK, """{"data":[]}"""));
        using var client = new HttpClient(handler);
        using var transport = new SafeProviderHttpTransport(client);
        using var inspector = new ProviderInspectionService(new ProviderProbePlanner(), transport,
            new ProviderProbeResponseMapper(), new InMemoryProviderCredentialSource([("deepseek", "endpoint", "synthetic-token")]));
        var descriptor = Descriptor("deepseek", "api.deepseek.com", "https://api.deepseek.com/v1");

        var result = await inspector.InspectAsync(descriptor, "endpoint", "https://api.deepseek.com/v1");

        Assert.Empty(handler.Requests);
        Assert.Equal(ProviderMetricAvailability.Unknown, result.MetricSnapshot.BalanceAvailability);
        Assert.Empty(result.MetricSnapshot.Metrics);
    }

    [Fact]
    public async Task ModelOnlyRefreshNeverRunsBillingRecipe()
    {
        var handler = new JsonHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, """{"data":[{"id":"model-a"}]}""")
            : (HttpStatusCode.OK, """{"balance":5}"""));
        using var client = new HttpClient(handler);
        using var transport = new SafeProviderHttpTransport(client);
        using var inspector = new ProviderInspectionService(new ProviderProbePlanner(), transport,
            new ProviderProbeResponseMapper(), new InMemoryProviderCredentialSource([("test", "endpoint", "synthetic-token")]));
        var provider = Descriptor("test", "api.test.test");
        provider.Capabilities.Models = SupportedRecipe(new RecipeResponseMapping
        {
            Models = new JsonFieldMapping { JsonPath = "/data" },
        }, path: "/models");
        provider.Capabilities.Models.Strategy = "openai-models";
        provider.Capabilities.Balance = SupportedRecipe(new RecipeResponseMapping
        {
            Balance = new JsonFieldMapping { JsonPath = "/balance" },
        }, path: "/billing");

        var result = await inspector.DiscoverModelsAsync(provider, "endpoint", "https://api.test.test/v1");

        Assert.Equal(["model-a"], result.ModelIds);
        Assert.Single(handler.Requests);
        Assert.EndsWith("/models", handler.Requests[0].AbsolutePath, StringComparison.Ordinal);
        Assert.Empty(result.MetricSnapshot.Metrics);
    }

    private static ProviderMetric Metric(string id, ProviderMetricKind kind, decimal value, ProviderMetricScope scope,
        ProviderMetricUnit unit, string period, ProviderMetricPeriodSemantics semantics) => new()
        {
            Id = id,
            Kind = kind,
            Value = value,
            Scope = scope,
            Unit = unit,
            Availability = ProviderMetricAvailability.Live,
            ObservedAt = ObservedAt,
            Period = period,
            PeriodSemantics = semantics,
        };

    private static ProviderCapabilityRecipe SupportedRecipe(RecipeResponseMapping mapping, string? path = null, string? url = null) => new()
    {
        Status = CapabilityStatus.Supported,
        Strategy = "json-http",
        Request = new RecipeRequest { Method = "GET", Url = url, Path = path, Auth = "bearer" },
        Response = mapping,
    };

    private static ProviderDescriptor Descriptor(string id, string host, string? baseUrl = null) => new()
    {
        Id = id,
        DisplayName = id,
        Match = new ProviderMatchCriteria { ExactHosts = [host] },
        TrustedHosts = [host],
        Routes = [new ProviderRoute { Id = "primary", BaseUrl = baseUrl ?? $"https://{host}/v1", IsDefault = true }],
    };

    private static Dictionary<string, ProbeHttpResponse> Response(JsonElement json) =>
        new Dictionary<string, ProbeHttpResponse> { ["default"] = new(true, json.Clone(), 200, null) };

    private sealed class JsonHandler(Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            var (status, body) = respond(request);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
