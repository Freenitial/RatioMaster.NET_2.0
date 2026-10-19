namespace RatioMaster.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using RatioMaster.Engine;
using RatioMaster.Models;

/// <summary>
/// The listening port each emulated client uses. A real client listens on one port for all of its torrents,
/// so every tab of one profile behind one network exit announces the same port. The port is drawn once with the
/// client's own rule, checked for availability, and saved with the session so it survives restarts.
/// </summary>
internal static class ListeningPortBook
{
    private const int PickAttempts = 24;
    private static readonly object Sync = new();
    private static readonly Dictionary<string, int> Ports = new(StringComparer.Ordinal);

    internal static event Action? Changed;

    /// <summary>Separates exits: tabs behind different proxies appear to trackers as different addresses.</summary>
    internal static string Route(ProxyConfig proxy) => proxy.Kind == ProxyKind.None ? "direct"
        : proxy.Kind.ToString().ToLowerInvariant() + "://" + proxy.Host.Trim().Trim('[', ']').ToLowerInvariant() + ":" + proxy.Port;

    internal static string Key(string profileName, string route) => profileName + "|" + route;

    internal static int? Peek(string profileName, string route)
    {
        lock (Sync) return Ports.TryGetValue(Key(profileName, route), out int port) ? port : null;
    }

    /// <summary>The profile's port for this exit, drawn on first use within the client's ranges.</summary>
    internal static int Resolve(ClientProfile profile, string route, Func<int, bool>? available = null)
    {
        string key = Key(profile.Name, route);
        HashSet<int> taken;
        lock (Sync)
        {
            if (Ports.TryGetValue(key, out int existing)) return existing;

            // Two clients behind one exit never share a port: a tracker would see both identities on one address.
            taken = Ports.Where(entry => entry.Key.EndsWith("|" + route, StringComparison.Ordinal)).Select(entry => entry.Value).ToHashSet();
        }
        available ??= IsAvailable;
        int chosen = PortRange.Pick(profile.PortRanges);
        // A fixed default port (Transmission's 51413) stays as is: that is exactly what the real client would announce.
        if (profile.PortRanges.Sum(range => range.Count) > 1)
        {
            for (int attempt = 0; attempt < PickAttempts && (taken.Contains(chosen) || !available(chosen)); attempt++)
                chosen = PortRange.Pick(profile.PortRanges);
        }
        lock (Sync)
        {
            if (Ports.TryGetValue(key, out int raced)) return raced;
            Ports[key] = chosen;
        }
        Changed?.Invoke();
        return chosen;
    }

    /// <summary>Adopts a port saved by an older version for a profile that has none yet.</summary>
    internal static bool Adopt(ClientProfileDefinition profile, string route, int port)
    {
        if (!PortRange.Contains(profile.PortRanges, port)) return false;
        lock (Sync)
        {
            if (!Ports.TryAdd(Key(profile.Name, route), port)) return false;
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>Other profiles given the same port behind the same exit (fixed default ports can coincide).</summary>
    internal static IReadOnlyList<string> SharingProfiles(string profileName, string route, int port)
    {
        string own = Key(profileName, route), suffix = "|" + route;
        lock (Sync)
        {
            return Ports.Where(entry => entry.Value == port && entry.Key != own && entry.Key.EndsWith(suffix, StringComparison.Ordinal))
                .Select(entry => entry.Key[..^suffix.Length]).Order(StringComparer.Ordinal).ToArray();
        }
    }

    internal static void Load(IReadOnlyDictionary<string, int>? saved)
    {
        lock (Sync)
        {
            Ports.Clear();
            if (saved is null) return;
            foreach ((string key, int port) in saved)
                if (key.Length is > 0 and <= 256 && port is >= 1 and <= 65535 && Ports.Count < 1024) Ports[key] = port;
        }
    }

    internal static Dictionary<string, int> Snapshot()
    {
        lock (Sync) return new Dictionary<string, int>(Ports, StringComparer.Ordinal);
    }

    /// <summary>Free on every address family this machine listens on, or already served by this process.</summary>
    internal static bool IsAvailable(int port)
    {
        if (PeerListenerHub.Shared.Serves(port)) return true;
        try
        {
            using Socket probe = new(Socket.OSSupportsIPv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            PeerListenerHub.ConfigureListenSocket(probe);
            probe.Bind(new IPEndPoint(Socket.OSSupportsIPv6 ? IPAddress.IPv6Any : IPAddress.Any, port));
            return true;
        }
        catch (SocketException) { return false; }
    }
}
