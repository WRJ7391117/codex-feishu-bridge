namespace DeepOriBridge.Services;

internal sealed class PageIdentityCommandGate
{
    private int _generation;

    internal bool PageIdentityBound { get; private set; }
    internal bool PageIdentityFaulted { get; private set; }
    internal bool AllowsSensitiveActions => PageIdentityBound && !PageIdentityFaulted;

    internal int BeginBinding()
    {
        _generation++;
        PageIdentityBound = false;
        PageIdentityFaulted = false;
        return _generation;
    }

    internal bool TryMarkBound(int generation)
    {
        if (generation != _generation || PageIdentityFaulted)
        {
            return false;
        }

        PageIdentityBound = true;
        return true;
    }

    internal bool TryMarkFaulted(int generation)
    {
        if (generation != _generation)
        {
            return false;
        }

        PageIdentityBound = false;
        PageIdentityFaulted = true;
        return true;
    }

    internal void Invalidate()
    {
        _generation++;
        PageIdentityBound = false;
        PageIdentityFaulted = true;
    }

    internal bool MayEnable(bool requested) => requested && AllowsSensitiveActions;
}
