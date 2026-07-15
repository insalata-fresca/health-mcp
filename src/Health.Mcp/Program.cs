using Health.Mcp;
using Sinapsi.Mcp;

var builder = WebApplication.CreateBuilder(args);

// Immutable runtime config (OAuth creds + endpoints + advertised data types).
builder.Services.AddSingleton(HealthOptions.FromEnvironment());

// The Google Health client as a pooled typed HttpClient (IHttpClientFactory).
// Google Health reads are quick; the SDK's default 100s timeout is fine here, so
// (unlike the long-running GatewayMcpClient) we do NOT disable it.
builder.Services.AddHttpClient<GoogleHealthClient>();

builder
    .AddSinapsiMcpServer("health-mcp", "0.1.0")
    // Stateless HTTP transport: no Mcp-Session-Id is threaded and Sinapsi.Mcp
    // strips the stray header in stateless mode.
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<HealthTools>();

var app = builder.Build();

// Fail fast at startup if the required credentials are missing (surfaces a clear
// error instead of a per-call 401). Resolving the singleton validates the env.
_ = app.Services.GetRequiredService<HealthOptions>();

app.MapSinapsiMcp(envPrefix: "HEALTH_MCP", defaultPort: 9226).Run();
