using Agent.Api;
using Agent.Api.Security;
using Agent.Core;
using Agent.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDevOpsAgent(builder.Configuration);
builder.Services.AddChatSessionStorage(builder.Configuration);
builder.Services.AddApiSecurity(builder.Configuration);
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).WithTags("Health");
app.MapChatEndpoints()
    .RequireAuthorization(ApiSecurityExtensions.ClientPolicy)
    .RequireRateLimiting(ApiSecurityExtensions.RateLimitPolicy);
app.MapLintEndpoints()
    .RequireAuthorization(ApiSecurityExtensions.ClientPolicy)
    .RequireRateLimiting(ApiSecurityExtensions.RateLimitPolicy);

app.WarnIfUnauthenticated();
app.Run();

/// <summary>Entry point, visible to the integration tests.</summary>
public partial class Program;
