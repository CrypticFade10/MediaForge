namespace SimilarVideoFinder.Core;

public static class StaTask
{
    public static Task<T> Run<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(action()); } catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true, Name = "SimilarVideoFinder Shell operations" };
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); return completion.Task;
    }
}
