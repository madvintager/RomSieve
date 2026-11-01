namespace RomSieve.Core.Services;

/// <summary>
/// Remaining time estimate shared by the scan, classification, and transfer progress reports.
/// </summary>
public static class EtaEstimator
{
    /// <summary>
    /// Estimates the remaining seconds from the byte throughput when the byte totals are known,
    /// otherwise from the item rate. Returns 0 while nothing is processed or nothing remains.
    /// </summary>
    public static double EstimateSeconds(
        double elapsedSeconds,
        int processedCount,
        int totalCount,
        long processedBytes = 0,
        long totalBytes = 0)
    {
        var remainingItems = totalCount - processedCount;
        if (elapsedSeconds <= 0 || processedCount <= 0 || remainingItems <= 0)
        {
            return 0.0;
        }

        var remainingBytes = Math.Max(0L, totalBytes - processedBytes);
        if (totalBytes > 0 && processedBytes > 0 && remainingBytes > 0)
        {
            return remainingBytes / (processedBytes / elapsedSeconds);
        }

        return remainingItems / (processedCount / elapsedSeconds);
    }
}
