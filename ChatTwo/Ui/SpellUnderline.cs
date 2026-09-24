// TildeTools: written for this fork, not part of upstream Chat 2.

using System.Numerics;
using ChatTwo.Ipc;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace ChatTwo.Ui;

public sealed class SpellUnderline
{
    private static readonly Vector4 Colour = new(1f, 0.25f, 0.25f, 1f);

    private const float Drop = 1.5f;
    private const float Thickness = 1.5f;

    private readonly Plugin Plugin;

    private string PendingWord = string.Empty;

    // Only the position tells two identical words apart. Without it the fourth "teh" fixed the first
    private int PendingAt = -1;

    public SpellUnderline(Plugin plugin) => Plugin = plugin;

    private (IReadOnlyList<Misspelling> For, float Font, List<(float Left, float Width)> At) Measured = ([], 0f, []);

    // Off the input's own state, not guessed from the caret, which only pins the scroll at the
    // line's end. No state until clicked into, and unfocused draws from the start
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

    public (int Start, int End) InputSelection { get; private set; } = (-1, -1);

    // Must run while the input is current, before the early exits: text with no misspellings still has a selection
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

    // Call immediately after the input, while it's the current item
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

        // Clipped to the frame like the glyphs. Insetting by the padding shaves the end letters' marks
        drawList.PushClipRect(new Vector2(min.X, min.Y), new Vector2(min.X + size.X, min.Y + size.Y), true);

        var colour = ImGui.GetColorU32(Colour);
        var thick = Thickness * ImGuiHelpers.GlobalScale;

        // Under the text, never past the box. Scaled-up small padding would hang it outside
        var y = Math.Min(
            min.Y + size.Y - ImGui.GetStyle().FramePadding.Y + Drop * ImGuiHelpers.GlobalScale,
            min.Y + size.Y - thick);

        var mouseX = ImGui.GetIO().MousePos.X;

        var rightClicked = ImGui.IsMouseClicked(ImGuiMouseButton.Right);
        var clickedWord = string.Empty;
        var clickedAt = -1;

        var fontSize = ImGui.GetFontSize();
        if (!ReferenceEquals(Measured.For, misspellings) || Measured.Font != fontSize)
        {
            // Whole prefix each time: CalcTextSize rounds up per call, so a running total drifts a pixel per word
            List<(float Left, float Width)> at = new(misspellings.Count);
            foreach (var misspelling in misspellings)
            {
                var valid = misspelling.Start >= 0 && misspelling.Start + misspelling.Length <= text.Length;
                at.Add(valid
                    ? (ImGui.CalcTextSize(text.AsSpan(0, misspelling.Start)).X,
                       ImGui.CalcTextSize(text.AsSpan(misspelling.Start, misspelling.Length)).X)
                    : (float.NaN, 0f));
            }

            Measured = (misspellings, fontSize, at);
        }

        for (var i = 0; i < misspellings.Count; i++)
        {
            var misspelling = misspellings[i];
            var (offset, width) = Measured.At[i];
            if (float.IsNaN(offset))
                continue;

            var left = min.X + inset - scroll + offset;
            var right = left + width;

            drawList.AddLine(new Vector2(left, y), new Vector2(right, y), colour, thick);

            // Latched on the click, clearing it later empties the menu as it opens
            if (hovered && rightClicked && mouseX >= left && mouseX <= right)
            {
                clickedWord = misspelling.Word(text);

                // The box draws its own text, so this is already the index a correction rewrites
                clickedAt = misspelling.Start;
            }
        }

        drawList.PopClipRect();

        // Only for a click in this box. The preview draws first, so a click latched there got wiped here
        if (rightClicked && hovered)
        {
            PendingWord = clickedWord;
            PendingAt = clickedAt;
        }
    }

    // at is -1 when unknown, falling back to first-match. A wrong index rewrites a word nobody clicked
    public void SetPendingWord(string word, int at = -1)
    {
        PendingWord = word;
        PendingAt = at;
    }

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

    public static string ReplaceWord(string text, string word, string replacement, int at)
    {
        if (at >= 0 && at + word.Length <= text.Length &&
            string.CompareOrdinal(text, at, word, 0, word.Length) == 0)
            return text[..at] + replacement + text[(at + word.Length)..];

        return ReplaceWord(text, word, replacement);
    }

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
