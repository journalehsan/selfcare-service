#!/bin/bash

echo "Building SelfCare Uptime Watcher Service for Linux..."

# Check if .NET 6 SDK is installed
if ! command -v dotnet &> /dev/null; then
    echo "Error: .NET 6 SDK not found. Please install .NET 6 SDK first."
    echo "Install with: sudo apt install dotnet-sdk-6.0"
    exit 1
fi

# Restore packages
echo "Restoring NuGet packages..."
dotnet restore SelfCareUptimeWatcher.csproj
if [ $? -ne 0 ]; then
    echo "Error: Failed to restore packages"
    exit 1
fi

# Build the project
echo "Building project..."
dotnet build SelfCareUptimeWatcher.csproj -c Release
if [ $? -ne 0 ]; then
    echo "Error: Build failed"
    exit 1
fi

# Publish self-contained executable for Linux
echo "Publishing self-contained executable for Linux..."
dotnet publish SelfCareUptimeWatcher.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true
if [ $? -ne 0 ]; then
    echo "Error: Publish failed"
    exit 1
fi

echo ""
echo "Build completed successfully!"
echo ""
echo "Executable location: bin/Release/net6.0-windows/linux-x64/publish/SelfCareUptimeWatcher"
echo ""
echo "Note: This service is primarily designed for Windows."
echo "For Linux, consider using the Rust implementation instead."
echo ""
