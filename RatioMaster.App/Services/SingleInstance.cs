namespace RatioMaster.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Keeps RatioMaster to ONE process per user session. A second process is never what the user wants here:
/// the app already has tabs for running several torrents at once, and two processes would fight over the
/// portable <c>ratiomaster.session</c> file (last writer wins, so one instance's tabs silently overwrite
/// the other's). Launching again therefore hands focus back to the running instance — together with any
/// .torrent files or folders it was opened with, which the running instance adds as tabs.
///
/// <para>Desktop only: this lives in <c>Program.cs</c>'s path, which is excluded from the Android build.</para>
/// </summary>
internal static class SingleInstance
{
    // Windows scopes the unprefixed mutex name to the interactive session.
    // Pipe connections are restricted to the current user.
    private const string MutexName = "RatioMaster.NET.instance";
    private const string PipeName = "RatioMaster.NET.activate";
    private const byte ActivateOnly = 1;
    private const byte ActivateWithFiles = 2;
    private const int MaximumRequestBytes = 256 * 1024;

    private static readonly object Pending = new();
    private static Mutex? mutex;
    private static Action<IReadOnlyList<string>>? handler;
    private static List<string> queuedFiles = [];
    private static bool queuedActivation;

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

    /// <summary>
    /// Ask the running instance to surface its window and open <paramref name="files"/>. "Open with" on several files
    /// starts one process per file, all at once: the instance answers one at a time and may still be starting, so a
    /// launch keeps trying for a while as long as that instance exists.
    /// </summary>
    internal static void SignalExistingInstance(IReadOnlyList<string> files)
    {
        byte[] request = EncodeRequest(files);
        System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(15))
        {
            try
            {
                using NamedPipeClientStream client = new(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                client.Connect(1000);
                client.Write(request);
                client.Flush();
                return;
            }
            catch (TimeoutException) { }
            catch (IOException) { Thread.Sleep(100); }
            catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException) { return; }
            if (!InstanceExists()) return;
        }
    }

    private static bool InstanceExists()
    {
        try
        {
            if (!Mutex.TryOpenExisting(MutexName, out Mutex? existing)) return false;
            existing.Dispose();
            return true;
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Absolute paths, so the running instance resolves them independently of this process's directory.</summary>
    internal static byte[] EncodeRequest(IReadOnlyList<string> files)
    {
        string[] paths = files.Where(path => !string.IsNullOrWhiteSpace(path) && !path.StartsWith("--", StringComparison.Ordinal))
            .Select(path => { try { return Path.GetFullPath(path); } catch { return string.Empty; } })
            .Where(path => path.Length > 0 && path.IndexOf('\n') < 0).ToArray();
        if (paths.Length == 0) return [ActivateOnly];
        byte[] payload = Encoding.UTF8.GetBytes(string.Join('\n', paths));
        if (payload.Length > MaximumRequestBytes) return [ActivateOnly];
        return [ActivateWithFiles, .. payload];
    }

    /// <summary>
    /// Listens for later launches as soon as this instance owns the mutex, before the window exists; requests that
    /// arrive meanwhile wait for <see cref="Attach"/>.
    /// </summary>
    internal static void StartListener()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await ListenAsync(PipeName, Deliver, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                Console.Error.WriteLine("Unable to listen for application activation: " + exception.Message);
            }
        });
    }

    /// <summary>
    /// Receives activations from now on, starting with those that arrived while the window was being built.
    /// <paramref name="onActivate"/> runs on a BACKGROUND thread: the caller marshals to the UI thread.
    /// </summary>
    internal static void Attach(Action<IReadOnlyList<string>> onActivate)
    {
        List<string> early;
        bool activate;
        lock (Pending)
        {
            handler = onActivate;
            (early, activate) = (queuedFiles, queuedActivation);
            (queuedFiles, queuedActivation) = ([], false);
        }
        if (activate) onActivate(early);
    }

    internal static void Deliver(IReadOnlyList<string> files)
    {
        Action<IReadOnlyList<string>>? target;
        lock (Pending)
        {
            target = handler;
            if (target is null)
            {
                queuedFiles.AddRange(files);
                queuedActivation = true;
                return;
            }
        }
        target(files);
    }

    internal static async Task ListenAsync(string pipeName, Action<IReadOnlyList<string>> onActivate, CancellationToken cancellationToken,
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
                if (await ReadRequestAsync(server, deadline.Token).ConfigureAwait(false) is { } files) onActivate(files);
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

    /// <summary>The files of a valid activation request (empty for a plain activation), or null.</summary>
    internal static async Task<IReadOnlyList<string>?> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] kind = new byte[1];
        if (await stream.ReadAsync(kind, cancellationToken).ConfigureAwait(false) != 1) return null;
        if (kind[0] == ActivateOnly) return [];
        if (kind[0] != ActivateWithFiles) return null;
        using MemoryStream payload = new();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (payload.Length + read > MaximumRequestBytes) return null;
            payload.Write(buffer, 0, read);
        }
        string text;
        try { text = new UTF8Encoding(false, true).GetString(payload.ToArray()); }
        catch (DecoderFallbackException) { return null; }
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(Path.IsPathFullyQualified).ToArray();
    }
}
