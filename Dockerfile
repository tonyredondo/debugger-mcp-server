# Debugger MCP Server Dockerfile
# Supports debugging .NET dumps from versions 5.0 through 10.0
# Uses LLDB + SOS for Linux debugging, requires DAC from matching .NET runtime
#
# SOS debugging extension (libsosplugin.so) is installed via dotnet-sos tool
# and copied to /sos/ directory with SOS_PLUGIN_PATH environment variable set
#
# Final image uses ASP.NET runtime (not SDK) for smaller size.
# Older .NET runtimes are installed for DAC support when debugging older dumps.
#
# Architecture support:
# - AMD64 (x86_64): Full support for .NET 5.0-10.0
# - ARM64 (aarch64): Support for .NET 6.0-10.0 only (.NET 5.0 not available on ARM64 Linux)

# =============================================================================
# Build stage - compile the application
# =============================================================================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project file and restore dependencies
COPY ["DebuggerMcp/DebuggerMcp.csproj", "DebuggerMcp/"]
RUN dotnet restore "DebuggerMcp/DebuggerMcp.csproj"

# Copy source and build
COPY . .
WORKDIR "/src/DebuggerMcp"
RUN dotnet build "DebuggerMcp.csproj" -c Release -o /app/build

# =============================================================================
# Publish stage - create release artifacts
# =============================================================================
FROM build AS publish
RUN dotnet publish "DebuggerMcp.csproj" -c Release -o /app/publish /p:UseAppHost=false

# =============================================================================
# Tools stage - install .NET diagnostic tools (requires SDK)
# =============================================================================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS tools
# Install diagnostic tools that will be copied to final image
RUN dotnet tool install --tool-path /tools dotnet-symbol \
    && dotnet tool install --tool-path /tools dotnet-dump \
    && dotnet tool install --tool-path /tools dotnet-gcdump \
    && dotnet tool install --tool-path /tools dotnet-monitor \
    && dotnet tool install --tool-path /tools dotnet-trace \
    && dotnet tool install --tool-path /tools dotnet-stack \
    && dotnet tool install --tool-path /tools dotnet-counters \
    && dotnet tool install --tool-path /tools dotnet-coverage \
    && dotnet tool install --tool-path /tools dotnet-sos \
    && dotnet tool install --tool-path /tools dotnet-debugger-extensions

# Install SOS debugging extension (libsosplugin.so) to a known location
# dotnet-sos install puts it in ~/.dotnet/sos, we copy it to /sos
RUN /tools/dotnet-sos install --architecture $(uname -m | sed 's/x86_64/x64/' | sed 's/aarch64/arm64/') \
    && mkdir -p /sos \
    && cp -r /root/.dotnet/sos/* /sos/

# =============================================================================
# Final stage - ASP.NET runtime image (smaller than SDK)
# SOS is installed via dotnet-sos and copied from tools stage
# =============================================================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Install LLDB debugger and required tools
# libunwind-dev is required for ClrMD to read dump memory
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        lldb \
        curl \
        ca-certificates \
        file \
        libunwind-dev \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

# Install additional .NET runtimes for DAC support when debugging older dumps
# The ASP.NET runtime includes .NET 10 runtime
# We add older runtimes so we can load the correct DAC for each dump version
ARG TARGETARCH
RUN curl -sSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh \
    && chmod +x dotnet-install.sh \
    # .NET 5.0 - only available on x64
    && if [ "$TARGETARCH" = "amd64" ] || [ "$(uname -m)" = "x86_64" ]; then \
        echo "Installing .NET 5.0 runtime (x64 only)..." && \
        ./dotnet-install.sh --runtime dotnet --channel 5.0 --install-dir /usr/share/dotnet; \
    else \
        echo "Skipping .NET 5.0 on ARM64 (not supported)"; \
    fi \
    # .NET 6.0 - LTS, supports both x64 and ARM64
    && echo "Installing .NET 6.0 runtime..." \
    && ./dotnet-install.sh --runtime dotnet --channel 6.0 --install-dir /usr/share/dotnet \
    # .NET 7.0 - supports both x64 and ARM64
    && echo "Installing .NET 7.0 runtime..." \
    && ./dotnet-install.sh --runtime dotnet --channel 7.0 --install-dir /usr/share/dotnet \
    # .NET 8.0 - LTS, supports both x64 and ARM64
    && echo "Installing .NET 8.0.22 runtime..." \
    && ./dotnet-install.sh --runtime dotnet --version 8.0.22 --install-dir /usr/share/dotnet \
    # .NET 9.0 - install multiple patch versions for DAC compatibility
    # SOS/DAC requires exact patch version match for Build ID validation
    && echo "Installing .NET 9.0.10 runtime..." \
    && ./dotnet-install.sh --runtime dotnet --version 9.0.10 --install-dir /usr/share/dotnet \
    && echo "Installing .NET 9.0.11 runtime..." \
    && ./dotnet-install.sh --runtime dotnet --version 9.0.11 --install-dir /usr/share/dotnet \
    # .NET 10
    && echo "Installing .NET 10.0.0 runtime..." \
    && ./dotnet-install.sh --runtime dotnet --version 10.0.0 --install-dir /usr/share/dotnet \
    # Cleanup
    && rm dotnet-install.sh

# Copy diagnostic tools from tools stage
COPY --from=tools /tools /tools

# Copy SOS debugging extension (libsosplugin.so)
# This is required for .NET debugging with LLDB
COPY --from=tools /sos /sos

# Add tools to PATH and set SOS plugin path
ENV PATH="${PATH}:/tools"
ENV SOS_PLUGIN_PATH=/sos/libsosplugin.so

# Create directories for dump, symbol, log, and session files
RUN mkdir -p /app/dumps /app/symbols /app/logs /app/sessions

# Copy published application
COPY --from=publish /app/publish .

# Set environment variables
ENV ASPNETCORE_URLS=http://+:5000
ENV PORT=5000
ENV DUMP_STORAGE_PATH=/app/dumps
ENV SYMBOL_STORAGE_PATH=/app/symbols
ENV LOG_STORAGE_PATH=/app/logs
ENV SESSION_STORAGE_PATH=/app/sessions

# Expose HTTP port
EXPOSE 5000

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD curl -f http://localhost:5000/health || exit 1

# Run in MCP HTTP mode by default
ENTRYPOINT ["dotnet", "DebuggerMcp.dll", "--mcp-http"]
