$ErrorActionPreference = 'Stop'

$programSource = Get-Content -Raw -Path (Join-Path $PSScriptRoot '..\Program.cs')
$managerSource = Get-Content -Raw -Path (Join-Path $PSScriptRoot '..\Services\PlcConnectionManager.cs')
$keepAliveSource = Get-Content -Raw -Path (Join-Path $PSScriptRoot '..\Services\PlcNetworkKeepAliveService.cs')

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

Assert-Contains $programSource 'AddHostedService<PlcNetworkKeepAliveService>' `
    'API must register the controlled PLC network keepalive hosted service.'

Assert-NotContains $programSource 'AddHostedService<KeepAliveService>' `
    'API must not register the legacy Timer-based KeepAliveService.'

Assert-NotContains $programSource 'AddHostedService<PlcHeartbeatService>' `
    'API must not register the legacy Timer-based PlcHeartbeatService.'

Assert-Contains $managerSource 'PlcConnectionUnavailableException' `
    'Connection manager must expose a fast-fail exception for PLCs in reconnect cooldown.'

Assert-Contains $managerSource 'CanAttemptConnection' `
    'Connection manager must check per-PLC reconnect cooldown before blocking on TCP connect.'

Assert-Contains $managerSource 'RecordConnectionFailure' `
    'Connection manager must record failures and open a per-PLC circuit breaker.'

Assert-Contains $managerSource 'GetConnectionSnapshot' `
    'Connection manager must expose PLC connection snapshots for background keepalive.'

Assert-Contains $keepAliveSource 'BackgroundService' `
    'PLC network keepalive must use BackgroundService.'

Assert-Contains $keepAliveSource 'PeriodicTimer' `
    'PLC network keepalive must use PeriodicTimer instead of System.Threading.Timer.'

Assert-Contains $keepAliveSource 'MaxDegreeOfParallelism' `
    'PLC network keepalive must bound concurrent PLC probes.'

Assert-NotContains $keepAliveSource 'async void' `
    'PLC network keepalive must not use async void callbacks.'

Write-Host 'API PLC connection resilience checks passed.'
