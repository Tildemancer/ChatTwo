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
    private (string Input, string? Name, uint World, InputChannel Channel, bool TellSpecial, bool Unoffered, string Line) Composed = (string.Empty, null, 0, InputChannel.Invalid, false, false, string.Empty);

    // Mirrors SendChatBox, make sure you change both together.
    // I know that technically I could just replace it, but I don't want to :) so.
    // (Making SendChatBox call this would edit upstream and be annoying for me later)
    public string ComposeLine(Tab activeTab, string chatInput, bool tellSpecial)
    {
        var target = activeTab.TellTarget.IsSet()
            ? activeTab.TellTarget
            : activeTab.CurrentChannel.TempTellTarget ?? activeTab.CurrentChannel.TellTarget;

        var channel = activeTab.CurrentChannel.UseTempChannel ? activeTab.CurrentChannel.TempChannel : activeTab.CurrentChannel.Channel;

        // SendChatBox never offers these to the splitter, so they compose to nothing and show no split.
        // Checked outside the cache, a foray can start while the text stays the same.
        var unoffered = target is { ContentId: not 0 } && (Sheets.IsInForay() || target.Reason == TellReason.PartyFinder);

        // Cached because it's called every frame and a tell's head reads the World sheet.
        if ((chatInput, target?.Name, target?.World ?? 0, channel, tellSpecial, unoffered) == (Composed.Input, Composed.Name, Composed.World, Composed.Channel, Composed.TellSpecial, Composed.Unoffered))
            return Composed.Line;

        var head = target != null ? $"/tell {target.ToTargetString()}" : channel.Prefix();
        var trimmed = chatInput.Trim();
        var line = trimmed.Length == 0 || tellSpecial || Splitter.StartsWithTranslationCommand(trimmed) ? string.Empty
            : trimmed.StartsWith('/') ? trimmed
            : unoffered ? string.Empty
            : $"{head} {trimmed}";

        Composed = (chatInput, target?.Name, target?.World ?? 0, channel, tellSpecial, unoffered, line);
        return line;
    }

    // False when the text stays in the box, and the caller keeps the temp channel.
    public bool SendChatBox(Tab activeTab, ref string chatInput, ref bool tellSpecial)
    // TildeTools ends
    {
        if (!string.IsNullOrWhiteSpace(chatInput))
        {
            var trimmed = chatInput.Trim();
            AddBacklog(trimmed);
            InputBacklogIdx = -1;

            // TildeTools
            // Payload bytes and the game's tell command can't be split.
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
                    // Offered at any length, since one that fits can carry a break marker.
                    // Not in forays or to Party Finder contacts, which C2 sends by content id
                    // Their temp channel is set per message, so split /tell parts may not arrive.
                    var tellLine = $"/tell {target.ToTargetString()} {trimmed}";
                    var tellTake = target.ContentId != 0 && (Sheets.IsInForay() || target.Reason == TellReason.PartyFinder)
                        ? SplitTake.NotTaken
                        : Plugin.Splitter.Offer(tellLine);

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
            // Before auto-translate becomes bytes, that way the splitter sees plain text
            // Every line goes to it, and it declines one that fits without a break marker.
            var take = Plugin.Splitter.Offer(trimmed);

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