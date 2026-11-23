#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
ADB="${ADB:-${ANDROID_HOME:-$HOME/Android/Sdk}/platform-tools/adb}"
APK="$PROJECT_DIR/bin/Debug/net10.0-android/com.lumin.baihua-Signed.apk"

echo "Building..."
dotnet build "$PROJECT_DIR/MobileApp.Maui.csproj" \
    -f net10.0-android -c Debug -p:TargetFrameworks=net10.0-android

echo "Installing with --no-incremental (avoids compressed native lib crash on this device)..."
"$ADB" uninstall com.lumin.baihua || true
"$ADB" install --no-incremental "$APK"

echo "Launching..."
"$ADB" shell am start -n com.lumin.baihua/crc649890283e1058254e.MainActivity
