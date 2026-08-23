$ErrorActionPreference = 'Stop'

$source = Get-Content -Raw -Path (Join-Path $PSScriptRoot '..\Services\PlcService.cs')

function Assert-Contains($Text, $Pattern, $Message) {
    if ($Text -notmatch $Pattern) {
        throw $Message
    }
}

Assert-Contains $source 'SyncLoadingPointWeightsAsync' `
    'API PLC status updates must synchronize LoadingPoint_Status.Weight.'

Assert-Contains $source 'UPDATE\s+LoadingPoint_Status[\s\S]*SET\s+\[Weight\]\s*=\s*\{1\}[\s\S]*PLCLocationCode\s*=\s*0' `
    'BoxWeightA must update LoadingPoint_Status.Weight for PLCLocationCode 0.'

Assert-Contains $source 'UPDATE\s+LoadingPoint_Status[\s\S]*SET\s+\[Weight\]\s*=\s*\{2\}[\s\S]*PLCLocationCode\s*=\s*1' `
    'BoxWeightB must update LoadingPoint_Status.Weight for PLCLocationCode 1.'

Assert-Contains $source 'await\s+SyncLoadingPointWeightsAsync\(_context,\s*plcConfig\)' `
    'UpdatePlcStatusAsync must sync loading point weights before saving the PLC status.'

Write-Host 'PLC status loading point weight sync checks passed.'
