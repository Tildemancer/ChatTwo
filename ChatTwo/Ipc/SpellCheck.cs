using Dalamud.Plugin.Ipc;

namespace ChatTwo.Ipc;

/// <summary>One misspelled word and where it sits in the text.</summary>
public readonly record struct Misspelling(int Start, int Length)
{
    public string Word(string text) =>
        Start >= 0 && Length > 0 && Start + Length <= text.Length
            ? text.Substring(Start, Length)
            : string.Empty;
}

/// <summary>
/// Optional spellchecking from a plugin that provides a dictionary; does nothing when none is
/// installed. Results are cached per string, since the preview redraws every frame.
/// </summary>
public sealed class SpellCheck : IDisposable
{
    private const int RequiredApiVersion = 1;

    private ICallGateSubscriber<int> ApiVersionGate { get; }
    private ICallGateSubscriber<string, List<int>> CheckGate { get; }
    private ICallGateSubscriber<string, List<string>> SuggestGate { get; }
    private ICallGateSubscriber<string, bool> AddGate { get; }
    private ICallGateSubscriber<string, bool> IgnoreGate { get; }
    private ICallGateSubscriber<object?> AvailableGate { get; }

    /// <summary>
    /// Answers already given, keyed by the text they were about. Holds many, not one: a split
    /// message is checked a part at a time and each part would evict the last.
    /// </summary>
    private readonly Dictionary<string, List<Misspelling>> Cached = [];

    /// <summary>Cleared all at once instead of aged out: the keys are half-typed words.</summary>
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

        // A new dictionary invalidates every cached answer.
        Cached.Clear();
        LastSuggested = string.Empty;
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

    /// <summary>Logs the checker's presence once.</summary>
    private bool ReportedState;

    /// <summary>The misspelled words in a string, or an empty list when no checker is installed.</summary>
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
            // Positions arrive flattened as start, length, start, length.
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

    /// <summary>
    /// Words that might have been meant instead, best first. Cached because the open menu asks
    /// EVERY frame and a lookup costs tens of milliseconds.
    /// </summary>
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

    /// <summary>Teaches the dictionary a word, so it stops being flagged anywhere.</summary>
    public void AddToDictionary(string word)
    {
        try
        {
            AddGate.InvokeFunc(word);
        }
        catch
        {
            // The word stays flagged.
        }

        Cached.Clear();
        LastSuggested = string.Empty;
    }

    /// <summary>
    /// Leaves a word alone until the game restarts, without learning it. The checker does the
    /// filtering, not us, so every text box agrees.
    /// </summary>
    public void Ignore(string word)
    {
        try
        {
            IgnoreGate.InvokeFunc(word);
        }
        catch
        {
            // The word stays flagged.
        }

        Cached.Clear();
        LastSuggested = string.Empty;
    }

    public void Dispose()
    {
        AvailableGate.Unsubscribe(OnAvailable);
    }
}
