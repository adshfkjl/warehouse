$ErrorActionPreference = 'Stop'

$source = Get-Content -Raw -Path (Join-Path $PSScriptRoot '..\Controllers\PlcOperationsController.cs')

function Assert-Contains($Text, $Pattern, $Message) {
    if ($Text -notmatch $Pattern) {
        throw $Message
    }
}

function Assert-NotContains($Text, $Pattern, $Message) {
    if ($Text -match $Pattern) {
        throw $Message
    }
}

Assert-Contains $source 'StartOutboundPalletSyncMonitor\(\s*string plcId,\s*int shelf,\s*int position,\s*int loadingPoint,\s*string palletNumber' `
    'Outbound pallet sync monitor must receive the pallet number captured before outbound completion.'

Assert-Contains $source 'StartOutboundPalletSyncMonitor\(\s*location\.PLCID,\s*location\.Shelf,\s*location\.Position,\s*request\.LoadingPoint,\s*request\.PalletCode' `
    'Tray outbound must pass request.PalletCode to the pallet sync monitor.'

Assert-NotContains $source 'UpdateLoadingPointPallet\(\s*plcId,\s*loadingPoint,\s*location\.Tray' `
    'Outbound pallet sync monitor must not depend on LocationManagements.Tray after completion because it may already be cleared.'

Write-Host 'Outbound pallet sync checks passed.'
