using Agent.Api;
using Agent.Core;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDevOpsAgent(builder.Configuration);
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).WithTags("Health");
app.MapChatEndpoints();
app.MapLintEndpoints();

app.Run();

/// <summary>Entry point, visible to the integration tests.</summary>
public partial class Program;
