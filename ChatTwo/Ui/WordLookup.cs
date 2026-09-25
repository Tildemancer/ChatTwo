// TildeTools: written for this fork, not part of upstream Chat 2.

using System.Text;
using ChatTwo.Ipc;
using Dalamud.Bindings.ImGui;

namespace ChatTwo.Ui;

// A word right-clicked in the log, for Define at the top of the message menu: "That's sophistry, and you know it!"
public static class WordLookup
{
    // The piece of text clicked and the character under the pointer, read when the menu draws
    private static (string Line, int Index) Clicked = ("", -1);
    private static int ClickFrame = -1;

    // Right after a piece of message text is drawn, while it's the current item
    public static unsafe void Watch(byte* text, byte* textEnd)
    {
        if (!ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            return;

        // Forgotten once each right-click, so a click on an icon or emote can't bring back the last word
        if (ClickFrame != ImGui.GetFrameCount())
            (Clicked, ClickFrame) = (("", -1), ImGui.GetFrameCount());

        if (!ImGui.IsItemHovered())
            return;

        var line = Encoding.UTF8.GetString(text, (int)(textEnd - text));
        Clicked = (line, SpellUnderline.IndexAt(line, ImGui.GetIO().MousePos.X - ImGui.GetItemRectMin().X));
    }

    public static void DrawDefine(SpellCheck spelling)
    {
        if (!spelling.IsAvailable || spelling.WordAt(Clicked.Line, Clicked.Index) is not var (start, length))
            return;

        var word = Clicked.Line.Substring(start, length);

        // Nothing in the log to replace, so no Use
        if (ImGui.Selectable($"Define \"{word}\""))
            spelling.Define(word, word, null);

        ImGui.Separator();
    }
}
