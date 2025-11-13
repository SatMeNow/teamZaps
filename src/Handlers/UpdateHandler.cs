using System.Diagnostics.CodeAnalysis;
using teamZaps.Services;

namespace teamZaps.Handlers;

public class UpdateHandler : IUpdateHandler
{
    public UpdateHandler(ILogger<UpdateHandler> logger, LnbitsService lnbitsService)
    {
        this.logger = logger;
        this.lnbitsService = lnbitsService;
    }

    public async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        try
        {
            switch (update.Type)
            {
                case UpdateType.Message:
                    await HandleMessageAsync(botClient, update.Message!, cancellationToken);
                    break;
                case UpdateType.EditedMessage:
                    await HandleEditedMessageAsync(botClient, update.EditedMessage!, cancellationToken);
                    break;
                default:
                    logger.LogInformation("Received update type: {UpdateType}", update.Type);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling update");
        }
    }

    public Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, HandleErrorSource source, CancellationToken cancellationToken)
    {
        string errorMessage;
        if (exception is ApiRequestException apiRequestException)
        {
            errorMessage = $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}";
        }
        else
        {
            errorMessage = exception.ToString();
        }

        logger.LogError("HandleError: {ErrorMessage}", errorMessage);
        return (Task.CompletedTask);
    }

    private async Task HandleMessageAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        if (message.Text is null)
            return;

        long chatId = message.Chat.Id;
        logger.LogInformation("Received message from {ChatId}: {MessageText}", chatId, message.Text);

        if (message.IsCommand())
        {
            await HandleCommandAsync(botClient, message, cancellationToken);
            return;
        }

        await botClient.SendMessage(chatId: chatId, text: $"You said: {message.Text}", cancellationToken: cancellationToken);
    }

    private async Task HandleCommandAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        message.GetCommand(out var cmd, out var args);
        
        try
        {
            string response;
            switch (cmd)
            {
                case "/start":
                    response = "Welcome to Team Zaps! 🎯\n\nI'm ready to help you. Use /help to see available commands.";
                    break;
                case "/help":
                    response = "Available commands:\n/start - Start the bot\n/help - Show this help message\n/about - About Team Zaps";
                    break;
                case "/about":
                    response = "Team Zaps Bot v1.0\n\nA powerful Telegram bot built with .NET 9";
                    break;

                #if DEBUG
                case "/balance":
                    var walletDetails = await lnbitsService.GetWalletDetailsAsync(cancellationToken).ConfigureAwait(false);
                    if (walletDetails is null)
                        response = "Failed to fetch wallet details.";
                    else
                        response = $"Wallet has a balance of {walletDetails.Balance} sats.";
                    break;
                case "/create":
                    var amount = args.GetArgument<double>(0);
                    var memo = args.TryGetArguments(1);
                    var resp1 = await lnbitsService.CreateInvoiceAsync(amount, "EUR", memo, cancellationToken).ConfigureAwait(false);
                    if (resp1 is null)
                        response = "Failed to create invoice.";
                    else
                        response = $"Invoice created. hash: '{resp1.PaymentHash}' | request: '{resp1.PaymentRequest}'";
                    break;
                case "/check":
                    var paymentHash = args.GetArgument(0);
                    var resp2 = await lnbitsService.CheckPaymentStatusAsync(paymentHash, cancellationToken).ConfigureAwait(false);
                    if (resp2 is null)
                        response = "Failed to check payment.";
                    else
                        response = $"Invoice payed: '{resp2.Paid}' ({resp2.Amount} sats)";
                    break;
                case "/pay":
                    var bolt11 = args.GetArgument(0);
                    var resp3 = await lnbitsService.PayInvoiceAsync(bolt11, cancellationToken).ConfigureAwait(false);
                    if (resp3 is null)
                        response = "Failed to pay invoice.";
                    else
                        response = $"Payed '{resp3.Memo}': {resp3.Amount} sats @ {resp3.Fee} sats fee.";
                    break;
                #endif

                default:
                    response = "Unknown command. Use /help to see available commands.";
                    break;
            }

            await botClient.SendMessage(chatId: chatId, text: response, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling command: {cmd}", cmd);
            await botClient.SendMessage(chatId: chatId, text: "An error occurred while processing your command.", cancellationToken: cancellationToken);
            return;
        }
    }

    private async Task HandleEditedMessageAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        logger.LogInformation("Received edited message from {ChatId}", message.Chat.Id);
        await botClient.SendMessage(chatId: message.Chat.Id, text: "I noticed you edited your message! 📝", cancellationToken: cancellationToken);
    }

    private ILogger<UpdateHandler> logger;
    private LnbitsService lnbitsService;
}

internal static partial class Ext
{
    public static bool IsCommand(this Message source) => (source.Text?.StartsWith('/') == true);
    public static void GetCommand(this Message source, out string command, out string[] args)
    {
        if (source?.IsCommand() == true)
        {
            var items = source.Text!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            {
                command = items.First().ToLower();
                args = items.Skip(1).ToArray();
            }
        }
        else
            throw new InvalidOperationException("Failed to parse command from message!");
    }

    /// <inheritdoc cref="GetArgument(string[], int)"/>
    public static T GetArgument<T>(this string[] source, int index) => (T)Convert.ChangeType(GetArgument(source, index), typeof(T));
    /// <summary>
    /// Gets the argument at the specified index.
    /// </summary>
    public static string GetArgument(this string[] source, int index)
    {
        var res = TryGetArgument(source, index);
        if (res is null)
            throw new IndexOutOfRangeException("Argument index out of range.");
        else
            return (res!);
    }
    /// <summary>
    /// Gets the argument at the specified index and all following arguments as concatenated string.
    /// </summary>
    public static string GetArguments(this string[] source, int index)
    {
        var res = TryGetArguments(source, index);
        if (res is null)
            throw new IndexOutOfRangeException("Argument index out of range.");
        else
            return (res!);
    }
    /// <inheritdoc cref="GetArgument(string[], int)"/>
    public static T? TryGetArgument<T>(this string[] source, int index)
        where T : IConvertible
    {
        var res = TryGetArgument(source, index);
        if (res is null)
            return (default);
        else
            return ((T)Convert.ChangeType(res, typeof(T)));
    }
    /// <inheritdoc cref="GetArgument(string[], int)"/>
    public static string? TryGetArgument(this string[] source, int index)
    {
        if (index < source.Length)
            return (source.ElementAt(index));
        else
            return (null);
    }
    /// <inheritdoc cref="GetArguments(string[], int)"/>
    public static string? TryGetArguments(this string[] source, int index)
    {
        if (index < source.Length)
            return (string.Join(' ', source.Skip(index)));
        else
            return (null);
    }
}