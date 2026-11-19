using Llm.ProviderKit.Catalog;

namespace Llm.ProviderKit.Inspection;

/// <summary>Supplies an explicitly documented, read-only billing recipe for an otherwise unknown provider.</summary>
public interface IGenericProviderBillingStrategy
{
    string Id { get; }
    int Order { get; }
    ProviderCapabilityRecipe? CreateRecipe(Uri providerOrigin);
}
