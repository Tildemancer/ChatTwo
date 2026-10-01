using System.Numerics;
using System.Text;
// TildeTools: Removed three usings here and one below, only the code moved to InputPreview.TildeTools.cs used them.
using ChatTwo.Ui.Handler;
using ChatTwo.Util;
// TildeTools: The fourth, see above.
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
    private string LastInput = string.Empty;
    private string LastTrimmed = string.Empty;
    // TildeTools ends
    private Message? PreviewMessage;

    private int CursorPosition;
    private bool NextChunkIsAutoTranslate;

    // TildeTools: Removed SelectedCursorPos. Only the letter loop DrawWords replaced ever set it. SelectedRange actually does its job now.

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
            // TildeTools
            PreviewHeight = PreviewWidth = 0;
            // TildeTools ends
            PreviewMessage = null;
            HasEvaluation = false;

            return;
        }

        // TildeTools
        if (Plugin.Config.PreviewPosition is PreviewPosition.None)
        {
            HasEvaluation = false;
            return;
        }

        if (PreviewMessage == null || LastInput != InputHandler.ChatInput)
        {
            LastInput = InputHandler.ChatInput;
            LastTrimmed = LastInput.Trim();

            // Past the cap, only the parts draw.
            // Parsing 18k characters costs 28-35 ms, since ReplaceWithPayload copies bytes[i..] per byte!
            PreviewMessage = BuildMessage(Encoding.UTF8.GetByteCount(LastTrimmed) > Ipc.Splitter.DefaultByteCap ? string.Empty : LastTrimmed);
        }

        UpdateSplitParts();

        // Uses the untrimmed input, since Trim drops the trailing space that marks a word done.
        // Split parts check themselves.
        if (SplitMessages is null && PreviewMessage.Content.Count > 0)
            SetSpellSource(LastTrimmed, complete: char.IsWhiteSpace(InputHandler.ChatInput[^1]));
        else
            SpellMarks = [];

        HasEvaluation = !Plugin.Config.OnlyPreviewIf || PreviewMessage.Content.Count > 1 || SplitHasEvaluation;
        // TildeTools ends
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

        // TildeTools
        var width = PreviewWidth > 0 ? PreviewWidth : size.X;

        // PreviewWidth and PreviewHeight are in pixels, and Window scales Size again, so divide it out...
        Size = new Vector2(width, PreviewHeight) / Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
        // TildeTools ends

        var y = Plugin.Config.PreviewPosition switch
        {
            PreviewPosition.Top => pos.Y - PreviewHeight,
            PreviewPosition.Bottom => pos.Y + size.Y,
            _ => throw new ArgumentOutOfRangeException(nameof(Plugin.Config.PreviewPosition), Plugin.Config.PreviewPosition, null),
        };

        // TildeTools
        Position = KeepOnScreen(y, pos, size.X, width);
        // TildeTools ends
        PositionCondition = ImGuiCond.Always;
    }

    public override void Draw()
    {
        CalculatePreview();
        DrawPreview();
    }

    // TildeTools
    // CalculatePreview and DrawPreview are in InputPreview.TildeTools.cs.
    private void DrawChunksPreview(IReadOnlyList<Chunk> chunks, PayloadHandler? handler = null, float lineWidth = 0f)
    // TildeTools ends
    {
        CursorPosition = 0;

        using var style = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
        for (var i = 0; i < chunks.Count; i++)
        {
            if (chunks[i] is TextChunk text && string.IsNullOrEmpty(text.Content))
                continue;

            // TildeTools
            DrawChunkPreview(chunks[i], handler, lineWidth);
            // TildeTools ends

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

    // TildeTools
    private void DrawChunkPreview(Chunk chunk, PayloadHandler? handler = null, float lineWidth = 0f)
    // TildeTools ends
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

        // TildeTools
        DrawWords(text.Content, handler);
        // TildeTools ends
        ImGui.NewLine();
    }

    // TildeTools: WhitespaceRegex went with the letter-by-letter loop. See DrawWords
}
