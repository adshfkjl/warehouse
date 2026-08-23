$ErrorActionPreference = 'Stop'

$source = Get-Content -Raw -Path (Join-Path $PSScriptRoot '..\Services\ModbusService.cs')

function Assert-Contains($Text, $Pattern, $Message) {
    if ($Text -notmatch $Pattern) {
        throw $Message
    }
}

Assert-Contains $source '\*\*\*\*' `
    'Windows service PLC network errors must include the searchable marker.'

Assert-Contains $source 'LogNetworkException\(' `
    'Windows service ModbusService must centralize marked network exception logging.'

Assert-Contains $source 'operation' `
    'Marked network log must include the operation name.'

Assert-Contains $source 'IP=\{_plc\.IpAddress\}' `
    'Marked network log must include PLC IP address.'

Assert-Contains $source 'Port=\{_plc\.Port\}' `
    'Marked network log must include PLC port.'

Write-Host 'PLC network exception logging checks passed.'
