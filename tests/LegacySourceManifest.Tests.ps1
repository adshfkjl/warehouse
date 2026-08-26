$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$verifier = Join-Path $repoRoot 'scripts/verify-legacy-source.ps1'
$pwsh = (Get-Process -Id $PID).Path
$testsRun = 0

function Assert-True {
    param([bool]$Condition, [string]$Message)

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-Equal {
    param([object]$Actual, [object]$Expected, [string]$Message)

    if ($Actual -ne $Expected) {
        throw "$Message Expected '$Expected', got '$Actual'."
    }
}

function New-Fixture {
    $fixture = Join-Path ([System.IO.Path]::GetTempPath()) ("legacy-source-manifest-" + [guid]::NewGuid().ToString('N'))
    $legacyRoot = Join-Path $fixture 'warehouse'
    $manifestPath = Join-Path $fixture 'manifest.sha256'
    New-Item -ItemType Directory -Path $legacyRoot -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $legacyRoot 'alpha.txt') -Value 'alpha' -NoNewline
    Set-Content -LiteralPath (Join-Path $legacyRoot 'nested.txt') -Value 'nested' -NoNewline
    return @{ Fixture = $fixture; LegacyRoot = $legacyRoot; ManifestPath = $manifestPath }
}

function Write-Manifest {
    param([hashtable]$Fixture)

    $records = Get-ChildItem -LiteralPath $Fixture.LegacyRoot -File -Recurse | Where-Object {
        $relativeDirectory = [System.IO.Path]::GetRelativePath($Fixture.LegacyRoot, $_.DirectoryName).Split([System.IO.Path]::DirectorySeparatorChar)
        -not ($relativeDirectory | Where-Object { $_ -in @('bin', 'obj') })
    } | ForEach-Object {
        $relativePath = [System.IO.Path]::GetRelativePath($Fixture.LegacyRoot, $_.FullName).Replace('\', '/')
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
        "$hash *$relativePath"
    } | Sort-Object
    @(
        '# Legacy source integrity manifest'
        '# Established: 2026-08-27'
        $records
    ) | Set-Content -LiteralPath $Fixture.ManifestPath -NoNewline:$false
}

function Invoke-Verifier {
    param([hashtable]$Fixture)

    $output = & $pwsh -NoProfile -File $verifier -LegacyRoot $Fixture.LegacyRoot -ManifestPath $Fixture.ManifestPath 2>&1
    return @{ ExitCode = $LASTEXITCODE; Output = ($output | Out-String) }
}

function Invoke-Test {
    param([string]$Name, [scriptblock]$Body)

    $script:testsRun++
    try {
        & $Body
        Write-Host "PASS: $Name"
    }
    catch {
        throw "FAIL: $Name`n$($_.Exception.Message)"
    }
}

Invoke-Test 'requires the verifier script to exist before exercising its contract' {
    Assert-True (Test-Path -LiteralPath $verifier -PathType Leaf) 'scripts/verify-legacy-source.ps1 must exist.'
}

Invoke-Test 'accepts an unchanged manifest and does not rewrite it' {
    $fixture = New-Fixture
    try {
        Write-Manifest $fixture
        $before = [System.IO.File]::ReadAllBytes($fixture.ManifestPath)
        $result = Invoke-Verifier $fixture
        Assert-Equal $result.ExitCode 0 "Expected unchanged fixture to verify. $($result.Output)"
        $after = [System.IO.File]::ReadAllBytes($fixture.ManifestPath)
        Assert-True ([System.Linq.Enumerable]::SequenceEqual[byte]($before, $after)) 'Verifier must not rewrite the manifest.'
    }
    finally {
        Remove-Item -LiteralPath $fixture.Fixture -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Invoke-Test 'fails when the manifest is missing' {
    $fixture = New-Fixture
    try {
        $result = Invoke-Verifier $fixture
        Assert-True ($result.ExitCode -ne 0) 'Missing manifest must fail verification.'
    }
    finally {
        Remove-Item -LiteralPath $fixture.Fixture -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Invoke-Test 'detects a changed file without rewriting the manifest' {
    $fixture = New-Fixture
    try {
        Write-Manifest $fixture
        $before = [System.IO.File]::ReadAllBytes($fixture.ManifestPath)
        Set-Content -LiteralPath (Join-Path $fixture.LegacyRoot 'alpha.txt') -Value 'changed' -NoNewline
        $result = Invoke-Verifier $fixture
        Assert-True ($result.ExitCode -ne 0) 'Changed file must fail verification.'
        Assert-True ([System.Linq.Enumerable]::SequenceEqual[byte]($before, [System.IO.File]::ReadAllBytes($fixture.ManifestPath))) 'Verifier must not rewrite manifest after a changed file.'
    }
    finally {
        Remove-Item -LiteralPath $fixture.Fixture -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Invoke-Test 'detects an added file' {
    $fixture = New-Fixture
    try {
        Write-Manifest $fixture
        Set-Content -LiteralPath (Join-Path $fixture.LegacyRoot 'added.txt') -Value 'added' -NoNewline
        $result = Invoke-Verifier $fixture
        Assert-True ($result.ExitCode -ne 0) 'Added file must fail verification.'
    }
    finally {
        Remove-Item -LiteralPath $fixture.Fixture -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Invoke-Test 'detects a deleted file' {
    $fixture = New-Fixture
    try {
        Write-Manifest $fixture
        Remove-Item -LiteralPath (Join-Path $fixture.LegacyRoot 'nested.txt') -Force
        $result = Invoke-Verifier $fixture
        Assert-True ($result.ExitCode -ne 0) 'Deleted file must fail verification.'
    }
    finally {
        Remove-Item -LiteralPath $fixture.Fixture -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Invoke-Test 'rejects malformed manifests and duplicate manifest paths' {
    $fixture = New-Fixture
    try {
        Set-Content -LiteralPath $fixture.ManifestPath -Value @(
            '# Legacy source integrity manifest'
            '# Established: 2026-08-27'
            'not-a-manifest-entry'
        )
        $malformed = Invoke-Verifier $fixture
        Assert-True ($malformed.ExitCode -ne 0) 'Malformed manifest must fail verification.'

        $hash = (Get-FileHash -LiteralPath (Join-Path $fixture.LegacyRoot 'alpha.txt') -Algorithm SHA256).Hash.ToLowerInvariant()
        Set-Content -LiteralPath $fixture.ManifestPath -Value @(
            '# Legacy source integrity manifest'
            '# Established: 2026-08-27'
            "$hash *alpha.txt"
            "$hash *alpha.txt"
        )
        $duplicate = Invoke-Verifier $fixture
        Assert-True ($duplicate.ExitCode -ne 0) 'Duplicate manifest path must fail verification.'
    }
    finally {
        Remove-Item -LiteralPath $fixture.Fixture -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Invoke-Test 'rejects reparse points and symbolic links' {
    $fixture = New-Fixture
    try {
        Write-Manifest $fixture
        $linkPath = Join-Path $fixture.LegacyRoot 'escape-link'
        try {
            New-Item -ItemType SymbolicLink -Path $linkPath -Target $fixture.Fixture -ErrorAction Stop | Out-Null
        }
        catch {
            New-Item -ItemType Junction -Path $linkPath -Target $fixture.Fixture -ErrorAction Stop | Out-Null
        }

        $result = Invoke-Verifier $fixture
        Assert-True ($result.ExitCode -ne 0) 'Reparse point, symbolic link, or junction must fail verification.'
    }
    finally {
        Remove-Item -LiteralPath $fixture.Fixture -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Invoke-Test 'excludes only directories named bin or obj' {
    $fixture = New-Fixture
    try {
        New-Item -ItemType Directory -Path (Join-Path $fixture.LegacyRoot 'bin') -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $fixture.LegacyRoot 'obj') -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $fixture.LegacyRoot 'binary') -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $fixture.LegacyRoot 'bin\generated.txt') -Value 'generated' -NoNewline
        Set-Content -LiteralPath (Join-Path $fixture.LegacyRoot 'obj\generated.txt') -Value 'generated' -NoNewline
        Set-Content -LiteralPath (Join-Path $fixture.LegacyRoot 'binary\kept.txt') -Value 'kept' -NoNewline
        Write-Manifest $fixture
        Set-Content -LiteralPath (Join-Path $fixture.LegacyRoot 'bin\generated.txt') -Value 'changed' -NoNewline
        Set-Content -LiteralPath (Join-Path $fixture.LegacyRoot 'obj\generated.txt') -Value 'changed' -NoNewline
        $excludedChanges = Invoke-Verifier $fixture
        Assert-Equal $excludedChanges.ExitCode 0 "Files below exact bin and obj directory names must be excluded. $($excludedChanges.Output)"

        Set-Content -LiteralPath (Join-Path $fixture.LegacyRoot 'binary\kept.txt') -Value 'changed' -NoNewline
        $keptChange = Invoke-Verifier $fixture
        Assert-True ($keptChange.ExitCode -ne 0) 'Directory names other than exact bin or obj must not be excluded.'
    }
    finally {
        Remove-Item -LiteralPath $fixture.Fixture -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "Legacy source manifest tests passed: $testsRun"
