param(
    [Parameter(Mandatory)][string]$Directory,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{40}$')][string]$SourceCommit,
    [switch]$SourceDirty
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $Directory).Path.TrimEnd('\')
$target = Join-Path $root 'portable-manifest.json'
if (Test-Path -LiteralPath $target) { throw 'Refusing to replace an existing manifest. Publish to a fresh directory.' }
$files = [ordered]@{}
function Get-PayloadFiles([string]$Folder) {
    foreach ($item in Get-ChildItem -LiteralPath $Folder -Force) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse points are forbidden.' }
        if ($item.PSIsContainer) { Get-PayloadFiles $item.FullName } else { $item }
    }
}
$ancestor = Get-Item -LiteralPath $root
while ($ancestor) {
    if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unsafe publish root.' }
    $ancestor = $ancestor.Parent
}
foreach ($file in Get-PayloadFiles $root | Sort-Object FullName) {
    $relative = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
    $files[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
}
foreach ($required in @('DeepOriBridge.exe','DeepOriBridge.dll','Tools/feishu-event-sidecar.exe','coreclr.dll','Microsoft.WinUI.dll','DeepOriBridge.pri','Assets/AppIcon.ico','Assets/Square44x44Logo.scale-200.png')) {
    if (!$files.Contains($required)) { throw "Missing self-contained payload: $required" }
}
if ($files.Count -gt 4096) { throw 'Unexpected payload size.' }
$manifest = [ordered]@{Version=1;Channel='unsigned-portable-preview-v1';SourceCommit=$SourceCommit;SourceDirty=[bool]$SourceDirty;Files=$files}
$bytes = [Text.UTF8Encoding]::new($false).GetBytes(($manifest | ConvertTo-Json -Depth 5))
$stream = [IO.File]::Open($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
Write-Output "Manifest created for $($files.Count) files. Unsigned; not publisher authentication."
