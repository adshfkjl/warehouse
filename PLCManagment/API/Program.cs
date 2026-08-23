using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using PLCManagement.API.Data;
using PLCManagement.API.Interfaces;
using PLCManagement.API.Services;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);
var storeHouseConnectionString = BuildPooledConnectionString(
    builder.Configuration.GetConnectionString("StoreHouseConnection"));

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

builder.Services.AddDbContextPool<ApplicationDbContext>(options =>
    options.UseSqlServer(storeHouseConnectionString,
        sqlOptions =>
        {
            sqlOptions.EnableRetryOnFailure(
                maxRetryCount: 3,
                maxRetryDelay: TimeSpan.FromSeconds(3),
                errorNumbersToAdd: null);
            sqlOptions.CommandTimeout(10);
            sqlOptions.MaxBatchSize(10);
        }),
    poolSize: 32);

builder.Services.AddScoped(_ => new SqlConnection(storeHouseConnectionString));
builder.Services.AddSingleton<IPlcConnectionManager, PlcConnectionManager>();

builder.Services.AddScoped<ILocationCheckService, LocationCheckService>();
builder.Services.AddScoped<ILogService, LogService>();
builder.Services.AddScoped<IPlcService, PlcService>();
builder.Services.AddScoped<IBillOperationService, BillOperationService>();
builder.Services.AddScoped<IInventoryService, InventoryService>();
builder.Services.AddScoped<IAcceptanceService, AcceptanceService>();
builder.Services.AddScoped<IDocumentUpLoadService, DocumentUpLoadService>();
builder.Services.AddScoped<IDocumentDownLoadService, DocumentDownLoadService>();
builder.Services.AddScoped<IPlcMonitorService, PlcMonitorService>();
builder.Services.AddScoped<ITrayPreUploadService, TrayPreUploadService>();
builder.Services.AddScoped<IInventoryCheckService, InventoryCheckService>();

// Keep PLC connections warm with bounded background probes.
// Do not register the legacy Timer-based KeepAliveService or PlcHeartbeatService here.
builder.Services.AddHostedService<PlcNetworkKeepAliveService>();

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Error)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("logs/log-.txt", rollingInterval: RollingInterval.Day)
    .CreateLogger();

builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Error);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);

builder.Host.UseSerilog();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
app.UseCors("AllowAll");
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        if (context.Context.Request.Path.StartsWithSegments("/inventory-check"))
        {
            context.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
            context.Context.Response.Headers.Pragma = "no-cache";
            context.Context.Response.Headers.Expires = "0";
        }
    }
});

using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    try
    {
        var context = services.GetRequiredService<ApplicationDbContext>();
        context.Database.Migrate();

        Log.Information("Database migration completed. PLC connections will be warmed by the keepalive service.");
    }
    catch (Exception ex)
    {
        var logger = services.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "初始化数据库或PLC连接时发生错误");
    }
}

app.UseSwagger();
app.UseSwaggerUI();

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    try
    {
        await SeedData.Initialize(services);
    }
    catch (Exception ex)
    {
        var logger = services.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "初始化数据库种子数据时发生错误");
    }
}

app.Run();

static string BuildPooledConnectionString(string? connectionString)
{
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException("StoreHouseConnection connection string is not configured.");
    }

    var builder = new SqlConnectionStringBuilder(connectionString)
    {
        Pooling = true,
        MinPoolSize = 5,
        MaxPoolSize = 100,
        ConnectTimeout = 3
    };

    return builder.ConnectionString;
}
