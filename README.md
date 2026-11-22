# LLM Provider Kit

**English** | [Русский](./README_RU.md)

`Llm.ProviderKit` is a standalone .NET library for provider catalog validation, exact endpoint matching, safe read-only HTTP inspection, model inventory merging, and explicit provider/model facts. It has no Codex, WinUI, or Control Center dependency and does not own provider credentials.

## Capabilities

- Parse and validate versioned provider catalogs while preserving unknown extension fields.
- Match exact IDN hosts and exact base URLs; never use suffix or substring matching.
- Plan only GET/HEAD requests, enforce HTTPS (except loopback test endpoints), exact trusted-host allowlists, and reject credential-bearing static headers.
- Inspect models and declarative billing/usage facts over bounded responses with timeouts and trusted redirects.
- Normalize typed balance, spending, credit, request, and token metrics while retaining scope, units, period semantics, provenance, and freshness.
- Provide credentials through an operation-scoped `IProviderCredentialSource`; the transport never persists them or includes them in errors.
- Merge exact model IDs without collapsing `:free` variants, preserve manual/disabled settings, and mark stale availability without deleting entries.
- Keep unknown context limits and capabilities unknown; retain explicit evidence and consumer-owned model overrides.
- Offer a deterministic, versioned JSON CLI over stdin/stdout for catalog validation and inventory merge.

The kit only performs read-only inspection. It does not send inference/canary requests and does not infer model context limits or capabilities from model names.

## Install and API example

The package is built locally and is not published to NuGet:

```powershell
dotnet pack .\src\Llm.ProviderKit\Llm.ProviderKit.csproj -c Release -o .\artifacts\packages
```

```csharp
using Llm.ProviderKit.Catalog;
using Llm.ProviderKit.Models;

var inventory = new ProviderModelInventory();
inventory.MergeDiscoveredModels(["model-x", "model-x:free"]);
var exactIds = inventory.GetDeterministicSnapshot().Select(model => model.Id);
```

`ProviderInspectionService` accepts an injected `IProviderCredentialSource`. Filesystem storage, signatures, vaults, routing, and application lifecycle remain host responsibilities.

### New API token usage

A trusted descriptor can opt into `new-api-token-usage` with an optional `keyUsage` recipe alongside its existing usage recipe. Set one bearer-authenticated GET request with a root-relative `path` or an exact HTTPS `url`. The planner checks that the target shares the active origin and is trusted. The transport rejects redirects for this strategy.

The snapshot reports finite grant, remaining quota and usage in raw `quota-units` scoped to the API key. An unlimited key has a live `QuotaLimit` metric with `IsUnlimited = true` and a null value. Key expiry, when supplied, appears in `ExpiresAt`. Read account balance from a separate, explicitly mapped account observation. [New API token usage](https://docs.newapi.pro/en/docs/api/management/token-management/usage-token-get)

## JSON command

Build `Llm.ProviderKit.Cli`, then send a versioned request to `llm-provider --json` on stdin:

```json
{"version":1,"operation":"inventory.merge","inventory":{"models":[]},"reportedIds":["model-x:free","model-x"]}
```

The command returns a versioned structured result and never echoes input values. Supported operations are `catalog.validate` and `inventory.merge`.

## Validation

```powershell
eng\verify.ps1
```

The gates restore, build, test, check format and dependencies, pack the library, and run a synthetic external consumer that references only the generated package.

## Provenance and license

Extracted and normalized from [Codex Control Center](https://github.com/MalenkiySolovey/codex-control-center) source commit `fd590546dc52800e27f420ea5de73a2b2a5e50d0`. Product-specific Codex catalog projection, signed catalog distribution, credential storage, routing, and runtime switching remain in Control Center. See [THIRD_PARTY_NOTICES.md](./THIRD_PARTY_NOTICES.md). The original MIT copyright notice is preserved.
