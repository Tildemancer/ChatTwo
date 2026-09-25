// TildeTools: written for this fork, not part of upstream Chat 2.

using System.Text;
using ChatTwo.Util;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using static ChatTwo.Ipc.SpellCheck;

namespace ChatTwo.Ipc;

public enum SplitTake
{
    // Send it ourselves
    NotTaken,

    // Send nothing, the box can be cleared
    Queued,

    // Send nothing and keep the text, it's the only copy
    Refused,
}

public sealed class Splitter : IDisposable
{
    public const int DefaultByteCap = 500;

    private const int RequiredApiVersion = 1;

    private readonly ICallGateSubscriber<int> ApiVersionGate = Plugin.Interface.GetIpcSubscriber<int>("TildeTools.Split.ApiVersion");
    private readonly ICallGateSubscriber<int> InputByteCapGate = Plugin.Interface.GetIpcSubscriber<int>("TildeTools.Split.InputByteCap");
    private readonly ICallGateSubscriber<string, int, bool> SendLineGate = Plugin.Interface.GetIpcSubscriber<string, int, bool>("TildeTools.Split.SendLine");
    private readonly ICallGateSubscriber<string, int, int> SendLineStatusGate = Plugin.Interface.GetIpcSubscriber<string, int, int>("TildeTools.Split.SendLineStatus");
    private readonly ICallGateSubscriber<string, int, List<string>> SplitLineGate = Plugin.Interface.GetIpcSubscriber<string, int, List<string>>("TildeTools.Split.SplitLine");
    private readonly ICallGateSubscriber<string, int, List<int>> SplitSpansGate = Plugin.Interface.GetIpcSubscriber<string, int, List<int>>("TildeTools.Split.SplitLineBodySpans");
    private readonly ICallGateSubscriber<string, int, List<int>> SplitSourcesGate = Plugin.Interface.GetIpcSubscriber<string, int, List<int>>("TildeTools.Split.SplitLineBodySources");
    private readonly ICallGateSubscriber<string, int, int> PostingMsGate = Plugin.Interface.GetIpcSubscriber<string, int, int>("TildeTools.Split.PostingMs");
    private readonly ICallGateSubscriber<object?> AvailableGate = Plugin.Interface.GetIpcSubscriber<object?>("TildeTools.Split.Available");

    public Splitter()
    {
        AvailableGate.Subscribe(Refresh);
        Refresh();
    }

    public int InputByteCap { get; private set; } = DefaultByteCap;

    // Recorded, not inferred from the cap: a splitter reporting exactly 500 would look absent
    public bool IsAvailable { get; private set; }

    // Bumped on every refresh, so a cached split knows the settings behind it changed
    public int Generation { get; private set; }

    private void Refresh()
    {
        var cap = Try<int?>(() => ApiVersionGate.InvokeFunc() >= RequiredApiVersion ? InputByteCapGate.InvokeFunc() : null, null);
        (IsAvailable, InputByteCap) = (cap != null, Math.Max(cap ?? DefaultByteCap, DefaultByteCap));
        Generation++;
    }

    // How long its parts take to go out, 0 when the splitter's too old to say
    public int PostingMs(string line) => Try(() => PostingMsGate.InvokeFunc(line, DefaultByteCap), 0);

    // Outside the span is the splitter's own
    // Empty when it's too old to say
    // An older splitter lacks the gate. Mark everything, as before
    public List<(int Start, int Length)> BodySpans(string line)
    {
        List<(int, int)> spans = [];
        if (Try<List<int>?>(() => SplitSpansGate.InvokeFunc(line, DefaultByteCap), null) is { } flat && flat.Count % 2 == 0)
            for (var i = 0; i + 1 < flat.Count; i += 2)
                spans.Add((flat[i], flat[i + 1]));

        return spans;
    }

    public List<int> BodySources(string line) => Try<List<int>?>(() => SplitSourcesGate.InvokeFunc(line, DefaultByteCap), null) ?? [];

    public List<string>? Split(string line) => Try<List<string>?>(() => SplitLineGate.InvokeFunc(line, DefaultByteCap), null);

    // Yes/no can't tell "not mine" from "refused", and we sent a refused line ourselves.
    // An older splitter's no still means send it
    public SplitTake Offer(string line)
    {
        try
        {
            return SendLineStatusGate.InvokeFunc(line, DefaultByteCap) switch
            {
                1 => SplitTake.Queued,
                2 => SplitTake.Refused,
                _ => SplitTake.NotTaken,
            };
        }
        catch (IpcNotReadyError)
        {
            // A splitter from before SendLineStatus, yes/no is all it has
            return Try(() => SendLineGate.InvokeFunc(line, DefaultByteCap), false) ? SplitTake.Queued : SplitTake.NotTaken;
        }
        catch (Exception ex)
        {
            // The gate's there and threw. It may have queued first, so SendLine could send twice, keep it in the box
            Plugin.Log.Error(ex, "The splitter failed on a line; kept it in the box.");
            Plugin.ChatGui.PrintError("[Chat 2] The splitter hit an error, so that message was kept in the box.");
            return SplitTake.Refused;
        }
    }

    // Over the cap the game drops a line silently, and the text with it, so it stays in the box
    public static bool KeptForLength(string line)
    {
        if (Encoding.UTF8.GetByteCount(line) <= DefaultByteCap)
            return false;

        Plugin.ChatGui.PrintError("[Chat 2] That message is too long to send this way, so it was kept in the box.");
        return true;
    }

    // On a copy, StartsWithCommand rewrites what it's given
    public static bool StartsWithTranslationCommand(string trimmed)
    {
        var bytes = Encoding.UTF8.GetBytes(trimmed);
        return AutoTranslate.StartsWithCommand(ref bytes);
    }

    public void Dispose() => AvailableGate.Unsubscribe(Refresh);
}
