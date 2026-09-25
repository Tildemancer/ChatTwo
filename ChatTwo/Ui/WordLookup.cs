// TildeTools: written for this fork, not part of upstream Chat 2.

using System.Text;
using ChatTwo.Ipc;
using Dalamud.Bindings.ImGui;

namespace ChatTwo.Ui;

public static class WordLookup
{
    private static (string Line, int Index) Clicked = ("", -1);
    private static int ClickFrame = -1;

    private static string? Word;

    // Right after text..textEnd is drawn, while it's the current item
    public static unsafe void Watch(byte* text, byte* textEnd)
    {
        // Left too: upstream's LeftClickPayload falls through to RightClickPayload
        if (!ImGui.IsMouseClicked(ImGuiMouseButton.Right) && !ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            return;

        // Cleared each click on the log, or clicking an icon or emote shows the last word
        // Not a click in the open menu, which stays open
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

        if (ImGui.Selectable($"Define \"{Word}\""))
            spelling.Define(Word, Word, null);

        ImGui.Separator();
    }
}
