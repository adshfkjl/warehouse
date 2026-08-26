[CmdletBinding()]
param(
    [string]$ManifestPath,
    [string]$LegacyRoot
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path

if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = Join-Path $repoRoot 'docs/legacy-source-manifest.sha256'
}

if ([string]::IsNullOrWhiteSpace($LegacyRoot)) {
    $LegacyRoot = Join-Path $repoRoot 'warehouse'
}

function Test-PathWithinRoot {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Candidate
    )

    $normalizedRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    $normalizedCandidate = [System.IO.Path]::GetFullPath($Candidate)
    $prefix = $normalizedRoot + [System.IO.Path]::DirectorySeparatorChar
    return $normalizedCandidate.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)
}

function Test-ExcludedDirectoryName {
    param([Parameter(Mandatory = $true)][string]$Name)

    return $Name.Equals('bin', [System.StringComparison]::OrdinalIgnoreCase) -or
        $Name.Equals('obj', [System.StringComparison]::OrdinalIgnoreCase)
}

function Test-ReparsePointInRootPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $current = [System.IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrWhiteSpace($current)) {
        $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            return $true
        }

        $parent = [System.IO.Directory]::GetParent($current)
        if ($null -eq $parent -or $parent.FullName -eq $current) {
            break
        }
        $current = $parent.FullName
    }

    return $false
}

function Get-LegacyFiles {
    param([Parameter(Mandatory = $true)][string]$Root)

    $files = @{}
    $directories = [System.Collections.Generic.Stack[string]]::new()
    $directories.Push($Root)

    while ($directories.Count -gt 0) {
        $directory = $directories.Pop()
        foreach ($item in @(Get-ChildItem -LiteralPath $directory -Force -ErrorAction Stop)) {
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Legacy source contains a reparse point or symbolic link: $($item.FullName)"
            }

            $fullPath = [System.IO.Path]::GetFullPath($item.FullName)
            if (-not (Test-PathWithinRoot -Root $Root -Candidate $fullPath)) {
                throw "Legacy source path resolves outside the configured root: $($item.FullName)"
            }

            if ($item.PSIsContainer) {
                if (-not (Test-ExcludedDirectoryName -Name $item.Name)) {
                    $directories.Push($fullPath)
                }
                continue
            }

            $relativePath = [System.IO.Path]::GetRelativePath($Root, $fullPath).Replace('\', '/')
            if ($relativePath.StartsWith('../', [System.StringComparison]::Ordinal) -or $relativePath.Equals('..', [System.StringComparison]::Ordinal)) {
                throw "Legacy source path escaped the configured root: $($item.FullName)"
            }

            if ($files.ContainsKey($relativePath)) {
                throw "Legacy source contains duplicate relative path: $relativePath"
            }

            $files[$relativePath] = (Get-FileHash -Algorithm SHA256 -LiteralPath $fullPath).Hash.ToLowerInvariant()
        }
    }

    return $files
}

function Read-Manifest {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Legacy source manifest is missing: $Path"
    }

    $lines = @(Get-Content -LiteralPath $Path -ErrorAction Stop)
    if ($lines.Count -lt 3 -or $lines[0] -ne '# Legacy source integrity manifest' -or $lines[1] -notmatch '^# Established: \d{4}-\d{2}-\d{2}$') {
        throw 'Legacy source manifest header is missing or malformed.'
    }

    $entries = @{}
    $previousPath = $null
    for ($index = 2; $index -lt $lines.Count; $index++) {
        $line = $lines[$index]
        if ($line -notmatch '^([0-9A-Fa-f]{64}) \*(.+)$') {
            throw "Legacy source manifest has malformed entry at line $($index + 1)."
        }

        $hash = $matches[1].ToLowerInvariant()
        $relativePath = $matches[2]
        if ($relativePath -match '(^/|\\|:|//|(^|/)\.{1,2}(/|$))') {
            throw "Legacy source manifest has unsafe relative path at line $($index + 1)."
        }

        if ($entries.ContainsKey($relativePath)) {
            throw "Legacy source manifest has duplicate relative path: $relativePath"
        }
        if ($null -ne $previousPath -and [System.StringComparer]::Ordinal.Compare($previousPath, $relativePath) -ge 0) {
            throw "Legacy source manifest paths must be in strictly increasing ordinal order at line $($index + 1)."
        }

        $entries[$relativePath] = $hash
        $previousPath = $relativePath
    }

    return $entries
}

try {
    if (Test-ReparsePointInRootPath -Path $LegacyRoot) {
        throw "Legacy source root must not be reached through a reparse point or symbolic link: $LegacyRoot"
    }

    $legacyRootItem = Get-Item -LiteralPath $LegacyRoot -Force -ErrorAction Stop
    if (-not $legacyRootItem.PSIsContainer) {
        throw "Legacy source root is not a directory: $LegacyRoot"
    }
    if (($legacyRootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Legacy source root must not be a reparse point or symbolic link: $LegacyRoot"
    }

    $resolvedLegacyRoot = (Resolve-Path -LiteralPath $LegacyRoot -ErrorAction Stop).Path
    $manifest = Read-Manifest -Path $ManifestPath
    $actual = Get-LegacyFiles -Root $resolvedLegacyRoot

    $missing = @($manifest.Keys | Where-Object { -not $actual.ContainsKey($_) } | Sort-Object)
    $added = @($actual.Keys | Where-Object { -not $manifest.ContainsKey($_) } | Sort-Object)
    $changed = @($manifest.Keys | Where-Object { $actual.ContainsKey($_) -and $actual[$_] -ne $manifest[$_] } | Sort-Object)

    if ($missing.Count -gt 0 -or $added.Count -gt 0 -or $changed.Count -gt 0) {
        $details = @()
        if ($missing.Count -gt 0) { $details += "missing: $($missing -join ', ')" }
        if ($added.Count -gt 0) { $details += "added: $($added -join ', ')" }
        if ($changed.Count -gt 0) { $details += "changed: $($changed -join ', ')" }
        throw "Legacy source integrity verification failed: $($details -join '; ')"
    }

    Write-Host "Legacy source integrity verification passed: $($actual.Count) files match the manifest."
    exit 0
}
catch {
    Write-Error $_
    exit 1
}
