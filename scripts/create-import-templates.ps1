$templateDirectory = Join-Path (Get-Location) 'docs/templates'
New-Item -ItemType Directory -Force -Path $templateDirectory | Out-Null

function Add-ZipEntry($archive, [string]$name, [string]$contents) {
    $entry = $archive.CreateEntry($name)
    $writer = [System.IO.StreamWriter]::new($entry.Open(), [System.Text.UTF8Encoding]::new($false))
    try { $writer.Write($contents) } finally { $writer.Dispose() }
}

function New-ImportTemplate([string]$path, [string[]]$headers) {
    $rows = @(@('TemplateVersion', '1.0'), $headers)
    $shared = [System.Collections.Generic.List[string]]::new()
    foreach ($row in $rows) {
        foreach ($value in $row) {
            if (-not $shared.Contains($value)) { [void]$shared.Add($value) }
        }
    }

    $sheet = [System.Text.StringBuilder]::new()
    [void]$sheet.Append('<?xml version="1.0" encoding="UTF-8"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>')
    for ($rowIndex = 0; $rowIndex -lt $rows.Count; $rowIndex++) {
        [void]$sheet.Append('<row r="').Append($rowIndex + 1).Append('">')
        for ($columnIndex = 0; $columnIndex -lt $rows[$rowIndex].Count; $columnIndex++) {
            $reference = [char](65 + $columnIndex) + ($rowIndex + 1)
            $sharedIndex = $shared.IndexOf($rows[$rowIndex][$columnIndex])
            [void]$sheet.Append('<c r="').Append($reference).Append('" t="s"><v>').Append($sharedIndex).Append('</v></c>')
        }
        [void]$sheet.Append('</row>')
    }
    [void]$sheet.Append('</sheetData></worksheet>')

    $sharedXml = [System.Text.StringBuilder]::new()
    [void]$sharedXml.Append('<?xml version="1.0" encoding="UTF-8"?><sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" count="').Append($shared.Count).Append('" uniqueCount="').Append($shared.Count).Append('">')
    foreach ($value in $shared) {
        [void]$sharedXml.Append('<si><t>').Append([System.Security.SecurityElement]::Escape($value)).Append('</t></si>')
    }
    [void]$sharedXml.Append('</sst>')

    $file = [System.IO.File]::Open($path, [System.IO.FileMode]::Create)
    $archive = [System.IO.Compression.ZipArchive]::new($file, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        Add-ZipEntry $archive '[Content_Types].xml' '<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/></Types>'
        Add-ZipEntry $archive '_rels/.rels' '<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>'
        Add-ZipEntry $archive 'xl/workbook.xml' '<?xml version="1.0" encoding="UTF-8"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Import" sheetId="1" r:id="rId1"/></sheets></workbook>'
        Add-ZipEntry $archive 'xl/_rels/workbook.xml.rels' '<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings" Target="sharedStrings.xml"/></Relationships>'
        Add-ZipEntry $archive 'xl/worksheets/sheet1.xml' $sheet.ToString()
        Add-ZipEntry $archive 'xl/sharedStrings.xml' $sharedXml.ToString()
    } finally {
        $archive.Dispose()
        $file.Dispose()
    }
}

New-ImportTemplate (Join-Path $templateDirectory 'inbound-import.xlsx') @('OrderNumber', 'MaterialId', 'Quantity', 'BatchNumber', 'ExpirationDate', 'PalletCode', 'WeightKg')
New-ImportTemplate (Join-Path $templateDirectory 'outbound-import.xlsx') @('OrderNumber', 'MaterialId', 'Quantity', 'BatchNumber', 'PalletId', 'LocationId')
