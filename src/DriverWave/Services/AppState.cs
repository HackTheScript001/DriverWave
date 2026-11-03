namespace DriverWave.Services;

/// <summary>Tracks whether any page is in the middle of a job, so the app doesn't update itself mid-install.</summary>
public static class AppState
{
    private static int _busy;

    public static bool IsBusy => Volatile.Read(ref _busy) > 0;

    public static void Enter() => Interlocked.Increment(ref _busy);

    public static void Exit()
    {
        if (Interlocked.Decrement(ref _busy) < 0) Interlocked.Exchange(ref _busy, 0);
    }
}
