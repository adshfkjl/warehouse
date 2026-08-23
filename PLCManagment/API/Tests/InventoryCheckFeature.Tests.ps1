$ErrorActionPreference = 'Stop'

$root = Join-Path $PSScriptRoot '..'
$controllerPath = Join-Path $root 'Controllers\InventoryCheckController.cs'
$servicePath = Join-Path $root 'Services\InventoryCheckService.cs'
$interfacePath = Join-Path $root 'Interfaces\IInventoryCheckService.cs'
$dtoPath = Join-Path $root 'Models\Dtos\InventoryCheckDto.cs'
$taskModelPath = Join-Path $root 'Models\InventoryCheckTask.cs'
$itemModelPath = Join-Path $root 'Models\InventoryCheckItem.cs'
$dbContextPath = Join-Path $root 'Data\ApplicationDbContext.cs'
$programPath = Join-Path $root 'Program.cs'
$uiIndexPath = Join-Path $root 'wwwroot\inventory-check\index.html'
$uiCssPath = Join-Path $root 'wwwroot\inventory-check\inventory-check.css'
$uiJsPath = Join-Path $root 'wwwroot\inventory-check\inventory-check.js'
$plcConnectionManagerPath = Join-Path $root 'Services\PlcConnectionManager.cs'

function Assert-FileExists($Path, $Message) {
    if (-not (Test-Path -LiteralPath $Path)) {
        throw $Message
    }
}

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

Assert-FileExists $controllerPath 'InventoryCheckController must exist.'
Assert-FileExists $servicePath 'InventoryCheckService must exist.'
Assert-FileExists $interfacePath 'IInventoryCheckService must exist.'
Assert-FileExists $dtoPath 'Inventory check DTOs must exist.'
Assert-FileExists $taskModelPath 'InventoryCheckTask model must exist.'
Assert-FileExists $itemModelPath 'InventoryCheckItem model must exist.'
Assert-FileExists $uiIndexPath 'Inventory check static UI index.html must exist.'
Assert-FileExists $uiCssPath 'Inventory check static UI CSS must exist.'
Assert-FileExists $uiJsPath 'Inventory check static UI JavaScript must exist.'
Assert-FileExists $plcConnectionManagerPath 'PLC connection manager must exist.'

$controller = Get-Content -Raw -Path $controllerPath
$service = Get-Content -Raw -Path $servicePath
$dbContext = Get-Content -Raw -Path $dbContextPath
$program = Get-Content -Raw -Path $programPath
$uiIndex = Get-Content -Raw -Path $uiIndexPath
$uiJs = Get-Content -Raw -Path $uiJsPath
$plcConnectionManager = Get-Content -Raw -Path $plcConnectionManagerPath

Assert-Contains $controller '\[Route\("api/inventory-check"\)\]' `
    'InventoryCheckController route must be api/inventory-check.'
Assert-Contains $controller 'outbound-range' `
    'Controller must expose outbound-range endpoint.'
Assert-Contains $controller 'items/\{itemId:long\}/schedule-inbound' `
    'Controller must expose item-level inbound scheduling endpoint.'
Assert-NotContains $controller 'tasks/\{taskId:long\}/schedule-inbound' `
    'Controller must not expose task-level batch inbound scheduling endpoint.'
Assert-Contains $controller 'cancel' `
    'Controller must expose inventory check cancellation endpoint.'

Assert-Contains $service 'ParseTrayRange' `
    'Service must parse tray ranges explicitly.'
Assert-Contains $service 'LocationManagements' `
    'Service must query LocationManagements.'
Assert-Contains $service '\.Tray' `
    'Service must base range selection on LocationManagements.Tray.'
Assert-Contains $service 'IsLoadingPointEmpty\(request\.PlcId, 0\)|IsLoadingPointEmpty\(plcId, 0\)' `
    'Service must check loading point 0.'
Assert-Contains $service 'IsLoadingPointEmpty\(request\.PlcId, 1\)|IsLoadingPointEmpty\(plcId, 1\)' `
    'Service must check loading point 1.'
Assert-Contains $service 'OutboundOperation\(' `
    'Service must reuse existing outbound operation.'
Assert-Contains $service 'InboundOperation\(' `
    'Service must reuse existing inbound operation.'
Assert-Contains $service 'UpdatePlcStatusAsync\(' `
    'Service must wait for PLC outbound completion status before marking an item succeeded.'
Assert-Contains $service 'UpdateLoadingPointPallet\(' `
    'Service must sync the outbound tray to the selected loading point before inbound scheduling.'
Assert-Contains $service 'CancelTaskAsync' `
    'Service must support user cancellation of an inventory check task.'
Assert-Contains $service 'ScheduleInboundItemAsync' `
    'Service must support scheduling inbound for a single inventory check item.'
Assert-NotContains $service 'public async Task<InventoryCheckTaskResponse> ScheduleInboundAsync\(long taskId\)' `
    'Service must not schedule inbound for a whole task in one operation.'
Assert-Contains $service 'LoadingPointPollingMilliseconds' `
    'Service must wait when loading points 0 and 1 are not empty.'
Assert-NotContains $service 'this tray was skipped' `
    'Service must not skip a tray only because loading points 0 and 1 are currently not empty.'

Assert-Contains $dbContext 'DbSet<InventoryCheckTask>' `
    'DbContext must expose InventoryCheckTasks.'
Assert-Contains $dbContext 'DbSet<InventoryCheckItem>' `
    'DbContext must expose InventoryCheckItems.'
Assert-Contains $program 'AddScoped<IInventoryCheckService, InventoryCheckService>' `
    'Program.cs must register inventory check service.'
Assert-Contains $program 'UseStaticFiles\(' `
    'Program.cs must serve static inventory check UI files.'

Assert-Contains $uiIndex 'inventory-check.js' `
    'Inventory check UI must load its JavaScript.'
Assert-Contains $uiIndex 'cancelTaskButton' `
    'Inventory check UI must expose a cancel task button.'
Assert-Contains $uiJs '/api/inventory-check/outbound-range' `
    'Inventory check UI must call outbound range endpoint.'
Assert-Contains $uiJs '/api/inventory-check/tasks/\$\{taskId\}' `
    'Inventory check UI must call task query endpoint.'
Assert-Contains $uiJs '/api/inventory-check/items/\$\{itemId\}/schedule-inbound' `
    'Inventory check UI must call item-level schedule inbound endpoint.'
Assert-Contains $uiJs 'data-schedule-inbound-item' `
    'Inventory check UI must render schedule inbound controls in task detail rows.'
Assert-Contains $uiJs 'Succeeded: "成功"' `
    'Inventory check UI must display outbound and inbound statuses in Chinese.'
Assert-NotContains $uiJs '/api/inventory-check/tasks/\$\{taskId\}/schedule-inbound' `
    'Inventory check UI must not call task-level batch schedule inbound endpoint.'
Assert-Contains $uiJs '/cancel' `
    'Inventory check UI must call cancel endpoint.'
Assert-Contains $uiJs 'setInterval' `
    'Inventory check UI must poll task status.'
Assert-Contains $uiJs 'file:' `
    'Inventory check UI must detect when it is opened from a local file instead of the API host.'
Assert-Contains $uiJs '无法连接接口' `
    'Inventory check UI must explain network-level fetch failures.'

Assert-Contains $plcConnectionManager 'Modbus probe failed' `
    'PLC reconnect success must require a successful Modbus status-register probe, not only TCP connect.'
Assert-Contains $plcConnectionManager 'Modbus communication probe succeeded' `
    'PLC reconnect success log must describe a Modbus communication probe instead of plain TCP reconnect.'

Write-Host 'Inventory check feature checks passed.'
