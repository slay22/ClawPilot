# Use the .NET 10 SDK for building
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /app

# Copy the project files and restore
COPY src/ClawPilot.Worker/ClawPilot.Worker.csproj src/ClawPilot.Worker/
RUN dotnet restore src/ClawPilot.Worker/ClawPilot.Worker.csproj

# Copy the rest of the source code
COPY src/ClawPilot.Worker/ src/ClawPilot.Worker/
WORKDIR /app/src/ClawPilot.Worker
RUN dotnet publish -c Release -o /app/publish

# Use the .NET 10 runtime for running
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "ClawPilot.Worker.dll"]
