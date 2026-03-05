#!/bin/bash

# Team Zaps - Run Script
# This script helps you quickly run the Telegram bot

PROJECT_DIR="src"
PROJECT_NAME="teamZaps"

echo "🚀 Team Zaps - Telegram Bot"
echo "============================"
echo ""

# Check if .NET is installed
if ! command -v dotnet &> /dev/null; then
    echo "❌ Error: .NET SDK is not installed"
    echo "Please install .NET 9.0 SDK from: https://dotnet.microsoft.com/download"
    exit 1
fi

# Check .NET version
DOTNET_VERSION=$(dotnet --version)
echo "✅ .NET SDK version: $DOTNET_VERSION"

# Navigate to project directory
cd "$PROJECT_DIR"

# Check if bot token is configured
BOT_TOKEN=$(grep -A 1 '"BotToken"' appsettings.json | grep -v "BotToken" | cut -d'"' -f4)
if [ "$BOT_TOKEN" == "YOUR_BOT_TOKEN_HERE" ] && [ -z "$Telegram__BotToken" ]; then
    echo ""
    echo "⚠️  Warning: Bot token is not configured!"
    echo ""
    echo "Please configure your bot token by either:"
    echo "1. Editing src/appsettings.json"
    echo "2. Setting environment variable: export Telegram__BotToken=\"YOUR_TOKEN\""
    echo ""
    echo "Get your token from @BotFather on Telegram"
    echo ""
    exit 1
fi

# Build the project
echo ""
echo "🔨 Building project..."
dotnet build --configuration Release

if [ $? -eq 0 ]; then
    echo "✅ Build successful!"
    echo ""
    echo "🤖 Starting bot..."
    echo "Press Ctrl+C to stop"
    echo ""
    dotnet run --configuration Release
else
    echo "❌ Build failed!"
    exit 1
fi
