param([string]$SkillPath = (Join-Path $PSScriptRoot '..\DeepOriBridge\Assets\CodexSkills\deepori-bridge-setup\SKILL.md'))
$ErrorActionPreference = 'Stop'
# Offline validation only: do not contact a real setup pipe or Feishu application.
$text = Get-Content -LiteralPath $SkillPath -Raw -Encoding UTF8
$blocks = [regex]::Matches($text, '(?s)```powershell\r?\n(.*?)```')
if ($blocks.Count -ne 3) { throw 'Expected exactly three setup examples.' }
foreach ($block in $blocks) {
    $parseTokens = $null
    $parseErrors = $null
    $null = [Management.Automation.Language.Parser]::ParseInput($block.Groups[1].Value, [ref]$parseTokens, [ref]$parseErrors)
    if ($parseErrors.Count) { throw 'PowerShell setup example has syntax errors.' }
}
if ($text.Contains('[Convert]::ToHexString') -or $text.Contains('RandomNumberGenerator]::GetBytes(')) {
    throw 'Example requires APIs unavailable in Windows PowerShell 5.1.'
}
$payload = [Text.StringBuilder]::new('abc')
$sha = [Security.Cryptography.SHA256]::Create()
$hashSnippet = [regex]::Match($blocks[1].Groups[1].Value, '(?s)try \{ \$actualPayload = .*?finally \{ \$sha.Dispose\(\) \}').Value
if (!$hashSnippet) { throw 'No manifest digest example found.' }
. ([scriptblock]::Create($hashSnippet))
if ($actualPayload -cne 'BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD') {
    throw 'Example SHA-256 result is incorrect.'
}
$requestExpression = [regex]::Match($blocks[2].Groups[1].Value, '(?m)^\s*RequestId = (.+)$').Groups[1].Value
$requestId = . ([scriptblock]::Create($requestExpression))
if ($requestId -notmatch '^[a-fA-F0-9]{32}$') { throw 'Invalid command request ID.' }
Write-Output "PASS: 3 example syntax checks, SHA-256 known vector, command request ID; PowerShell $($PSVersionTable.PSVersion). No live operations."
