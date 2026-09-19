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
/// Optional spellchecking from a plugin that provides a dictionary.
///
/// Chat 2 has no dictionary of its own, so this does nothing at all unless one is
/// installed. Results are cached per string: the preview draws every frame and
/// checking crosses into another plugin.
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
    /// Answers already given, keyed by the text they were about.
    ///
    /// More than one, because a split message is checked a part at a time and a single
    /// remembered answer would be thrown away by the very next part — turning what
    /// should be one check per edit into one per part per pass, every frame.
    /// </summary>
    private readonly Dictionary<string, List<Misspelling>> Cached = [];

    /// <summary>
    /// Emptied rather than aged. Keys pile up a keystroke at a time, and there is
    /// nothing to be gained from remembering half-typed words.
    /// </summary>
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

        // A dictionary arriving changes every previous answer.
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

    /// <summary>
    /// The misspelled words in a string, or an empty list when no checker is
    /// installed.
    /// </summary>
    /// <summary>Reports once whether a checker answered, so a silent nothing explains itself.</summary>
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
    /// Words that might have been meant instead, best first.
    ///
    /// Cached because the menu asks again on every frame it is open, and working out
    /// the answer means searching the dictionary for words that sound or look like
    /// this one — tens of milliseconds, which is fine once and ruinous sixty times a
    /// second.
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
            // Nothing useful to do; the word simply stays flagged.
        }

        Cached.Clear();
        LastSuggested = string.Empty;
    }

    /// <summary>
    /// Leaves a word alone until the game is restarted, without learning it.
    ///
    /// Handed to the checker rather than filtered here, so every text box that
    /// checks spelling agrees about which words are being overlooked.
    /// </summary>
    public void Ignore(string word)
    {
        try
        {
            IgnoreGate.InvokeFunc(word);
        }
        catch
        {
            // Nothing useful to do; the word simply stays flagged.
        }

        Cached.Clear();
        LastSuggested = string.Empty;
    }

    public void Dispose()
    {
        AvailableGate.Unsubscribe(OnAvailable);
    }
}
