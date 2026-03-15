using System.Collections.Concurrent;
using System.Security.Cryptography;
using DotNut;
using DotNut.Api;
using DotNut.ApiModels;
using NBitcoin.Secp256k1;
using TeamZaps.Utils;
using TeamZaps.Configuration;
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
        }
        catch
        {
            FailedRequests++;
            throw;
        }
    }

    public Task<long> GetBalanceAsync(CancellationToken cancellationToken = default) => Task.FromResult(proofs.Sum(p => (long)p.Proof.Amount));
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
            PostMeltQuoteBolt11Response meltResponse;
            try
            {
                var selected = SelectProofsGreedy(totalNeeded);

                if (selected.Count == 0 || (ulong)selected.Sum(p => (long)p.Proof.Amount) < totalNeeded)
                    throw new InvalidOperationException(
                        $"Insufficient Cashu tokens. Need {totalNeeded} sats but wallet only has {proofs.Sum(p => (long)p.Proof.Amount)} sats.")
                        .AddLogLevel(LogLevel.Warning)
                        .AnswerUser();

                meltResponse = await client.Melt<PostMeltQuoteBolt11Response, PostMeltBolt11Request>(
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
