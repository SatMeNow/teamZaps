using System.Text;
using System.Text.Json;
using TeamZaps.Backends.Lightning;
using TeamZaps.Services;

namespace TeamZaps.Examples;

/// <summary>
/// Reads the local Cashu proof wallet (data/wallets/cashu.json) and serializes its
/// proofs into standard NUT-00 Cashu tokens (cashuA/cashuB) that any compatible wallet can import.
///
/// Background:
///   CashuWallet stores raw BDHKE proof components (amount, id, secret, C) — the
///   internal form used by the mint protocol.  Other wallets (Minibits, eNuts, …)
///   expect proofs wrapped in the portable token envelope defined by NUT-00:
///
///     { "token": [{ "mint": "<mintUrl>", "proofs": [...] }] }
///
///   base64url-encoded and prefixed with "cashuA" (v3/JSON) or "cashuB" (v4/CBOR).
///
///   This sample performs that conversion so you can recover funds from the wallet
///   file if the bot's Cashu backend holds unspent proofs.
/// </summary>
public class Sample_CashuExport
{
    /// <summary>
    /// Reads all proofs from the wallet storage and prints one Cashu token (cashuA/cashuB) per
    /// distinct mint URL. Each proof carries the URL of its mint, so the output
    /// is correct even when proofs span multiple mints.
    /// </summary>
    public static async Task ExportTokensAsync(FileService<CashuWallet> walletStorage)
    {
        var wallet = await walletStorage.ReadAsync().ConfigureAwait(false);

        if (wallet is null || wallet.Proofs.Count == 0)
        {
            Console.WriteLine("Cashu wallet is empty — no proofs to export.");
            return;
        }

        var totalSats = wallet.Proofs.Sum(p => (long)p.Amount);
        Console.WriteLine($"Found {wallet.Proofs.Count} proofs ({totalSats} sats total)");
        Console.WriteLine();

        // One Cashu token (cashuA/cashuB) per mint — group by the per-proof MintUrl.
        foreach (var mintGroup in wallet.Proofs.GroupBy(p => p.MintUrl))
        {
            var proofNodes = mintGroup.Select(p => new
            {
                amount = (long)p.Amount,
                id     = p.Id,
                secret = p.Secret,
                C      = p.C
            }).ToArray();

            var mintSats = proofNodes.Sum(p => p.amount);

            // NUT-00 token envelope — one token per mint, all proofs inside.
            var tokenEnvelope = new
            {
                token = new[]
                {
                    new
                    {
                        mint   = mintGroup.Key,
                        proofs = proofNodes
                    }
                }
            };

            var json  = JsonSerializer.Serialize(tokenEnvelope);
            var token = "cashuA" + Base64UrlEncode(json);

            Console.WriteLine($"Mint '{mintGroup.Key}' ({proofNodes.Length} proofs, {mintSats} sats):");
            Console.WriteLine(token);
            Console.WriteLine();
        }
    }

    private static string Base64UrlEncode(string json)
    {
        var bytes  = Encoding.UTF8.GetBytes(json);
        var base64 = Convert.ToBase64String(bytes);
        // URL-safe alphabet, no padding — standard for Cashu tokens (cashuA/cashuB).
        return base64.Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
