namespace RatioMaster.Services;

using System;
using System.Collections.Generic;
using System.Text;

/// <summary>Bounds both queued messages and displayed text before the UI processes a batch.</summary>
internal sealed class TerminalBuffer
{
    internal const int Capacity = 100_000;
    private readonly object sync = new();
    private readonly Queue<string> pending = new();
    private int pendingCharacters;
    private bool scheduled;

    internal bool Enqueue(string message)
    {
        if (message.Length > Capacity) message = message[..(Capacity - 16)] + "… (truncated)\n";
        lock (sync)
        {
            while (pending.Count > 0 && pendingCharacters + message.Length > Capacity)
                pendingCharacters -= pending.Dequeue().Length;
            pending.Enqueue(message);
            pendingCharacters += message.Length;
            if (scheduled) return false;
            scheduled = true;
            return true;
        }
    }

    internal string Drain(string displayed)
    {
        lock (sync)
        {
            StringBuilder text = new(Math.Min(Capacity * 2, displayed.Length + pendingCharacters));
            text.Append(displayed.Length > Capacity ? displayed[^Capacity..] : displayed);
            while (pending.TryDequeue(out string? line)) text.Append(line);
            pendingCharacters = 0;
            scheduled = false;
            if (text.Length <= Capacity) return text.ToString();
            int start = text.Length - Capacity;
            int lineEnd = start;
            while (lineEnd < text.Length && text[lineEnd] != '\n') lineEnd++;
            if (lineEnd < text.Length - 1) start = lineEnd + 1;
            return text.ToString(start, text.Length - start);
        }
    }

    internal void Clear()
    {
        lock (sync)
        {
            pending.Clear();
            pendingCharacters = 0;
        }
    }
}
