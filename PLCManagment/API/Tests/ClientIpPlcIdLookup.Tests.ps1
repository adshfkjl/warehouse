$ErrorActionPreference = 'Stop'

$controllerSource = Get-Content -Raw -Path (Join-Path $PSScriptRoot '..\Controllers\PlcConfigurationsController.cs')
$modelSource = Get-Content -Raw -Path (Join-Path $PSScriptRoot '..\Models\PlcConfiguration.cs')

function Assert-Contains($Text, $Pattern, $Message) {
    if ($Text -notmatch $Pattern) {
        throw $Message
    }
}

Assert-Contains $modelSource '\[Column\("IpAddress_PC"\)\]' `
    'PlcConfiguration must map the IpAddress_PC database column.'

Assert-Contains $modelSource 'string\?\s+IpAddressPC\s*{\s*get;\s*set;\s*}' `
    'PlcConfiguration must expose IpAddressPC for the client PC IP address.'

Assert-Contains $controllerSource 'ApplicationDbContext' `
    'PlcConfigurationsController must use ApplicationDbContext for client IP lookup.'

Assert-Contains $controllerSource '\[HttpGet\("current-plc-id"\)\]' `
    'PlcConfigurationsController must expose GET api/PlcConfigurations/current-plc-id.'

Assert-Contains $controllerSource 'RemoteIpAddress' `
    'The current PLC lookup endpoint must read the client IP from HttpContext.Connection.RemoteIpAddress.'

Assert-Contains $controllerSource 'MapToIPv4' `
    'The current PLC lookup endpoint must normalize IPv4-mapped IPv6 client addresses.'

Assert-Contains $controllerSource 'IpAddressPC\s*==\s*clientIp' `
    'The current PLC lookup endpoint must match PlcConfigurations.IpAddress_PC to the detected client IP.'

Assert-Contains $controllerSource 'Select\(p\s*=>\s*p\.PlcId\)' `
    'The current PLC lookup endpoint must return only the matching PlcId.'

Assert-Contains $controllerSource 'plcId\s*=\s*"-1"' `
    'The current PLC lookup endpoint must return "-1" in the plcId field when no PlcId matches the detected client IP.'

Write-Host 'Client IP PLC ID lookup checks passed.'
