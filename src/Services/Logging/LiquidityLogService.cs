using System.ComponentModel;
using System.Globalization;
using TeamZaps;
using TeamZaps.Configuration;
using TeamZaps.Services;
using TeamZaps.Session;
using TeamZaps.Utils;

namespace TeamZaps.Logging;

public enum LogTag
{
    Shutdown,
    Startup,
}

/// <summary>
/// Lightweight logging service for monitoring sats locked in the bot at runtime.
/// Appends to a CSV file that can be opened in Excel or other standard tools.
/// </summary>
public class LiquidityLogService : IHostedService
{
    #region Constants
    private static readonly string LogPath = Path.Combine(Common.LogPath, "liquidity.csv");
    private static readonly string[] Columns = [ "Timestamp", "Sessions", PaymentCurrency.Sats.GetDescription(), BotBehaviorOptions.AcceptedFiatCurrency.GetDescription() ];
    #endregion


    public LiquidityLogService(ILogger<LiquidityLogService> logger, SessionManager sessionManager)
    {
        this.logger = logger;
        this.sessionManager = sessionManager;
    }


    #region Initialization
    public Task StartAsync(CancellationToken cancellationToken) => LogAsync(LogTag.Startup, cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => LogAsync(LogTag.Shutdown, cancellationToken);
    #endregion
    #region Operation
    public Task LogAsync(CancellationToken cancellationToken = default) => LogAsync(null, cancellationToken);
    public async Task LogAsync(LogTag? tag, CancellationToken cancellationToken = default)
    {
        await update.WaitAsync(cancellationToken);
        try
        {
            // Create directory if needed
            var directory = Path.GetDirectoryName(LogPath);
            if (directory is not null)
                Directory.CreateDirectory(directory);
            
            // Create file with header if it doesn't exist
            if (!File.Exists(LogPath))
                await File.WriteAllTextAsync(LogPath, (string.Join(",", Columns) + "\n"), cancellationToken);
            
            // Append monitoring record
            var line = $"{DateTime.UtcNow:O},";
            if (tag is null)
            {
                var sessionCount = sessionManager.ActiveSessions.Count();
                var lockedSats = sessionManager.TotalLockedSats;
                var lockedFiat = sessionManager.TotalLockedFiat;

                line += $"{sessionCount},{lockedSats},{lockedFiat.ToString("N2", CultureInfo.InvariantCulture)}";
            }
            else
                // Write specific tag to file
                line += $"#{tag}";

            await File.AppendAllTextAsync(LogPath, (line + "\n"), cancellationToken);
        }
        catch (IOException ex) when (ex.Message.Contains("being used by another process"))
        {
            logger.LogWarning("Cannot write to liquidity log (file opened in another application).");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error writing liquidity log.");
        }
        finally
        {
            update.Release();
        }
    }
    #endregion


    private readonly ILogger<LiquidityLogService> logger;
    private readonly SessionManager sessionManager;

    private static readonly SemaphoreSlim update = new(1, 1);
}