<#
.SYNOPSIS
  Sets the library version everywhere it is stated, in one step.

.DESCRIPTION
  Writes Libraries/collaboration/VERSION, Libraries/collaboration/.version, LibraryInfo.LibraryVersion and, where the demo project's
  ProjectSettings/collaboration.json exists, its LibraryVersion. The core tests check that all of them agree, so run them afterwards.
  Line endings and every other byte of each file are kept. -Root runs it against another copy of the repository.

.EXAMPLE
  powershell -File Tools/Bump-Version.ps1 1.3.0
#>
param(
    [Parameter(Mandatory = $true, Position = 0)][string]$Version,
    [string]$Root = (Join-Path $PSScriptRoot '..')
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "The version must be three numbers such as 1.3.0, not '$Version'." }
$Root = [IO.Path]::GetFullPath($Root)
$library = Join-Path $Root 'Libraries/collaboration'
$encoding = New-Object Text.UTF8Encoding($false)

function Set-Text([string]$path, [string]$pattern, [string]$replacement) {
    $text = [IO.File]::ReadAllText($path)
    $regex = New-Object Text.RegularExpressions.Regex($pattern)
    if (-not $regex.IsMatch($text)) { throw "No version was found in $path." }
    [IO.File]::WriteAllText($path, $regex.Replace($text, $replacement, 1), $encoding)
    Write-Host "Updated $path"
}

foreach ($name in @('VERSION', '.version')) {
    $path = Join-Path $library $name
    if (-not (Test-Path -LiteralPath $path)) { throw "$path does not exist; is -Root the repository folder?" }
    [IO.File]::WriteAllText($path, $Version, $encoding)
    Write-Host "Updated $path"
}
Set-Text (Join-Path $library 'Editor/Core/LibraryInfo.cs') 'LibraryVersion = "\d+\.\d+\.\d+"' ('LibraryVersion = "' + $Version + '"')
$settings = Join-Path $Root 'ProjectSettings/collaboration.json'
if (Test-Path -LiteralPath $settings) {
    Set-Text $settings '"LibraryVersion"\s*:\s*"\d+\.\d+\.\d+"' ('"LibraryVersion": "' + $Version + '"')
}
Write-Host "The library version is now $Version. Run the core tests to confirm every copy agrees."
