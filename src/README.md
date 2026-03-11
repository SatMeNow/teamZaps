# Team Zaps - Developer Documentation 🛠️

> **Lightning payment coordination bot built with .NET 9**

This document provides technical information for developers who want to understand, modify, or contribute to Team Zaps.

## 🏗️ Architecture Overview

Team Zaps is a sophisticated Telegram bot that coordinates Lightning Network payments for group bill splitting. It's built using modern .NET practices with a clean, maintainable architecture.

### Key Features

- ✅ **Enterprise-Grade Architecture** - Built with .NET 9 Host Builder pattern
- ✅ **Dependency Injection** - Full DI container with proper service lifetimes  
- ✅ **Background Services** - Payment monitoring and bot lifecycle management
- ✅ **Lightning Integration** - LNbits API for invoice creation and payment processing
- ✅ **Session Management** - Concurrent session handling across multiple groups
- ✅ **Message Lifecycle** - Sophisticated message tracking and updates
- ✅ **Structured Logging** - Serilog with contextual logging throughout
- ✅ **Modern C#** - Nullable reference types, pattern matching, records
- ✅ **Payment Parser** - Advanced regex-based payment parsing with memo support

## 📁 Project Structure

```
src/
├── Configuration/                 # Configuration models and settings
│   ├── TelegramSettings.cs       # Bot token configuration
│   ├── LnbitsSettings.cs         # Lightning service configuration  
│   ├── BotBehaviorOptions.cs     # Runtime behavior settings
│   └── DebugSettings.cs          # Debug-only configuration (DEBUG builds)
├── Handlers/                     # Telegram update processing
│   ├── UpdateHandler.cs          # Main update router (partial class)
│   ├── UpdateHandler.DirectMessage.cs    # Private message handling
│   └── UpdateHandler.Session.cs          # Group session commands
├── Services/                     # Background and integration services
│   ├── TelegramBotService.cs     # Main bot service lifecycle
│   ├── PaymentMonitorService.cs  # Background payment monitoring
│   ├── RecoveryService.cs        # Lost sats recovery system
│   └── Backends/                 # Pluggable backend implementations
│       ├── Backend.cs            # Backend interfaces and base types
│       ├── Backend.AlbyHub.cs    # AlbyHub NWC backend
│       ├── Backend.Lnbits.cs     # LNBits REST API backend
│       ├── Backend.CoinGecko.cs  # CoinGecko exchange rate backend
│       └── Backend.ElectrumX.cs  # ElectrumX blockchain data backend
├── Sessions/                     # Core session management
│   ├── SessionManager.cs         # Session storage and lifecycle
│   ├── SessionState.cs           # Session and participant models
│   ├── SessionWorkflowService.cs # Session workflow logic
│   └── PaymentMonitorService.cs  # Background payment monitoring
├── Helper/                       # Specialized message builders
│   ├── MessageHelper.Status.cs   # Session status messages
│   ├── MessageHelper.Payment.cs  # Lightning invoice messages
│   ├── MessageHelper.Winner.cs   # Winner announcement messages
│   ├── MessageHelper.Summary.cs  # Payment summary messages
│   └── PaymentParser.cs          # Payment amount parsing logic
├── Utils.cs                      # Extension methods and utilities
├── Common.cs                     # Custom attributes and enums
├── GlobalUsings.cs              # Global using statements
└── Program.cs                   # Application entry point
```

## 🔧 Configuration

### Lightning Backend

Team Zaps supports multiple Lightning backend implementations through a common `ILightningBackend` interface. **The first backend configured in the `Backends` section will be selected and used.**

#### AlbyHub Backend (NWC/NIP-47)

AlbyHub uses the **Nostr Wallet Connect (NWC)** protocol based on NIP-47 for communication over Nostr relays. This provides a decentralized approach to Lightning wallet integration.

```json
{
  "Backends": {
    "AlbyHub": {
      "ConnectionString": "nostr+walletconnect://PUBKEY?relay=wss://relay.getalby.com/v1&secret=SECRET",
      "RelayUrls": [ "wss://relay.getalby.com/v1" ]
    }
  }
}
```

**Configuration:**
- `ConnectionString` - NWC connection URI from AlbyHub wallet settings (format: `nostr+walletconnect://PUBKEY?relay=RELAY_URL&secret=SECRET`)
- `RelayUrls` - Specify relay URLs from connection string

**How to get the connection string:**
1. Open your AlbyHub wallet
2. Go to Connections → Create new connection
3. Select "Nostr Wallet Connect"
4. Copy the `nostr+walletconnect://...` URI

#### LNBits Backend (REST API)

LNBits uses a traditional REST API for Lightning operations. Requires a running LNbits instance.

```json
{
  "Backends": {
    "LNBits": {
      "LndhubUrl": "https://your-lnbits.com/lndhub/ext/",
      "ApiKey": "YOUR_LNBITS_API_KEY"
    }
  }
}
```

**Configuration:**
- `LndhubUrl` - LNDhub extension URL (must end with `/lndhub/ext/`)
- `ApiKey` - Invoice/read key from your LNbits wallet

#### Exchange Rate Backends

**Yadio Backend (Recommended)**

Yadio provides free BTC exchange rate data for fiat currency support.

```json
{
  "Backends": {
    "Yadio": { }
  }
}
```

**Configuration:**
- No settings required - works out of the box
- Automatically fetches fiat exchange rates
- Used by lightning backends to support fiat currency invoices
- Rate limit: 100 requests per minute

---

**Legacy Backends (CoinCap & CoinGecko)**

These backends now require paid API keys:

```json
{
  "Backends": {
    "CoinCap": {
      "ApiKey": "your_coincap_api_key_here"
    },
    "CoinGecko": {
      "ApiKey": "your_coingecko_api_key_here"
    }
  }
}
```

#### ElectrumX Backend (Blockchain Data)

ElectrumX provides real-time Bitcoin blockchain data including current block height and timestamps through persistent connections with automatic subscription to new blocks.

**Connection Strategy:**
- Uses a **single active connection** with automatic failover to backup hosts
- Maintains persistent connection with keepalive pings
- Subscribes to real-time block notifications for instant updates
- Automatically fails over to next configured host on connection issues
- Recommended to configure **3-5 hosts** for optimal reliability

**Configuration:**
- `Hosts` - Array of ElectrumX servers. Supports `HOST:PORT` or `HOST` notation per entry (required)
- `Port` - Default server port (50001 for TCP, 50002 for SSL). Used when port is omitted from host notation. **SSL is automatically used for port 50002**
- `ValidateSslCertificate` - Whether to validate SSL/TLS certificates. Set to `false` to accept self-signed certificates (required for many public servers)
- `Timeout` - Connection and request timeout in milliseconds

**Host Configuration Example**

```json
{
  "Backends": {
    "ElectrumX": {
      "Hosts": [
        "electrum.blockstream.info",
        "fulcrum.sethforprivacy.com",
        "electrum.emzy.de",
        "bitcoin.lu.ke",
        "bitcoin.grey.pw"
      ],
      "Port": 50002,
      "ValidateSslCertificate": false,
      "Timeout": 10000
    }
  }
}
```

The bot will connect to the first host and maintain that connection. If it fails, the bot automatically switches to the next configured host and continues operation seamlessly.

**Public ElectrumX Servers:**

For a comprehensive list of public ElectrumX servers with real-time status monitoring, see: **[1209k Bitcoin ElectrumX Monitor](https://1209k.com/bitcoin-eye/ele.php)**

Here are some reliable servers to get started:

- `electrum.blockstream.info:50001` (TCP) / `:50002` (SSL)
- `electrum.qtornado.com:50001` (TCP) / `:50002` (SSL)
- `bitcoin.aranguren.org:50001` (TCP) / `:50002` (SSL)
- `electrum.emzy.de:50001` (TCP) / `:50002` (SSL)
- `kirsche.emzy.de:50002` (SSL only)
- `bitcoin.lu.ke:50001` (TCP) / `:50002` (SSL)
- `fulcrum.sethforprivacy.com:50002` (SSL only)
- `bitcoin.grey.pw:50001` (TCP) / `:50002` (SSL)

### Bot Behavior Options

The `BotBehavior` section controls various aspects of bot behavior:

#### MaxBudget
Controls the maximum total lottery budget (in Euro) across all active sessions.
- **Default**: `disabled`

When the limit is reached, new users cannot join lotteries until existing sessions complete or are cancelled.

```json
{
  "BotBehavior": {
    "MaxBudget": 5000.0
  }
}
```

#### MaxLockedSats
Controls the maximum total *locked sats* across all active sessions server-wide.
- **Default**: `disabled` (no limit)

This setting helps avoid locking too much on-chain liquidity at once. When the configured limit is reached, actions that would increase the locked sats (for example creating new sessions or making payments) will be refused.

```json
{
  "BotBehavior": {
    "MaxLockedSats": 1000000.0
  }
}
```

#### MaxEstimatedLockedSats
Controls the maximum total *estimated locked sats* across all active sessions server-wide.
- **Default**: `disabled` (no limit)

Estimated locked sats is a heuristic based on historical group- and user-statistics and can be used to provide a buffer for expected future locked sats. When the configured limit is reached, the bot will refuse actions that would increase the estimated locked sats (for example creating new sessions or making payments) above the limit.

```json
{
  "BotBehavior": {
    "MaxEstimatedLockedSats": 2000000.0
  }
}
```

#### MaxParallelSessions
Controls the maximum number of concurrent sessions allowed server-wide.
- **Default**: `disabled` (unlimited sessions)

When the limit is reached, new sessions cannot be started until existing sessions complete or are cancelled. This maybe helps manage server load and resource usage, but also to preserve the lightning backend.

```json
{
  "BotBehavior": {
    "MaxParallelSessions": 10
  }
}
```

#### Locale
Controls the locale/culture used system-wide for formatting and localization.
- **Default**: `en-US`

This setting controls:
- Number formatting (thousands separators, decimal separators)
- Currency display
- Date and time formatting

```json
{
  "BotBehavior": {
    "Locale": "de-DE"
  }
}
```

**Supported Locales:** `en-US`, `de-DE`, `it-IT`, `fr-FR`, `es-ES`, `pt-BR`, `ja-JP`, `ko-KR`, `zh-CN`, `zh-TW`, etc.

#### SanityCheckTime
Controls the time of day when backend sanity checks are performed.
- **Default**: `03:00:00` (3:00 AM)

Sanity checks verify backend connectivity and operational status. The bot will attempt to reconnect to backends if checks fail.

```json
{
  "BotBehavior": {
    "SanityCheckTime": "03:00:00"
  }
}
```

### Recovery Settings

Configure the lost sats recovery system for interrupted sessions.

**Configuration:**
- `Enable` - Enable or disable the recovery system (default: `true`)
- `DailyScanTime` - Time of day when the recovery scan runs in HH:MM:SS format (default: `08:00:00`)

```json
{
  "Recovery": {
    "Enable": true,
    "DailyScanTime": "03:00:00"
  }
}
```

When disabled, all recovery operations are stopped: no lost sats records are created, existing recovery files are not processed, and background scanning is skipped. Schedule scans during low-traffic hours and adjust timing based on your user time zones

## 🧠 Core Concepts

### Session Lifecycle

```mermaid
graph TD
    A[Start Session] --> B[Waiting for Lottery]
    B --> C[Someone Enters Lottery]
    C --> D[Accepting Orders]
    D --> E[Session Host Closes Orders]
    E --> F[Invoices Sent to Participants]
    F --> G[Waiting for Payments]
    G --> H[All Payments Confirmed]
    H --> I[Draw Winner]
    I --> J[Winner Submits Invoice]
    J --> K[Execute Payout]
    K --> L[Session Completed]
```

### Payment Flow

1. **User Input** - Natural language parsing (`"5.99 Beer"`) submitted as an **order** during `AcceptingOrders` phase
2. **Order Registration** - Structured `OrderRecord` objects stored per participant with amounts and memos
3. **Order Phase Closes** - Session host calls `/closezap`; the bot converts all orders to Lightning invoices
4. **Invoice Creation** - One consolidated Lightning invoice created per participant; session enters `WaitingForPayments`
5. **Payment Monitoring** - Background service polls payment status
6. **Confirmation** - UI updates and session state changes; once all invoices are paid the lottery draw runs automatically

### Lost and Found Recovery System

Team Zaps includes a comprehensive **Lost and Found** recovery system to protect users from losing sats due to interrupted sessions, network issues, or other failures.

#### How It Works

**Automatic Detection:**
- When sessions fail or are cancelled unexpectedly, user payments are automatically recorded as "lost sats"
- Background service scans for lost payments periodically
- Recovery records are stored as JSON files in the `data/lostSats/` directory

**User Notification:**
- Users with lost sats receive automatic notifications via direct message
- Notifications include recovery amount and instructions to clain lost sats
- Notifications are sent up to 3 times before the bot stops trying (sent during daily recovery scans)

### Message Management

Team Zaps employs sophisticated message lifecycle management:

- **Status Messages** - Pinned group messages showing session state
- **User Messages** - Private messages with personal status and controls
- **Payment Messages** - Lightning invoice messages with QR codes
- **Winner Messages** - Lottery result announcements
- **Summary Messages** - Complete payment breakdowns for winners

## 🔧 Key Services

### Backend Architecture

Team Zaps uses a **pluggable backend architecture** that allows different service providers to be swapped without changing application code. Backends implement feature-specific interfaces and are automatically discovered via attributes.

#### Backend Interface Pattern

All backends must:
1. **Implement one or more backend interfaces** based on provided features:
   - `ILightningBackend` - Lightning wallet operations (create/pay invoices, check status)
   - `IExchangeRateBackend` - Cryptocurrency exchange rate lookups

2. **Decorate the class** with `[BackendDescription("BackendName")]` attribute:
   ```csharp
   [BackendDescription("AlbyHub")]
   public class AlbyHubService : ILightningBackend
   {
       // Implementation...
   }
   ```

#### Available Backends

**Lightning Backends:**
- **AlbyHub** - NWC (Nostr Wallet Connect) using NIP-47 protocol
  - Implements: `ILightningBackend`
  - Configuration: `Backends:AlbyHub` section
  - Features: Invoice creation, payment, status checks via Nostr relays

- **LNBits** - Traditional REST API integration
  - Implements: `ILightningBackend`
  - Configuration: `Backends:LNBits` section  
  - Features: Full Lightning operations with fiat currency support

**Exchange Rate Backends:**
- **Yadio** - Free cryptocurrency price data (Recommended)
  - Implements: `IExchangeRateBackend`
  - Configuration: `Backends:Yadio` section (no settings required)
  - API: Yadio.io API (no authentication required)
  - Features: BTC/USD, BTC/EUR and other fiat currency exchange rates
  - Rate limits: 100 requests per minute
  - Status: ✅ Active and maintained

- **CoinCap** - Cryptocurrency price data (Deprecated)
  - Implements: `IExchangeRateBackend`
  - Configuration: `Backends:CoinCap` section
  - API: CoinCap API v2 (requires paid API key)
  - Features: BTC/USD prices with automatic EUR conversion rate updates
  - Status: ⚠️ Free tier no longer available, API key required

- **CoinGecko** - Cryptocurrency price data (Deprecated)
  - Implements: `IExchangeRateBackend`
  - Configuration: `Backends:CoinGecko` section
  - API: CoinGecko API v3 (requires paid API key)
  - Features: Native BTC/USD and BTC/EUR rates
  - Status: ⚠️ Free tier no longer available, API key required

**Blockchain Data Backends:**
- **ElectrumX** - Bitcoin blockchain information via ElectrumX protocol
  - Implements: `IBackend` (can be extended for specific interfaces)
  - Configuration: `Backends:ElectrumX` section
  - Protocol: JSON-RPC 2.0 over TCP
  - Features: Current block height/time, block headers, server info
  - Note: NBitcoin does NOT include ElectrumX support - this is a custom implementation

#### Backend Selection

Backends are automatically registered based on configuration:
```json
{
  "Backends": {
    "AlbyHub": { /* config */ },  // ← First backend is selected
    "LNBits": { /* config */ }
  }
}
```

The first configured backend in each category is automatically selected and injected into services.

#### Adding New Backends

To add a new backend:

1. Create `Backend.YourService.cs` in `Services/Backends/`
2. Implement required interface(s) (`ILightningBackend`, `IExchangeRateBackend`, etc.)
3. Add `[BackendDescription("YourService")]` attribute

**Example - Multi-Feature Backend:**
```csharp
[BackendDescription("SuperWallet")]
public class SuperWalletService : ILightningBackend, IExchangeRateBackend
{
    // Implements both Lightning operations AND exchange rates
}
```

### SessionManager
```csharp
// Central session storage and participant management
var session = sessionManager.GetSessionByChat(chatId);
var participant = sessionManager.GetOrAddParticipant(session, userId, displayName);
```

### PaymentMonitorService
```csharp
// Background service checking payment status every 5 seconds
// Automatically updates UI when payments are confirmed
// Handles cleanup of help messages and status updates
```

### RecoveryService
```csharp
// Background service for lost sats recovery
// - Runs periodic scans every 6 hours
// - Notifies users about pending recoveries
// - Manages recovery file storage in data/lostSats/ directory
// - Registered as both Singleton and HostedService
public class RecoveryService : BackgroundService
{
    // Record lost sats for interrupted payments
    public async Task RecordLostSatsAsync(ParticipantState participant, string reason);
    
    // Clear recovery record after successful recovery
    public Task ClearLostSatsAsync(long userId);
    
    // Get user's lost sats record
    public Task<LostSatsRecord?> TryGetLostSatsAsync(long userId);
    
    // Get all pending recoveries (for diagnostics)
    public Task<ICollection<LostSatsRecord>> GetAllLostSatsAsync();
    
    // Scan for lost sats and notify users
    public async Task ScanForLostSatsAsync();
}
```

### StatisticService
```csharp
// Comprehensive statistics tracking for users, groups, and platform-wide metrics
// Automatically updates after each completed session
// Data persisted to disk in data/stats/ directory
// Registered as both Singleton and HostedService

public class StatisticService : IHostedService
{
    // Platform-wide statistics (null until first session completes)
    public GeneralStatistics? GeneralStats { get; set; }
    
    // Per-group statistics indexed by chat ID
    public IReadOnlyDictionary<long, GroupStatistics> GroupStats { get; }
    
    // Per-user statistics indexed by user ID
    public IReadOnlyDictionary<long, UserStatistics> UserStats { get; }
    
    // Group rankings (null if fewer than 3 groups)
    public GroupRankingStatistics? GroupRanking { get; set; }
    
    // Total unique participants across all sessions
    public int Participants { get; }
    
    // Update statistics after session completion
    public Task<bool> UpdateStatisticsAsync(SessionState session);
}
```

**Statistics File Storage:**

Statistics are persisted to disk using the `FileService<T>` pattern:

```
data/
├── stats/
    ├── general.json                    # GeneralStatistics
    ├── group/
    │   ├── group_{groupId}.json        # GroupStatistics per group
    ├── user/
        ├── user_{userId}.json          # UserStatistics per user
```

**Key Features:**

1. **Automatic Updates**: Statistics update automatically after each session via `SessionWorkflowService`
2. **Block-based Duration**: All durations measured in Bitcoin blocks for consistency
3. **Monthly Trends**: Tracks monthly metrics with 5-year retention
4. **Liquidity Metrics**: Tracks sats per block, per session and per participant to understand spending patterns
5. **Performance Metrics**: Monitors max parallel sessions and peak concurrent sats
6. **Group Rankings**: Top 10 groups by various metrics (requires ≥3 groups)

### LiquidityLogService
```csharp
// Lightweight CSV logging service for monitoring liquidity locked in active sessions
// Appends timestamped snapshots to a CSV file for external analysis
// File can be opened in Excel, Google Sheets, or any standard spreadsheet tool

public class LiquidityLogService
{
    // Log current locked sats snapshot
    public Task LogAsync(CancellationToken cancellationToken = default);
}
```

**CSV Log Format:**

```csv
Timestamp,Sessions,Satoshis,Euros
2026-01-03T14:30:45Z,3,45000,15.50
2026-01-03T14:35:00Z,5,75000,25.80
```

**Log File Location:**

```
data/
├── log/
    └── liquidity.csv               # Time-series liquidity monitoring
```

**Key Features:**

1. **Lightweight Design**: Single `AppendAllTextAsync` call per snapshot - minimal overhead
2. **Standard Format**: CSV with comma delimiter (`,`) for European locale compatibility
3. **ISO 8601 Timestamps**: Sortable, timezone-aware UTC timestamps
4. **Thread-Safe**: Semaphore-based locking prevents concurrent write conflicts
5. **External Analysis**: Can be opened in Excel, imported to databases, or analyzed with scripts
6. **Liquidity Monitoring**: Track how many sats are locked across active sessions over time
7. **Automatic Headers**: Creates file with column headers on first write

**Use Cases:**
- Monitor peak liquidity requirements for Lightning backend sizing
- Analyze session concurrency patterns over time
- Track historical liquidity trends for capacity planning
- Export data to external monitoring/alerting systems
- Generate time-series charts in Excel or data visualization tools

**Implementation Notes:**
- No automatic scheduling - must be invoked explicitly from calling code

### Lightning Backend (ILightningBackend)
```csharp
// Abstracted Lightning Network integration
// Automatically uses the first configured backend (AlbyHub or LNBits)
var invoice = await lightningBackend.CreateInvoiceAsync(amount, "EUR", memo);
var status = await lightningBackend.CheckPaymentStatusAsync(paymentHash);
var result = await lightningBackend.PayInvoiceAsync(bolt11Invoice);
```

**Available Backends:**
- **AlbyHub** - Uses NWC (Nostr Wallet Connect) with NIP-47 protocol over Nostr relays
- **LNBits** - Uses REST API for LNbits instances

### PaymentParser
```csharp
// Advanced payment string parsing with regex
if (PaymentParser.TryParse("5.99 beer + 2.50 pizza", out var tokens, out var error))
{
    // tokens contain structured PaymentToken objects
    // Supports: amounts, currencies, memos, multiple formats
}
```

## 🛠️ Self-Hosting

Self-hosting is the recommended way to set up your development environment. By running the bot locally, you have full control over configuration, can test changes instantly, and debug issues in real-time.

### Prerequisites

Required:

- .NET 9.0 SDK
- Telegram Bot Token (from [@BotFather](https://t.me/botfather))
- Lightning Backend:
  - LNbits instance, OR
  - AlbyHub with NWC connection

Optional but recommended

- VS Code or Visual Studio
- Git

### BotFather Configuration

Configure your bot commands in @BotFather using the `/setcommands` command:

```
stat - Show personal statistics
recover - Recover lost sats from interrupted sessions
help - Show help and available commands
about - About this bot
```

### Setup Steps

1. **Clone and Build**
```bash
git clone <repository-url>
cd TeamZaps/src
dotnet restore
dotnet build
```

2. **Configure Services**

Create `appsettings.Development.json`:
```json
{
  "Telegram": {
    "BotToken": "YOUR_BOT_TOKEN_FROM_BOTFATHER",
    "RootUsers": [ 123456789 ]
  },
  "Backends": {
    "LNBits": {
      "LndhubUrl": "YOUR_LNDHUB_URL_HERE",
      "ApiKey": "YOUR_API_KEY_HERE"
    },
    "AlbyHub": {
      "ConnectionString": "YOUR_NWC_CONNECTION_STRING_HERE",
      "RelayUrls": [ "YOUR_RELAY_URLS_HERE" ]
    }
  },
  "BotBehavior": {
    "BudgetChoices": [50, 100, 150, 200, 250, 300],
    "MaxBudget": 10000.0
  },
  "Debug": {
    "FixBudget": 5.0
  }
}
```

3. **Run Development Server**
```bash
# Standard run
dotnet run

# Watch mode (auto-reload)
dotnet watch run

# With specific environment
ASPNETCORE_ENVIRONMENT=Development dotnet run
```

## 🧪 Development Workflow

### Adding New Features

1. **Plan the User Experience** - How should users interact with your feature?
2. **Design the Data Model** - What state needs to be tracked? 
3. **Implement Message Handlers** - How does Telegram input get processed?
4. **Build Message Builders** - How is information presented to users?
5. **Add Background Processing** - What happens asynchronously?
6. **Write Tests** - Ensure reliability and prevent regressions

### Code Style Guidelines

```csharp
// ✅ Good: Use expression-bodied members
public bool HasPayments => !Payments.IsEmpty();

// ✅ Good: Use pattern matching 
var status = phase switch
{
    SessionPhase.WaitingForPayments => "Ready for payments",
    SessionPhase.Completed => "Session finished",
    _ => "Unknown status"
};

// ✅ Good: Use nullable reference types
public string? WinnerInvoiceBolt11 { get; set; }

// ✅ Good: Use StringBuilder for complex message building
var message = new StringBuilder();
message.AppendLine("🎯 Session Status");
message.AppendLine($"Phase: *{session.Phase}*");
return message.ToString();

// ✅ Good: Use 'is null' patterns consistently
if (participant.StatusMessageId is null)
    return;
```

### Message Helper Patterns

```csharp
internal static class YourMessageHelper
{
    public static async Task<Message> SendAsync(...) 
    {
        // Create and send new message
        // Store message ID in session state
        // Return message for further processing
    }
    
    public static async Task UpdateAsync(...) 
    {
        // Edit existing message
        // Handle deletion/recreation if needed
        // Graceful error handling with logging
    }
    
    private static string Build(...) 
    {
        // Use StringBuilder for message construction  
        // Keep all UI text generation here
        // Support different states/contexts
    }
}
```

## 🔍 Debugging & Troubleshooting

### Debug Configuration

The `DebugSettings` class provides development-time configuration options that are only available in DEBUG builds:

```csharp
// Configuration/DebugSettings.cs
public class DebugSettings
{
    public const string SectionName = "Debug";

#if DEBUG
    /// <summary>
    /// Pre-configured budget for users when joining the lottery.
    /// </summary>
    public double? FixBudget { get; set; }
#endif
}
```

All debug settings will apply when set in `appsettings.Development.json`.

#### Debug.FixBudget

This automatically assigns a default budget to users joining the lottery, bypassing the budget selection UI:

```json
{
  "Debug": {
    "FixBudget": 5.0  // Users get 5€ budget automatically
  }
}
```

### Common Issues

**Bot doesn't respond:**
```bash
# Check logs for errors
dotnet run
# Look for "Bot initialized successfully" message
# Verify bot token in appsettings
```

**Payment monitoring not working:**
```bash
# Verify LNbits configuration
# Check LNbits API connectivity  
# Monitor PaymentMonitorService logs
```

**Message updates failing:**
```bash
# Check for "message to edit not found" errors
# Verify message IDs are stored correctly
# Look for Telegram API rate limiting
```

### Logging Configuration

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft": "Warning",
        "System": "Warning",
        "TeamZaps.Sessions.PaymentMonitorService": "Debug"
      }
    }
  }
}
```

### Performance Monitoring

- Session count: `sessionManager.ActiveSessions.Count`
- Payment monitoring: Check logs for polling frequency
- Memory usage: Monitor `ConcurrentDictionary` sizes
- API calls: Track LNbits request/response times

## 🧪 Testing

### Unit Testing Structure
```bash
# Recommended test structure (not yet implemented)
tests/
├── Unit/
│   ├── PaymentParserTests.cs
│   ├── SessionManagerTests.cs  
│   └── MessageHelperTests.cs
├── Integration/
│   ├── LnbitsServiceTests.cs
│   └── TelegramBotTests.cs
└── TestHelpers/
    ├── MockTelegramBot.cs
    └── TestSessionFactory.cs
```

### Screenshot Generation

Mock screenshots for the user docs are generated in two steps:

**1. Send mock messages** — run the bot locally and call `Sample_Screenshots.SendStatusScreenshotsAsync()`. This sends labeled message pairs (`📸 \`step-XX-name\`` followed by the actual mock message) to the configured root user's DM.

**2. Capture screenshots** — use the Playwright script in `tools/screenshots/`:

```bash
cd tools/screenshots
npm install
npx playwright install chromium
node screenshot.js
```

Workflow:
1. A browser opens — log in with your Telegram account (QR code on first run; session is cached in `.session/`).
2. Navigate to the bot's DM chat and press Enter.
3. Click the first mock message (or its `📸` label) and press Enter — all remaining screenshots are captured automatically.

Output files are written to `docs/screenshots/*.png`.

---

### Manual Testing Checklist

- [ ] Start session in group chat
- [ ] Join session from multiple users
- [ ] Enter lottery and verify order phase unlocks
- [ ] Send various order formats in private chat
- [ ] Close order phase (`/closezap`) and verify invoices are sent to participants
- [ ] Pay Lightning invoices and verify confirmation
- [ ] Verify lottery draw runs automatically once all invoices are paid
- [ ] Submit winner invoice and verify payout
- [ ] Test force-close of payment phase (button on status message)
- [ ] Test error scenarios (invalid amounts, network issues)
- [ ] Verify message updates and cleanup

## 🚀 Deployment

### Production Configuration

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Warning",
      "Override": {
        "teamZaps": "Information"
      }
    }
  }
}
```

> **Note:** Per-group admin restrictions (session start/close/cancel) are per-chat settings configured via the `/config` command inside each group.

### Environment Variables
```bash
# Required
export Telegram__BotToken="production-token"
export Backends__LNBits__LndhubUrl="https://your-lnbits.com/lndhub/ext/"
export Backends__LNBits__ApiKey="production-api-key"

# Optional
export ASPNETCORE_ENVIRONMENT="Production"
export BotBehavior__MaxBudget="5000"
```

### Docker Deployment (Recommended)

The repository includes a production-ready `docker-compose.yml` and `Dockerfile`.

**To deploy on a server:**

1. Copy `docker-compose.yml` and `.env.example` to your server.
   ```bash
   wget https://raw.githubusercontent.com/SatMeNow/teamZaps/refs/heads/master/docker-compose.yml
   wget https://raw.githubusercontent.com/SatMeNow/teamZaps/refs/heads/master/.env.example
   ```
2. Rename `.env.example` to `.env` and fill in your secrets (Bot Token, NWC connection string, etc.).
   ```bash
   mv .env.example .env
   ```
3. Customize by changing environment variables in `.env` to suit your own needs.
4. Create the required `appsettings.json` file and `data` directory:
   ```bash
   # Create data directory for logs/persistence
   mkdir -p /app/data

   # Create an empty appsettings.json (required for volume mount)
   # Note: You can also copy src/appsettings.json from the repo for a full template
   echo "{}" > /app/appsettings.json
   ```
5. Run:
   ```bash
   docker compose up -d
   ```

This will pull the latest pre-built image from **GitHub Container Registry** (`ghcr.io/satmenow/teamzaps`) and start the bot.

**Manual Build:**
If you prefer to build the image locally:
```bash
docker build -t teamzaps .
```

## 🔄 CI/CD & Versioning

The project uses GitHub Actions for Continuous Integration and Deployment.

### Automated Pipeline
The pipeline (`.github/workflows/deploy.yml`) automatically runs on:
- Pushes to `master`
- Pull Requests to `master`
- Tag pushes (`v*`)

**It performs the following steps:**
1. **Build & Test**: Compiles the code and runs tests (if any).
2. **Auto-Tagging**: Calculates the next semantic version based on commit messages.
3. **Docker Build**: Builds a multi-stage Docker image with the new version tag.
4. **Publish**: Pushes the image to GitHub Container Registry (GHCR).
5. **Release**: Creates a GitHub Release with binaries and changelog.

### Controlling Version Bumps
The versioning system follows [Semantic Versioning](https://semver.org/). You can control the version bump by including specific keywords in your **commit messages** or **PR titles**:

| Keyword | Effect | Example |
|---------|--------|---------|
| `#major` | Major version bump (X.0.0) | `feat: rewrite core engine #major` |
| `#minor` | Minor version bump (0.X.0) | `feat: add new payment method #minor` |
| (none) | Patch version bump (0.0.X) | `fix: typo in readme` |

*Default behavior is a **Patch** bump if no keyword is found.*

### Beta & RC Builds
You can create pre-release builds (e.g., `v1.0.1-beta.0`) from the `nextMaster` branch without affecting the stable `master` branch.

1. Go to **GitHub Actions** -> **Build and Deploy**.
2. Click **Run workflow**.
3. Select Branch: `nextMaster`.
4. Select Release Type: `beta` or `rc`.

This will:
- Create a pre-release tag (e.g., `v1.0.1-beta.0`).
- Build and push a Docker image with that tag.
- Create a GitHub "Pre-release" with artifacts.

### Manual Dev & Debug Builds
Manual workflow dispatches can be started from *any* branch. When you run the workflow, pick the branch you need and choose the release type:

1. Go to **GitHub Actions** -> **Build and Deploy**.
2. Click **Run workflow**.
3. Select your branch.
4. Select Release Type: `debug`.

The pipeline builds a Debug-mode Docker image, publishes it to GHCR, and skips pushing git tags/releases (the workflow runs `dry_run` during the tagging step).

The pipeline still calculates the semantic version for you, and the Docker build picks up the resolved version via `/p:Version` so `/diag` and other diagnostics show the same version that shipped. Debug builds produce Docker images tagged both `debug` and `v<version>-debug.<run>` (e.g., `v0.0.2-debug.123`), making it easy to identify the exact debug build.

### Available Docker Tags

The following tags are published to GHCR (`ghcr.io/satmenow/teamzaps`):

| Tag Pattern | Source | Description |
|-------------|--------|-------------|
| `latest` | `master` branch | Stable production releases |
| `v1.2.3` | `master` branch | Specific stable version (semver) |
| `v1.2` | `master` branch | Major.minor version tag |
| `beta` | `nextMaster` manual | Latest beta pre-release |
| `v1.2.3-beta.0` | `nextMaster` manual | Specific beta version |
| `rc` | `nextMaster` manual | Latest release candidate |
| `v1.2.3-rc.0` | `nextMaster` manual | Specific RC version |
| `debug` | Any branch manual | Latest debug build |
| `v1.2.3-debug.123` | Any branch manual | Specific debug build with run number |
| `sha-<hash>` | Any build | Git commit-specific image |

**To use a specific tag in docker-compose:**
```bash
# In .env file:
DOCKER_TAG=beta
# or
DOCKER_TAG=v1.2.3-debug.123
```

### Automatic Updates with Watchtower

The `docker-compose.yml` includes Watchtower, which automatically pulls and deploys new images when they're published to GHCR.

**Default behavior:**
- Polls GHCR every 30 minutes for image updates
- Only updates containers with the `com.centurylinklabs.watchtower.enable=true` label
- Automatically cleans up old images

**To disable auto-updates:**
Remove or comment out the `watchtower` service in `docker-compose.yml`.

**To change update frequency:**
Adjust `WATCHTOWER_POLL_INTERVAL` (in seconds) in the watchtower service environment variables.

## ✅ User-facing Commands (quick reference)

Use the commands below in the appropriate context — group chats or private/direct messages with the bot.

> Explained commands in this section are only relevant for technical users!
  For end-user guidance (screenshots and UX tips) see the user-facing README in the project root: [README.MD](../README.MD)

### Group commands (use inside the group chat)

- `/stat` - Show group statistics

  This command displays comprehensive statistics for the group where it's used. By default, anyone can use this command, but access can be restricted to admins via the `/config` command.
  
  **Permissions**: Controlled by `BotAdminOptions.AllowNonAdminStatistics` (default: `true`). When set to `false`, only group administrators can view statistics.
  
  **Statistics shown:**
  - **Current Session**: If a session is active, shows duration, participants, and totals for the ongoing session
  - **Group History**: All-time metrics including:
    - Total sessions completed
    - First session date (blockchain height)
    - Total duration across all sessions (in blocks)
    - Total unique participants
    - Total sats spent and tips given
    - Averages per block, per session, and per participant
  - **Monthly Trends**: Recent participation metrics
  
  **Implementation**: Uses `HandleGroupStatisticsAsync()` which checks permissions via `BotAdminOptions` before calling `GroupStatisticsMessage.SendAsync()`. If the group has no completed sessions yet, an error message is returned. Statistics are automatically calculated after each session completion by the `StatisticService`.

### Private commands (use in a direct/private chat with the bot)
- `/diag` - Show diagnostics (root users only)

  This command returns detailed runtime diagnostics intended for the bot operator (root user) only. It includes:
  - Current host environment and process information
  - Active sessions and their phases
  - Recovery queue status and lost sats summary
  - Registered backends and their health status
  
  Only root user IDs (configured in `appsettings.*.json` under `Telegram:RootUsers`) can run `/diag`. The output may contain sensitive operational details — do not share publicly.

- `/stat` - Show personal and server statistics

  This command displays comprehensive statistics about user activity and platform-wide metrics. It has two levels of access:
  
  **For all users:**
  - Personal statistics
  
  **For root users only:**
  - Server-wide statistics including:
    - Platform activity (total sessions, participants, duration in blocks)
    - Liquidity metrics (sats per block/session/participant, max parallel sats)
    - Performance metrics (max parallel sessions, monthly trends)
    - Started at block reference showing which group initiated the first session
  
  **Liquidity Planning**: The liquidity metrics are particularly useful for bot operators to understand Lightning wallet requirements. `MaxParallelSats` shows the peak liquidity needed across concurrent sessions, while `SatsPerBlock` indicates average throughput. These help admins size their Lightning backend appropriately.
  
  Statistics are calculated automatically after each completed session by the `StatisticService`. All data is persisted to disk in the `data/stats/` directory:
  - `data/stats/general.json` - Platform-wide statistics
  - `data/stats/group/group_{groupId}.json` - Per-group statistics
  - `data/stats/user/user_{userId}.json` - Per-user statistics

## 🤝 Contributing

You will find the [repository](https://github.com/SatMeNow/teamZaps) on github.

### Pull Request Process

1. **Fork & Branch** - Create feature branches from `master`
2. **Follow Patterns** - Match existing code style and architecture
3. **Test Thoroughly** - Manual testing at minimum, unit tests preferred  
4. **Update Documentation** - Keep this README current
5. **Small Changes** - Prefer small, focused PRs over large refactors

### Areas for Contribution

- 🧪 **Unit Tests** - Critical for reliability
- 📊 **Metrics & Monitoring** - Performance insights
- 🌐 **Internationalization** - Multi-language support  
- 🔒 **Security Hardening** - Rate limiting, input validation
- 📱 **UI Improvements** - Better inline keyboards and messages
- ⚡ **Lightning Features** - Additional payment methods, routing

## 📚 Resources

### External APIs & Documentation
- [Telegram Bot API](https://core.telegram.org/bots/api)
- [Telegram.Bot Library](https://github.com/TelegramBots/Telegram.Bot)
- [LNbits API Documentation](https://lnbits.org/)
- [Lightning Network Specifications](https://github.com/lightningnetwork/lightning-rfc)

### .NET Resources
- [.NET 9 Documentation](https://docs.microsoft.com/dotnet/)
- [Dependency Injection in .NET](https://docs.microsoft.com/aspnet/core/fundamentals/dependency-injection)
- [Serilog Documentation](https://serilog.net/)
- [Background Services in .NET](https://docs.microsoft.com/aspnet/core/fundamentals/host/hosted-services)

---

**Happy coding!** 🚀⚡
