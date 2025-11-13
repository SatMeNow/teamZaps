# Team Zaps

A Telegram bot built with .NET 9, featuring dependency injection, hosted services, and structured logging.

## Features

- ✅ **Host Builder Pattern** - Uses `IHostBuilder` for proper application lifecycle management
- ✅ **Dependency Injection** - Full DI support with `Microsoft.Extensions.DependencyInjection`
- ✅ **Structured Logging** - Serilog integration for comprehensive logging
- ✅ **Configuration Management** - JSON and environment variable configuration support
- ✅ **Background Service** - Telegram bot runs as a hosted background service
- ✅ **Modern C#** - Built with .NET 9 and nullable reference types enabled

## Project Structure

```
src/teamZaps/
├── Configuration/           # Configuration models
│   └── TelegramSettings.cs
├── Handlers/               # Telegram update handlers
│   └── UpdateHandler.cs
├── Services/               # Background services
│   └── TelegramBotService.cs
├── Program.cs              # Application entry point with host builder
└── appsettings.json        # Configuration file
```

## Getting Started

### Prerequisites

- .NET 9.0 SDK
- A Telegram Bot Token (get one from [@BotFather](https://t.me/botfather))

### Configuration

1. Open `appsettings.json` and add your bot token:

```json
{
  "Telegram": {
    "BotToken": "YOUR_BOT_TOKEN_HERE"
  }
}
```

Alternatively, you can set the token via environment variable:

```bash
export Telegram__BotToken="YOUR_BOT_TOKEN_HERE"
```

### Build and Run

```bash
cd src/teamZaps
dotnet restore
dotnet build
dotnet run
```

## Available Commands

The bot currently supports these commands:

- `/start` - Start the bot and see welcome message
- `/help` - Display available commands
- `/about` - Show information about the bot

## Architecture

### Host Builder

The application uses the Generic Host (`IHost`) which provides:
- Dependency injection
- Configuration
- Logging
- Hosted service lifecycle management

### Services

- **TelegramBotService**: Background service that manages the bot lifecycle
- **UpdateHandler**: Handles incoming Telegram updates (messages, commands, etc.)

### Configuration

Configuration is loaded from multiple sources in order:
1. `appsettings.json`
2. `appsettings.{Environment}.json`
3. Environment variables

## Development

### Adding New Commands

Edit `Handlers/UpdateHandler.cs` and add your command in the `HandleCommandAsync` method:

```csharp
var response = command.ToLower().Split(' ')[0] switch
{
    "/start" => "Welcome to Team Zaps! 🎯",
    "/help" => "Available commands...",
    "/yournewcommand" => "Your response here",
    _ => "Unknown command."
};
```

### Adding New Services

1. Create your service class in the `Services/` folder
2. Register it in `Program.cs` in the `ConfigureServices` section:

```csharp
services.AddSingleton<YourService>();
// or
services.AddHostedService<YourBackgroundService>();
```

## Logging

The application uses Serilog for structured logging. Logs are output to the console with timestamps and log levels.

To change the log level, edit `appsettings.json`:

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft": "Warning"
      }
    }
  }
}
```

## License

MIT
