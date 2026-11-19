using System.Text.Json;
using System.Text.Json.Serialization;

namespace Llm.ProviderKit.Catalog;

public enum CapabilityStatus
{
    Unknown = 0,
    Supported = 1,
    Unsupported = 2,
}

/// <summary>A versioned catalog of public provider facts. It never stores credentials.</summary>
public sealed class ProviderCatalog
{
    [JsonPropertyName("$schema")]
    public string? Schema { get; set; }

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("catalogVersion")]
    public int CatalogVersion { get; set; } = 1;

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("minAppVersion")]
    public string? MinAppVersion { get; set; }

    [JsonPropertyName("providers")]
    public List<ProviderDescriptor> Providers { get; set; } = [];
}

/// <summary>Generic provider definition; unknown JSON properties are preserved losslessly.</summary>
public sealed class ProviderDescriptor
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("aliases")]
    public List<string>? Aliases { get; set; }

    [JsonPropertyName("match")]
    public ProviderMatchCriteria Match { get; set; } = new();

    [JsonPropertyName("routes")]
    public List<ProviderRoute> Routes { get; set; } = [];

    [JsonPropertyName("trustedHosts")]
    public List<string> TrustedHosts { get; set; } = [];

    [JsonPropertyName("capabilities")]
    public ProviderCapabilities Capabilities { get; set; } = new();

    [JsonPropertyName("evidence")]
    public List<ProviderEvidence>? Evidence { get; set; }

    [JsonPropertyName("metadata")]
    public ProviderMetadata? Metadata { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class ProviderMatchCriteria
{
    [JsonPropertyName("exactHosts")]
    public List<string> ExactHosts { get; set; } = [];

    [JsonPropertyName("exactBaseUrls")]
    public List<string>? ExactBaseUrls { get; set; }
}

public sealed class ProviderRoute
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("region")]
    public string? Region { get; set; }

    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = string.Empty;

    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; set; }

    [JsonPropertyName("isReserve")]
    public bool IsReserve { get; set; }
}

public sealed class ProviderCapabilities
{
    [JsonPropertyName("models")]
    public ProviderCapabilityRecipe Models { get; set; } = new();

    [JsonPropertyName("balance")]
    public ProviderCapabilityRecipe Balance { get; set; } = new();

    [JsonPropertyName("usage")]
    public ProviderCapabilityRecipe Usage { get; set; } = new();
}

public sealed class ProviderCapabilityRecipe
{
    [JsonPropertyName("status")]
    [JsonConverter(typeof(JsonStringEnumConverter<CapabilityStatus>))]
    public CapabilityStatus Status { get; set; } = CapabilityStatus.Unknown;

    [JsonPropertyName("strategy")]
    public string Strategy { get; set; } = "unknown";

    [JsonPropertyName("request")]
    public RecipeRequest? Request { get; set; }

    [JsonPropertyName("requests")]
    public List<RecipeRequest>? Requests { get; set; }

    [JsonPropertyName("response")]
    public RecipeResponseMapping? Response { get; set; }

    [JsonPropertyName("outputs")]
    public RecipeResponseMapping? Outputs { get; set; }
}

public sealed class RecipeRequest
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("method")]
    public string Method { get; set; } = "GET";

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("auth")]
    public string Auth { get; set; } = "none";

    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; set; }
}

public sealed class RecipeResponseMapping
{
    [JsonPropertyName("balance")]
    public JsonFieldMapping? Balance { get; set; }

    [JsonPropertyName("used")]
    public JsonFieldMapping? Used { get; set; }

    [JsonPropertyName("limit")]
    public JsonFieldMapping? Limit { get; set; }

    [JsonPropertyName("remaining")]
    public JsonFieldMapping? Remaining { get; set; }

    [JsonPropertyName("currency")]
    public CurrencyMapping? Currency { get; set; }

    [JsonPropertyName("models")]
    public JsonFieldMapping? Models { get; set; }
}

public sealed class JsonFieldMapping
{
    [JsonPropertyName("pointer")]
    public string JsonPath { get; set; } = string.Empty;

    [JsonPropertyName("requestId")]
    public string? RequestId { get; set; }

    [JsonPropertyName("operation")]
    public string? Operation { get; set; }

    [JsonPropertyName("sources")]
    public List<string>? Sources { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "decimal";

    [JsonPropertyName("transform")]
    public string? Transform { get; set; }

    [JsonPropertyName("scaleFactor")]
    public decimal? ScaleFactor { get; set; }
}

public sealed class CurrencyMapping
{
    [JsonPropertyName("literal")]
    public string? Literal { get; set; }

    [JsonPropertyName("pointer")]
    public string? JsonPath { get; set; }

    [JsonPropertyName("requestId")]
    public string? RequestId { get; set; }
}

public sealed class ProviderEvidence
{
    [JsonPropertyName("capability")]
    public string Capability { get; set; } = string.Empty;

    [JsonPropertyName("sourceType")]
    public string SourceType { get; set; } = "official-doc";

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("verifiedAt")]
    public DateTimeOffset VerifiedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("note")]
    public string? Note { get; set; }
}

public sealed class ProviderMetadata
{
    [JsonPropertyName("verifiedAt")]
    public DateTimeOffset? VerifiedAt { get; set; }

    [JsonPropertyName("reviewAfter")]
    public DateTimeOffset? ReviewAfter { get; set; }

    [JsonPropertyName("deprecated")]
    public bool Deprecated { get; set; }
}
