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
builder.Services.AddSingleton<WmsTaskScheduler>(sp => new WmsTaskScheduler(
    sp.GetRequiredService<IWarehouseDeviceGateway>(),
    DeviceCapability.TaskKeyDeduplication | DeviceCapability.TaskQuery | DeviceCapability.StopControl,
    sp.GetRequiredService<TaskSchedulerState>()));

builder.Services.AddSingleton<InventoryService>();
builder.Services.AddSingleton<InboundOrderService>();
builder.Services.AddSingleton<PutawayAllocationService>();
builder.Services.AddSingleton<PutawayTaskService>();
builder.Services.AddSingleton<InboundReconciliationService>();
builder.Services.AddSingleton<OutboundAllocationService>();
builder.Services.AddSingleton<OutboundTaskService>();
builder.Services.AddSingleton<OutboundReviewService>();
builder.Services.AddSingleton<RelocationService>();
builder.Services.AddSingleton<StocktakingService>();
builder.Services.AddScoped<StocktakingDifferenceService>();
builder.Services.AddScoped<ExceptionWorkItemService>();
builder.Services.AddScoped<PhysicalResultConfirmationService>();
builder.Services.AddSingleton<TaskCancellationService>();
builder.Services.AddSingleton<SpreadsheetImportService>();
builder.Services.AddSingleton<IReportsReadModel, InMemoryReportsReadModel>();
builder.Services.AddSingleton<MessagingHealthState>();
builder.Services.AddSingleton<WarehouseHealthCheckService>();
builder.Services.AddSingleton<InMemoryIntegrationOutbox>();
builder.Services.AddSingleton<IIntegrationOutbox>(sp => sp.GetRequiredService<InMemoryIntegrationOutbox>());
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
