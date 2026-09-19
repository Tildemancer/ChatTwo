using Dalamud.Plugin.Ipc;

namespace ChatTwo.Ipc;

/// <summary>
/// Optional: talks to a plugin that splits over-length messages.
/// Falls back to Chat 2's normal behaviour when none is installed.
/// </summary>
public sealed class Splitter : IDisposable
{
    /// <summary>Bytes, NOT characters. The limit the game itself enforces.</summary>
    public const int DefaultByteCap = 500;

    /// <summary>The API version this was written against.</summary>
    private const int RequiredApiVersion = 1;

    private ICallGateSubscriber<int> ApiVersionGate { get; }
    private ICallGateSubscriber<int> InputByteCapGate { get; }
    private ICallGateSubscriber<string, int, bool> SendLineGate { get; }
    private ICallGateSubscriber<string, int, List<string>> SplitLineGate { get; }
    private ICallGateSubscriber<object?> AvailableGate { get; }

    /// <summary>Cached; the input box is drawn every frame.</summary>
    private int CachedCap { get; set; } = DefaultByteCap;

    public Splitter()
    {
        ApiVersionGate = Plugin.Interface.GetIpcSubscriber<int>("TildeTools.Split.ApiVersion");
        InputByteCapGate = Plugin.Interface.GetIpcSubscriber<int>("TildeTools.Split.InputByteCap");
        SendLineGate = Plugin.Interface.GetIpcSubscriber<string, int, bool>("TildeTools.Split.SendLine");
        SplitLineGate = Plugin.Interface.GetIpcSubscriber<string, int, List<string>>("TildeTools.Split.SplitLine");
        AvailableGate = Plugin.Interface.GetIpcSubscriber<object?>("TildeTools.Split.Available");

        // Fires when the splitter loads after Chat 2.
        AvailableGate.Subscribe(Refresh);

        Refresh();
    }

    /// <summary>How many bytes the message box should accept.</summary>
    public int InputByteCap => CachedCap;

    /// <summary>Re-reads the limit. Call when the plugin list changes.</summary>
    public void Refresh()
    {
        CachedCap = QueryCap();
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

            // NEVER shrink below the game's limit on another plugin's word.
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
    /// True when a compatible splitter is answering. Recorded, not inferred from
    /// the cap: a cap at exactly the game's own limit would read as "absent".
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
