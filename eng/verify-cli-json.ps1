$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src/Llm.ProviderKit.Cli/Llm.ProviderKit.Cli.csproj'
$request = '{"version":1,"operation":"inventory.merge","inventory":{"models":[{"id":"manual-disabled","enabled":false,"discoverySource":"manual"}]},"reportedIds":["model:free","model"],"observedAt":"1970-01-01T00:00:00+00:00"}'
$responseText = $request | dotnet run --project $project -c Release --no-build -- --json
if ($LASTEXITCODE -ne 0) { throw 'JSON CLI returned a failure exit code.' }
$response = $responseText | ConvertFrom-Json
if ($response.version -ne 1 -or -not $response.ok -or $response.operation -ne 'inventory.merge') {
    throw 'JSON CLI did not return a versioned inventory result.'
}
if ($response.inventory.models.Count -ne 3 -or $response.inventory.models[0].id -ne 'manual-disabled' -or $response.inventory.models[0].enabled) {
    throw 'JSON CLI did not preserve the disabled manual model and exact IDs.'
}
if (@($response.inventory.models | Where-Object { $_.id -eq 'model:free' }).Count -ne 1 -or @($response.inventory.models | Where-Object { $_.id -eq 'model' }).Count -ne 1) {
    throw 'JSON CLI conflated exact model identifiers.'
}
Write-Output 'JSON CLI deterministic inventory smoke: PASS'
