namespace RatioMaster.Services;

using System;

/// <summary>Samples rates from monotonic byte counters and elapsed time without adding a timer.</summary>
internal sealed class TransferRateHistory(int capacity)
{
    private readonly double[] values = new double[capacity];
    private long previousBytes;
    private double previousSeconds;

    internal double[] Snapshot() => (double[])values.Clone();

    internal void Reset(long bytes, double seconds = 0)
    {
        Array.Clear(values);
        previousBytes = bytes;
        previousSeconds = seconds;
    }

    internal bool Sample(long bytes, double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < previousSeconds || bytes < previousBytes)
        {
            Reset(bytes, double.IsFinite(seconds) ? seconds : 0);
            return true;
        }
        double elapsed = seconds - previousSeconds;
        if (elapsed < 1) return false;
        Array.Copy(values, 1, values, 0, values.Length - 1);
        values[^1] = (bytes - previousBytes) / elapsed;
        previousBytes = bytes;
        previousSeconds = seconds;
        return true;
    }
}
