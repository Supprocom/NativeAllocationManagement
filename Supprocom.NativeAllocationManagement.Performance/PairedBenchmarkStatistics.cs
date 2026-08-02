namespace Supprocom.NativeAllocationManagement.Performance;

internal static class PairedBenchmarkStatistics
{
    internal static double ConfidenceLower95(
        IReadOnlyCollection<double> samples)
    {
        if (samples.Count < 2)
        {
            return double.NaN;
        }

        double mean = samples.Average();
        double sumSquares = samples.Sum(value =>
            (value - mean) * (value - mean));
        double standardDeviation = Math.Sqrt(
            sumSquares / (samples.Count - 1));
        return mean - StudentCritical95(samples.Count - 1)
            * standardDeviation
            / Math.Sqrt(samples.Count);
    }

    internal static double LogicalGigabytesPerSecond(
        long logicalBytes,
        double elapsedMilliseconds) =>
        logicalBytes
        / (1024d * 1024d * 1024d)
        / (elapsedMilliseconds / 1000d);

    private static double StudentCritical95(
        int degreesOfFreedom) =>
        degreesOfFreedom switch
        {
            1 => 12.706,
            2 => 4.303,
            3 => 3.182,
            4 => 2.776,
            5 => 2.571,
            6 => 2.447,
            7 => 2.365,
            8 => 2.306,
            9 => 2.262,
            10 => 2.228,
            11 => 2.201,
            12 => 2.179,
            13 => 2.160,
            14 => 2.145,
            15 => 2.131,
            16 => 2.120,
            17 => 2.110,
            18 => 2.101,
            19 => 2.093,
            20 => 2.086,
            21 => 2.080,
            22 => 2.074,
            23 => 2.069,
            24 => 2.064,
            25 => 2.060,
            26 => 2.056,
            27 => 2.052,
            28 => 2.048,
            29 => 2.045,
            _ => 1.96
        };
}
