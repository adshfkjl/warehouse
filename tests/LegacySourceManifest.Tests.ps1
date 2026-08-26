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

    $records = [System.Collections.Generic.List[object]]::new()
    Get-ChildItem -LiteralPath $Fixture.LegacyRoot -File -Recurse | Where-Object {
        $relativeDirectory = [System.IO.Path]::GetRelativePath($Fixture.LegacyRoot, $_.DirectoryName).Split([System.IO.Path]::DirectorySeparatorChar)
        -not ($relativeDirectory | Where-Object { $_ -in @('bin', 'obj') })
    } | ForEach-Object {
        $relativePath = [System.IO.Path]::GetRelativePath($Fixture.LegacyRoot, $_.FullName).Replace('\', '/')
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
        $records.Add([pscustomobject]@{ RelativePath = $relativePath; Record = "$hash *$relativePath" })
    }
    $records.Sort([System.Comparison[object]] {
        param($left, $right)
        return [System.StringComparer]::Ordinal.Compare([string]$left.RelativePath, [string]$right.RelativePath)
    })
    @(
        '# Legacy source integrity manifest'
        '# Established: 2026-08-27'
        $records | ForEach-Object { $_.Record }
    ) | Set-Content -LiteralPath $Fixture.ManifestPath -NoNewline:$false
}

function Assert-ManifestUnchanged {
    param([byte[]]$Before, [string]$ManifestPath, [string]$Message)

    Assert-True ([System.Linq.Enumerable]::SequenceEqual[byte]($Before, [System.IO.File]::ReadAllBytes($ManifestPath))) $Message
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
        Assert-ManifestUnchanged $before $fixture.ManifestPath 'Verifier must not rewrite the manifest.'
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
        Assert-ManifestUnchanged $before $fixture.ManifestPath 'Verifier must not rewrite manifest after a changed file.'
    }
    finally {
        Remove-Item -LiteralPath $fixture.Fixture -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Invoke-Test 'detects an added file' {
    $fixture = New-Fixture
    try {
        Write-Manifest $fixture
        $before = [System.IO.File]::ReadAllBytes($fixture.ManifestPath)
        Set-Content -LiteralPath (Join-Path $fixture.LegacyRoot 'added.txt') -Value 'added' -NoNewline
        $result = Invoke-Verifier $fixture
        Assert-True ($result.ExitCode -ne 0) 'Added file must fail verification.'
        Assert-ManifestUnchanged $before $fixture.ManifestPath 'Verifier must not rewrite manifest after an added file.'
    }
    finally {
        Remove-Item -LiteralPath $fixture.Fixture -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Invoke-Test 'detects a deleted file' {
    $fixture = New-Fixture
    try {
        Write-Manifest $fixture
        $before = [System.IO.File]::ReadAllBytes($fixture.ManifestPath)
        Remove-Item -LiteralPath (Join-Path $fixture.LegacyRoot 'nested.txt') -Force
        $result = Invoke-Verifier $fixture
        Assert-True ($result.ExitCode -ne 0) 'Deleted file must fail verification.'
        Assert-ManifestUnchanged $before $fixture.ManifestPath 'Verifier must not rewrite manifest after a deleted file.'
    }
    finally {
        Remove-Item -LiteralPath $fixture.Fixture -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Invoke-Test 'rejects malformed manifests without rewriting the manifest' {
    $fixture = New-Fixture
    try {
        Set-Content -LiteralPath $fixture.ManifestPath -Value @(
            '# Legacy source integrity manifest'
            '# Established: 2026-08-27'
            'not-a-manifest-entry'
        )
        $before = [System.IO.File]::ReadAllBytes($fixture.ManifestPath)
        $malformed = Invoke-Verifier $fixture
        Assert-True ($malformed.ExitCode -ne 0) 'Malformed manifest must fail verification.'
        Assert-ManifestUnchanged $before $fixture.ManifestPath 'Verifier must not rewrite a malformed manifest.'
    }
    finally {
        Remove-Item -LiteralPath $fixture.Fixture -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Invoke-Test 'rejects duplicate manifest paths without rewriting the manifest' {
    $fixture = New-Fixture
    try {
        $hash = (Get-FileHash -LiteralPath (Join-Path $fixture.LegacyRoot 'alpha.txt') -Algorithm SHA256).Hash.ToLowerInvariant()
        Set-Content -LiteralPath $fixture.ManifestPath -Value @(
            '# Legacy source integrity manifest'
            '# Established: 2026-08-27'
            "$hash *alpha.txt"
            "$hash *alpha.txt"
        )
        $before = [System.IO.File]::ReadAllBytes($fixture.ManifestPath)
        $duplicate = Invoke-Verifier $fixture
        Assert-True ($duplicate.ExitCode -ne 0) 'Duplicate manifest path must fail verification.'
        Assert-ManifestUnchanged $before $fixture.ManifestPath 'Verifier must not rewrite a duplicate-path manifest.'
    }
    finally {
        Remove-Item -LiteralPath $fixture.Fixture -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Invoke-Test 'rejects reparse points and symbolic links' {
    $fixture = New-Fixture
    try {
        Write-Manifest $fixture
        $before = [System.IO.File]::ReadAllBytes($fixture.ManifestPath)
        $linkPath = Join-Path $fixture.LegacyRoot 'escape-link'
        try {
            New-Item -ItemType SymbolicLink -Path $linkPath -Target $fixture.Fixture -ErrorAction Stop | Out-Null
        }
        catch {
            New-Item -ItemType Junction -Path $linkPath -Target $fixture.Fixture -ErrorAction Stop | Out-Null
        }

        $result = Invoke-Verifier $fixture
        Assert-True ($result.ExitCode -ne 0) 'Reparse point, symbolic link, or junction must fail verification.'
        Assert-ManifestUnchanged $before $fixture.ManifestPath 'Verifier must not rewrite manifest after a reparse point is found.'
    }
    finally {
        Remove-Item -LiteralPath $fixture.Fixture -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Invoke-Test 'writes manifest records in ordinal relative-path order' {
    $fixture = New-Fixture
    try {
        1..16 | ForEach-Object {
            $name = "path-$($_.ToString('D2')).txt"
            Set-Content -LiteralPath (Join-Path $fixture.LegacyRoot $name) -Value ([guid]::NewGuid().ToString('N')) -NoNewline
        }
        Write-Manifest $fixture
        $paths = @(Get-Content -LiteralPath $fixture.ManifestPath | Select-Object -Skip 2 | ForEach-Object { ($_ -split ' \*', 2)[1] })
        $expected = [System.Collections.Generic.List[string]]::new()
        $paths | ForEach-Object { $expected.Add($_) }
        $expected.Sort([System.StringComparer]::Ordinal)
        Assert-Equal ($paths -join "`n") ($expected -join "`n") 'Manifest records must be sorted by relative path, not by hash.'
    }
    finally {
        Remove-Item -LiteralPath $fixture.Fixture -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Invoke-Test 'rejects out-of-order manifest paths without rewriting the manifest' {
    $fixture = New-Fixture
    try {
        Write-Manifest $fixture
        $lines = @(Get-Content -LiteralPath $fixture.ManifestPath)
        @($lines[0], $lines[1], $lines[3], $lines[2]) | Set-Content -LiteralPath $fixture.ManifestPath
        $before = [System.IO.File]::ReadAllBytes($fixture.ManifestPath)
        $result = Invoke-Verifier $fixture
        Assert-True ($result.ExitCode -ne 0) 'Out-of-order manifest paths must fail verification.'
        Assert-ManifestUnchanged $before $fixture.ManifestPath 'Verifier must not rewrite an out-of-order manifest.'
    }
    finally {
        Remove-Item -LiteralPath $fixture.Fixture -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Invoke-Test 'rejects unsafe manifest paths without rewriting the manifest' {
    $fixture = New-Fixture
    try {
        $hash = (Get-FileHash -LiteralPath (Join-Path $fixture.LegacyRoot 'alpha.txt') -Algorithm SHA256).Hash.ToLowerInvariant()
        foreach ($path in @('../outside.txt', '/outside.txt', 'C:/outside.txt', 'nested/./alpha.txt', 'nested\\alpha.txt')) {
            @(
                '# Legacy source integrity manifest'
                '# Established: 2026-08-27'
                "$hash *$path"
            ) | Set-Content -LiteralPath $fixture.ManifestPath
            $before = [System.IO.File]::ReadAllBytes($fixture.ManifestPath)
            $result = Invoke-Verifier $fixture
            Assert-True ($result.ExitCode -ne 0) "Unsafe manifest path '$path' must fail verification. $($result.Output)"
            Assert-ManifestUnchanged $before $fixture.ManifestPath "Verifier must not rewrite unsafe manifest path '$path'."
        }
    }
    finally {
        Remove-Item -LiteralPath $fixture.Fixture -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Invoke-Test 'rejects a legacy root reached through a reparse-point parent' {
    $fixture = New-Fixture
    try {
        $targetParent = Join-Path $fixture.Fixture 'target-parent'
        New-Item -ItemType Directory -Path $targetParent -Force | Out-Null
        Move-Item -LiteralPath $fixture.LegacyRoot -Destination $targetParent
        $linkedParent = Join-Path $fixture.Fixture 'linked-parent'
        New-Item -ItemType Junction -Path $linkedParent -Target $targetParent -ErrorAction Stop | Out-Null
        $fixture.LegacyRoot = Join-Path $linkedParent 'warehouse'
        Write-Manifest $fixture
        $before = [System.IO.File]::ReadAllBytes($fixture.ManifestPath)
        $result = Invoke-Verifier $fixture
        Assert-True ($result.ExitCode -ne 0) 'A legacy root reached through a reparse-point parent must fail verification.'
        Assert-ManifestUnchanged $before $fixture.ManifestPath 'Verifier must not rewrite manifest when legacy root has a reparse-point parent.'
    }
    finally {
        Remove-Item -LiteralPath $fixture.Fixture -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Invoke-Test 'rejects a legacy root that is itself a reparse point without rewriting the manifest' {
    $fixture = New-Fixture
    try {
        $targetRoot = Join-Path $fixture.Fixture 'target-root'
        Move-Item -LiteralPath $fixture.LegacyRoot -Destination $targetRoot
        $linkedRoot = Join-Path $fixture.Fixture 'linked-root'
        try {
            New-Item -ItemType SymbolicLink -Path $linkedRoot -Target $targetRoot -ErrorAction Stop | Out-Null
        }
        catch {
            New-Item -ItemType Junction -Path $linkedRoot -Target $targetRoot -ErrorAction Stop | Out-Null
        }
        $fixture.LegacyRoot = $linkedRoot
        Write-Manifest $fixture
        $before = [System.IO.File]::ReadAllBytes($fixture.ManifestPath)
        $result = Invoke-Verifier $fixture
        Assert-True ($result.ExitCode -ne 0) 'A legacy root that is itself a symbolic link or junction must fail verification.'
        Assert-ManifestUnchanged $before $fixture.ManifestPath 'Verifier must not rewrite manifest when legacy root itself is a reparse point.'
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

        $before = [System.IO.File]::ReadAllBytes($fixture.ManifestPath)
        Set-Content -LiteralPath (Join-Path $fixture.LegacyRoot 'binary\kept.txt') -Value 'changed' -NoNewline
        $keptChange = Invoke-Verifier $fixture
        Assert-True ($keptChange.ExitCode -ne 0) 'Directory names other than exact bin or obj must not be excluded.'
        Assert-ManifestUnchanged $before $fixture.ManifestPath 'Verifier must not rewrite manifest after a binary directory file changes.'
    }
    finally {
        Remove-Item -LiteralPath $fixture.Fixture -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "Legacy source manifest tests passed: $testsRun"
