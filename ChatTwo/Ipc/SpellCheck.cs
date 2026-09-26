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
    private const int RequiredApiVersion = 1;

    private readonly ICallGateSubscriber<int> ApiVersionGate = Plugin.Interface.GetIpcSubscriber<int>("TildeTools.Spell.ApiVersion");
    private readonly ICallGateSubscriber<string, List<int>> CheckGate = Plugin.Interface.GetIpcSubscriber<string, List<int>>("TildeTools.Spell.Check");
    private readonly ICallGateSubscriber<string, List<string>?> SuggestGate = Plugin.Interface.GetIpcSubscriber<string, List<string>?>("TildeTools.Spell.Suggest");
    private readonly ICallGateSubscriber<string, bool> AddGate = Plugin.Interface.GetIpcSubscriber<string, bool>("TildeTools.Spell.AddToDictionary");
    private readonly ICallGateSubscriber<string, bool> IgnoreGate = Plugin.Interface.GetIpcSubscriber<string, bool>("TildeTools.Spell.Ignore");
    private readonly ICallGateSubscriber<string, int, List<int>> WordAtGate = Plugin.Interface.GetIpcSubscriber<string, int, List<int>>("TildeTools.Spell.WordAt");
    private readonly ICallGateSubscriber<string, List<string>> SynonymsGate = Plugin.Interface.GetIpcSubscriber<string, List<string>>("TildeTools.Spell.Synonyms");
    private readonly ICallGateSubscriber<string, string, Action<string>?, bool> DefineGate = Plugin.Interface.GetIpcSubscriber<string, string, Action<string>?, bool>("TildeTools.Spell.Define");
    private readonly ICallGateSubscriber<object?> AvailableGate = Plugin.Interface.GetIpcSubscriber<object?>("TildeTools.Spell.Available");

    // Many, not one: a split message is checked a part at a time, so one slot means each part evicts the last
    private readonly Dictionary<string, List<Misspelling>> Cached = [];

    // Moves when answers change: a new dictionary, a word added or ignored
    // Drop remembered results when it does
    public int Generation { get; private set; }

    // Every part of a 32000-byte message, about 67, with room over
    internal const int MostToRemember = 256;

    // Each keystroke adds the whole input too: 36 KB a snapshot at 18000
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

        // Not Try: its lambda captures text, so every call allocated, cache hits too
        try
        {
            // Flattened: start, length, start, length
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

    // Null while TildeTools is still looking
    // Not Try: asked every frame the menu is open, and Try's lambda allocates
    public IReadOnlyList<string>? Suggest(string word)
    {
        try
        {
            return SuggestGate.InvokeFunc(word);
        }
        catch
        {
            return [];
        }
    }

    public void AddToDictionary(string word) => Try(() => AddGate.InvokeFunc(word), false);

    // Until restart, without learning it
    // Filtered in the checker, so every box agrees
    public void Ignore(string word) => Try(() => IgnoreGate.InvokeFunc(word), false);

    public (int Start, int Length)? WordAt(string text, int index) =>
        Try<(int, int)?>(() => WordAtGate.InvokeFunc(text, index) is [var start, var length] ? (start, length) : null, null);

    public IReadOnlyList<string> Synonyms(string word) => Try(() => SynonymsGate.InvokeFunc(word), []);

    // Opens TildeTools' Define window
    // Its Use button calls use with original's replacement, none when use is null
    public void Define(string word, string original, Action<string>? use) => Try(() => DefineGate.InvokeFunc(word, original, use), false);

    // Gates throw while TildeTools unloads or their module is off
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
