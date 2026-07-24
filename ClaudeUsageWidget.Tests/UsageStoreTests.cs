using ClaudeUsageWidget.Core;
using Xunit;

namespace ClaudeUsageWidget.Tests;

public class UsageStoreTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static Func<DateTime> Now(DateTime d)
    {
        return new Func<DateTime>(() => d);
    }

    private static long MonthTotal(UsageStore s)
    {
        (List<(string model, Agg agg, double cost)> rows, double total) m = s.MonthByModel();
        long sum = 0;
        foreach ((string model, Agg agg, double cost) row in m.rows) sum += row.agg.Total;
        return sum;
    }

    [Fact]
    public void TodayAndMonth_AreScopedCorrectly()
    {
        string dir = TestUtil.NewTempDir();
        string cache = Path.Combine(TestUtil.NewTempDir(), "c.json");
        DateTime mtime = new DateTime(2026, 7, 20, 12, 0, 0);
        TestUtil.WriteJsonl(dir, "s.jsonl", new[]
        {
            TestUtil.Line("t1", "claude-fable-5", "2026-07-20T09:00:00Z", 100, 0, 0, 0), // today
            TestUtil.Line("t2", "claude-fable-5", "2026-07-10T09:00:00Z", 50, 0, 0, 0),  // this month
            TestUtil.Line("t3", "claude-fable-5", "2026-06-30T09:00:00Z", 999, 0, 0, 0)  // previous month -> excluded
        }, mtime);

        UsageStore s = new UsageStore(dir, cache, Now(new DateTime(2026, 7, 20, 12, 0, 0)), Utc);
        s.Scan();

        (Agg agg, double cost) today = s.Today();
        Assert.Equal(100, today.agg.Total);      // only t1 is "today"
        Assert.Equal(150, MonthTotal(s));         // t1 + t2, NOT the June t3
        Assert.Equal("2026-07", s.Month);
    }

    [Fact]
    public void CrossFileDuplicates_CountedOnceCanonically()
    {
        string dir = TestUtil.NewTempDir();
        string cache = Path.Combine(TestUtil.NewTempDir(), "c.json");
        DateTime mtime = new DateTime(2026, 7, 20, 12, 0, 0);
        // same message id in two files, different usage -> canonical (max total) counted once
        TestUtil.WriteJsonl(dir, "a.jsonl", new[] { TestUtil.Line("dup", "claude-fable-5", "2026-07-20T09:00:00Z", 10, 0, 0, 0) }, mtime);
        TestUtil.WriteJsonl(dir, "b.jsonl", new[] { TestUtil.Line("dup", "claude-fable-5", "2026-07-20T09:00:00Z", 100, 0, 0, 0) }, mtime);

        UsageStore s = new UsageStore(dir, cache, Now(new DateTime(2026, 7, 20, 12, 0, 0)), Utc);
        s.Scan();

        Assert.Equal(100, s.Today().agg.Total);   // not 110
    }

    [Fact]
    public void MonthRollover_RebuildsAndAgesOutOldFiles()
    {
        string dir = TestUtil.NewTempDir();
        string cache = Path.Combine(TestUtil.NewTempDir(), "c.json");
        DateTime julyMtime = new DateTime(2026, 7, 20, 12, 0, 0);
        TestUtil.WriteJsonl(dir, "s.jsonl", new[]
        {
            TestUtil.Line("t1", "claude-fable-5", "2026-07-20T09:00:00Z", 100, 0, 0, 0)
        }, julyMtime);

        UsageStore july = new UsageStore(dir, cache, Now(new DateTime(2026, 7, 20, 12, 0, 0)), Utc);
        july.Scan();
        Assert.Equal(100, MonthTotal(july));

        // same folder, but now it is August; the July file (mtime 07-20) is older than the month start
        UsageStore august = new UsageStore(dir, cache, Now(new DateTime(2026, 8, 5, 12, 0, 0)), Utc);
        august.Scan();
        Assert.Equal("2026-08", august.Month);
        Assert.Equal(0, MonthTotal(august));
        Assert.Equal(0, august.Today().agg.Total);
    }

    [Fact]
    public void Cache_NotRewrittenWhenNothingChanged()
    {
        string dir = TestUtil.NewTempDir();
        string cache = Path.Combine(TestUtil.NewTempDir(), "c.json");
        DateTime mtime = new DateTime(2026, 7, 20, 12, 0, 0);
        TestUtil.WriteJsonl(dir, "s.jsonl", new[]
        {
            TestUtil.Line("t1", "claude-fable-5", "2026-07-20T09:00:00Z", 100, 0, 0, 0)
        }, mtime);

        UsageStore s = new UsageStore(dir, cache, Now(new DateTime(2026, 7, 20, 12, 0, 0)), Utc);
        s.Scan();
        Assert.True(File.Exists(cache));
        DateTime firstWrite = File.GetLastWriteTimeUtc(cache);

        System.Threading.Thread.Sleep(30);
        s.Scan(); // nothing changed -> cache must not be rewritten

        Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(cache));
    }

    [Fact]
    public void PerModelBreakdown_SumsAndSortsByCost()
    {
        string dir = TestUtil.NewTempDir();
        string cache = Path.Combine(TestUtil.NewTempDir(), "c.json");
        DateTime mtime = new DateTime(2026, 7, 20, 12, 0, 0);
        TestUtil.WriteJsonl(dir, "s.jsonl", new[]
        {
            TestUtil.Line("m1", "claude-opus-4-8", "2026-07-20T09:00:00Z", 1_000_000, 0, 0, 0), // opus: $15
            TestUtil.Line("m2", "claude-haiku-4-5", "2026-07-20T09:00:00Z", 1_000_000, 0, 0, 0) // haiku: $0.80
        }, mtime);

        UsageStore s = new UsageStore(dir, cache, Now(new DateTime(2026, 7, 20, 12, 0, 0)), Utc);
        s.Scan();

        (List<(string model, Agg agg, double cost)> rows, double total) m = s.MonthByModel();
        Assert.Equal(2, m.rows.Count);
        Assert.Equal("opus-4-8", m.rows[0].model); // higher cost first
        Assert.Equal("haiku-4-5", m.rows[1].model);
        Assert.Equal(15.80, m.total, 4);
    }
}
