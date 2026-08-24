[CmdletBinding()]
param(
    [int]$HealthPort = 5089,
    [int]$HealthTimeoutSeconds = 30
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Set-Location $repoRoot

$blocked = [System.Collections.Generic.List[string]]::new()
$apiProcess = $null
$apiLog = Join-Path ([System.IO.Path]::GetTempPath()) "warehouse-wms-api-$PID.log"
$apiErrorLog = Join-Path ([System.IO.Path]::GetTempPath()) "warehouse-wms-api-$PID.err.log"

function Invoke-RequiredCommand {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$ArgumentList,
        [Parameter(Mandatory = $true)][string]$Description
    )

    Write-Host "RUN: $FilePath $($ArgumentList -join ' ')"
    & $FilePath @ArgumentList
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "$Description failed with exit code $exitCode."
    }
}

function Add-Blocked {
    param([Parameter(Mandatory = $true)][string]$Reason)

    $blocked.Add($Reason)
    Write-Warning "BLOCKED: $Reason"
}

function Test-ExternalCommand {
    param([Parameter(Mandatory = $true)][string]$Name)

    return $null -ne (Get-Command $Name -ErrorAction SilentlyContinue)
}

try {
    # STEP 1 - restore
    Write-Host "STEP 1 - restore"
    Invoke-RequiredCommand dotnet @("restore", "Warehouse.Wms.sln") "dotnet restore"

    # STEP 2 - build
    Write-Host "STEP 2 - build"
    Invoke-RequiredCommand dotnet @("build", "Warehouse.Wms.sln", "--no-restore") "dotnet build"

    # STEP 3 - test
    Write-Host "STEP 3 - test"
    Invoke-RequiredCommand dotnet @("test", "Warehouse.Wms.sln", "--no-build", "--no-restore", "--logger", "console;verbosity=minimal") "dotnet test"

    # STEP 4 - migration
    Write-Host "STEP 4 - migration"
    $migrationDirectory = Join-Path $repoRoot "src/Warehouse.Wms.Infrastructure/Migrations"
    $hasMigrations = (Test-Path $migrationDirectory -PathType Container) -and (@(Get-ChildItem $migrationDirectory -Filter "*.cs" -File -ErrorAction SilentlyContinue).Count -gt 0)
    if (-not $hasMigrations) {
        Write-Host "Migration checks not applicable: no EF Core migrations exist in the current scaffold."
    }
    else {
        $hasDocker = Test-ExternalCommand "docker"
        $hasEf = Test-ExternalCommand "dotnet-ef"

        if (-not $hasDocker) {
            Add-Blocked "Docker CLI is not installed; local database and migration checks cannot run."
        }
        else {
            & docker info --format "{{.ServerVersion}}" *> $null
            if ($LASTEXITCODE -ne 0) {
                Add-Blocked "Docker daemon is unavailable; local database and migration checks cannot run."
            }
        }

        if (-not $hasEf) {
            Add-Blocked "dotnet-ef is not installed; database migration command cannot run."
        }

        if ($blocked.Count -eq 0) {
            Invoke-RequiredCommand docker @("compose", "-f", "docker-compose.dev.yml", "config") "docker compose config"
            Invoke-RequiredCommand docker @("compose", "-f", "docker-compose.dev.yml", "up", "-d") "docker compose up"
            $env:ConnectionStrings__WmsDb = "Server=127.0.0.1,14333;Database=WmsIntegrationTest;User Id=sa;Password=WmsDevOnly!123;TrustServerCertificate=True"
            Invoke-RequiredCommand dotnet-ef @("database", "update", "--project", "src/Warehouse.Wms.Infrastructure", "--startup-project", "src/Warehouse.Wms.Api") "dotnet ef database update"
        }
        else {
            Write-Host "Migration execution skipped because prerequisites are BLOCKED."
        }
    }

    # STEP 5 - health
    Write-Host "STEP 5 - health"
    $healthUri = "http://127.0.0.1:$HealthPort"
    $apiArguments = @(
        "run", "--project", "src/Warehouse.Wms.Api", "--no-build", "--no-restore",
        "--no-launch-profile", "--urls", $healthUri
    )
    $apiProcess = Start-Process -FilePath "dotnet" -ArgumentList $apiArguments -WorkingDirectory $repoRoot -PassThru -WindowStyle Hidden -RedirectStandardOutput $apiLog -RedirectStandardError $apiErrorLog
    $deadline = [DateTime]::UtcNow.AddSeconds($HealthTimeoutSeconds)
    $healthy = $false
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($apiProcess.HasExited) {
            $details = if (Test-Path $apiErrorLog) { Get-Content -Raw $apiErrorLog } else { "no API error output" }
            throw "API exited before health checks completed. $details"
        }

        try {
            $live = Invoke-WebRequest -Uri "$healthUri/health/live" -UseBasicParsing -TimeoutSec 2
            $ready = Invoke-WebRequest -Uri "$healthUri/health/ready" -UseBasicParsing -TimeoutSec 2
            if ($live.StatusCode -eq 200 -and $ready.StatusCode -eq 200) {
                $healthy = $true
                break
            }
        }
        catch {
            Start-Sleep -Milliseconds 500
        }
    }

    if (-not $healthy) {
        throw "Health checks did not return HTTP 200 within $HealthTimeoutSeconds seconds."
    }
    Write-Host "Health checks passed: /health/live and /health/ready returned HTTP 200."

    # STEP 6 - warehouse-protection
    Write-Host "STEP 6 - warehouse-protection"
    $legacyDiff = (& git diff --name-only -- warehouse | Out-String).Trim()
    if ($legacyDiff) {
        throw "Legacy warehouse directory has tracked changes: $legacyDiff"
    }
    Write-Host "Legacy warehouse protection passed: git diff --name-only -- warehouse is empty."
}
catch {
    Write-Error $_
    exit 1
}
finally {
    if ($null -ne $apiProcess -and -not $apiProcess.HasExited) {
        Stop-Process -Id $apiProcess.Id -Force -ErrorAction SilentlyContinue
        $apiProcess.WaitForExit()
    }
    Remove-Item $apiLog, $apiErrorLog -Force -ErrorAction SilentlyContinue
}

if ($blocked.Count -gt 0) {
    Write-Warning "Quality gate completed with BLOCKED prerequisites:"
    $blocked | ForEach-Object { Write-Warning "- $_" }
    exit 2
}

Write-Host "Quality gate passed."
exit 0
