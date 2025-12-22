using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.Secp256k1;
using NNostr.Client;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace teamZaps.Communication;

public class NostrWalletConnector : IDisposable
{
	public NostrWalletConnector(ILoggerFactory loggerFactory, string connectionString, string[]? relayUrls)
	{
		this.logger = loggerFactory.CreateLogger<NostrWalletConnector>();
        
        // Parse NWC connection string:
        var connectionUri = new Uri(connectionString);
        if (connectionUri.Scheme != "nostr+walletconnect")
            throw new InvalidOperationException("Invalid NWC connection string. Must start with 'nostr+walletconnect://'");
        this.walletPubkey = connectionUri.Host;
        this.walletPubkeyBytes = Convert.FromHexString(walletPubkey);

        // Extract secret and relay parameters:
        var queryParams = ParseQueryString(connectionUri.Query);
        var secret = (queryParams.TryGetValue("secret", out var secretValue)) && (secretValue.Count > 0)
            ? secretValue[0]
            : throw new InvalidOperationException("NWC connection string missing 'secret' parameter");
        this.clientPrivateKey = Context.Instance.CreateECPrivKey(Convert.FromHexString(secret));
        clientPublicKey = Convert.ToHexString(clientPrivateKey.CreateXOnlyPubKey().ToBytes()).ToLower();

        // Determine relay URLs:
        if (relayUrls?.Length > 0)
            ; // Use specified relays.
        else if ((queryParams.TryGetValue("relay", out var relayValues)) && (relayValues.Count > 0))
            relayUrls = relayValues.ToArray();
        else
            throw new InvalidOperationException("NWC connection string missing 'relay' parameter(s)");

        // Initialize Nostr client:
        this.Relays = relayUrls
            .Select(r => new Uri(r))
            .ToArray();
        this.nostrClient = new NostrClient(Relays.First());
	}


    #region Properties
	public Uri[] Relays { get; }
	public string Pubkey => walletPubkey;

	public ulong SentRequests { get; private set; }
    #endregion


    #region Initialization
    public void Dispose()
    {
        nostrClient.Dispose();
    }
    #endregion
    #region Operation
	public async Task<TResult?> SendNwcRequestAsync<TResult>(object request, CancellationToken cancellationToken)
        where TResult : class
	{
		try
		{
			await nostrClient.ConnectAndWaitUntilConnected(cancellationToken).ConfigureAwait(false);
			var requestJson = JsonSerializer.Serialize(request);
			var encryptedContent = EncryptNip04(requestJson, walletPubkeyBytes);

			var evt = new NostrEvent
			{
				Kind = 23194,
				Content = encryptedContent,
				CreatedAt = DateTimeOffset.UtcNow,
				Tags = new List<NostrEventTag>
				{
					new NostrEventTag { TagIdentifier = "p", Data = new List<string> { walletPubkey } }
				}
			};
			await evt.ComputeIdAndSignAsync(clientPrivateKey).ConfigureAwait(false);

			var responseReceived = new TaskCompletionSource<NostrEvent>();
			var subscriptionId = Guid.NewGuid().ToString();

			void OnEventsReceived(object? sender, (string subscriptionId, NostrEvent[] events) args)
			{
				if (args.subscriptionId == subscriptionId)
				{
					foreach (var responseEvent in args.events)
					{
						var eTag = responseEvent.GetTaggedData("e");
						if ((responseEvent.Kind == 23195) && (eTag.Length > 0) && (eTag[0] == evt.Id))
						{
							responseReceived.TrySetResult(responseEvent);
							return;
						}
					}
				}
			}

			nostrClient.EventsReceived += OnEventsReceived;

			try
			{
				await nostrClient.CreateSubscription(subscriptionId, new[]
				{
					new NostrSubscriptionFilter
					{
						Kinds = new[] { 23195 },
						Authors = new[] { walletPubkey }
					}
				}).ConfigureAwait(false);

				await nostrClient.PublishEvent(evt, cancellationToken).ConfigureAwait(false);
				SentRequests++;

				using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
				using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

				var responseEvent = await responseReceived.Task.WaitAsync(linkedCts.Token).ConfigureAwait(false);
				var decryptedContent = DecryptNip04(responseEvent!.Content!, walletPubkeyBytes);
				var response = JsonSerializer.Deserialize<NwcResponse<TResult>>(decryptedContent);

				if ((response is not null) && (response.Error is not null))
				{
					logger.LogError("Received error: {Code} - {Message}", response.Error.Code, response.Error.Message);
					return (null);
				}

				return (response?.Result);
			}
			finally
			{
				nostrClient.EventsReceived -= OnEventsReceived;
				await nostrClient.CloseSubscription(subscriptionId).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException)
		{
			logger.LogWarning("Request timed out");
			return (null);
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Error sending request");
			return (null);
		}
	}
    #endregion


    #region Helper
    private static Dictionary<string, List<string>> ParseQueryString(string query)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query))
            return (result);

        var queryWithoutPrefix = query.TrimStart('?');
        var pairs = queryWithoutPrefix.Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in pairs)
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);
            var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
            if (!result.ContainsKey(key))
                result[key] = new List<string>();
            result[key].Add(value);
        }

        return (result);
    }
	private string EncryptNip04(string plaintext, byte[] recipientPubkey)
	{
		var compressedPubkey = new byte[33];
		compressedPubkey[0] = 0x02;
		Array.Copy(recipientPubkey, 0, compressedPubkey, 1, 32);
		var sharedPoint = ECPubKey.Create(compressedPubkey).GetSharedPubkey(clientPrivateKey);
		var sharedSecret = sharedPoint.ToBytes()[1..33];

		var iv = RandomUtils.GetBytes(16);

		using var aes = Aes.Create();
		aes.Key = sharedSecret;
		aes.IV = iv;
		aes.Mode = CipherMode.CBC;
		aes.Padding = PaddingMode.PKCS7;

		using var encryptor = aes.CreateEncryptor();
		var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
		var ciphertext = encryptor.TransformFinalBlock(plaintextBytes, 0, plaintextBytes.Length);

		return ($"{Convert.ToBase64String(ciphertext)}?iv={Convert.ToBase64String(iv)}");
	}
	private string DecryptNip04(string encryptedContent, byte[] senderPubkey)
	{
		var parts = encryptedContent.Split("?iv=");
		if (parts.Length != 2)
			throw new InvalidOperationException("Invalid encrypted content format");

		var ciphertext = Convert.FromBase64String(parts[0]);
		var iv = Convert.FromBase64String(parts[1]);

		var compressedPubkey = new byte[33];
		compressedPubkey[0] = 0x02;
		Array.Copy(senderPubkey, 0, compressedPubkey, 1, 32);
		var sharedPoint = ECPubKey.Create(compressedPubkey).GetSharedPubkey(clientPrivateKey);
		var sharedSecret = sharedPoint.ToBytes()[1..33];

		using var aes = Aes.Create();
		aes.Key = sharedSecret;
		aes.IV = iv;
		aes.Mode = CipherMode.CBC;
		aes.Padding = PaddingMode.PKCS7;

		using var decryptor = aes.CreateDecryptor();
		var plaintext = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);

		return (Encoding.UTF8.GetString(plaintext));
	}
    #endregion


    private readonly ILogger<NostrWalletConnector> logger;
	private readonly NostrClient nostrClient;
    private readonly string walletPubkey;
    private readonly byte[] walletPubkeyBytes;
    private readonly ECPrivKey clientPrivateKey;
    private readonly string clientPublicKey;
}


#region Models
public class NwcRequest
{
    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("params")]
    public object Params { get; set; } = new();
}

public class NwcResponse<T>
{
	[JsonPropertyName("result_type")]
	public string ResultType { get; set; } = string.Empty;

	[JsonPropertyName("result")]
	public T? Result { get; set; }

	[JsonPropertyName("error")]
	public NwcError? Error { get; set; }
}
public class NwcError
{
	[JsonPropertyName("code")]
	public string Code { get; set; } = string.Empty;

	[JsonPropertyName("message")]
	public string Message { get; set; } = string.Empty;
}
#endregion