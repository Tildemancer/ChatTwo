using Dalamud.Plugin.Ipc;

namespace ChatTwo.Ipc;

/// <summary>
/// Optional cooperation with a plugin that splits over-length messages.
///
/// The game refuses a chat line longer than 500 bytes, so Chat 2 sizes its input
/// box to match. When a splitter is present it can accept more and hand anything
/// too long over to be cut up and sent in order.
///
/// Every call falls back to Chat 2's normal behaviour, so with no splitter
/// installed nothing here changes anything.
/// </summary>
public sealed class Splitter : IDisposable
{
    /// <summary>The limit the game itself enforces, and Chat 2's behaviour without a splitter.</summary>
    public const int DefaultByteCap = 500;

    /// <summary>The API version this was written against.</summary>
    private const int RequiredApiVersion = 1;

    private ICallGateSubscriber<int> ApiVersionGate { get; }
    private ICallGateSubscriber<int> InputByteCapGate { get; }
    private ICallGateSubscriber<string, int, bool> SendLineGate { get; }
    private ICallGateSubscriber<string, int, List<string>> SplitLineGate { get; }
    private ICallGateSubscriber<object?> AvailableGate { get; }

    /// <summary>Cached so it is not an IPC call every frame the input box is drawn.</summary>
    private int CachedCap { get; set; } = DefaultByteCap;

    public Splitter()
    {
        ApiVersionGate = Plugin.Interface.GetIpcSubscriber<int>("TildeTools.Split.ApiVersion");
        InputByteCapGate = Plugin.Interface.GetIpcSubscriber<int>("TildeTools.Split.InputByteCap");
        SendLineGate = Plugin.Interface.GetIpcSubscriber<string, int, bool>("TildeTools.Split.SendLine");
        SplitLineGate = Plugin.Interface.GetIpcSubscriber<string, int, List<string>>("TildeTools.Split.SplitLine");
        AvailableGate = Plugin.Interface.GetIpcSubscriber<object?>("TildeTools.Split.Available");

        // Fired when the splitter loads, so a Chat 2 that started first notices it.
        AvailableGate.Subscribe(Refresh);

        Refresh();
    }

    /// <summary>
    /// How many bytes the message box should accept. Falls back to the game's own
    /// limit whenever no compatible splitter answers.
    /// </summary>
    public int InputByteCap => CachedCap;

    /// <summary>
    /// Re-reads the limit. Cheap enough to call when the plugin list changes, and
    /// it must be called then, because the splitter may have just appeared.
    /// </summary>
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

            // Never shrink below what the game allows on the word of another plugin.
            return cap < DefaultByteCap ? DefaultByteCap : cap;
        }
        catch
        {
            // No splitter installed, or it is an incompatible version.
            IsAvailable = false;
            return DefaultByteCap;
        }
    }

    /// <summary>
    /// Offers a finished chat line, e.g. <c>/p some long text</c>, to the splitter.
    /// Returns true if it took ownership and will send it; false means Chat 2 should
    /// send it as it normally would.
    /// </summary>
    /// <summary>
    /// True when a compatible splitter is installed and answering. Recorded when
    /// asked rather than inferred from the cap, which would read as "absent" for
    /// anyone who set their limit to exactly the game's own.
    /// </summary>
    public bool IsAvailable { get; private set; }

    /// <summary>
    /// Asks how a finished chat line would divide up, for showing what a send will
    /// actually produce. Null when there is no splitter or it declined.
    /// </summary>
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

    public void Dispose()
    {
        AvailableGate.Unsubscribe(Refresh);
    }
}
