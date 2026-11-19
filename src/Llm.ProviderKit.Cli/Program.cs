using System.Text.Json;
using System.Text.Json.Serialization;
using Llm.ProviderKit.Catalog;
using Llm.ProviderKit.Models;

var jsonOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = false,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
};

object output;
var exitCode = 0;
try
{
    if (!args.SequenceEqual(["--json"], StringComparer.Ordinal))
        throw new CliInputException("Run this command with --json and provide one versioned JSON object on stdin.");
    var input = await Console.In.ReadToEndAsync();
    if (System.Text.Encoding.UTF8.GetByteCount(input) > ProviderCatalogValidator.MaxCatalogSizeBytes)
        throw new CliInputException("Input exceeds the supported size limit.");
    using var document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 64 });
    var root = document.RootElement;
    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version) || version.GetInt32() != 1)
        throw new CliInputException("Request version must be 1.");
    if (!root.TryGetProperty("operation", out var operationElement) || operationElement.GetString() is not { } operation)
        throw new CliInputException("Request operation is required.");

    switch (operation)
    {
        case "catalog.validate":
            if (!root.TryGetProperty("catalog", out var catalogElement)) throw new CliInputException("Catalog payload is required.");
            var catalogResult = ProviderCatalogValidator.ParseAndValidate(System.Text.Encoding.UTF8.GetBytes(catalogElement.GetRawText()));
            if (!catalogResult.IsValid || catalogResult.Catalog is null)
            {
                exitCode = 1;
                output = new { version = 1, ok = false, operation, error = catalogResult.Error ?? "Catalog is invalid." };
            }
            else
            {
                output = new { version = 1, ok = true, operation, providerCount = catalogResult.Catalog.Providers.Count };
            }
            break;

        case "inventory.merge":
            if (!root.TryGetProperty("inventory", out var inventoryElement)) throw new CliInputException("Inventory payload is required.");
            var inventory = JsonSerializer.Deserialize<ProviderModelInventory>(inventoryElement.GetRawText(), jsonOptions)
                ?? throw new CliInputException("Inventory payload is invalid.");
            var reported = root.TryGetProperty("reportedIds", out var reportedElement)
                ? JsonSerializer.Deserialize<List<string>>(reportedElement.GetRawText(), jsonOptions) ?? []
                : [];
            var time = root.TryGetProperty("observedAt", out var observedElement) && observedElement.TryGetDateTimeOffset(out var parsed)
                ? parsed
                : DateTimeOffset.UnixEpoch;
            var merge = inventory.MergeDiscoveredModels(reported, time);
            output = new
            {
                version = 1,
                ok = true,
                operation,
                merge,
                inventory = new
                {
                    inventory.SelectedModelId,
                    inventory.DiscoveryStatus,
                    inventory.LastDiscoveryAt,
                    models = inventory.GetDeterministicSnapshot(),
                },
            };
            break;

        default:
            throw new CliInputException("Unsupported operation.");
    }
}
catch (Exception exception) when (exception is CliInputException or JsonException or InvalidOperationException or FormatException)
{
    exitCode = 1;
    output = new { version = 1, ok = false, error = exception is CliInputException ? exception.Message : "Request JSON is invalid." };
}

Console.Out.WriteLine(JsonSerializer.Serialize(output, jsonOptions));
return exitCode;

internal sealed class CliInputException(string message) : Exception(message);
