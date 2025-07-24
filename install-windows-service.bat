@echo off
setlocal

echo Installing SelfcareService as Windows Service...

REM Check if running as administrator
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo ERROR: This script must be run as Administrator
    echo Please right-click and select "Run as administrator"
    pause
    exit /b 1
)

REM Set variables
set SERVICE_NAME=SelfcareService
set SERVICE_DISPLAY_NAME=Selfcare Service
set SERVICE_DESCRIPTION=Selfcare Service for system management tasks
set CURRENT_DIR=%~dp0
set EXECUTABLE_PATH=%CURRENT_DIR%SelfcareService\bin\Release\net9.0\SelfcareService.exe

REM Check if executable exists
if not exist "%EXECUTABLE_PATH%" (
    echo ERROR: Service executable not found at %EXECUTABLE_PATH%
    echo Please build the project in Release mode first using:
    echo dotnet publish -c Release
    pause
    exit /b 1
)

echo Stopping service if already running...
sc stop %SERVICE_NAME% >nul 2>&1

echo Removing existing service if present...
sc delete %SERVICE_NAME% >nul 2>&1

echo Creating Windows Service...
sc create %SERVICE_NAME% binPath= "\"%EXECUTABLE_PATH%\"" DisplayName= "%SERVICE_DISPLAY_NAME%" start= auto
if %errorLevel% neq 0 (
    echo ERROR: Failed to create service
    pause
    exit /b 1
)

echo Setting service description...
sc description %SERVICE_NAME% "%SERVICE_DESCRIPTION%"

echo Starting service...
sc start %SERVICE_NAME%
if %errorLevel% neq 0 (
    echo ERROR: Failed to start service
    pause
    exit /b 1
)

echo.
echo SUCCESS: SelfcareService has been installed and started successfully
echo.
echo Service Information:
echo   Name: %SERVICE_NAME%
echo   Display Name: %SERVICE_DISPLAY_NAME%
echo   Status: Running
echo   Startup Type: Automatic
echo.
echo You can manage the service using:
echo   - Services.msc (GUI)
echo   - sc query %SERVICE_NAME% (check status)
echo   - sc stop %SERVICE_NAME% (stop service)
echo   - sc start %SERVICE_NAME% (start service)
echo.

pause
