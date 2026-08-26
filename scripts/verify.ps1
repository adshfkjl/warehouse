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

function Resolve-DockerCommand {
    $command = Get-Command docker -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $candidates = @(
        (Join-Path ${env:ProgramFiles} "Docker\Docker\resources\bin\docker.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Docker\Docker\resources\bin\docker.exe")
    )
    return $candidates | Where-Object { Test-Path $_ -PathType Leaf } | Select-Object -First 1
}

function Wait-ForSqlServer {
    param(
        [Parameter(Mandatory = $true)][string]$DockerCommand,
        [int]$TimeoutSeconds = 120
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        & $DockerCommand exec warehouse-wms-test-db /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "WmsDevOnly!123" -C -Q "SET NOCOUNT ON; SELECT 1" *> $null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "SQL Server container is ready for client connections."
            return
        }

        Start-Sleep -Seconds 2
    }

    throw "SQL Server container did not become ready within $TimeoutSeconds seconds."
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

    # STEP 4 - legacy-source-integrity
    Write-Host "STEP 4 - legacy-source-integrity"
    Invoke-RequiredCommand pwsh @("-NoProfile", "-File", "scripts/verify-legacy-source.ps1") "legacy source integrity verification"

    # STEP 5 - migration
    Write-Host "STEP 5 - migration"
    $migrationDirectory = Join-Path $repoRoot "src/Warehouse.Wms.Infrastructure/Migrations"
    $hasMigrations = (Test-Path $migrationDirectory -PathType Container) -and (@(Get-ChildItem $migrationDirectory -Filter "*.cs" -File -ErrorAction SilentlyContinue).Count -gt 0)
    if (-not $hasMigrations) {
        Write-Host "Migration checks not applicable: no EF Core migrations exist in the current scaffold."
    }
    else {
        $dockerCommand = Resolve-DockerCommand
        $hasDocker = -not [string]::IsNullOrWhiteSpace($dockerCommand)
        $hasEf = Test-ExternalCommand "dotnet-ef"

        if (-not $hasDocker) {
            Add-Blocked "Docker CLI was not found in PATH or standard Docker Desktop locations; local database and migration checks cannot run."
        }
        else {
            & $dockerCommand info --format "{{.ServerVersion}}" *> $null
            if ($LASTEXITCODE -ne 0) {
                Add-Blocked "Docker daemon is unavailable; local database and migration checks cannot run."
            }
        }

        if (-not $hasEf) {
            Add-Blocked "dotnet-ef is not installed; database migration command cannot run."
        }

        if ($blocked.Count -eq 0) {
            Invoke-RequiredCommand $dockerCommand @("compose", "-f", "docker-compose.dev.yml", "config") "docker compose config"
            Invoke-RequiredCommand $dockerCommand @("compose", "-f", "docker-compose.dev.yml", "up", "-d") "docker compose up"
            Wait-ForSqlServer -DockerCommand $dockerCommand
            $env:ConnectionStrings__WmsDb = "Server=127.0.0.1,14333;Database=WmsIntegrationTest;User Id=sa;Password=WmsDevOnly!123;TrustServerCertificate=True"
            # The API defaults to Production when launched without a profile.
            # Use the local SQL persistence mode explicitly for the health gate;
            # production still requires the same explicit setting at deployment.
            $env:Wms__PersistenceMode = "SqlServer"
            Invoke-RequiredCommand dotnet-ef @("database", "update", "--project", "src/Warehouse.Wms.Infrastructure", "--startup-project", "src/Warehouse.Wms.Api") "dotnet ef database update"
        }
        else {
            Write-Host "Migration execution skipped because prerequisites are BLOCKED."
        }
    }

    # STEP 6 - health
    Write-Host "STEP 6 - health"
    # The verification host exercises the dependency-free development composition.
    # Production configuration remains validated by the API startup guards.
    $env:ASPNETCORE_ENVIRONMENT = "Development"
    $env:Wms__PersistenceMode = "InMemory"
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

    # STEP 7 - warehouse-protection
    Write-Host "STEP 7 - warehouse-protection"
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
