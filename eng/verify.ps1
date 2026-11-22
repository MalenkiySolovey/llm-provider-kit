$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot 'Llm.ProviderKit.slnx'
$feed = Join-Path $repoRoot 'artifacts/packages'
$consumer = Join-Path $repoRoot 'examples/Llm.ProviderKit.PackageConsumer/Llm.ProviderKit.PackageConsumer.csproj'
$package = Join-Path $feed 'Llm.ProviderKit.0.1.0-preview.6.nupkg'

Push-Location $repoRoot
try {
    dotnet restore $solution
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }

    dotnet build $solution -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }

    dotnet test 'tests/Llm.ProviderKit.Tests/Llm.ProviderKit.Tests.csproj' -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }

    & (Join-Path $PSScriptRoot 'verify-cli-json.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'JSON CLI smoke failed.' }

    dotnet format whitespace $solution --verify-no-changes --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Whitespace format verification failed.' }

    dotnet format analyzers $solution --verify-no-changes --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Analyzer format verification failed.' }

    dotnet list $solution package --vulnerable --include-transitive
    if ($LASTEXITCODE -ne 0) { throw 'Dependency audit failed.' }

    New-Item -ItemType Directory -Path $feed -Force | Out-Null
    dotnet pack 'src/Llm.ProviderKit/Llm.ProviderKit.csproj' -c Release --no-build -o $feed
    if ($LASTEXITCODE -ne 0) { throw 'Package creation failed.' }
    if (-not (Test-Path -LiteralPath $package)) { throw 'Expected NuGet package was not created.' }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($package)
    try {
        $nuspecEntry = $archive.Entries | Where-Object { $_.FullName -like '*.nuspec' } | Select-Object -First 1
        if ($null -eq $nuspecEntry) { throw 'Package does not contain a nuspec.' }
        $reader = [IO.StreamReader]::new($nuspecEntry.Open())
        try { [xml]$nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ($nuspec.package.metadata.id -ne 'Llm.ProviderKit' -or $nuspec.package.metadata.version -ne '0.1.0-preview.6') {
            throw 'Package identity or version did not match the expected pin.'
        }
    }
    finally {
        $archive.Dispose()
    }

    $globalPackageCache = Join-Path $env:USERPROFILE '.nuget/packages/llm.providerkit/0.1.0-preview.6'
    if (Test-Path -LiteralPath $globalPackageCache) { Remove-Item -LiteralPath $globalPackageCache -Recurse -Force }
    dotnet restore $consumer --configfile 'eng/NuGet.PackageSmoke.config' --force-evaluate
    if ($LASTEXITCODE -ne 0) { throw 'Package-only consumer restore failed.' }
    dotnet build $consumer -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Package-only consumer build failed.' }
    dotnet run --project $consumer -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Package-only consumer smoke failed.' }

    git diff --check
    if ($LASTEXITCODE -ne 0) { throw 'Git whitespace validation failed.' }
    git diff --cached --check
    if ($LASTEXITCODE -ne 0) { throw 'Staged Git whitespace validation failed.' }
}
finally {
    Pop-Location
}
