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
