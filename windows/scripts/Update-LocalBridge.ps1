param(
    [string]$NuGetCache = 'D:\Evolution\Caches\NuGet',
    [string]$WaitForTaskLog,
    [string]$WaitForTurnId
)

$ErrorActionPreference = 'Stop'
$bridgeRepo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$bridgeProject = Join-Path $bridgeRepo 'windows\DeepOriBridge\DeepOriBridge.csproj'
$bridgeAppx = Join-Path $bridgeRepo 'windows\DeepOriBridge\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\AppX'
$bridgeExe = Join-Path $bridgeAppx 'DeepOriBridge.exe'
$bridgePackage = @(Get-AppxPackage -Name 'E1E677B4-5C45-4603-818D-FA1592843ABC')
if (!(Test-Path -LiteralPath $bridgeProject) -or $bridgePackage.Count -ne 1 -or
    [IO.Path]::GetFullPath($bridgePackage[0].InstallLocation) -ne $bridgeAppx) {
    throw 'The installed Bridge must point to this exact checkout before a local update.'
}

if ($WaitForTaskLog -or $WaitForTurnId) {
    if (!$WaitForTaskLog -or !$WaitForTurnId) { throw 'Both Task log and turn ID are required for a safe deferred update.' }
    & (Join-Path $PSScriptRoot 'Wait-TaskIdle.ps1') -RolloutPath $WaitForTaskLog -TurnId $WaitForTurnId
    # Verify the deployment target again after waiting; do not update a relocated package.
    $bridgePackage = @(Get-AppxPackage -Name 'E1E677B4-5C45-4603-818D-FA1592843ABC')
    if ($bridgePackage.Count -ne 1 -or [IO.Path]::GetFullPath($bridgePackage[0].InstallLocation) -ne $bridgeAppx) {
        throw 'Package location changed while waiting; update canceled.'
    }
}

$bridgeBuildError = $null
try {
    $bridgeInstances = @(Get-Process -Name DeepOriBridge -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $bridgeExe })
    if ($bridgeInstances.Count -gt 1) { throw 'Multiple Bridge instances; update was not started.' }
    if ($bridgeInstances.Count -eq 1) {
        $bridgeProcess = $bridgeInstances[0]
        if (!$bridgeProcess.CloseMainWindow() -or !$bridgeProcess.WaitForExit(15000)) {
            throw 'Bridge has not shut down gracefully; no files were replaced.'
        }
    }
    $previousNuGetCache = $env:NUGET_PACKAGES
    try {
        $env:NUGET_PACKAGES = $NuGetCache
        & dotnet build $bridgeProject -c Release -p:Platform=x64 --nologo
        if ($LASTEXITCODE -ne 0) { throw "Bridge build failed (exit $LASTEXITCODE)." }
    }
    finally { $env:NUGET_PACKAGES = $previousNuGetCache }
}
catch { $bridgeBuildError = $_ }
finally {
    # Keep restart in the SAME PowerShell invocation as shutdown/build. A tool
    # response being interrupted must not leave restart in an unissued next call.
    $bridgeLive = @(Get-Process -Name DeepOriBridge -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $bridgeExe })
    if ($bridgeLive.Count -eq 0) {
        Start-Process -FilePath explorer.exe -ArgumentList 'shell:AppsFolder\E1E677B4-5C45-4603-818D-FA1592843ABC_1z32rh13vfry6!App' -WindowStyle Hidden
    }
    $bridgeDeadline = [DateTime]::UtcNow.AddSeconds(40)
    do {
        $bridgeLive = @(Get-Process -Name DeepOriBridge -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -eq $bridgeExe })
        if ($bridgeLive.Count -eq 1 -and $bridgeLive[0].MainWindowHandle -ne 0 -and
            $bridgeLive[0].MainWindowTitle -eq 'DeepOri Bridge for Windows' -and $bridgeLive[0].Responding) {
            Write-Output "Bridge window restored (PID $($bridgeLive[0].Id))."
            break
        }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $bridgeDeadline)
    if ($bridgeLive.Count -ne 1 -or $bridgeLive[0].MainWindowHandle -eq 0 -or
        $bridgeLive[0].MainWindowTitle -ne 'DeepOri Bridge for Windows' -or !$bridgeLive[0].Responding) {
        throw 'Bridge restart did not produce a responsive window; update is NOT complete.'
    }
}
if ($null -ne $bridgeBuildError) { throw $bridgeBuildError }
