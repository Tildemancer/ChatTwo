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

    // Off the input's own state, not guessed from the caret, which only pins the scroll at the
    // line's end. No state until clicked into, and unfocused draws from the start
    private static unsafe float ScrollOffset()
    {
        // The state outlives focus, but only an active box draws scrolled or shows its selection
        if (!ImGui.IsItemActive())
            return 0f;

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
        if (state.IsNull || !ImGui.IsItemActive())
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
        var rightClicked = ImGui.IsMouseClicked(ImGuiMouseButton.Right);
        var clicked = Underline(text, misspellings, hovered && rightClicked);

        // Only for a click in this box. The preview draws first, so a click latched there got wiped here
        if (rightClicked && hovered)
            (PendingWord, PendingAt) = clicked;
    }

    // The misspelling under the pointer when checkClick, else ("", -1)
    private (string Word, int At) Underline(string text, IReadOnlyList<Misspelling> misspellings, bool checkClick)
    {
        var min = ImGui.GetItemRectMin();
        var size = ImGui.GetItemRectSize();
        var origin = min.X + ImGui.GetStyle().FramePadding.X - ScrollOffset();
        var thick = Thickness * ImGuiHelpers.GlobalScale;

        // Under the text, never past the box. Scaled-up small padding would hang it outside
        var y = Math.Min(
            min.Y + size.Y - ImGui.GetStyle().FramePadding.Y + Drop * ImGuiHelpers.GlobalScale,
            min.Y + size.Y - thick);

        var drawList = ImGui.GetWindowDrawList();
        var colour = ImGui.GetColorU32(Colour);
        var mouseX = ImGui.GetIO().MousePos.X;
        var measured = MeasuredFor(text, misspellings);
        var clickedWord = string.Empty;
        var clickedAt = -1;

        // Clipped to the frame like the glyphs. Insetting by the padding shaves the end letters' marks
        drawList.PushClipRect(min, min + size, true);

        for (var i = 0; i < misspellings.Count; i++)
        {
            var (offset, width) = measured[i];
            if (float.IsNaN(offset))
                continue;

            var left = origin + offset;
            var right = left + width;

            drawList.AddLine(new Vector2(left, y), new Vector2(right, y), colour, thick);

            // Latched on the click, clearing it later empties the menu as it opens
            if (checkClick && mouseX >= left && mouseX <= right)
            {
                clickedWord = misspellings[i].Word(text);

                // The box draws its own text, so this is already the index a correction rewrites
                clickedAt = misspellings[i].Start;
            }
        }

        drawList.PopClipRect();
        return (clickedWord, clickedAt);
    }

    private (IReadOnlyList<Misspelling> For, float Font, List<(float Left, float Width)> At) Measured = ([], 0f, []);

    private List<(float Left, float Width)> MeasuredFor(string text, IReadOnlyList<Misspelling> misspellings)
    {
        var fontSize = ImGui.GetFontSize();
        if (ReferenceEquals(Measured.For, misspellings) && Measured.Font == fontSize)
            return Measured.At;

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
        return at;
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
