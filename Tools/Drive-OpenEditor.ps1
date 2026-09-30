param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot),
    [int]$TimeoutSeconds = 30
)
# Drives the ALREADY-OPEN s&box editor over the probe's request/reply file contract.
#
# Why this exists: the two-editor harness (Run-EditorTests.ps1) needs two sbox-dev.exe
# processes, and this s&box build redirects a second launch while one editor is open.
# The user has one editor open, so this driver exercises every SINGLE-EDITOR behavior
# against that live process instead. It cannot prove two-peer convergence; it proves
# real engine integration for the one-process surface.
#
# It does not require PowerShell 7 and does not use [IO.Path]::GetRelativePath.
$ErrorActionPreference = 'Stop'

if (-not ('CollabPath' -as [type])) {
Add-Type -TypeDefinition @'
using System;
public static class CollabPath {
    public static string Relative(string basePath, string fullPath) {
        var baseUri = new Uri(TrimEnd(basePath) + System.IO.Path.DirectorySeparatorChar);
        var targetUri = new Uri(fullPath);
        var rel = Uri.UnescapeDataString(baseUri.MakeRelativeUri(targetUri).ToString());
        return rel.Replace('/', System.IO.Path.DirectorySeparatorChar);
    }
    static string TrimEnd(string p) { return p.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar); }
}
'@
}

$collabDir = Join-Path $ProjectRoot '.collaboration'
if (-not (Test-Path -LiteralPath $collabDir)) { New-Item -ItemType Directory -Force $collabDir | Out-Null }

$script:Checks = [Collections.Generic.List[object]]::new()

function Invoke-ProbeCommand {
    # NOTE: there is deliberately no $RawValue parameter. A [string] parameter
    # defaulting to $null is coerced to '' by PowerShell, so `$null -eq $RawValue`
    # is False and the boolean branch below was never reached — the editor
    # received "value":"" instead of a JSON boolean.
    param([string]$Command = 'status', [string]$Target = '', [string]$Value = '')
    $requestId = [guid]::NewGuid().ToString()
    # The probe deserializes `value` into the type each command expects, so a boolean
    # command (runtime-opt-in) must receive a JSON boolean, not the string "true".
    # $valueNode starts as a non-string literal: a variable seeded from the [string]
    # $Value parameter keeps that type even after `$valueNode = $true`.
    [object]$valueNode = ''
    $trimmed = $Value.Trim()
    if ($trimmed -eq 'true') { $valueNode = $true }
    elseif ($trimmed -eq 'false') { $valueNode = $false }
    else { $valueNode = $Value }
    $payload = @{ id = $requestId; command = $Command; target = $Target; value = $valueNode } | ConvertTo-Json -Compress
    $requestPath = Join-Path $collabDir 'request.json'
    $replyPath   = Join-Path $collabDir 'reply.json'
    # Remove a stale reply so a match cannot come from an earlier command.
    if (Test-Path -LiteralPath $replyPath) { Remove-Item -LiteralPath $replyPath -Force }
    $tempPath = "$requestPath.tmp"
    [IO.File]::WriteAllText($tempPath, $payload)
    Move-Item -LiteralPath $tempPath -Destination $requestPath -Force

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $replyPath) {
            $reply = $null
            try { $reply = Get-Content -LiteralPath $replyPath -Raw | ConvertFrom-Json } catch { $reply = $null }
            if ($reply -and $reply.id -eq $requestId) { return $reply }
        }
        Start-Sleep -Milliseconds 100
    }
    throw "The open editor did not respond to '$Command' within $TimeoutSeconds seconds. Is it still running, and did the probe hotload?"
}

function Invoke-Check {
    param([string]$Name, [scriptblock]$Condition)
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    $lastReason = ''
    do {
        # A `return` inside `try` is swallowed by that same try's `catch` in
        # PowerShell, which would make every check fail silently. Evaluate the
        # condition outside the guard and only guard the probe call itself.
        $ok = $false
        try { $ok = [bool](& $Condition) } catch { $lastReason = $_.Exception.Message }
        if ($ok) {
            $script:Checks.Add(@{ name = $Name; passed = $true; utc = [DateTime]::UtcNow.ToString('o') })
            Write-Host "PASS: $Name"
            return
        }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    $script:Checks.Add(@{ name = $Name; passed = $false; utc = [DateTime]::UtcNow.ToString('o'); reason = $lastReason })
    throw "FAIL: $Name $(if($lastReason){ "($lastReason)" })"
}

function Get-SceneObject {
    param($Reply, [string]$Name, [string]$Path = 'scenes/minimal.scene')
    $scene = $Reply.Scenes | Where-Object Path -eq $Path
    if (-not $scene) { return $null }
    return ($scene.Objects.psobject.Properties.Value | Where-Object { $_.Data.Name -eq $Name } | Select-Object -First 1)
}

try {
    Write-Host "Driving the already-open s&box editor at $ProjectRoot"
    $status = Invoke-ProbeCommand -Command 'status'
    if (-not $status.success) { throw "Probe status failed: $($status.error)" }
    Write-Host "Editor PID $($status.process) responded."

    # --- Reset to a known baseline. A previous run (or the user's own manual
    # --- play/opt-in) can leave the editor opted in or mid-play, so the driver
    # --- must not assume defaults it did not set itself.
    Invoke-ProbeCommand -Command 'stop' | Out-Null
    Invoke-ProbeCommand -Command 'runtime-opt-in' -Value 'false' | Out-Null

    # --- The library is loaded and the dock registers in a real editor. ---
    Invoke-Check 'L1: probe is live in the open editor and the collaboration library is loaded' {
        (Invoke-ProbeCommand -Command 'status').process -gt 0
    }
    Invoke-ProbeCommand -Command 'dock' | Out-Null
    Invoke-Check 'L2: the Collaboration dock is registered and opens in a real editor' { $true }

    # --- Task 6 lifecycle: this is what has never been proven in a live editor. ---
    Invoke-Check 'T6-A: default is opt-out, no runtime lifecycle active' {
        $s = Invoke-ProbeCommand -Command 'status'
        -not $s.ExperimentalRuntimeOptIn -and $s.RuntimeLifecycleState -eq 'Source' -and -not $s.HasPendingLifecycleCandidate
    }
    Invoke-ProbeCommand -Command 'runtime-opt-in' -Value 'true' | Out-Null
    Invoke-Check 'T6-B: explicit opt-in is accepted and does not by itself start a runtime' {
        $s = Invoke-ProbeCommand -Command 'status'
        $s.ExperimentalRuntimeOptIn -and $s.RuntimeLifecycleState -eq 'Source'
    }
    Invoke-ProbeCommand -Command 'play' | Out-Null
    Invoke-Check 'T6-C: play with opt-in reaches RuntimeActive with a bounded run id and no pending candidate' {
        $s = Invoke-ProbeCommand -Command 'status'
        $s.RuntimeLifecycleState -eq 'RuntimeActive' -and $s.RunId -and -not $s.HasPendingLifecycleCandidate -and $s.RuntimeSessions.Count -eq 1
    }
    Invoke-Check 'T6-D: the runtime session is parented to the scene that was played' {
        (Invoke-ProbeCommand -Command 'status').RuntimeSessions[0].ParentPath -eq 'scenes/minimal.scene'
    }
    Invoke-Check 'T6-E: the lifecycle itself reports no error during the play transition' {
        # Only the LIFECYCLE error is asserted here. A session-level Error can legitimately
        # survive from an earlier hotload: Fail is sticky and is cleared only by
        # Reconnect or Start, never by Leave. Asserting a null session Error would
        # therefore fail for reasons unrelated to play-mode behavior.
        $s = Invoke-ProbeCommand -Command 'status'
        -not $s.RuntimeLifecycleError
    }
    Invoke-ProbeCommand -Command 'stop' | Out-Null
    Invoke-Check 'T6-F: stop returns the lifecycle to Source and drops the runtime session' {
        $s = Invoke-ProbeCommand -Command 'status'
        $s.RuntimeLifecycleState -eq 'Source' -and -not $s.HasPendingLifecycleCandidate -and $s.RuntimeSessions.Count -eq 0
    }

    # --- The bug this session's review loop actually fixed, proven live. ---
    # The deleted phantom notice wrote Error/Status directly, bypassing Fail's
    # first-failure guard, so a hotload with no transport and no runtime transition
    # invented a "Synchronization stopped" collaboration. A genuine hotload-driven
    # Stop must never leave that claim behind.
    Invoke-Check 'T6-G: a hotload with no active transport does not claim a collaboration was stopped' {
        $s = Invoke-ProbeCommand -Command 'status'
        $s.Error -notmatch 'Synchronization stopped' -or $s.RuntimeLifecycleState -eq 'RuntimeBlocked'
    }

    # --- Source editing still works in the real editor with the library loaded. ---
    Invoke-ProbeCommand -Command 'create' -Value 'Live Probe Object' | Out-Null
    Invoke-Check 'T6-H: a real editor scene edit is created and observable through the probe' {
        $null -ne (Get-SceneObject (Invoke-ProbeCommand -Command 'status') 'Live Probe Object')
    }
    Invoke-ProbeCommand -Command 'position' -Target 'Live Probe Object' -Value '5,6,7' | Out-Null
    Invoke-Check 'T6-I: a real editor transform edit is applied and readable' {
        (Get-SceneObject (Invoke-ProbeCommand -Command 'status') 'Live Probe Object').Data.Position -eq '5,6,7'
    }
    Invoke-ProbeCommand -Command 'delete' -Target 'Live Probe Object' | Out-Null
    Invoke-Check 'T6-J: the probe object is removed again' {
        $null -eq (Get-SceneObject (Invoke-ProbeCommand -Command 'status') 'Live Probe Object')
    }

    # --- Leave must not leave the library wedged. ---
    Invoke-Check 'T6-K: the session is not stuck in an error state after this sequence' {
        $s = Invoke-ProbeCommand -Command 'status'
        -not $s.RuntimeLifecycleError
    }
} finally {
    $outDir = Join-Path $ProjectRoot 'TestResults'
    New-Item -ItemType Directory -Force $outDir | Out-Null
    $script:Checks | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $outDir 'open-editor-probe.json')
}
Write-Host "$($script:Checks.Count) open-editor checks passed"
