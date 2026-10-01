// TildeTools: written for this fork, not part of upstream Chat 2.

using System.Numerics;
using System.Text;
using ChatTwo.Ipc;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace ChatTwo.Ui;

public sealed class SpellUnderline(Plugin plugin)
{
    private static readonly Vector4 Colour = new(1f, 0.25f, 0.25f, 1f);

    private const float Drop = 1.5f;
    private const float Thickness = 1.5f;

    private readonly Plugin Plugin = plugin;

    private string PendingWord = string.Empty;

    private bool PendingMisspelled;

    // Set when the menu opens, so Use isn't allocated every frame.
    // TT keeps synonyms and corrections per MenuId.
    private string MenuId = string.Empty;
    private int MenusOpened;
    private Action<string> Use = _ => { };

    // Held for the next frame's input, the menu and Define window call Use elsewhere.
    private (string Word, int At, string Replacement)? Used;

    public static void Underline(float left, float right, float bottom)
    {
        var y = bottom - Drop * ImGuiHelpers.GlobalScale;
        ImGui.GetWindowDrawList().AddLine(new Vector2(left, y), new Vector2(right, y), ImGui.GetColorU32(Colour), Thickness * ImGuiHelpers.GlobalScale);
    }

    public (int Start, int End) InputSelection { get; private set; } = (-1, -1);

    private float InputOrigin;

    // Scroll comes from ImGui's input state, since the caret only pins it at the line's end.
    // Its selection is in characters like the text, the callback's positions are bytes.
    // Only the focused box scrolls or selects, even though the state outlives focus.
    private unsafe void CaptureInputState()
    {
        InputOrigin = ImGui.GetItemRectMin().X + ImGui.GetStyle().FramePadding.X;

        var state = ImGuiP.GetInputTextState(ImGuiP.GetItemID());
        if (state.IsNull || !ImGui.IsItemActive())
        {
            InputSelection = (-1, -1);
            return;
        }

        var (from, to) = (state.Stb.SelectStart, state.Stb.SelectEnd);
        (InputSelection, InputOrigin) = (from == to ? (-1, -1) : (Math.Min(from, to), Math.Max(from, to)), InputOrigin - state.ScrollX);
    }

    // Call right after the input, while it's still the current item.
    // Returns the caret's byte position after a correction, alternatively -1.
    public int DrawForInput(ref string text)
    {
        CaptureInputState();

        var caret = -1;
        if (Used is var (word, at, replacement))
        {
            (text, var end) = ReplaceWord(text, word, replacement, at);
            caret = end < 0 ? -1 : Encoding.UTF8.GetByteCount(text.AsSpan(0, end));
            Used = null;
        }

        var misspellings = Plugin.SpellCheck.Check(text);
        if (misspellings.Count > 0)
            UnderlineInput(text, misspellings);

        // AllowWhenBlockedByPopup, because the menu's still probably open when you right-click another word.
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByPopup) && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            PendAt(text, IndexAt(text, ImGui.GetIO().MousePos.X - InputOrigin), misspellings);

        return caret;
    }

    // What the menu offers for a right-click at a box position, from the box or the preview.
    // False when there's no word there.
    internal bool PendAt(string text, int index, IReadOnlyList<Misspelling> misspellings)
    {
        var (from, to) = Selected(text);

        // A selection goes whole, that way a name or phrase is also looked up whole.
        if (index >= from && index < to)
            SetPendingWord(text[from..to], from, misspellings.Any(m => m.Start == from && m.Length == to - from));
        else if (misspellings.FirstOrDefault(m => index >= m.Start && index < m.Start + m.Length) is { Length: > 0 } hit)
            SetPendingWord(hit.Word(text), hit.Start);
        else if (Plugin.SpellCheck.WordAt(text, index) is var (start, length))
            SetPendingWord(text.Substring(start, length), start, misspelled: false);
        else
            PendingWord = string.Empty;

        return PendingWord.Length > 0;
    }

    // Trimmed to its letters like a word, so edge spaces don't count.
    private (int From, int To) Selected(string text)
    {
        if (InputSelection.Start < 0)
            return (-1, -1);

        var (from, to) = (Math.Min(InputSelection.Start, text.Length), Math.Min(InputSelection.End, text.Length));

        while (from < to && !char.IsLetterOrDigit(text[from]))
            from++;

        while (to > from && !char.IsLetterOrDigit(text[to - 1]))
            to--;

        return from < to ? (from, to) : (-1, -1);
    }

    internal static int IndexAt(string text, float x)
    {
        if (x < 0)
            return -1;

        var (font, size) = (ImGui.GetFont(), ImGui.GetFontSize());
        var (low, high) = (0, text.Length);

        while (low < high)
        {
            var mid = (low + high + 1) / 2;

            if (ImGui.CalcTextSizeA(font, size, float.MaxValue, 0f, text.AsSpan(0, mid), out _).X <= x)
                low = mid;
            else
                high = mid - 1;
        }

        return low;
    }

    private void UnderlineInput(string text, IReadOnlyList<Misspelling> misspellings)
    {
        var min = ImGui.GetItemRectMin();
        var size = ImGui.GetItemRectSize();
        var thick = Thickness * ImGuiHelpers.GlobalScale;

        // Under the text, clamped inside the box in case the padding's scaled up.
        var y = Math.Min(
            min.Y + size.Y - ImGui.GetStyle().FramePadding.Y + Drop * ImGuiHelpers.GlobalScale,
            min.Y + size.Y - thick);

        var drawList = ImGui.GetWindowDrawList();
        var colour = ImGui.GetColorU32(Colour);
        var measured = MeasuredFor(text, misspellings);

        // Clipped to the frame like the text, since clipping at the padding cuts off edge marks.
        for (var i = 0; i < misspellings.Count; i++)
        {
            var (offset, width) = measured[i];
            if (float.IsNaN(offset))
                continue;

            var left = Math.Max(InputOrigin + offset, min.X);
            var right = Math.Min(InputOrigin + offset + width, min.X + size.X);
            if (right <= left)
                continue;

            drawList.AddLine(new Vector2(left, y), new Vector2(right, y), colour, thick);
        }
    }

    // Face is in the key since Plugin.Draw can switch fonts at the same size.
    private (IReadOnlyList<Misspelling> For, ImFontPtr Face, float Size, List<(float Left, float Width)> At) Measured = ([], default, 0f, []);

    private List<(float Left, float Width)> MeasuredFor(string text, IReadOnlyList<Misspelling> misspellings)
    {
        var (font, fontSize) = (ImGui.GetFont(), ImGui.GetFontSize());
        if (ReferenceEquals(Measured.For, misspellings) && Measured.Face == font && Measured.Size == fontSize)
            return Measured.At;

        // Gap by gap in one pass, since a prefix per misspelling cost ~16 ms at ~800m isspellings.
        // CalcTextSizeA doesn't round, so the gaps don't drift like CalcTextSize's would.
        float Width(ReadOnlySpan<char> span) => ImGui.CalcTextSizeA(font, fontSize, float.MaxValue, 0f, span, out _).X;

        List<(float Left, float Width)> at = new(misspellings.Count);
        var x = 0f;
        var measuredTo = 0;

        foreach (var misspelling in misspellings)
        {
            var start = misspelling.Start;
            if (start < 0 || start + misspelling.Length > text.Length)
            {
                at.Add((float.NaN, 0f));
                continue;
            }

            // Out of order, so measuring starts over from the start of the text.
            if (start < measuredTo)
                (x, measuredTo) = (0f, 0);

            x += Width(text.AsSpan(measuredTo, start - measuredTo));
            var width = Width(text.AsSpan(start, misspelling.Length));
            at.Add((x, width));

            x += width;
            measuredTo = start + misspelling.Length;
        }

        Measured = (misspellings, font, fontSize, at);
        return at;
    }

    // at is the word's position, so identical typos can be told apart.
    // -1 if unknown.
    private void SetPendingWord(string word, int at, bool misspelled = true) =>
        (PendingWord, PendingMisspelled, MenuId, Use) = (word, misspelled, $"Chat 2 {++MenusOpened}", replacement => Used = (word, at, replacement));

    // TT draws the entries into this popup. See SpellMenu
    public void DrawContextEntries()
    {
        if (PendingWord.Length == 0)
            return;

        if (Plugin.SpellCheck.DrawMenu(MenuId, PendingWord, PendingMisspelled, Use))
            PendingWord = string.Empty;

        ImGui.Separator();
    }

    // The caret goes past the space after the word.
    // A space is added at the end because a last word counts as unfinished until one follows.
    private static (string Text, int Caret) ReplaceWord(string text, string word, string replacement, int at)
    {
        if (at < 0 || at + word.Length > text.Length || string.CompareOrdinal(text, at, word, 0, word.Length) != 0)
            for (at = text.IndexOf(word, StringComparison.Ordinal); at >= 0; at = text.IndexOf(word, at + 1, StringComparison.Ordinal))
                if ((at == 0 || !char.IsLetterOrDigit(text[at - 1])) && (at + word.Length >= text.Length || !char.IsLetterOrDigit(text[at + word.Length])))
                    break;

        if (at < 0)
            return (text, -1);

        var end = at + replacement.Length;
        text = text[..at] + replacement + text[(at + word.Length)..] + (at + word.Length == text.Length ? " " : "");
        return (text, text[end] == ' ' ? end + 1 : end);
    }
}
