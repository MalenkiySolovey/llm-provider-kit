using System.Net;
using Llm.ProviderKit.Catalog;
using Llm.ProviderKit.Inspection;
using Llm.ProviderKit.Models;

var handler = new SyntheticModelsHandler();
using var httpClient = new HttpClient(handler);
using var transport = new SafeProviderHttpTransport(httpClient);
var credentialSource = new InMemoryProviderCredentialSource([("synthetic", "endpoint-1", "synthetic-token")]);
using var inspector = new ProviderInspectionService(new ProviderProbePlanner(), transport, new ProviderProbeResponseMapper(), credentialSource);

var provider = new ProviderDescriptor
{
    Id = "synthetic",
    DisplayName = "Synthetic provider",
    Match = new ProviderMatchCriteria { ExactHosts = ["api.synthetic.test"] },
    TrustedHosts = ["api.synthetic.test"],
    Routes = [new ProviderRoute { Id = "primary", DisplayName = "Primary", BaseUrl = "https://api.synthetic.test/v1", IsDefault = true }],
    Capabilities = new ProviderCapabilities
    {
        Models = new ProviderCapabilityRecipe
        {
            Status = CapabilityStatus.Supported,
            Strategy = "openai-models",
            Request = new RecipeRequest { Method = "GET", Path = "/models", Auth = "bearer" },
            Response = new RecipeResponseMapping { Models = new JsonFieldMapping { JsonPath = "/data" } },
        },
        Balance = new ProviderCapabilityRecipe { Status = CapabilityStatus.Unsupported },
        Usage = new ProviderCapabilityRecipe { Status = CapabilityStatus.Unsupported },
    },
};

var catalog = new ProviderCatalog { Providers = [provider] };
if (ProviderCatalogValidator.Validate(catalog) is { } catalogError) throw new InvalidDataException(catalogError);
var inventory = new ProviderModelInventory
{
    Models =
    [
        new ProviderModelItem { Id = "model:free", Enabled = false, DiscoverySource = ModelDiscoverySource.Manual },
        new ProviderModelItem { Id = "manual-only", Enabled = true, DiscoverySource = ModelDiscoverySource.Manual },
    ],
};
var refresher = new ProviderModelInventoryRefreshService(inspector);
var merge = await refresher.RefreshAsync(provider, "endpoint-1", "https://api.synthetic.test/v1", inventory, DateTimeOffset.UnixEpoch);
if (!merge.Succeeded || merge.ReportedCount != 2) throw new InvalidOperationException("Synthetic model discovery did not return both exact IDs.");
if (handler.RequestCount != 1 || handler.SawUnexpectedCredential) throw new InvalidOperationException("The mock transport did not receive the expected isolated request.");

var snapshot = inventory.GetDeterministicSnapshot();
if (snapshot.Single(model => model.Id == "model:free") is not { Enabled: false, DiscoverySource: ModelDiscoverySource.Mixed })
    throw new InvalidOperationException("The manually disabled model was not preserved.");
if (snapshot.Single(model => model.Id == "manual-only") is not { Enabled: true, DiscoverySource: ModelDiscoverySource.Manual, Availability: ModelAvailability.NotReported })
    throw new InvalidOperationException("The unreported manual model was not preserved.");
if (snapshot.Count(model => model.Id == "model") != 1 || merge.Added != 1)
    throw new InvalidOperationException("Exact model identifiers did not merge deterministically.");

var unsupported = new ProviderDescriptor { Id = "unsupported", DisplayName = "Unsupported", Capabilities = new ProviderCapabilities { Models = new ProviderCapabilityRecipe { Status = CapabilityStatus.Unsupported } } };
var unsupportedResult = await inspector.InspectAsync(unsupported, "endpoint-1", "https://api.synthetic.test/v1");
if (unsupportedResult.Succeeded || handler.RequestCount != 1) throw new InvalidOperationException("Unsupported capabilities unexpectedly sent a request.");

Console.WriteLine($"PASS: discovered={merge.ReportedCount}; added={merge.Added}; manual-preserved={merge.ManualPreserved}; requests={handler.RequestCount}");

internal sealed class SyntheticModelsHandler : HttpMessageHandler
{
    public int RequestCount { get; private set; }
    public bool SawUnexpectedCredential { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        SawUnexpectedCredential = request.Headers.Authorization?.Parameter != "synthetic-token";
        if (request.Method != HttpMethod.Get || request.RequestUri?.AbsoluteUri != "https://api.synthetic.test/v1/models")
            throw new InvalidOperationException("The inspection request did not match the synthetic route.");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"data\":[{\"id\":\"model:free\"},{\"id\":\"model\"}]}", System.Text.Encoding.UTF8, "application/json"),
        });
    }
}
