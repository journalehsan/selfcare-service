@echo off
setlocal

echo Uninstalling SelfcareService Windows Service...

REM Check if running as administrator
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo ERROR: This script must be run as Administrator
    echo Please right-click and select "Run as administrator"
    pause
    exit /b 1
)

set SERVICE_NAME=SelfcareService

echo Stopping service...
sc stop %SERVICE_NAME%
if %errorLevel% neq 0 (
    echo Warning: Service may not be running
)

echo Removing service...
sc delete %SERVICE_NAME%
if %errorLevel% neq 0 (
    echo ERROR: Failed to remove service
    pause
    exit /b 1
)

echo.
echo SUCCESS: SelfcareService has been uninstalled successfully
echo.

pause
