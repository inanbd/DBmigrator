namespace DbMigrator.Desktop.Tests;

internal static class Wait
{
    /// <summary>Lets posted work and background tasks run until <paramref name="condition"/> holds.</summary>
    public static async Task UntilAsync(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            await Task.Delay(5);
        }
    }

    /// <summary>Awaits a task that must finish soon, so a hung test fails instead of stalling the run.</summary>
    public static async Task ForAsync(Task task, string what, int timeoutMs = 10_000)
    {
        if (await Task.WhenAny(task, Task.Delay(timeoutMs)) != task)
        {
            throw new TimeoutException($"Timed out waiting for {what}.");
        }

        await task;
    }
}
