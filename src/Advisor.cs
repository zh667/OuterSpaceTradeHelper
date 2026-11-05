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
        public int planBuyOffset, planSellOffset; // units already claimed at these stations by the player's open plans
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

        // Contraband is never offered (the game refuses both buying and selling it). Stale markets are included
        // only when the player allows them; every row shows how stale its markets are.
        static bool Usable(GoodsQuote q, Config cfg)
        {
            if (q == null || q.contraband) return false;
            if (!q.market.Explored && !cfg.includeUndiscovered) return false;
            if (q.market.planet && !cfg.includeGround) return false;
            if (!q.market.Exact && q.market.pendingDays > cfg.maxStaleDays) return false;
            return true;
        }

        public static IEnumerator Arbitrage(Snapshot snap, Config cfg)
        {
            var byGid = new Dictionary<string, List<GoodsQuote>>();
            foreach (var m in snap.markets)
                foreach (var q in m.goods)
                {
                    if (!Usable(q, cfg)) continue;
                    List<GoodsQuote> l;
                    if (!byGid.TryGetValue(q.gid, out l)) byGid[q.gid] = l = new List<GoodsQuote>();
                    l.Add(q);
                }
            // Units the player's open plans will still buy/sell at a station: new advice starts after them, so it does
            // not count on the same cheap stock or the same buyer twice.
            var plannedBuy = new Dictionary<string, int>();
            var plannedSell = new Dictionary<string, int>();
            foreach (var p in Plans.list)
            {
                if (p.state == 2) continue;
                if (p.state == 0 && p.buyUid >= 0) { var k = p.buyUid + "|" + p.gid; int v; plannedBuy.TryGetValue(k, out v); plannedBuy[k] = v + p.qty; }
                { var k = p.sellUid + "|" + p.gid; int v; plannedSell.TryGetValue(k, out v); plannedSell[k] = v + p.HoldQty; }
            }
            var ideas = new List<TradeIdea>();
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (var kv in byGid)
            {
                if (Budget.Over) { Budget.tTrades += System.Diagnostics.Stopwatch.GetTimestamp() - t0; yield return null; t0 = System.Diagnostics.Stopwatch.GetTimestamp(); }
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
                        int ob, os;
                        plannedBuy.TryGetValue(b.market.uid + "|" + b.gid, out ob);
                        plannedSell.TryGetValue(s.market.uid + "|" + s.gid, out os);
                        var idea = BestQuantity(b, s, ob, os);
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
            Budget.tTrades += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        }

        // The marginal profit of the i-th unit (sell price after tax minus buy price with tax) never increases with
        // i: buying lowers the seller's stock (price up) and selling raises the buyer's stock (price down). So the best
        // quantity is the last unit whose marginal profit is still positive; a binary search finds exactly the same
        // quantity as walking unit by unit.
        static TradeIdea BestQuantity(GoodsQuote b, GoodsQuote s, int ob, int os)
        {
            int maxUnits = (b.special ? Math.Min(b.stock, MaxSpecialUnits) : b.stock) - ob;
            if (maxUnits <= 0) return null;
            // Special goods carry the durability of the items bought into the sale.
            Func<int, double> marg = i => s.SellBoughtUnit(os, b, ob, i) * (1 - s.tax) - b.BuyUnit(i + ob) * (1 + b.tax);
            if (marg(0) <= 0) return null;
            int lo = 0, hi = maxUnits - 1;
            while (lo < hi)
            {
                int mid = lo + (hi - lo + 1) / 2;
                if (marg(mid) > 0) lo = mid; else hi = mid - 1;
            }
            int q = lo + 1;
            var idea = new TradeIdea { gid = b.gid, buy = b, sell = s, qty = q, planBuyOffset = ob, planSellOffset = os };
            // Exact game quotes when the market is up to date; units already claimed by plans are priced first.
            idea.cost = b.BuyCost(ob + q) - (ob > 0 ? b.BuyCost(ob) : 0);
            idea.revenue = s.SellBoughtRevenue(os, b, ob, q);
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
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (var kv in total)
            {
                if (Budget.Over) { Budget.tSells += System.Diagnostics.Stopwatch.GetTimestamp() - t0; yield return null; t0 = System.Diagnostics.Stopwatch.GetTimestamp(); }
                string gid;
                if (kv.Key.StartsWith("550_")) continue;   // the player's own warships are not stock to sell off
                if (!snap.itemKeyToGid.TryGetValue(kv.Key, out gid)) continue;
                SellIdea best = null;
                foreach (var m in snap.markets)
                {
                    if (Budget.Over) { Budget.tSells += System.Diagnostics.Stopwatch.GetTimestamp() - t0; yield return null; t0 = System.Diagnostics.Stopwatch.GetTimestamp(); }
                    GoodsQuote q;
                    if (!m.byGid.TryGetValue(gid, out q) || !Usable(q, cfg)) continue;
                    double first = q.sell1;
                    if (first <= 0) continue;
                    int maxUnits = q.special ? Math.Min(kv.Value, MaxSpecialUnits) : kv.Value;
                    // Unit price only falls as we sell, so find the last unit still worth at least half the first.
                    int lo = 0, hi = maxUnits - 1;
                    while (lo < hi)
                    {
                        int mid = lo + (hi - lo + 1) / 2;
                        if (q.SellUnit(mid) * (1 - q.tax) >= first * 0.5) lo = mid; else hi = mid - 1;
                    }
                    int sold = lo + 1;
                    double rev = q.SellHeld(0, sold, snap.HeldSorted(kv.Key, q.specialGoods));
                    if (sold <= 0) continue;
                    if (best == null || rev > best.revenue)
                        best = new SellIdea { key = kv.Key, have = kv.Value, qty = sold, market = q, revenue = rev, firstUnitNet = first };
                }
                if (best == null) continue;
                best.name = MarketScanner.ItemName(snap, kv.Key);
                best.category = best.market.category;
                best.location = where[kv.Key];
                best.fleetAu = best.market.market.fleetAu;
                res.Add(best);
            }
            res.Sort((a, b) => b.revenue.CompareTo(a.revenue));
            snap.sells = res;
            Budget.tSells += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        }
    }
}
