using System.Text;
// TildeTools
using ChatTwo.Ipc;
// TildeTools ends
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

    // TildeTools
    private (string Input, string? Name, uint World, InputChannel Channel, string Line) Composed = (string.Empty, null, 0, InputChannel.Invalid, string.Empty);

    // Mirrors SendChatBox so the preview agrees with the send. Change one, change both
    public string ComposeLine(Tab activeTab, string chatInput)
    {
        var target = activeTab.TellTarget.IsSet()
            ? activeTab.TellTarget
            : activeTab.CurrentChannel.TempTellTarget ?? activeTab.CurrentChannel.TellTarget;

        var channel = activeTab.CurrentChannel.UseTempChannel ? activeTab.CurrentChannel.TempChannel : activeTab.CurrentChannel.Channel;

        // Cached: asked every frame, and a tell's head reads the World sheet
        // At 18k characters, trimming and joining copy 36 KB each
        if ((chatInput, target?.Name, target?.World ?? 0, channel) == (Composed.Input, Composed.Name, Composed.World, Composed.Channel))
            return Composed.Line;

        var head = target != null ? $"/tell {target.ToTargetString()}" : channel.Prefix();
        var trimmed = chatInput.Trim();
        var line = trimmed.Length == 0 || trimmed.StartsWith('/') ? trimmed : $"{head} {trimmed}";

        Composed = (chatInput, target?.Name, target?.World ?? 0, channel, line);
        return line;
    }

    // False when the text stays in the box, so the caller keeps the temp channel for the retry
    public bool SendChatBox(Tab activeTab, ref string chatInput, ref bool tellSpecial)
    // TildeTools ends
    {
        if (!string.IsNullOrWhiteSpace(chatInput))
        {
            var trimmed = chatInput.Trim();
            AddBacklog(trimmed);
            InputBacklogIdx = -1;

            // TildeTools
            // Neither can be split: payload bytes, and the game's tell command
            if ((tellSpecial || Splitter.StartsWithTranslationCommand(trimmed)) && Splitter.KeptForLength(trimmed))
                return false;
            // TildeTools ends

            if (HasTranslationCommand(trimmed))
            {
                activeTab.CurrentChannel.ResetTempChannel();
                chatInput = string.Empty;
                // TildeTools
                return true;
                // TildeTools ends
            }

            if (tellSpecial)
            {
                var tellBytes = Encoding.UTF8.GetBytes(trimmed);
                AutoTranslate.ReplaceWithPayload(ref tellBytes);

                Plugin.Functions.Chat.SendTellUsingCommandInner(tellBytes);
                tellSpecial = false;

                activeTab.CurrentChannel.ResetTempChannel();
                chatInput = string.Empty;
                // TildeTools
                return true;
                // TildeTools ends
            }

            if (!trimmed.StartsWith('/'))
            {
                var target = activeTab.TellTarget.IsSet() ? activeTab.TellTarget : activeTab.CurrentChannel.TempTellTarget ?? activeTab.CurrentChannel.TellTarget;
                if (target != null)
                {
                    // TildeTools
                    // Every tell, one that fits can carry a break marker
                    var tellLine = $"/tell {target.ToTargetString()} {trimmed}";
                    var tellTake = Plugin.Splitter.Offer(tellLine);

                    if (tellTake == SplitTake.Queued)
                    {
                        activeTab.CurrentChannel.ResetTempChannel();
                        chatInput = string.Empty;
                        return true;
                    }

                    if (tellTake == SplitTake.Refused || Splitter.KeptForLength(target.ContentId == 0 ? tellLine : trimmed))
                        return false;
                    // TildeTools ends

                    // ContentId 0 is a case where we can't directly send messages, so we send a /tell formatted message and let the game handle it
                    if (target.ContentId == 0)
                    {
                        trimmed = $"/tell {target.ToTargetString()} {trimmed}";
                        var tellBytes = Encoding.UTF8.GetBytes(trimmed);
                        AutoTranslate.ReplaceWithPayload(ref tellBytes);

                        ChatBox.SendMessageUnsafe(tellBytes);

                        activeTab.CurrentChannel.ResetTempChannel();
                        chatInput = string.Empty;
                        // TildeTools
                        return true;
                        // TildeTools ends
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
                    // TildeTools
                    return true;
                    // TildeTools ends
                }

                if (activeTab.CurrentChannel.UseTempChannel)
                    trimmed = $"{activeTab.CurrentChannel.TempChannel.Prefix()} {trimmed}";
                else
                    trimmed = $"{activeTab.CurrentChannel.Channel.Prefix()} {trimmed}";
            }

            // TildeTools
            // Before auto-translate becomes bytes, so the splitter only sees plain text
            // Every line, it declines one that fits with no break marker
            var take = Plugin.Splitter.Offer(trimmed);

            // Must not go out as is
            // False keeps the text, and the caller keeps its temp channel
            if (take == SplitTake.Refused || take == SplitTake.NotTaken && Splitter.KeptForLength(trimmed))
                return false;

            if (take == SplitTake.NotTaken)
            {
                var bytes = Encoding.UTF8.GetBytes(trimmed);
                AutoTranslate.ReplaceWithPayload(ref bytes);

                ChatBox.SendMessageUnsafe(bytes);
            }
            // TildeTools ends
        }

        activeTab.CurrentChannel.ResetTempChannel();
        chatInput = string.Empty;
        // TildeTools
        return true;
        // TildeTools ends
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