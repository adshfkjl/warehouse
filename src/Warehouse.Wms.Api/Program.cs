var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();

var app = builder.Build();

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
