using System.Diagnostics;
using TeamZaps.Services;
using TeamZaps.Session;
using TeamZaps.Utils;

namespace TeamZaps.Handlers;

internal static class WelcomeMessage
{
    public static async Task SendAsync(SessionState session, User firstUser, ITelegramBotClient botClient, CancellationToken cancellationToken)
    {
        Debug.Assert(session.PendingWelcome is null);
        
        session.PendingWelcome = new(0, [ firstUser ]);
        var botUser = await botClient.GetBotUser(cancellationToken).ConfigureAwait(false);
        var msg = await botClient.SendMessage(session.ChatId, Build(session, botUser), parseMode: ParseMode.Markdown, cancellationToken: cancellationToken).ConfigureAwait(false);
        session.PendingWelcome = session.PendingWelcome with { MessageId = msg.MessageId };
    }

    public static async Task UpdateAsync(SessionState session, ITelegramBotClient botClient, CancellationToken cancellationToken)
    {
        Debug.Assert(session.PendingWelcome is not null);
        
        var botUser = await botClient.GetBotUser(cancellationToken).ConfigureAwait(false);
        await botClient.EditMessageText(session.ChatId, session.PendingWelcome!.MessageId, Build(session, botUser), parseMode: ParseMode.Markdown, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public static async Task DeleteAsync(SessionState session, ITelegramBotClient botClient, CancellationToken cancellationToken)
    {
        Debug.Assert(session.PendingWelcome is not null);
        
        await botClient.DeleteMessageAsync(session.ChatId, session.PendingWelcome!.MessageId, cancellationToken).ConfigureAwait(false);
        session.PendingWelcome = null;
    }

    private static string Build(SessionState session, User botUser)
    {
        var welcome = session.PendingWelcome!;
        var salutation = string.Join(", ", welcome.PendingUsers.Select(u => $"@{u.UserName()}"));
        return $"Hey {salutation}, we did not meet before ✌️\n" +
               "I'm a telegram bot, *helping you* and your friends *to coordinate lightning payments*.\n\n" +
               $"ℹ️ Please *start a private chat* to interact with me, by clicking @{botUser.UserName()}. See you soon 👍";
    }
}
