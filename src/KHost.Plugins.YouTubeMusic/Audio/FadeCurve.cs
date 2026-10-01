namespace KHost.Plugins.YouTubeMusic.Audio;

/// <summary>The steps of a fade, as multipliers of the level the fade is measured against.</summary>
public static class FadeCurve
{
    /// <summary>A step every 100ms: 15 for the default 1.5s, each measured under 2ms.</summary>
    public static readonly TimeSpan StepInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>How far down the last audible step reaches before the final silence.</summary>
    public const double FloorDecibels = -60;

    /// <summary>Equal decibel steps, so the ear hears an even fall. A linear ramp in amplitude
    /// sounds as though nothing happens for most of its length and then cuts.</summary>
    /// <returns>One multiplier per <see cref="StepInterval"/>, the last always 0; empty when
    /// <paramref name="duration"/> is not positive.</returns>
    public static IReadOnlyList<float> GainSteps(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            return [];

        var count = Math.Max(1, (int)Math.Round(duration / StepInterval));
        var steps = new float[count];

        for (var i = 1; i < count; i++)
            steps[i - 1] = (float)Math.Pow(10, FloorDecibels * i / count / 20);

        steps[count - 1] = 0f;

        return steps;
    }

    /// <summary>The fade-in from silence: the fade-out's steps walked back up, so the rise is as
    /// even to the ear as the fall.</summary>
    /// <returns>One multiplier per <see cref="StepInterval"/>, the last always 1; empty when
    /// <paramref name="duration"/> is not positive.</returns>
    public static IReadOnlyList<float> RiseSteps(TimeSpan duration)
    {
        var fall = GainSteps(duration);

        if (fall.Count == 0)
            return [];

        var rise = new float[fall.Count];

        for (var i = 0; i < fall.Count - 1; i++)
            rise[i] = fall[fall.Count - 2 - i];

        rise[^1] = 1f;

        return rise;
    }
}
