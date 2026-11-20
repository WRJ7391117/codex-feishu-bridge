# Run explicitly in an administrator PowerShell. This preserves Bluetooth pairing
# and restarts only the uniquely identified PromLight HID-over-GATT service.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$lampPrincipal = [Security.Principal.WindowsPrincipal]::new(
    [Security.Principal.WindowsIdentity]::GetCurrent())
if (!$lampPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Administrator PowerShell is required. No device was changed.'
}

$lampServices = @(Get-PnpDevice -PresentOnly | Where-Object {
    $_.Class -eq 'HIDClass' -and $_.InstanceId -match
        '^BTHLEDEVICE\\\{00001812-0000-1000-8000-00805F9B34FB\}_DEV_VID&0107D7_PID&6B01_REV&[0-9A-F]{4}_[0-9A-F]{12}\\'
})
if ($lampServices.Count -ne 1) {
    throw "Expected exactly one present PromLight Bluetooth HID service; found $($lampServices.Count). No device was changed."
}

$lampTarget = $lampServices[0].InstanceId
$lampAddress = [regex]::Match($lampTarget, '_([0-9A-F]{12})\\', 'IgnoreCase').Groups[1].Value
if ($lampAddress.Length -ne 12) { throw 'Could not verify the lamp identity. No device was changed.' }
Write-Output "Restarting only PromLight Bluetooth HID service: $lampTarget"
& "$env:SystemRoot\System32\pnputil.exe" /restart-device $lampTarget
if ($LASTEXITCODE -ne 0) { throw "Device restart failed (exit $LASTEXITCODE). Pairing was not removed." }

# PnPUtil can print an error while returning zero. Verify the HID child itself;
# the continued presence of the Bluetooth parent does NOT prove recovery.
$lampDeadline = [DateTime]::UtcNow.AddSeconds(20)
do {
    $lampInterfaces = @(Get-PnpDevice -PresentOnly | Where-Object {
        $_.Class -eq 'HIDClass' -and $_.InstanceId -like 'HID\*' -and
        $_.InstanceId -match 'VID&0107D7_PID&6B01_' -and
        $_.InstanceId -match ([regex]::Escape($lampAddress) + '\\') -and $_.Status -eq 'OK'
    })
    if ($lampInterfaces.Count -gt 0) {
        Write-Output 'PromLight HID interface is present again. Pairing was preserved.'
        Write-Output 'Next verify Bridge reports a successful write and check the physical lamp; interface recovery alone is not a lighting test.'
        return
    }
    Start-Sleep -Milliseconds 500
} while ([DateTime]::UtcNow -lt $lampDeadline)
throw 'The PromLight HID interface did not return. Repair is NOT verified. No pairing was removed and no Bluetooth adapter was restarted.'
