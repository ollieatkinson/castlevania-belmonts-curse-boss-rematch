namespace BossRematch;

// Use real time: the map can pause the game's scaled clock while closing.
internal sealed class ArenaTravelGate
{
    private float? readySince;
    private int readyFrame;

    internal bool CanStart(bool ready, float now, int frame)
    {
        if (!ready)
        {
            Reset();
            return false;
        }
        if (readySince == null)
        {
            readySince = now;
            readyFrame = frame;
        }
        return now - readySince.Value >= 0.2f && frame - readyFrame >= 2;
    }

    internal void Reset() => readySince = null;
}
