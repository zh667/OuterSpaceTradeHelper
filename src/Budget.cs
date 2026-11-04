using System.Diagnostics;
using System.Text;

namespace TradeHelper
{
    // Per-frame time budget shared by all background work, plus cumulative timings for the log.
    public static class Budget
    {
        static readonly Stopwatch frame = new Stopwatch();
        static double limitMs = 4;

        public static void StartFrame(double ms) { limitMs = ms; frame.Restart(); }
        public static bool Over => frame.Elapsed.TotalMilliseconds >= limitMs;

        // Profiling of one scan (ticks).
        public static long tTax, tPool, tBase, tEvent, tDef, tRoute, tTrades, tSells, tInventory;
        public static int nGoods;

        public static void Reset() { tTax = tPool = tBase = tEvent = tDef = tRoute = tTrades = tSells = tInventory = 0; nGoods = 0; }

        static string Ms(long ticks) => (ticks * 1000.0 / Stopwatch.Frequency).ToString("0");

        public static string Report()
        {
            var sb = new StringBuilder();
            sb.Append("goods=").Append(nGoods).Append(" tax=").Append(Ms(tTax)).Append("ms pool=").Append(Ms(tPool))
              .Append("ms base=").Append(Ms(tBase)).Append("ms event=").Append(Ms(tEvent)).Append("ms def=").Append(Ms(tDef))
              .Append("ms route=").Append(Ms(tRoute)).Append("ms inventory=").Append(Ms(tInventory))
              .Append("ms trades=").Append(Ms(tTrades)).Append("ms sells=").Append(Ms(tSells)).Append("ms");
            return sb.ToString();
        }
    }
}
