<#
.SYNOPSIS
    Regenerates schema/v1/*.schema.json from the extractors' declared keys.

.DESCRIPTION
    The JSON Schema is GENERATED from each extractor's CreateKeys / DerivedKeys plus their type
    annotations, committed to the repo, and checked for staleness by the test suite and by CI.
    Hand-editing schema/v1/*.json is not a thing; editing an extractor is.

    Run this after changing anything an extractor declares, and commit the result together with a
    CHANGELOG entry under "### Schema" — a schema diff is a public interface change.

    Needs no Tekla installation: the generator loads the Open API assemblies from NuGet.

.PARAMETER Check
    Do not write. Regenerate into a temporary folder and compare against the committed files,
    exiting 1 if they differ. This is what CI runs.

.EXAMPLE
    ./scripts/Generate-Schema.ps1

.EXAMPLE
    ./scripts/Generate-Schema.ps1 -Check
#>
[CmdletBinding()]
param(
    [switch] $Check,
    [string] $Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'source/TeklaDump.SchemaGen/TeklaDump.SchemaGen.csproj'
$committed = Join-Path $repoRoot 'schema/v1'

$target = if ($Check) {
    Join-Path ([System.IO.Path]::GetTempPath()) ("tekladump-schema-" + [guid]::NewGuid().ToString('N'))
} else {
    $committed
}

Write-Host "Generating schema into $target"
& dotnet run --project $project --configuration $Configuration -- $target
if ($LASTEXITCODE -ne 0) { throw "Schema generation failed with exit code $LASTEXITCODE." }

if (-not $Check) {
    Write-Host "Done. Commit schema/v1/ together with a CHANGELOG entry under '### Schema'."
    return
}

$stale = @()
foreach ($name in @('inspect.schema.json', 'bulk.schema.json')) {
    $expectedPath = Join-Path $target $name
    $actualPath = Join-Path $committed $name

    if (-not (Test-Path $actualPath)) {
        $stale += "$name is missing from schema/v1/."
        continue
    }

    # Byte comparison, not text: the generator writes UTF-8 without BOM and "`n" endings, and the
    # committed file has to match that regardless of anyone's git autocrlf setting.
    $expected = [System.IO.File]::ReadAllBytes($expectedPath)
    $actual = [System.IO.File]::ReadAllBytes($actualPath)

    if ($expected.Length -ne $actual.Length -or
        [System.Convert]::ToBase64String($expected) -ne [System.Convert]::ToBase64String($actual)) {
        $stale += "$name is stale."
    }
}

Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue

if ($stale.Count -gt 0) {
    Write-Host ''
    foreach ($message in $stale) { Write-Host "  $message" -ForegroundColor Red }
    Write-Host ''
    Write-Host "An extractor's declared keys changed without the schema being regenerated." -ForegroundColor Red
    Write-Host "Run ./scripts/Generate-Schema.ps1 and commit the result." -ForegroundColor Red
    exit 1
}

Write-Host 'schema/v1 is up to date.'
