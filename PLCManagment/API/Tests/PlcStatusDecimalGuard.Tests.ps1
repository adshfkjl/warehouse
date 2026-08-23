$ErrorActionPreference = 'Stop'

$source = Get-Content -Raw -Path (Join-Path $PSScriptRoot '..\Services\PlcService.cs')

function Assert-Contains($Text, $Pattern, $Message) {
    if ($Text -notmatch $Pattern) {
        throw $Message
    }
}

Assert-Contains $source 'EnsurePlcStatusDecimals' `
    'PLC status updates must sanitize all decimal fields before saving.'

Assert-Contains $source 'plcConfig\.PosX\s*=\s*EnsureDbDecimalRange\(plcConfig\.PosX' `
    'PosX must be constrained to the database decimal range.'

Assert-Contains $source 'plcConfig\.PosY\s*=\s*EnsureDbDecimalRange\(plcConfig\.PosY' `
    'PosY must be constrained to the database decimal range.'

Assert-Contains $source 'plcConfig\.PosZ\s*=\s*EnsureDbDecimalRange\(plcConfig\.PosZ' `
    'PosZ must be constrained to the database decimal range.'

Assert-Contains $source 'catch \(Exception ex\)[\s\S]*EnsurePlcStatusDecimals\(plcConfig\);[\s\S]*await _context\.SaveChangesAsync\(\);' `
    'UpdatePlcStatusAsync catch block must sanitize decimal fields before saving the error state.'

Write-Host 'PLC status decimal guard checks passed.'
