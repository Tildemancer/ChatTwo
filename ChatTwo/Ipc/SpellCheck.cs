// TildeTools: written for this fork, not part of upstream Chat 2.

using Dalamud.Plugin.Ipc;

namespace ChatTwo.Ipc;

public readonly record struct Misspelling(int Start, int Length)
{
    public string Word(string text) =>
        Start >= 0 && Length > 0 && Start + Length <= text.Length
            ? text.Substring(Start, Length)
            : string.Empty;
}

public sealed class SpellCheck : IDisposable
{
    private const int RequiredApiVersion = 6;

    private readonly ICallGateSubscriber<int> ApiVersionGate = Plugin.Interface.GetIpcSubscriber<int>("TildeTools.Spell.ApiVersion");
    private readonly ICallGateSubscriber<string, List<int>> CheckGate = Plugin.Interface.GetIpcSubscriber<string, List<int>>("TildeTools.Spell.Check");
    private readonly ICallGateSubscriber<string, int, List<int>> WordAtGate = Plugin.Interface.GetIpcSubscriber<string, int, List<int>>("TildeTools.Spell.WordAt");
    private readonly ICallGateSubscriber<string, bool> DefineGate = Plugin.Interface.GetIpcSubscriber<string, bool>("TildeTools.Spell.Define");
    private readonly ICallGateSubscriber<object?> AvailableGate = Plugin.Interface.GetIpcSubscriber<object?>("TildeTools.Spell.Available");
    private readonly ICallGateSubscriber<string, string, bool, Action<string>, bool> DrawMenuGate = Plugin.Interface.GetIpcSubscriber<string, string, bool, Action<string>, bool>("TildeTools.Spell.DrawMenu");

    // Cached per text, since a split message gets checked a part at a time.
    private readonly Dictionary<string, List<Misspelling>> Cached = [];

    // Moves when answers change (a new dictionary, a word added or ignored, etc)
    public int Generation { get; private set; }

    // A message's parts with significant room left over. 32k bytes splits ~67 parts, plus headroom.
    internal const int MostToRemember = 256;

    // Each keystroke caches the whole input too. 36 KB at 18000 characters!!!
    // You have no idea how happy I am for this number.
    private const int MostCharacters = 65536;
    private int CachedCharacters;

    public SpellCheck()
    {
        AvailableGate.Subscribe(OnAvailable);
        OnAvailable();
    }

    public bool IsAvailable { get; private set; }

    // Also after a word is added or ignored, see SpellIpc.Announce
    private void OnAvailable()
    {
        IsAvailable = Try(() => ApiVersionGate.InvokeFunc() >= RequiredApiVersion, false);
        Cached.Clear();
        CachedCharacters = 0;
        Generation++;
    }

    private bool ReportedState;

    public IReadOnlyList<Misspelling> Check(string text)
    {
        if (!ReportedState && !string.IsNullOrEmpty(text))
        {
            ReportedState = true;
            Plugin.Log.Information($"Spellchecker available to Chat 2: {IsAvailable}.");
        }

        if (!IsAvailable || string.IsNullOrEmpty(text))
            return [];

        if (Cached.TryGetValue(text, out var remembered))
            return remembered;

        if (Cached.Count >= MostToRemember || CachedCharacters > MostCharacters)
        {
            Cached.Clear();
            CachedCharacters = 0;
        }

        CachedCharacters += text.Length;
        var result = Cached[text] = [];

        // Specifically NOT Try, because its lambda would capture text and allocate every call. :nauseated: Running on every frame's evil little sibling.
        try
        {
            // Flattened: start, length, start, length.
            var flat = CheckGate.InvokeFunc(text);
            for (var i = 0; i + 1 < flat.Count; i += 2)
                result.Add(new Misspelling(flat[i], flat[i + 1]));
        }
        catch
        {
            IsAvailable = false;
        }

        return result;
    }

    // True once TT is done with the word, or gone.
    // Again, not Try: its lambda allocates, and the menu draws every frame.
    public bool DrawMenu(string id, string word, bool misspelled, Action<string> use)
    {
        try
        {
            return DrawMenuGate.InvokeFunc(id, word, misspelled, use);
        }
        catch
        {
            return true;
        }
    }

    public (int Start, int Length)? WordAt(string text, int index) =>
        Try<(int, int)?>(() => WordAtGate.InvokeFunc(text, index) is [var start, var length] ? (start, length) : null, null);

    // Opens TT's Define window.
    public void Define(string word) => Try(() => DefineGate.InvokeFunc(word), false);

    // Gates throw while TT unloads or their module is off. Not great.
    // Is it time to use a Try that returns failed? Ough.
    // TODO: check the gate's HasFunction, I guess. I don't know I didn't do it here I did it in WS... I'll do this later
    internal static T Try<T>(Func<T> call, T failed)
    {
        try
        {
            return call();
        }
        catch
        {
            return failed;
        }
    }

    public void Dispose() => AvailableGate.Unsubscribe(OnAvailable);
}
