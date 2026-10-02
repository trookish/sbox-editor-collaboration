# Runs the net10.0 smoke project. The executable is downloaded to the Hermes scratch directory, never the repository.
# The temporary public tunnel serves ONLY "ok" at /health and is closed when the smoke process ends.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $PSScriptRoot 'TunnelSmoke/TunnelSmoke.csproj'
$capture = Join-Path $PSScriptRoot 'TunnelSmoke/sample-output.txt'
$scratch = Join-Path $env:LOCALAPPDATA 'hermes/cache/scratch'
# The Windows PowerShell process can inherit a system TMPDIR unrelated to Hermes; refuse it so test artifacts stay in Hermes scratch.
$tmp = if ($env:TMPDIR -and $env:TMPDIR.StartsWith($scratch, [StringComparison]::OrdinalIgnoreCase)) { $env:TMPDIR } else { $scratch }
if (-not (Test-Path $tmp)) { New-Item -ItemType Directory -Force -Path $tmp | Out-Null }
Write-Host "Scratch directory: $tmp"
Write-Host "Smoke project: $project"
Write-Host "Captured banner: $capture"
dotnet run --project $project -- --tmp $tmp --capture $capture
if ($LASTEXITCODE -ne 0) { throw "The real tunnel smoke test failed (exit $LASTEXITCODE)." }
