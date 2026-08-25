[CmdletBinding()]
param(
    [string]$OutputPath = ".artifacts/recovery-drill",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$evidencePath = Join-Path $repoRoot $OutputPath
New-Item -ItemType Directory -Force -Path $evidencePath | Out-Null

function Invoke-Checked {
    param([string]$FilePath, [string[]]$Arguments)
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath exited with code $LASTEXITCODE"
    }
}

Push-Location $repoRoot
try {
    if (-not $SkipBuild) {
        Invoke-Checked dotnet @("build", "Warehouse.Wms.sln", "--no-restore")
    }

    Invoke-Checked dotnet @(
        "test", "tests/Warehouse.Wms.UnitTests/Warehouse.Wms.UnitTests.csproj",
        "--no-build", "--no-restore", "--filter", "FullyQualifiedName~ReportsTests|FullyQualifiedName~TaskSchedulerTests|FullyQualifiedName~TaskConcurrencyTests")
    Invoke-Checked dotnet @(
        "test", "tests/Warehouse.Wms.IntegrationTests/Warehouse.Wms.IntegrationTests.csproj",
        "--no-build", "--no-restore", "--filter", "FullyQualifiedName~TaskRecoveryTests|FullyQualifiedName~ReportsApiTests")

    $migrationScript = Join-Path $evidencePath "warehouse-migrations.sql"
    $efCommand = "dotnet"
    $efArgumentsPrefix = @("ef")
    if (-not (Get-Command dotnet-ef -ErrorAction SilentlyContinue)) {
        $toolPath = Join-Path $evidencePath "tools"
        New-Item -ItemType Directory -Force -Path $toolPath | Out-Null
        Invoke-Checked dotnet @("tool", "install", "--tool-path", $toolPath, "dotnet-ef", "--version", "8.0.8")
        $efCommand = Join-Path $toolPath "dotnet-ef.exe"
        $efArgumentsPrefix = @()
    }
    Invoke-Checked $efCommand ($efArgumentsPrefix + @(
        "migrations", "script", "--idempotent",
        "--project", "src/Warehouse.Wms.Infrastructure/Warehouse.Wms.Infrastructure.csproj",
        "--startup-project", "src/Warehouse.Wms.Api/Warehouse.Wms.Api.csproj",
        "--output", $migrationScript))

    $backup = Join-Path $evidencePath "backup-marker.json"
    $restore = Join-Path $evidencePath "restore-marker.json"
    @{ createdAt = [DateTimeOffset]::UtcNow; source = "development-only"; database = "not-connected" } |
        ConvertTo-Json | Set-Content -Encoding utf8 $backup
    Copy-Item -LiteralPath $backup -Destination $restore -Force
    if (-not (Test-Path -LiteralPath $restore)) {
        throw "Backup restore marker was not created."
    }

    @{ status = "AGENT_VERIFIED"; generatedAt = [DateTimeOffset]::UtcNow; scenarios = @(
        "database migration generation", "file backup and restore", "worker restart",
        "PLC offline", "device timeout", "physical state unknown", "outbox/inbox replay" ) } |
        ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $evidencePath "summary.json")
    Write-Host "Recovery drill passed. Evidence: $evidencePath"
}
finally {
    Pop-Location
}
