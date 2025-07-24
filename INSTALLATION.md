# SelfcareService Installation Guide

This guide provides instructions for installing the SelfcareService on both Windows and Linux systems.

## Prerequisites

### Windows
- .NET 9.0 Runtime or SDK
- Administrator privileges

### Linux
- .NET 9.0 Runtime or SDK
- Root privileges (sudo)

## Installation

### Windows Installation

1. **Build the application (if not already built):**
   ```cmd
   cd SelfcareService
   dotnet publish -c Release
   ```

2. **Install as Windows Service:**
   - Right-click on `install-windows-service.bat`
   - Select "Run as administrator"
   - Follow the prompts

   **OR** run from command line as Administrator:
   ```cmd
   install-windows-service.bat
   ```

3. **Verify installation:**
   ```cmd
   sc query SelfcareService
   ```

### Linux Installation

1. **Install the service:**
   ```bash
   sudo ./install-linux-service.sh
   ```

   The script will:
   - Build and publish the application if needed
   - Install the service to `/opt/selfcare-service/`
   - Create and enable the systemd service
   - Start the service running as root

2. **Verify installation:**
   ```bash
   sudo systemctl status selfcare-service
   ```

## Service Management

### Windows
- **Start service:** `sc start SelfcareService`
- **Stop service:** `sc stop SelfcareService`
- **Check status:** `sc query SelfcareService`
- **View in GUI:** Run `services.msc`

### Linux
- **Start service:** `sudo systemctl start selfcare-service`
- **Stop service:** `sudo systemctl stop selfcare-service`
- **Restart service:** `sudo systemctl restart selfcare-service`
- **Check status:** `sudo systemctl status selfcare-service`
- **View logs:** `sudo journalctl -u selfcare-service -f`
- **View all logs:** `sudo journalctl -u selfcare-service`

## Uninstallation

### Windows
- Right-click on `uninstall-windows-service.bat` and "Run as administrator"
- **OR** run: `uninstall-windows-service.bat`

### Linux
```bash
sudo ./uninstall-linux-service.sh
```

## Configuration

The service reads configuration from:
- **Windows:** `appsettings.json` and `appsettings.Production.json` in the service directory
- **Linux:** `/opt/selfcare-service/appsettings.json` and `/opt/selfcare-service/appsettings.Production.json`

## Security Considerations

- **Windows:** The service runs under the Local System account by default
- **Linux:** The service runs as root user for system-level operations
- Ensure proper file permissions and access controls are in place
- Review and configure the service settings according to your security requirements

## Troubleshooting

### Windows
- Check Event Viewer for service-related errors
- Ensure the executable path is correct
- Verify .NET runtime is installed

### Linux
- Check service logs: `sudo journalctl -u selfcare-service`
- Verify .NET runtime is installed: `dotnet --version`
- Check file permissions on `/opt/selfcare-service/`
- Ensure the service file is properly formatted: `sudo systemctl status selfcare-service`

## Files Overview

### Windows Files
- `install-windows-service.bat` - Windows service installation script
- `uninstall-windows-service.bat` - Windows service uninstallation script

### Linux Files
- `install-linux-service.sh` - Linux systemd service installation script
- `uninstall-linux-service.sh` - Linux systemd service uninstallation script
- `selfcare-service.service` - systemd service configuration file
