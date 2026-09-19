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
    /// because swapping one letter for another leaves the length alone and still needs
    /// a rebuild.
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

            return;
        }

        SetSpellSource( InputHandler.ChatInput.Trim() );

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
    /// <summary>True while laying out off-screen to measure the height.</summary>
    private bool Measuring;

    // TildeTools
    /// <summary>
    /// Asks the splitter how the message divides up, but only when it changes. It's a
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

        if (!InputHandler.Plugin.Splitter.IsAvailable || line.Length == 0)
            return;

        // TildeTools
        // One part means nothing is being split.
        var parts = InputHandler.Plugin.Splitter.Split(line);
        if (parts is not { Count: > 1 })
            return;

        SplitParts = parts;
        SplitMessages = [.. parts.Select(BuildMessage)];
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
        // The width text actually gets, inside the window's padding.
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
    /// It has to be that width, since text wraps against whatever region it's drawn in.
    /// </summary>
    private float MeasureColumn(Action draw)
    {
        var restore = ImGui.GetCursorPos();
        var height = 0f;

        // TildeTools
        // Left at the cursor, one pixel tall. Move the child outside its parent instead
        // and ImGui culls it, and a culled child measures as zero height. Oops.
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
        // A tooltip sizes itself, so it gets one column. The columns from
        // window mode linger after a switch to tooltip, so the mode gets checked as
        // well as the count.
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
    /// <summary>
    /// The misspelled word at each character of <see cref="SpellSource"/>, or null.
    /// Built once per source change, since doing it as you go would be a cross-plugin
    /// call for every letter.
    /// </summary>
    private string?[] SpellMarks = [];

    private void SetSpellSource(string text)
    {
        if (ReferenceEquals(SpellSource, text) && SpellMarks.Length == text.Length)
            return;

        SpellSource = text;
        SpellMarks = new string?[text.Length];

        foreach (var misspelling in InputHandler.Plugin.SpellCheck.Check(text))
        {
            var word = misspelling.Word(text);
            if (word.Length == 0)
                continue;

            var end = Math.Min(text.Length, misspelling.Start + misspelling.Length);
            for (var i = Math.Max(0, misspelling.Start); i < end; i++)
                SpellMarks[i] = word;
        }
    }

    // TildeTools
    /// <summary>Whether clicking a letter should move the cursor in the input box.</summary>
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
        MapsToInput = false;

        try
        {
            // TildeTools
            // Checked against the part, so marks need no offset mapping.
            SetSpellSource(parts[index]);

            // TildeTools
            // Ids spaced well apart per part, so the same letter in two parts is two items.
            DrawChunksPreview(messages[index].Content, handler, unique: 100000 * (index + 1));
        }
        finally
        {
            SpellSource = previousSource;
            SpellMarks = previousMarks;
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
                // Emote payloads don't add newlines themselves, which breaks
                // non-modern mode.
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
            // TextWrap doesn't work for emotes, so wrap manually.
            if (ImGui.GetContentRegionAvail().X < emoteSize.X)
                ImGui.NewLine();

            // TildeTools
            // Dummy while loading; on failure fall through to the name.
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

        foreach (var word in WhitespaceRegex().Split(text.Content).Where(s => s != string.Empty))
        {
            var wordSize = ImGui.CalcTextSize(word);
            if (ImGui.GetContentRegionAvail().X < wordSize.X)
                ImGui.NewLine();

            foreach (var letter in word)
            {
                var letterSize = ImGui.CalcTextSize(letter.ToString());

                CursorPosition++;
                if (ImGui.Selectable($"{letter}##{CursorPosition + unique}", false, ImGuiSelectableFlags.None, letterSize)
                    && MapsToInput)
                {
                    // TildeTools
                    // Only when the drawn text is the input itself: a split part
                    // carries markers the box does not.
                    SelectedCursorPos = CursorPosition;
                    InputHandler.FocusedPreview = true;
                }

                if (MisspelledWordAt(CursorPosition - 1) is { } misspelled)
                {
                    SpellUnderline.UnderlineLastItem();

                    if (!Measuring && ImGui.IsItemClicked(ImGuiMouseButton.Right))
                    {
                        InputHandler.Spelling.SetPendingWord(misspelled);

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
