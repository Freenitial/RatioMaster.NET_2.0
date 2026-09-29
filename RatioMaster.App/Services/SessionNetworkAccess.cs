namespace RatioMaster.Services;

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

internal enum LocalNetworkPermissionResult { Granted, Denied, Cancelled }
internal enum SessionNetworkDecision { Allowed, DeniedEndpoint, DeniedListener, Cancelled }

internal interface ILocalNetworkPermission
{
    bool IsGranted { get; }
    Task<LocalNetworkPermissionResult> RequestAsync();
}

/// <summary>Checks Android local-network access before session networking begins.</summary>
internal static class SessionNetworkAccess
{
    internal static ILocalNetworkPermission? Provider { get; set; }

    internal static Task<SessionNetworkDecision> PrepareAsync(string host, bool useListener, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(37)) return Task.FromResult(SessionNetworkDecision.Allowed);
        return Provider is { } provider
            ? EvaluateAsync(host, useListener, provider, Dns.GetHostAddressesAsync, cancellationToken)
            : Task.FromResult(SessionNetworkDecision.Cancelled);
    }

    internal static async Task<SessionNetworkDecision> EvaluateAsync(string host, bool useListener,
        ILocalNetworkPermission permission, Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (permission.IsGranted) return SessionNetworkDecision.Allowed;
        bool localEndpoint = await IsLocalHostAsync(host, resolve, cancellationToken);
        if (!localEndpoint && !useListener) return SessionNetworkDecision.Allowed;
        LocalNetworkPermissionResult result = await permission.RequestAsync().WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return result switch
        {
            LocalNetworkPermissionResult.Granted => SessionNetworkDecision.Allowed,
            LocalNetworkPermissionResult.Cancelled => SessionNetworkDecision.Cancelled,
            _ => localEndpoint ? SessionNetworkDecision.DeniedEndpoint : SessionNetworkDecision.DeniedListener,
        };
    }

    private static async Task<bool> IsLocalHostAsync(string host,
        Func<string, CancellationToken, Task<IPAddress[]>> resolve, CancellationToken cancellationToken)
    {
        host = host.Trim().TrimEnd('.');
        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return true;
        if (IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? address)) return IsLocalAddress(address);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            foreach (IPAddress resolved in await resolve(host, timeout.Token).ConfigureAwait(false))
                if (IsLocalAddress(resolved)) return true;
        }
        catch (SocketException) { }
        catch (ArgumentException) { }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }

    internal static bool IsLocalAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
                || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 169 && bytes[1] == 254
                || bytes[0] is >= 224 and <= 239 || address.Equals(IPAddress.Broadcast);
        return address.IsIPv6LinkLocal || address.IsIPv6Multicast || (bytes[0] & 0xFE) == 0xFC;
    }
}
