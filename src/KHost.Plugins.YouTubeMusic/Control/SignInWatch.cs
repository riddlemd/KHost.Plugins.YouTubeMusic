namespace KHost.Plugins.YouTubeMusic.Control;

/// <summary>Decides when losing the Google sign-in is worth telling the host about: once, when it
/// goes while the venue wants music, and not again until it has come back. The music carries on
/// signed out either way; the warning is that adverts are now on their way into the room.</summary>
public sealed class SignInWatch
{
    private readonly Lock _gate = new();
    private bool _armed;

    /// <summary>The last definite answer; null until the page has given one.</summary>
    public bool? SignedIn { get; private set; }

    /// <param name="signedIn">Null is "could not tell", which neither arms nor fires.</param>
    /// <param name="wanted">The venue has asked for break music and not since stopped it.</param>
    /// <returns>True exactly when the host should be told it has been signed out.</returns>
    public bool Observe(bool? signedIn, bool wanted)
    {
        lock (_gate)
        {
            if (signedIn is not { } now)
                return false;

            SignedIn = now;

            if (now)
            {
                _armed = true;
                return false;
            }

            // Held over a sign-out seen while idle, so the next start still says so.
            if (!_armed || !wanted)
                return false;

            _armed = false;
            return true;
        }
    }
}
