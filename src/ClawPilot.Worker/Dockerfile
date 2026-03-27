# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ClawPilot.sln .
COPY src/ClawPilot.Worker/ClawPilot.Worker.csproj src/ClawPilot.Worker/

RUN dotnet restore src/ClawPilot.Worker/ClawPilot.Worker.csproj

COPY src/ClawPilot.Worker/ src/ClawPilot.Worker/

RUN dotnet publish src/ClawPilot.Worker/ClawPilot.Worker.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app/publish .
COPY Soul.md .

# Pre-create writable directories for SQLite journal and logs
RUN mkdir -p /app/data /app/logs

ENTRYPOINT ["dotnet", "ClawPilot.Worker.dll"]
