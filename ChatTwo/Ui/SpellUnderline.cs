// TildeTools: written for this fork, not part of upstream Chat 2.

using System.Numerics;
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

    // Corrections only for a misspelling, any word gets Synonyms and Define
    private bool PendingMisspelled;

    // Null until Synonyms is clicked, and again when it's clicked a second time
    private IReadOnlyList<string>? SynonymsShown;

    // Define's Use, landing on the next frame the input draws, where its text can be written
    private (string Word, int At, string Replacement)? Used;

    private Action<string> UseFor(string word, int at) => replacement => Used = (word, at, replacement);

    public SpellUnderline(Plugin plugin) => Plugin = plugin;

    public static void Underline(float left, float right, float bottom)
    {
        var y = bottom - Drop * ImGuiHelpers.GlobalScale;

        ImGui.GetWindowDrawList().AddLine(
            new Vector2(left, y),
            new Vector2(right, y),
            ImGui.GetColorU32(Colour),
            Thickness * ImGuiHelpers.GlobalScale);
    }

    public (int Start, int End) InputSelection { get; private set; } = (-1, -1);

    private float InputScroll;

    // Must run while the input is current, before the early exits: text with no misspellings still has a selection
    // Scroll off the input's own state, not guessed from the caret, which only pins it at the line's end
    // The state outlives focus, but only an active box draws scrolled or shows its selection
    private unsafe void CaptureInputState()
    {
        var state = ImGuiP.GetInputTextState(ImGuiP.GetItemID());
        if (state.IsNull || !ImGui.IsItemActive())
        {
            (InputSelection, InputScroll) = ((-1, -1), 0f);
            return;
        }

        InputScroll = state.ScrollX;
        var from = state.Stb.SelectStart;
        var to = state.Stb.SelectEnd;

        InputSelection = from == to
            ? (-1, -1)
            : (Math.Min(from, to), Math.Max(from, to));
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

        if (string.IsNullOrEmpty(text) || !Plugin.SpellCheck.IsAvailable)
            return;

        var rightClicked = ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right);
        var misspellings = Plugin.SpellCheck.Check(text);
        var clicked = misspellings.Count > 0 ? UnderlineInput(text, misspellings, rightClicked) : (Word: string.Empty, At: -1);

        // Only for a click in this box. The preview draws first, so a click latched there got wiped here
        if (!rightClicked)
            return;

        var origin = ImGui.GetItemRectMin().X + ImGui.GetStyle().FramePadding.X - InputScroll;

        if (clicked.At >= 0)
            SetPendingWord(clicked.Word, clicked.At);
        else if (Plugin.SpellCheck.WordAt(text, IndexAt(text, ImGui.GetIO().MousePos.X - origin)) is var (start, length))
            (PendingWord, PendingAt, PendingMisspelled, SynonymsShown) = (text.Substring(start, length), start, false, null);
        else
            PendingWord = string.Empty;
    }

    // The character under x, -1 left of the text: the longest prefix no wider than x ends just before it
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

    // The misspelling under the pointer when checkClick, else ("", -1)
    private (string Word, int At) UnderlineInput(string text, IReadOnlyList<Misspelling> misspellings, bool checkClick)
    {
        var min = ImGui.GetItemRectMin();
        var size = ImGui.GetItemRectSize();
        var origin = min.X + ImGui.GetStyle().FramePadding.X - InputScroll;
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

        // Cut to the frame like the glyphs, by hand rather than a clip rect pushed
        // Insetting by the padding shaves the end letters' marks
        for (var i = 0; i < misspellings.Count; i++)
        {
            var (offset, width) = measured[i];
            if (float.IsNaN(offset))
                continue;

            var left = Math.Max(origin + offset, min.X);
            var right = Math.Min(origin + offset + width, min.X + size.X);
            if (right <= left)
                continue;

            drawList.AddLine(new Vector2(left, y), new Vector2(right, y), colour, thick);

            // Latched on the click, clearing it later empties the menu as it opens
            if (checkClick && mouseX >= left && mouseX <= right)
            {
                clickedWord = misspellings[i].Word(text);

                // The box draws its own text, so this is already the index a correction rewrites
                clickedAt = misspellings[i].Start;
            }
        }

        return (clickedWord, clickedAt);
    }

    private (IReadOnlyList<Misspelling> For, float Font, List<(float Left, float Width)> At) Measured = ([], 0f, []);

    private List<(float Left, float Width)> MeasuredFor(string text, IReadOnlyList<Misspelling> misspellings)
    {
        var fontSize = ImGui.GetFontSize();
        if (ReferenceEquals(Measured.For, misspellings) && Measured.Font == fontSize)
            return Measured.At;

        // One pass gap by gap, not a whole prefix per misspelling: 16 ms a keystroke at 840 of them
        // Unrounded, so the running total can't drift a pixel per word the way CalcTextSize's rounding did
        var font = ImGui.GetFont();
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

        Measured = (misspellings, fontSize, at);
        return at;
    }

    // A misspelling. at is -1 when unknown, falling back to first-match. A wrong index rewrites a word nobody clicked
    public void SetPendingWord(string word, int at = -1)
    {
        (PendingWord, PendingAt, PendingMisspelled, SynonymsShown) = (word, at, true, null);
    }

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
            // Its own ids, a synonym can share a label with a correction
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
            foreach (var suggestion in suggestions.Take(8))
                if (ImGui.Selectable(suggestion))
                    text = ReplaceWord(text, PendingWord, suggestion, PendingAt);

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
