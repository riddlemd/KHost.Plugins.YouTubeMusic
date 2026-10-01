using KHost.Plugins.YouTubeMusic.Audio;

namespace KHost.Plugins.YouTubeMusic.Tests.Audio;

public class FadeCurveTests
{
    // The same equal-decibel steps as the fall, walked back up, so the rise sounds as even.
    [Fact]
    public void RiseSteps_Always_AreTheFallWalkedBackUpEndingAtFull()
    {
        var rise = FadeCurve.RiseSteps(TimeSpan.FromMilliseconds(1500));
        var fall = FadeCurve.GainSteps(TimeSpan.FromMilliseconds(1500));

        Assert.Equal(15, rise.Count);
        Assert.Equal(fall.Take(14).Reverse(), rise.Take(14));
        Assert.Equal(1f, rise[^1]);
    }

    [Fact]
    public void RiseSteps_NoDuration_IsEmpty()
        => Assert.Empty(FadeCurve.RiseSteps(TimeSpan.Zero));

    [Fact]
    public void RiseSteps_ShorterThanOneStep_IsOneStepToFull()
        => Assert.Equal([1f], FadeCurve.RiseSteps(TimeSpan.FromMilliseconds(30)));

    [Fact]
    public void GainSteps_DefaultFade_IsFifteenStepsEndingInSilence()
    {
        var steps = FadeCurve.GainSteps(TimeSpan.FromMilliseconds(1500));

        Assert.Equal(15, steps.Count);
        Assert.Equal(0f, steps[^1]);
    }

    [Fact]
    public void GainSteps_Always_FallStrictly()
    {
        var steps = FadeCurve.GainSteps(TimeSpan.FromMilliseconds(1500));

        for (var i = 1; i < steps.Count; i++)
            Assert.True(steps[i] < steps[i - 1], $"Step {i} ({steps[i]}) does not fall below step {i - 1} ({steps[i - 1]}).");
    }

    // Equal steps in decibels: a linear ramp would put the first step at 14/15 of full, which the
    // ear hears as no change at all.
    [Fact]
    public void GainSteps_Always_FallInEqualDecibelSteps()
    {
        var steps = FadeCurve.GainSteps(TimeSpan.FromMilliseconds(1500));

        Assert.Equal(-4.0, 20 * Math.Log10(steps[0]), 3);
        Assert.Equal(-56.0, 20 * Math.Log10(steps[^2]), 3);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void GainSteps_NoDuration_IsEmpty(int milliseconds)
        => Assert.Empty(FadeCurve.GainSteps(TimeSpan.FromMilliseconds(milliseconds)));

    [Fact]
    public void GainSteps_ShorterThanOneStep_IsOneStepToSilence()
        => Assert.Equal([0f], FadeCurve.GainSteps(TimeSpan.FromMilliseconds(30)));
}
