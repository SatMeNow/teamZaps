using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotNut;
using DotNut.Api;
using DotNut.ApiModels;
using NBitcoin.Secp256k1;
using TeamZaps.Utils;
using TeamZaps.Configuration;
using TeamZaps.Payment;
using TeamZaps.Services;
using TeamZaps.Session;

namespace TeamZaps.Backends.Lightning;

[Storage("wallets", "cashu.json")]
public class CashuWallet
{
    public List<CashuProofRecord> Proofs { get; set; } = new();
}
public class CashuProofRecord
{
    public string MintUrl { get; set; } = string.Empty;
    public ulong Amount { get; set; }
    public string Id { get; set; } = string.Empty;
    public string Secret { get; set; } = string.Empty;
    public string C { get; set; } = string.Empty;
}

/// <summary>
/// Cashu mint backend: supports both inbound Lightning (NUT-04 mint) and outbound Lightning (NUT-05 melt).
/// Uses DotNut for all Cashu/BDHKE operations.
/// </summary>
[BackendDescription("Cashu")]
public class CashuService : ICashuBackend, ISanitizableBackend
{
    public CashuService(
        ILogger<CashuService> logger,
        IOptions<CashuSettings> settings,
        IExchangeRateBackend exchangeRate,
        IHttpClientFactory httpFactory,
        FileService<CashuWallet> walletStorage)
    {
        this.logger = logger;
        this.settings = settings.Value;
        this.exchangeRate = exchangeRate;
        this.walletStorage = walletStorage;

        var httpClient = httpFactory.CreateClient();
        httpClient.BaseAddress = new Uri(this.settings.MintUrl.TrimEnd('/') + '/');
        this.client = new CashuHttpClient(httpClient);
        

        // [Testing]
        // Examples.Sample_CashuExport.ExportTokensAsync(walletStorage).Wait();
    }


    #region Properties.Management
    bool ISanitizableBackend.Ready => true;
    #endregion
    #region Properties
    public string MintUrl => settings.MintUrl;
    public long MinimumReserve => settings.MinimumReserve;
    public long SentRequests { get; private set; }
    public long FailedRequests { get; private set; }
    #endregion


    #region Initialization
    public async Task SanityCheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            await client.GetInfo(cancellationToken).ConfigureAwait(false);
            await RefreshKeysetAsync(cancellationToken).ConfigureAwait(false);
            await LoadWalletAsync().ConfigureAwait(false);
            initialized = true;
            SentRequests++;

            // Warn root users if the wallet is below the minimum reserve needed for winner payouts.
            var balance = proofs.Sum(p => (long)p.Proof.Amount);
            if (balance < settings.MinimumReserve)
                throw new InvalidOperationException(
                    $"Cashu wallet balance {balance.Format()} is below the minimum reserve of {settings.MinimumReserve.Format()}. " +
                    $"Please top up the wallet at {settings.MintUrl} to allow winner payouts.");
        }
        catch
        {
            FailedRequests++;
            throw;
        }
    }

    public async Task<long> GetBalanceAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return proofs.Sum(p => (long)p.Proof.Amount);
    }
    #endregion


    #region Operation.Token
    public async Task<long> ReceiveTokenAsync(string cashuToken, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        // Decode the cashuA token envelope.
        if (!cashuToken.IsCashuToken())
            throw new FormatException("Invalid Cashu token: must start with 'cashuA'.")
                .AddLogLevel(LogLevel.Warning)
                .AnswerUser();

        var json = Encoding.UTF8.GetString(Base64UrlDecode(cashuToken[6..]));
        var envelope = JsonSerializer.Deserialize<CashuTokenEnvelope>(json)
            ?? throw new FormatException("Invalid Cashu token: failed to deserialize envelope.")
                .AddLogLevel(LogLevel.Warning)
                .AnswerUser();

        // Validate mint URL.
        var expectedMint = settings.MintUrl.TrimEnd('/');
        var inputProofs = new List<(Proof Proof, string MintUrl)>();
        foreach (var entry in envelope.token)
        {
            var entryMint = entry.mint.TrimEnd('/');
            if (!string.Equals(entryMint, expectedMint, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Token mint *{entry.mint}* does not match my configured mint.\n\n" +
                    $"Only tokens from *{settings.MintUrl}* are accepted.")
                    .AddLogLevel(LogLevel.Warning)
                    .AnswerUser();

            inputProofs.AddRange(entry.proofs.Select(p => (new Proof
            {
                Amount = p.amount,
                Id = new KeysetId(p.id),
                Secret = new StringSecret(p.secret),
                C = new PubKey(p.C)
            }, entryMint)));
        }

        if (inputProofs.Count == 0)
            throw new FormatException("Token contains no proofs.")
                .AddLogLevel(LogLevel.Warning)
                .AnswerUser();

        var totalSats = inputProofs.Sum(p => (long)p.Proof.Amount);

        // NUT-03 swap: burn user's proofs and mint fresh ones for us atomically.
        var denominations = GetDenominations((ulong)totalSats).ToList();
        var blindingData = new List<(string Secret, ECPrivKey R)>(denominations.Count);
        var blindedMessages = new BlindedMessage[denominations.Count];
        for (int i = 0; i < denominations.Count; i++)
        {
            var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLower();
            var r = ECPrivKey.Create(RandomNumberGenerator.GetBytes(32));
            var Y = Cashu.MessageToCurve(secret);
            var B_ = Cashu.ComputeB_(Y, r);
            blindedMessages[i] = new BlindedMessage { Amount = denominations[i], Id = activeKeysetId, B_ = B_ };
            blindingData.Add((secret, r));
        }

        await walletLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var swapResponse = await client.Swap(
                new PostSwapRequest { Inputs = inputProofs.Select(p => p.Proof).ToArray(), Outputs = blindedMessages },
                cancellationToken).ConfigureAwait(false);

            for (int i = 0; i < swapResponse.Signatures.Length; i++)
            {
                var sig = swapResponse.Signatures[i];
                var (secret, r) = blindingData[i];
                ECPubKey A = activeKeyset![sig.Amount];
                ECPubKey C = Cashu.ComputeC(sig.C_, r, A);
                proofs.Add(new ProofWithMint(
                    new Proof { Amount = sig.Amount, Id = sig.Id, Secret = new StringSecret(secret), C = C },
                    settings.MintUrl.TrimEnd('/')));
            }

            await SaveWalletAsync().ConfigureAwait(false);
        }
        finally
        {
            walletLock.Release();
        }

        logger.LogInformation("Received Cashu token: {Count} proofs swapped, {Sats} sats absorbed.", inputProofs.Count, totalSats);
        SentRequests++;
        return totalSats;
    }

    public async Task<string> SendTokenAsync(long sats, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await walletLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var selected = SelectProofsGreedy((ulong)sats);
            var selectedTotal = selected.Sum(p => (long)p.Proof.Amount);

            // If we can't select the exact amount, swap for exact change first.
            if (selectedTotal != sats)
            {
                var changeAmount = selectedTotal - sats;
                var changeDenoms = GetDenominations((ulong)changeAmount).ToList();
                var sendDenoms = GetDenominations((ulong)sats).ToList();
                var allDenoms = changeDenoms.Concat(sendDenoms).ToList();

                var blindingData = new List<(string Secret, ECPrivKey R)>(allDenoms.Count);
                var blindedMessages = new BlindedMessage[allDenoms.Count];
                for (int i = 0; i < allDenoms.Count; i++)
                {
                    var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLower();
                    var r = ECPrivKey.Create(RandomNumberGenerator.GetBytes(32));
                    var Y = Cashu.MessageToCurve(secret);
                    var B_ = Cashu.ComputeB_(Y, r);
                    blindedMessages[i] = new BlindedMessage { Amount = allDenoms[i], Id = activeKeysetId, B_ = B_ };
                    blindingData.Add((secret, r));
                }

                var swapResponse = await client.Swap(
                    new PostSwapRequest { Inputs = selected.Select(p => p.Proof).ToArray(), Outputs = blindedMessages },
                    cancellationToken).ConfigureAwait(false);

                // Remove the old proofs.
                var spentCs = selected.Select(p => p.Proof.C).ToHashSet();
                proofs.RemoveAll(p => spentCs.Contains(p.Proof.C));

                // Unblind and sort: change proofs back to wallet, send proofs collected separately.
                var newProofs = new List<ProofWithMint>(allDenoms.Count);
                for (int i = 0; i < swapResponse.Signatures.Length; i++)
                {
                    var sig = swapResponse.Signatures[i];
                    var (secret, r) = blindingData[i];
                    ECPubKey A = activeKeyset![sig.Amount];
                    ECPubKey C = Cashu.ComputeC(sig.C_, r, A);
                    newProofs.Add(new ProofWithMint(
                        new Proof { Amount = sig.Amount, Id = sig.Id, Secret = new StringSecret(secret), C = C },
                        settings.MintUrl.TrimEnd('/')));
                }

                // First changeDenoms.Count proofs go back to the wallet; remainder are the send proofs.
                proofs.AddRange(newProofs.Take(changeDenoms.Count));
                selected = newProofs.Skip(changeDenoms.Count).ToList();
                await SaveWalletAsync().ConfigureAwait(false);
            }
            else
            {
                // Exact match — just remove from wallet.
                var spentCs = selected.Select(p => p.Proof.C).ToHashSet();
                proofs.RemoveAll(p => spentCs.Contains(p.Proof.C));
                await SaveWalletAsync().ConfigureAwait(false);
            }

            var token = SerializeToken(selected);
            logger.LogInformation("Sent Cashu token: {Count} proofs, {Sats} sats.", selected.Count, sats);
            SentRequests++;
            return token;
        }
        finally
        {
            walletLock.Release();
        }
    }

    private static string SerializeToken(IEnumerable<ProofWithMint> sendProofs)
    {
        var grouped = sendProofs.GroupBy(p => p.MintUrl);
        var envelope = new
        {
            token = grouped.Select(g => new
            {
                mint = g.Key,
                proofs = g.Select(p => new
                {
                    amount = (long)p.Proof.Amount,
                    id = p.Proof.Id.ToString(),
                    secret = ((StringSecret)p.Proof.Secret).Secret,
                    C = p.Proof.C.ToString()
                }).ToArray()
            }).ToArray()
        };
        var json = JsonSerializer.Serialize(envelope);
        var bytes = Encoding.UTF8.GetBytes(json);
        return "cashuA" + Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + ((4 - s.Length % 4) % 4), '=');
        return Convert.FromBase64String(s);
    }
    #endregion


    #region Operation.Invoice
    public Task<ILightningInvoice> CreateInvoiceAsync(double amount, PaymentCurrency currency, string? memo = null, CancellationToken cancellationToken = default)
    {
        long sats = (currency == PaymentCurrency.Sats) ? (long)amount : exchangeRate.ToSats(amount);
        return CreateInvoiceAsync(sats, memo, cancellationToken);
    }

    public async Task<ILightningInvoice> CreateInvoiceAsync(long amount, string? memo = null, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var mintQuote = await client.CreateMintQuote<PostMintQuoteBolt11Response, PostMintQuoteBolt11Request>(
                "bolt11",
                new PostMintQuoteBolt11Request { Amount = (ulong)amount, Unit = settings.Unit, Description = memo },
                cancellationToken).ConfigureAwait(false);

            // Use the quote ID as the "payment hash" — uniquely identifies this payment throughout the system.
            pendingMints[mintQuote.Quote] = amount;
            SentRequests++;
            return new CashuInvoice
            {
                PaymentRequest = mintQuote.Request,
                PaymentHash = mintQuote.Quote,
                SatsAmount = amount
            };
        }
        catch
        {
            FailedRequests++;
            throw;
        }
    }

    public async Task<IPaymentResponse> PayInvoiceAsync(string bolt11, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var meltQuote = await client.CreateMeltQuote<PostMeltQuoteBolt11Response, PostMeltQuoteBolt11Request>(
                "bolt11",
                new PostMeltQuoteBolt11Request { Request = bolt11, Unit = settings.Unit },
                cancellationToken).ConfigureAwait(false);

            var totalNeeded = meltQuote.Amount + (ulong)meltQuote.FeeReserve;

            await walletLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var selected = SelectProofsGreedy(totalNeeded);

                if (selected.Count == 0 || (ulong)selected.Sum(p => (long)p.Proof.Amount) < totalNeeded)
                    throw new InvalidOperationException(
                        $"Insufficient Cashu tokens. Need {totalNeeded} sats but wallet only has {proofs.Sum(p => (long)p.Proof.Amount)} sats.")
                        .AddLogLevel(LogLevel.Warning)
                        .AnswerUser();

                var meltResponse = await client.Melt<PostMeltQuoteBolt11Response, PostMeltBolt11Request>(
                    "bolt11",
                    new PostMeltBolt11Request { Quote = meltQuote.Quote, Inputs = selected.Select(p => p.Proof).ToArray() },
                    cancellationToken).ConfigureAwait(false);

                if (!string.Equals(meltResponse.State, "PAID", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Melt request returned state '{meltResponse.State}' (expected PAID).")
                        .AddLogLevel(LogLevel.Warning);

                // Remove spent proofs and persist wallet.
                var spentCs = selected.Select(p => p.Proof.C).ToHashSet();
                proofs.RemoveAll(p => spentCs.Contains(p.Proof.C));
                await SaveWalletAsync().ConfigureAwait(false);
            }
            finally
            {
                walletLock.Release();
            }

            logger.LogInformation("Melt completed for quote {QuoteId} ({Amount} sats, {Fee} sats fee).",
                meltQuote.Quote, meltQuote.Amount, meltQuote.FeeReserve);
            SentRequests++;
            return new CashuPaymentResponse
            {
                PaymentHash = meltQuote.Quote,  // Quote ID used as payment identifier.
                Amount = (long)meltQuote.Amount,
                Fee = meltQuote.FeeReserve
            };
        }
        catch
        {
            FailedRequests++;
            throw;
        }
    }

    public async Task<IPaymentStatus> CheckPaymentStatusAsync(string paymentHash, CancellationToken cancellationToken = default)
    {
        if (!pendingMints.TryGetValue(paymentHash, out var sats))
            return new CashuPaymentStatus { Paid = false };

        try
        {
            // paymentHash == quoteId in this backend.
            var quoteState = await client.CheckMintQuote<PostMintQuoteBolt11Response>(
                "bolt11", paymentHash, cancellationToken).ConfigureAwait(false);

            if (!string.Equals(quoteState.State, "PAID", StringComparison.OrdinalIgnoreCase))
            {
                SentRequests++;
                return new CashuPaymentStatus { Paid = false };
            }

            // Quote is paid — mint eCash tokens. Errors are logged and retried next poll.
            await walletLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await MintTokensAsync(paymentHash, sats, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to mint tokens for quote {QuoteId}, will retry next poll.", paymentHash);
                return new CashuPaymentStatus { Paid = false };
            }
            finally
            {
                walletLock.Release();
            }

            pendingMints.TryRemove(paymentHash, out _);
            SentRequests++;
            return new CashuPaymentStatus { Paid = true, SatsAmount = sats };
        }
        catch
        {
            FailedRequests++;
            throw;
        }
    }
    #endregion


    #region Private helpers
    private async Task MintTokensAsync(string quoteId, long sats, CancellationToken ct)
    {
        // Caller must hold walletLock.
        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        var denominations = GetDenominations((ulong)sats).ToList();
        var blindingData = new List<(string Secret, ECPrivKey R)>(denominations.Count);
        var blindedMessages = new BlindedMessage[denominations.Count];

        for (int i = 0; i < denominations.Count; i++)
        {
            var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLower();
            var r = ECPrivKey.Create(RandomNumberGenerator.GetBytes(32));
            var Y = Cashu.MessageToCurve(secret);
            var B_ = Cashu.ComputeB_(Y, r);
            blindedMessages[i] = new BlindedMessage { Amount = denominations[i], Id = activeKeysetId, B_ = B_ };
            blindingData.Add((secret, r));
        }

        var mintResponse = await client.Mint<PostMintBolt11Request, PostMintBolt11Response>(
            "bolt11",
            new PostMintBolt11Request { Quote = quoteId, Outputs = blindedMessages },
            ct).ConfigureAwait(false);

        for (int i = 0; i < mintResponse.Signatures.Length; i++)
        {
            var sig = mintResponse.Signatures[i];
            var (secret, r) = blindingData[i];
            ECPubKey A = activeKeyset![sig.Amount];  // PubKey → ECPubKey (implicit)
            ECPubKey C = Cashu.ComputeC(sig.C_, r, A);  // PubKey → ECPubKey (implicit), returns ECPubKey
            proofs.Add(new ProofWithMint(
                new Proof
                {
                    Amount = sig.Amount,
                    Id = sig.Id,
                    Secret = new StringSecret(secret),
                    C = C  // ECPubKey → PubKey (implicit)
                },
                settings.MintUrl.TrimEnd('/')));
        }

        await SaveWalletAsync().ConfigureAwait(false);
        logger.LogInformation("Minted {Count} proofs ({Sats} sats) from quote {QuoteId}.", mintResponse.Signatures.Length, sats, quoteId);
    }

    private List<ProofWithMint> SelectProofsGreedy(ulong amount)
    {
        // Greedy: take largest proofs first until we cover the requested amount.
        var result = new List<ProofWithMint>();
        var remaining = (long)amount;
        foreach (var p in proofs.OrderByDescending(p => p.Proof.Amount))
        {
            if (remaining <= 0) break;
            result.Add(p);
            remaining -= (long)p.Proof.Amount;
        }
        return (remaining <= 0) ? result : new List<ProofWithMint>(); // empty = insufficient
    }

    private async Task RefreshKeysetAsync(CancellationToken ct)
    {
        var keysResponse = await client.GetKeys(ct).ConfigureAwait(false);
        var keysetsResponse = await client.GetKeysets(ct).ConfigureAwait(false);

        var keyset = keysResponse.Keysets.FirstOrDefault(k =>
            string.Equals(k.Unit, settings.Unit, StringComparison.OrdinalIgnoreCase));

        if (keyset is null)
            throw new InvalidOperationException(
                $"No active keyset for unit '{settings.Unit}' found on mint '{settings.MintUrl}'.");

        activeKeysetId = keyset.Id;
        activeKeyset = keyset.Keys;
        // Get the input fee from the keysets metadata response (not available in GetKeysResponse in 1.0.x).
        var keysetMeta = keysetsResponse.Keysets.FirstOrDefault(k => k.Id.ToString() == keyset.Id.ToString());
        inputFeePpk = keysetMeta?.InputFee ?? 0;
        logger.LogDebug("Loaded Cashu keyset {KeysetId} for unit '{Unit}' (fee: {FeePpk} ppk).", activeKeysetId, settings.Unit, inputFeePpk);
    }

    private async Task LoadWalletAsync()
    {
        var wallet = await walletStorage.ReadAsync().ConfigureAwait(false);
        if (wallet?.Proofs is { Count: > 0 } records)
        {
            proofs = records.Select(r => new ProofWithMint(
                new Proof
                {
                    Amount = r.Amount,
                    Id = new KeysetId(r.Id),
                    Secret = new StringSecret(r.Secret),
                    C = new PubKey(r.C)
                },
                r.MintUrl)
            ).ToList();
            logger.LogInformation("Loaded {Count} Cashu proofs ({Sats} sats) from wallet.",
                proofs.Count, proofs.Sum(p => (long)p.Proof.Amount));
        }
    }

    private Task SaveWalletAsync()
    {
        var wallet = new CashuWallet
        {
            Proofs = proofs.Select(p => new CashuProofRecord
            {
                MintUrl = p.MintUrl,
                Amount = p.Proof.Amount,
                Id = p.Proof.Id.ToString(),
                Secret = ((StringSecret)p.Proof.Secret).Secret,
                C = p.Proof.C.ToString()
            }).ToList()
        };
        return walletStorage.WriteAsync(wallet);
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (initialized) return;

        await walletLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (initialized) return;
            if (activeKeyset is null)
                await RefreshKeysetAsync(ct).ConfigureAwait(false);
            await LoadWalletAsync().ConfigureAwait(false);
            initialized = true;
        }
        finally
        {
            walletLock.Release();
        }
    }

    private IEnumerable<ulong> GetDenominations(ulong amount)
    {
        var available = activeKeyset!.Keys.OrderByDescending(k => k).ToList();
        while (amount > 0)
        {
            var denom = available.FirstOrDefault(d => d <= amount);
            if (denom == 0)
                throw new InvalidOperationException(
                    $"Cannot decompose {amount} sats into available mint denominations.");
            yield return denom;
            amount -= denom;
        }
    }
    #endregion


    private readonly ILogger<CashuService> logger;
    private readonly CashuSettings settings;
    private readonly IExchangeRateBackend exchangeRate;
    private readonly CashuHttpClient client;
    private readonly FileService<CashuWallet> walletStorage;
    private List<ProofWithMint> proofs = new();
    private readonly SemaphoreSlim walletLock = new(1, 1);
    private readonly ConcurrentDictionary<string, long> pendingMints = new();  // quoteId → sats
    private Keyset? activeKeyset;
    private KeysetId activeKeysetId = default!;
    private ulong inputFeePpk;
    private bool initialized;

    private sealed record ProofWithMint(Proof Proof, string MintUrl);
}

// --- File-private types ---

// Token deserialisation envelope for receiving cashuA tokens.
file class CashuTokenEnvelope
{
    public CashuTokenEntry[] token { get; set; } = [];
}
file class CashuTokenEntry
{
    public string mint { get; set; } = string.Empty;
    public CashuProofDto[] proofs { get; set; } = [];
}
file class CashuProofDto
{
    public ulong amount { get; set; }
    public string id { get; set; } = string.Empty;
    public string secret { get; set; } = string.Empty;
    public string C { get; set; } = string.Empty;
}

// Swap request/response (NUT-03).
// Note: DotNut 1.0.6 exposes PostSwapRequest and PostSwapResponse directly in DotNut.ApiModels.

// --- File-private response models ---

file class CashuInvoice : ILightningInvoice
{
    public string PaymentRequest { get; init; } = string.Empty;
    public string PaymentHash { get; init; } = string.Empty;
    public long? SatsAmount { get; init; }
}

file class CashuPaymentStatus : IPaymentStatus
{
    public bool Paid { get; init; }
    public long SatsAmount { get; init; }
    public double FiatAmount => 0;
    public double FiatRate => 0;
}

file class CashuPaymentResponse : IPaymentResponse
{
    public string PaymentHash { get; init; } = string.Empty;
    public long Amount { get; init; }
    public long Fee { get; init; }
}
