<#
.SYNOPSIS
  One-click solo test of the collaboration library, with one Steam account and no second person.

.DESCRIPTION
  Copies this project into a disposable folder (.collaboration/test-copies/Solo), opens the copy in its own s&box editor window, and runs the
  test there by itself: the copy hosts a session, a scripted collaborator joins it over the real transport, edits an object, leaves, and is
  refused when it should be. You watch the blue floating sphere and the checklist in the Collaboration dock. Your real project is never changed.

  This is the same thing as the "Run solo test" button in the Collaboration dock; both call the same copy code.

.PARAMETER Manual
  Open the copy but do not start the test; press Run the test in the copy's Collaboration dock instead.

.PARAMETER Relay
  Also point the copy at a relay, for example ws://localhost:8080/relay (start one with: dotnet run --project Tools/Relay), so the relay step runs.

.PARAMETER Force
  Run even when less than 3 GB of memory is available. The test opens a second editor (about 3.6 GB); on a machine that is already short of memory
  this can make other programs stall.

.PARAMETER Stop
  Ends a running solo test from outside its window: the collaborator leaves, the object it made is removed, and the session ends.

.PARAMETER Wait
  Wait for the report and print it. Without it the script returns as soon as the editor window is open.
#>
param(
    [switch]$Manual,
    [string]$Relay,
    [switch]$Wait,
    [switch]$Stop,
    [switch]$Force,
    [string]$SboxRoot = 'C:\Program Files (x86)\Steam\steamapps\common\sbox'
)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$exe = Join-Path $SboxRoot 'sbox-dev.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "s&box was not found at $SboxRoot. Pass -SboxRoot with its folder." }

$copyRoot = Join-Path $workspace '.collaboration/test-copies/Solo'
if ($Stop) {
    $cmd = Join-Path $copyRoot '.collaboration/solo-command.txt'
    if (-not (Test-Path -LiteralPath (Split-Path $cmd))) { Write-Host 'There is no solo test copy to stop.'; exit 0 }
    Set-Content -LiteralPath $cmd -Value 'stop'
    Write-Host 'Asked the solo test window to stop. It ends the session within a second or two.'
    exit 0
}
$alive = Join-Path $copyRoot '.collaboration/solo-alive.txt'
if (Test-Path -LiteralPath $alive) {
    $lines = Get-Content -LiteralPath $alive
    if ($lines.Count -ge 2) {
        $age = ([DateTime]::UtcNow - [DateTime]::Parse($lines[1], $null, 'RoundtripKind')).TotalSeconds
        if ($age -lt 20 -and (Get-Process -Id ([int]$lines[0]) -ErrorAction SilentlyContinue)) {
            Write-Host 'The solo test window is already open. Switch to it.'
            exit 0
        }
    }
}

# Available memory = free + the standby cache Windows can give back at once; the smaller "free" figure would refuse machines that have room.
$availGb = [math]::Round((Get-Counter '\Memory\Available MBytes').CounterSamples[0].CookedValue / 1024, 1)
if ($availGb -lt 3.0 -and -not $Force) {
    throw "Only $availGb GB of memory is available. The solo test opens a second editor, which needs about 3 GB, so close some programs first. To run it anyway, add -Force."
}
if ($availGb -lt 3.0) { Write-Warning "Only $availGb GB of memory is available; continuing because of -Force." }

Write-Host 'Building the solo test copy...'
$args1 = @('run', '--project', (Join-Path $PSScriptRoot 'RelayTests/RelayTests.csproj'), '--', 'solo-copy', $workspace)
if ($Manual) { $args1 += '--manual' }
$out = & dotnet @args1 2>&1
if ($LASTEXITCODE -ne 0) { throw ("Could not build the solo test copy:`n" + ($out -join "`n")) }
$project = ($out | Where-Object { $_ -like 'PROJECT=*' } | Select-Object -First 1) -replace '^PROJECT=', ''
$files = ($out | Where-Object { $_ -like 'FILES=*' } | Select-Object -First 1) -replace '^FILES=', ''
if (-not $project) { throw ("The copy was not reported:`n" + ($out -join "`n")) }
Write-Host "Copied $files files."

if ($Relay) {
    $settings = Join-Path $copyRoot 'ProjectSettings/collaboration.json'
    $json = Get-Content -LiteralPath $settings -Raw | ConvertFrom-Json
    $json | Add-Member -NotePropertyName RelayUrl -NotePropertyValue $Relay -Force
    $json | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $settings -Encoding UTF8
    Write-Host "Copy points at the relay $Relay."
}

Write-Host 'Opening the solo test window...'
Start-Process -FilePath $exe -ArgumentList @('-project', ('"' + $project + '"'), '-novid') | Out-Null
if ($Manual) { Write-Host 'Opened. In the window, use the Collaboration dock > Try it alone > Run the test.'; exit 0 }
Write-Host 'Opened. The test starts by itself about ten seconds after the editor loads; watch the Collaboration dock in that window.'

if ($Wait) {
    $report = Join-Path $copyRoot '.collaboration/solo-report.json'
    $deadline = [DateTime]::UtcNow.AddMinutes(6)
    $state = ''
    while ([DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Seconds 3
        if (Test-Path -LiteralPath $report) {
            try { $r = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json } catch { continue }
            $state = $r.state
            if ($state -in @('checks-passed', 'checks-failed', 'aborted', 'stopped')) { break }
        }
    }
    if (-not (Test-Path -LiteralPath $report)) { throw 'The test did not report within six minutes. Look at the solo test window.' }
    $r = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    foreach ($s in $r.steps) { '{0,-8} {1}{2}' -f $s.state, $s.title, $(if ($s.detail) { "  -- $($s.detail)" } else { '' }) }
    Write-Host ''
    Write-Host $r.summary
    if ($state -eq 'checks-passed') { Write-Host 'The collaborator keeps orbiting until you press Stop in that window.' }
    if ($state -ne 'checks-passed') { exit 1 }
}
