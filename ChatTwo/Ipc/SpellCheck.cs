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

    private ICallGateSubscriber<int> ApiVersionGate { get; }
    private ICallGateSubscriber<string, List<int>> CheckGate { get; }
    private ICallGateSubscriber<string, List<string>?> SuggestGate { get; }
    private ICallGateSubscriber<string, bool> AddGate { get; }
    private ICallGateSubscriber<string, bool> IgnoreGate { get; }
    private ICallGateSubscriber<string, int, List<int>> WordAtGate { get; }
    private ICallGateSubscriber<string, List<string>> SynonymsGate { get; }
    private ICallGateSubscriber<string, string, Action<string>?, bool> DefineGate { get; }
    private ICallGateSubscriber<object?> AvailableGate { get; }

    // Many, not one: a split message is checked a part at a time, so one slot means each part evicts the last
    private readonly Dictionary<string, List<Misspelling>> Cached = [];

    // Moves when answers change (new dictionary, word added or ignored). Drop remembered results when it does
    public int Generation { get; private set; }

    // Every part of a 32000-byte message, about 67, with room over
    internal const int MostToRemember = 256;

    public SpellCheck()
    {
        ApiVersionGate = Plugin.Interface.GetIpcSubscriber<int>("TildeTools.Spell.ApiVersion");
        CheckGate = Plugin.Interface.GetIpcSubscriber<string, List<int>>("TildeTools.Spell.Check");
        SuggestGate = Plugin.Interface.GetIpcSubscriber<string, List<string>?>("TildeTools.Spell.Suggest");
        AddGate = Plugin.Interface.GetIpcSubscriber<string, bool>("TildeTools.Spell.AddToDictionary");
        IgnoreGate = Plugin.Interface.GetIpcSubscriber<string, bool>("TildeTools.Spell.Ignore");
        WordAtGate = Plugin.Interface.GetIpcSubscriber<string, int, List<int>>("TildeTools.Spell.WordAt");
        SynonymsGate = Plugin.Interface.GetIpcSubscriber<string, List<string>>("TildeTools.Spell.Synonyms");
        DefineGate = Plugin.Interface.GetIpcSubscriber<string, string, Action<string>?, bool>("TildeTools.Spell.Define");
        AvailableGate = Plugin.Interface.GetIpcSubscriber<object?>("TildeTools.Spell.Available");

        AvailableGate.Subscribe(OnAvailable);
        Refresh();
    }

    public bool IsAvailable { get; private set; }

    private void OnAvailable()
    {
        Refresh();

        Cached.Clear();
        Generation++;
    }

    public void Refresh() => IsAvailable = Try(() => ApiVersionGate.InvokeFunc() >= RequiredApiVersion, false);

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

        if (Cached.Count >= MostToRemember)
            Cached.Clear();

        var result = Cached[text] = [];

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

    // Null while TildeTools is still looking, the menu asks again next frame
    // Its own catch rather than Try: asked every frame the menu is open, and Try's lambda allocates each call
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

    public void AddToDictionary(string word)
    {
        Try(() => AddGate.InvokeFunc(word), false);
        Cached.Clear();
        Generation++;
    }

    // Until restart, without learning it. Filtered in the checker, so every box agrees
    public void Ignore(string word)
    {
        Try(() => IgnoreGate.InvokeFunc(word), false);
        Cached.Clear();
        Generation++;
    }

    // The word around index as the spellchecker reads words, so any word can be looked up
    public (int Start, int Length)? WordAt(string text, int index) =>
        Try<(int, int)?>(() => WordAtGate.InvokeFunc(text, index) is [var start, var length] ? (start, length) : null, null);

    public IReadOnlyList<string> Synonyms(string word) => Try(() => SynonymsGate.InvokeFunc(word), []);

    // Opens TildeTools' definition window
    // Its Use button calls use with the word to put in original's place, none when use is null
    public void Define(string word, string original, Action<string>? use) => Try(() => DefineGate.InvokeFunc(word, original, use), false);

    // A call that can't throw here: TildeTools may be unloading, or its Spelling off
    private static T Try<T>(Func<T> call, T failed)
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

    public void Dispose()
    {
        AvailableGate.Unsubscribe(OnAvailable);
    }
}
