$ErrorActionPreference = 'Stop'

$source = Get-Content -Raw -Path (Join-Path $PSScriptRoot '..\Services\DatabaseService.cs')

function Assert-Contains($Text, $Pattern, $Message) {
    if ($Text -notmatch $Pattern) {
        throw $Message
    }
}

Assert-Contains $source 'LoadingPoint_Status' 'UpdatePlcStatus must update LoadingPoint_Status.'
Assert-Contains $source 'PLCID\s*=\s*@PlcId' 'LoadingPoint_Status update must match the same PLCID.'
Assert-Contains $source 'PLCLocationCode\s*=\s*0' 'BoxWeightA must update PLCLocationCode 0.'
Assert-Contains $source 'PLCLocationCode\s*=\s*1' 'BoxWeightB must update PLCLocationCode 1.'
Assert-Contains $source '@BoxWeightA' 'LoadingPoint_Status update must use BoxWeightA.'
Assert-Contains $source '@BoxWeightB' 'LoadingPoint_Status update must use BoxWeightB.'

Write-Host 'DatabaseService weight sync checks passed.'
