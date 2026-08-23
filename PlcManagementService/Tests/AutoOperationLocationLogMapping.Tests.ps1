$ErrorActionPreference = 'Stop'

$source = Get-Content -Raw -Path (Join-Path $PSScriptRoot '..\PLCService.cs')

function Assert-Contains($Text, $Pattern, $Message) {
    if ($Text -notmatch $Pattern) {
        throw $Message
    }
}

Assert-Contains $source 'InboundShelf,\s*InboundPosition' `
    'Auto operation logs must include InboundShelf and InboundPosition columns so inbound completion can update LocationManagements.ShelfStatus.'

Assert-Contains $source 'InboundShelf\s*=\s*operationType\s*==\s*0\s*\?\s*bill\.Shelf\s*:\s*-1' `
    'Auto inbound logs must write bill.Shelf to InboundShelf.'

Assert-Contains $source 'InboundPosition\s*=\s*operationType\s*==\s*0\s*\?\s*bill\.Position\s*:\s*-1' `
    'Auto inbound logs must write bill.Position to InboundPosition.'

Assert-Contains $source 'OutboundShelf\s*=\s*operationType\s*==\s*1\s*\?\s*bill\.Shelf\s*:\s*-1' `
    'Auto outbound logs must write bill.Shelf to OutboundShelf only for outbound operations.'

Assert-Contains $source 'OutboundPosition\s*=\s*operationType\s*==\s*1\s*\?\s*bill\.Position\s*:\s*-1' `
    'Auto outbound logs must write bill.Position to OutboundPosition only for outbound operations.'

Write-Host 'Auto operation location log mapping checks passed.'
