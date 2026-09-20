// TildeTools: written for this fork, not part of upstream Chat 2.

using System.Numerics;
using ChatTwo.Ipc;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace ChatTwo.Ui;

/// <summary>Marks misspelled words with a red underline and offers corrections.</summary>
public sealed class SpellUnderline
{
    private static readonly Vector4 Colour = new(1f, 0.25f, 0.25f, 1f);

    private const float Drop = 1.5f;
    private const float Thickness = 1.5f;

    private readonly Plugin Plugin;

    private string PendingWord = string.Empty;

    public SpellUnderline(Plugin plugin) => Plugin = plugin;

    /// <summary>
    /// How far the box has scrolled sideways.
    ///
    /// Taken off the input's own state rather than worked out. The caret only tells
    /// you where the scroll must be while you are typing at the end of the line —
    /// move the cursor back into the middle and the true offset can sit anywhere in
    /// a box-width range, which is why guessing it drifted further the more was typed.
    ///
    /// There is no state until the box has been clicked into, and an unfocused box
    /// draws from the start, so nothing to offset by then.
    /// </summary>
    private static unsafe float ScrollOffset()
    {
        var state = ImGuiP.GetInputTextState(ImGuiP.GetItemID());

        return state.IsNull ? 0f : state.ScrollX;
    }

    public static void UnderlineLastItem()
    {
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var y = max.Y - Drop * ImGuiHelpers.GlobalScale;

        ImGui.GetWindowDrawList().AddLine(
            new Vector2(min.X, y),
            new Vector2(max.X, y),
            ImGui.GetColorU32(Colour),
            Thickness * ImGuiHelpers.GlobalScale);
    }

    /// <summary>
    /// Underlines the misspellings in the input just drawn, and offers corrections on
    /// right-click. Call this IMMEDIATELY after the input, while its box is current.
    /// </summary>
    public void DrawForInput(ref string text)
    {
        if (string.IsNullOrEmpty(text) || !Plugin.SpellCheck.IsAvailable)
            return;

        var misspellings = Plugin.SpellCheck.Check(text);
        if (misspellings.Count == 0)
            return;

        var hovered = ImGui.IsItemHovered();
        var min = ImGui.GetItemRectMin();
        var size = ImGui.GetItemRectSize();

        var inset = ImGui.GetStyle().FramePadding.X;

        var scroll = ScrollOffset();

        var drawList = ImGui.GetWindowDrawList();

        // Clipped to the frame, the way ImGui clips the glyphs themselves. Insetting
        // by the padding would shave the marks under the first and last letters.
        drawList.PushClipRect(new Vector2(min.X, min.Y), new Vector2(min.X + size.X, min.Y + size.Y), true);

        var colour = ImGui.GetColorU32(Colour);
        var thick = Thickness * ImGuiHelpers.GlobalScale;

        // Just under the text, but never past the bottom of the box. A style with
        // little vertical padding, scaled up, would otherwise hang the line outside
        // the frame it belongs to.
        var y = Math.Min(
            min.Y + size.Y - ImGui.GetStyle().FramePadding.Y + Drop * ImGuiHelpers.GlobalScale,
            min.Y + size.Y - thick);

        var mouseX = ImGui.GetIO().MousePos.X;

        var rightClicked = ImGui.IsMouseClicked(ImGuiMouseButton.Right);
        var clickedWord = string.Empty;

        foreach (var misspelling in misspellings)
        {
            if (misspelling.Start < 0 || misspelling.Start + misspelling.Length > text.Length)
                continue;

            // The whole prefix each time, rather than adding up the gaps between the
            // marked words. Turns out CalcTextSize rounds every call up to a whole
            // pixel, so a running total slides a pixel further right per word it has
            // passed, and by the tenth one the line is sitting off the end of it.
            var left = min.X + inset - scroll + ImGui.CalcTextSize(text.AsSpan(0, misspelling.Start)).X;
            var right = left + ImGui.CalcTextSize(text.Substring(misspelling.Start, misspelling.Length)).X;

            drawList.AddLine(new Vector2(left, y), new Vector2(right, y), colour, thick);

            // Latch it on the click. Turns out clearing it later empties the menu just as it opens.
            if (hovered && rightClicked && mouseX >= left && mouseX <= right)
                clickedWord = misspelling.Word(text);
        }

        drawList.PopClipRect();

        if (rightClicked)
            PendingWord = clickedWord;
    }

    public void SetPendingWord(string word) => PendingWord = word;

    /// <summary>
    /// Adds correction entries to the caller's already-open context menu, for the word
    /// the pointer was last over. Does nothing when that was not a marked word.
    /// </summary>
    public bool DrawContextEntries(ref string text)
    {
        if (PendingWord.Length == 0)
            return false;

        ImGui.TextDisabled(PendingWord);

        if (ImGui.Selectable("Add to dictionary"))
            Plugin.SpellCheck.AddToDictionary(PendingWord);

        if (ImGui.Selectable("Ignore for now"))
            Plugin.SpellCheck.Ignore(PendingWord);

        var suggestions = Plugin.SpellCheck.Suggest(PendingWord);
        if (suggestions.Count > 0)
        {
            ImGui.Separator();

            foreach (var suggestion in suggestions.Take(8))
                if (ImGui.Selectable(suggestion))
                    text = ReplaceWord(text, PendingWord, suggestion);
        }

        ImGui.Separator();
        return true;
    }

    /// <summary>Swaps the first whole-word occurrence, never one sitting inside a longer word.</summary>
    public static string ReplaceWord(string text, string word, string replacement)
    {
        for (var i = text.IndexOf(word, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(word, i + 1, StringComparison.Ordinal))
        {
            var beforeOk = i == 0 || !char.IsLetterOrDigit(text[i - 1]);
            var after = i + word.Length;
            var afterOk = after >= text.Length || !char.IsLetterOrDigit(text[after]);

            if (beforeOk && afterOk)
                return text[..i] + replacement + text[after..];
        }

        return text;
    }
}
