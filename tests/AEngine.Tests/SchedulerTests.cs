using AEngine.Core.Runtime;

namespace AEngine.Tests;

public class SchedulerTests
{
    [Fact]
    public void CollectDue_ReturnsActionsInWakeTurnOrder()
    {
        var scheduler = new Scheduler();
        scheduler.Schedule(new ScheduledAction(5, "npc", "look", null));
        scheduler.Schedule(new ScheduledAction(2, "npc", "go", "door"));
        scheduler.Schedule(new ScheduledAction(9, "npc", "look", null));

        var due = scheduler.CollectDue(5);

        Assert.Equal(2, due.Count);
        Assert.Equal(2, due[0].WakeTurn);
        Assert.Equal(5, due[1].WakeTurn);
        Assert.Equal(1, scheduler.Count);
    }

    [Fact]
    public void CollectDue_NothingDue_ReturnsEmpty()
    {
        var scheduler = new Scheduler();
        scheduler.Schedule(new ScheduledAction(3, "npc", "look", null));

        Assert.Empty(scheduler.CollectDue(2));
        Assert.Equal(1, scheduler.Count);
    }
}
