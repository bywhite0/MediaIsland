using MediaIsland.Components;
using Xunit;

namespace MediaIsland.Tests;

public class InterludeDotsPresenterTests
{
    [Fact]
    public void GetAnimationState_StaggersTheDotsAfterTheEntranceFade()
    {
        var state = InterludeDotsPresenter.GetAnimationState(
            TimeSpan.Zero,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(1));

        Assert.True(state.IsVisible);
        Assert.True(state.Scale > 0);
        Assert.True(state.FirstDotOpacity > state.SecondDotOpacity);
        Assert.Equal(state.SecondDotOpacity, state.ThirdDotOpacity, 6);
    }

    [Fact]
    public void GetAnimationState_FadesAllDotsOutAtTheEndOfTheInterlude()
    {
        var state = InterludeDotsPresenter.GetAnimationState(
            TimeSpan.Zero,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(4950));

        Assert.True(state.IsVisible);
        Assert.InRange(state.FirstDotOpacity, 0, 0.2);
        Assert.InRange(state.SecondDotOpacity, 0, 0.2);
        Assert.InRange(state.ThirdDotOpacity, 0, 0.2);
    }
}
