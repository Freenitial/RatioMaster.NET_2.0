namespace RatioMaster.BitTorrent;

using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;

internal sealed class ValueDictionary : IBEncodeValue
{
    private readonly Dictionary<string, IBEncodeValue> dict = [];

    internal void Add(string key, IBEncodeValue value) => dict.Add(key, value);

    internal bool Contains(string key) => dict.ContainsKey(key);

    public byte[] Encode()
    {
        Collection<byte> collection = [(byte)'d'];

        // Preserve insertion order. Metainfo hashes use the original source bytes in TorrentBencodeReader.
        foreach (string key in dict.Keys)
        {
            foreach (byte b in new ValueString(key).Encode())
            {
                collection.Add(b);
            }

            foreach (byte b in dict[key].Encode())
            {
                collection.Add(b);
            }
        }

        collection.Add((byte)'e');
        byte[] result = new byte[collection.Count];
        collection.CopyTo(result, 0);
        return result;
    }

    public void Parse(Stream s)
    {
        for (byte b = BEncode.ReadByteChecked(s); b != 0x65; b = BEncode.ReadByteChecked(s))
        {
            if (b < (byte)'0' || b > (byte)'9')
            {
                throw new TorrentException("Key expected to be a string.");
            }

            ValueString keyString = new();
            BEncode.CountNode();
            keyString.Parse(s, b);
            if (dict.ContainsKey(keyString.String))
                throw new TorrentException("Bencoded dictionary keys must be unique.");
            IBEncodeValue value = BEncode.Parse(s);
            dict.Add(keyString.String, value);
        }
    }

    internal void SetStringValue(string key, string value) => this[key] = new ValueString(value);

    internal IBEncodeValue this[string key]
    {
        get
        {
            if (!dict.ContainsKey(key))
            {
                dict.Add(key, new ValueString(string.Empty));
            }

            return dict[key];
        }

        set => dict[key] = value;
    }

    internal ICollection Keys => dict.Keys;

    internal ICollection Values => dict.Values;
}
