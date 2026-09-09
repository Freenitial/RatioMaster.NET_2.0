namespace RatioMaster.Engine;

internal sealed class UdpAnnounceRequest
{
    internal byte[] InfoHash { get; set; } = [];

    internal byte[] PeerId { get; set; } = [];

    internal long Downloaded { get; set; }

    internal long Left { get; set; }

    internal long Uploaded { get; set; }

    /// <summary>0: normal; 1: completed; 2: started; 3: stopped.</summary>
    internal int Event { get; set; }

    internal uint Key { get; set; }

    internal int NumWant { get; set; } = -1;

    internal ushort Port { get; set; }
}
