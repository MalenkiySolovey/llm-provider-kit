using System.Text;
using System.Text.Json;
using Llm.ProviderKit.Catalog;
using Llm.ProviderKit.Models;

namespace Llm.ProviderKit.Tests;

public sealed class CatalogAndInventoryTests
{
    [Fact]
    public void CatalogParserPreservesUnknownApplicationExtensionAndMatcherIsExact()
    {
        const string json = """
            {"schemaVersion":1,"catalogVersion":2,"providers":[{"id":"example","displayName":"Example","match":{"exactHosts":["api.example.test"]},"trustedHosts":["api.example.test"],"routes":[{"id":"primary","baseUrl":"https://api.example.test/v1"}],"codex":{"wireApi":"responses","defaultModel":"model-x"}}]}
            """;
        var result = ProviderCatalogValidator.ParseAndValidate(Encoding.UTF8.GetBytes(json));
        Assert.True(result.IsValid, result.Error);
        var provider = Assert.Single(result.Catalog!.Providers);
        Assert.True(provider.AdditionalProperties!.ContainsKey("codex"));
        Assert.True(ProviderMatcher.Matches(provider, "https://api.example.test/v1"));
        Assert.False(ProviderMatcher.Matches(provider, "https://api.example.test.attacker.invalid/v1"));
    }

    [Fact]
    public void CatalogValidationRejectsDuplicateIdsAndRemoteHttpRoutes()
    {
        var catalog = new ProviderCatalog
        {
            Providers = [ValidProvider("same"), ValidProvider("SAME")],
        };
        Assert.Contains("duplicate", ProviderCatalogValidator.Validate(catalog), StringComparison.OrdinalIgnoreCase);

        var provider = ValidProvider("insecure");
        provider.Routes[0].BaseUrl = "http://example.test/v1";
        Assert.Contains("safe BaseUrl", ProviderCatalogValidator.Validate(provider), StringComparison.Ordinal);
    }

    [Fact]
    public void InventoryMergePreservesManualAndDisabledEntriesAndExactIds()
    {
        var inventory = new ProviderModelInventory
        {
            Models =
            [
                new ProviderModelItem { Id = "manual-disabled", Enabled = false, DiscoverySource = ModelDiscoverySource.Manual },
                new ProviderModelItem { Id = "model", Enabled = false, DiscoverySource = ModelDiscoverySource.Discovered },
                new ProviderModelItem { Id = "model:free", Enabled = true, DiscoverySource = ModelDiscoverySource.Manual },
            ],
        };

        var result = inventory.MergeDiscoveredModels(["model", "model:free", "new"], DateTimeOffset.UnixEpoch);

        Assert.Equal(1, result.ModelsAdded);
        Assert.Equal(2, result.ManualModelsPreserved);
        Assert.False(inventory.Models.Single(model => model.Id == "manual-disabled").Enabled);
        Assert.False(inventory.Models.Single(model => model.Id == "model").Enabled);
        Assert.Equal(ModelDiscoverySource.Mixed, inventory.Models.Single(model => model.Id == "model:free").DiscoverySource);
        Assert.Equal(ModelAvailability.NotReported, inventory.Models.Single(model => model.Id == "manual-disabled").Availability);
        Assert.Equal(["manual-disabled", "model", "model:free", "new"], inventory.GetDeterministicSnapshot().Select(model => model.Id));
    }

    [Fact]
    public void InventoryMergeDoesNotChangeStateWhenDiscoveryIsUnavailable()
    {
        var inventory = new ProviderModelInventory { Models = [new ProviderModelItem { Id = "manual", DiscoverySource = ModelDiscoverySource.Manual }] };
        var result = inventory.MergeDiscoveredModels(null);
        Assert.Equal(0, result.ReportedCount);
        Assert.Equal(ModelAvailability.Unknown, inventory.Models[0].Availability);
        Assert.Null(inventory.LastDiscoveryAt);
    }

    private static ProviderDescriptor ValidProvider(string id) => new()
    {
        Id = id,
        DisplayName = id,
        Match = new ProviderMatchCriteria { ExactHosts = ["example.test"] },
        TrustedHosts = ["example.test"],
        Routes = [new ProviderRoute { Id = "primary", BaseUrl = "https://example.test/v1" }],
    };
}
