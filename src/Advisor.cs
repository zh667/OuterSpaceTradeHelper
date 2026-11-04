using System;
using System.Collections;
using System.Collections.Generic;

namespace TradeHelper
{
    public class TradeIdea
    {
        public string gid, name, category;
        public GoodsQuote buy, sell;
        public int qty;
        public double cost, revenue;
        public double Profit => revenue - cost;
        public double Margin => cost > 0 ? Profit / cost : 0;
        public double legAu = -1;    // buy market -> sell market, game route distance
        public double fleetAu = -1;  // player fleet -> buy market
        public double TotalAu => (legAu < 0 || fleetAu < 0) ? -1 : legAu + fleetAu;
        public bool Stale => !buy.market.Exact || !sell.market.Exact;
        public int MaxPending => Math.Max(buy.market.pendingDays, sell.market.pendingDays);
    }

    public class SellIdea
    {
        public string key, name, location, category;
        public int have, qty;
        public GoodsQuote market;
        public double revenue, firstUnitNet;
        public double fleetAu = -1;
    }

    public static class Advisor
    {
        const int MaxSpecialUnits = 40;
        const int GoodsPerStep = 25;

        // Contraband is never offered (the game refuses both buying and selling it). Stale markets are included
        // only when the player allows them; every row shows how stale its markets are.
        static bool Usable(GoodsQuote q, Config cfg)
        {
            if (q == null || q.contraband) return false;
            if (!q.market.Exact && q.market.pendingDays > cfg.maxStaleDays) return false;
            return true;
        }

        public static IEnumerator Arbitrage(Snapshot snap, Config cfg)
        {
            var byGid = new Dictionary<string, List<GoodsQuote>>();
            foreach (var m in snap.markets)
                foreach (var q in m.goods)
                {
                    if (!Usable(q, cfg) || q.type == 550) continue; // ships: price depends on defects
                    List<GoodsQuote> l;
                    if (!byGid.TryGetValue(q.gid, out l)) byGid[q.gid] = l = new List<GoodsQuote>();
                    l.Add(q);
                }
            var ideas = new List<TradeIdea>();
            int n = 0;
            foreach (var kv in byGid)
            {
                if (++n % GoodsPerStep == 0) yield return null;
                var l = kv.Value;
                if (l.Count < 2) continue;
                var buys = l.FindAll(q => q.buyable && q.stock > 0);
                if (buys.Count == 0) continue;
                buys.Sort((a, b) => a.buy1.CompareTo(b.buy1));
                var sells = new List<GoodsQuote>(l);
                sells.Sort((a, b) => b.sell1.CompareTo(a.sell1));
                TradeIdea best = null;
                for (int bi = 0; bi < Math.Min(4, buys.Count); bi++)
                    for (int si = 0; si < Math.Min(4, sells.Count); si++)
                    {
                        var b = buys[bi]; var s = sells[si];
                        if (b.market == s.market || s.sell1 <= b.buy1) continue;
                        var idea = BestQuantity(b, s);
                        if (idea != null && (best == null || idea.Profit > best.Profit)) best = idea;
                    }
                if (best == null || best.Profit <= 0) continue;
                best.name = l[0].name;
                best.category = l[0].category;
                best.legAu = snap.RouteAu(best.buy.market.galaxyId, best.sell.market.galaxyId);
                best.fleetAu = best.buy.market.fleetAu;
                ideas.Add(best);
            }
            ideas.Sort((a, b) => b.Profit.CompareTo(a.Profit));
            snap.trades = ideas;
        }

        static TradeIdea BestQuantity(GoodsQuote b, GoodsQuote s)
        {
            int maxUnits = b.special ? Math.Min(b.stock, MaxSpecialUnits) : b.stock;
            int step = b.special ? 1 : Math.Max(1, (int)Math.Max(1, Math.Min(b.depth, s.depth) / 10));
            double cost = 0, rev = 0, bestProfit = 0; int bestQ = 0;
            int i = 0;
            while (i < maxUnits)
            {
                int k = Math.Min(step, maxUnits - i);
                double c = 0, r = 0;
                for (int j = 0; j < k; j++) { c += b.BuyUnit(i + j); r += s.SellUnit(i + j); }
                cost += c * (1 + b.tax); rev += r * (1 - s.tax);
                i += k;
                if (rev - cost > bestProfit) { bestProfit = rev - cost; bestQ = i; }
                else if (r * (1 - s.tax) < c * (1 + b.tax)) break; // this chunk already loses money
            }
            if (bestQ <= 0) return null;
            var idea = new TradeIdea { gid = b.gid, buy = b, sell = s, qty = bestQ };
            idea.cost = b.BuyCost(bestQ);       // exact game quote when the market is up to date
            idea.revenue = s.SellRevenue(bestQ);
            return idea;
        }

        // Where each stocked item sells best: sell until the unit price falls under half of the first unit.
        public static IEnumerator SellOwnStock(Snapshot snap, Config cfg)
        {
            var total = new Dictionary<string, int>();
            var where = new Dictionary<string, string>();
            foreach (var loc in snap.inventory)
                foreach (var kv in loc.items)
                {
                    int v; total.TryGetValue(kv.Key, out v); total[kv.Key] = v + kv.Value;
                    string w; where.TryGetValue(kv.Key, out w);
                    where[kv.Key] = string.IsNullOrEmpty(w) ? loc.name : (w.Contains(loc.name) ? w : w + "、" + loc.name);
                }
            var res = new List<SellIdea>();
            int n = 0;
            foreach (var kv in total)
            {
                if (++n % GoodsPerStep == 0) yield return null;
                string gid;
                if (!snap.itemKeyToGid.TryGetValue(kv.Key, out gid)) continue;
                SellIdea best = null;
                foreach (var m in snap.markets)
                {
                    GoodsQuote q;
                    if (!m.byGid.TryGetValue(gid, out q) || !Usable(q, cfg)) continue;
                    double first = q.sell1;
                    if (first <= 0) continue;
                    int maxUnits = q.special ? Math.Min(kv.Value, MaxSpecialUnits) : kv.Value;
                    int step = q.special ? 1 : Math.Max(1, (int)(q.depth / 10));
                    double rev = 0; int sold = 0;
                    while (sold < maxUnits)
                    {
                        int k = Math.Min(step, maxUnits - sold);
                        double u = q.SellUnit(sold + k - 1) * (1 - q.tax);
                        if (u < first * 0.5) break;
                        for (int j = 0; j < k; j++) rev += q.SellUnit(sold + j) * (1 - q.tax);
                        sold += k;
                    }
                    if (sold <= 0) continue;
                    if (best == null || rev > best.revenue)
                        best = new SellIdea { key = kv.Key, have = kv.Value, qty = sold, market = q, revenue = rev, firstUnitNet = first };
                }
                if (best == null) continue;
                best.revenue = best.market.SellRevenue(best.qty); // exact game quote when possible
                best.name = MarketScanner.ItemName(snap, kv.Key);
                best.category = best.market.category;
                best.location = where[kv.Key];
                best.fleetAu = best.market.market.fleetAu;
                res.Add(best);
            }
            res.Sort((a, b) => b.revenue.CompareTo(a.revenue));
            snap.sells = res;
        }
    }
}
