$ErrorActionPreference = 'Stop'

$source = Get-Content -Raw -Path (Join-Path $PSScriptRoot '..\PLCService.cs')
$dbSource = Get-Content -Raw -Path (Join-Path $PSScriptRoot '..\Services\DatabaseService.cs')

function Assert-Contains($Text, $Pattern, $Message) {
    if ($Text -notmatch $Pattern) {
        throw $Message
    }
}

function Assert-Order($Text, $FirstPattern, $SecondPattern, $Message) {
    $first = [regex]::Match($Text, $FirstPattern)
    $second = [regex]::Match($Text, $SecondPattern)

    if (!$first.Success -or !$second.Success -or $first.Index -ge $second.Index) {
        throw $Message
    }
}

Assert-Order $source '_dbService\.UpdatePlcStatus\(updatedPlc\);' 'ShouldCheckAutoTask\(updatedPlc\)' `
    'PLC status must be saved immediately after the first PLC read, before auto-task processing can delay it.'

Assert-Contains $source 'TryDetermineOutboundLoadingPoint' `
    'Auto outbound must use a loading point selector that can reject full loading points.'
Assert-Contains $source 'GetLoadingPointStatuses' `
    'Auto outbound loading point selection must read LoadingPoint_Status.'
Assert-Contains $source 'CurrentPalletNumber' `
    'Auto outbound loading point selection must consider CurrentPalletNumber.'
Assert-Contains $source 'BoxWeightA\s*<\s*2m' `
    'PLCLocationCode 0 must be selected only when BoxWeightA is less than 2.'
Assert-Contains $source 'BoxWeightB\s*<\s*2m' `
    'PLCLocationCode 1 must be selected only when BoxWeightB is less than 2.'
Assert-Contains $source 'operationId\s*=\s*-1' `
    'Auto outbound must skip issuing a command when both loading points are unavailable.'
Assert-Contains $dbSource 'CurrentPalletNumber' `
    'DatabaseService must query CurrentPalletNumber from LoadingPoint_Status.'
Assert-Contains $source 'StartAutoOutboundPalletSyncMonitor\(\s*plc\.PlcId,\s*bill\.Shelf,\s*bill\.Position,\s*loadingPoint,\s*bill\.Tray' `
    'Auto outbound must start a pallet sync monitor with the tray captured before outbound completion.'
Assert-Contains $source 'UpdateLoadingPointPallet\(' `
    'Auto outbound pallet sync monitor must update LoadingPoint_Status.CurrentPalletNumber.'
Assert-Contains $source 'StartAutoOutboundPalletSyncMonitor[\s\S]*observedOutboundProgress[\s\S]*plcStatus\.OperationResult\s*!=\s*0[\s\S]*observedOutboundProgress\s*=\s*true[\s\S]*plcStatus\.OperationResult\s*!=\s*6\s*&&\s*!\(plcStatus\.OperationResult\s*==\s*0\s*&&\s*observedOutboundProgress\)' `
    'Auto outbound pallet sync must accept the return to idle after progress when the short-lived completion state 6 is missed.'
Assert-Contains $dbSource 'UPDATE\s+LoadingPoint_Status[\s\S]*CurrentPalletNumber\s*=\s*@PalletNumber' `
    'DatabaseService must update CurrentPalletNumber in LoadingPoint_Status.'

Write-Host 'PLC service auto outbound checks passed.'
