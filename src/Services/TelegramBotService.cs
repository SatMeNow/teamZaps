using System.Diagnostics.CodeAnalysis;
using teamZaps.Handlers;
using teamZaps.Session;

namespace teamZaps.Services;


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
    string IUserName.UserName => User.Username.DisplayName(null);
}

record struct CommandMessage(Message Source, string Value, string[] Arguments)
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
    public const string Config = "/config";
}
public static class BotPmCommand
{
    public const string Start = "/start";
    public const string Help = "/help";
    public const string Recover = "/recover";
}
public static class CallbackActions
{
    public const string JoinLottery = "joinLottery";
    public const string ViewStatus = "viewStatus";
    public const string JoinSession = "joinSession";
    public const string CloseSession = "closeSession";
    public const string CancelSession = "cancelSession";
    public const string MakePayment = "makePayment";
    public const string SelectBudget = "selectBudget";
    public const string SetTip = "setTip";
    public const string SelectTip = "selectTip";
    public const string AdminOptions = "adminOptions";
    public const string RecoverCreate = "recoverCreate";
    public const string RecoverCancel = "recoverCancel";
    public const string RecoverInvoice = "recoverInvoice";
}

public class TelegramBotService : BackgroundService
{
    public TelegramBotService(ILogger<TelegramBotService> logger, ITelegramBotClient botClient, UpdateHandler updateHandler)
    {
        this.logger = logger;
        this.botClient = botClient;
        this.updateHandler = updateHandler;
    }


    public bool Ready { get; private set; }


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            User me = await botClient.GetMe(stoppingToken).ConfigureAwait(false);
            logger.LogInformation("Bot {BotUsername} initialized successfully", me);

            var receiverOptions = new ReceiverOptions { AllowedUpdates = Array.Empty<UpdateType>() };
            await botClient.ReceiveAsync(updateHandler, receiverOptions, stoppingToken).ConfigureAwait(false);

            this.Ready = true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error starting bot");
            throw;
        }
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        this.Ready = false;

        logger.LogInformation("Stopping Team Zaps Telegram Bot...");
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }


    private readonly ILogger<TelegramBotService> logger;
    private readonly ITelegramBotClient botClient;
    private readonly UpdateHandler updateHandler;
}

internal static partial class Ext
{
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
    public static string MarkdownDisplayName(this User source) => MarkdownDisplayName(source.Username, source.Id);
    public static string MarkdownDisplayName(this string? name, long? id = null) => $"[@{name}](tg://user?id={id})";

    public static bool IsCommand(this Message source) => (source.Text?.StartsWith('/') == true);
    public static bool TryGetCommand(this Message source, [NotNullWhen(true)] out CommandMessage? command)
    {
        if (source?.IsCommand() == true)
        {
            var items = source.Text!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            command = new CommandMessage(
                source,
                items.First().ToLower(),
                items.Skip(1).ToArray()
            );
        }
        else
            command = null;

        return (command is not null);
    }
    public static async Task<bool> BotCanPinMessagesAsync(this ITelegramBotClient botClient, long chatId, CancellationToken cancellationToken = default)
    {
        try
        {
            var me = await botClient.GetMe(cancellationToken).ConfigureAwait(false);
            var member = await botClient.GetChatMember(chatId, me.Id, cancellationToken).ConfigureAwait(false);
            if (member is ChatMemberOwner)
                return (true);
            if (member is ChatMemberAdministrator admin)
                return (admin.CanPinMessages);
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400 || ex.ErrorCode == 403)
        {
            // Chat not found OR bot was kicked/forbidden
        }
        return (false);
    }
}