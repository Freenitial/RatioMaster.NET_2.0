namespace RatioMaster.Services;

using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

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
    // Windows scopes the unprefixed mutex name to the interactive session.
    // Pipe connections are restricted to the current user.
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
        catch (PlatformNotSupportedException)
        {
            // Platforms without named mutex support allow startup without instance coordination.
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Unable to coordinate the application instance: " + exception.Message);
            return false;
        }
    }

    /// <summary>Ask the running instance to surface its window. Best-effort: if it doesn't answer we simply
    /// exit, which is still better than opening a rogue second instance.</summary>
    internal static void SignalExistingInstance()
    {
        try
        {
            using NamedPipeClientStream client = new(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
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
        _ = Task.Run(async () =>
        {
            try
            {
                await ListenAsync(PipeName, onActivate, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                Console.Error.WriteLine("Unable to listen for application activation: " + exception.Message);
            }
        });
    }

    internal static async Task ListenAsync(string pipeName, Action onActivate, CancellationToken cancellationToken,
        Action? onListening = null)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            // Endpoint creation failures escape the loop; only individual client failures are retried.
            using NamedPipeServerStream server = new(pipeName, PipeDirection.In, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            onListening?.Invoke();
            try
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                if (await ReadActivationAsync(server, deadline.Token).ConfigureAwait(false)) onActivate();
            }
            catch (OperationCanceledException)
            {
                // An idle client expires independently of cancellation of the listener itself.
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A client can close before acceptance or disconnect while its signal is being read.
            }
            finally
            {
                if (server.IsConnected)
                {
                    try { server.Disconnect(); }
                    catch (IOException) { }
                }
            }
        }
    }

    internal static async Task<bool> ReadActivationAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] signal = new byte[1];
        return await stream.ReadAsync(signal, cancellationToken).ConfigureAwait(false) == 1 && signal[0] == 1;
    }
}
