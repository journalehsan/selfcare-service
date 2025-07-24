#!/bin/bash

# Selfcare Service Uninstallation Script for Linux
# This script uninstalls the SelfcareService systemd service

set -e

SERVICE_NAME="selfcare-service"
INSTALL_DIR="/opt/selfcare-service"
SYSTEMD_SERVICE_FILE="/etc/systemd/system/$SERVICE_NAME.service"

echo "Uninstalling SelfcareService systemd service..."

# Check if running as root
if [[ $EUID -ne 0 ]]; then
   echo "ERROR: This script must be run as root (use sudo)" 
   exit 1
fi

# Stop service
echo "Stopping service..."
systemctl stop $SERVICE_NAME 2>/dev/null || true

# Disable service
echo "Disabling service..."
systemctl disable $SERVICE_NAME 2>/dev/null || true

# Remove systemd service file
echo "Removing systemd service file..."
rm -f "$SYSTEMD_SERVICE_FILE"

# Reload systemd
echo "Reloading systemd configuration..."
systemctl daemon-reload

# Remove installation directory
echo "Removing installation directory..."
rm -rf "$INSTALL_DIR"

echo ""
echo "SUCCESS: SelfcareService has been uninstalled successfully"
echo ""
