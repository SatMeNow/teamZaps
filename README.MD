# Team Zaps 🎯⚡

**Split bills with Bitcoin Lightning in Telegram groups!**

Team Zaps helps groups coordinate Lightning payments for shared expenses. When Bitcoin isn't accepted at your favorite restaurant or bar, use Team Zaps to let everyone pay in sats while one person handles the fiat transaction.

## 🎲 How It Works

Your team meets up for food and drinks, but the venue doesn't accept Bitcoin? Here's how Team Zaps solves this:

1. **Start a Session** - Someone creates a Team Zaps session in your Telegram group
2. **Join & Order** - Everyone joins the session and submits their orders in private chat
3. **Payment Phase** - Once the admin closes the order phase, everyone receives an invoice and pays in sats
4. **Lottery Draw** - Once all payments are confirmed, one participant is randomly selected to pay the bill in fiat
5. **Winner Takes All** - The selected person receives all the Lightning payments as compensation for covering the fiat bill

### The Result 🎉
- ✅ The venue gets their worthless fiat money
- ✅ Everyone pays for their meal in Bitcoin
- ✅ The winner effectively swaps fiat for sats at market rate — KYC-free, no exchange required
- ✅ No one needs to deal with currency exchange

## 🚀 Getting Started

Follow the appropriate path below depending on whether you are a group admin or a regular user.

### Admins: Set up the bot for your group
1. Add the bot to your group: search for `@TeamZapsBot` and invite it.
2. Open `Group Info` → `Administrators` → Promote the bot to an administrator.
   - Grant these permissions at minimum:
     - **Delete messages**
   - Optional but recommended:
     - **Pin messages** (if pinned session status is desired)  
3. Confirm the bot appears in the admin list.
4. Configure who may start/close sessions in the bot settings (admins only by default).

### Users: Join and use Team Zaps
Once the bot is added and has necessary permissions, users can interact:

In the group chat:
```
/startzap        # Admins only (configurable)
/status          # Show current session status
/help            # Show help and commands
```

How to participate:

- Start a Session:
  - Use `/startzap` in a group chat to start a session
  - You can use `/startzap [some title]` alternatively to start a session with a custom title
    - Custom titles are useful for describing the purpose (e.g., "Satoshi's birthday party", "Team Lunch")
    - The session title is displayed in status messages, statistics, and all session-related communications

    **Examples:**
    - `/startzap` - Starts a session with the default name (group chat title)
    - `/startzap Pizza 🍕 party` - Starts a session named "Pizza 🍕 party"
    - `/startzap Team Lunch @ Joe's Restaurant` - Starts a session with a descriptive custom name
  
- Click the pinned session message's **🎯 Join** button to join a session.
- If you're willing to pay the bill in fiat, click **🎰 Enter Lottery**.
- To submit your orders, open a private chat with the bot and send your items like:
  - `5.99 beer`
  - `12.50 pizza + 3.00 tip`
  - Multiple lines are allowed.
- Once the order phase is closed, you'll receive a Lightning invoice in private chat — pay it to confirm your participation.

Payment flow summary:
1. Join session in group
2. Enter lottery (optional)
3. Submit orders in private chat
4. Order phase closes → invoices sent to all participants
5. Pay your Lightning invoice
6. All payments confirmed → winner is drawn
7. Winner submits Lightning invoice → automatic payout

## Payments and Invoices

If a winner is asked to submit a Lightning invoice for a payout, it's fine to split the total amount into multiple invoices. This helps when a wallet, route, or node limits single-invoice size.

How to split invoices:
- Create one or more Lightning invoices whose summed sats equal the requested amount.
- Send the invoices sequentially to the bot in your private chat until the total is met.
- The bot will always show you the remaining amount to claim.

Tips:
- If you encounter routing failures, try smaller invoices (for example, 50–90% of the problematic route size) and send multiple invoices.

## ⚡ Order Examples

The bot accepts various order formats in private chat:

```
5.99 Beer                    # Euro amount with note
12.50€ lunch                 # Explicit Euro symbol  
3.00 Milk + 2.50 Pizza       # Multiple amounts
```

Once the session host closes the order phase, the bot converts all orders to Lightning invoices and sends them to each participant privately.

## 🎰 Lottery System

- Anyone can join the lottery by clicking "🎰 Enter Lottery"
- Lottery participants agree to pay the fiat bill if selected
- Winner is randomly chosen when session closes
- Winner receives ALL Lightning payments as compensation
- Fair and transparent - everyone has equal chances

## 🔧 Bot Commands

### Group (use in group chats)
| Command | Description | Who can Use |
|---------|-------------|-------------|
| `/startzap` | Start a new payment session | Admins (configurable) |
| `/closezap` | Close orders and start payment phase | Admins (configurable) |
| `/cancelzap` | Cancel the current session | Admins (configurable) |
| `/config` | Change options (e.g. permissions) | Admins |
| `/status` | View current session status | Anyone |
| `/stat` | View group statistics | Admins (configurable) |

### Private (use in direct messages with the bot)
| Command | Description |
|---------|-------------|
| `/recover` | Recover lost sats from failed sessions | 
| `/stat` | View your personal statistics |
| `/help` | Show help information |
| `/about` | View bot information |

## 💰 Lost and Found Recovery

**What happens if a session fails?**

Team Zaps includes a **Lost and Found** system to protect your payments. If a session should get interrupted, your sats are automatically tracked for recovery.

Possible session interrupts may take place when:

- Winner fails to submit payout invoice
- Network errors interrupt payment processing
- Lightning issues interrupt payment processing
- Unexpected session failures occur

**How your recovery is protected:**

The bot stores your recoverable sats by mapping the amount to your Telegram user ID. These mappings updated instantly with every sent or received sat!

This means:
- 🔄 **Bot Downtime Safe** - Even if the bot goes offline, your recovery data is preserved and will be available once the bot becomes operational again
- 🔒 **Privacy Protected** - Only your numeric Telegram ID is stored, no names, usernames, or personal information
- 💾 **Persistent Storage** - Your recovery remains available until you claim it, regardless of downtimes, server restarts or maintenance
- 🔔 **Reminder System** - You'll receive 3 reminders (one per day) about outstanding recoveries

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

**Splitting Recovery into Multiple Invoices:**

You can split it into multiple invoices. This helps when a wallet, route, or node limits single-invoice size.

How to split invoices:
- Create one or more Lightning invoices whose summed sats equal the requested amount.
- Send the invoices sequentially to the bot in your private chat until the total is met.
- The bot will always show you the remaining amount to claim.

Tips:
- If you encounter routing failures, try smaller invoices (for example, 50–90% of the problematic route size) and send multiple invoices.

## 📊 Statistics & Insights

Team Zaps provides statistics to help admins plan Lightning liquidity, enable friendly competition between groups, and give users a personal overview.

**Privacy-first design**: Only numeric Telegram user IDs are stored. No names, usernames, or personal information is ever collected.

### Personal Statistics

**In private chat with the bot:**

```
/stat                        # View your personal statistics
```

**What you'll see:**
- **Overall**: Total sessions joined, duration, total sats spent, tips given
- **Averages**: Sats per session and per block
- **History**: When you first used the bot (blockchain height)

Statistics are calculated automatically after each completed session. They're purely for your own reference and planning.

### Group Statistics

**In the group chat:**

```
/stat                        # View group statistics anytime
```

**What the group sees:**
- **Session Stats**: If a session is active, shows current session details
- **Group History**: All-time totals including sessions, participant count, and sats spent
- **Averages**: Sats per block, per session, and per participant
- **Tips**: Total tips given and percentage
- **Monthly Activity**: Recent participation trends

Group statistics help:
- ⚡ **For admins**: Estimate Lightning liquidity requirements based on actual usage patterns
- 🏆 **For fun**: Friendly competition between groups
- 📊 **For everyone**: Understand group activity and participation levels

**Note**: Statistics are automatically posted to the group after each completed session. Admins can configure whether users can manually view statistics anytime using the `/stat` command.

## �🔒 Privacy & Security

- **Private Payments** - All payment amounts are handled in private chat- **No Personal Data** - Only numeric user IDs stored, no names or usernames- **No Custody** - Bot doesn't hold your Bitcoin, uses Lightning invoices
- **Open Source** - Full source code available for audit
- **Group Privacy** - Only final totals are shown in group chat

## 🤝 Contributing

Contributions are welcome

Please read the [developer documentation](src/README.md) for technical details and contribution guidelines.
You will find the [repository](https://github.com/SatMeNow/teamZaps) on github.

## 📄 License

MIT License - see [LICENSE](LICENSE) file for details.

## 📞 Support

- 🐛 **Bug Reports**: Create an [issue](https://github.com/SatMeNow/teamZaps/issues) on GitHub
- 💡 **Feature Requests**: Create an [issue](https://github.com/SatMeNow/teamZaps/issues) on GitHub
- 📖 **Documentation**: Check the [developer documents](src/README.md) or the [user documents](README.md)

## Related documents

- [Telegram Bot API](https://core.telegram.org/bots/api)
  - [Markdown samples](https://core.telegram.org/bots/api#markdownv2-style)

## 💜 Support the Project

Team Zaps is built on **value for value**. If you find this bot useful and it's providing value to you and your community, you can choose to return value in whatever way feels right to you. Thank you for supporting this development 🙏

Your contribution helps:
- 🔧 Keep the bot running and maintained
- ⚡ Add new features and improvements
- 🐞 Fix bugs and enhance stability

<div align="center">

![Donation QR Code](docs/4v4-qr.png)

**Lightning Address:** `satmenow@getalby.com`

</div>

---

🧑‍💻 **Made with ⚡ by Bitcoiners, for Bitcoiners** 🧡
