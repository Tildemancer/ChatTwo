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

    // TildeTools
    private (string Input, string Head, string Line) Composed = (string.Empty, string.Empty, string.Empty);

    // TildeTools
    // Mirrors SendChatBox so the preview agrees with the send. Change one, change both
    public string ComposeLine(Tab activeTab, string chatInput)
    {
        var target = activeTab.TellTarget.IsSet()
            ? activeTab.TellTarget
            : activeTab.CurrentChannel.TempTellTarget ?? activeTab.CurrentChannel.TellTarget;

        var head = target != null
            ? $"/tell {target.ToTargetString()}"
            : activeTab.CurrentChannel.UseTempChannel
                ? activeTab.CurrentChannel.TempChannel.Prefix()
                : activeTab.CurrentChannel.Channel.Prefix();

        // TildeTools
        // Asked every frame, and trimming and joining the same line again is a 36 KB copy each at 18k characters
        if (chatInput == Composed.Input && head == Composed.Head)
            return Composed.Line;

        var trimmed = chatInput.Trim();
        var line = trimmed.Length == 0 || trimmed.StartsWith('/') ? trimmed : $"{head} {trimmed}";

        Composed = (chatInput, head, line);
        return line;
    }

    // TildeTools
    // False when the text stays in the box, so the caller keeps the temp channel for the retry
    public bool SendChatBox(Tab activeTab, ref string chatInput, ref bool tellSpecial)
    {
        if (!string.IsNullOrWhiteSpace(chatInput))
        {
            var trimmed = chatInput.Trim();
            AddBacklog(trimmed);
            InputBacklogIdx = -1;

            // TildeTools
            // Neither can be split: payload bytes, and the game's tell command
            if ((tellSpecial || StartsWithTranslationCommand(trimmed)) && KeptForLength(trimmed))
                return false;

            if (HasTranslationCommand(trimmed))
            {
                activeTab.CurrentChannel.ResetTempChannel();
                chatInput = string.Empty;
                return true;
            }

            if (tellSpecial)
            {
                var tellBytes = Encoding.UTF8.GetBytes(trimmed);
                AutoTranslate.ReplaceWithPayload(ref tellBytes);

                Plugin.Functions.Chat.SendTellUsingCommandInner(tellBytes);
                tellSpecial = false;

                activeTab.CurrentChannel.ResetTempChannel();
                chatInput = string.Empty;
                return true;
            }

            if (!trimmed.StartsWith('/'))
            {
                var target = activeTab.TellTarget.IsSet() ? activeTab.TellTarget : activeTab.CurrentChannel.TempTellTarget ?? activeTab.CurrentChannel.TellTarget;
                if (target != null)
                {
                    // TildeTools
                    // Every tell, one that fits can carry a break marker
                    var tellTake = Plugin.Splitter.Offer($"/tell {target.ToTargetString()} {trimmed}");

                    if (tellTake == SplitTake.Queued)
                    {
                        activeTab.CurrentChannel.ResetTempChannel();
                        chatInput = string.Empty;
                        return true;
                    }

                    // TildeTools
                    if (tellTake == SplitTake.Refused || KeptForLength(trimmed))
                        return false;

                    // ContentId 0 is a case where we can't directly send messages, so we send a /tell formatted message and let the game handle it
                    if (target.ContentId == 0)
                    {
                        trimmed = $"/tell {target.ToTargetString()} {trimmed}";

                        var tellBytes = Encoding.UTF8.GetBytes(trimmed);
                        AutoTranslate.ReplaceWithPayload(ref tellBytes);

                        ChatBox.SendMessageUnsafe(tellBytes);

                        activeTab.CurrentChannel.ResetTempChannel();
                        chatInput = string.Empty;
                        return true;
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
                    return true;
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

            // TildeTools
            // Must not go out as is
            // False keeps the text, and the caller keeps its temp channel
            if (take == SplitTake.Refused || take == SplitTake.NotTaken && KeptForLength(trimmed))
                return false;

            if (take == SplitTake.NotTaken)
            {
                var bytes = Encoding.UTF8.GetBytes(trimmed);
                AutoTranslate.ReplaceWithPayload(ref bytes);

                ChatBox.SendMessageUnsafe(bytes);
            }
        }

        activeTab.CurrentChannel.ResetTempChannel();
        chatInput = string.Empty;
        return true;
    }

    // TildeTools
    // Over the cap the game drops a line silently, and the text with it, so it stays in the box
    private static bool KeptForLength(string line)
    {
        if (Encoding.UTF8.GetByteCount(line) <= Splitter.DefaultByteCap)
            return false;

        Plugin.ChatGui.PrintError("[Chat 2] That message is too long to send this way, so it was kept in the box.");
        return true;
    }

    // TildeTools
    // On a copy, StartsWithCommand rewrites what it's given
    private static bool StartsWithTranslationCommand(string trimmed)
    {
        var bytes = Encoding.UTF8.GetBytes(trimmed);
        return AutoTranslate.StartsWithCommand(ref bytes);
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