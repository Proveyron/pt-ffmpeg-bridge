namespace Bridge.Protocol;

/// <summary>DIPC identifies every method by the djb2 hash of its ASCII name (e.g. "ExtractAudio").</summary>
public static class Djb2
{
    public static uint Hash(string name)
    {
        uint h = 5381;
        foreach (char c in name)
            h = unchecked(h * 33 + (byte)c);
        return h;
    }
}
