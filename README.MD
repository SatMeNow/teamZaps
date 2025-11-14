# Team Zaps - What is it?

Your team meets up, have some food and drinks, but bitcoin is not accepted at your favored location?!

Than try _team zaps_ to coordinate your lightning payments:

1. Create a _team event_.

2. All participants fund the event while ordering their drinks.

3. One of all participants will be drawn in the lottery to pay the bill for all participants, while receiving all spent funds.

Finally we end up in this situation:

✅ the host got his wortless fiat

✅ Every pleb paid his bill in sats

✅ The drawn pleb, who paid the host, defacto swapped fiat in sats 😎


# Use the bot in a telegram group

- [ ] Add the `TeamZapsBot` into your group.

- [ ] Grant the bot admin rights.

# Project Summary

## 🚀 Quick Commands

### First Time Setup
```bash
cd src/teamZaps
# Edit appsettings.json with your bot token
dotnet restore
dotnet build
```

### Run the Bot
```bash
# Option 1: Direct
cd src/teamZaps && dotnet run

# Option 2: Using script
./run.sh
```

### Development
```bash
# Watch mode (auto-reload on changes)
cd src/teamZaps && dotnet watch run

# Build only
dotnet build

# Clean build
dotnet clean && dotnet build
```

## 🔑 Configuration Required

Before running, you **must** configure your Telegram bot token:

### Method 1: Edit appsettings.json
```json
{
  "Telegram": {
    "BotToken": "1234567890:ABCdefGHIjklMNOpqrsTUVwxyz"
  },
  "Lnbits": {
    "LndhubUrl": "https://myLnbits.org/lndhub/ext/",
    "WalletId": "1234567890ABCdefGHIjklMNOpqrsTUV",
    "ApiKey": "1234567890ABCdefGHIjklMNOpqrsTUV"
  }
}
```

### Method 2: Environment Variable
```bash
export Telegram__BotToken="1234567890:ABCdefGHIjklMNOpqrsTUVwxyz"
```

**Get your token from:** [@BotFather](https://t.me/botfather) on Telegram

## 🧪 Testing Your Bot

1. Start the application
2. Open Telegram
3. Find your bot by username
4. Send these commands:
   - `/help` → Command list
   - `/about` → Bot info
   - Any text → Echo response

## 🐛 Troubleshooting

### Build Fails
```bash
dotnet clean
dotnet restore
dotnet build
```

### Bot Doesn't Respond
- Check bot token is correct
- Verify bot is not already running elsewhere
- Check Telegram API status
- Review application logs

### Permission Issues
```bash
chmod +x run.sh
```

## 🔒 Security

**⚠️ Important: Never commit your bot token!**

- Use `.gitignore` (already configured)
- Use environment variables in production
- Consider using Azure Key Vault or similar for production

## 📖 Resources

- [Telegram Bot API](https://core.telegram.org/bots/api)
- [Telegram.Bot Library](https://github.com/TelegramBots/Telegram.Bot)

## 📝 License

MIT
