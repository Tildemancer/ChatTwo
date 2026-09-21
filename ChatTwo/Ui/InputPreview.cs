using Dalamud.Interface.Colors;
using System.Numerics;
using System.Text;
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
    /// <summary>
    /// The input the preview was last built from. Compared as text rather than length,
    /// since swapping one letter for another leaves the length alone.
    /// </summary>
    private string LastInput = string.Empty;
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
            PreviewMessage = null;
            HasEvaluation = false;

            // TildeTools
            // A drag ends when the button comes up, which is only noticed while drawing.
            // Stop drawing mid-drag and the drag state survives into the next preview.
            DragAnchor = -1;
            DragHead = -1;

            return;
        }

        // TildeTools
        // Complete when the last keystroke was a space. That is the checker's signal
        // that a word is done, and the Trim below would throw it away.
        var typed = InputHandler.ChatInput;
        SetSpellSource(typed.Trim(), complete: typed.Length > 0 && char.IsWhiteSpace(typed[^1]));

        if (PreviewMessage == null || LastInput != InputHandler.ChatInput)
        {
            LastInput = InputHandler.ChatInput;

            PreviewMessage = BuildMessage(InputHandler.ChatInput.Trim());
        }

        HasEvaluation = !Plugin.Config.OnlyPreviewIf || PreviewMessage.Content.Count > 1;

        UpdateSplitParts();
    }

    // TildeTools
    /// <summary>Renders text the way a chat line is rendered: payloads, icons and all.</summary>
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
    /// <summary>The parts a splitter would break this message into, if any.</summary>
    private List<string>? SplitParts;

    // TildeTools
    /// <summary>Those same parts as messages, so each previews the way it will arrive.</summary>
    private List<Message>? SplitMessages;

    private string LastSplitInput = string.Empty;

    // TildeTools
    /// <summary>The typed-text span within each part, empty when the splitter cannot say.</summary>
    private List<(int Start, int Length)> SplitBodies = [];

    // TildeTools
    /// <summary>True while laying out off-screen to measure the height.</summary>
    private bool Measuring;

    // TildeTools
    /// <summary>
    /// Asks the splitter how the message divides up, but only when it changes. It is a
    /// cross-plugin call and the preview draws every frame, so asking each time adds up.
    /// </summary>
    private void UpdateSplitParts()
    {
        var line = InputHandler.ComposedLine;
        if (line == LastSplitInput)
            return;

        LastSplitInput = line;
        SplitParts = null;
        SplitMessages = null;
        SplitBodies = [];

        if (!InputHandler.Plugin.Splitter.IsAvailable || line.Length == 0)
            return;

        // TildeTools
        // One part means nothing is being split.
        var parts = InputHandler.Plugin.Splitter.Split(line);
        if (parts is not { Count: > 1 })
            return;

        SplitParts = parts;
        SplitMessages = [.. parts.Select(BuildMessage)];

        // TildeTools
        // Which slice of each part is text you typed. The rest is the splitter's
        // markers, and underlining those as misspellings is noise.
        SplitBodies = InputHandler.Plugin.Splitter.BodySpans(line);

        // TildeTools
        // And where each slice came from in the line, so right-clicking a word corrects
        // that word rather than the first one spelled like it. Empty when the splitter
        // cannot map it.
        SplitSources = InputHandler.Plugin.Splitter.BodySources(line);

    }

    // TildeTools
    /// <summary>Where each part's body begins in <see cref="InputHandler.ComposedLine"/>.</summary>
    private List<int> SplitSources = [];

    // TildeTools
    /// <summary>Which split part is being drawn, or -1 for the box's own text.</summary>
    private int SplitIndex = -1;

    // TildeTools
    /// <summary>Boundary the drag started from, in box positions, or -1 when not dragging.</summary>
    private int DragAnchor = -1;

    // TildeTools
    /// <summary>Boundary the drag has reached.</summary>
    private int DragHead = -1;

    // TildeTools
    /// <summary>A range to select in the box, handed over once the drag is let go.</summary>
    public int SelectedRangeStart = -1;
    public int SelectedRangeEnd = -1;

    // TildeTools
    /// <summary>Whether a letter ending at this boundary falls inside the live drag.</summary>
    private bool InDrag(int caret) =>
        caret >= 0 && DragAnchor >= 0 && DragHead >= 0 &&
        caret - 1 >= Math.Min(DragAnchor, DragHead) &&
        caret <= Math.Max(DragAnchor, DragHead);

    // TildeTools
    /// <summary>
    /// Whether this letter is shown as selected: inside a drag happening here, or
    /// inside the box's own selection.
    ///
    /// Both are in box positions, so one comparison does for both. The box's selection
    /// is a frame behind, since the preview draws first.
    /// </summary>
    private bool InSelection(int caret)
    {
        if (InDrag(caret))
            return true;

        var (from, to) = InputHandler.Spelling.InputSelection;

        return caret >= 0 && from >= 0 && caret - 1 >= from && caret <= to;
    }

    // TildeTools
    /// <summary>
    /// Where the caret goes for a click on the letter ending at <paramref name="afterLetter"/>
    /// in the drawn text, or -1 when the click should do nothing.
    ///
    /// The caret lands after the letter, as a text box does, so the hover mark is drawn
    /// on the letter's trailing edge rather than over it.
    /// </summary>
    private int CaretTargetFor(int afterLetter)
    {
        if (MapsToInput)
            return afterLetter;

        // A split part carries a channel command and markers of ours, so a position in
        // it means nothing to the box until it has been traced back.
        var mapped = SourceIndexOf(SplitIndex, afterLetter - 1);
        return mapped < 0 ? -1 : mapped + 1;
    }

    // TildeTools
    /// <summary>
    /// Turns a position inside one split part into a position in the input box, or -1
    /// when it cannot be done.
    ///
    /// The composed line is the input with a channel command or tell target on the
    /// front, and a part's body is a trimmed slice of that. The route is part -> body
    /// -> composed line -> box. Any step that cannot be made returns -1 rather than
    /// guessing.
    /// </summary>
    private int SourceIndexOf(int partIndex, int positionInPart)
    {
        var typedText = InputHandler.ChatInput;
        var leading = typedText.Length - typedText.TrimStart().Length;

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

        // ComposeLine only ever prepends, and always to the TRIMMED input, so the gap
        // between the two is fixed and the leading spaces have to be added back.
        var prefix = InputHandler.ComposedLine.Length - typedText.Trim().Length;

        var index = composed - prefix + leading;
        return index >= 0 && index < typedText.Length ? index : -1;
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
    /// <summary>
    /// Moves the preview beside the chat window when it will not fit above or below,
    /// preferring the right.
    /// </summary>
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
        // Level with the chat window, sliding up only as far as it must.
        var top = Math.Clamp(windowPos.Y, 0, Math.Max(0, screen.Y - PreviewHeight));

        return new Vector2(Math.Clamp(x, 0, Math.Max(0, screen.X - previewWidth)), top);
    }

    public override void Draw()
    {
        CalculatePreview();
        DrawPreview();
    }

    // TildeTools
    /// <summary>The width of one column, which is the chat window's own width.</summary>
    private float ColumnWidth = 200f;

    public float PreviewWidth;

    // TildeTools
    /// <summary>Which parts go in which column, filled top to bottom then left to right.</summary>
    private readonly List<List<int>> Columns = [];

    public void CalculatePreview()
    {
        // We Pre-draw this once to get the actual height :HideThePain:
        PreviewHeight = 0;

        // TildeTools
        // The width text gets, inside the window's padding.
        var sidePadding = ImGui.GetStyle().WindowPadding.X * 2;
        ColumnWidth = Math.Max(120f, InputHandler.MainWindow.LastWindowSize.X - sidePadding);
        PreviewWidth = ColumnWidth + sidePadding;
        Columns.Clear();

        var padding = IsWindowMode ? ImGui.GetStyle().WindowPadding.Y * 2 : 0;

        // TildeTools
        // Nothing interactive during the measure. Duplicate ids would fight with the real draw.
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
    /// <summary>
    /// Measures each part, then fills columns with them, spilling into a new column
    /// to the right rather than off the bottom of the screen.
    /// </summary>
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
        // Nothing measured, but there are parts to show: one column, full height.
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
            // A part taller than the screen overflows its column rather than looping forever.
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
    /// <summary>
    /// Draws a block into a hidden child of one column's width and reports its height.
    /// It has to be that width, since text wraps against the region it is drawn in.
    /// </summary>
    private float MeasureColumn(Action draw)
    {
        var restore = ImGui.GetCursorPos();
        var height = 0f;

        // TildeTools
        // Left at the cursor, one pixel tall. Move the child outside its parent and
        // ImGui culls it, and a culled child measures zero. Oops.
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
        // Back to the top, so the real drawing covers the sliver left behind.
        ImGui.SetCursorPos(restore);
        return height;
    }

    public void DrawPreview()
    {
        // TildeTools
        // A drag ends wherever the button came up, often nowhere near a letter, so it
        // is finished here rather than in the letter loop. Handed over only on release:
        // setting it every frame drags keyboard focus into the box mid-drag.
        if (DragAnchor >= 0 && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            if (DragHead >= 0 && DragHead != DragAnchor)
            {
                SelectedRangeStart = Math.Min(DragAnchor, DragHead);
                SelectedRangeEnd = Math.Max(DragAnchor, DragHead);
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
                DrawChunksPreview(PreviewMessage!.Content, InputHandler.PayloadHandler, unique: 10000);
            }

            DrawSpellingPopup();
            return;
        }

        // TildeTools
        // A tooltip sizes itself, so one column. Window-mode columns linger after a
        // switch to tooltip, so check the mode as well as the count.
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
            // Only above the first column: it names the whole preview.
            if (c == 0)
                DrawSplitHeader();

            foreach (var part in Columns[c])
                DrawSplitPart(part, InputHandler.PayloadHandler);
        }

        DrawSpellingPopup();
    }

    // TildeTools
    /// <summary>
    /// The text spelling positions are counted against: the trimmed input, or one
    /// part of a split message while that part is drawn.
    /// </summary>
    private string SpellSource = string.Empty;

    // TildeTools
    /// <summary>The body span those marks were built for, part of the cache key.</summary>
    private (int Start, int Length)? SpellBody;

    // TildeTools
    /// <summary>
    /// The misspelled word at each character of <see cref="SpellSource"/>, or null.
    /// Built once per source change, since doing it as you go would be a cross-plugin
    /// call for every letter.
    /// </summary>
    private string?[] SpellMarks = [];

    // TildeTools
    /// <summary>
    /// Points the marks at a piece of text.
    ///
    /// <paramref name="complete"/> says the text is not being typed into any more. The
    /// checker leaves the final word alone unless trailing whitespace says it is
    /// finished, and the text drawn here is trimmed, so that signal is gone. Without
    /// this the preview trails a word behind the box, and the last word of every split
    /// part is never checked.
    /// </summary>
    private void SetSpellSource(string text, bool complete = false, (int Start, int Length)? body = null)
    {
        // TildeTools
        // The span is part of the key: the same part text with a different body slice
        // needs different marks. Without it, passing a span rebuilds every frame, and a
        // rebuild is a cross-plugin call per part per frame.
        if (ReferenceEquals(SpellSource, text) && SpellMarks.Length == text.Length && SpellBody == body)
            return;

        SpellSource = text;
        SpellBody = body;
        SpellMarks = new string?[text.Length];

        // TildeTools
        // Only the typed part is checked. The markers around it are ours, and come back
        // as misspellings otherwise.
        var from = 0;
        var to = text.Length;

        if (body is { } span && span.Length > 0 && span.Start >= 0 && span.Start + span.Length <= text.Length)
        {
            from = span.Start;
            to = span.Start + span.Length;
        }

        // TildeTools
        // Padded only for the check. The marks still line up with the drawn text, and
        // the extra position is never read.
        //
        // Unfinished, it is cut at the end of the body instead. The checker takes the
        // last character it was handed as the signal, and on the closing part that
        // character is ours: the "]" of a final marker, or an OOC bracket. Every word
        // then looked finished, and the half-typed one got a line under it.
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
    /// <summary>
    /// Whether the last typed word is finished, by the same rule the checker uses.
    /// Whitespace alone was not enough: close an OOC note and the last character typed
    /// is a bracket, which the splitter lifts off the body, so the preview stopped
    /// marking a word the box still marked.
    /// </summary>
    private static bool FinishedTyping(string typed) =>
        typed.Length > 0 &&
        (char.IsWhiteSpace(typed[^1]) ||
         (char.IsPunctuation(typed[^1]) && typed[^1] is not ('\'' or '-')));

    // TildeTools
    /// <summary>
    /// Whether the drawn text IS the box's text, so a position in it needs no working
    /// out. False while a split part is being drawn, where the same click has to be
    /// traced back through <see cref="SourceIndexOf"/> instead.
    /// </summary>
    private bool MapsToInput = true;

    // TildeTools
    /// <summary>The misspelled word at a position in the drawn text, or null.</summary>
    private string? MisspelledWordAt(int position) =>
        position >= 0 && position < SpellMarks.Length ? SpellMarks[position] : null;

    // TildeTools
    /// <summary>Set when a marked letter is right-clicked, wherever it was drawn.</summary>
    private bool OpenSpellingPopup;

    private void DrawSpellingPopup()
    {
        // TildeTools
        // Opened here, not at the click, so the id matches the BeginPopup below.
        if (OpenSpellingPopup)
        {
            OpenSpellingPopup = false;
            ImGui.OpenPopup("##preview-spelling");
        }

        using var popup = ImRaii.Popup("##preview-spelling");
        if (!popup.Success)
            return;

        // TildeTools
        // Corrections apply to the real input, not the trimmed copy.
        InputHandler.Spelling.DrawContextEntries(ref InputHandler.ChatInput);
    }

    // TildeTools
    /// <summary>
    /// Heads the per-part preview, which replaces the whole-message one rather than
    /// sitting above it.
    /// </summary>
    private void DrawSplitHeader()
    {
        if (SplitParts is not { } parts)
            return;

        // TildeTools
        // Only the gaps are waited on: the first part goes out the moment you press enter.
        var seconds = (parts.Count - 1) * InputHandler.Plugin.Splitter.IntervalMs / 1000f;

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
        // Both get saved. The marks belong to the source, so they MUST go back with it.
        var previousSource = SpellSource;
        var previousMarks = SpellMarks;
        var previousSplitIndex = SplitIndex;
        MapsToInput = false;

        try
        {
            // TildeTools
            // Which part is being drawn, so a right-click in it can be traced back to
            // the place in the box it came from.
            SplitIndex = index;

            // TildeTools
            // Every part but the last was cut to length, so its words are all finished.
            // The last one is the end of what you are typing, and that word is not.
            var typed = InputHandler.ChatInput;
            var lastPart = index == parts.Count - 1;

            SetSpellSource(
                parts[index],
                complete: !lastPart || FinishedTyping(typed),
                body: index < SplitBodies.Count ? SplitBodies[index] : null);

            // TildeTools
            // Ids spaced well apart per part, so the same letter in two parts is two items.
            DrawChunksPreview(messages[index].Content, handler, unique: 100000 * (index + 1));
        }
        finally
        {
            SpellSource = previousSource;
            SpellMarks = previousMarks;
            SplitIndex = previousSplitIndex;
            MapsToInput = true;
        }
    }

    private void DrawChunksPreview(IReadOnlyList<Chunk> chunks, PayloadHandler? handler = null, float lineWidth = 0f, int unique = 0)
    {
        CursorPosition = 0;

        using var style = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
        for (var i = 0; i < chunks.Count; i++)
        {
            if (chunks[i] is TextChunk text && string.IsNullOrEmpty(text.Content))
                continue;

            DrawChunkPreview(chunks[i], handler, lineWidth, unique);

            if (i < chunks.Count - 1)
            {
                ImGui.SameLine();
            }
            else if (chunks[i].Link is EmotePayload && Plugin.Config.ShowEmotes)
            {
                // TildeTools
                // Emote payloads add no newline of their own, which breaks non-modern
                // mode.
                ImGui.SameLine();
                ImGui.TextUnformatted("");
            }
        }
    }

    private void DrawChunkPreview(Chunk chunk, PayloadHandler? handler = null, float lineWidth = 0f, int unique = 0)
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
            // TextWrap does not work for emotes, so wrap manually.
            if (ImGui.GetContentRegionAvail().X < emoteSize.X)
                ImGui.NewLine();

            // TildeTools
            // Dummy while loading. On failure, fall through to the name.
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
        // Every letter is a Selectable so it can be clicked, and a Selectable paints a
        // box behind itself on hover. That box sat ON the letter while the caret goes
        // AFTER it, so the hover colour is cleared and a caret is drawn where the click
        // would land.
        //
        // The selected colour stays and does the drag highlight. Drawing it ourselves
        // meant a filled rect OVER the glyph, tinting the letter. The Selectable puts
        // it behind, the way a text selection looks everywhere else.
        using var letterColours = ImRaii
            .PushColor(ImGuiCol.Header, ImGui.GetColorU32(ImGuiCol.TextSelectedBg))
            .Push(ImGuiCol.HeaderHovered, 0u)
            .Push(ImGuiCol.HeaderActive, 0u);

        foreach (var word in WhitespaceRegex().Split(text.Content).Where(s => s != string.Empty))
        {
            var wordSize = ImGui.CalcTextSize(word);
            if (ImGui.GetContentRegionAvail().X < wordSize.X)
                ImGui.NewLine();

            foreach (var letter in word)
            {
                var letterSize = ImGui.CalcTextSize(letter.ToString());

                CursorPosition++;

                // TildeTools
                // Worked out before the letter is drawn, so everything below uses the
                // same value.
                var caret = CaretTargetFor(CursorPosition);

                var clicked = ImGui.Selectable(
                    $"{letter}##{CursorPosition + unique}", InSelection(caret), ImGuiSelectableFlags.None, letterSize);

                // TildeTools
                // Drag to select. Boundary is whichever half of the letter the pointer
                // is on, so a drag starting inside a word starts where you aimed.
                if (caret >= 0 && !Measuring)
                {
                    var from = ImGui.GetItemRectMin();
                    var to = ImGui.GetItemRectMax();
                    var mouse = ImGui.GetIO().MousePos;

                    var over = mouse.X >= from.X && mouse.X <= to.X &&
                               mouse.Y >= from.Y && mouse.Y <= to.Y;

                    if (over)
                    {
                        var boundary = mouse.X < (from.X + to.X) / 2f ? caret - 1 : caret;

                        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                            DragAnchor = DragHead = boundary;
                        else if (DragAnchor >= 0 && ImGui.IsMouseDown(ImGuiMouseButton.Left))
                            DragHead = boundary;
                    }
                }

                if (clicked && caret >= 0)
                {
                    SelectedCursorPos = caret;
                    InputHandler.FocusedPreview = true;
                }

                // TildeTools
                // A line where the caret will go, on the trailing edge of the letter.
                // Nothing over a marker, since clicking one does nothing.
                if (caret >= 0 && !Measuring && ImGui.IsItemHovered())
                {
                    var edge = ImGui.GetItemRectMax().X;
                    var top = ImGui.GetItemRectMin().Y;

                    ImGui.GetWindowDrawList().AddLine(
                        new Vector2(edge, top),
                        new Vector2(edge, ImGui.GetItemRectMax().Y),
                        ImGui.GetColorU32(ImGuiCol.Text),
                        ImGuiHelpers.GlobalScale);
                }

                if (MisspelledWordAt(CursorPosition - 1) is { } misspelled)
                {
                    SpellUnderline.UnderlineLastItem();

                    if (!Measuring && ImGui.IsItemClicked(ImGuiMouseButton.Right))
                    {
                        // TildeTools
                        // Back up to the start of the word. The click lands on a letter,
                        // and the correction needs where the word begins.
                        var start = CursorPosition - 1;
                        while (start > 0 && ReferenceEquals(SpellMarks[start - 1], misspelled))
                            start--;

                        InputHandler.Spelling.SetPendingWord(misspelled, SourceIndexOf(SplitIndex, start));

                        // TildeTools
                        // Flagged, not opened here: a popup is found by the id stack it
                        // was opened under, and this runs inside a child under a pushed id.
                        OpenSpellingPopup = true;
                    }
                }

                ImGui.SameLine();
            }
        }
        ImGui.NewLine();
    }

    [GeneratedRegex(@"(\s)")]
    private static partial Regex WhitespaceRegex();
}
