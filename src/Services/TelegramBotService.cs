using System.Diagnostics.CodeAnalysis;
using teamZaps.Handlers;
using teamZaps.Sessions;

namespace teamZaps.Services;


public interface IUser
{
    public User User { get; }

    /// <example>@MyUserName</example>
    public string UserName => User.Username.DisplayName(null);
    /// <example>@MyUserName (1000)</example>
    public string DisplayName => User.DisplayName();
    /// <example>[{MyUserName}](tg://user?id={1000})</example>
    public string MarkdownDisplayName => User.MarkdownDisplayName();
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

public class TelegramBotService : BackgroundService
{
    public TelegramBotService(ILogger<TelegramBotService> logger, ITelegramBotClient botClient, UpdateHandler updateHandler)
    {
        this.logger = logger;
        this.botClient = botClient;
        this.updateHandler = updateHandler;
    }


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            User me = await botClient.GetMe(stoppingToken);
            logger.LogInformation("Bot {BotUsername} initialized successfully", me);

            var receiverOptions = new ReceiverOptions { AllowedUpdates = Array.Empty<UpdateType>() };
            await botClient.ReceiveAsync(updateHandler, receiverOptions, stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error starting bot");
            throw;
        }
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Stopping Team Zaps Telegram Bot...");
        await base.StopAsync(cancellationToken);
    }


    private readonly ILogger<TelegramBotService> logger;
    private readonly ITelegramBotClient botClient;
    private readonly UpdateHandler updateHandler;
}

internal static partial class Ext
{
    public static string UserName<T>(this T source) where T : IUser => source.UserName;
    public static string DisplayName<T>(this T source) where T : IUser => source.DisplayName;
    /// <remarks>
    /// For completeness only, as <see cref="User.ToString()"/> already provides a suitable display name.
    /// </remarks>
    public static string DisplayName(this User source) => source.ToString();
    public static string DisplayName(this string? userName, long? userId = null)
    {
        var result = userName ?? "?";
        if (userId is not null)
            result = $" ({userId})";
        return (result);
    }
    public static string MarkdownDisplayName<T>(this T source) where T : IUser => source.MarkdownDisplayName;
    public static string MarkdownDisplayName(this User source) => MarkdownDisplayName(source.Username, source.Id);
    private static string MarkdownDisplayName(this string? name, long? id = null) => $"[@{name}](tg://user?id={id})";

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
}