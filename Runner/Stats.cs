// Statystyki opisowe (docs/02-plan-badania.md, pkt 4.4).
namespace Runner;

internal static class Stats
{
    // Wartości krytyczne t-Studenta dla 95% przedziału ufności (dwustronnie), df = 1..30.
    private static readonly double[] T95 =
    [
        12.706, 4.303, 3.182, 2.776, 2.571, 2.447, 2.365, 2.306, 2.262, 2.228,
        2.201, 2.179, 2.160, 2.145, 2.131, 2.120, 2.110, 2.101, 2.093, 2.086,
        2.080, 2.074, 2.069, 2.064, 2.060, 2.056, 2.052, 2.048, 2.045, 2.042,
    ];

    public static double Mean(IReadOnlyCollection<double> v) => v.Count == 0 ? double.NaN : v.Average();

    public static double Median(IReadOnlyCollection<double> v) => Percentile(v, 50);

    /// <summary>Percentyl z interpolacją liniową między sąsiednimi wartościami.</summary>
    public static double Percentile(IReadOnlyCollection<double> v, double p)
    {
        if (v.Count == 0)
            return double.NaN;
        double[] s = v.Order().ToArray();
        double pos = (s.Length - 1) * p / 100.0;
        int lo = (int)Math.Floor(pos), hi = (int)Math.Ceiling(pos);
        return s[lo] + (s[hi] - s[lo]) * (pos - lo);
    }

    /// <summary>Odchylenie standardowe z próby (n − 1).</summary>
    public static double Std(IReadOnlyCollection<double> v)
    {
        if (v.Count < 2)
            return 0.0;
        double m = v.Average();
        return Math.Sqrt(v.Sum(x => (x - m) * (x - m)) / (v.Count - 1));
    }

    /// <summary>Połowa szerokości 95% przedziału ufności średniej (rozkład t-Studenta).</summary>
    public static double Ci95(IReadOnlyCollection<double> v)
    {
        if (v.Count < 2)
            return double.NaN;
        int df = v.Count - 1;
        double t = df <= T95.Length ? T95[df - 1] : 1.96;
        return t * Std(v) / Math.Sqrt(v.Count);
    }

    /// <summary>Średnia geometryczna (do uśredniania stosunków czasów).</summary>
    public static double GeoMean(IReadOnlyCollection<double> v)
    {
        var valid = v.Where(x => x > 0 && double.IsFinite(x)).ToArray();
        return valid.Length == 0 ? double.NaN : Math.Exp(valid.Average(Math.Log));
    }
}
