param([string]$Copy = 'A', [string]$Command = 'status', [string]$Target, [string]$Value)
$ErrorActionPreference = 'Stop'
$copyRoot = Join-Path $PSScriptRoot "../.collaboration/test-copies/$Copy/.collaboration"
$requestId = [guid]::NewGuid().ToString()
$payload = @{ id=$requestId; command=$Command; target=$Target; value=$Value } | ConvertTo-Json -Compress
$tempPath = Join-Path $copyRoot 'request.tmp'
[IO.File]::WriteAllText($tempPath,$payload)
Move-Item -LiteralPath $tempPath -Destination (Join-Path $copyRoot 'request.json') -Force
$deadline = [DateTime]::UtcNow.AddSeconds(30)
while([DateTime]::UtcNow -lt $deadline) {
    $replyPath=Join-Path $copyRoot 'reply.json'
    if(Test-Path -LiteralPath $replyPath) {
        try { $reply = Get-Content -LiteralPath $replyPath -Raw | ConvertFrom-Json } catch { $reply = $null }
        if($reply.id -eq $requestId) { $reply | ConvertTo-Json -Depth 50 -Compress; exit 0 }
    }
    Start-Sleep -Milliseconds 100
}
throw "Editor $Copy did not respond to $Command within 30 seconds"
