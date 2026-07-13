FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY Compass.sln global.json ./
COPY Compass.Api/Compass.Api.csproj Compass.Api/
COPY Compass.Api.Tests/Compass.Api.Tests.csproj Compass.Api.Tests/
COPY Compass.AppHost/Compass.AppHost.csproj Compass.AppHost/

RUN dotnet restore Compass.Api/Compass.Api.csproj

COPY Compass.Api/ Compass.Api/
RUN dotnet publish Compass.Api/Compass.Api.csproj -c Release -o /publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

RUN mkdir -p /app/data /app/trust /app/policies

COPY --from=build /publish .
COPY Compass.Api/policies /app/policies

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD curl -f http://localhost:8080/healthz || exit 1

ENTRYPOINT ["dotnet", "Compass.Api.dll"]
