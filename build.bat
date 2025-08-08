@echo off
echo Building SelfCare Uptime Watcher Service...

REM Check if .NET 6 SDK is installed
dotnet --version >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo Error: .NET 6 SDK not found. Please install .NET 6 SDK first.
    echo Download from: https://dotnet.microsoft.com/download/dotnet/6.0
    pause
    exit /b 1
)

REM Restore packages
echo Restoring NuGet packages...
dotnet restore SelfCareUptimeWatcher.csproj
if %ERRORLEVEL% neq 0 (
    echo Error: Failed to restore packages
    pause
    exit /b 1
)

REM Build the project
echo Building project...
dotnet build SelfCareUptimeWatcher.csproj -c Release
if %ERRORLEVEL% neq 0 (
    echo Error: Build failed
    pause
    exit /b 1
)

REM Publish self-contained executable
echo Publishing self-contained executable...
dotnet publish SelfCareUptimeWatcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
if %ERRORLEVEL% neq 0 (
    echo Error: Publish failed
    pause
    exit /b 1
)

echo.
echo Build completed successfully!
echo.
echo Executable location: bin\Release\net6.0-windows\win-x64\publish\SelfCareUptimeWatcher.exe
echo.
echo Installation Instructions:
echo 1. Copy the executable to a permanent location (e.g., C:\Program Files\SelfCare\)
echo 2. Run as Administrator: sc create SelfCareUptimeWatcher binPath="C:\Path\To\SelfCareUptimeWatcher.exe" start=auto
echo 3. Start the service: sc start SelfCareUptimeWatcher
echo.
echo For testing: SelfCareUptimeWatcher.exe test
echo.
pause
