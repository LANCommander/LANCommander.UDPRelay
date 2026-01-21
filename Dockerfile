# Runtime stage - expects pre-built artifacts in ./publish directory
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app

# Install gosu for switching users (available in Debian repos)
RUN apt-get update && \
    apt-get install -y --no-install-recommends gosu && \
    rm -rf /var/lib/apt/lists/*

# Copy published application (built by GitHub Actions)
COPY publish .

# Copy entrypoint script
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

# Create a non-root user for security
# The docker group will be created dynamically by the entrypoint script
RUN groupadd -r udprelay && \
    useradd -r -g udprelay udprelay && \
    chown -R udprelay:udprelay /app

# Note: UDP ports are dynamically discovered and bound at runtime.
# The application will bind to ports based on discovered container configurations.
# If you need to expose specific ports, add EXPOSE directives or use --publish in docker run.

# Run entrypoint as root so it can adjust group permissions
# The entrypoint will switch to the udprelay user before executing the app
ENTRYPOINT ["/entrypoint.sh", "dotnet", "LANCommander.UDPRelay.dll"]
