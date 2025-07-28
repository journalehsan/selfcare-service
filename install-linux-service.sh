#!/bin/bash

# Selfcare Service Installation Script for Linux
# This script installs the SelfcareService as a systemd service running as root
#
# Note: This version includes updated Windows-specific authentication fixes
# that resolve communication issues between LocalSystem service and user apps.

set -e

SERVICE_NAME="selfcare-service"
SERVICE_USER="root"
INSTALL_DIR="/opt/selfcare-service"
SYSTEMD_SERVICE_FILE="/etc/systemd/system/$SERVICE_NAME.service"
CURRENT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "Installing SelfcareService as systemd service..."

# Check if running as root
if [[ $EUID -ne 0 ]]; then
   echo "ERROR: This script must be run as root (use sudo)" 
   exit 1
fi

# Check if .NET runtime is installed
if ! command -v dotnet &> /dev/null; then
    echo "ERROR: .NET runtime is not installed"
    echo "Please install .NET 9.0 runtime first:"
    echo "  curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 9.0 --runtime dotnet"
    exit 1
fi

# Use pre-built distribution directory
PUBLISH_DIR="$CURRENT_DIR/dist/linux"

# Check if distribution directory exists
if [[ ! -d "$PUBLISH_DIR" ]]; then
    echo "ERROR: Distribution directory not found at $PUBLISH_DIR"
    echo "Please build the distribution first by running:"
    echo "  cd SelfcareService && dotnet publish -c Release -r linux-x64 --self-contained"
    echo "  Then copy the files to dist/linux/"
    exit 1
fi

# Check if executable exists
if [[ ! -f "$PUBLISH_DIR/SelfcareService" ]]; then
    echo "ERROR: Service executable not found at $PUBLISH_DIR/SelfcareService"
    echo "Please ensure the distribution is properly built in dist/linux/"
    exit 1
fi

# Stop service if running
echo "Stopping service if already running..."
systemctl stop $SERVICE_NAME 2>/dev/null || true

# Create installation directory
echo "Creating installation directory..."
mkdir -p "$INSTALL_DIR"

# Copy application files
echo "Copying application files..."
cp -r "$PUBLISH_DIR"/* "$INSTALL_DIR/"

# Make executable
chmod +x "$INSTALL_DIR/SelfcareService"

# Set ownership
chown -R $SERVICE_USER:$SERVICE_USER "$INSTALL_DIR"

# Install systemd service file
echo "Installing systemd service file..."
cp "$CURRENT_DIR/selfcare-service.service" "$SYSTEMD_SERVICE_FILE"

# Reload systemd
echo "Reloading systemd configuration..."
systemctl daemon-reload

# Enable service to start on boot
echo "Enabling service to start on boot..."
systemctl enable $SERVICE_NAME

# Start service
echo "Starting service..."
systemctl start $SERVICE_NAME

# Check service status
sleep 2
if systemctl is-active --quiet $SERVICE_NAME; then
    echo ""
    echo "SUCCESS: SelfcareService has been installed and started successfully"
    echo ""
    echo "Service Information:"
    echo "  Name: $SERVICE_NAME"
    echo "  Status: $(systemctl is-active $SERVICE_NAME)"
    echo "  Enabled: $(systemctl is-enabled $SERVICE_NAME)"
    echo "  Installation Directory: $INSTALL_DIR"
    echo "  Running as: $SERVICE_USER"
    echo ""
    echo "Useful commands:"
    echo "  systemctl status $SERVICE_NAME     # Check service status"
    echo "  systemctl stop $SERVICE_NAME       # Stop service"
    echo "  systemctl start $SERVICE_NAME      # Start service"
    echo "  systemctl restart $SERVICE_NAME    # Restart service"
    echo "  journalctl -u $SERVICE_NAME -f     # View live logs"
    echo "  journalctl -u $SERVICE_NAME        # View all logs"
    echo ""
else
    echo "ERROR: Service failed to start"
    echo "Check logs with: journalctl -u $SERVICE_NAME"
    exit 1
fi
