using System.Windows;
using System.Windows.Threading;

namespace CodeLines.App.Tests;

/// <summary>
/// WPF allows one Application per process, so every render test runs on one shared STA thread that owns it.
/// </summary>
internal static class WpfTestHost
{
    private static readonly Lazy<Dispatcher> Host = new(() =>
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    });

    public static void Run(Action test)
    {
        var operation = Host.Value.InvokeAsync(test);
        if (!operation.Task.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("WPF render timed out.");
        operation.Task.GetAwaiter().GetResult();
    }
}
