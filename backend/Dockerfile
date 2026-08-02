# Waypoint.Api — multi-stage build. Build args populate the version/build metadata
# GET /api/v1/health reports; the compose stack (deploy/) supplies real values, unset
# args fall back to the WaypointBuildOptions "dev" defaults.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Waypoint.sln ./
COPY Waypoint.Core/Waypoint.Core.csproj Waypoint.Core/
COPY Waypoint.Infrastructure/Waypoint.Infrastructure.csproj Waypoint.Infrastructure/
COPY Waypoint.Api/Waypoint.Api.csproj Waypoint.Api/
COPY Waypoint.Tests/Waypoint.Tests.csproj Waypoint.Tests/
RUN dotnet restore Waypoint.sln

COPY . .
RUN dotnet publish Waypoint.Api/Waypoint.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

ARG BUILD_VERSION=0.0.0-dev
ARG BUILD_SHA=unknown
ARG BUILD_DATE=unknown
ENV Build__Version=${BUILD_VERSION} \
	Build__Sha=${BUILD_SHA} \
	Build__BuiltAt=${BUILD_DATE} \
	ASPNETCORE_URLS=http://+:8080 \
	ASPNETCORE_ENVIRONMENT=Production \
	DOTNET_EnableDiagnostics=0

# Runs as the non-root user the base image already provides.
USER app

COPY --from=build /app .

EXPOSE 8080

ENTRYPOINT ["dotnet", "Waypoint.Api.dll"]
