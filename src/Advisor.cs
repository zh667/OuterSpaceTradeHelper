using System;
using System.Collections.Generic;

namespace TradeHelper
{
    public class TradeIdea
    {
        public string gid, name;
        public GoodsQuote buy, sell;
        public int qty;
        public double cost, revenue;
        public double Profit => revenue - cost;
        public float legDistance;   // buy market -> sell market
        public float fleetDistance; // fleet -> buy market
    }

    public class SellIdea
    {
        public string key, name, location;
        public int have, qty;
        public GoodsQuote market;
        public double revenue, firstUnitNet;
        public float fleetDistance;
    }

    public static class Advisor
    {
        const int MaxSpecialUnits = 40;

        static bool Usable(GoodsQuote q, Config cfg)
        {
            if (q == null || q.contraband) return false;
            if (cfg.onlyFreshInAdvice && q.market.pendingDays > cfg.freshDays) return false;
            return true;
        }

        // Best buy-here / sell-there pairs. For every goods line we keep the three cheapest buy markets and
        // the three best sell markets (by first unit) and search the quantity with the highest profit.
        public static List<TradeIdea> Arbitrage(Snapshot snap, Config cfg, int limit = 60)
        {
            var byGid = new Dictionary<string, List<GoodsQuote>>();
            foreach (var m in snap.markets)
                foreach (var q in m.goods)
                {
                    if (!Usable(q, cfg)) continue;
                    if (q.type == 550) continue; // ships: price depends on defects, handle by hand
                    List<GoodsQuote> l;
                    if (!byGid.TryGetValue(q.gid, out l)) byGid[q.gid] = l = new List<GoodsQuote>();
                    l.Add(q);
                }
            var ideas = new List<TradeIdea>();
            foreach (var kv in byGid)
            {
                var l = kv.Value;
                if (l.Count < 2) continue;
                var buys = l.FindAll(q => q.buyable && q.stock > 0);
                if (buys.Count == 0) continue;
                buys.Sort((a, b) => (a.BuyUnit(0) * (1 + a.tax)).CompareTo(b.BuyUnit(0) * (1 + b.tax)));
                var sells = new List<GoodsQuote>(l);
                sells.Sort((a, b) => (b.SellUnit(0) * (1 - b.tax)).CompareTo(a.SellUnit(0) * (1 - a.tax)));
                TradeIdea best = null;
                for (int bi = 0; bi < Math.Min(3, buys.Count); bi++)
                    for (int si = 0; si < Math.Min(3, sells.Count); si++)
                    {
                        var b = buys[bi]; var s = sells[si];
                        if (b.market == s.market) continue;
                        if (s.SellUnit(0) * (1 - s.tax) <= b.BuyUnit(0) * (1 + b.tax)) continue;
                        var idea = BestQuantity(b, s);
                        if (idea != null && (best == null || idea.Profit > best.Profit)) best = idea;
                    }
                if (best == null || best.Profit <= 0) continue;
                best.name = l[0].name;
                best.legDistance = snap.Distance(best.buy.market.galaxyId, best.sell.market.galaxyId);
                best.fleetDistance = snap.Distance(snap.fleetGalaxyId, best.buy.market.galaxyId);
                ideas.Add(best);
            }
            ideas.Sort((a, b) => b.Profit.CompareTo(a.Profit));
            if (ideas.Count > limit) ideas.RemoveRange(limit, ideas.Count - limit);
            return ideas;
        }

        static TradeIdea BestQuantity(GoodsQuote b, GoodsQuote s)
        {
            int maxUnits = b.special ? Math.Min(b.stock, MaxSpecialUnits) : b.stock;
            // Normal goods: step through quantities in chunks so huge stocks stay cheap to evaluate.
            int step = b.special ? 1 : Math.Max(1, (int)Math.Max(1, Math.Min(b.depth, s.depth) / 10));
            double cost = 0, rev = 0, bestProfit = 0; int bestQ = 0;
            int i = 0;
            while (i < maxUnits)
            {
                int n = Math.Min(step, maxUnits - i);
                double c = 0, r = 0;
                for (int j = 0; j < n; j++) { c += b.BuyUnit(i + j); r += s.SellUnit(i + j); }
                cost += c * (1 + b.tax); rev += r * (1 - s.tax);
                i += n;
                if (rev - cost > bestProfit) { bestProfit = rev - cost; bestQ = i; }
                else if (r * (1 - s.tax) < c * (1 + b.tax)) break; // marginal chunk loses money
            }
            if (bestQ <= 0) return null;
            var idea = new TradeIdea { gid = b.gid, buy = b, sell = s, qty = bestQ };
            idea.cost = b.BuyCost(bestQ);
            idea.revenue = s.SellRevenue(bestQ);
            return idea;
        }

        // Where each stocked item sells best. Sells until the unit price falls under half of the first unit.
        public static List<SellIdea> SellOwnStock(Snapshot snap, Config cfg, int limit = 80)
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
            foreach (var kv in total)
            {
                string gid;
                if (!snap.itemKeyToGid.TryGetValue(kv.Key, out gid)) continue;
                SellIdea best = null;
                foreach (var m in snap.markets)
                {
                    GoodsQuote q;
                    if (!m.byGid.TryGetValue(gid, out q) || !Usable(q, cfg)) continue;
                    double first = q.SellUnit(0) * (1 - q.tax);
                    if (first <= 0) continue;
                    int maxUnits = q.special ? Math.Min(kv.Value, MaxSpecialUnits) : kv.Value;
                    int step = q.special ? 1 : Math.Max(1, (int)(q.depth / 10));
                    double rev = 0; int sold = 0;
                    while (sold < maxUnits)
                    {
                        int n = Math.Min(step, maxUnits - sold);
                        double u = q.SellUnit(sold + n - 1) * (1 - q.tax);
                        if (u < first * 0.5) break;
                        for (int j = 0; j < n; j++) rev += q.SellUnit(sold + j) * (1 - q.tax);
                        sold += n;
                    }
                    if (sold <= 0) continue;
                    if (best == null || rev > best.revenue)
                        best = new SellIdea { key = kv.Key, have = kv.Value, qty = sold, market = q, revenue = rev, firstUnitNet = first };
                }
                if (best == null) continue;
                best.name = MarketScanner.ItemName(snap, kv.Key);
                best.location = where[kv.Key];
                best.fleetDistance = snap.Distance(snap.fleetGalaxyId, best.market.market.galaxyId);
                res.Add(best);
            }
            res.Sort((a, b) => b.revenue.CompareTo(a.revenue));
            if (res.Count > limit) res.RemoveRange(limit, res.Count - limit);
            return res;
        }
    }
}
