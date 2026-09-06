param(
    [Parameter(Mandatory)][int]$BridgeProcessId,
    [Parameter(Mandatory)][long]$StartTicks,
    [Parameter(Mandatory)][string]$StateDirectory,
    [string]$PackageFamily,
    [switch]$Portable,
    [string]$ExpectedExecutableHash
)
$ErrorActionPreference = 'Stop'
if (!$Portable -and $PackageFamily -notmatch '^[A-Za-z0-9_.-]+$') { throw 'Invalid package identity.' }
if ($Portable -and ($PackageFamily -or $ExpectedExecutableHash -notmatch '^[A-F0-9]{64}$')) { throw 'Invalid portable identity.' }
$expectedExe = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\DeepOriBridge.exe'))
$expectedState = if ($Portable) {
    [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'DeepOriBridge\Portable-Unsigned-Isolated-v1\CrashRecovery'))
} else { [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA "Packages\$PackageFamily\LocalState\CrashRecovery")) }
if ([IO.Path]::GetFullPath($StateDirectory) -ne $expectedState) { throw 'Recovery storage identity mismatch.' }
trap {
    if ($expectedState -and [IO.Directory]::Exists($expectedState)) {
        [IO.File]::AppendAllText((Join-Path $expectedState 'recovery.log'),
            "$(Get-Date -Format o) recovery-error: $($_.Exception.GetType().Name) $($_.Exception.Message)`n")
    }
    exit 1
}
$bridgeProcess = Get-Process -Id $BridgeProcessId -ErrorAction SilentlyContinue
if (!$bridgeProcess -or $bridgeProcess.Path -ne $expectedExe -or
    $bridgeProcess.StartTime.ToUniversalTime().Ticks -ne $StartTicks) { exit }
# Keep the original process handle: a recycled PID is never treated as this app.
$null = $bridgeProcess.Handle
$bridgeProcess.WaitForExit()
$marker = Join-Path $expectedState "$BridgeProcessId-$StartTicks.normal"
if (Test-Path -LiteralPath $marker) {
    Remove-Item -LiteralPath $marker
    exit
}
$historyPath = Join-Path $expectedState 'attempts.json'
$attempts = @()
if (Test-Path -LiteralPath $historyPath) {
    $attempts = @(Get-Content -LiteralPath $historyPath -Raw | ConvertFrom-Json | Where-Object {
        [DateTimeOffset]::Parse($_) -gt [DateTimeOffset]::UtcNow.AddMinutes(-5)
    })
}
if ($attempts.Count -ge 3) {
    [IO.File]::AppendAllText((Join-Path $expectedState 'recovery.log'), "$(Get-Date -Format o) restart-limit-reached`n")
    exit
}
$attempts += [DateTimeOffset]::UtcNow.ToString('o')
[IO.File]::WriteAllText($historyPath, (ConvertTo-Json -InputObject @($attempts)))
Start-Sleep -Seconds 2
$existing = @(Get-Process -Name DeepOriBridge -ErrorAction SilentlyContinue | Where-Object Path -eq $expectedExe)
if ($existing.Count -gt 0) { exit }
if ($Portable) {
    # The restarted app independently revalidates the complete payload and consent
    # before constructing services. Never bypass its first-run gate on recovery.
    if ((Get-FileHash -LiteralPath $expectedExe -Algorithm SHA256).Hash -cne $ExpectedExecutableHash) {
        [IO.File]::AppendAllText((Join-Path $expectedState 'recovery.log'), "$(Get-Date -Format o) executable-changed-restart-refused`n")
        exit 1
    }
    $part = Get-Item -LiteralPath $expectedExe
    while ($part) {
        if ($part.Attributes -band [IO.FileAttributes]::ReparsePoint) { exit 1 }
        $part = if ($part -is [IO.FileInfo]) { $part.Directory } else { $part.Parent }
    }
    Start-Process -FilePath $expectedExe -WorkingDirectory (Split-Path -Parent $expectedExe) -WindowStyle Hidden
} else {
    Start-Process -FilePath explorer.exe -ArgumentList "shell:AppsFolder\$PackageFamily!App" -WindowStyle Hidden
}
$deadline = [DateTime]::UtcNow.AddSeconds(40)
do {
    $live = @(Get-Process -Name DeepOriBridge -ErrorAction SilentlyContinue | Where-Object Path -eq $expectedExe)
    if ($live.Count -eq 1 -and $live[0].MainWindowHandle -ne 0 -and $live[0].Responding) {
        [IO.File]::AppendAllText((Join-Path $expectedState 'recovery.log'), "$(Get-Date -Format o) window-restored`n")
        exit
    }
    Start-Sleep -Milliseconds 500
} while ([DateTime]::UtcNow -lt $deadline)
[IO.File]::AppendAllText((Join-Path $expectedState 'recovery.log'), "$(Get-Date -Format o) restart-unverified`n")
exit 1
