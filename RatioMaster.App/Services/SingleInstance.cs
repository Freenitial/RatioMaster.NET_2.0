namespace RatioMaster.Services;

using System;
using System.IO.Pipes;
using System.Threading;

/// <summary>
/// Keeps RatioMaster to ONE process per user session. A second process is never what the user wants here:
/// the app already has tabs for running several torrents at once, and two processes would fight over the
/// portable <c>ratiomaster.session</c> file (last writer wins, so one instance's tabs silently overwrite
/// the other's). Launching again therefore hands focus back to the running instance — which matters because
/// closing the window only minimises it to the tray, so the "already running" instance is usually invisible.
///
/// <para>Desktop only: this lives in <c>Program.cs</c>'s path, which is excluded from the Android build.</para>
/// </summary>
internal static class SingleInstance
{
    // Unprefixed name = per-session on Windows (a second user gets their own instance, which is correct);
    // on Unix .NET backs both primitives with files under the temp dir, so the same scoping applies.
    private const string MutexName = "RatioMaster.NET.instance";
    private const string PipeName = "RatioMaster.NET.activate";

    private static Mutex? mutex;

    /// <summary>True if THIS process is the first instance. False means one is already running.</summary>
    internal static bool TryAcquire()
    {
        try
        {
            mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                mutex = null;
            }

            return createdNew;
        }
        catch
        {
            // Named mutexes can be unavailable (locked-down or exotic platform). Never block the user from
            // starting the app just because we couldn't arbitrate — degrade to the old multi-instance
            // behaviour rather than refusing to launch.
            return true;
        }
    }

    /// <summary>Ask the running instance to surface its window. Best-effort: if it doesn't answer we simply
    /// exit, which is still better than opening a rogue second instance.</summary>
    internal static void SignalExistingInstance()
    {
        try
        {
            using NamedPipeClientStream client = new(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            client.WriteByte(1);
            client.Flush();
        }
        catch
        {
            // instance is starting up, shutting down, or otherwise not listening — nothing useful to do
        }
    }

    /// <summary>
    /// Listen for later launches. <paramref name="onActivate"/> is raised on a BACKGROUND thread, so the
    /// caller is responsible for marshalling to the UI thread.
    /// </summary>
    internal static void StartListener(Action onActivate)
    {
        Thread listener = new(() =>
        {
            while (true)
            {
                try
                {
                    // Recreated per connection: a NamedPipeServerStream serves exactly one client.
                    using NamedPipeServerStream server = new(PipeName, PipeDirection.In, 1);
                    server.WaitForConnection();
                    if (server.ReadByte() >= 0)
                    {
                        onActivate();
                    }
                }
                catch
                {
                    // Don't let a transient pipe error spin this thread at 100% CPU.
                    Thread.Sleep(500);
                }
            }
        })
        {
            IsBackground = true,
            Name = "single-instance-listener",
        };

        listener.Start();
    }
}
