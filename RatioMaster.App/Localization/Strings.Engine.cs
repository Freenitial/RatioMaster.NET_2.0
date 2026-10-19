namespace RatioMaster.Localization;

/// <summary>Session engine: log lines, alerts, stop reasons and validation errors.</summary>
public static partial class Strings
{
    public static string EngineAlreadyStarted => T("Create a session before starting another torrent.");
    public static string EngineInfoHashLength => T("Tracker info-hash must be 20 bytes.");
    public static string EnginePeerIdLength => T("Peer ID must contain exactly 20 single-byte characters.");
    public static string EngineTrackerScheme => T("Use an HTTP, HTTPS or UDP tracker URL.");
    public static string UdpProxyUnsupported => T("UDP trackers cannot use this TCP proxy. Choose an HTTP(S) tracker or disable the proxy.");
    public static string EnginePortRange => T("Listening port must be between 1 and 65535.");
    public static string EngineInvalidSize => T("Invalid torrent size.");
    public static string EngineFinishedFinite => T("Finished percentage must be finite.");

    public static string LogEmulationInfo => T("CLIENT EMULATION INFO:");
    public static string LogEmulationName(string name) => F1("Name: {0}", name);
    public static string LogEmulationPeerId(string peerId) => F1("PeerID: {0}", peerId);
    public static string LogEmulationPort(string port) => F1("Port: {0}", port);
    public static string LogResumed => T("Resumed.");
    public static string LogUpdatingInterval(int seconds) => F1("Updating interval: {0}", seconds);
    public static string LogWillRetry(int seconds) => F1("Will retry in {0} seconds.", seconds);
    public static string LogLeechersBack => T("Leechers available: upload resumed.");
    public static string PeersUnreadable(string detail) => F1("Error parsing peers: {0}", detail);
    public static string ScrapeUnavailable(string detail) => F1("Scrape unavailable: {0}", detail);

    public static string AlertPausedByYou => T("Paused by you.");
    public static string AlertNoLeechers => T("Paused: no leechers.");
    public static string AlertWaitingForTracker => T("Waiting for tracker.");
    public static string AlertTrackerOk => T("Tracker OK");

    public static string SessionError(string detail) => F1("Session error: {0}", detail);
    public static string TrackerWarning(string message) => F1("Tracker warning: {0}", message);
    public static string TrackerError(string message) => F1("Tracker error: {0}", message);
    public static string CompletionRefused(string message) => F1("The tracker refused the completed announce ({0}). It will be sent again.", message);
    public static string TrackerDelayUnrepresentable => T("The tracker delay cannot be represented by the session clock.");
    public static string TrackerDelayOutOfRange(string name) => F1("Tracker {0} is outside the supported integer-second range; automatic announces have been stopped.", name);
    public static string TrackerIdIgnored => T("Tracker ID ignored: expected at most 1024 bytes.");
    public static string TrackerNoResponse => T("No usable response from tracker.");
    public static string TrackerHttpStatus(int status) => F1("Tracker returned HTTP {0}.", status);
    public static string TrackerResponseTooLarge => T("Tracker response exceeds the size limit.");
    public static string TrackerUndecodable => T("Could not decode the tracker response.");
    public static string TrackerRejectedEmpty => T("Tracker explicitly rejected the announce.");
    public static string UdpKeyFormat => T("UDP tracker key must be a 32-bit hexadecimal value.");

    public static string StatusStopped => T("Stopped");
    public static string StoppedSessionError => T("Stopped: session error");
    public static string StoppedScheduleError => T("Stopped: tracker schedule error");
    public static string StoppedTrackerRejection(string message) => F1("Stopped: tracker rejection - {0}", message);
    public static string StoppedSeedersBelow => T("Stopped: seeders below threshold");
    public static string StoppedLeechersBelow => T("Stopped: leechers below threshold");
    public static string StoppedSwarmRatioBelow => T("Stopped: leechers/seeders below threshold");
    public static string StoppedRatioReached => T("Stopped: target ratio reached");
    public static string StoppedUploadAbove => T("Stopped: uploaded above threshold");
    public static string StoppedDownloadAbove => T("Stopped: downloaded above threshold");
    public static string StoppedTimeLimit => T("Stopped: time limit reached");

    public static string OperationShutdown => T("Session shutdown");
    public static string OperationCompletion => T("Completion notification");
    public static string OperationStop => T("Stop notification");
    public static string OperationFailed(string operation, string detail) => F2("{0} failed: {1}", operation, detail);
    public static string RejectionPreventedCompletion => T("Tracker rejection prevented successful completion.");
    public static string CompletionNotDelivered(string detail) => F1("Completion notification not delivered: {0}", detail);

    public static string ListenerStarted(string port) => F1("Listening for peers on port {0}.", port);
    public static string ListenerUnavailable(string port, string detail) => F2("TCP listener unavailable on port {0}: {1}", port, detail);
    public static string ListenerDuplicate(string port) => F1("Another tab already answers peers for this torrent on port {0}.", port);
    public static string PeerHandshakeAnswered(string address) => F1("Peer handshake answered: {0}", address);
    public static string PeerHandshakeAnsweredEncrypted(string address) => F1("Peer handshake answered: {0} (encrypted)", address);
}
