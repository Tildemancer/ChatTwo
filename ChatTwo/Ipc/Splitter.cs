// TildeTools: written for this fork, not part of upstream Chat 2.

using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

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

    private ICallGateSubscriber<int> ApiVersionGate { get; }
    private ICallGateSubscriber<int> InputByteCapGate { get; }
    private ICallGateSubscriber<string, int, bool> SendLineGate { get; }
    private ICallGateSubscriber<string, int, int> SendLineStatusGate { get; }
    private ICallGateSubscriber<string, int, List<string>> SplitLineGate { get; }
    private ICallGateSubscriber<string, int, List<int>> SplitSpansGate { get; }
    private ICallGateSubscriber<string, int, List<int>> SplitSourcesGate { get; }
    private ICallGateSubscriber<int> IntervalMsGate { get; }
    private ICallGateSubscriber<object?> AvailableGate { get; }

    private int CachedCap { get; set; } = DefaultByteCap;

    private int CachedInterval { get; set; }

    public Splitter()
    {
        ApiVersionGate = Plugin.Interface.GetIpcSubscriber<int>("TildeTools.Split.ApiVersion");
        InputByteCapGate = Plugin.Interface.GetIpcSubscriber<int>("TildeTools.Split.InputByteCap");
        SendLineGate = Plugin.Interface.GetIpcSubscriber<string, int, bool>("TildeTools.Split.SendLine");
        SendLineStatusGate = Plugin.Interface.GetIpcSubscriber<string, int, int>("TildeTools.Split.SendLineStatus");
        SplitLineGate = Plugin.Interface.GetIpcSubscriber<string, int, List<string>>("TildeTools.Split.SplitLine");
        SplitSpansGate = Plugin.Interface.GetIpcSubscriber<string, int, List<int>>("TildeTools.Split.SplitLineBodySpans");
        SplitSourcesGate = Plugin.Interface.GetIpcSubscriber<string, int, List<int>>("TildeTools.Split.SplitLineBodySources");
        IntervalMsGate = Plugin.Interface.GetIpcSubscriber<int>("TildeTools.Split.IntervalMs");
        AvailableGate = Plugin.Interface.GetIpcSubscriber<object?>("TildeTools.Split.Available");

        AvailableGate.Subscribe(Refresh);

        Refresh();
    }

    public int InputByteCap => CachedCap;

    public int IntervalMs => CachedInterval;

    public void Refresh()
    {
        CachedCap = QueryCap();
        CachedInterval = QueryInterval();
    }

    private int QueryInterval()
    {
        if (!IsAvailable)
            return 0;

        try
        {
            var interval = IntervalMsGate.InvokeFunc();
            return interval > 0 ? interval : 0;
        }
        catch
        {
            return 0;
        }
    }

    private int QueryCap()
    {
        try
        {
            if (ApiVersionGate.InvokeFunc() < RequiredApiVersion)
            {
                IsAvailable = false;
                return DefaultByteCap;
            }

            IsAvailable = true;
            var cap = InputByteCapGate.InvokeFunc();

            return cap < DefaultByteCap ? DefaultByteCap : cap;
        }
        catch
        {
            IsAvailable = false;
            return DefaultByteCap;
        }
    }

    // Recorded, not inferred from the cap: a splitter reporting exactly 500 would look absent
    public bool IsAvailable { get; private set; }

    // Outside the span is the splitter's own. Empty when it's too old to say
    public List<(int Start, int Length)> BodySpans(string line)
    {
        try
        {
            var flat = SplitSpansGate.InvokeFunc(line, DefaultByteCap);
            if (flat is null || flat.Count % 2 != 0)
                return [];

            var spans = new List<(int, int)>(flat.Count / 2);
            for (var i = 0; i + 1 < flat.Count; i += 2)
                spans.Add((flat[i], flat[i + 1]));

            return spans;
        }
        catch
        {
            // An older splitter lacks the gate. Mark everything, as before
            return [];
        }
    }

    public List<int> BodySources(string line)
    {
        try
        {
            return SplitSourcesGate.InvokeFunc(line, DefaultByteCap) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public List<string>? Split(string line)
    {
        try
        {
            var parts = SplitLineGate.InvokeFunc(line, DefaultByteCap);
            return parts is { Count: > 0 } ? parts : null;
        }
        catch
        {
            return null;
        }
    }

    public bool TrySend(string line)
    {
        try
        {
            return SendLineGate.InvokeFunc(line, DefaultByteCap);
        }
        catch
        {
            return false;
        }
    }

    // Yes/no can't tell "not mine" from "refused", and we sent a refused line ourselves.
    // An older splitter's no still means send it
    public SplitTake Offer(string line)
    {
        try
        {
            var status = SendLineStatusGate.InvokeFunc(line, DefaultByteCap);

            return status switch
            {
                1 => SplitTake.Queued,
                2 => SplitTake.Refused,
                _ => SplitTake.NotTaken,
            };
        }
        catch (IpcNotReadyError)
        {
            // A splitter from before SendLineStatus, yes/no is all it has
            return TrySend(line) ? SplitTake.Queued : SplitTake.NotTaken;
        }
        catch (Exception ex)
        {
            // The gate's there and threw. It may have queued first, so SendLine could send twice. Keep it in the box
            Plugin.Log.Error(ex, "The splitter failed on a line; kept it in the box.");
            Plugin.ChatGui.PrintError("[Chat 2] The splitter hit an error, so that message was kept in the box.");
            return SplitTake.Refused;
        }
    }

    public void Dispose()
    {
        AvailableGate.Unsubscribe(Refresh);
    }
}
