using ClaudeUsageWidget.Core;
using Xunit;

namespace ClaudeUsageWidget.Tests;

public class DedupTests
{
    private static UsageRecord Rec(string id, long inp, long outp, long cr, long cw)
    {
        return new UsageRecord { Id = id, DateLocal = "2026-07-20", Model = "fable-5", In = inp, Out = outp, CacheRead = cr, CacheWrite = cw };
    }

    [Fact]
    public void ConflictingDuplicates_KeepMaxTotal()
    {
        // same id, different usage -> canonical is the record with the greatest total
        List<UsageRecord> input = new List<UsageRecord>
        {
            Rec("a", 10, 10, 10, 10),   // total 40
            Rec("a", 100, 0, 0, 0),     // total 100  <-- winner
            Rec("a", 20, 20, 0, 0)      // total 40
        };
        List<UsageRecord> outp = Dedup.Canonical(input);
        Assert.Single(outp);
        Assert.Equal(100, outp[0].Total);
        Assert.Equal(100, outp[0].In);
    }

    [Fact]
    public void ResultIsIndependentOfOrder()
    {
        UsageRecord[] recs =
        {
            Rec("a", 10, 10, 10, 10),
            Rec("a", 100, 0, 0, 0),
            Rec("b", 5, 5, 5, 5),
            Rec("a", 20, 20, 0, 0),
            Rec("b", 500, 0, 0, 0)
        };

        long TotalFor(IEnumerable<UsageRecord> src, string id)
        {
            long sum = 0;
            foreach (UsageRecord r in Dedup.Canonical(src.ToList()))
                if (r.Id == id) sum += r.Total;
            return sum;
        }

        long a1 = TotalFor(recs, "a");
        long b1 = TotalFor(recs, "b");

        // reverse order must give identical canonical selection
        List<UsageRecord> reversed = recs.Reverse().ToList();
        Assert.Equal(a1, TotalFor(reversed, "a"));
        Assert.Equal(b1, TotalFor(reversed, "b"));
        Assert.Equal(100, a1);
        Assert.Equal(500, b1);
    }

    [Fact]
    public void EmptyIdRecordsAreAllKept()
    {
        List<UsageRecord> input = new List<UsageRecord>
        {
            Rec("", 1, 0, 0, 0),
            Rec("", 2, 0, 0, 0),
            Rec("", 3, 0, 0, 0)
        };
        List<UsageRecord> outp = Dedup.Canonical(input);
        Assert.Equal(3, outp.Count);
    }

    [Fact]
    public void TieBrokenDeterministicallyByOutput()
    {
        // equal total, different breakdown -> higher Output wins
        List<UsageRecord> input = new List<UsageRecord>
        {
            Rec("a", 40, 0, 0, 0),   // total 40, out 0
            Rec("a", 0, 40, 0, 0)    // total 40, out 40 <-- winner
        };
        List<UsageRecord> outp = Dedup.Canonical(input);
        Assert.Single(outp);
        Assert.Equal(40, outp[0].Out);
    }
}
