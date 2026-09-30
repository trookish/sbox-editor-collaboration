param(
    [string]$Command = 'status',
    [string]$Target,
    [string]$Value,
    [int]$TimeoutSeconds = 30
)
# Drives the probe contract against the ALREADY OPEN editor on this project.
# Identical contract to Invoke-Probe.ps1, but rooted at the workspace
# .collaboration folder instead of a disposable test copy, so it talks to the
# editor instance the user has open right now.
$ErrorActionPreference = 'Stop'
$collabRoot = Join-Path $PSScriptRoot '../.collaboration'
$requestPath = Join-Path $collabRoot 'request.json'
$replyPath = Join-Path $collabRoot 'reply.json'
# Remove a stale reply up front, like Drive-OpenEditor.ps1, so a match cannot
# come from an earlier command's reply.
if (Test-Path -LiteralPath $replyPath) { Remove-Item -LiteralPath $replyPath -Force -ErrorAction SilentlyContinue }
$requestId = [guid]::NewGuid().ToString()
$payload = @{ id=$requestId; command=$Command; target=$Target; value=$Value } | ConvertTo-Json -Compress
$tempPath = Join-Path $collabRoot 'request.tmp'
[IO.File]::WriteAllText($tempPath,$payload)
Move-Item -LiteralPath $tempPath -Destination $requestPath -Force
$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
while([DateTime]::UtcNow -lt $deadline) {
    if(Test-Path -LiteralPath $replyPath) {
        try { $reply = Get-Content -LiteralPath $replyPath -Raw | ConvertFrom-Json } catch { $reply = $null }
        if($reply.id -eq $requestId) { $reply | ConvertTo-Json -Depth 50 -Compress; exit 0 }
    }
    Start-Sleep -Milliseconds 100
}
# A request still on disk is a live instruction: the editor may read it minutes
# later and execute it unobserved, which is not acceptable for the mutating
# commands this driver can send (reset, save, delete). Withdraw it before
# reporting the timeout.
try { if (Test-Path -LiteralPath $requestPath) { Remove-Item -LiteralPath $requestPath -Force -ErrorAction Stop } } catch { }
throw "Open editor did not respond to $Command within $TimeoutSeconds seconds. The pending request.json was withdrawn, but the editor may have read it first: treat $Command as possibly executed."
