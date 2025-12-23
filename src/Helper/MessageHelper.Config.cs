using teamZaps.Configuration;
using teamZaps.Services;
using teamZaps.Sessions;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace teamZaps.Helper;

internal static class ConfigMessage
{
    public static Task<Message> SendAsync(ITelegramBotClient botClient, long chatId, string chatTitle, BotAdminOptions options, CancellationToken cancellationToken) => botClient.SendMessage(chatId,
        text: BuildText(chatTitle),
        parseMode: ParseMode.Markdown,
        replyMarkup: BuildKeyboard(options),
        disableNotification: true,
        cancellationToken: cancellationToken);
    public static Task UpdateAsync(ITelegramBotClient botClient, long chatId, int messageId, string chatTitle, BotAdminOptions options, CancellationToken cancellationToken) => botClient.EditMessageText(
        chatId: chatId,
        messageId: messageId,
        text: BuildText(chatTitle),
        parseMode: ParseMode.Markdown,
        replyMarkup: BuildKeyboard(options),
        cancellationToken: cancellationToken);
    private static string BuildText(string chatTitle)
    {
        return ($"⚙️ *Session configuration*\n\n" +
            $"Configure who can manage sessions in *{chatTitle}*:\n\n" +
            "• *Non-admin start*: Allow any member to start a session\n" +
            "• *Non-admin close*: Allow any member to close a session and start the lottery\n" +
            "• *Non-admin cancel*: Allow any member to cancel a session\n\n" +
            "Click the buttons below to toggle settings:");
    }

    private static InlineKeyboardMarkup BuildKeyboard(BotAdminOptions options)
    {
        Func<bool, string, InlineKeyboardButton> createButton = (enabled, label) => InlineKeyboardButton.WithCallbackData(
            $"{(enabled ? "✅" : "❌")} Non-admin {label}",
            $"{CallbackActions.AdminOptions}_{label.ToLower().Replace(" ", "")}"
        );
        return (new InlineKeyboardMarkup(new[]
        {
            new[] { createButton(options.AllowNonAdminSessionStart, "start") },
            new[] { createButton(options.AllowNonAdminSessionClose, "close") },
            new[] { createButton(options.AllowNonAdminSessionCancel, "cancel") }
        }));
    }
}
