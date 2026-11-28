# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY src/Pulse.Contracts/Pulse.Contracts.csproj src/Pulse.Contracts/
COPY src/Pulse.Server/Pulse.Server.csproj src/Pulse.Server/
RUN dotnet restore src/Pulse.Server/Pulse.Server.csproj

COPY src/Pulse.Contracts/ src/Pulse.Contracts/
COPY src/Pulse.Server/ src/Pulse.Server/
RUN dotnet publish src/Pulse.Server/Pulse.Server.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app

COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0

EXPOSE 8080
USER $APP_UID

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD wget --spider -q http://127.0.0.1:8080/healthz || exit 1

ENTRYPOINT ["dotnet", "Pulse.Server.dll"]
