# syntax=docker/dockerfile:1

# Build stage: restore against the project files first so the package layer is cached.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Agent.Core/Agent.Core.csproj src/Agent.Core/
COPY src/Agent.Api/Agent.Api.csproj src/Agent.Api/
RUN dotnet restore src/Agent.Api/Agent.Api.csproj
COPY src/ src/
RUN dotnet publish src/Agent.Api/Agent.Api.csproj -c Release -o /out --no-restore

# Runtime stage: ASP.NET Core runtime only, running as the image's non-root 'app' user.
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out .
COPY samples/ /workspace/samples/
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Workspace__Root=/workspace
USER app
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s CMD curl -fsS http://localhost:8080/health || exit 1
ENTRYPOINT ["dotnet", "Agent.Api.dll"]
