param([Parameter(Mandatory)][int]$BridgeProcessId)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public class BridgeUiVerificationNative {
 [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h,int x,int y,int width,int height,bool repaint);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h,IntPtr dc,uint flags);
}
"@
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$expectedExe = Join-Path $repo 'windows\DeepOriBridge\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\AppX\DeepOriBridge.exe'
$process = Get-Process -Id $BridgeProcessId
if ($process.Path -ne $expectedExe -or $process.MainWindowTitle -ne 'DeepOri Bridge for Windows') {
    throw 'Not the exact packaged candidate window.'
}
$window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
function Find-Element([string]$property,[string]$value) {
    $key = if($property -eq 'Id') { [System.Windows.Automation.AutomationElement]::AutomationIdProperty } else { [System.Windows.Automation.AutomationElement]::NameProperty }
    $condition=New-Object System.Windows.Automation.PropertyCondition($key,$value)
    $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
}
function Click-Name([string]$name) {
    $element=Find-Element 'Name' $name
    if (!$element -or !$element.Current.IsEnabled) { throw "Unavailable navigation: $name" }
    $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 800
}
function Check-Page([string]$page) {
    if (!(Find-Element 'Id' "DeepOriBridge.$page.Page")) { throw "Wrong page: $page" }
    $buttons = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Button)))
    $outer=$window.Current.BoundingRectangle
    foreach($button in $buttons) {
        if (!$button.Current.IsOffscreen) {
            $r=$button.Current.BoundingRectangle
            if ($r.Left -lt $outer.Left -or $r.Right -gt $outer.Right) { throw "Visible button exceeds window on $page" }
        }
    }
    Write-Output "PASS: $page page, visible buttons within window ($($outer.Width)px)."
}
# Navigation only. Do not press credential, pairing, setup authorization, or connection mutations.
foreach($width in @(2200,1600,900,700)) {
    $null=[BridgeUiVerificationNative]::MoveWindow($process.MainWindowHandle,30,30,$width,1200,$true)
    Start-Sleep -Milliseconds 400
    Check-Page 'Home'
    Click-Name '打开桥接配置'
    Check-Page 'Configuration'
    Click-Name '返回首页'
    Click-Name '打开运行诊断'
    Check-Page 'Diagnostics'
    Click-Name '返回首页'
    $expander=Find-Element 'Id' 'GettingStartedExpander'
    $expander.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Click-Name '进入首次连接向导'
    Check-Page 'ConnectionSetup'
    Click-Name '返回首页'
}
$null=[BridgeUiVerificationNative]::MoveWindow($process.MainWindowHandle,30,30,1600,1150,$true)
Start-Sleep -Milliseconds 400
$scroll=(Find-Element 'Id' 'RootScrollViewer').GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
$scroll.SetScrollPercent(-1,55)
Write-Output 'PASS: 16 page/width checks and return navigation. App left running. No credential or remote mutation controls invoked.'
