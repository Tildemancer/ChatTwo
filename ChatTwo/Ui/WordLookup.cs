// TildeTools: written for this fork, not part of upstream Chat 2.

using System.Text;
using ChatTwo.Ipc;
using Dalamud.Bindings.ImGui;

namespace ChatTwo.Ui;

// A word clicked in the log, for Define at the top of the message menu: "That's sophistry, and you know it!"
public static class WordLookup
{
    // The piece of text clicked and the character under the pointer, read when the menu draws
    private static (string Line, int Index) Clicked = ("", -1);
    private static int ClickFrame = -1;

    // The word there, worked out on the menu's first frame rather than every frame it's open
    private static string? Word;

    // Right after a piece of message text is drawn, while it's the current item
    public static unsafe void Watch(byte* text, byte* textEnd)
    {
        // A left click on plain text opens the same menu: upstream's LeftClickPayload falls through to RightClickPayload
        if (!ImGui.IsMouseClicked(ImGuiMouseButton.Right) && !ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            return;

        // Forgotten once each click on the log, so a click on an icon or emote can't bring back the last word
        // Not for a click inside the open menu, which stays open
        if (ClickFrame != ImGui.GetFrameCount() && ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByPopup))
            (Clicked, ClickFrame, Word) = (("", -1), ImGui.GetFrameCount(), null);

        if (!ImGui.IsItemHovered())
            return;

        var line = Encoding.UTF8.GetString(text, (int)(textEnd - text));
        Clicked = (line, SpellUnderline.IndexAt(line, ImGui.GetIO().MousePos.X - ImGui.GetItemRectMin().X));
    }

    public static void DrawDefine(SpellCheck spelling)
    {
        if (!spelling.IsAvailable)
            return;

        Word ??= spelling.WordAt(Clicked.Line, Clicked.Index) is var (start, length) ? Clicked.Line.Substring(start, length) : "";
        if (Word.Length == 0)
            return;

        // Nothing in the log to replace, so no Use
        if (ImGui.Selectable($"Define \"{Word}\""))
            spelling.Define(Word, Word, null);

        ImGui.Separator();
    }
}
