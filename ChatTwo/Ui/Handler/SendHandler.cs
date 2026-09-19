using System.Text;
using ChatTwo.Ipc;
using ChatTwo.Code;
using ChatTwo.GameFunctions;
using ChatTwo.GameFunctions.Types;
using ChatTwo.Util;

namespace ChatTwo.Ui.Handler;

public class SendHandler
{
    private readonly Plugin Plugin;

    public List<string> InputBacklog = [];
    public int InputBacklogIdx = -1;

    private string Message = string.Empty;
    private bool TellSpecialUnused;

    public SendHandler(Plugin plugin)
    {
        Plugin = plugin;
    }

    public void SendWithoutContext(string message)
    {
        Message = message;
        SendChatBox(Plugin.CurrentTab, ref Message, ref TellSpecialUnused);
    }

    /// <summary>
    /// The finished chat line that <see cref="SendChatBox"/> would build from this
    /// input: the channel prefix or tell target, followed by the text.
    ///
    /// Used to show what a splitter will do with a message before it is sent, so
    /// the preview and the send agree. It has to follow the shape below; if the
    /// composition there changes, this changes with it.
    /// </summary>
    public static string ComposeLine(Tab activeTab, string chatInput)
    {
        var trimmed = chatInput.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('/'))
            return trimmed;

        var target = activeTab.TellTarget.IsSet()
            ? activeTab.TellTarget
            : activeTab.CurrentChannel.TempTellTarget ?? activeTab.CurrentChannel.TellTarget;

        if (target != null)
            return $"/tell {target.ToTargetString()} {trimmed}";

        var prefix = activeTab.CurrentChannel.UseTempChannel
            ? activeTab.CurrentChannel.TempChannel.Prefix()
            : activeTab.CurrentChannel.Channel.Prefix();

        return $"{prefix} {trimmed}";
    }

    public void SendChatBox(Tab activeTab, ref string chatInput, ref bool tellSpecial)
    {
        if (!string.IsNullOrWhiteSpace(chatInput))
        {
            var trimmed = chatInput.Trim();
            AddBacklog(trimmed);
            InputBacklogIdx = -1;

            if (HasTranslationCommand(trimmed))
            {
                activeTab.CurrentChannel.ResetTempChannel();
                chatInput = string.Empty;
                return;
            }

            if (tellSpecial)
            {
                var tellBytes = Encoding.UTF8.GetBytes(trimmed);
                AutoTranslate.ReplaceWithPayload(ref tellBytes);

                Plugin.Functions.Chat.SendTellUsingCommandInner(tellBytes);
                tellSpecial = false;

                activeTab.CurrentChannel.ResetTempChannel();
                chatInput = string.Empty;
                return;
            }

            if (!trimmed.StartsWith('/'))
            {
                var target = activeTab.TellTarget.IsSet() ? activeTab.TellTarget : activeTab.CurrentChannel.TempTellTarget ?? activeTab.CurrentChannel.TellTarget;
                if (target != null)
                {
                    // ContentId 0 is a case where we can't directly send messages, so we send a /tell formatted message and let the game handle it
                    //
                    // An over-length tell takes the same route even when we could
                    // send it directly, because SendTell has no way to break a
                    // message up: it is one call carrying one message. Written as a
                    // command instead, a splitter can cut it into several tells.
                    var oversized = Encoding.UTF8.GetByteCount(trimmed) > Splitter.DefaultByteCap;
                    if (target.ContentId == 0 || oversized)
                    {
                        trimmed = $"/tell {target.ToTargetString()} {trimmed}";

                        if (oversized && Plugin.Splitter.TrySend(trimmed))
                        {
                            activeTab.CurrentChannel.ResetTempChannel();
                            chatInput = string.Empty;
                            return;
                        }

                        var tellBytes = Encoding.UTF8.GetBytes(trimmed);
                        AutoTranslate.ReplaceWithPayload(ref tellBytes);

                        ChatBox.SendMessageUnsafe(tellBytes);

                        activeTab.CurrentChannel.ResetTempChannel();
                        chatInput = string.Empty;
                        return;
                    }

                    var reason = target.Reason;
                    var world = Sheets.WorldSheet.GetRow(target.World);
                    if (world is { IsPublic: true })
                    {
                        if (reason == TellReason.Reply && GameFunctions.GameFunctions.GetFriends().Any(friend => friend.ContentId == target.ContentId))
                            reason = TellReason.Friend;

                        var tellBytes = Encoding.UTF8.GetBytes(trimmed);
                        AutoTranslate.ReplaceWithPayload(ref tellBytes);

                        Plugin.Functions.Chat.SendTell(reason, target.ContentId, target.Name, (ushort) world.RowId, tellBytes, trimmed);
                    }

                    activeTab.CurrentChannel.ResetTempChannel();
                    chatInput = string.Empty;
                    return;
                }

                if (activeTab.CurrentChannel.UseTempChannel)
                    trimmed = $"{activeTab.CurrentChannel.TempChannel.Prefix()} {trimmed}";
                else
                    trimmed = $"{activeTab.CurrentChannel.Channel.Prefix()} {trimmed}";
            }

            // Offered before auto-translate is turned into payload bytes, so the
            // splitter sees plain text it can safely cut. Only over-length messages
            // are offered, so an ordinary send costs nothing.
            var splitterTookIt =
                Encoding.UTF8.GetByteCount(trimmed) > Splitter.DefaultByteCap &&
                Plugin.Splitter.TrySend(trimmed);

            if (!splitterTookIt)
            {
                var bytes = Encoding.UTF8.GetBytes(trimmed);
                AutoTranslate.ReplaceWithPayload(ref bytes);

                ChatBox.SendMessageUnsafe(bytes);
            }
        }

        activeTab.CurrentChannel.ResetTempChannel();
        chatInput = string.Empty;
    }

    private bool HasTranslationCommand(string trimmed)
    {
        var messageBytes = Encoding.UTF8.GetBytes(trimmed);
        if (AutoTranslate.StartsWithCommand(ref messageBytes))
        {
            ChatBox.SendMessageUnsafe(messageBytes);
            return true;
        }

        return false;
    }

    public void AddBacklog(string message)
    {
        for (var i = 0; i < InputBacklog.Count; i++)
        {
            if (InputBacklog[i] != message)
                continue;

            InputBacklog.RemoveAt(i);
            break;
        }

        InputBacklog.Add(message);
    }
}