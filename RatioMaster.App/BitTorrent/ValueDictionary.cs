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

        // Re-encode keys in the order they were parsed. Valid torrents store the
        // "info" dictionary with keys already sorted, so preserving insertion
        // order reproduces the original bytes and keeps the SHA-1 info_hash valid.
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
            if (!char.IsNumber((char)b))
            {
                throw new TorrentException("Key expected to be a string.");
            }

            ValueString keyString = new();
            keyString.Parse(s, b);
            IBEncodeValue value = BEncode.Parse(s);
            dict[keyString.String] = value;
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
