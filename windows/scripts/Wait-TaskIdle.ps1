param(
    [Parameter(Mandatory=$true)][string]$RolloutPath,
    [Parameter(Mandatory=$true)][string]$TurnId,
    [ValidateRange(1, 3600)][int]$MaxWaitSeconds = 1800,
    [ValidateRange(1, 30)][int]$QuietSeconds = 10
)

$ErrorActionPreference = 'Stop'
$taskLog = (Resolve-Path -LiteralPath $RolloutPath).Path
if ([IO.Path]::GetExtension($taskLog) -ne '.jsonl' -or $TurnId -notmatch '^[A-Za-z0-9_-]{1,128}$') {
    throw 'Expected an explicit Task JSONL path and exact turn ID; nothing was stopped.'
}
$taskDeadline = [DateTime]::UtcNow.AddSeconds($MaxWaitSeconds)
$taskStream = [IO.FileStream]::new($taskLog, [IO.FileMode]::Open, [IO.FileAccess]::Read,
    [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
$taskReader = [IO.StreamReader]::new($taskStream)
$taskTerminals = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$taskLatest = ''
$taskPartial = ''
$taskQuietSince = $null
Write-Output 'Waiting for the requested turn to finish and for the Task to become idle; Bridge remains running.'
try {
    while ([DateTime]::UtcNow -lt $taskDeadline) {
        if ($taskStream.Length -lt $taskStream.Position) { throw 'Task log was replaced; update canceled without stopping Bridge.' }
        $taskPartial += $taskReader.ReadToEnd()
        $taskLineEnd = $taskPartial.LastIndexOf("`n")
        if ($taskLineEnd -ge 0) {
            $taskCompleteLines = $taskPartial.Substring(0, $taskLineEnd)
            $taskPartial = $taskPartial.Substring($taskLineEnd + 1)
            foreach ($taskLine in $taskCompleteLines.Split("`n")) {
                if ($taskLine -notmatch '"type"\s*:\s*"(task_started|task_complete|task_failed|turn_aborted)"') { continue }
                try { $taskEvent = $taskLine | ConvertFrom-Json -ErrorAction Stop } catch { continue }
                if ($taskEvent.type -ne 'event_msg' -or !$taskEvent.payload.turn_id) { continue }
                $taskEventTurn = [string]$taskEvent.payload.turn_id
                if ($taskEvent.payload.type -eq 'task_started') {
                    $taskLatest = $taskEventTurn
                    $taskQuietSince = $null
                }
                elseif ($taskEvent.payload.type -in @('task_complete', 'task_failed', 'turn_aborted')) {
                    [void]$taskTerminals.Add($taskEventTurn)
                }
            }
        }
        $taskIdle = $taskLatest.Length -gt 0 -and $taskTerminals.Contains($taskLatest)
        if ($taskTerminals.Contains($TurnId) -and $taskIdle -and $taskPartial.Length -eq 0) {
            if ($null -eq $taskQuietSince) { $taskQuietSince = [DateTime]::UtcNow }
            if (([DateTime]::UtcNow - $taskQuietSince).TotalSeconds -ge $QuietSeconds) {
                Write-Output 'Requested turn completed and Task stayed idle; local update may proceed.'
                return
            }
        }
        else { $taskQuietSince = $null }
        Start-Sleep -Milliseconds 500
    }
    throw 'Task did not become idle before the update deadline; Bridge was left running and unchanged.'
}
finally { $taskReader.Dispose(); $taskStream.Dispose() }
