# Use the .NET 10 SDK for building
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /app

# Copy the project files and restore
COPY src/ClawPilot.Dashboard/ClawPilot.Dashboard.csproj src/ClawPilot.Dashboard/
RUN dotnet restore src/ClawPilot.Dashboard/ClawPilot.Dashboard.csproj

# Copy the rest of the source code
COPY src/ClawPilot.Dashboard/ src/ClawPilot.Dashboard/
WORKDIR /app/src/ClawPilot.Dashboard
RUN dotnet publish -c Release -o /app/publish

# Use the .NET 10 ASP.NET runtime for running
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "ClawPilot.Dashboard.dll"]
