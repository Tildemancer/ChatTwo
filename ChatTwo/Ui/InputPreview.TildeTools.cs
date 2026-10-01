// TildeTools: written for this fork, not part of upstream Chat 2.

using System.Numerics;
using System.Text;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ChatTwo.Code;
using ChatTwo.Resources;
using ChatTwo.Ui.Handler;
using ChatTwo.Util;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Bindings.ImGui;

namespace ChatTwo.Ui;

public partial class InputPreview
{
    // TildeTools: Moved from C2's InputPreview.cs and reworked because split parts are parsed here too.
    private static Message BuildMessage(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        AutoTranslate.ReplaceWithPayload(ref bytes);

        var chunks = ChunkUtil.ToChunks(SeString.Parse(bytes), ChunkSource.Content, ChatType.Say).ToList();
        var message = Message.FakeMessage(chunks, new ChatCode(XivChatType.Say, 0, 0));
        message.DecodeTextParam();

        return message;
    }

    private Dictionary<string, List<Chunk>> ParsedBodies = [];
    private (bool Show, EmoteCache.LoadingState Loaded, int Blocked) ParsedWithEmotes;
    private bool SplitHasEvaluation;

    // A body with <item>, <flag> or <status> in it is always parsed fresh.
    // C2 swaps those for the item or status linked in chat and the map flag and any of them can change while the text stays the same.
    // see Message.DecodeTextParam
    private static readonly string[] LiveParams = ["<item>", "<flag>", "<status>"];

    // Only the text the player typed gets parsed. Emote Splitter's command/tags/markers go in as plain text.
    // Parsing costs ~0.3 ms per 500 characters, so a body that didn't change reuses its parse from the last update! Blessings of performance upon we...
    private Message BuildPart(string part, (int Start, int Length)? span, Dictionary<string, List<Chunk>> kept)
    {
        var (start, end) = BodyRange(span, part.Length);

        // The space between the body and the suffix stays with the body, so the last word keeps its trailing space.
        if (end < part.Length && part[end] == ' ')
            end++;

        var text = part[start..end];
        if (!ParsedBodies.TryGetValue(text, out var parsed))
            ParsedBodies[text] = parsed = (LiveParams.Any(text.Contains) ? null : kept.GetValueOrDefault(text)) ?? BuildMessage(text).Content;

        // Built with the constructor which takes the chunks as they are. FakeMessage's constructor would run CheckMessageContent over the whole part again.
        List<Chunk> content = [.. Plain(part[..start]), .. parsed, .. Plain(part[end..])];
        return new Message(Guid.NewGuid(), 0, 0, DateTimeOffset.UtcNow, new ChatCode(XivChatType.Say, 0, 0),
            [], content, new SeString(), new SeString(), Guid.Empty);

        static IEnumerable<Chunk> Plain(string affix) =>
            ChunkUtil.ToChunks(new SeString(new TextPayload(affix)), ChunkSource.Content, ChatType.Say);
    }

    private static (int Start, int End) BodyRange((int Start, int Length)? span, int length) =>
        span is { Start: >= 0 } s && s.Start + s.Length <= length ? (s.Start, s.Start + s.Length) : (0, length);

    private List<string>? SplitParts;
    private List<Message>? SplitMessages;
    private List<(int Start, int Length)> SplitBodies = [];
    private (string Line, int Generation) SplitFor = ("", -1);

    private string SplitHeader = "";

    private void UpdateSplitParts()
    {
        var line = InputHandler.ComposedLine;
        if ((line, InputHandler.Plugin.Splitter.Generation) == SplitFor)
            return;

        SplitFor = (line, InputHandler.Plugin.Splitter.Generation);
        SplitParts = null;
        SplitMessages = null;
        SplitHasEvaluation = false;

        if (!InputHandler.Plugin.Splitter.IsAvailable || line.Length == 0)
            return;

        var parts = InputHandler.Plugin.Splitter.Split(line);
        if (parts is not { Count: > 1 })
            return;

        SplitBodies = InputHandler.Plugin.Splitter.BodySpans(line);
        var seconds = MathF.Round(InputHandler.Plugin.Splitter.PostingMs(line) / 1000f, 1);
        SplitHeader = seconds >= 1f ? $"Will be sent as {parts.Count} parts, over about {seconds:0.#} second{(seconds == 1f ? "" : "s")}:" : $"Will be sent as {parts.Count} parts:";

        // Parses from the last update are reused unless the emote settings changed/
        // They're keyed by body, not by part, because a new part changes the count in every part's marker ("#m") while the bodies stay the same.
        // BlockedEmotes is hashed by content, not by Count, since the settings window edits that list in place.
        var emotes = (Plugin.Config.ShowEmotes, EmoteCache.State, Plugin.Config.BlockedEmotes.Aggregate(0, (hash, emote) => hash ^ emote.GetHashCode()));
        var kept = ParsedWithEmotes == emotes ? ParsedBodies : [];
        ParsedBodies = [];
        ParsedWithEmotes = emotes;

        SplitParts = parts;
        SplitMessages = [.. parts.Select((part, i) => BuildPart(part, i < SplitBodies.Count ? SplitBodies[i] : null, kept))];

        // For OnlyPreviewIf, checks bodies, since a part's affixes always make it more than one chunk.
        SplitHasEvaluation = ParsedBodies.Values.Any(body => body.Count > 1);

        SplitSources = InputHandler.Plugin.Splitter.BodySources(line);
    }

    private List<int> SplitSources = [];

    private int SplitIndex = -1;

    // In chat box positions, not the preview's. -1 when not dragging.
    private int DragAnchor = -1;

    private int DragHead = -1;

    // In the chat box's bytes, InputHandler.Callback selects in bytes.
    public (int Start, int End)? SelectedRange;

    private int ByteIndex(int position) =>
        Encoding.UTF8.GetByteCount(InputHandler.ChatInput.AsSpan(0, Math.Clamp(position, 0, InputHandler.ChatInput.Length)));

    // Both in chat box positions.
    // The drag counts too, the box's selection lags a frame behind.
    private bool InSelection(int caret)
    {
        var (from, to) = InputHandler.Spelling.InputSelection;
        return caret >= 0 && (Covers(Math.Min(DragAnchor, DragHead), Math.Max(DragAnchor, DragHead)) || Covers(from, to));

        bool Covers(int low, int high) => low >= 0 && caret - 1 >= low && caret <= high;
    }

    // The caret goes after the letter. -1 for a splitter marker, which does nothing.
    private int CaretTargetFor(int afterLetter) => SourceIndexOf(afterLetter - 1) is >= 0 and var mapped ? mapped + 1 : -1;

    // Part to body to composed line to box, -1 for anything not typed, like the aforementioned marker.
    private int SourceIndexOf(int positionInPart)
    {
        var (leading, prefix) = MapBasis();

        // Unsplit, the preview is the box's text minus its leading spaces, so add them back.
        var index = positionInPart + leading;

        if (SplitIndex >= 0)
        {
            if (SplitIndex >= SplitSources.Count || SplitIndex >= SplitBodies.Count)
                return -1;

            var (start, length) = SplitBodies[SplitIndex];
            var offset = positionInPart - start;
            if (offset < 0 || offset >= length)
                return -1;

            index = SplitSources[SplitIndex] + offset - prefix + leading;
        }

        return index >= 0 && index < InputHandler.ChatInput.Length ? index : -1;
    }

    // SourceIndexOf runs for every letter while selecting so it's cached per text.
    private (string Typed, string Composed, int Leading, int Prefix) Basis = ("", "", 0, 0);

    private (int Leading, int Prefix) MapBasis()
    {
        var typed = InputHandler.ChatInput;
        var composed = InputHandler.ComposedLine;
        // ComposeLine only adds the channel command in front, so the length difference is the prefix.
        if (!ReferenceEquals(typed, Basis.Typed) || !ReferenceEquals(composed, Basis.Composed))
            Basis = (typed, composed, typed.Length - typed.TrimStart().Length, composed.Length - typed.Trim().Length);

        return (Basis.Leading, Basis.Prefix);
    }

    // Measured against the game's viewport.
    // Multi-monitor setups move it off (0, 0).
    // A chat window on another monitor keeps upstream's placement? Untested.
    private Vector2 KeepOnScreen(float y, Vector2 windowPos, float windowWidth, float previewWidth)
    {
        var main = ImGuiHelpers.MainViewport;
        var (low, high) = (main.Pos, main.Pos + main.Size);

        if (Vector2.Clamp(windowPos, low, high) != windowPos || y >= low.Y && y + PreviewHeight <= high.Y)
            return windowPos with { Y = y };

        var right = windowPos.X + windowWidth;
        var x = right + previewWidth <= high.X || windowPos.X - low.X < previewWidth ? right : windowPos.X - previewWidth;
        return Vector2.Clamp(new Vector2(x, windowPos.Y), low, Vector2.Max(low, high - new Vector2(previewWidth, PreviewHeight)));
    }

    private float ColumnWidth;

    private float PreviewWidth;

    private readonly List<List<int>> Columns = [];

    // Measuring is a full invisible draw, so it's only redone when this key changes.
    // Face and UI scale change the layout at the same font size, so they're in it.
    // Other style edits and late emote failures aren't, the next keystroke catches those.
    private (Message? Preview, List<Message>? Parts, float Window, bool WindowMode, float Screen, ImFontPtr Face, float Font, float Scale, bool Emotes)? MeasuredFor;

    // TildeTools: Moved from C2's InputPreview.cs and reworked because split parts are measured here too. Part 2
    // We Pre-draw this once to get the actual height :HideThePain:
    public void CalculatePreview()
    {
        // Tooltips size themselves, nothing to measure.
        if (Plugin.Config.PreviewPosition is PreviewPosition.Tooltip)
            return;

        var key = (PreviewMessage, SplitMessages, InputHandler.MainWindow.LastWindowSize.X,
            IsWindowMode, ImGui.GetIO().DisplaySize.Y, ImGui.GetFont(), ImGui.GetFontSize(), ImGuiHelpers.GlobalScale, Plugin.Config.ShowEmotes);
        if (MeasuredFor == key)
            return;

        MeasuredFor = key;

        var sidePadding = ImGui.GetStyle().WindowPadding.X * 2;
        ColumnWidth = Math.Max(120f, InputHandler.MainWindow.LastWindowSize.X - sidePadding);
        PreviewWidth = ColumnWidth + sidePadding;
        Columns.Clear();

        var padding = IsWindowMode ? ImGui.GetStyle().WindowPadding.Y * 2 : 0;

        if (SplitMessages is null)
            PreviewHeight = MeasureColumn(() =>
            {
                ImGui.TextUnformatted(Language.Options_Preview_Header);
                DrawChunksPreview(PreviewMessage!.Content);
            }) + padding;
        else
            PackColumns(padding);
    }

    private void PackColumns(float padding)
    {
        var available = Math.Max(100f, ImGui.GetIO().DisplaySize.Y - padding);

        float header = 0;
        List<float> heights = [];

        MeasureColumn(() =>
        {
            var mark = ImGui.GetCursorPosY();
            ImGui.TextDisabled(SplitHeader);
            header = ImGui.GetCursorPosY() - mark;

            for (var i = 0; i < SplitMessages!.Count; i++)
            {
                mark = ImGui.GetCursorPosY();
                DrawSplitPart(i, null);
                heights.Add(ImGui.GetCursorPosY() - mark);
            }
        });

        // Nothing was measured (child is hidden), so it's one full-height column. PreviewWidth is already one wide.
        if (heights.Count < SplitMessages!.Count)
        {
            Columns.Add([.. Enumerable.Range(0, SplitMessages.Count)]);
            PreviewHeight = available + padding;
            return;
        }

        List<int> current = [];
        Columns.Add(current);
        var (used, tallest) = (header, header);

        for (var i = 0; i < heights.Count; i++)
        {
            // An oversized part still gets its own column without an empty one before it.
            if (current.Count > 0 && used + heights[i] > available)
            {
                Columns.Add(current = []);
                used = 0;
            }

            current.Add(i);
            used += heights[i];
            tallest = Math.Max(tallest, used);
        }

        PreviewHeight = Math.Min(tallest, available) + padding;
        PreviewWidth += ColumnWidth * (Columns.Count - 1);
    }

    private float MeasureColumn(Action draw)
    {
        var restore = ImGui.GetCursorPos();
        var height = 0f;

        // At the cursor, since ImGui culls a child outside its parent and it'd measure zero.
        using (var child = ImRaii.Child("##preview-measure", new Vector2(ColumnWidth, 1f), false,
                   ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoInputs))
        {
            if (child)
            {
                using var style = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero);

                var before = ImGui.GetCursorPosY();
                draw();
                height = ImGui.GetCursorPosY() - before;
            }
        }

        ImGui.SetCursorPos(restore);
        return height;
    }

    private int LastDrawFrame = -1;

    // TildeTools: Moved from C2's InputPreview.cs and reworked because split parts draw here too, part 3
    public void DrawPreview()
    {
        FinishDrag();

        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero))
        {
            if (SplitMessages is null)
            {
                ImGui.TextUnformatted(Language.Options_Preview_Header);
                DrawChunksPreview(PreviewMessage!.Content, InputHandler.PayloadHandler);
            }
            // Inside and Tooltip draw the parts in one run, so Inside's packed Columns go unused.
            else if (!IsWindowMode)
            {
                ImGui.TextDisabled(SplitHeader);

                for (var i = 0; i < SplitMessages.Count; i++)
                    DrawSplitPart(i, InputHandler.PayloadHandler);
            }
            else
            {
                var height = ImGui.GetContentRegionAvail().Y;

                for (var c = 0; c < Columns.Count; c++)
                {
                    if (c > 0)
                        ImGui.SameLine(0, 0);

                    using var child = ImRaii.Child($"##preview-col{c}", new Vector2(ColumnWidth, height), false,
                        ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);

                    if (!child)
                        continue;

                    if (c == 0)
                        ImGui.TextDisabled(SplitHeader);

                    foreach (var part in Columns[c])
                        DrawSplitPart(part, InputHandler.PayloadHandler);
                }
            }
        }

        DrawSpellingPopup();
    }

    private void FinishDrag()
    {
        // Missed a frame (hidden in combat), that way a drag in progress is dropped and not applied.
        var frame = ImGui.GetFrameCount();
        if (LastDrawFrame < frame - 1)
            DragAnchor = DragHead = -1;

        LastDrawFrame = frame;

        // Ends here, not in PointAt, since the button can be released anywhere.
        // Handed to the box only on release. Doing it every frame steals focus.
        if (DragAnchor >= 0 && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            if (DragHead >= 0 && DragHead != DragAnchor)
            {
                // Anchor then head, so the caret lands where the drag ended, even backwards.
                SelectedRange = (ByteIndex(DragAnchor), ByteIndex(DragHead));
                InputHandler.FocusedPreview = true;
            }

            DragAnchor = DragHead = -1;
        }
    }

    // Marked once per text, not per letter, since each letter would otherwise be an IPC call.
    private bool[] SpellMarks = [];

    private readonly Dictionary<(string Text, bool Complete, (int Start, int Length)? Body), bool[]> MarksFor = [];

    private int MarksGeneration;

    // complete: finished typing, so the last word gets marked.
    // Passed in since trimming lost the trailing space.
    private void SetSpellSource(string text, bool complete, (int Start, int Length)? body = null)
    {
        // Cleared when a word is added or ignored, since the answers change for the same text.
        var generation = InputHandler.Plugin.SpellCheck.Generation;
        if (generation != MarksGeneration)
        {
            MarksFor.Clear();
            MarksGeneration = generation;
        }

        var key = (text, complete, body);
        if (MarksFor.TryGetValue(key, out var remembered))
        {
            SpellMarks = remembered;
            return;
        }

        if (MarksFor.Count >= Ipc.SpellCheck.MostToRemember)
            MarksFor.Clear();

        SpellMarks = new bool[text.Length];
        MarksFor[key] = SpellMarks;

        // Body only, or the splitter markers would show as misspellings.
        var (from, to) = body is { Length: > 0 } ? BodyRange(body, text.Length) : (0, text.Length);

        // Finished text gets a trailing space for the checker only, so its last word also counts.
        // Unfinished text stops at the body, since the splitter's "]" or OOC bracket would look finished.
        var forCheck = complete
            ? text.Length > 0 ? text + " " : text
            : text[..to];

        foreach (var misspelling in InputHandler.Plugin.SpellCheck.Check(forCheck))
        {
            var end = Math.Min(to, misspelling.Start + misspelling.Length);
            for (var i = Math.Max(from, misspelling.Start); i < end; i++)
                SpellMarks[i] = true;
        }
    }

    // The checker's rule. Reads what was typed, so the closing OOC bracket still counts even if the splitter moved it.
    private static bool FinishedTyping(string typed) =>
        typed is [.., var last] && (char.IsWhiteSpace(last) || char.IsPunctuation(last) && last is not ('\'' or '-'));

    private bool MisspelledAt(int position) =>
        position >= 0 && position < SpellMarks.Length && SpellMarks[position];

    // Opened a frame late since EndFrame closes popups on a right-click over no item.
    private int SpellingClickFrame = int.MinValue;

    private void DrawSpellingPopup()
    {
        // Opened here so the id matches the BeginPopup below.
        if (ImGui.GetFrameCount() == SpellingClickFrame + 1)
            ImGui.OpenPopup("##preview-spelling");

        using var popup = ImRaii.Popup("##preview-spelling");
        if (!popup.Success)
            return;

        InputHandler.Spelling.DrawContextEntries();
    }

    private void DrawSplitPart(int index, PayloadHandler? handler)
    {
        using var id = ImRaii.PushId(index);

        ImGui.TextDisabled($"{index + 1}.");
        ImGui.SameLine();

        using var indent = ImRaii.PushIndent();

        SplitIndex = index;

        try
        {
            // Only the last part can still be mid-word, so earlier parts count as done.
            SetSpellSource(SplitParts![index], complete: index != SplitParts.Count - 1 || FinishedTyping(InputHandler.ChatInput),
                body: index < SplitBodies.Count ? SplitBodies[index] : null);

            DrawChunksPreview(SplitMessages![index].Content, handler);
        }
        finally
        {
            SplitIndex = -1;
        }
    }

    // One widget per word. Per letter cost ~3 ms a frame at 11k letters! Blessings of performance be upon we.
    private void DrawWords(string content, PayloadHandler? handler)
    {
        var selecting = DragAnchor >= 0 || InputHandler.Spelling.InputSelection.Start >= 0;
        var (drawList, textColour) = (ImGui.GetWindowDrawList(), ImGui.GetColorU32(ImGuiCol.Text));

        foreach (var word in WordsOf(content))
        {
            var wordSize = ImGui.CalcTextSize(word);

            // Only the word has to fit, its trailing spaces can hang over the edge.
            var room = ImGui.GetContentRegionAvail().X;
            if (room < wordSize.X && room < ImGui.CalcTextSize(word.AsSpan().TrimEnd()).X)
                ImGui.NewLine();

            var start = CursorPosition;
            CursorPosition += word.Length;

            // At least 1 px, since ImGui asserts on a 0size item.
            var size = wordSize with { X = Math.Max(wordSize.X, 1f) };

            // No handler means the measure pass, so layout only.
            if (handler is null)
            {
                ImGui.Dummy(size);
                ImGui.SameLine();
                continue;
            }

            // An invisible button per word, so dragging over the text doesn't accidentally move the window.
            var released = ImGui.InvisibleButton($"##{start}", size);
            var from = ImGui.GetItemRectMin();
            var to = ImGui.GetItemRectMax();

            if (selecting)
                DrawRuns(word, start, from, to.Y, selection: true);

            drawList.AddText(from, textColour, word);
            DrawRuns(word, start, from, to.Y, selection: false);

            if (ImGui.IsMouseHoveringRect(from, to))
                PointAt(word, start, from, to, released);

            ImGui.SameLine();
        }
    }

    // Prefix widths, since CalcTextSize rounds up and summed letter widths drift.
    // I wish there was a better way to do this.
    private static float EdgeOf(string word, int k) =>
        k == 0 ? 0f : ImGui.CalcTextSize(word.AsSpan(0, k)).X;

    // selection:
    // true draws the highlight,
    // false the misspelling underline.
    private void DrawRuns(string word, int start, Vector2 from, float bottom, bool selection)
    {
        var run = -1;
        for (var k = 0; k <= word.Length; k++)
        {
            var marked = k < word.Length &&
                         (selection ? InSelection(CaretTargetFor(start + k + 1)) : MisspelledAt(start + k));

            if (marked && run < 0)
                run = k;

            if (marked || run < 0)
                continue;

            var left = from.X + EdgeOf(word, run);
            var right = from.X + EdgeOf(word, k);

            if (selection)
                ImGui.GetWindowDrawList().AddRectFilled(
                    new Vector2(left, from.Y), new Vector2(right, bottom), ImGui.GetColorU32(ImGuiCol.TextSelectedBg));
            else
                SpellUnderline.Underline(left, right, bottom);

            run = -1;
        }
    }

    private int PressedCaret = -1;

    private void PointAt(string word, int start, Vector2 from, Vector2 to, bool released)
    {
        var mouseX = ImGui.GetIO().MousePos.X;

        var k = 0;
        var left = 0f;
        var right = EdgeOf(word, 1);
        while (k < word.Length - 1 && mouseX >= from.X + right)
        {
            k++;
            left = right;
            right = EdgeOf(word, k + 1);
        }

        var caret = CaretTargetFor(start + k + 1);
        var hovered = ImGui.IsItemHovered();

        // Hovered too, or a click on a window over the preview would start a selection.
        var pressed = hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        if (pressed)
            PressedCaret = caret;

        if (caret >= 0)
        {
            var boundary = mouseX < from.X + (left + right) / 2f ? caret - 1 : caret;

            if (pressed)
                DragAnchor = DragHead = boundary;
            else if (DragAnchor >= 0 && ImGui.IsMouseDown(ImGuiMouseButton.Left))
                DragHead = boundary;
        }

        if (released && caret >= 0 && caret == PressedCaret)
        {
            SelectedRange ??= (ByteIndex(caret), ByteIndex(caret));
            InputHandler.FocusedPreview = true;
        }

        // The caret shows after the letter, not over a marker since it's not technically in the box.
        if (caret >= 0 && hovered)
        {
            var edge = from.X + right;
            ImGui.GetWindowDrawList().AddLine(
                new Vector2(edge, from.Y), new Vector2(edge, to.Y), ImGui.GetColorU32(ImGuiCol.Text), ImGuiHelpers.GlobalScale);
        }

        // AllowWhenBlockedByPopup, any pen menu blocks the words from being hovered.
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByPopup) && ImGui.IsMouseClicked(ImGuiMouseButton.Right)
            && SourceIndexOf(start + k) is >= 0 and var at
            && InputHandler.Spelling.PendAt(InputHandler.ChatInput, at, InputHandler.Plugin.SpellCheck.Check(InputHandler.ChatInput)))
        {
            // Flagged, not opened, popups match by id and this is inside a child.
            SpellingClickFrame = ImGui.GetFrameCount();
        }
    }

    private static readonly ConditionalWeakTable<string, string[]> Words = [];

    // Split once per chunk. Keeps garbage down. Blessings of performance be upon we. Saves about 500kb a frame at 11k letters, so ~30mb/s @ 60fps. Even if it only happens with huge inputs, I know I've written posts that long and debated on them for like 20 minutes.
    private static string[] WordsOf(string content) =>
        Words.GetValue(content, c => [.. WordRegex().Matches(c).Select(m => m.Value)]);

    // A word and its trailing spaces are one item, halving the widgets.
    [GeneratedRegex(@"\S+\s*|\s+")]
    private static partial Regex WordRegex();
}
