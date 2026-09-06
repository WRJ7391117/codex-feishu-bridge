param(
    [Parameter(Mandatory)][int]$BridgeProcessId,
    [Parameter(Mandatory)][long]$StartTicks,
    [Parameter(Mandatory)][string]$StateDirectory,
    [Parameter(Mandatory)][string]$PackageFamily
)
$ErrorActionPreference = 'Stop'
if ($PackageFamily -notmatch '^[A-Za-z0-9_.-]+$') { throw 'Invalid package identity.' }
$expectedExe = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\DeepOriBridge.exe'))
$expectedState = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA "Packages\$PackageFamily\LocalState\CrashRecovery"))
if ([IO.Path]::GetFullPath($StateDirectory) -ne $expectedState) { throw 'Recovery storage identity mismatch.' }
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
Start-Process -FilePath explorer.exe -ArgumentList "shell:AppsFolder\$PackageFamily!App" -WindowStyle Hidden
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
