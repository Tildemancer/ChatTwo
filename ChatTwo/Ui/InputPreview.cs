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
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;

namespace ChatTwo.Ui;

public partial class InputPreview : Window
{
    private readonly InputHandler InputHandler;

    private bool Drawing;
    private bool HasEvaluation;
    public float PreviewHeight;

    // TildeTools
    private string LastInput = string.Empty;
    private string LastTrimmed = string.Empty;
    private Message? PreviewMessage;

    private int CursorPosition;
    private bool NextChunkIsAutoTranslate;

    public int SelectedCursorPos = -1;

    public InputPreview(InputHandler inputHandler) : base("##chat2-inputpreview")
    {
        Flags = ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoMove |
                ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoScrollbar;

        InputHandler = inputHandler;

        RespectCloseHotkey = false;
        DisableWindowSounds = true;
        IsOpen = true;

        Plugin.Framework.Update += UpdateConditionCheck;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= UpdateConditionCheck;
    }

    private bool ValidDraw => !string.IsNullOrEmpty(InputHandler.ChatInput) && InputHandler.ChatInput.Length >= Plugin.Config.PreviewMinimum;
    private void UpdateConditionCheck(IFramework framework)
    {
        Drawing = ValidDraw;
        if (!Drawing)
        {
            LastInput = string.Empty;
            PreviewHeight = 0;

            // TildeTools
            // Height was just zeroed, so remeasure even for the same text
            MeasuredFor = null;
            PreviewMessage = null;
            HasEvaluation = false;

            // TildeTools
            // A drag ends on button-up, only noticed while drawing, or it survives into the next preview
            DragAnchor = -1;
            DragHead = -1;

            return;
        }

        if (PreviewMessage == null || LastInput != InputHandler.ChatInput)
        {
            LastInput = InputHandler.ChatInput;
            LastTrimmed = LastInput.Trim();

            // TildeTools
            // Past the cap only the parts are drawn, or just the header when the splitter declines
            // Parsed, it cost 28-35 ms at 18k characters: ReplaceWithPayload copies bytes[i..] at every byte
            PreviewMessage = BuildMessage(Encoding.UTF8.GetByteCount(LastTrimmed) > Ipc.Splitter.DefaultByteCap ? string.Empty : LastTrimmed);
        }

        UpdateSplitParts();

        // TildeTools
        // A trailing space is the checker's done-signal, and Trim would throw it away
        // Split, each part marks itself: the whole text's marks were a 144 KB array a keystroke at 18k, never drawn
        if (SplitMessages is null)
            SetSpellSource(LastTrimmed, complete: char.IsWhiteSpace(InputHandler.ChatInput[^1]));
        else
            SpellMarks = [];

        // TildeTools
        HasEvaluation = !Plugin.Config.OnlyPreviewIf || PreviewMessage.Content.Count > 1 || SplitHasEvaluation;
    }

    // TildeTools
    private static Message BuildMessage(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        AutoTranslate.ReplaceWithPayload(ref bytes);

        var chunks = ChunkUtil.ToChunks(SeString.Parse(bytes), ChunkSource.Content, ChatType.Say).ToList();
        var message = Message.FakeMessage(chunks, new ChatCode(XivChatType.Say, 0, 0));
        message.DecodeTextParam();

        return message;
    }

    // TildeTools
    private Dictionary<string, List<Chunk>> ParsedBodies = [];
    private (bool Show, EmoteCache.LoadingState Loaded, int Blocked) ParsedWithEmotes;
    private bool SplitHasEvaluation;

    // TildeTools
    // Resolved from the game, not the text, so a flag or linked item can change under the same body
    private static readonly string[] LiveParams = ["<item>", "<flag>", "<status>"];

    // TildeTools
    // Only the body is tokenized, about 0.3 ms per 500 characters, and the affixes around it stay plain text
    private Message BuildPart(string part, (int Start, int Length)? span, Dictionary<string, List<Chunk>> kept)
    {
        // TildeTools
        // No usable span, so the whole part is the body
        var (start, end) = span is { } body && body.Start >= 0 && body.Start + body.Length <= part.Length
            ? (body.Start, body.Start + body.Length)
            : (0, part.Length);

        // TildeTools
        // The body takes the space before the suffix, so its words are the ones one parse of the part gives
        if (end < part.Length && part[end] == ' ')
            end++;

        var text = part[start..end];
        if (!ParsedBodies.TryGetValue(text, out var parsed))
            ParsedBodies[text] = parsed = (LiveParams.Any(text.Contains) ? null : kept.GetValueOrDefault(text)) ?? BuildMessage(text).Content;

        // TildeTools
        // The database-load constructor: FakeMessage's runs CheckMessageContent over the whole part again
        List<Chunk> content = [.. Plain(part[..start]), .. parsed, .. Plain(part[end..])];
        return new Message(Guid.NewGuid(), 0, 0, DateTimeOffset.UtcNow, new ChatCode(XivChatType.Say, 0, 0),
            [], content, new SeString(), new SeString(), Guid.Empty);

        static IEnumerable<Chunk> Plain(string affix) =>
            ChunkUtil.ToChunks(new SeString(new TextPayload(affix)), ChunkSource.Content, ChatType.Say);
    }

    // TildeTools
    private List<string>? SplitParts;

    // TildeTools
    private List<Message>? SplitMessages;

    private string LastSplitInput = string.Empty;
    private int LastSplitGeneration = -1;

    // TildeTools
    private List<(int Start, int Length)> SplitBodies = [];

    // TildeTools
    private int SplitPostingMs;

    // TildeTools
    private bool Measuring;

    // TildeTools
    private void UpdateSplitParts()
    {
        var line = InputHandler.ComposedLine;
        var generation = InputHandler.Plugin.Splitter.Generation;
        if (line == LastSplitInput && generation == LastSplitGeneration)
            return;

        LastSplitInput = line;
        LastSplitGeneration = generation;
        SplitParts = null;
        SplitMessages = null;
        SplitHasEvaluation = false;
        SplitBodies = [];

        if (!InputHandler.Plugin.Splitter.IsAvailable || line.Length == 0)
            return;

        // TildeTools
        var parts = InputHandler.Plugin.Splitter.Split(line);
        if (parts is not { Count: > 1 })
            return;

        // TildeTools
        SplitBodies = InputHandler.Plugin.Splitter.BodySpans(line);
        SplitPostingMs = InputHandler.Plugin.Splitter.PostingMs(line);

        // TildeTools
        // The last split's bodies by text, unless the emotes have changed since: switched, loaded or blocked
        // When the count moves, #m changes every part but not its body
        var emotes = (Plugin.Config.ShowEmotes, EmoteCache.State, Plugin.Config.BlockedEmotes.Count);
        var kept = ParsedWithEmotes == emotes ? ParsedBodies : [];
        ParsedBodies = [];
        ParsedWithEmotes = emotes;

        SplitParts = parts;
        SplitMessages = [.. parts.Select((part, i) => BuildPart(part, i < SplitBodies.Count ? SplitBodies[i] : null, kept))];

        // TildeTools
        // The bodies, not the parts: a part's affixes are chunks of their own, so every part had more than one
        SplitHasEvaluation = ParsedBodies.Values.Any(body => body.Count > 1);

        // TildeTools
        SplitSources = InputHandler.Plugin.Splitter.BodySources(line);

    }

    // TildeTools
    private List<int> SplitSources = [];

    // TildeTools
    private int SplitIndex = -1;

    // TildeTools
    // In box positions, -1 when not dragging
    private int DragAnchor = -1;

    // TildeTools
    private int DragHead = -1;

    // TildeTools
    public int SelectedRangeStart = -1;
    public int SelectedRangeEnd = -1;

    // TildeTools
    private bool InDrag(int caret) =>
        caret >= 0 && DragAnchor >= 0 && DragHead >= 0 &&
        caret - 1 >= Math.Min(DragAnchor, DragHead) &&
        caret <= Math.Max(DragAnchor, DragHead);

    // TildeTools
    // Both in box positions, so one comparison does. The box's selection is a frame behind
    private bool InSelection(int caret)
    {
        if (InDrag(caret))
            return true;

        var (from, to) = InputHandler.Spelling.InputSelection;

        return caret >= 0 && from >= 0 && caret - 1 >= from && caret <= to;
    }

    // TildeTools
    // -1 does nothing. The caret lands after the letter, as in a text box
    private int CaretTargetFor(int afterLetter)
    {
        // TildeTools
        // A split part carries a channel command and markers of ours, so a position in
        // it means nothing to the box until it has been traced back.
        var mapped = SourceIndexOf(SplitIndex, afterLetter - 1);
        return mapped < 0 ? -1 : mapped + 1;
    }

    // TildeTools
    // part -> body -> composed line -> box. A step that can't be made gives -1
    private int SourceIndexOf(int partIndex, int positionInPart)
    {
        var typedText = InputHandler.ChatInput;
        var (leading, prefix) = MapBasis();

        // TildeTools
        // Nothing was split, so the drawn text is the box's own, trimmed. Only the
        // leading spaces stand between the two. Most messages come through here.
        if (partIndex < 0)
        {
            var direct = positionInPart + leading;
            return direct >= 0 && direct < typedText.Length ? direct : -1;
        }

        if (partIndex >= SplitSources.Count || partIndex >= SplitBodies.Count)
            return -1;

        var body = SplitBodies[partIndex];
        var offset = positionInPart - body.Start;
        if (offset < 0 || offset >= body.Length)
            return -1;

        var composed = SplitSources[partIndex] + offset;

        var index = composed - prefix + leading;
        return index >= 0 && index < typedText.Length ? index : -1;
    }

    // TildeTools
    // Once per text, not per letter: SourceIndexOf runs for every drawn letter, and Trim copies the whole input
    private (string Typed, string Composed, int Leading, int Prefix) Basis = ("", "", 0, 0);

    private (int Leading, int Prefix) MapBasis()
    {
        var typed = InputHandler.ChatInput;
        var composed = InputHandler.ComposedLine;
        if (ReferenceEquals(typed, Basis.Typed) && ReferenceEquals(composed, Basis.Composed))
            return (Basis.Leading, Basis.Prefix);

        // TildeTools
        // ComposeLine only ever prepends, and always to the TRIMMED input, so the gap
        // between the two is fixed and the leading spaces have to be added back.
        Basis = (typed, composed, typed.Length - typed.TrimStart().Length, composed.Length - typed.Trim().Length);
        return (Basis.Leading, Basis.Prefix);
    }

    public bool IsDrawable => ValidDraw && HasEvaluation;

    private static bool IsWindowMode => Plugin.Config.PreviewPosition is PreviewPosition.Top or PreviewPosition.Bottom;
    public override bool DrawConditions()
    {
        return IsWindowMode && IsDrawable;
    }

    public override void PreDraw()
    {
        var pos = InputHandler.MainWindow.LastWindowPos;
        var size = InputHandler.MainWindow.LastWindowSize;

        var width = PreviewWidth > 0 ? PreviewWidth : size.X;
        Size = new Vector2(width, PreviewHeight);

        var y = Plugin.Config.PreviewPosition switch
        {
            PreviewPosition.Top => pos.Y - PreviewHeight,
            PreviewPosition.Bottom => pos.Y + size.Y,
            _ => throw new ArgumentOutOfRangeException(nameof(Plugin.Config.PreviewPosition), Plugin.Config.PreviewPosition, null),
        };

        Position = KeepOnScreen(pos with { Y = y }, pos, size.X, width);
        PositionCondition = ImGuiCond.Always;
    }

    // TildeTools
    private Vector2 KeepOnScreen(Vector2 wanted, Vector2 windowPos, float windowWidth, float previewWidth)
    {
        var screen = ImGui.GetIO().DisplaySize;

        if (wanted.Y >= 0 && wanted.Y + PreviewHeight <= screen.Y)
            return wanted;

        var right = windowPos.X + windowWidth;
        var fitsRight = right + previewWidth <= screen.X;
        var fitsLeft = windowPos.X - previewWidth >= 0;

        var x = fitsRight || !fitsLeft ? right : windowPos.X - previewWidth;

        // TildeTools
        var top = Math.Clamp(windowPos.Y, 0, Math.Max(0, screen.Y - PreviewHeight));

        return new Vector2(Math.Clamp(x, 0, Math.Max(0, screen.X - previewWidth)), top);
    }

    public override void Draw()
    {
        CalculatePreview();
        DrawPreview();
    }

    // TildeTools
    private float ColumnWidth = 200f;

    public float PreviewWidth;

    // TildeTools
    private readonly List<List<int>> Columns = [];

    // TildeTools
    // Measuring lays the whole preview out invisibly, as costly as drawing it. Selection and hover don't move text
    private (Message? Preview, List<Message>? Parts, float Window, bool WindowMode, float Screen, float Font, bool Emotes)? MeasuredFor;

    public void CalculatePreview()
    {
        // TildeTools
        var key = (PreviewMessage, SplitMessages, InputHandler.MainWindow.LastWindowSize.X,
            IsWindowMode, ImGui.GetIO().DisplaySize.Y, ImGui.GetFontSize(), Plugin.Config.ShowEmotes);
        if (MeasuredFor == key)
            return;

        MeasuredFor = key;

        // We Pre-draw this once to get the actual height :HideThePain:
        PreviewHeight = 0;

        // TildeTools
        var sidePadding = ImGui.GetStyle().WindowPadding.X * 2;
        ColumnWidth = Math.Max(120f, InputHandler.MainWindow.LastWindowSize.X - sidePadding);
        PreviewWidth = ColumnWidth + sidePadding;
        Columns.Clear();

        var padding = IsWindowMode ? ImGui.GetStyle().WindowPadding.Y * 2 : 0;

        // TildeTools
        // Nothing interactive while measuring, duplicate ids would fight the real draw
        Measuring = true;
        try
        {
            if (SplitMessages is null)
            {
                PreviewHeight = MeasureColumn(() =>
                {
                    ImGui.TextUnformatted(Language.Options_Preview_Header);
                    DrawChunksPreview(PreviewMessage!.Content);
                }) + padding;

                return;
            }

            PackColumns(padding);
        }
        finally
        {
            Measuring = false;
        }
    }

    // TildeTools
    private void PackColumns(float padding)
    {
        var available = Math.Max(100f, ImGui.GetIO().DisplaySize.Y - padding);

        float header = 0;
        List<float> heights = [];

        MeasureColumn(() =>
        {
            var mark = ImGui.GetCursorPosY();
            DrawSplitHeader();
            header = ImGui.GetCursorPosY() - mark;

            for (var i = 0; i < SplitMessages!.Count; i++)
            {
                mark = ImGui.GetCursorPosY();
                DrawSplitPart(i, null);
                heights.Add(ImGui.GetCursorPosY() - mark);
            }
        });

        // TildeTools
        if (heights.Count < SplitMessages!.Count)
        {
            Columns.Add([.. Enumerable.Range(0, SplitMessages.Count)]);
            PreviewHeight = available + padding;
            PreviewWidth = ColumnWidth + ImGui.GetStyle().WindowPadding.X * 2;
            return;
        }

        List<int> current = [];
        var used = header;
        var tallest = header;

        for (var i = 0; i < heights.Count; i++)
        {
            // TildeTools
            // A part taller than the screen overflows its column rather than looping forever
            if (current.Count > 0 && used + heights[i] > available)
            {
                Columns.Add(current);
                tallest = Math.Max(tallest, used);
                current = [];
                used = 0;
            }

            current.Add(i);
            used += heights[i];
        }

        if (current.Count > 0)
        {
            Columns.Add(current);
            tallest = Math.Max(tallest, used);
        }

        PreviewHeight = Math.Min(tallest, available) + padding;
        PreviewWidth = ColumnWidth * Math.Max(1, Columns.Count) + ImGui.GetStyle().WindowPadding.X * 2;
    }

    // TildeTools
    // Column width, since text wraps against the region it's drawn in
    private float MeasureColumn(Action draw)
    {
        var restore = ImGui.GetCursorPos();
        var height = 0f;

        // TildeTools
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

        // TildeTools
        ImGui.SetCursorPos(restore);
        return height;
    }

    // TildeTools
    private int LastDrawFrame = -1;

    public void DrawPreview()
    {
        // TildeTools
        // A drag the preview wasn't drawn for, hidden in combat say, is dropped rather than committed on return
        var frame = ImGui.GetFrameCount();
        if (LastDrawFrame < frame - 1)
            DragAnchor = DragHead = -1;

        LastDrawFrame = frame;

        // TildeTools
        // Finished here, not in the letter loop: the button can come up anywhere. Handed over on
        // release, per frame it drags focus into the box
        if (DragAnchor >= 0 && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            if (DragHead >= 0 && DragHead != DragAnchor)
            {
                // TildeTools
                // Anchor then head, not low then high: the box's caret goes where the drag ended
                SelectedRangeStart = DragAnchor;
                SelectedRangeEnd = DragHead;
                InputHandler.FocusedPreview = true;
            }

            DragAnchor = -1;
            DragHead = -1;
        }

        if (SplitMessages is null)
        {
            using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero))
            {
                ImGui.TextUnformatted(Language.Options_Preview_Header);
                DrawChunksPreview(PreviewMessage!.Content, InputHandler.PayloadHandler);
            }

            DrawSpellingPopup();
            return;
        }

        // TildeTools
        // A tooltip sizes itself, so one column. Window-mode columns linger after switching, so check the mode too
        if (!IsWindowMode || Columns.Count == 0)
        {
            using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero))
            {
                DrawSplitHeader();

                for (var i = 0; i < SplitMessages.Count; i++)
                    DrawSplitPart(i, InputHandler.PayloadHandler);
            }

            DrawSpellingPopup();
            return;
        }

        var height = ImGui.GetContentRegionAvail().Y;

        for (var c = 0; c < Columns.Count; c++)
        {
            if (c > 0)
                ImGui.SameLine(0, 0);

            using var child = ImRaii.Child($"##preview-col{c}", new Vector2(ColumnWidth, height), false,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);

            if (!child)
                continue;

            using var style = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero);

            // TildeTools
            if (c == 0)
                DrawSplitHeader();

            foreach (var part in Columns[c])
                DrawSplitPart(part, InputHandler.PayloadHandler);
        }

        DrawSpellingPopup();
    }

    // TildeTools
    // Built once per source, per letter would be a cross-plugin call each
    private string?[] SpellMarks = [];

    // TildeTools
    private readonly Dictionary<(string Text, bool Complete, (int Start, int Length)? Body), string?[]> MarksFor = [];

    // TildeTools
    // Every part of a 32000-byte message, about 67, with room over
    private const int MostMarksToRemember = 256;

    // TildeTools
    private int MarksGeneration;

    // TildeTools
    // complete: not being typed into. The drawn text is trimmed, which loses the trailing-space
    // signal, so without this the last word of every part is never checked
    private void SetSpellSource(string text, bool complete = false, (int Start, int Length)? body = null)
    {
        // TildeTools
        // A word added or ignored changes the answers for text that has not changed.
        var generation = InputHandler.Plugin.SpellCheck.Generation;
        if (generation != MarksGeneration)
        {
            MarksFor.Clear();
            MarksGeneration = generation;
        }

        // TildeTools
        // By value. By instance it rebuilt every frame: Trim makes a new string on a trailing space,
        // and split parts swap in and out
        var key = (text, complete, body);
        if (MarksFor.TryGetValue(key, out var remembered))
        {
            SpellMarks = remembered;
            return;
        }

        if (MarksFor.Count >= MostMarksToRemember)
            MarksFor.Clear();

        SpellMarks = new string?[text.Length];
        MarksFor[key] = SpellMarks;

        // TildeTools
        // Only the typed slice, our markers come back as misspellings otherwise
        var from = 0;
        var to = text.Length;

        if (body is { } span && span.Length > 0 && span.Start >= 0 && span.Start + span.Length <= text.Length)
        {
            from = span.Start;
            to = span.Start + span.Length;
        }

        // TildeTools
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

    // TildeTools
    // Same rule as the checker. Whitespace alone missed a closing OOC bracket, which the splitter lifts off
    private static bool FinishedTyping(string typed) =>
        typed.Length > 0 &&
        (char.IsWhiteSpace(typed[^1]) ||
         (char.IsPunctuation(typed[^1]) && typed[^1] is not ('\'' or '-')));

    // TildeTools
    private string? MisspelledWordAt(int position) =>
        position >= 0 && position < SpellMarks.Length ? SpellMarks[position] : null;

    // TildeTools
    private bool OpenSpellingPopup;

    private void DrawSpellingPopup()
    {
        // TildeTools
        // Opened here so the id matches BeginPopup below
        if (OpenSpellingPopup)
        {
            OpenSpellingPopup = false;
            ImGui.OpenPopup("##preview-spelling");
        }

        using var popup = ImRaii.Popup("##preview-spelling");
        if (!popup.Success)
            return;

        // TildeTools
        // The real input, not the trimmed copy
        InputHandler.Spelling.DrawContextEntries(ref InputHandler.ChatInput);
    }

    // TildeTools
    private void DrawSplitHeader()
    {
        if (SplitParts is not { } parts)
            return;

        // TildeTools
        var seconds = SplitPostingMs / 1000f;

        ImGui.TextDisabled(seconds >= 1f
            ? $"Will be sent as {parts.Count} messages, over about {seconds:0.#} seconds:"
            : $"Will be sent as {parts.Count} messages:");
    }

    private void DrawSplitPart(int index, PayloadHandler? handler)
    {
        if (SplitParts is not { } parts || SplitMessages is not { } messages)
            return;

        if (index < 0 || index >= messages.Count)
            return;

        using var id = ImRaii.PushId(index);

        ImGui.TextDisabled($"{index + 1}.");
        ImGui.SameLine();

        using var indent = ImRaii.PushIndent();

        // TildeTools
        // The whole text's marks, back after each part
        var previousMarks = SpellMarks;
        var previousSplitIndex = SplitIndex;

        try
        {
            // TildeTools
            SplitIndex = index;

            // TildeTools
            // Every part but the last was cut to length, so only the last part's last word is unfinished
            var typed = InputHandler.ChatInput;
            var lastPart = index == parts.Count - 1;

            SetSpellSource(
                parts[index],
                complete: !lastPart || FinishedTyping(typed),
                body: index < SplitBodies.Count ? SplitBodies[index] : null);

            DrawChunksPreview(messages[index].Content, handler);
        }
        finally
        {
            SpellMarks = previousMarks;
            SplitIndex = previousSplitIndex;
        }
    }

    private void DrawChunksPreview(IReadOnlyList<Chunk> chunks, PayloadHandler? handler = null, float lineWidth = 0f)
    {
        CursorPosition = 0;

        using var style = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
        for (var i = 0; i < chunks.Count; i++)
        {
            if (chunks[i] is TextChunk text && string.IsNullOrEmpty(text.Content))
                continue;

            DrawChunkPreview(chunks[i], handler, lineWidth);

            if (i < chunks.Count - 1)
            {
                ImGui.SameLine();
            }
            else if (chunks[i].Link is EmotePayload && Plugin.Config.ShowEmotes)
            {
                // TildeTools
                // Emote payloads add no newline, which breaks non-modern mode
                ImGui.SameLine();
                ImGui.TextUnformatted("");
            }
        }
    }

    private void DrawChunkPreview(Chunk chunk, PayloadHandler? handler = null, float lineWidth = 0f)
    {
        if (chunk is IconChunk icon)
        {
            InputHandler.ChunkHandler.DrawIcon(chunk, icon, handler);
            if (icon.Icon != BitmapFontIcon.AutoTranslateBegin)
                return;

            NextChunkIsAutoTranslate = true;
            var payload = (AutoTranslatePayload) chunk.Link!;
            CursorPosition += $"<at:{payload.Group},{payload.Key}>".Length;

            return;
        }

        if (chunk is not TextChunk text)
            return;

        if (chunk.Link is EmotePayload emotePayload && Plugin.Config.ShowEmotes)
        {
            var emoteSize = ImGui.CalcTextSize("W");
            emoteSize = emoteSize with { Y = emoteSize.X } * 1.5f;

            // TildeTools
            // TextWrap doesn't work for emotes, wrap by hand
            if (ImGui.GetContentRegionAvail().X < emoteSize.X)
                ImGui.NewLine();

            // TildeTools
            var image = EmoteCache.GetEmote(emotePayload.Code);
            if (image is { Failed: false })
            {
                if (image.IsLoaded)
                    image.Draw(emoteSize);
                else
                    ImGui.Dummy(emoteSize);

                if (ImGui.IsItemHovered())
                    ImGuiUtil.Tooltip(emotePayload.Code);

                CursorPosition += emotePayload.Code.Length;
                return;
            }
        }

        if (NextChunkIsAutoTranslate)
        {
            NextChunkIsAutoTranslate = false;
            ImGuiUtil.WrapText(text.Content, chunk, handler, InputHandler.Plugin.DefaultText, lineWidth);
            return;
        }

        if (text.Link != null)
        {
            if (text.Link is ItemPayload)
                CursorPosition += "<item>".Length;
            else if (text.Link is MapLinkPayload)
                CursorPosition += "<flag>".Length;
            else if (text.Link is EmotePayload emote)
                CursorPosition += emote.Code.Length;
            else if (text.Link is UriPayload)
                CursorPosition += text.Content.Length;

            ImGuiUtil.WrapText(text.Content, chunk, handler, InputHandler.Plugin.DefaultText, lineWidth);
            return;
        }

        // TildeTools
        // A widget per word, not per letter: at 11000 letters the Selectables cost about 2.7 ms a frame
        var selecting = DragAnchor >= 0 || InputHandler.Spelling.InputSelection.Start >= 0;

        foreach (var word in WordsOf(text.Content))
        {
            var wordSize = ImGui.CalcTextSize(word);

            // TildeTools
            // Trailing spaces ride along, but only the word has to fit, as in any wrapped text
            // The whole first, so the trimmed word is only measured when it might not fit
            var room = ImGui.GetContentRegionAvail().X;
            if (room < wordSize.X && room < ImGui.CalcTextSize(word.AsSpan().TrimEnd()).X)
                ImGui.NewLine();

            var start = CursorPosition;
            CursorPosition += word.Length;

            // TildeTools
            // ImGui refuses a zero-size item
            var size = wordSize with { X = Math.Max(wordSize.X, 1f) };

            // TildeTools
            // Layout is all that counts in the measuring child, a pixel tall and taking no input
            if (Measuring)
            {
                ImGui.Dummy(size);
                ImGui.SameLine();
                continue;
            }

            // TildeTools
            // A button, so a press holds the item and a drag over the text can't move the window
            var released = ImGui.InvisibleButton($"##{start}", size);
            var from = ImGui.GetItemRectMin();
            var to = ImGui.GetItemRectMax();

            if (selecting)
                DrawRuns(word, start, from, to.Y, selection: true);

            ImGui.GetWindowDrawList().AddText(from, ImGui.GetColorU32(ImGuiCol.Text), word);
            DrawRuns(word, start, from, to.Y, selection: false);

            if (ImGui.IsMouseHoveringRect(from, to))
                PointAt(word, start, from, to, released);

            ImGui.SameLine();
        }
        ImGui.NewLine();
    }

    // TildeTools
    // From the word's left, where letter k starts
    // Prefix widths: CalcTextSize rounds each call up, so summed letter widths drift
    private static float EdgeOf(string word, int k) =>
        k == 0 ? 0f : ImGui.CalcTextSize(word.AsSpan(0, k)).X;

    // TildeTools
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

    // TildeTools
    private int PressedCaret = -1;

    // TildeTools
    private void PointAt(string word, int start, Vector2 from, Vector2 to, bool released)
    {
        var mouseX = ImGui.GetIO().MousePos.X;

        // TildeTools
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

        // TildeTools
        // Hovered as well: a click on a window lying over the preview isn't ours
        var pressed = hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        if (pressed)
            PressedCaret = caret;

        // TildeTools
        // Drag boundary is whichever half of the letter the pointer's on
        if (caret >= 0)
        {
            var boundary = mouseX < from.X + (left + right) / 2f ? caret - 1 : caret;

            if (pressed)
                DragAnchor = DragHead = boundary;
            else if (DragAnchor >= 0 && ImGui.IsMouseDown(ImGuiMouseButton.Left))
                DragHead = boundary;
        }

        // TildeTools
        // A press and release on the same letter is a click, as each letter's Selectable had it
        if (released && caret >= 0 && caret == PressedCaret)
        {
            SelectedCursorPos = caret;
            InputHandler.FocusedPreview = true;
        }

        // TildeTools
        // Caret on the letter's trailing edge. Nothing over a marker, clicking one does nothing
        if (caret >= 0 && hovered)
        {
            var edge = from.X + right;
            ImGui.GetWindowDrawList().AddLine(
                new Vector2(edge, from.Y), new Vector2(edge, to.Y), ImGui.GetColorU32(ImGuiCol.Text), ImGuiHelpers.GlobalScale);
        }

        if (MisspelledWordAt(start + k) is { } misspelled && ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            var first = start + k;
            while (first > 0 && ReferenceEquals(SpellMarks[first - 1], misspelled))
                first--;

            InputHandler.Spelling.SetPendingWord(misspelled, SourceIndexOf(SplitIndex, first));

            // TildeTools
            // Flagged, not opened: a popup is found by the id stack it was opened under, and this is in a child
            OpenSpellingPopup = true;
        }
    }

    // TildeTools
    private static readonly ConditionalWeakTable<string, string[]> Words = new();

    // TildeTools
    // Split once per chunk text, not every frame: at 11000 letters it was about 500 KB of garbage a frame
    // Keyed by the chunk's string instance, so an entry goes when its chunk does
    private static string[] WordsOf(string content) =>
        Words.GetValue(content, c => WordRegex().Matches(c).Select(m => m.Value).ToArray());

    // TildeTools
    // A word and the spaces after it are one item, half as many as a word and a space each
    // Only leading spaces stand alone
    [GeneratedRegex(@"\S+\s*|\s+")]
    private static partial Regex WordRegex();
}
