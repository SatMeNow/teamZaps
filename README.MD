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
5.99 Beer                    # Euro amount with note
12.50€ lunch                 # Explicit Euro symbol  
3.00 Milk + 2.50 Pizza       # Multiple amounts
```

## 🎰 Lottery System

- Anyone can join the lottery by clicking "🎰 Enter Lottery"
- Lottery participants agree to pay the fiat bill if selected
- Winner is randomly chosen when session closes
- Winner receives ALL Lightning payments as compensation
- Fair and transparent - everyone has equal chances

## 🔧 Bot Commands

| Command | Description | Who can Use |
|---------|-------------|-------------|
| `/startsession` | Start a new payment session | Admins (configurable) |
| `/closesession` | Close payments and draw winner | Admins (configurable) |
| `/cancelsession` | Cancel the current session | Admins |
| `/status` | View current session status | Anyone |
| `/recover` | Recover lost sats from failed sessions | Anyone (private chat) |
| `/help` | Show help information | Anyone |
| `/diag` | Show diagnostics | Root users |

## 💰 Lost and Found Recovery

**What happens if a session fails?**

Team Zaps includes a **Lost and Found** system to protect your payments. If something goes wrong (session cancelled, network or lightning issues, unexpected errors), your sats are automatically tracked for recovery.

### How Recovery Works

1. **Automatic Detection** - Bot automatically detects when your payments are lost
2. **Notification** - You'll receive a direct message about recoverable sats
3. **Simple Recovery** - Use `/recover` command to get your sats back
4. **Instant Payout** - Submit a Lightning invoice and receive your recovery

### Using the Recovery Command

**In private chat with the bot:**

```
/recover                     # Check if you have any lost sats
```

**The bot will:**
- Show you the amount of recoverable sats
- Request a Lightning invoice for that exact amount
- Pay your invoice once verified
- Clear your recovery record

### When Does Recovery Happen?

Lost sats are automatically recorded when:
- Winner fails to submit payout invoice
- Network errors interrupt payment processing
- Lightning issues interrupt payment processing
- Unexpected session failures occur

### Important Notes

- ⏰ Recovery is available immediately after detection
- 🔔 You'll receive weekly reminders about outstanding recoveries
- 💬 Recovery only works in **private chat** with the bot

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

## Docs

- [Telegram Bot API](https://core.telegram.org/bots/api)
  - [Markdown samples](https://core.telegram.org/bots/api#markdownv2-style)

---

**Made with ⚡ by Bitcoiners, for Bitcoiners** 🧡
