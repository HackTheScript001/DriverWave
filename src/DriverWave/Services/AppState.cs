namespace DriverWave.Services;

/// <summary>Tracks whether any page is in the middle of a job, so the app doesn't update itself mid-install.</summary>
public static class AppState
{
    private static int _busy;

    public static bool IsBusy => Volatile.Read(ref _busy) > 0;

    /// <summary>Set when DriverWave is closing on purpose (e.g. to restart the PC), so it doesn't ask "are you sure?".</summary>
    public static bool ShuttingDown { get; set; }

    public static void Enter() => Interlocked.Increment(ref _busy);

    public static void Exit()
    {
        if (Interlocked.Decrement(ref _busy) < 0) Interlocked.Exchange(ref _busy, 0);
    }
}
