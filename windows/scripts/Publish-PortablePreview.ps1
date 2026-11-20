param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$AllowDirtyLocalBuild
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Output must be a new directory; no existing build is overwritten.' }
$source = (& git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve source commit.' }
$dirty = @(& git -C $repo status --porcelain).Count -ne 0
if ($dirty -and !$AllowDirtyLocalBuild) { throw 'Release staging requires a clean source checkout.' }
$null = New-Item -ItemType Directory -Path $output
$payload = Join-Path $output 'DeepOriBridge-Windows-x64-preview'
$build = Join-Path $output 'build'
& dotnet publish (Join-Path $repo 'windows\DeepOriBridge\DeepOriBridge.csproj') -c Release -r win-x64 `
    -p:Platform=x64 -p:BridgePortablePreview=true -p:BridgeDeveloperBuild=false `
    -p:BridgeLocalPreviewSensitiveRuntime=false -p:ContinuousIntegrationBuild=true `
    "-p:BaseOutputPath=$build\bin\" "-p:BaseIntermediateOutputPath=$build\obj\" -o $payload
if ($LASTEXITCODE -ne 0) { throw 'Portable publish failed; no ZIP created.' }
Copy-Item -LiteralPath (Join-Path $repo 'docs\windows\PORTABLE_INSTALL.md') -Destination (Join-Path $payload 'INSTALL.md')
# Preserve notices shipped by the restored dependencies, including runtime packs.
$notices = Join-Path $payload 'ThirdPartyNotices'
$null = New-Item -ItemType Directory -Path $notices
$assets = Get-Content -LiteralPath (Join-Path $build 'obj\project.assets.json') -Raw | ConvertFrom-Json
function Copy-PackageNotices([string]$PackageRoot, [string]$Label) {
    if (!(Test-Path -LiteralPath $PackageRoot -PathType Container)) { return }
    $safeLabel = $Label -replace '[^a-zA-Z0-9_.-]', '_'
    $matches = @(Get-ChildItem -LiteralPath $PackageRoot -File | Where-Object { $_.Name -match '(?i)(license|notice|copying)' })
    if ($matches.Count) {
        $destination = Join-Path $notices $safeLabel
        $null = New-Item -ItemType Directory -Path $destination -Force
        foreach ($notice in $matches) { Copy-Item -LiteralPath $notice.FullName -Destination $destination }
    }
}
foreach ($library in $assets.libraries.PSObject.Properties) {
    if ($library.Value.type -ne 'package') { continue }
    foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
        Copy-PackageNotices (Join-Path $folder $library.Value.path) $library.Name
    }
}
$runtime = Get-Content -LiteralPath (Join-Path $payload 'DeepOriBridge.runtimeconfig.json') -Raw | ConvertFrom-Json
foreach ($framework in $runtime.runtimeOptions.includedFrameworks) {
    foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
        $runtimePackage = $framework.name.ToLowerInvariant() + '.runtime.win-x64/' + $framework.version
        Copy-PackageNotices (Join-Path $folder $runtimePackage) $runtimePackage
    }
}
Push-Location (Join-Path $repo 'windows\FeishuEventSidecar')
try {
    # Enumerate only dependencies actually linked into the shipped sidecar.
    # `go list -m all` also fetches unrelated transitive test/tool modules.
    $modules = @(& go list -deps -f '{{if .Module}}{{.Module.Path}}|{{.Module.Version}}|{{.Module.Dir}}{{end}}' . | Sort-Object -Unique)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot collect Go dependency notices.' }
    foreach ($module in $modules) {
        $fields = $module.Split('|')
        if ($fields.Length -eq 3 -and $fields[2]) { Copy-PackageNotices $fields[2] ($fields[0] + '-' + $fields[1]) }
    }
} finally { Pop-Location }
Copy-PackageNotices ((& go env GOROOT).Trim()) 'Go-runtime'
Copy-PackageNotices $repo 'DeepOriBridge'
if ((& git -C $repo rev-parse HEAD).Trim() -ne $source -or
    (!$AllowDirtyLocalBuild -and @(& git -C $repo status --porcelain).Count -ne 0)) {
    throw 'Source changed during publish; no release archive created.'
}
& (Join-Path $PSScriptRoot 'New-PortableManifest.ps1') -Directory $payload -SourceCommit $source -SourceDirty:$dirty
$zip = Join-Path $output 'DeepOriBridge-Windows-x64-unsigned-preview.zip'
Compress-Archive -LiteralPath $payload -DestinationPath $zip
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), "$hash  $([IO.Path]::GetFileName($zip))`n", [Text.UTF8Encoding]::new($false))
Write-Output 'Local candidate only. This script never uploads or publishes a release.'
if ($dirty) { Write-Warning 'Dirty source: LOCAL TEST ONLY. Do not upload this ZIP.' }
Write-Output $zip
