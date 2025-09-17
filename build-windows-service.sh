#!/bin/bash

# Build SelfcareService for Windows from Linux
set -e

echo "Building SelfcareService for Windows (cross-compilation)..."

cd SelfcareService

# Clean previous builds
echo "Cleaning previous builds..."
rm -rf bin obj dist

# Restore packages
echo "Restoring packages..."
dotnet restore

# Build for Windows x64 with WINDOWS constant defined
echo "Building for Windows x64..."
dotnet publish \
    -c Release \
    -r win-x64 \
    --self-contained true \
    -p:PublishSingleFile=false \
    -p:PublishTrimmed=false \
    -o dist/win-x64

echo "Build completed successfully!"
echo "Output files are in: SelfcareService/dist/win-x64/"

# Copy to main service directory for packaging
cd ..
mkdir -p dist/windows
cp -r SelfcareService/dist/win-x64/* dist/windows/

# Create installation scripts
cat > dist/windows/install-service.bat << 'EOF'
@echo off
echo Installing SelfCare Service...
echo.

REM Check for admin rights
net session >nul 2>&1
if %errorLevel% == 0 (
    echo Running with administrator privileges.
) else (
    echo This script requires administrator privileges.
    echo Please run as administrator.
    pause
    exit /b 1
)

REM Stop service if running
sc stop SelfcareService 2>nul

REM Create service
sc create SelfcareService binPath="%~dp0SelfcareService.exe" start=auto DisplayName="SelfCare Service"
sc description SelfcareService "Provides system management and WMI query capabilities for SelfCare application"

REM Start service
sc start SelfcareService

echo.
echo Service installed and started successfully!
pause
EOF

cat > dist/windows/uninstall-service.bat << 'EOF'
@echo off
echo Uninstalling SelfCare Service...
echo.

REM Check for admin rights
net session >nul 2>&1
if %errorLevel% == 0 (
    echo Running with administrator privileges.
) else (
    echo This script requires administrator privileges.
    echo Please run as administrator.
    pause
    exit /b 1
)

REM Stop and delete service
sc stop SelfcareService 2>nul
sc delete SelfcareService

echo.
echo Service uninstalled successfully!
pause
EOF

echo "Installation scripts created in dist/windows/"
echo ""
echo "Files created:"
echo "  - dist/windows/SelfcareService.exe"
echo "  - dist/windows/install-service.bat"
echo "  - dist/windows/uninstall-service.bat"
echo ""
echo "To deploy on Windows:"
echo "  1. Copy dist/windows/ folder to Windows machine"
echo "  2. Run install-service.bat as Administrator"
