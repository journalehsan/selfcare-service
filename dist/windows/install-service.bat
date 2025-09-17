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
