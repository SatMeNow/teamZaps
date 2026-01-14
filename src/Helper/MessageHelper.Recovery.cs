using System.Text;
using TeamZaps.Services;
using TeamZaps.Session;
using TeamZaps.Utils;

namespace TeamZaps.Helper;

internal static partial class Ext
{
    public static StringBuilder AppendRecoveryMessage(this StringBuilder source, LostSatsRecord lostSats)
    {
        source.AppendLine("🔍 *Lost and Found recovery*\n");
        source.AppendLine($"I found *{lostSats.SatsAmount.Format()}* of lost funds from a previously interrupted session.\n");
        source.AppendLine($"📅 From: {lostSats.Timestamp:f}"); // Freitag, 31. Oktober 2008 17:04
        source.AppendLineIfNotNull("💬 Reason: {0}", lostSats.Reason);
        source.AppendLine();
        source.Append("Just *send me a lightning invoice* if you want to *claim now*.");

        return (source);
    }
}