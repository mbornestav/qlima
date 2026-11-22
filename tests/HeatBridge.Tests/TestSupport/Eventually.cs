namespace HeatBridge.Tests.TestSupport;

/// <summary>Polls a condition for asynchronous, eventually consistent assertions.</summary>
internal static class Eventually
{
    public static async Task WaitUntilAsync(Func<bool> condition, string description, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Timed out waiting until {description}.");
            }

            await Task.Delay(20);
        }
    }
}
