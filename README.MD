# Team Zaps 🎯⚡

> **Split bills with Bitcoin Lightning in Telegram groups!**

Team Zaps helps groups coordinate Lightning payments for shared expenses. When Bitcoin isn't accepted at your favorite restaurant or bar, use Team Zaps to let everyone pay in sats while one person handles the fiat transaction.

## 🎲 How It Works

Your team meets up for food and drinks, but the venue doesn't accept Bitcoin? Here's how Team Zaps solves this:

1. **Start a Session** - Someone creates a Team Zaps session in your Telegram group
2. **Join & Pay** - Everyone joins the session and sends Lightning payments for their orders
3. **Lottery Draw** - One participant is randomly selected to pay the bill in fiat  
4. **Winner Takes All** - The selected person receives all the Lightning payments as compensation

### The Result 🎉
- ✅ The venue gets their worthless fiat money
- ✅ Everyone pays for their meal in Bitcoin
- ✅ The winner effectively swaps fiat for sats at market rate
- ✅ No one needs to deal with currency exchange

## 🚀 Getting Started

### 1. Add Bot to Your Group
1. Add `@TeamZapsBot` to your Telegram group (replace with your actual bot username)
2. Grant the bot admin rights to pin messages

### 2. Start Using Team Zaps

**In your group chat:**
```
/startsession - Start a new payment session
/help - View all available commands
```

**The bot will guide you through:**
- Joining the session
- Entering the lottery (who's willing to pay fiat)  
- Making Lightning payments in private chat
- Drawing the winner and executing the payout

### 3. Payment Flow

1. **Join Session** - Click the "🎯 Join" button on the pinned message
2. **Enter Lottery** - Click "🎰 Enter Lottery" if you're willing to pay the bill
3. **Send Payments** - In private chat with the bot, send amounts like:
   - `5.99 beer`
   - `12.50 pizza + 3.00 tip`
   - Multiple lines also work!
4. **Pay Invoices** - Pay the Lightning invoices the bot sends you
5. **Wait for Draw** - When the group closes the session, a winner is drawn
6. **Automatic Payout** - Winner submits Lightning invoice and receives all payments

## ⚡ Payment Examples

The bot accepts various payment formats:

```
5.99 beer                    # Euro amount with note
12.50€ lunch                 # Explicit Euro symbol  
3.00 + 2.50 tip             # Multiple amounts
4.99 coffee
1.50 cookie                  # Multiple lines
```

## 🎰 Lottery System

- Anyone can join the lottery by clicking "🎰 Enter Lottery"
- Lottery participants agree to pay the fiat bill if selected
- Winner is randomly chosen when session closes
- Winner receives ALL Lightning payments as compensation
- Fair and transparent - everyone has equal chances

## 🔧 Bot Commands

| Command | Description | Who Can Use |
|---------|-------------|-------------|
| `/startsession` | Start a new payment session | Admins (configurable) |
| `/closesession` | Close payments and draw winner | Admins (configurable) |
| `/cancelsession` | Cancel the current session | Admins |
| `/status` | View current session status | Anyone |
| `/help` | Show help information | Anyone |

## 🔒 Privacy & Security

- **Private Payments** - All payment amounts are handled in private chat
- **No Custody** - Bot doesn't hold your Bitcoin, uses Lightning invoices
- **Open Source** - Full source code available for audit
- **Group Privacy** - Only final totals are shown in group chat

## 🛠️ Self-Hosting

Want to run your own Team Zaps bot? Check the [developer documentation](src/README.md) for setup instructions.

### Requirements
- .NET 9.0 SDK
- Telegram Bot Token (from [@BotFather](https://t.me/botfather))
- LNbits instance for Lightning payments

## 🤝 Contributing

Contributions are welcome! Please read the [developer documentation](src/README.md) for technical details and contribution guidelines.

## 📞 Support

- 🐛 **Bug Reports**: Open an issue on GitHub
- 💡 **Feature Requests**: Open an issue with the "enhancement" label
- 📖 **Documentation**: Check the [developer docs](src/README.md)

## 📄 License

MIT License - see [LICENSE](LICENSE) file for details.

---

**Made with ⚡ by Bitcoiners, for Bitcoiners** 🧡
