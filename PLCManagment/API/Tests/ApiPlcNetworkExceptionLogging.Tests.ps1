$ErrorActionPreference = 'Stop'

$coreSource = Get-Content -Raw -Path (Join-Path $PSScriptRoot '..\..\Core\Services\ModbusClient.cs')
$managerSource = Get-Content -Raw -Path (Join-Path $PSScriptRoot '..\Services\PlcConnectionManager.cs')
$poolSource = Get-Content -Raw -Path (Join-Path $PSScriptRoot '..\Services\PlcConnectionPool.cs')

function Assert-Contains($Text, $Pattern, $Message) {
    if ($Text -notmatch $Pattern) {
        throw $Message
    }
}

Assert-Contains $coreSource '\*\*\*\*' `
    'API/Core ModbusClient network errors must include the searchable marker.'

Assert-Contains $coreSource 'LogNetworkException\(' `
    'API/Core ModbusClient must centralize marked network exception logging.'

Assert-Contains $coreSource 'operation' `
    'API/Core marked network log must include the operation name.'

Assert-Contains $coreSource 'IP=\{_ipAddress\}' `
    'API/Core marked network log must include PLC IP address.'

Assert-Contains $coreSource 'Port=\{_port\}' `
    'API/Core marked network log must include PLC port.'

Assert-Contains $coreSource '_suppressConnectionFailureLogging' `
    'API/Core ModbusClient must support suppressing repeated connection-failure logs.'

Assert-Contains $managerSource 'ConnectionFailureLogInterval' `
    'API connection manager must rate-limit repeated network exception logs.'

Assert-Contains $managerSource 'suppressConnectionFailureLogging: true' `
    'API connection manager must suppress duplicate ModbusClient connection-failure logs.'

Assert-Contains $managerSource 'ShouldLogConnectionFailure\(failureCount\)' `
    'API connection manager must only log repeated connection failures on the configured interval.'

Assert-Contains $poolSource 'suppressConnectionFailureLogging: true' `
    'API connection pool must suppress duplicate ModbusClient connection-failure logs.'

Assert-Contains $managerSource '\*\*\*\*' `
    'API connection manager must include the searchable marker when connection attempts fail.'

Write-Host 'API PLC network exception logging checks passed.'
