# General Guidelines

## After Each Task — What Else to Update?

After implementing any feature or fix, always ask: *does this touch anything beyond the code?*

### User Docs (`README.MD`)
Update when users would notice the change:
- New or changed bot interaction (buttons, commands, flow steps, message format)
- New order input syntax or format
- New session phase or behavior visible to participants
- New or changed configuration options that self-hosters must set

### Developer Docs (`src/README.md`)
Update when the internals change:
- New or renamed service, handler, or message helper
- Changed session lifecycle / state machine / payment flow
- New backend or backend interface
- New message type tracked in the UI (message lifecycle section)
- New config section or option

### Codebase Reference (`AGENTS.md` — this file)
Update so future tasks stay accurate:
- New fields on `SessionState`, `ParticipantState`, `OrderRecord`, etc.
- New `CallbackActions` constants
- New handler methods in `UpdateHandler.*.cs`
- New service or pattern

### Deployment Files
Update when the deployment surface changes:
- `docker-compose.yml` / `Dockerfile` — new volume mounts, ports, or env vars
- `appsettings.Example.json` — new config sections or options users must know about
- `.env.example` — new env var overrides (uses `__` separator)

### Screenshot Tooling (`src/Examples/Sample.Screenshots.cs`)
Update when the private status message UI changes (new buttons, steps, or content).

---

## Project Overview

**TeamZaps** is a Telegram bot for coordinating Bitcoin Lightning Network payments to split group bills (restaurants, bars, etc.) when the venue doesn't accept Bitcoin.

**Flow:**
1. A session is started in a Telegram group
2. Participants join and optionally enter a lottery (willing to pay the fiat bill)
3. Everyone submits their orders in private chat
4. When the order phase closes, each participant receives a Lightning invoice to pay
5. Once all invoices are settled, a lottery winner is randomly drawn
6. The winner receives all collected sats as compensation for paying the fiat bill — a KYC-free fiat→BTC swap at market rate

**Tech stack:** .NET 9, C#, Telegram.Bot, NBitcoin, NLightning.Bolt11, NNostr.Client (NIP-47/NWC), Serilog, .NET Generic Host

**External services:**
- Telegram Bot API — primary user interface
- Lightning backends: AlbyHub (Nostr Wallet Connect) or LNbits (REST)
- Exchange rate backends: Yadio, CoinCap, CoinGecko (first configured wins)
- ElectrumX — Bitcoin indexer with multi-host failover

**Architecture:**
- `TelegramBotService` — bot lifecycle
- `PaymentMonitorService` — polls every 5s for invoice settlement
- `RecoveryService` — daily scan for lost sats from interrupted sessions
- `StatisticService` — session history stats
- `SessionManager` — singleton ConcurrentDictionary of active `SessionState` objects
- `SessionWorkflowService` — session lifecycle operations
- `FileService<T>` — generic JSON persistence via `[Storage]` attribute
- Update handlers split by partial class: group commands, direct messages, main router

**Session state machine:** `WaitingForLotteryParticipants → AcceptingOrders → WaitingForPayments → WaitingForInvoice → Completed/Canceled`

**Documentation:**
- User docs: `README.MD` (root) — setup, usage, screenshots (`docs/screenshots/`)
- Developer docs: `src/README.md` — architecture, configuration, backends, deployment, CI/CD

**Deployment:**
- Docker image: `ghcr.io/satmenow/teamzaps` (GHCR), pulled via `docker-compose.yml`
- Config via `.env` file (rename `.env.example`); env vars use `__` separator (e.g., `Backends__AlbyHub__ConnectionString`)
- Watchtower polls GHCR every 30 min and auto-updates containers with `com.centurylinklabs.watchtower.enable=true`
- Persistent volume mounts: `/app/data` for JSON files and logs, `/app/appsettings.json` for optional config overrides
- `run.sh` — convenience script for local dev: checks .NET SDK, validates bot token config, builds and runs

**Versioning & CI/CD:**
- GitHub Actions pipeline (`.github/workflows/deploy.yml`) triggers on pushes/PRs to `master` and tag pushes (`v*`)
- Pipeline steps: build & test → auto-tag → Docker build → publish to GHCR → GitHub Release
- Semantic versioning controlled by commit message keywords: `#major`, `#minor`, or default patch bump
- Pre-release builds (`beta`, `rc`) from `nextMaster` branch; debug builds from any branch via manual dispatch
- Docker tags: `latest`, `v1.2.3`, `v1.2`, `beta`, `rc`, `debug`, `sha-<hash>` — configurable via `DOCKER_TAG` in `.env`


# Code Style Preferences

- Exception messages: clean, descriptive, no emojis. Use markdown in user-facing text.
- Use `.AddLogLevel(LogLevel.X)` to set severity; `.AnswerUser()` to surface as a user reply.
- Other exception extensions: `.ExpireMessage(TimeSpan)`, `.AddTitle(string)`, `.AddHelp(string)`.
- Format extensions: `sats.Format()` → "1,234丰"; `fiat.Format()` → "1,234.50€"; `session.FormatAmount()`; `blockHeader.FormatHeight()`.
- Only **EUR** is supported as fiat currency.
- No over-engineering: no extra abstractions, no defensive coding for impossible paths, no docstrings on unchanged code, no unnecessary helpers.

Example:
```csharp
throw new InvalidOperationException("Session is not currently accepting new participants.")
    .AddLogLevel(LogLevel.Warning)
    .AnswerUser();
```


# Codebase Reference

## Session State Machine
`WaitingForLotteryParticipants → AcceptingOrders → WaitingForPayments → WaitingForInvoice → Completed/Canceled`
- **WaitingForLotteryParticipants**: no orders yet; unlocks to AcceptingOrders when first lottery entry joins
- **AcceptingOrders**: users submit payment tokens from DM
- **WaitingForPayments**: `/closezap` creates invoices; polling active
- **WaitingForInvoice**: all paid; winner must submit BOLT11; bot pays via `PayInvoiceAsync()`
- **Completed/Canceled**: terminal states

## Key Data Models
- `SessionState`: ChatId, Title, Participants, PendingPayments, LotteryParticipants, WinnerPayouts, OrdersFiatAmount, SatsAmount, TipAmount, Duration (Bitcoin blocks)
- `ParticipantState`: User, Orders, Payments, Tip preference, message IDs (`OrderConfirmationMessageId`, `BudgetSelectionMessageId`, `TipSelectionMessageId`, `PaymentMethodSelectionMessageId`, `EditPickerMessageId`), `PendingEdit` (`PendingEditToken?`)
- `OrderRecord`: FiatAmount, TipAmount, PaymentTokens, Timestamp; `RemoveToken(index, tip)` removes one token and recalculates totals
- `PendingEditToken`: OrderIndex, TokenIndex, PromptMessageId — tracks in-progress item edit (cleared after apply or cancel)
- `PaymentRecord`: User, SatsAmount, FiatAmount, PaymentHash, `PaymentRequest` (`string?` — null for Cashu push payments), Tokens
- `PendingPayment`: PaymentHash, Created, NotifiedPaid, Tokens, Currency, `PaymentRequest` (`string?` — null marks a Cashu push payment; skipped by Lightning poll loop)
- `PayableAmount` / `PayableFiatAmount`: track payout progress
- `PaymentToken`: Amount, Currency, Note — parsed from free text ("3.99 Pizza + 5€ Water")
- `LostSatsRecord`: UserId, SatsAmount, Timestamp, Reason, notifications count

## Services
- `TelegramBotService` — bot lifecycle, waits for backends ready, schedules daily sanity checks, notifies root users on failure
- `PaymentMonitorService` — polls every 5s for Lightning invoice settlement (skips `PendingPayment` with null `PaymentRequest`); `ConfirmPaymentAsync` is public and shared with `ProcessCashuTokenPaymentAsync`; on all paid → `DrawLotteryAsync` (Cashu winner gets immediate token DM and skips `WaitingForInvoice` if all payouts complete)
- `RecoveryService` — daily scan for lost sats from interrupted sessions; notifies users max 3x
- `StatisticService` — session history stats (avg duration, sats/participant, tips, max parallel sats)
- `SessionManager` — singleton `ConcurrentDictionary<chatId, SessionState>`; enforces budget/locked sats/parallel session limits; events: OnFirstSessionCreated, OnSessionRemoved, OnLastSessionRemoved
- `SessionWorkflowService` — facade for TryGetSession, TryStartSession, RemoveParticipant
- `FileService<T>` — generic JSON persistence; `[Storage]` attribute drives file paths in `data/{folder}/`
- `LiquidityLogService` — CSV log at `data/logs/liquidity.csv`; tags: Startup, Shutdown, RejectCreateSession, RejectJoinLottery, RejectPayment

## Update Handlers
- `UpdateHandler.cs` — main router; validates bot recipient
- `UpdateHandler.DirectMessage.cs` — `/start`, `/stat`, `/recover`, `/help`, `/about`, `/diag` (root-only), payment tokens, BOLT11 invoices, recovery invoices; Cashu token detection (cashuA/cashuB) → `ProcessCashuTokenPaymentAsync` (receives token via NUT-03 swap, calls `paymentMonitorService.ConfirmPaymentAsync`); edit order callbacks: ShowEditPicker, EditToken, RemoveToken, CancelEdit (HandleShowEditPickerAsync, HandleEditTokenAsync, HandleRemoveTokenAsync, HandleCancelEditAsync, HandleApplyEditAsync); leave session: LeaveSession (HandleLeaveSessionAsync); payment method: SetPaymentMethod, SelectPaymentMethod (HandlePaymentMethodSelectionAsync, HandleSetPaymentMethodAsync); winner Cashu payout button: PayoutViaCashu (HandlePayoutViaCashuAsync — removes button from summary message, sends Cashu token, handles full session-close flow if payout completed)
- `UpdateHandler.GroupMessage.cs` — `/startzap [title]`, `/closezap`, `/cancelzap`, `/status`, `/stat`, `/config`; callbacks: JoinSession, JoinLottery, SelectBudget, CloseSession, ForceClose, SetTip, AdminOptions; `CreateParticipantCashuRequestAsync` creates a Cashu push-payment request (null `PaymentRequest`, random `PaymentHash`)
- `MessageHelper.Status.cs` — `UserStatusMessage` (shows ✏️ Edit Order button when participant has orders; shows ⚡/🥜 Preferred payment button during AcceptingOrders phase; shows 🚪 Leave button during pre-payment phases; shows "left" state with no buttons after participant leaves), `EditOrderPickerMessage` (inline item picker with per-token ✏️/🗑️ buttons and ✖️ Close)
## Configuration Sections
- **BotBehavior**: Locale, SanityCheckTime, Tip/Budget choices, MaxBudget, MaxLockedSats, MaxParallelSessions
- **Telegram**: BotToken, RootUsers
- **Backends.ElectrumX**: Hosts[], Port, ValidateSslCertificate, Timeout
- **Backends.Yadio/CoinCap/CoinGecko**: (empty, use defaults)
- **Backends.Lnbits**: LndhubUrl, ApiKey
- **Backends.AlbyHub**: ConnectionString, RelayUrls[]
- **Backends.Cashu**: MintUrl, Unit (default: "sat"), MinimumReserve (default: 100 sats — wallet must stay above this for `/startzap` to succeed; covers Cashu `fee_reserve` on winner payouts; sanity check throws + notifies root users if violated)
- **Debug** (DEBUG build only): FixBudget — pin exchange rate for local testing
- **Recovery**: Enable, DailyScanTime
- Per-group: `BotAdminOptions` — `[Storage("adminOpt", "chat_{0}.json")]`
- Per-user: `BotUserOptions` — `[Storage("userOpt", "user_{0}.json")]`; fields: `Tip` (byte?), `PreferredPaymentMethod` (enum `PaymentMethod { Lightning, Cashu }`, default Lightning)

## Backends
- **AlbyHub** (`Backend.Lightning.AlbyHub.cs`): NWC over Nostr, NIP-47 encrypted, secp256k1 keys, 30s timeout; methods: get_balance, make_invoice, pay_invoice, lookup_invoice
- **LNbits** (`Backend.Lightning.Lnbits.cs`): REST `/api/v1/payments`; supports sat, USD, EUR; converts msat↔sat
- **Cashu** (`Backend.Cashu.cs`): Cashu mint via DotNut NuGet; NUT-04 (mint eCash from Lightning) + NUT-05 (melt eCash to pay Lightning) + NUT-03 (swap — used for atomic token receive); proof wallet at `data/wallets/cashu.json`; implements `ICashuBackend : ILightningBackend`; quoteId used as payment hash identifier; greedy coin selection for melt/swap; keyset fetched via `GetKeys`/`GetKeysets` on startup and cached; `ReceiveTokenAsync` — decodes cashuA (v3/JSON) or cashuB (v4/CBOR), validates mint URL, NUT-03 swap to absorb proofs, returns sats received; `SendTokenAsync(sats)` — selects proofs (swap for exact change if needed), serializes to cashuA Base64url token (v3/JSON); `QueryMeltFeeAsync(bolt11)` — creates a NUT-05 melt quote to get exact `fee_reserve` for a given BOLT11 without paying it (used by `ProcessWinnerInvoiceAsync` to pre-validate and deduct fee from winner entitlement); **fee asymmetry**: mint (inbound) is free, melt (outbound Lightning) charges `fee_reserve` per invoice — deducted from winner payout; Cashu token payouts are always fee-free; when configured as the primary Lightning backend, `cashuBackend` in `UpdateHandler`/`PaymentMonitorService` is derived via `lightningBackend as ICashuBackend` — no separate backend registration needed
- **Exchange rate** (`Backend.ExchangeRate.*.cs`): Yadio (`api.yadio.io/exrates/BTC`), CoinCap, CoinGecko; refreshes every 3+ min and on first session
- **ElectrumX** (`Backend.Indexer.ElectrumX.cs`): multi-host failover, TCP/TLS, 60s keepalive ping, auto-reconnect; `blockchain.headers.subscribe` stream
- **Nostr** (`Communication/Nostr.cs`): `NostrWalletConnector` — NWC connection string parsing, NIP-04 encrypt/decrypt, subscription + response handling (30s wait)

## Backend API References

### Cashu (DotNut)
- DotNut NuGet package: https://www.nuget.org/packages/DotNut
- DotNut source: https://github.com/Kukks/DotNut
- **NUT-04** (Mint — Lightning → eCash): https://github.com/cashubtc/nuts/blob/main/04.md
- **NUT-05** (Melt — eCash → Lightning): https://github.com/cashubtc/nuts/blob/main/05.md
- **NUT-03** (Swap — atomic token exchange; used for token receive): https://github.com/cashubtc/nuts/blob/main/03.md
- **NUT-00** (Token format / BDHKE): https://github.com/cashubtc/nuts/blob/main/00.md
- Key DotNut types: `CashuHttpClient` (in `DotNut.Api`), `Cashu` (BDHKE math), `Proof`, `BlindedMessage`, `BlindSignature`, `Keyset`, `KeysetId`, `StringSecret`, `PubKey` (all in `DotNut`), request/response models in `DotNut.ApiModels`
- DotNut 1.0.6 type differences from master: `PostMintBolt11Request`/`PostMintBolt11Response` (not `PostMint*`), `PostMeltBolt11Request` (not `PostMeltRequest`); `GetKeysResponse.KeysetItemResponse` lacks `Active` and `InputFeePpk` (use `GetKeysets` for fee)

### Nostr / NWC (AlbyHub)
- NIPs repository: https://github.com/nostr-protocol/nips
- **NIP-04** (Encrypted Direct Messages — used for payload encryption): https://github.com/nostr-protocol/nips/blob/master/04.md
- **NIP-47** (Nostr Wallet Connect — the NWC protocol itself): https://github.com/nostr-protocol/nips/blob/master/47.md
- AlbyHub source / setup: https://github.com/getAlby/hub

### LNbits
- REST API docs (Swagger UI, hosted on any instance): `{LndhubUrl}/docs`
- LNbits GitHub (source + wiki): https://github.com/lnbits/lnbits
- Endpoints used:
  - `GET  /api/v1/wallet` — sanity check / balance
  - `POST /api/v1/payments` — create invoice or pay invoice
  - `GET  /api/v1/payments/{payment_hash}` — check payment status
  - `DELETE /api/v1/payments/{payment_hash}` — cancel invoice

### ElectrumX
- Protocol methods reference: https://electrumx.readthedocs.io/en/latest/protocol-methods.html
- Methods used:
  - `server.ping` — keepalive (60s interval)
  - `server.features` — sanity check
  - `blockchain.headers.subscribe` — subscribe to new block notifications + get current block

### Yadio (Exchange Rate)
- Base URL: `https://api.yadio.io`
- Endpoint used: `GET /exrates/BTC` — returns BTC exchange rates for all fiat currencies

### CoinCap (Exchange Rate)
- API docs: https://pro.coincap.io/api-docs
- Endpoints used:
  - `GET https://api.coincap.io/v2/assets/bitcoin` — BTC/USD price
  - `GET https://api.coincap.io/v2/rates/{currency}` — fiat rates (e.g., EUR/USD)

### CoinGecko (Exchange Rate)
- API docs + key setup: https://docs.coingecko.com/reference/setting-up-your-api-key
- Endpoint used: `GET https://api.coingecko.com/api/v3/simple/price?ids=bitcoin&vs_currencies={currency}`

## Key Patterns
- **`[Storage]` attribute**: `[Storage("folder", "file_{0}.json")]` — drives `FileService<T>` JSON paths under `data/`
- **`AddHostedServiceAsSingleton<T>`**: registers as both `IHostedService` and singleton injectable
- **Exception extensions** (`Common.cs`): `.ExpireMessage(TimeSpan)` (delete after), `.AddTitle(string)`, `.AddHelp(string)` (in addition to `.AnswerUser()` and `.AddLogLevel()` above)
- **Format extensions**: `1234.Format()` → "1,234丰" (sats); `1234.5.Format()` → "1,234.50€"; `session.FormatAmount()` → "*1,000* (100€)"; `blockHeader.FormatHeight()` → mempool.space link
- **`UtilTask.DelayWhileAsync()`** — async poll helper
- **Backend discovery**: first configured backend of each type wins at startup
- **Lottery seed**: deterministic shuffle seeded from `TickCount + ChatTitle + BlockHeight + ParticipantCount + TotalFiat`
- Only **EUR** supported as fiat currency
- Sessions track **Bitcoin block heights** (via ElectrumX) for objective duration
- Multi-invoice winner payout to handle routing/wallet limits
- Force Close: available after 3 min minimum (DEBUG: instant)
- `PaymentParser`: regex-based free-text order parser ("3.99 Pizza + 5€ Water" → tokens)

## Logging
- Serilog rolling file: `data/logs/YYYY/log-YYYY-MM-DD.txt`
- Liquidity CSV: `data/logs/liquidity.csv` (append-only, runtime monitoring)

## Tests
- `tests/Test.cs` — base `UnitTest<T>` with `Mock<ILogger<T>>`, `BackendUnitTest<T>`
- `tests/Backend/Test.ElectrumX.cs` — ElectrumX tests (incomplete)

## Screenshot Tooling
- `src/Examples/Sample.Screenshots.cs` — sends 11 labeled mock message pairs to the root user's DM via `SendStatusScreenshotsAsync()`; uses reflection to call private `Build`/`BuildKeyboard` methods; fresh `ParticipantState` per step; Alice 10% tip, Bob 5% tip, Charlie no tip; only Alice and Bob in lottery
- `tools/screenshots/screenshot.js` — Playwright (Node.js) script that opens Telegram Web, lets the user click the first mock message, then auto-advances through all 11 messages using DOM sibling walking and saves cropped PNGs to `docs/screenshots/`
- `tools/screenshots/package.json` — declares `playwright` dependency; run `npm install && npx playwright install chromium` once
- Session is persisted in `tools/screenshots/.session/` (gitignored)