namespace ClaudeUsageWidget.Core;

/// <summary>
/// Rough, notional API-equivalent pricing (USD per 1M tokens). This is NOT a bill - a
/// subscription is a flat fee. Fable's public price is unknown, so it is priced at the Opus
/// tier (runs high); adjust here if you have better numbers. Use ccusage for authoritative $.
/// </summary>
public static class Pricing
{
    public readonly struct Rate
    {
        public readonly double Input;
        public readonly double Output;
        public readonly double CacheWrite;
        public readonly double CacheRead;

        public Rate(double input, double output, double cacheWrite, double cacheRead)
        {
            Input = input;
            Output = output;
            CacheWrite = cacheWrite;
            CacheRead = cacheRead;
        }
    }

    public static Rate For(string model)
    {
        string m = (model ?? "").ToLowerInvariant();
        if (m.Contains("haiku")) return new Rate(0.80, 4.0, 1.00, 0.08);
        if (m.Contains("sonnet")) return new Rate(3.0, 15.0, 3.75, 0.30);
        return new Rate(15.0, 75.0, 18.75, 1.50); // opus / fable / default
    }

    public static double Cost(string model, Agg a)
    {
        Rate p = For(model);
        return a.In / 1e6 * p.Input
             + a.Out / 1e6 * p.Output
             + a.CacheWrite / 1e6 * p.CacheWrite
             + a.CacheRead / 1e6 * p.CacheRead;
    }
}
