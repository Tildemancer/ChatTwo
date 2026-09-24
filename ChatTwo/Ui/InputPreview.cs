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

        // TildeTools
        // A trailing space is the checker's done-signal, and Trim would throw it away
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
    private List<string>? SplitParts;

    // TildeTools
    private List<Message>? SplitMessages;

    private string LastSplitInput = string.Empty;
    private int LastSplitGeneration = -1;

    // TildeTools
    private List<(int Start, int Length)> SplitBodies = [];

    // TildeTools
    private bool Measuring;

    // TildeTools
    private void UpdateSplitParts()
    {
        var line = InputHandler.ComposedLine;
        var generation = InputHandler.Plugin.Splitter.Generation;
        if (line == LastSplitInput && generation == LastSplitGeneration)
            return;

        // TildeTools
        // A keystroke changes a part or two, but every part's Message was parsed again: about 6 ms at 18 parts
        // Unchanged parts keep theirs
        var built = new Dictionary<string, Message>();
        if (SplitParts is { } old)
            foreach (var (part, message) in old.Zip(SplitMessages!))
                built.TryAdd(part, message);

        LastSplitInput = line;
        LastSplitGeneration = generation;
        SplitParts = null;
        SplitMessages = null;
        SplitBodies = [];

        if (!InputHandler.Plugin.Splitter.IsAvailable || line.Length == 0)
            return;

        // TildeTools
        var parts = InputHandler.Plugin.Splitter.Split(line);
        if (parts is not { Count: > 1 })
            return;

        SplitParts = parts;
        SplitMessages = [.. parts.Select(part => built.GetValueOrDefault(part) ?? BuildMessage(part))];

        // TildeTools
        SplitBodies = InputHandler.Plugin.Splitter.BodySpans(line);

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
                DrawChunksPreview(PreviewMessage!.Content, InputHandler.PayloadHandler, unique: 10000);
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
    private string SpellSource = string.Empty;

    // TildeTools
    private (int Start, int Length)? SpellBody;

    // TildeTools
    // Built once per source, per letter would be a cross-plugin call each
    private string?[] SpellMarks = [];

    // TildeTools
    private readonly Dictionary<(string Text, bool Complete, (int Start, int Length)? Body), string?[]> MarksFor = [];

    // TildeTools
    private const int MostMarksToRemember = 64;

    // TildeTools
    private int MarksGeneration;

    // TildeTools
    // complete: not being typed into. The drawn text is trimmed, which loses the trailing-space
    // signal, so without this the last word of every part is never checked
    private void SetSpellSource(string text, bool complete = false, (int Start, int Length)? body = null)
    {
        // TildeTools
        // By value. By instance it rebuilt every frame: Trim makes a new string on a trailing space,
        // and split parts swap in and out
        SpellSource = text;
        SpellBody = body;

        // A word added or ignored changes the answers for text that has not changed.
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
        // The marks belong to the source, so they go back with it
        var previousSource = SpellSource;
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

            // TildeTools
            // Ids spaced per part, so the same letter in two parts is two items
            DrawChunksPreview(messages[index].Content, handler, unique: 100000 * (index + 1));
        }
        finally
        {
            SpellSource = previousSource;
            SpellMarks = previousMarks;
            SplitIndex = previousSplitIndex;
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
                // Emote payloads add no newline, which breaks non-modern mode
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
        // Each letter is a Selectable. Its hover box sat on the letter while the caret goes after it,
        // so hover is cleared and a caret drawn instead. The selected colour does the drag highlight
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
                var caret = CaretTargetFor(CursorPosition);

                var clicked = ImGui.Selectable(
                    $"{letter}##{CursorPosition + unique}", InSelection(caret), ImGuiSelectableFlags.None, letterSize);

                // TildeTools
                // Drag boundary is whichever half of the letter the pointer's on
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

                        // Hovered as well: a click on a window lying over the preview isn't ours
                        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && ImGui.IsItemHovered())
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
                // Caret on the letter's trailing edge. Nothing over a marker, clicking one does nothing
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
                        var start = CursorPosition - 1;
                        while (start > 0 && ReferenceEquals(SpellMarks[start - 1], misspelled))
                            start--;

                        InputHandler.Spelling.SetPendingWord(misspelled, SourceIndexOf(SplitIndex, start));

                        // TildeTools
                        // Flagged, not opened: a popup is found by the id stack it was opened under, and this is in a child
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
