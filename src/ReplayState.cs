namespace BossRematch;

// Transient encounter values never enter the player's saved progression.
internal sealed class ReplayState
{
    internal enum Stage { Entering, Fighting, Returning }
    internal Stage Phase { get; private set; } = Stage.Entering;
    internal readonly Dictionary<string, bool> Keys = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, int> Characters = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, bool> Virtues = new(StringComparer.Ordinal);
    private readonly HashSet<int> resetSpawners = new();
    private int saveReadDepth;
    internal bool TryPrepareSpawner(int id) => CanVirtualize && resetSpawners.Add(id);
    internal bool CanVirtualize => Phase != Stage.Returning && saveReadDepth == 0;
    // Hydrating the real components from disk must not overwrite the replay's
    // temporary progression through the game's normal setter methods.
    internal void BeginSaveRead() => saveReadDepth++;
    internal void EndSaveRead()
    {
        if (saveReadDepth == 0) throw new InvalidOperationException("No save read is active.");
        saveReadDepth--;
    }
    internal void Arrived() { if (Phase == Stage.Entering) Phase = Stage.Fighting; }
    internal void Return() => Phase = Stage.Returning;
    internal bool TryReadKey(string id, out bool value)
    {
        value = false;
        return CanVirtualize && Keys.TryGetValue(id, out value);
    }
    internal bool WriteKey(string id, bool value, bool original)
    {
        var previous = Keys.TryGetValue(id, out var prior) ? prior : original;
        Keys[id] = value;
        return previous != value;
    }
}
