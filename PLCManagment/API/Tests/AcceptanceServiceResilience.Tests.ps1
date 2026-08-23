$ErrorActionPreference = 'Stop'

$root = Join-Path $PSScriptRoot '..'
$servicePath = Join-Path $root 'Services\AcceptanceService.cs'

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

if (-not (Test-Path -LiteralPath $servicePath)) {
    throw 'AcceptanceService.cs must exist.'
}

$service = Get-Content -Raw -Path $servicePath

Assert-Contains $service '\[dbo\]\.\[WX_GetTYByMO_NO\]' `
    'Acceptance read must call the schema-qualified WX_GetTYByMO_NO procedure.'
Assert-Contains $service '\[dbo\]\.\[WX_SaveTYInfo\]' `
    'Acceptance save must call the schema-qualified WX_SaveTYInfo procedure.'
Assert-Contains $service 'ReadAcceptanceInfo\(reader\)' `
    'Acceptance procedure rows must be mapped through the shared safe reader.'
Assert-Contains $service 'GetOrdinalOrMissing' `
    'Acceptance reader must tolerate result sets that omit optional columns.'
Assert-Contains $service 'INullable\s*\{\s*IsNull:\s*true\s*\}' `
    'Acceptance reader must tolerate SQL null value wrappers.'
Assert-Contains $service 'AddVarCharParameter\(command, "@MO_NO", moNo, 50\)' `
    'MO_NO must be passed with an explicit SQL type and length.'
Assert-Contains $service 'AddDecimalParameter\(command, "@QTY_OK", request\.QTY_OK\)' `
    'QTY_OK must be passed with an explicit decimal SQL type.'
Assert-Contains $service 'AddDecimalParameter\(command, "@QTY_LOST", request\.QTY_LOST\)' `
    'QTY_LOST must be passed with an explicit decimal SQL type.'
Assert-NotContains $service 'AddWithValue' `
    'AcceptanceService must not use AddWithValue for stored procedure parameters.'
Assert-NotContains $service 'reader\["QTY_' `
    'Acceptance quantity fields must not be read directly from SqlDataReader.'
Assert-NotContains $service 'Convert\.ToDecimal\(reader' `
    'Acceptance decimal conversions must go through SafeGetDecimal.'

Write-Host 'Acceptance service resilience checks passed.'
