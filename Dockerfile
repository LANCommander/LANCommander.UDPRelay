# Runtime stage - expects pre-built artifacts in ./publish directory
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app

# Copy published application (built by GitHub Actions)
COPY publish .

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
