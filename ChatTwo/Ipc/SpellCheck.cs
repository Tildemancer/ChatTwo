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
    private ICallGateSubscriber<string, List<string>> SuggestGate { get; }
    private ICallGateSubscriber<string, bool> AddGate { get; }
    private ICallGateSubscriber<string, bool> IgnoreGate { get; }
    private ICallGateSubscriber<object?> AvailableGate { get; }

    // Many, not one: a split message is checked a part at a time, so one slot means each part evicts the last
    private readonly Dictionary<string, List<Misspelling>> Cached = [];

    // Moves when answers change (new dictionary, word added or ignored). Drop remembered results when it does
    public int Generation { get; private set; }

    private const int MostToRemember = 64;

    public SpellCheck()
    {
        ApiVersionGate = Plugin.Interface.GetIpcSubscriber<int>("TildeTools.Spell.ApiVersion");
        CheckGate = Plugin.Interface.GetIpcSubscriber<string, List<int>>("TildeTools.Spell.Check");
        SuggestGate = Plugin.Interface.GetIpcSubscriber<string, List<string>>("TildeTools.Spell.Suggest");
        AddGate = Plugin.Interface.GetIpcSubscriber<string, bool>("TildeTools.Spell.AddToDictionary");
        IgnoreGate = Plugin.Interface.GetIpcSubscriber<string, bool>("TildeTools.Spell.Ignore");
        AvailableGate = Plugin.Interface.GetIpcSubscriber<object?>("TildeTools.Spell.Available");

        AvailableGate.Subscribe(OnAvailable);
        Refresh();
    }

    public bool IsAvailable { get; private set; }

    private void OnAvailable()
    {
        Refresh();

        Cached.Clear();
        LastSuggested = string.Empty;
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

        List<Misspelling> result = [];

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
            return result;
        }

        Cached[text] = result;
        return result;
    }

    private string LastSuggested = string.Empty;
    private IReadOnlyList<string> LastSuggestions = [];

    // Cached, the open menu asks every frame and a lookup costs tens of ms
    public IReadOnlyList<string> Suggest(string word)
    {
        if (word == LastSuggested)
            return LastSuggestions;

        try
        {
            LastSuggestions = SuggestGate.InvokeFunc(word);
        }
        catch
        {
            LastSuggestions = [];
        }

        LastSuggested = word;
        return LastSuggestions;
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
        LastSuggested = string.Empty;
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
        LastSuggested = string.Empty;
        Generation++;
    }

    public void Dispose()
    {
        AvailableGate.Unsubscribe(OnAvailable);
    }
}
