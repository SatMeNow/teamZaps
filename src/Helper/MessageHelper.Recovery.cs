using System.Text;
using teamZaps.Services;
using teamZaps.Sessions;
using teamZaps.Utils;

namespace teamZaps.Helper;

internal static partial class Ext
{
    public static StringBuilder AppendRecoveryMessage(this StringBuilder source, LostSatsRecord lostSats)
    {
        source.AppendLine("🔍 *Lost and Found recovery*\n");
        source.AppendLine($"I found *{lostSats.SatsAmount.Format()}* of lost funds from a previously interrupted session.\n");
        source.AppendLine($"📅 From: {lostSats.Timestamp:yyyy-MM-dd HH:mm}");
        source.AppendLineIfNotNull("💬 Reason: {0}", lostSats.Reason);
        source.AppendLine();
        source.Append("Just *send me a lightning invoice* if you want to *claim now*.");

        return (source);
    }
}