using ServiceBusToolset.Application.Common.ServiceBus.Models;
using Shouldly;
using Xunit;

namespace ServiceBusToolset.Application.Tests.ServiceBus.Models;

public class EntityTargetShould
{
    [Fact]
    public void CreateQueueTarget_WhenForQueueCalled()
    {
        var target = EntityTarget.ForQueue("my-queue");

        target.Queue.ShouldBe("my-queue");
        target.Topic.ShouldBeNull();
        target.Subscription.ShouldBeNull();
    }

    [Fact]
    public void CreateSubscriptionTarget_WhenForSubscriptionCalled()
    {
        var target = EntityTarget.ForSubscription("my-topic", "my-subscription");

        target.Queue.ShouldBeNull();
        target.Topic.ShouldBe("my-topic");
        target.Subscription.ShouldBe("my-subscription");
    }

    [Fact]
    public void ReturnTrueForIsQueueMode_WhenTargetIsQueue()
    {
        var target = EntityTarget.ForQueue("my-queue");

        target.IsQueueMode.ShouldBeTrue();
        target.IsSubscriptionMode.ShouldBeFalse();
    }

    [Fact]
    public void ReturnTrueForIsSubscriptionMode_WhenTargetIsSubscription()
    {
        var target = EntityTarget.ForSubscription("my-topic", "my-subscription");

        target.IsSubscriptionMode.ShouldBeTrue();
        target.IsQueueMode.ShouldBeFalse();
    }

    [Fact]
    public void ReturnQueueDescription_WhenTargetIsQueue()
    {
        var target = EntityTarget.ForQueue("my-queue");
        target.GetDescription().ShouldBe("queue 'my-queue'");
    }

    [Fact]
    public void ReturnSubscriptionDescription_WhenTargetIsSubscription()
    {
        var target = EntityTarget.ForSubscription("my-topic", "my-subscription");
        target.GetDescription().ShouldBe("topic 'my-topic' subscription 'my-subscription'");
    }

    [Fact]
    public void SupportEquality_WhenUsedAsRecord()
    {
        var target1 = EntityTarget.ForQueue("my-queue");
        var target2 = EntityTarget.ForQueue("my-queue");
        var target3 = EntityTarget.ForQueue("other-queue");

        target1.ShouldBe(target2);
        target1.ShouldNotBe(target3);
    }
}
