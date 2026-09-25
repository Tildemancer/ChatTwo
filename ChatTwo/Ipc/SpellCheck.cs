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
    private ICallGateSubscriber<string, string, Action<string>, bool> DefineGate { get; }
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
        DefineGate = Plugin.Interface.GetIpcSubscriber<string, string, Action<string>, bool>("TildeTools.Spell.Define");
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

    public void Refresh()
    {
        try
        {
            IsAvailable = ApiVersionGate.InvokeFunc() >= RequiredApiVersion;
        }
        catch
        {
            IsAvailable = false;
        }
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

    // Null while Wordsmith is still looking, the menu asks again next frame
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
        try
        {
            AddGate.InvokeFunc(word);
        }
        catch
        {
        }

        Cached.Clear();
        Generation++;
    }

    // Until restart, without learning it. Filtered in the checker, so every box agrees
    public void Ignore(string word)
    {
        try
        {
            IgnoreGate.InvokeFunc(word);
        }
        catch
        {
        }

        Cached.Clear();
        Generation++;
    }

    // The word around index as the spellchecker reads words, so any word can be looked up
    public (int Start, int Length)? WordAt(string text, int index)
    {
        try
        {
            return WordAtGate.InvokeFunc(text, index) is [var start, var length] ? (start, length) : null;
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<string> Synonyms(string word)
    {
        try
        {
            return SynonymsGate.InvokeFunc(word);
        }
        catch
        {
            return [];
        }
    }

    // Opens TildeTools' definition window. Its Use button calls use with the word to put in original's place
    public void Define(string word, string original, Action<string> use)
    {
        try
        {
            DefineGate.InvokeFunc(word, original, use);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        AvailableGate.Unsubscribe(OnAvailable);
    }
}
