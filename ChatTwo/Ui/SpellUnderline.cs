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
    /// Where the pending word sits in the text a correction will rewrite, or -1 when
    /// unknown.
    ///
    /// Only the position tells two identical words apart. Without it, correcting the
    /// fourth "teh" in a paragraph rewrote the first one, and the one clicked stayed
    /// where it was.
    /// </summary>
    private int PendingAt = -1;

    public SpellUnderline(Plugin plugin) => Plugin = plugin;

    /// <summary>
    /// How far the box has scrolled sideways.
    ///
    /// Taken off the input's own state, not worked out from the caret. The caret only
    /// pins the scroll down while you are typing at the end of the line. Move the
    /// cursor back into the middle and the true offset can sit anywhere in a box-width
    /// range, so guessing drifted further the more was typed.
    ///
    /// No state until the box has been clicked into, and an unfocused box draws from
    /// the start, so there is nothing to offset by then.
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
    /// What the box currently has selected, as positions in its own text, or (-1, -1)
    /// for nothing. Read so the preview can show the same selection.
    /// </summary>
    public (int Start, int End) InputSelection { get; private set; } = (-1, -1);

    /// <summary>
    /// Reads that selection off the box's own state. MUST run while the input is the
    /// current item, and before the early exits below. Text with nothing misspelled in
    /// it still has a selection worth mirroring.
    /// </summary>
    private unsafe void CaptureInputSelection()
    {
        var state = ImGuiP.GetInputTextState(ImGuiP.GetItemID());
        if (state.IsNull)
        {
            InputSelection = (-1, -1);
            return;
        }

        var from = state.Stb.SelectStart;
        var to = state.Stb.SelectEnd;

        InputSelection = from == to
            ? (-1, -1)
            : (Math.Min(from, to), Math.Max(from, to));
    }

    /// <summary>
    /// Underlines the misspellings in the input just drawn, and offers corrections on
    /// right-click. Call this IMMEDIATELY after the input, while its box is current.
    /// </summary>
    public void DrawForInput(ref string text)
    {
        CaptureInputSelection();

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

        // Just under the text, never past the bottom of the box. Little vertical
        // padding, scaled up, would otherwise hang the line outside the frame.
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

            // Whole prefix each time, not a running total. Turns out CalcTextSize
            // rounds each call up to a whole pixel, so the total drifts a pixel per
            // word. By the tenth the line sits off the end.
            var left = min.X + inset - scroll + ImGui.CalcTextSize(text.AsSpan(0, misspelling.Start)).X;
            var right = left + ImGui.CalcTextSize(text.Substring(misspelling.Start, misspelling.Length)).X;

            drawList.AddLine(new Vector2(left, y), new Vector2(right, y), colour, thick);

            // Latch it on the click. Clearing it later empties the menu as it opens.
            if (hovered && rightClicked && mouseX >= left && mouseX <= right)
            {
                clickedWord = misspelling.Word(text);

                // The box draws the input itself, so this is already an index into the
                // string a correction rewrites. Nothing to map.
                clickedAt = misspelling.Start;
            }
        }

        drawList.PopClipRect();

        // Only when the click landed in THIS box. This used to fire on every right
        // click anywhere. The preview draws before the input, so a click latched in
        // the preview got wiped here on the same frame.
        if (rightClicked && hovered)
        {
            PendingWord = clickedWord;
            PendingAt = clickedAt;
        }
    }

    /// <param name="at">
    /// Where the word sits in the text the correction will rewrite, or -1 when the
    /// caller cannot say. Unknown falls back to first-match. A wrong index rewrites a
    /// word nobody clicked.
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
    /// Same shape as the native chat box's corrections.
    /// </summary>
    public static string ReplaceWord(string text, string word, string replacement, int at)
    {
        if (at >= 0 && at + word.Length <= text.Length &&
            string.CompareOrdinal(text, at, word, 0, word.Length) == 0)
            return text[..at] + replacement + text[(at + word.Length)..];

        // Position unknown, or the text moved between the click and the pick. Back to
        // first-match rather than rewriting blind.
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
