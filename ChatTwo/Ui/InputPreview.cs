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

    /// <summary>
    /// The input the preview was last built from.
    ///
    /// Compared as text rather than by length: an edit that happens to keep the
    /// same length, such as correcting a misspelling, would otherwise leave the
    /// preview showing the old message.
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

        // What the letters drawn for the whole message are counted against, unless a
        // part temporarily stands in for it.
        SetSpellSource( InputHandler.ChatInput.Trim() );

        if (PreviewMessage == null || LastInput != InputHandler.ChatInput)
        {
            LastInput = InputHandler.ChatInput;

            PreviewMessage = BuildMessage(InputHandler.ChatInput.Trim());
        }

        HasEvaluation = !Plugin.Config.OnlyPreviewIf || PreviewMessage.Content.Count > 1;

        UpdateSplitParts();
    }

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

    /// <summary>The parts a splitter would break this message into, if any.</summary>
    private List<string>? SplitParts;

    /// <summary>
    /// Those same parts as messages, so each is previewed the way it will arrive
    /// rather than as a line of plain text beneath the real preview.
    /// </summary>
    private List<Message>? SplitMessages;

    private string LastSplitInput = string.Empty;

    /// <summary>True while laying out off-screen to measure the height.</summary>
    private bool Measuring;

    /// <summary>
    /// Asks the splitter how the message divides up, when it has changed. Only on
    /// change: this crosses to another plugin, and the preview draws every frame.
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

        // One part means nothing is being split, and a list of one tells the reader
        // nothing they cannot see in the box.
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

        // Several columns wide once the parts stop fitting in one.
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

    /// <summary>
    /// Moves the preview beside the chat window when it will not fit above or below
    /// it.
    ///
    /// A long message makes a tall preview, and a chat window near the top or bottom
    /// of the screen leaves nowhere for it to grow into, so it grows off the edge and
    /// the parts furthest from the window are the ones lost. Put beside the window
    /// instead it has the full height of the screen to use. It goes to whichever side
    /// has the room, preferring the right.
    /// </summary>
    private Vector2 KeepOnScreen(Vector2 wanted, Vector2 windowPos, float windowWidth, float previewWidth)
    {
        var screen = ImGui.GetIO().DisplaySize;

        if (wanted.Y >= 0 && wanted.Y + PreviewHeight <= screen.Y)
            return wanted;

        // Beside the chat window: to the right, unless the preview will not fit there
        // and will on the left.
        var right = windowPos.X + windowWidth;
        var fitsRight = right + previewWidth <= screen.X;
        var fitsLeft = windowPos.X - previewWidth >= 0;

        var x = fitsRight || !fitsLeft ? right : windowPos.X - previewWidth;

        // Beside is only an improvement if the whole of it is on screen, so it starts
        // level with the chat window and slides up only as far as it must.
        var top = Math.Clamp(windowPos.Y, 0, Math.Max(0, screen.Y - PreviewHeight));

        return new Vector2(Math.Clamp(x, 0, Math.Max(0, screen.X - previewWidth)), top);
    }

    public override void Draw()
    {
        CalculatePreview();
        DrawPreview();
    }

    /// <summary>The width of one column, which is the chat window's own width.</summary>
    private float ColumnWidth = 200f;

    /// <summary>How wide the whole preview is: one column, or several side by side.</summary>
    public float PreviewWidth;

    /// <summary>Which parts go in which column, filled top to bottom then left to right.</summary>
    private readonly List<List<int>> Columns = [];

    public void CalculatePreview()
    {
        // We Pre-draw this once to get the actual height :HideThePain:
        PreviewHeight = 0;

        // The width text actually gets, inside the window's padding. Measuring against
        // anything else gives heights that do not match how it will be drawn.
        var sidePadding = ImGui.GetStyle().WindowPadding.X * 2;
        ColumnWidth = Math.Max(120f, InputHandler.MainWindow.LastWindowSize.X - sidePadding);
        PreviewWidth = ColumnWidth + sidePadding;
        Columns.Clear();

        var padding = IsWindowMode ? ImGui.GetStyle().WindowPadding.Y * 2 : 0;

        // Nothing interactive during the measure: it happens off-screen every frame,
        // and a second set of the same ids would fight with the real one.
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

    /// <summary>
    /// Works out how tall each part is, then fills columns with them.
    ///
    /// A long enough message makes a preview taller than the screen, and a window
    /// cannot show what is past its own bottom edge. Rather than let the far end fall
    /// off, the parts continue in another column to the right, as many as it takes.
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

        // Nothing measured, but there are parts to show. Rather than size the window
        // to the header and hide them all, give them one column and the full height.
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
            // A part taller than the screen on its own still has to go somewhere, so
            // it starts a column and overflows it rather than looping forever.
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

    /// <summary>
    /// Runs a block of drawing off-screen, constrained to one column's width, and
    /// reports how tall it came out.
    ///
    /// Inside a child of that width, because text wraps against the region it is
    /// drawn in: measured against a window several columns wide, every part would
    /// come out shorter than it will really be.
    /// </summary>
    private float MeasureColumn(Action draw)
    {
        var restore = ImGui.GetCursorPos();
        var height = 0f;

        // Left where the cursor already is, not moved off somewhere out of the way:
        // a child placed outside its parent is culled, and a culled child draws
        // nothing and reports nothing, which measures every part as no height at all.
        // One pixel tall is enough to stay visible and clip everything inside it.
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

        // Back to the top, so the real drawing covers the sliver this left behind.
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

        // As a tooltip nothing measures the parts, because the tooltip sizes itself.
        // One column then, however long it comes out. The mode is checked as well as
        // the count, because columns worked out while the preview was a window are
        // still here after a switch to tooltip, and laying a tooltip out in columns
        // sized for the chat window collapses it to a sliver.
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

            // Only above the first column: it names the whole preview, not the column.
            if (c == 0)
                DrawSplitHeader();

            foreach (var part in Columns[c])
                DrawSplitPart(part, InputHandler.PayloadHandler);
        }

        DrawSpellingPopup();
    }

    /// <summary>
    /// The text the letters being drawn belong to, which spelling positions are
    /// counted against.
    ///
    /// For the whole-message preview this is the trimmed input, so offsets line up
    /// with the box. For one part of a split message it is that part, which is why no
    /// offset has to be mapped back: each part is checked in its own right, and a
    /// correction is applied to the input by the word rather than by position.
    /// </summary>
    private string SpellSource = string.Empty;

    /// <summary>
    /// The misspelled word at each character of <see cref="SpellSource"/>, or null.
    ///
    /// Worked out once when the source changes rather than per letter. The letters are
    /// drawn one at a time, the whole preview is laid out more than once a frame, and
    /// asking per letter meant a cross-plugin spellcheck per letter — which the single
    /// cached answer could not absorb, because each part of a split message evicted the
    /// one before it.
    /// </summary>
    private string?[] SpellMarks = [];

    /// <summary>Points the marks at a different piece of text, rebuilding them.</summary>
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

    /// <summary>Whether clicking a letter should move the cursor in the input box.</summary>
    private bool MapsToInput = true;

    /// <summary>The misspelled word at a position in the drawn text, or null.</summary>
    private string? MisspelledWordAt(int position) =>
        position >= 0 && position < SpellMarks.Length ? SpellMarks[position] : null;

    /// <summary>
    /// The correction menu for a word right-clicked in the preview. Its own popup
    /// rather than the input's, since this is a separate window with no menu of its
    /// own to add to.
    /// </summary>
    /// <summary>Set when a marked letter is right-clicked, wherever it was drawn.</summary>
    private bool OpenSpellingPopup;

    private void DrawSpellingPopup()
    {
        // Opened here rather than where the click happened, so the id matches the
        // BeginPopup below however deeply nested the letter was.
        if (OpenSpellingPopup)
        {
            OpenSpellingPopup = false;
            ImGui.OpenPopup("##preview-spelling");
        }

        using var popup = ImRaii.Popup("##preview-spelling");
        if (!popup.Success)
            return;

        // Corrections apply to the real input, not the trimmed copy the preview
        // is built from.
        InputHandler.Spelling.DrawContextEntries(ref InputHandler.ChatInput);
    }

    /// <summary>
    /// Previews the separate messages a long one will be sent as, each drawn the way
    /// a chat line is drawn.
    ///
    /// This replaces the whole-message preview rather than sitting under it. Showing
    /// both meant reading the same text twice, once as it is typed and once as it
    /// arrives, and only the second is what anyone will actually see.
    /// </summary>
    private void DrawSplitHeader()
    {
        if (SplitParts is { } parts)
            ImGui.TextDisabled($"Will be sent as {parts.Count} messages:");
    }

    /// <summary>Draws one part, numbered as it will be sent.</summary>
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

        // Both saved, not just the text: the marks belong to it, and putting the text
        // back without them would leave the message marked as though it were the part.
        var previousSource = SpellSource;
        var previousMarks = SpellMarks;
        MapsToInput = false;

        try
        {
            // Checked against the part rather than the input, so a mark sits under
            // the right letters without any offset having to be worked out.
            SetSpellSource(parts[index]);

            // Ids are spaced well apart per part, so the same letter in two parts
            // is two items rather than one that ImGui cannot tell apart.
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
                // Emote payloads seem to not automatically put newlines, which
                // is an issue when modern mode is disabled.
                ImGui.SameLine();
                // Use default ImGui behavior for newlines.
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

            // TextWrap doesn't work for emotes, so we have to wrap them manually
            if (ImGui.GetContentRegionAvail().X < emoteSize.X)
                ImGui.NewLine();

            // We only draw a dummy if it is still loading, in case it failed, we draw the actual name
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
                    // Only where a letter's place in the drawn text is its place in the
                    // box. A part of a split message carries markers the box does not,
                    // so its letters sit at no particular position in the input.
                    SelectedCursorPos = CursorPosition;
                    InputHandler.FocusedPreview = true;
                }

                // Each letter knows its place in the message, so a misspelled word
                // can be marked exactly where it sits rather than listed separately.
                if (MisspelledWordAt(CursorPosition - 1) is { } misspelled)
                {
                    SpellUnderline.UnderlineLastItem();

                    if (!Measuring && ImGui.IsItemClicked(ImGuiMouseButton.Right))
                    {
                        InputHandler.Spelling.SetPendingWord(misspelled);

                        // Asked for here and opened at the window's root, because a
                        // popup is found by the id stack it was opened under. A part is
                        // drawn inside a column child and under a pushed id, so opening
                        // it here would name a popup that the matching BeginPopup —
                        // which runs at the root, after the children close — can never
                        // find. That is why corrections stopped working once the
                        // preview grew columns.
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
