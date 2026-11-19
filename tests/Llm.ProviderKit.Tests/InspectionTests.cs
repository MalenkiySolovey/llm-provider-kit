using System.Net;
using System.Text.Json;
using Llm.ProviderKit.Catalog;
using Llm.ProviderKit.Inspection;
using Llm.ProviderKit.Models;

namespace Llm.ProviderKit.Tests;

public sealed class InspectionTests
{
    [Theory]
    [InlineData("POST", "https://api.example.test/v1/models")]
    [InlineData("GET", "http://remote.example.test/models")]
    [InlineData("GET", "https://attacker.example.test/models")]
    public void PlannerRejectsUnsafeMethodsSchemesAndHosts(string method, string target)
    {
        var descriptor = Provider();
        var plan = new ProviderProbePlanner().Plan(descriptor,
            new ProviderCapabilityRecipe { Request = new RecipeRequest { Method = method, Url = target, Auth = "bearer" } },
            "https://api.example.test/v1", "models");
        Assert.False(plan.IsValid);
        Assert.Null(plan.Plan);
    }

    [Fact]
    public void PlannerRejectsCredentialAndControlHeadersInCatalog()
    {
        var recipe = new ProviderCapabilityRecipe
        {
            Request = new RecipeRequest { Method = "GET", Path = "/models", Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer leaked" } },
        };
        Assert.Contains("credentials", new ProviderProbePlanner().Plan(Provider(), recipe, "https://api.example.test/v1", "models").Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MapperExtractsExactModelIdsAndBoundedBillingFacts()
    {
        using var doc = JsonDocument.Parse("""{"data":[{"id":"model:free"},{"id":"model"}],"balance_cents":1250,"used":3}""");
        var response = new ProbeHttpResponse(true, doc.RootElement.Clone(), 200, null);
        var mapper = new ProviderProbeResponseMapper();
        Assert.Equal(["model:free", "model"], mapper.MapModels(response, "/data").ModelIds);
        var billing = mapper.MapBilling(new Dictionary<string, ProbeHttpResponse> { ["default"] = response },
            new RecipeResponseMapping
            {
                Balance = new JsonFieldMapping { JsonPath = "/balance_cents", Transform = "centsToUnits" },
                Used = new JsonFieldMapping { JsonPath = "/used" },
            });
        Assert.Equal(12.5m, billing.Balance);
        Assert.Equal(3m, billing.Used);
    }

    [Fact]
    public async Task TransportBlocksUntrustedRedirectWithoutFollowingIt()
    {
        var handler = new RedirectHandler();
        using var client = new HttpClient(handler);
        using var transport = new SafeProviderHttpTransport(client);
        var plan = new ProviderProbePlan(new Uri("https://api.example.test/v1/models"), "GET", "bearer", null,
            ["api.example.test"], "models");
        var result = await transport.SendAsync(plan, Provider(), ProviderCredential.FromSecret("synthetic-secret"));
        Assert.False(result.Success);
        Assert.Contains("allowlist", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal("synthetic-secret", handler.FirstAuthorization);
    }

    [Fact]
    public async Task TransportEnforcesResponseSizeLimit()
    {
        using var client = new HttpClient(new OversizedResponseHandler());
        using var transport = new SafeProviderHttpTransport(client);
        var plan = new ProviderProbePlan(new Uri("https://api.example.test/models"), "GET", "none", null,
            ["api.example.test"], "models", TransportOptions: new ProviderTransportOptions(MaxResponseBytes: 8));
        var result = await transport.SendAsync(plan, Provider(), null);
        Assert.False(result.Success);
        Assert.Contains("size limit", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InspectorUsesMockTransportAndDoesNotProbeUnsupportedCapability()
    {
        var handler = new ModelResponseHandler();
        using var client = new HttpClient(handler);
        using var transport = new SafeProviderHttpTransport(client);
        var credentials = new InMemoryProviderCredentialSource([("example", "endpoint", "synthetic-token")]);
        using var inspector = new ProviderInspectionService(new ProviderProbePlanner(), transport, new ProviderProbeResponseMapper(), credentials);
        var provider = Provider();
        provider.Capabilities.Models.Status = CapabilityStatus.Supported;
        provider.Capabilities.Models.Strategy = "openai-models";
        provider.Capabilities.Models.Request = new RecipeRequest { Method = "GET", Path = "/models", Auth = "bearer" };
        var result = await inspector.InspectAsync(provider, "endpoint", "https://api.example.test/v1");
        Assert.True(result.Succeeded);
        Assert.Equal(["model:free", "model"], result.ModelIds);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal("synthetic-token", handler.Authorization);
    }

    [Fact]
    public async Task UnknownProviderInspectionUsesOnlyInjectedDocumentedBillingStrategy()
    {
        var handler = new GenericBillingHandler();
        using var client = new HttpClient(handler);
        using var transport = new SafeProviderHttpTransport(client);
        var credentials = new InMemoryProviderCredentialSource([("generic-unknown", "endpoint", "synthetic-token")]);
        using var inspector = new ProviderInspectionService(new ProviderProbePlanner(), transport,
            new ProviderProbeResponseMapper(), credentials, [new DocumentedUsageStrategy()]);

        var result = await inspector.InspectUnknownAsync("endpoint", "https://custom-proxy.internal/v1");

        Assert.True(result.Succeeded);
        Assert.Equal(["model-x"], result.ModelIds);
        Assert.Equal(2m, result.Billing.Used);
        Assert.Equal(CapabilityStatus.Supported, result.Capabilities["usage"]);
        Assert.Equal(2, handler.RequestCount);
        Assert.All(handler.AuthorizationValues, value => Assert.Equal("synthetic-token", value));
    }

    private static ProviderDescriptor Provider() => new()
    {
        Id = "example",
        DisplayName = "Example",
        Match = new ProviderMatchCriteria { ExactHosts = ["api.example.test"] },
        TrustedHosts = ["api.example.test"],
        Routes = [new ProviderRoute { Id = "primary", BaseUrl = "https://api.example.test/v1" }],
    };

    private sealed class RedirectHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string? FirstAuthorization { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            FirstAuthorization = request.Headers.Authorization?.Parameter;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://evil.example.test/steal") } });
        }
    }

    private sealed class OversizedResponseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"this is too long\":true}") });
    }

    private sealed class ModelResponseHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string? Authorization { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Authorization = request.Headers.Authorization?.Parameter;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[{\"id\":\"model:free\"},{\"id\":\"model\"}]}") });
        }
    }

    private sealed class DocumentedUsageStrategy : IGenericProviderBillingStrategy
    {
        public string Id => "synthetic-usage";
        public int Order => 10;
        public ProviderCapabilityRecipe? CreateRecipe(Uri providerOrigin) =>
            providerOrigin.Host.Equals("custom-proxy.internal", StringComparison.OrdinalIgnoreCase)
                ? new ProviderCapabilityRecipe
                {
                    Status = CapabilityStatus.Supported,
                    Strategy = "synthetic-read-only-usage",
                    Requests = [new RecipeRequest { Id = "usage", Method = "GET", Path = "/usage", Auth = "bearer" }],
                    Outputs = new RecipeResponseMapping
                    {
                        Used = new JsonFieldMapping { RequestId = "usage", JsonPath = "/total" },
                    },
                }
                : null;
    }

    private sealed class GenericBillingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public List<string?> AuthorizationValues { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            AuthorizationValues.Add(request.Headers.Authorization?.Parameter);
            var body = request.RequestUri?.AbsolutePath.EndsWith("/usage", StringComparison.Ordinal) == true
                ? "{\"total\":2}"
                : "{\"data\":[{\"id\":\"model-x\"}]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
