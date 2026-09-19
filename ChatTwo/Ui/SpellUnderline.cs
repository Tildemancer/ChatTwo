using System.Numerics;
using ChatTwo.Ipc;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace ChatTwo.Ui;

/// <summary>
/// Marks misspelled words with a red underline and offers corrections on them.
///
/// ImGui's text input cannot colour part of its contents, so in the input the
/// marks are drawn over the top, positioned by measuring the text. In the preview
/// the letters are already drawn one at a time with their position in the message
/// known, so each one can simply be underlined where it sits.
/// </summary>
public sealed class SpellUnderline
{
    /// <summary>Bright enough to read at a glance against chat's own colours.</summary>
    private static readonly Vector4 Colour = new(1f, 0.25f, 0.25f, 1f);

    private const float Drop = 1.5f;
    private const float Thickness = 1.5f;

    private readonly Plugin Plugin;

    /// <summary>The word a menu is open for, kept while the popup lives.</summary>
    private string PendingWord = string.Empty;

    public SpellUnderline(Plugin plugin) => Plugin = plugin;

    /// <summary>Draws a line under one item that has just been drawn.</summary>
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
    /// Underlines the misspellings in the input just drawn, and offers corrections
    /// on right-click. Call immediately after the input, while its box is current.
    ///
    /// The marks only hold while the whole text is visible: once the input scrolls
    /// sideways the characters on screen no longer start at the beginning, and the
    /// scroll position is not exposed. Rather than mark the wrong words, nothing is
    /// drawn past that point.
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
        if (ImGui.CalcTextSize(text).X > size.X - inset * 2)
            return;

        var drawList = ImGui.GetWindowDrawList();
        var colour = ImGui.GetColorU32(Colour);
        var y = min.Y + size.Y - ImGui.GetStyle().FramePadding.Y + Drop * ImGuiHelpers.GlobalScale;
        var mouseX = ImGui.GetIO().MousePos.X;

        var rightClicked = ImGui.IsMouseClicked(ImGuiMouseButton.Right);
        var clickedWord = string.Empty;

        foreach (var misspelling in misspellings)
        {
            if (misspelling.Start < 0 || misspelling.Start + misspelling.Length > text.Length)
                continue;

            var left = min.X + inset + ImGui.CalcTextSize(text[..misspelling.Start]).X;
            var right = left + ImGui.CalcTextSize(text.Substring(misspelling.Start, misspelling.Length)).X;

            drawList.AddLine(new Vector2(left, y), new Vector2(right, y), colour, Thickness * ImGuiHelpers.GlobalScale);

            // Latched on the click and left alone afterwards. Clearing it while the
            // pointer is elsewhere would empty the menu the moment it opened, since
            // by then the pointer is over the menu and not the input.
            if (hovered && rightClicked && mouseX >= left && mouseX <= right)
                clickedWord = misspelling.Word(text);
        }

        if (rightClicked)
            PendingWord = clickedWord;
    }

    /// <summary>Sets the word a menu should offer corrections for.</summary>
    public void SetPendingWord(string word) => PendingWord = word;

    /// <summary>
    /// Adds correction entries to a context menu that is already open, for the word
    /// the pointer was last over. Does nothing when that was not a marked word.
    ///
    /// Added to the caller's own menu rather than opening a second one, so a right
    /// click does not have two menus competing for it.
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

    /// <summary>
    /// Swaps the first whole-word occurrence, so correcting one word cannot quietly
    /// alter a longer one that happens to contain it.
    /// </summary>
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
