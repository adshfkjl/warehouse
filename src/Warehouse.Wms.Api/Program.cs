using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Warehouse.Wms.Application.Authorization;
using Warehouse.Wms.Application.Devices;
using Warehouse.Wms.Application.Exceptions;
using Warehouse.Wms.Application.Identity;
using Warehouse.Wms.Application.Import;
using Warehouse.Wms.Application.Inbound;
using Warehouse.Wms.Application.Inventory;
using Warehouse.Wms.Application.Outbound;
using Warehouse.Wms.Application.Relocation;
using Warehouse.Wms.Application.Stocktaking;
using Warehouse.Wms.Application.Tasks;
using Warehouse.Wms.Api.Identity;
using Warehouse.Wms.DeviceGateway;
using Warehouse.Wms.Domain.Devices;
using Warehouse.Wms.Infrastructure.Health;
using Warehouse.Wms.Application.Integrations;
using Warehouse.Wms.Infrastructure.Integrations;
using Warehouse.Wms.Infrastructure.Persistence;
using Warehouse.Wms.Application.Reports;
using Warehouse.Wms.Application.Points;
using Warehouse.Wms.Infrastructure.Reports;
using Warehouse.Wms.Infrastructure.Warehouse;
using Warehouse.Wms.Infrastructure.Background;

using WmsTaskScheduler = Warehouse.Wms.Application.Tasks.TaskScheduler;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();
builder.Services.AddHttpContextAccessor();
builder.Services.AddControllers();

// The development host is deliberately composed with the simulated gateway.
// A real PLC adapter is opt-in and must be supplied by a separately reviewed
// deployment configuration; the WMS API never writes PLC registers directly.
builder.Services.AddSingleton<SimulatedDeviceGateway>();
builder.Services.AddSingleton<IWarehouseDeviceGateway>(sp => sp.GetRequiredService<SimulatedDeviceGateway>());
builder.Services.AddSingleton<TaskSchedulerState>();
builder.Services.AddSingleton<TaskWorker>();
builder.Services.AddSingleton<WorkflowRecoveryService>(sp => new WorkflowRecoveryService(
    sp.GetRequiredService<ITaskPersistenceStore>()));
builder.Services.AddHostedService<TaskWorkerHostedService>();
builder.Services.AddSingleton<WmsTaskScheduler>(sp => new WmsTaskScheduler(
    sp.GetRequiredService<IWarehouseDeviceGateway>(),
    DeviceCapability.TaskKeyDeduplication | DeviceCapability.TaskQuery | DeviceCapability.StopControl,
    sp.GetRequiredService<TaskSchedulerState>(),
    sp.GetService<ITaskCommandOutbox>(),
    builder.Configuration["Wms:SchedulerWorkerId"],
    persistenceStore: sp.GetService<ITaskPersistenceStore>(),
    resourceLockStore: sp.GetService<IResourceLockStore>()));

var configuredPersistenceMode = builder.Configuration["Wms:PersistenceMode"];
if (string.IsNullOrWhiteSpace(configuredPersistenceMode) && builder.Environment.IsProduction())
{
    throw new InvalidOperationException("Wms:PersistenceMode must be explicitly configured in Production.");
}

var persistenceMode = configuredPersistenceMode ?? "InMemory";
if (builder.Environment.IsProduction()
    && !persistenceMode.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("Production requires Wms:PersistenceMode=SqlServer.");
}

if (!persistenceMode.Equals("InMemory", StringComparison.OrdinalIgnoreCase)
    && !persistenceMode.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("Wms:PersistenceMode must be InMemory or SqlServer.");
}

if (persistenceMode.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
{
    var connectionString = builder.Configuration.GetConnectionString("WmsDb");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException("ConnectionStrings:WmsDb is required when Wms:PersistenceMode=SqlServer.");
    }

    builder.Services.AddSqlServerInventoryPersistence(connectionString);
    builder.Services.AddSingleton<ILoadingPointRuntimeStatus, UnknownLoadingPointRuntimeStatus>();
    builder.Services.AddSingleton<ILoadingPointCatalog, SqlServerLoadingPointCatalog>();
    builder.Services.AddSingleton<InventoryService>(sp =>
        new InventoryService(sp.GetRequiredService<IInventoryLedgerStore>()));
}
else
{
    builder.Services.AddSingleton<ILoadingPointRuntimeStatus, InMemoryLoadingPointRuntimeStatus>();
    builder.Services.AddSingleton<ILoadingPointCatalog>(_ => new InMemoryLoadingPointCatalog([
        new OutboundLoadingPoint(new Warehouse.Wms.Domain.MasterData.LoadingPoint(Guid.Parse("00000000-0000-0000-0000-000000000006"), "LP-DEV-01", "开发装载点"), false)
    ]));
    builder.Services.AddSingleton<InventoryService>();
    builder.Services.AddSingleton<InMemoryTaskPersistenceStore>();
    builder.Services.AddSingleton<ITaskPersistenceStore>(sp => sp.GetRequiredService<InMemoryTaskPersistenceStore>());
    builder.Services.AddSingleton<IResourceLockStore>(sp => sp.GetRequiredService<InMemoryTaskPersistenceStore>());
    builder.Services.AddSingleton<InMemoryIntegrationOutbox>();
    builder.Services.AddSingleton<IIntegrationOutbox>(sp => sp.GetRequiredService<InMemoryIntegrationOutbox>());
    builder.Services.AddSingleton<InMemoryBusinessWorkflowStore>();
    builder.Services.AddSingleton<IBusinessWorkflowStore>(sp => sp.GetRequiredService<InMemoryBusinessWorkflowStore>());
}
builder.Services.AddSingleton<InboundOrderService>();
builder.Services.AddSingleton<PutawayAllocationService>();
builder.Services.AddSingleton<PutawayTaskService>(sp => new PutawayTaskService(
    sp.GetRequiredService<InboundOrderService>(),
    sp.GetRequiredService<PutawayAllocationService>(),
    sp.GetRequiredService<WmsTaskScheduler>(),
    sp.GetService<IResourceLockStore>(),
    sp.GetService<IBusinessWorkflowStore>()));
builder.Services.AddSingleton<InboundReconciliationService>(sp => new InboundReconciliationService(
    sp.GetRequiredService<InboundOrderService>(),
    sp.GetRequiredService<PutawayTaskService>(),
    sp.GetRequiredService<InventoryService>(),
    sp.GetService<IResourceLockStore>()));
builder.Services.AddSingleton<OutboundAllocationService>(sp => new OutboundAllocationService(
    sp.GetRequiredService<InventoryService>(), sp.GetService<IResourceLockStore>()));
builder.Services.AddSingleton<OutboundTaskService>(sp => new OutboundTaskService(
    sp.GetRequiredService<OutboundAllocationService>(),
    sp.GetRequiredService<WmsTaskScheduler>(),
    sp.GetRequiredService<ILoadingPointCatalog>(),
    sp.GetService<IResourceLockStore>(),
    sp.GetService<IBusinessWorkflowStore>()));
builder.Services.AddSingleton<OutboundReviewService>(sp => new OutboundReviewService(
    sp.GetRequiredService<OutboundTaskService>(),
    sp.GetRequiredService<InventoryService>(),
    sp.GetService<IBusinessWorkflowStore>()));
builder.Services.AddSingleton<RelocationService>(sp => new RelocationService(
    sp.GetRequiredService<InventoryService>(), sp.GetRequiredService<WmsTaskScheduler>(), sp.GetService<IResourceLockStore>(), sp.GetService<IBusinessWorkflowStore>()));
builder.Services.AddSingleton<StocktakingService>(sp => new StocktakingService(
    sp.GetService<IEnumerable<StocktakingInventoryItem>>() ?? Array.Empty<StocktakingInventoryItem>(),
    sp.GetService<WmsTaskScheduler>(),
    sp.GetService<IResourceLockStore>(),
    sp.GetService<IBusinessWorkflowStore>()));
builder.Services.AddScoped<StocktakingDifferenceService>();
builder.Services.AddScoped<ExceptionWorkItemService>();
builder.Services.AddScoped<PhysicalResultConfirmationService>();
builder.Services.AddSingleton<TaskCancellationService>();
builder.Services.AddSingleton<SpreadsheetImportService>();
builder.Services.AddSingleton<IReportsReadModel, InMemoryReportsReadModel>();
builder.Services.AddSingleton<IStatisticsService, InMemoryStatisticsService>();
var statisticsPeriod = Enum.TryParse<StatisticsPeriod>(builder.Configuration["Wms:Statistics:Period"], true, out var configuredStatisticsPeriod)
    ? configuredStatisticsPeriod
    : StatisticsPeriod.Day;
var statisticsSchedule = new StatisticsScheduleOptions(
    statisticsPeriod,
    TimeSpan.TryParse(builder.Configuration["Wms:Statistics:RunAt"], out var configuredRunAt) ? configuredRunAt : TimeSpan.FromHours(1),
    builder.Configuration.GetValue("Wms:Statistics:Enabled", true));
builder.Services.AddSingleton(statisticsSchedule);
builder.Services.AddSingleton<InMemoryStatisticsScheduler>();
builder.Services.AddSingleton<IPointReadModel>(_ => new InMemoryPointReadModel([
    new WarehousePointSnapshot("WH-01", "Z1", "A1", "R01", 1, "A1-01-01", "Occupied", "PLT-00128", "MAT-001", "示例物料", "LOT-01", 12, 120, DateTimeOffset.UtcNow.AddSeconds(-20), 1, false, null, "Executing", "LP-01"),
    new WarehousePointSnapshot("WH-01", "Z1", "A1", "R01", 2, "A1-01-02", "Free", null, null, null, null, 0, 0, DateTimeOffset.UtcNow.AddSeconds(-20), 1, false, null, "Idle", null),
    new WarehousePointSnapshot("WH-01", "Z1", "A2", "R02", 2, "A2-02-02", "PhysicalUnknown", "PLT-00117", "MAT-002", "待核对物料", "LOT-02", 1, 10, DateTimeOffset.UtcNow.AddMinutes(-8), 2, true, "设备结果未知", "Unknown", "LP-02")
]));
builder.Services.AddSingleton<MessagingHealthState>();
builder.Services.AddSingleton<WarehouseHealthCheckService>();
builder.Services.AddSingleton<IIntegrationCommandService>(sp =>
    new IntegrationCommandService(
        sp.GetRequiredService<IIntegrationOutbox>(),
        builder.Configuration.GetValue("Wms:ExternalIntegrationsEnabled", false)));

var jwtOptions = builder.Configuration.GetSection("Jwt").Get<JwtOptions>()
    ?? new JwtOptions(
        "warehouse-development",
        "warehouse-development",
        "development-only-signing-key-at-least-32-characters-long",
        TimeSpan.FromMinutes(15),
        TimeSpan.FromDays(1));
jwtOptions.Validate();

builder.Services.AddSingleton<IAuditLog, InMemoryAuditLog>();
builder.Services.AddSingleton<IIdentityService>(sp => new InMemoryIdentityService(
    jwtOptions,
    sp.GetRequiredService<IAuditLog>()));
builder.Services.AddSingleton<IRiskAuthorizationService>(sp => sp.GetRequiredService<IIdentityService>());
builder.Services.AddScoped<HttpCurrentUserAccessor>();
builder.Services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<HttpCurrentUserAccessor>());

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(15)
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var exception = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    context.Response.ContentType = "application/json";
    context.Response.StatusCode = exception switch
    {
        UnauthorizedAccessException => StatusCodes.Status401Unauthorized,
        ArgumentException => StatusCodes.Status400BadRequest,
        KeyNotFoundException => StatusCodes.Status404NotFound,
        InvalidOperationException => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status500InternalServerError
    };
    await context.Response.WriteAsJsonAsync(new
    {
        code = exception?.GetType().Name ?? "INTERNAL_ERROR",
        message = exception?.Message ?? "An unexpected error occurred."
    });
}));
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");
app.MapGet("/", () => Results.Ok(new
{
    service = "Warehouse.Wms.Api",
    status = "ok"
}));

app.Run();

public partial class Program
{
}
