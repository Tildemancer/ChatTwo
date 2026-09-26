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

    // Message.TextParamRegex's three, resolved from the game, not the text, so a flag or linked item can change under the same body
    private static readonly string[] LiveParams = ["<item>", "<flag>", "<status>"];

    // Only the body is tokenized, about 0.3 ms per 500 characters, and the affixes around it stay plain text
    private Message BuildPart(string part, (int Start, int Length)? span, Dictionary<string, List<Chunk>> kept)
    {
        var (start, end) = BodyRange(span, part.Length);

        // The body takes the space before the suffix, so its words are the ones one parse of the part gives
        if (end < part.Length && part[end] == ' ')
            end++;

        var text = part[start..end];
        if (!ParsedBodies.TryGetValue(text, out var parsed))
            ParsedBodies[text] = parsed = (LiveParams.Any(text.Contains) ? null : kept.GetValueOrDefault(text)) ?? BuildMessage(text).Content;

        // The database-load constructor: FakeMessage's runs CheckMessageContent over the whole part again
        List<Chunk> content = [.. Plain(part[..start]), .. parsed, .. Plain(part[end..])];
        return new Message(Guid.NewGuid(), 0, 0, DateTimeOffset.UtcNow, new ChatCode(XivChatType.Say, 0, 0),
            [], content, new SeString(), new SeString(), Guid.Empty);

        static IEnumerable<Chunk> Plain(string affix) =>
            ChunkUtil.ToChunks(new SeString(new TextPayload(affix)), ChunkSource.Content, ChatType.Say);
    }

    // No usable span, so the whole part is the body
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

        // The last split's bodies by text, unless the emotes have changed since: switched, loaded or blocked
        // When the count moves, #m changes every part but not its body
        // By content, not Count: the settings edit this set in place, so swapping one for another kept the count
        var emotes = (Plugin.Config.ShowEmotes, EmoteCache.State, Plugin.Config.BlockedEmotes.Aggregate(0, (hash, emote) => hash ^ emote.GetHashCode()));
        var kept = ParsedWithEmotes == emotes ? ParsedBodies : [];
        ParsedBodies = [];
        ParsedWithEmotes = emotes;

        SplitParts = parts;
        SplitMessages = [.. parts.Select((part, i) => BuildPart(part, i < SplitBodies.Count ? SplitBodies[i] : null, kept))];

        // The bodies, not the parts: a part's affixes are chunks of their own, so every part had more than one
        SplitHasEvaluation = ParsedBodies.Values.Any(body => body.Count > 1);

        SplitSources = InputHandler.Plugin.Splitter.BodySources(line);
    }

    private List<int> SplitSources = [];

    private int SplitIndex = -1;

    // In box positions, -1 when not dragging
    private int DragAnchor = -1;

    private int DragHead = -1;

    // In the box's bytes, for InputHandler.Callback
    public (int Start, int End)? SelectedRange;

    // Box works in bytes, preview in chars. Drifts on accents
    private int ByteIndex(int position) =>
        Encoding.UTF8.GetByteCount(InputHandler.ChatInput.AsSpan(0, Math.Clamp(position, 0, InputHandler.ChatInput.Length)));

    // Both in box positions, so one comparison does. The box's selection is a frame behind
    private bool InSelection(int caret)
    {
        var (from, to) = InputHandler.Spelling.InputSelection;
        return caret >= 0 && (Covers(Math.Min(DragAnchor, DragHead), Math.Max(DragAnchor, DragHead)) || Covers(from, to));

        bool Covers(int low, int high) => low >= 0 && caret - 1 >= low && caret <= high;
    }

    // -1 does nothing. The caret lands after the letter, as in a text box
    // A split part carries a channel command and markers of ours, so a position in
    // it means nothing to the box until it has been traced back.
    private int CaretTargetFor(int afterLetter) => SourceIndexOf(afterLetter - 1) is >= 0 and var mapped ? mapped + 1 : -1;

    // part -> body -> composed line -> box. A step that can't be made gives -1
    private int SourceIndexOf(int positionInPart)
    {
        var (leading, prefix) = MapBasis();

        // Nothing was split, so the drawn text is the box's own, trimmed. Only the
        // leading spaces stand between the two. Most messages come through here.
        var index = positionInPart + leading;

        if (SplitIndex >= 0)
        {
            if (SplitIndex >= SplitSources.Count || SplitIndex >= SplitBodies.Count)
                return -1;

            var body = SplitBodies[SplitIndex];
            var offset = positionInPart - body.Start;
            if (offset < 0 || offset >= body.Length)
                return -1;

            index = SplitSources[SplitIndex] + offset - prefix + leading;
        }

        return index >= 0 && index < InputHandler.ChatInput.Length ? index : -1;
    }

    // Once per text: SourceIndexOf runs per letter while selecting, and Trim copies the whole input
    private (string Typed, string Composed, int Leading, int Prefix) Basis = ("", "", 0, 0);

    private (int Leading, int Prefix) MapBasis()
    {
        var typed = InputHandler.ChatInput;
        var composed = InputHandler.ComposedLine;
        if (ReferenceEquals(typed, Basis.Typed) && ReferenceEquals(composed, Basis.Composed))
            return (Basis.Leading, Basis.Prefix);

        // ComposeLine only ever prepends, and always to the trimmed input, so the gap
        // between the two is fixed and the leading spaces have to be added back.
        Basis = (typed, composed, typed.Length - typed.TrimStart().Length, composed.Length - typed.Trim().Length);
        return (Basis.Leading, Basis.Prefix);
    }

    // Against the game's viewport, which multi-monitor windows can move off the desktop origin
    // A chat window on another monitor keeps upstream's placement
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

    // Measuring lays the whole preview out invisibly, as costly as drawing it. Selection and hover don't move text
    // Face too: Plugin.Draw's font can change at the same size
    // The UI scale too: it changes the style's padding and spacing without always changing the font
    // Not keyed: other style edits, and an emote image failing after the measure
    // Either way the next keystroke measures again
    private (Message? Preview, List<Message>? Parts, float Window, bool WindowMode, float Screen, ImFontPtr Face, float Font, float Scale, bool Emotes)? MeasuredFor;

    public void CalculatePreview()
    {
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

        // We Pre-draw this once to get the actual height :HideThePain:
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

        // CalculatePreview has already set PreviewWidth for one column
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
            // A part taller than the screen overflows its own column, no empty one before it
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

    // Column width, since text wraps against the region it's drawn in
    private float MeasureColumn(Action draw)
    {
        var restore = ImGui.GetCursorPos();
        var height = 0f;

        // One pixel tall at the cursor. Outside its parent ImGui culls it, and a culled child measures zero. Oops
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
            // Columns are packed in every mode, only a Top or Bottom window draws them
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
        // A drag the preview wasn't drawn for, hidden in combat say, is dropped rather than committed on return
        var frame = ImGui.GetFrameCount();
        if (LastDrawFrame < frame - 1)
            DragAnchor = DragHead = -1;

        LastDrawFrame = frame;

        // Finished here, not in PointAt: the button can come up anywhere. Handed over on
        // release, per frame it drags focus into the box
        if (DragAnchor >= 0 && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            if (DragHead >= 0 && DragHead != DragAnchor)
            {
                // Anchor then head, not low then high: the box's caret goes where the drag ended
                SelectedRange = (ByteIndex(DragAnchor), ByteIndex(DragHead));
                InputHandler.FocusedPreview = true;
            }

            DragAnchor = DragHead = -1;
        }
    }

    // Built once per source, per letter would be a cross-plugin call each
    private string?[] SpellMarks = [];

    private readonly Dictionary<(string Text, bool Complete, (int Start, int Length)? Body), string?[]> MarksFor = [];

    private int MarksGeneration;

    // complete: not being typed into. The drawn text is trimmed, which loses the trailing-space
    // signal, so without this the last word of every part is never checked
    private void SetSpellSource(string text, bool complete, (int Start, int Length)? body = null)
    {
        // A word added or ignored changes the answers for text that has not changed.
        var generation = InputHandler.Plugin.SpellCheck.Generation;
        if (generation != MarksGeneration)
        {
            MarksFor.Clear();
            MarksGeneration = generation;
        }

        // By value. By instance it rebuilt every frame: Trim makes a new string on a trailing space,
        // and split parts swap in and out
        var key = (text, complete, body);
        if (MarksFor.TryGetValue(key, out var remembered))
        {
            SpellMarks = remembered;
            return;
        }

        if (MarksFor.Count >= Ipc.SpellCheck.MostToRemember)
            MarksFor.Clear();

        SpellMarks = new string?[text.Length];
        MarksFor[key] = SpellMarks;

        // Only the typed slice, our markers come back as misspellings otherwise
        var (from, to) = body is { Length: > 0 } ? BodyRange(body, text.Length) : (0, text.Length);

        // Padded only for the check. Unfinished, cut at the body's end instead: on the last part the
        // final character is ours ("]" or an OOC bracket), so every word looked finished
        var forCheck = complete
            ? text.Length > 0 ? text + " " : text
            : text[..to];

        foreach (var misspelling in InputHandler.Plugin.SpellCheck.Check(forCheck))
        {
            var word = misspelling.Word(text);
            if (word.Length == 0)
                continue;

            var end = Math.Min(to, misspelling.Start + misspelling.Length);
            for (var i = Math.Max(from, misspelling.Start); i < end; i++)
                SpellMarks[i] = word;
        }
    }

    // Same rule as the checker. Whitespace alone missed a closing OOC bracket, which the splitter lifts off
    private static bool FinishedTyping(string typed) =>
        typed is [.., var last] && (char.IsWhiteSpace(last) || char.IsPunctuation(last) && last is not ('\'' or '-'));

    private string? MisspelledWordAt(int position) =>
        position >= 0 && position < SpellMarks.Length ? SpellMarks[position] : null;

    // The menu opens a frame after the press: ImGui's EndFrame closes popups on a right press with no item hovered
    // An open menu blocks the words
    private int SpellingClickFrame = int.MinValue;

    private void DrawSpellingPopup()
    {
        // Opened here so the id matches BeginPopup below
        if (ImGui.GetFrameCount() == SpellingClickFrame + 1)
            ImGui.OpenPopup("##preview-spelling");

        using var popup = ImRaii.Popup("##preview-spelling");
        if (!popup.Success)
            return;

        // The real input, not the trimmed copy
        InputHandler.Spelling.DrawContextEntries(ref InputHandler.ChatInput);
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
            // Every part but the last was cut to length, so only the last part's last word is unfinished
            SetSpellSource(SplitParts![index], complete: index != SplitParts.Count - 1 || FinishedTyping(InputHandler.ChatInput),
                body: index < SplitBodies.Count ? SplitBodies[index] : null);

            DrawChunksPreview(SplitMessages![index].Content, handler);
        }
        finally
        {
            SplitIndex = -1;
        }
    }

    // A widget per word, not per letter: at 11000 letters the Selectables cost about 2.7 ms a frame
    private void DrawWords(string content, PayloadHandler? handler)
    {
        var selecting = DragAnchor >= 0 || InputHandler.Spelling.InputSelection.Start >= 0;
        var (drawList, textColour) = (ImGui.GetWindowDrawList(), ImGui.GetColorU32(ImGuiCol.Text));

        foreach (var word in WordsOf(content))
        {
            var wordSize = ImGui.CalcTextSize(word);

            // Trailing spaces ride along, but only the word has to fit, as in any wrapped text
            var room = ImGui.GetContentRegionAvail().X;
            if (room < wordSize.X && room < ImGui.CalcTextSize(word.AsSpan().TrimEnd()).X)
                ImGui.NewLine();

            var start = CursorPosition;
            CursorPosition += word.Length;

            // ImGui refuses a zero-size item
            var size = wordSize with { X = Math.Max(wordSize.X, 1f) };

            // Layout is all that counts in the measuring child, a pixel tall and taking no input
            // Only the measure pass has no handler
            if (handler is null)
            {
                ImGui.Dummy(size);
                ImGui.SameLine();
                continue;
            }

            // A button, so a press holds the item and a drag over the text can't move the window
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

    // From the word's left, where letter k starts
    // Prefix widths: CalcTextSize rounds each call up, so summed letter widths drift
    private static float EdgeOf(string word, int k) =>
        k == 0 ? 0f : ImGui.CalcTextSize(word.AsSpan(0, k)).X;

    // The selection behind the text or the misspelling underline, a run of letters at a time
    private void DrawRuns(string word, int start, Vector2 from, float bottom, bool selection)
    {
        var run = -1;
        for (var k = 0; k <= word.Length; k++)
        {
            var marked = k < word.Length &&
                         (selection ? InSelection(CaretTargetFor(start + k + 1)) : MisspelledWordAt(start + k) != null);

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

        // The letter under the pointer: the first whose right edge is past mouseX, else the last
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

        // Hovered as well: a click on a window lying over the preview isn't ours
        var pressed = hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        if (pressed)
            PressedCaret = caret;

        // Drag boundary is whichever half of the letter the pointer's on
        if (caret >= 0)
        {
            var boundary = mouseX < from.X + (left + right) / 2f ? caret - 1 : caret;

            if (pressed)
                DragAnchor = DragHead = boundary;
            else if (DragAnchor >= 0 && ImGui.IsMouseDown(ImGuiMouseButton.Left))
                DragHead = boundary;
        }

        // Press and release on the same letter is a click
        if (released && caret >= 0 && caret == PressedCaret)
        {
            SelectedRange ??= (ByteIndex(caret), ByteIndex(caret));
            InputHandler.FocusedPreview = true;
        }

        // Caret on the letter's trailing edge. Nothing over a marker, clicking one does nothing
        if (caret >= 0 && hovered)
        {
            var edge = from.X + right;
            ImGui.GetWindowDrawList().AddLine(
                new Vector2(edge, from.Y), new Vector2(edge, to.Y), ImGui.GetColorU32(ImGuiCol.Text), ImGuiHelpers.GlobalScale);
        }

        if (MisspelledWordAt(start + k) is { } misspelled && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByPopup) && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            var first = start + k;
            while (first > 0 && ReferenceEquals(SpellMarks[first - 1], misspelled))
                first--;

            InputHandler.Spelling.SetPendingWord(misspelled, SourceIndexOf(first));

            // Flagged, not opened: a popup is found by the id stack it was opened under, and this is in a child
            SpellingClickFrame = ImGui.GetFrameCount();
        }
    }

    private static readonly ConditionalWeakTable<string, string[]> Words = new();

    // Split once per chunk text, not every frame: at 11000 letters it was about 500 KB of garbage a frame
    // Keyed by the chunk's string instance, so an entry goes when its chunk does
    private static string[] WordsOf(string content) =>
        Words.GetValue(content, c => WordRegex().Matches(c).Select(m => m.Value).ToArray());

    // A word and the spaces after it are one item, half as many as a word and a space each
    // Only leading spaces stand alone
    [GeneratedRegex(@"\S+\s*|\s+")]
    private static partial Regex WordRegex();
}
