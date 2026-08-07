using WidgetWorks.Application;
using WidgetWorks.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication();
builder.Services.AddInfrastructure();
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", (TimeProvider clock) => Results.Ok(new
{
    status = "ok",
    utcNow = clock.GetUtcNow(),
}));

app.Run();

/// <summary>Exposed for integration tests (WebApplicationFactory).</summary>
public partial class Program { }
