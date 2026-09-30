$ErrorActionPreference='Stop'
$checks=[Collections.Generic.List[object]]::new()
function Probe([string]$copy,[string]$command='status',[string]$target='',[string]$value='') {
    $r=& "$PSScriptRoot/Invoke-Probe.ps1" -Copy $copy -Command $command -Target $target -Value $value | ConvertFrom-Json
    if(!$r.success) { throw "$copy/$command failed: $($r.error)" }
    return $r
}
function Check([string]$name,[scriptblock]$condition) {
    $deadline=[DateTime]::UtcNow.AddSeconds(15)
    do { if(& $condition) { $checks.Add(@{name=$name;passed=$true;utc=[DateTime]::UtcNow.ToString('o')}); Write-Host "PASS: $name"; return }; Start-Sleep -Milliseconds 150 } while([DateTime]::UtcNow -lt $deadline)
    throw "FAIL: $name"
}
function Object($reply,[string]$name,[string]$path='scenes/minimal.scene') {
    ($reply.Scenes | Where-Object Path -eq $path).Objects.psobject.Properties.Value | Where-Object {$_.Data.Name -eq $name}
}
try {
    Probe A reset | Out-Null; Probe B reset | Out-Null
    Probe A open '' 'scenes/minimal.scene' | Out-Null; Probe B open '' 'scenes/minimal.scene' | Out-Null
    Probe A dock | Out-Null; Probe B dock | Out-Null
    Check 'M1: dock constructed in two separate editor processes' { (Probe A).process -ne (Probe B).process }
    Probe A host | Out-Null; Probe B join | Out-Null
    Check 'M2: two editors connected and roster populated' { $a=Probe A; $b=Probe B; $a.Connected -and $b.Connected -and !$a.Error -and !$b.Error -and $a.Roster.Count -eq 1 -and $b.Roster.Count -eq 1 }
    Check 'M3: both editors execute remote camera marker drawing' { (Probe A).DrawnMarkers -eq 1 -and (Probe B).DrawnMarkers -eq 1 }
    Probe B camera '' '12,34,56' | Out-Null
    Check 'M3: camera position streamed without scene save' { (Probe A).Roster[0].Presence.position -eq '12,34,56' }
    Probe B select Sun | Out-Null
    Check 'M3: named selection highlight draw callback' { (Probe A).DrawnSelections -eq 1 }
    Probe B open '' 'scenes/secondary.scene' | Out-Null
    Check 'M3: other-scene peer remains in roster; marker filtered' { $a=Probe A; $a.Roster[0].Scene -eq 'scenes/secondary.scene' -and $a.DrawnMarkers -eq 0 }
    Probe A open '' 'scenes/secondary.scene' | Out-Null
    Check 'M3: opening peer scene restores its marker' { (Probe A).DrawnMarkers -eq 1 }
    Probe A open '' 'scenes/minimal.scene' | Out-Null; Probe B open '' 'scenes/minimal.scene' | Out-Null
    Probe A runtime-opt-in '' 'true' | Out-Null
    Probe A create '' 'Before Runtime' | Out-Null
    Check 'T6: source fixture converged before runtime lifecycle activation' { (Object (Probe A) 'Before Runtime') -and (Object (Probe B) 'Before Runtime') }
    Probe A play | Out-Null
    Check 'T6: connected source lifecycle has one approved opt-in runtime session' { $a=Probe A; $a.Connected -and !$a.Error -and $a.RuntimeLifecycleState -eq 'RuntimeActive' -and $a.RunId -and !$a.HasPendingLifecycleCandidate -and $a.RuntimeSessions.Count -eq 1 -and $a.RuntimeSessions[0].ParentPath -eq 'scenes/minimal.scene' }
    Check 'T6: source collaboration remains connected without error while opted-in runtime lifecycle is active' { $a=Probe A; $b=Probe B; $a.Connected -and $b.Connected -and !$a.Error -and !$b.Error }
    Probe B delete 'Before Runtime' | Out-Null
    Check 'T6: source-edit convergence continues while runtime lifecycle is active' { $a=Probe A; $b=Probe B; $a.Connected -and $b.Connected -and !$a.Error -and !$b.Error -and $null -eq (Object $a 'Before Runtime') -and $null -eq (Object $b 'Before Runtime') }
    Probe A stop | Out-Null
    Check 'T6: connected stop returns the source lifecycle to Source' { $a=Probe A; $a.Connected -and !$a.Error -and $a.RuntimeLifecycleState -eq 'Source' -and !$a.HasPendingLifecycleCandidate -and $a.RuntimeSessions.Count -eq 0 }
    Probe A runtime-opt-in '' 'false' | Out-Null
    Probe A create '' 'Test Root' | Out-Null
    Check 'M4: unsaved object creation preserves GUID' { $a=Object (Probe A) 'Test Root'; $b=Object (Probe B) 'Test Root'; $a -and $b -and $a.Id -eq $b.Id }
    Probe A rotation 'Test Root' '0,0,0.70710677,0.70710677' | Out-Null
    Check 'M4: rotation synchronizes' { $a=Object (Probe A) 'Test Root'; $b=Object (Probe B) 'Test Root'; $a.Data.Rotation -ne '0,0,0,1' -and $a.Data.Rotation -eq $b.Data.Rotation }
    Probe A scale 'Test Root' '2,3,4' | Out-Null
    Check 'M4: scale synchronizes' { (Object (Probe B) 'Test Root').Data.Scale -eq '2,3,4' }
    Probe A begin | Out-Null
    for($i=1;$i -le 8;$i++) {
        $position="$($i*10),20,30"
        Probe A position 'Test Root' $position | Out-Null
        Check "M4: intermediate transform $i observed remotely" { (Object (Probe B) 'Test Root').Data.Position -eq $position }
    }
    Probe A end | Out-Null
    Check 'M4: transform gesture receives final acknowledgement' { (Probe A).Status -match 'revision' }
    Probe A undo | Out-Null
    Check 'M4: one undo reverses all eight streamed gesture updates' { (Object (Probe B) 'Test Root').Data.Position -eq '0,0,0' }
    Probe A position 'Test Root' '80,20,30' | Out-Null
    Check 'M4: transform restored for late-join fixture' { (Object (Probe B) 'Test Root').Data.Position -eq '80,20,30' }
    Probe A create-model '' 'Test Model' | Out-Null
    Check 'M4: model and collider creation preserves component GUIDs' { $a=Object (Probe A) 'Test Model'; $b=Object (Probe B) 'Test Model'; $a -and $b -and $b.Data.Components.Count -eq 2 -and ($a.Data.Components.__guid -join ',') -eq ($b.Data.Components.__guid -join ',') }
    Probe B tint 'Test Model' '0.2,0.3,0.4,1' | Out-Null
    Check 'M4: renderer tint synchronizes' { ((Object (Probe A) 'Test Model').Data.Components | Where-Object __type -eq 'Sandbox.ModelRenderer').Tint -eq '0.2,0.3,0.4,1' }
    Probe B box-scale 'Test Model' '10,20,30' | Out-Null
    Check 'M4: collider scale synchronizes' { ((Object (Probe A) 'Test Model').Data.Components | Where-Object __type -eq 'Sandbox.BoxCollider').Scale -eq '10,20,30' }
    Probe A delete 'Test Model' | Out-Null
    Check 'M4: supported component-bearing object deletion' { $null -eq (Object (Probe B) 'Test Model') }
    Probe A create '' 'Test Child' | Out-Null
    Check 'M4: second object arrives' { $null -ne (Object (Probe B) 'Test Child') }
    Probe A parent 'Test Child' 'Test Root' | Out-Null
    Check 'M4: parenting converges' { $b=Probe B; (Object $b 'Test Child').Parent -eq (Object $b 'Test Root').Id }
    Probe A rename 'Test Child' 'Renamed Child' | Out-Null
    Check 'M4: renaming converges' { $null -ne (Object (Probe B) 'Renamed Child') }
    Probe A undo | Out-Null
    Check 'M4: collaboration undo propagates inverse operation' { $null -ne (Object (Probe B) 'Test Child') }
    Probe B light Sun '0.2,0.4,0.6,1' | Out-Null
    Check 'M4: supported inspector property converges' { (Object (Probe A) 'Sun').Data.Components[0].LightColor -eq '0.2,0.4,0.6,1' }
    Probe A delete 'Test Child' | Out-Null
    Check 'M4: object deletion converges' { $null -eq (Object (Probe B) 'Test Child') }
    Probe B reset | Out-Null
    Probe B join | Out-Null
    Check 'M4: late join from disk baseline receives unsaved authority state' { $b=Probe B; $b.Connected -and !$b.Error -and (Object $b 'Test Root').Data.Position -eq '80,20,30' }
    Probe B leave | Out-Null
    Check 'M4: disconnected peer removed from roster' { (Probe A).Roster.Count -eq 0 }
    Probe A rename 'Test Root' 'During Disconnect' | Out-Null
    Check 'M4: host commits during disconnect' { (Probe A).Status -match 'revision' }
    Probe B reconnect | Out-Null
    Check 'M4: reconnect catches up without duplicate IDs' { $b=Probe B; $objects=($b.Scenes | Where-Object Path -eq 'scenes/minimal.scene').Objects.psobject.Properties.Value; $b.Connected -and !$b.Error -and (Object $b 'During Disconnect') -and ($objects.Id | Sort-Object -Unique).Count -eq $objects.Count }
    Probe B open '' 'scenes/secondary.scene' | Out-Null
    Probe B close '' 'scenes/minimal.scene' | Out-Null
    Probe A rename 'During Disconnect' 'Edited While Closed' | Out-Null
    Check 'M4: host keeps editing a scene closed by another collaborator' { (Probe A).Status -match 'revision' }
    Probe B leave | Out-Null; Probe B reconnect | Out-Null
    Check 'M4: reconnect tolerates a previously closed scene' { $b=Probe B; $b.Connected -and !$b.Error }
    Probe B open '' 'scenes/minimal.scene' | Out-Null
    Check 'M4: reopening a closed scene catches up from authority' { $null -ne (Object (Probe B) 'Edited While Closed') }
    $raceId=(Object (Probe A) 'Edited While Closed').Id
    $raceTime=[DateTime]::UtcNow.AddSeconds(2).ToString('o')
    Probe A schedule-rename $raceId "$raceTime|Race A" | Out-Null
    Probe B schedule-rename $raceId "$raceTime|Race B" | Out-Null
    Check 'M4: simultaneous same-object edits produce a visible conflict' { (Probe A).Error -match 'oncurrent|onflict' -or (Probe B).Error -match 'oncurrent|onflict' }
    $loser=if((Probe A).Error){'A'}else{'B'}
    Probe $loser reconnect | Out-Null
    Check 'M4: conflict recovery converges both editors' {
        $a=Probe A; $b=Probe B
        $an=($a.Scenes | Where-Object Path -eq 'scenes/minimal.scene').Objects.$raceId.Data.Name
        $bn=($b.Scenes | Where-Object Path -eq 'scenes/minimal.scene').Objects.$raceId.Data.Name
        !$a.Error -and !$b.Error -and $an -eq $bn
    }
    Probe B unsupported $raceId | Out-Null
    Check 'M4: unsupported built-in property stops synchronization visibly' { (Probe B).Error -match 'Unsupported GameObject property' }
    Probe B reconnect | Out-Null
    Check 'M4: unsupported local edit archived and authoritative state restored' { $b=Probe B; !$b.Error -and $b.Connected -and ($b.Scenes | Where-Object Path -eq 'scenes/minimal.scene').Objects.$raceId.Data.NetworkMode -eq 2 }
    Probe A save | Out-Null; Probe B save | Out-Null
    $root=Join-Path $PSScriptRoot '../.collaboration/test-copies'
    Check 'M4: both saved scene files agree and contain no presence objects' {
        $a=Get-Content "$root/A/Assets/scenes/minimal.scene" -Raw | ConvertFrom-Json
        $b=Get-Content "$root/B/Assets/scenes/minimal.scene" -Raw | ConvertFrom-Json
        ($a.GameObjects | ConvertTo-Json -Depth 80 -Compress) -eq ($b.GameObjects | ConvertTo-Json -Depth 80 -Compress) -and !($a.GameObjects.Name -match 'Developer [AB]')
    }
    Probe B reset | Out-Null
    Probe B join | Out-Null
    Check 'M4: late join accepts the host saved checkpoint' { $b=Probe B; $b.Connected -and !$b.Error }
    Probe B reset | Out-Null
    $mismatchPath=Join-Path $root 'B/ProjectSettings/collaboration-test-mismatch.txt'
    [IO.File]::WriteAllText($mismatchPath,'deliberately incompatible source baseline')
    Probe B join | Out-Null
    Check 'M2: incompatible project copies rejected with sync instructions' { (Probe B).Error -match 'baseline differs' }
    Remove-Item -LiteralPath $mismatchPath
    Probe B reset | Out-Null
    Probe B wrong-key | Out-Null
    Check 'M2: wrong session key produces explicit failure' { (Probe B).Error -match 'Incorrect session key' }
    Probe B leave | Out-Null; Probe A leave | Out-Null
} finally {
    $out=Join-Path $PSScriptRoot '../TestResults'
    New-Item -ItemType Directory -Force $out | Out-Null
    $checks | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $out 'editor-tests.json')
}
Write-Host "$($checks.Count) two-editor checks passed"
