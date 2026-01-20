# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files
COPY LANCommander.UDPRelay.slnx .
COPY LANCommander.UDPRelay.Core/LANCommander.UDPRelay.Core.csproj LANCommander.UDPRelay.Core/
COPY LANCommander.UDPRelay/LANCommander.UDPRelay.csproj LANCommander.UDPRelay/

# Restore dependencies
RUN dotnet restore LANCommander.UDPRelay.slnx

# Copy all source files
COPY LANCommander.UDPRelay.Core/ LANCommander.UDPRelay.Core/
COPY LANCommander.UDPRelay/ LANCommander.UDPRelay/

# Build the application
RUN dotnet build LANCommander.UDPRelay.slnx -c Release --no-restore -o /app/build

# Publish stage
FROM build AS publish
RUN dotnet publish LANCommander.UDPRelay/LANCommander.UDPRelay.csproj -c Release --no-build -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app

# Copy published application
COPY --from=publish /app/publish .

# Create a non-root user for security
RUN groupadd -r udprelay && useradd -r -g udprelay udprelay
RUN chown -R udprelay:udprelay /app

# Note: UDP ports are dynamically discovered and bound at runtime.
# The application will bind to ports based on discovered container configurations.
# If you need to expose specific ports, add EXPOSE directives or use --publish in docker run.

# Switch to non-root user
USER udprelay

# Entry point
ENTRYPOINT ["dotnet", "LANCommander.UDPRelay.dll"]
