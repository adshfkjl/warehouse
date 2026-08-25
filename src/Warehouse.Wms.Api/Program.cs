using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Warehouse.Wms.Application.Authorization;
using Warehouse.Wms.Application.Identity;
using Warehouse.Wms.Api.Identity;
using Warehouse.Wms.Infrastructure.Health;
using Warehouse.Wms.Application.Integrations;
using Warehouse.Wms.Infrastructure.Integrations;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();
builder.Services.AddHttpContextAccessor();
builder.Services.AddControllers();
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
