// TildeTools: written for this fork, not part of upstream Chat 2.

using Dalamud.Plugin.Ipc;

namespace ChatTwo.Ipc;

/// <summary>
/// Talks to a plugin that splits over-length messages, if one is installed.
/// If none is, Chat 2 just carries on with its normal behaviour.
/// </summary>
public sealed class Splitter : IDisposable
{
    /// <summary>Bytes, not characters. This is the limit the game itself enforces.</summary>
    public const int DefaultByteCap = 500;

    /// <summary>The API version this was written against.</summary>
    private const int RequiredApiVersion = 1;

    private ICallGateSubscriber<int> ApiVersionGate { get; }
    private ICallGateSubscriber<int> InputByteCapGate { get; }
    private ICallGateSubscriber<string, int, bool> SendLineGate { get; }
    private ICallGateSubscriber<string, int, List<string>> SplitLineGate { get; }
    private ICallGateSubscriber<int> IntervalMsGate { get; }
    private ICallGateSubscriber<object?> AvailableGate { get; }

    /// <summary>Cached, because the input box gets drawn every frame.</summary>
    private int CachedCap { get; set; } = DefaultByteCap;

    /// <summary>Cached alongside the cap, and zero while no splitter is answering.</summary>
    private int CachedInterval { get; set; }

    public Splitter()
    {
        ApiVersionGate = Plugin.Interface.GetIpcSubscriber<int>("TildeTools.Split.ApiVersion");
        InputByteCapGate = Plugin.Interface.GetIpcSubscriber<int>("TildeTools.Split.InputByteCap");
        SendLineGate = Plugin.Interface.GetIpcSubscriber<string, int, bool>("TildeTools.Split.SendLine");
        SplitLineGate = Plugin.Interface.GetIpcSubscriber<string, int, List<string>>("TildeTools.Split.SplitLine");
        IntervalMsGate = Plugin.Interface.GetIpcSubscriber<int>("TildeTools.Split.IntervalMs");
        AvailableGate = Plugin.Interface.GetIpcSubscriber<object?>("TildeTools.Split.Available");

        // Fires when the splitter loads after Chat 2.
        AvailableGate.Subscribe(Refresh);

        Refresh();
    }

    public int InputByteCap => CachedCap;

    /// <summary>
    /// Milliseconds the splitter leaves between parts, or zero when nothing is
    /// answering. The first part goes out at once, so a message of n parts takes
    /// n-1 of these.
    /// </summary>
    public int IntervalMs => CachedInterval;

    /// <summary>Re-reads the limit. Call when the plugin list changes.</summary>
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

            // Don't shrink below the game's limit just because another plugin says so.
            return cap < DefaultByteCap ? DefaultByteCap : cap;
        }
        catch
        {
            // No splitter, or an incompatible version.
            IsAvailable = false;
            return DefaultByteCap;
        }
    }

    /// <summary>
    /// True when a compatible splitter is answering. We record this rather than work it
    /// out from the cap, since a splitter reporting exactly the game's own limit would
    /// look like no splitter at all.
    /// </summary>
    public bool IsAvailable { get; private set; }

    /// <summary>Preview of how a chat line would divide up. Null if the splitter declined.</summary>
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

    /// <summary>Offers a chat line to the splitter. True means the splitter sends it, not us.</summary>
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

    public void Dispose()
    {
        AvailableGate.Unsubscribe(Refresh);
    }
}
