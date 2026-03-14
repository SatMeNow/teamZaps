using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using TeamZaps.Backends;
using TeamZaps.Configuration;
using TeamZaps.Handlers;
using TeamZaps.Utils;

namespace TeamZaps.Services;


public interface IUserName
{
    long UserId { get; }
    string UserName { get; }
    /// <example>@MyUserName (1000)</example>
    string DisplayName => UserName.DisplayName(UserId);
    /// <example>[{MyUserName}](tg://user?id={1000})</example>
    string MarkdownDisplayName => UserName.MarkdownDisplayName(UserId);
}
public interface IUser : IUserName
{
    public User User { get; }

    long IUserName.UserId => User.Id;
    /// <example>@MyUserName</example>
    string IUserName.UserName => User.UserName().DisplayName(null);
}

public record struct CommandMessage(Message Source, string Value, string? Recipient, string[] Arguments)
{
    #region  Properties
    public long ChatId => Source.Chat.Id;
    public long UserId => From.Id;
    public User From => Source.From!;
    #endregion


    public override string ToString() => Value;
}

public static class BotGroupCommand
{
    public const string StartSession = "/startzap";
    public const string CloseSession = "/closezap";
    public const string CancelSession = "/cancelzap";
    public const string Status = "/status";
    public const string Statistics = "/stat";
    public const string Config = "/config";
}
public static class BotPmCommand
{
    public const string Start = "/start";
    public const string Statistics = "/stat";
    public const string Recover = "/recover";
    public const string Help = "/help";
    public const string About = "/about";
}
public static class BotRootCommand
{
    public const string Diagnosis = "/diag";
}
public static class CallbackActions
{
    public const string JoinLottery = "joinLottery";
    public const string ViewStatus = "viewStatus";
    public const string JoinSession = "joinSession";
    public const string CloseSession = "closeSession";
    public const string CancelSession = "cancelSession";
    public const string AddOrder = "addOrder";
    public const string SelectBudget = "selectBudget";
    public const string SetTip = "setTip";
    public const string SelectTip = "selectTip";
    public const string AdminOptions = "adminOptions";
    public const string ForceClose = "forceClose";
    public const string ShowEditPicker = "showEditPicker";
    public const string EditToken = "editToken";
    public const string RemoveToken = "removeToken";
    public const string CancelEdit = "cancelEdit";
    public const string LeaveSession = "leaveSession";
    public const string SetPaymentMethod = "setPayMethod";
    public const string SelectPaymentMethod = "selPayMethod";
}

public class TelegramBotService : BackgroundService
{
    public TelegramBotService(ILogger<TelegramBotService> logger, IOptions<BotBehaviorOptions> botBehavior, IOptions<TelegramSettings> telegramSettings, ITelegramBotClient botClient, UpdateHandler updateHandler, IEnumerable<IBackend> backends)
    {
        this.logger = logger;
        this.botClient = botClient;
        this.updateHandler = updateHandler;
        this.sanitizableBackends = backends.OfType<ISanitizableBackend>().ToArray();
        this.botBehaviour = botBehavior.Value;
        this.telegramSettings = telegramSettings.Value;
    }


    #region Initialization
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Wait for backends to become operational:
            if (!sanitizableBackends.IsEmpty())
            {
                logger.LogDebug("Waiting for backends to become operational...");
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeoutCts.Token);
                try
                {
                    while (!sanitizableBackends.All(b => b.Ready))
                        await Task.Delay(TimeSpan.FromSeconds(1), linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    throw new TimeoutException("Timeout waiting for backends to become ready.");
                }
                
                // Schedule sanity checks:
                _ = Task.Run(() => RunScheduledSanityChecksAsync(stoppingToken));
            }

            // Initialize telegram bot:
            var me = await botClient.GetMe(stoppingToken).ConfigureAwait(false);

            var receiverOptions = new ReceiverOptions { AllowedUpdates = Array.Empty<UpdateType>() };
            _ = Task.Run(() => botClient.ReceiveAsync(updateHandler, receiverOptions, stoppingToken));

            logger.LogInformation("Bot {BotUsername} initialized successfully.", me);


            // [Testing]
            // await Examples.Sample_Screenshots.SendStatusScreenshotsAsync(botClient, telegramSettings, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error starting bot.");
            throw;
        }
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Stopping Team Zaps Telegram Bot...");
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }


    private async Task RunScheduledSanityChecksAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Calculate next sanity check time:
                var now = DateTime.Now;
                var nextCheck = now.Date.Add(botBehaviour.SanityCheckTime);
                if (nextCheck <= now)
                    nextCheck = nextCheck.AddDays(1);
                var delay = (nextCheck - now);

                logger.LogInformation("Next backend sanity check scheduled for {NextCheck}.", nextCheck);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

                // Run sanity checks:
                logger.LogInformation("Running scheduled backend sanity checks.");
                var failedChecks = new Dictionary<ISanitizableBackend, Exception>();
                foreach (var backend in sanitizableBackends)
                {
                    try
                    {
                        await backend.SanityCheckAsync(cancellationToken).ConfigureAwait(false);
                        logger.LogDebug("Sanity check passed for {Backend}.", backend.BackendType);
                    }
                    catch (Exception ex)
                    {
                        failedChecks.Add(backend, ex);
                        logger.LogError(ex, "Sanity check failed for {Backend}.", backend.BackendType);
                    }
                }

                // Notify root members about failed checks:
                if (!failedChecks.IsEmpty())
                {
                    var message = $"❌ Backend *sanity check failed* for the following backends:\n" +
                                  string.Join('\n', failedChecks.Select(b => $"- *{b.Key.BackendType}*\n  {b.Value.Message}"));
                    foreach (var rootUser in telegramSettings.RootUsers)
                    {
                        try
                        {
                            await botClient.SendMessage(rootUser, message, parseMode: ParseMode.Markdown, cancellationToken: cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Failed to notify root user {RootUser} about sanity check failures.", rootUser);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in scheduled sanity check loop.");
                await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
            }
        }
    }
    #endregion


    private readonly ILogger<TelegramBotService> logger;
    private readonly BotBehaviorOptions botBehaviour;
    private readonly TelegramSettings telegramSettings;
    private readonly ITelegramBotClient botClient;
    private readonly UpdateHandler updateHandler;
    private readonly ISanitizableBackend[] sanitizableBackends;
}

internal static partial class Ext
{
    public static string UserName(this User source) => (source.Username ?? $"{source.FirstName}{source.LastName?.Insert(0, " ")}");
    public static string UserName<T>(this T source) where T : IUserName => source.UserName;
    public static string DisplayName<T>(this T source) where T : IUserName => source.DisplayName;
    public static string DisplayName(this User source) => source.ToString();
    public static string DisplayName(this string? userName, long? userId = null)
    {
        var result = userName ?? "?";
        if (userId is not null)
            result += $" ({userId})";
        return (result);
    }
    public static string MarkdownDisplayName<T>(this T source) where T : IUserName => source.MarkdownDisplayName;
    public static string MarkdownDisplayName(this User source) => MarkdownDisplayName(source.UserName(), source.Id);
    public static string MarkdownDisplayName(this string? name, long? id = null) => $"[@{name}](tg://user?id={id})";

    public static bool IsCommand(this Message source) => (source.Text?.StartsWith('/') == true);
    public static bool TryGetCommand(this Message source, [NotNullWhen(true)] out CommandMessage? command)
    {
        if (source?.IsCommand() == true)
        {
            var items = source.Text!
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var cmd = items
                .First()
                .Split('@');
            command = new CommandMessage(
                source,
                cmd.First().ToLower(), // Command name
                cmd.ElementAtOrDefault(1), // Recipient (if any)
                items.Skip(1).ToArray() // Arguments
            );
        }
        else
            command = null;

        return (command is not null);
    }
    public static bool IsGroup(this ChatType source) => source is ChatType.Group or ChatType.Supergroup;
    public static async Task<User> GetBotUser(this ITelegramBotClient source, CancellationToken cancellationToken = default)
    {
        if (botUser is null)
            botUser = await source.GetMe(cancellationToken).ConfigureAwait(false);
        return (botUser);
    }
    private static User? botUser = null;
    /// <summary>
    /// Get the bot's <see cref="ChatMember">role</see> in the specified chat.
    /// </summary>
    public static async Task<ChatMember?> GetBotRoleAsync(this ITelegramBotClient source, long chatId, CancellationToken cancellationToken = default)
    {
        try
        {
            var bot = await GetBotUser(source, cancellationToken).ConfigureAwait(false);
            return (await source.GetChatMember(chatId, bot.Id, cancellationToken).ConfigureAwait(false));
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400 || ex.ErrorCode == 403)
        {
            // Chat not found OR bot was kicked/forbidden
        }
        return (null);
    }
    
    public static void DeleteMessageAfterAsync(this ITelegramBotClient source, Message message, TimeSpan? after, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        after ??= TimeSpan.FromSeconds(15);
        await Task.Delay(after.Value, cancellationToken).ConfigureAwait(false);
        await source.DeleteMessageAsync(message, cancellationToken).ConfigureAwait(false);
    });
    public static Task<bool> DeleteMessageAsync(this ITelegramBotClient source, Message message, CancellationToken cancellationToken) => DeleteMessageAsync(source, message.Chat.Id, message.MessageId, cancellationToken);
    public static async Task<bool> DeleteMessageAsync(this ITelegramBotClient source, long chatId, int? messageId, CancellationToken cancellationToken)
    {
        if (messageId is not null)
        {
            try
            {
                await source.DeleteMessage(chatId, messageId.Value, cancellationToken).ConfigureAwait(false);
                return (true);
            }
            catch
            {
            }
        }
        return (false);
    }
}