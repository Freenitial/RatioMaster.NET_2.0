#if DEBUG
namespace RatioMaster;

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using RatioMaster.Services;

/// <summary>Offline policy checks for local-network permission and cancelled session preparation.</summary>
internal static class SessionNetworkSelfTest
{
    internal static async Task<bool> RunAsync()
    {
        bool passed = true;
        void Check(bool condition, string name)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            passed &= condition;
        }
        try
        {
            Task<IPAddress[]> PublicDns(string _, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") });
            }
            Permission denied = new(LocalNetworkPermissionResult.Denied);
            Check(await SessionNetworkAccess.EvaluateAsync("8.8.8.8", false, denied, PublicDns, CancellationToken.None)
                == SessionNetworkDecision.Allowed && denied.Requests == 0,
                "public trackers without a listener do not request local-network permission");
            Check(await SessionNetworkAccess.EvaluateAsync("10.0.0.2", false, denied, PublicDns, CancellationToken.None)
                == SessionNetworkDecision.DeniedEndpoint,
                "denied local tracker or proxy access stops before opening its connection");
            Check(await SessionNetworkAccess.EvaluateAsync("8.8.8.8", true, denied, PublicDns, CancellationToken.None)
                == SessionNetworkDecision.DeniedListener,
                "denied local peer access preserves public tracker operation");
            Check(await SessionNetworkAccess.EvaluateAsync("tracker.example", false, denied,
                (_, _) => Task.FromResult(new[] { IPAddress.Parse("192.168.4.2") }), CancellationToken.None)
                == SessionNetworkDecision.DeniedEndpoint,
                "DNS names resolving to private addresses require local-network permission");
            Check(await SessionNetworkAccess.EvaluateAsync("tracker.LOCAL.", false, denied,
                (_, _) => throw new Exception("mDNS must not run before permission"), CancellationToken.None)
                == SessionNetworkDecision.DeniedEndpoint,
                "local DNS names are recognized before restricted name resolution");
            Permission granted = new(LocalNetworkPermissionResult.Granted);
            Check(await SessionNetworkAccess.EvaluateAsync("fd00::1", false, granted, PublicDns, CancellationToken.None)
                == SessionNetworkDecision.Allowed && granted.Requests == 1,
                "granting permission allows an IPv6 local endpoint");
            Permission existingGrant = new(LocalNetworkPermissionResult.Denied) { IsGranted = true };
            Check(await SessionNetworkAccess.EvaluateAsync("tracker.example", true, existingGrant,
                (_, _) => throw new Exception("No lookup needed after permission grant"), CancellationToken.None)
                == SessionNetworkDecision.Allowed && existingGrant.Requests == 0,
                "an existing permission grant requires no prompt or preflight DNS lookup");
            Permission absentActivity = new(LocalNetworkPermissionResult.Cancelled);
            Check(await SessionNetworkAccess.EvaluateAsync("10.0.0.2", false, absentActivity, PublicDns, CancellationToken.None)
                == SessionNetworkDecision.Cancelled,
                "activity destruction cancels pending session preparation");
            int requestsBefore = denied.Requests;
            Check(await SessionNetworkAccess.EvaluateAsync("unresolved.example", false, denied,
                (_, _) => Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound)), CancellationToken.None)
                == SessionNetworkDecision.Allowed && denied.Requests == requestsBefore,
                "failed preflight DNS preserves normal tracker error handling without an unrelated permission prompt");
            using CancellationTokenSource stop = new();
            Permission waiting = new(LocalNetworkPermissionResult.Granted) { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            Task<SessionNetworkDecision> preparation = SessionNetworkAccess.EvaluateAsync("10.0.0.2", false, waiting, PublicDns, stop.Token);
            stop.Cancel();
            bool cancelled = false;
            try { await preparation; }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && !waiting.Pending.Task.IsCompleted,
                "closing a tab cancels its preparation without cancelling another tab's shared permission request");
            using CancellationTokenSource simultaneousStop = new();
            Permission completedDuringStop = new(LocalNetworkPermissionResult.Granted)
            {
                OnRequest = simultaneousStop.Cancel,
            };
            bool completionCancelled = false;
            try
            {
                await SessionNetworkAccess.EvaluateAsync("10.0.0.2", false, completedDuringStop, PublicDns, simultaneousStop.Token);
            }
            catch (OperationCanceledException) { completionCancelled = true; }
            Check(completionCancelled, "a permission result completed alongside a stop cannot restart the session");
            foreach (string address in new[] { "10.0.0.1", "172.16.0.1", "172.31.255.254", "192.168.0.1", "169.254.1.1", "224.0.0.1", "255.255.255.255", "fe80::1", "fd00::1", "ff02::1", "::ffff:192.168.0.1" })
                Check(SessionNetworkAccess.IsLocalAddress(IPAddress.Parse(address)), "local-network address recognized: " + address);
            foreach (string address in new[] { "8.8.8.8", "172.15.1.1", "172.32.1.1", "127.0.0.1", "::1", "2001:4860:4860::8888" })
                Check(!SessionNetworkAccess.IsLocalAddress(IPAddress.Parse(address)), "public or loopback address requires no LAN grant: " + address);
        }
        catch (Exception exception) { Check(false, exception.ToString()); }
        return passed;
    }

    private sealed class Permission(LocalNetworkPermissionResult result) : ILocalNetworkPermission
    {
        public bool IsGranted { get; init; }
        internal int Requests { get; private set; }
        internal TaskCompletionSource<LocalNetworkPermissionResult>? Pending { get; init; }
        internal Action? OnRequest { get; init; }
        public Task<LocalNetworkPermissionResult> RequestAsync()
        {
            Requests++;
            OnRequest?.Invoke();
            return Pending?.Task ?? Task.FromResult(result);
        }
    }
}
#endif
