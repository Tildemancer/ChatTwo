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
        // Once the input scrolls sideways the on-screen text no longer starts at index 0,
        // and the scroll offset is not exposed. Draw nothing rather than the wrong words, for now.
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

            // Latch it on the click. Turns out clearing it later empties the menu just as it opens.
            if (hovered && rightClicked && mouseX >= left && mouseX <= right)
                clickedWord = misspelling.Word(text);
        }

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
