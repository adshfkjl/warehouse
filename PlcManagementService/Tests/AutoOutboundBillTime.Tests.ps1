$ErrorActionPreference = 'Stop'

$source = Get-Content -Raw -Path (Join-Path $PSScriptRoot '..\PLCService.cs')

function Assert-Contains($Text, $Pattern, $Message) {
    if ($Text -notmatch $Pattern) {
        throw $Message
    }
}

Assert-Contains $source 'if\s*\(\s*operationType\s*==\s*1\s*\)[\s\S]*OperationID=\{operationId\}' `
    'Auto outbound must defer BillDetail.OutBoundTime updates until the outbound completion monitor observes PLC completion.'

Assert-Contains $source 'StartAutoOutboundBillDetailCompletionMonitor\(\s*plc\.PlcId,\s*bill,\s*loadingPoint,\s*operationWeight\s*\)' `
    'Auto outbound logging must start a BillDetail completion monitor with the original bill context.'

Assert-Contains $source 'observedOutboundProgress[\s\S]*plcStatus\.OperationResult\s*!=\s*0[\s\S]*observedOutboundProgress\s*=\s*true' `
    'Auto outbound completion monitor must not treat the initial idle OperationResult=0 as completion.'

Write-Host 'Auto outbound BillDetail time checks passed.'
