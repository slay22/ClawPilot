# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ClawPilot.sln .
COPY src/ClawPilot.Dashboard/ClawPilot.Dashboard.csproj src/ClawPilot.Dashboard/

RUN dotnet restore src/ClawPilot.Dashboard/ClawPilot.Dashboard.csproj

COPY src/ClawPilot.Dashboard/ src/ClawPilot.Dashboard/

RUN dotnet publish src/ClawPilot.Dashboard/ClawPilot.Dashboard.csproj \
    -c Release \
    -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "ClawPilot.Dashboard.dll"]
