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

    /// <summary>
    /// Where the pending word sits in the text a correction will rewrite, or -1 when we
    /// could not work it out.
    ///
    /// Only the position tells two identical words apart. Without it, correcting the
    /// fourth "teh" in a paragraph rewrote the first one, and clicking again walked
    /// through them in order while the one under the pointer sat there unchanged.
    /// </summary>
    private int PendingAt = -1;

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
        var clickedAt = -1;

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
            {
                clickedWord = misspelling.Word(text);

                // The box draws the input itself, so this is already an index into the
                // very string a correction rewrites. Nothing to map.
                clickedAt = misspelling.Start;
            }
        }

        drawList.PopClipRect();

        // Only when the click landed in THIS box. It used to claim every right click
        // anywhere, and since the preview is drawn before the input, a click in the
        // preview was latched by the preview and then wiped here on the same frame.
        if (rightClicked && hovered)
        {
            PendingWord = clickedWord;
            PendingAt = clickedAt;
        }
    }

    /// <param name="at">
    /// Where the word sits in the text the correction will rewrite, or -1 when the
    /// caller cannot say. Wrong is worse than unknown here: unknown falls back to the
    /// old first-match behaviour, wrong rewrites a word nobody pointed at.
    /// </param>
    public void SetPendingWord(string word, int at = -1)
    {
        PendingWord = word;
        PendingAt = at;
    }

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
                    text = ReplaceWord(text, PendingWord, suggestion, PendingAt);
        }

        ImGui.Separator();
        return true;
    }

    /// <summary>
    /// Swaps the occurrence at <paramref name="at"/>, or the first whole-word one when
    /// that position is unknown or no longer holds the word.
    ///
    /// The same shape as the native chat box's corrections, which had this right all
    /// along; this fork was written without it.
    /// </summary>
    public static string ReplaceWord(string text, string word, string replacement, int at)
    {
        if (at >= 0 && at + word.Length <= text.Length &&
            string.CompareOrdinal(text, at, word, 0, word.Length) == 0)
            return text[..at] + replacement + text[(at + word.Length)..];

        // Either nobody could say where it was, or the text moved under us between the
        // click and the pick. Back to the old behaviour rather than rewriting blind.
        return ReplaceWord(text, word, replacement);
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
