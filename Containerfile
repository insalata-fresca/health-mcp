# health-mcp — Google Health API v4 read-only MCP (C#, .NET 8).
#
# Standalone multi-stage build:
#   docker build -t health-mcp .
#   docker run --rm -p 9226:9226 --env-file config.env health-mcp
#
# Pure managed app — no host mounts, no CLI; all configuration arrives via
# environment variables (see config.env.example).
#
# NOTE: this project references `Sinapsi.Mcp`, a small MCP-host helper library.
# Every other dependency resolves from nuget.org. Provide a NuGet source that
# serves Sinapsi.Mcp (e.g. add a nuget.config) if it is not on your feeds.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Health.Mcp/Health.Mcp.csproj \
        -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV HEALTH_MCP_HOST=0.0.0.0 \
    HEALTH_MCP_PORT=9226 \
    ASPNETCORE_URLS=http://0.0.0.0:9226 \
    DOTNET_EnableDiagnostics=0
EXPOSE 9226
ENTRYPOINT ["dotnet", "Health.Mcp.dll"]
