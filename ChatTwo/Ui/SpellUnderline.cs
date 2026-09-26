// TildeTools: written for this fork, not part of upstream Chat 2.

using System.Numerics;
using System.Text;
using ChatTwo.Ipc;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

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

    private bool PendingMisspelled;

    private IReadOnlyList<string>? SynonymsShown;

    private (string Word, int At, string Replacement)? Used;

    private Action<string> UseFor(string word, int at) => replacement => Used = (word, at, replacement);

    public SpellUnderline(Plugin plugin) => Plugin = plugin;

    public static void Underline(float left, float right, float bottom)
    {
        var y = bottom - Drop * ImGuiHelpers.GlobalScale;
        ImGui.GetWindowDrawList().AddLine(new Vector2(left, y), new Vector2(right, y), ImGui.GetColorU32(Colour), Thickness * ImGuiHelpers.GlobalScale);
    }

    public (int Start, int End) InputSelection { get; private set; } = (-1, -1);

    private float InputOrigin;

    // Scroll off the input's own state, not guessed from the caret, which only pins it at the line's end
    // The state outlives focus, but only an active box draws scrolled or shows its selection
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

    // Call immediately after the input, while it's the current item
    public void DrawForInput(ref string text)
    {
        CaptureInputState();

        if (Used is var (word, at, replacement))
        {
            text = ReplaceWord(text, word, replacement, at);
            Used = null;
        }

        var misspellings = Plugin.SpellCheck.Check(text);
        if (misspellings.Count > 0)
            UnderlineInput(text, misspellings);

        // AllowWhenBlockedByPopup: the menu, still open on the press, reopens on the release with whatever this latched
        // A block, not an early return: the lambda's capture of index would allocate every frame
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByPopup) && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            var index = IndexAt(text, ImGui.GetIO().MousePos.X - InputOrigin);
            var (from, to) = Selected(text);

            // A highlighted stretch goes whole, a name or a phrase
            if (index >= from && index < to)
                SetPendingWord(text[from..to], from, misspellings.Any(m => m.Start == from && m.Length == to - from));
            else if (misspellings.FirstOrDefault(m => index >= m.Start && index < m.Start + m.Length) is { Length: > 0 } hit)
                SetPendingWord(hit.Word(text), hit.Start);
            else if (Plugin.SpellCheck.WordAt(text, index) is var (start, length))
                SetPendingWord(text.Substring(start, length), start, misspelled: false);
            else
                PendingWord = string.Empty;
        }
    }

    // In chars, as the text is, where the box counts bytes
    // Trimmed to its letters, like a word
    private (int From, int To) Selected(string text)
    {
        if (InputSelection.Start < 0)
            return (-1, -1);

        var bytes = Encoding.UTF8.GetBytes(text);
        var (from, to) = (Encoding.UTF8.GetCharCount(bytes, 0, Math.Min(InputSelection.Start, bytes.Length)), Encoding.UTF8.GetCharCount(bytes, 0, Math.Min(InputSelection.End, bytes.Length)));

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

        // Under the text, never past the box. Scaled-up small padding would hang it outside
        var y = Math.Min(
            min.Y + size.Y - ImGui.GetStyle().FramePadding.Y + Drop * ImGuiHelpers.GlobalScale,
            min.Y + size.Y - thick);

        var drawList = ImGui.GetWindowDrawList();
        var colour = ImGui.GetColorU32(Colour);
        var measured = MeasuredFor(text, misspellings);

        // Cut to the frame like the glyphs
        // Insetting by the padding shaves the end letters' marks
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

    // Face too: Plugin.Draw's font can change at the same size
    private (IReadOnlyList<Misspelling> For, ImFontPtr Face, float Size, List<(float Left, float Width)> At) Measured = ([], default, 0f, []);

    private List<(float Left, float Width)> MeasuredFor(string text, IReadOnlyList<Misspelling> misspellings)
    {
        var (font, fontSize) = (ImGui.GetFont(), ImGui.GetFontSize());
        if (ReferenceEquals(Measured.For, misspellings) && Measured.Face == font && Measured.Size == fontSize)
            return Measured.At;

        // One pass gap by gap, not a whole prefix per misspelling: 16 ms a keystroke at 840 of them
        // Unrounded, so the running total can't drift a pixel per word the way CalcTextSize's rounding did
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

            // Out of order, so the prefix is measured again from the start
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

    // at is -1 when unknown, never a guess
    public void SetPendingWord(string word, int at, bool misspelled = true) => (PendingWord, PendingAt, PendingMisspelled, SynonymsShown) = (word, at, misspelled, null);

    public void DrawContextEntries(ref string text)
    {
        if (PendingWord.Length == 0)
            return;

        KeepOnScreen();
        ImGui.TextDisabled(PendingWord);

        if (ImGui.Selectable("Synonyms", false, ImGuiSelectableFlags.DontClosePopups))
            SynonymsShown = SynonymsShown is null ? Plugin.SpellCheck.Synonyms(PendingWord) : null;

        if (SynonymsShown is not null)
        {
            // A synonym can share a label with a correction
            using var id = ImRaii.PushId("synonyms");
            using var indent = ImRaii.PushIndent();

            if (SynonymsShown.Count == 0)
                ImGui.TextDisabled("None found");

            foreach (var synonym in SynonymsShown)
                if (ImGui.Selectable(synonym))
                    Plugin.SpellCheck.Define(synonym, PendingWord, UseFor(PendingWord, PendingAt));
        }

        if (ImGui.Selectable("Define"))
            Plugin.SpellCheck.Define(PendingWord, PendingWord, UseFor(PendingWord, PendingAt));

        ImGui.Separator();

        if (!PendingMisspelled)
            return;

        var learn = ImGui.Selectable("Add to dictionary");
        var ignore = ImGui.Selectable("Ignore for now");

        // Asking for suggestions now starts a ~100 ms lookup that the next frame's check waits on
        if (learn || ignore)
        {
            if (learn)
                Plugin.SpellCheck.AddToDictionary(PendingWord);
            else
                Plugin.SpellCheck.Ignore(PendingWord);

            PendingWord = string.Empty;
            return;
        }

        var suggestions = Plugin.SpellCheck.Suggest(PendingWord);
        if (suggestions is null || suggestions.Count > 0)
            ImGui.Separator();

        if (suggestions is null)
            ImGui.TextDisabled("Looking for corrections...");
        else
            for (var i = 0; i < suggestions.Count; i++)
                if (ImGui.Selectable(suggestions[i]))
                    text = ReplaceWord(text, PendingWord, suggestions[i], PendingAt);

        ImGui.Separator();
    }

    // Suggestions come in after it opens, and ImGui only fits a popup to the screen on the frame it opens
    private static void KeepOnScreen()
    {
        var screen = ImGui.GetWindowViewport();
        var at = ImGui.GetWindowPos();
        var fit = Vector2.Clamp(at, screen.WorkPos, Vector2.Max(screen.WorkPos, screen.WorkPos + screen.WorkSize - ImGui.GetWindowSize()));

        if (fit != at)
            ImGui.SetWindowPos(fit);
    }

    private static string ReplaceWord(string text, string word, string replacement, int at)
    {
        if (at < 0 || at + word.Length > text.Length || string.CompareOrdinal(text, at, word, 0, word.Length) != 0)
            for (at = text.IndexOf(word, StringComparison.Ordinal); at >= 0; at = text.IndexOf(word, at + 1, StringComparison.Ordinal))
                if ((at == 0 || !char.IsLetterOrDigit(text[at - 1])) && (at + word.Length >= text.Length || !char.IsLetterOrDigit(text[at + word.Length])))
                    break;

        return at < 0 ? text : text[..at] + replacement + text[(at + word.Length)..];
    }
}
