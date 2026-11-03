<#
.SYNOPSIS
  Turns on this repository's Git hooks (Tools/hooks), so every push first runs the compile gate and the core tests.

.DESCRIPTION
  Points core.hooksPath at Tools/hooks. The hooks are versioned with the repository, so a later change to them needs no reinstall.
  Run it once per clone. -Uninstall restores Git's default hooks folder.
#>
param([switch]$Uninstall)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ($Uninstall) {
    git -C $root config --unset core.hooksPath
    Write-Host 'Repository hooks turned off. Git uses .git/hooks again.'
    return
}
git -C $root config core.hooksPath Tools/hooks
if ($LASTEXITCODE -ne 0) { throw 'core.hooksPath could not be set.' }
# Git for Windows ignores the executable bit; elsewhere the hook must be executable.
if (-not $IsWindows -and $PSVersionTable.PSEdition -eq 'Core') { chmod +x (Join-Path $root 'Tools/hooks/pre-push') }
Write-Host 'Repository hooks turned on: each push now runs the compile gate (when s&box is installed) and the core tests.'
