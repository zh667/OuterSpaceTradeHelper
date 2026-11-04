using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace TradeHelper
{
    [Serializable]
    public class PlanPoint
    {
        public string time;      // real clock
        public long gameMM;      // game clock
        public double cost, revenue, profit;
        public int buyPending, sellPending;
    }

    // A trade the player chose to follow: buy qty of a goods line at one station, sell at another.
    [Serializable]
    public class Plan
    {
        public string id;
        public string gid, key, name;           // key = "type_id" in the player's inventory
        public int qty;
        public long buyUid = -1, sellUid;
        public string buyName, sellName, buyGalaxy, sellGalaxy;
        public string createdAt;
        public long createdMM;
        public double planCost, planRevenue;
        public int ownedAtStart;
        public int state;                       // 0 = to buy, 1 = bought (holding), 2 = done
        public int boughtQty;
        public bool keepFresh = true;
        public List<long> addedKeepFresh = new List<long>();
        public List<PlanPoint> history = new List<PlanPoint>();

        // Live values (not saved).
        [NonSerialized] public double curCost = double.NaN, curRevenue = double.NaN;
        [NonSerialized] public int buyStock = -1, owned;
        [NonSerialized] public MarketInfo buyMarket, sellMarket;
        [NonSerialized] public List<KeyValuePair<MarketInfo, double>> alternatives = new List<KeyValuePair<MarketInfo, double>>();
        [NonSerialized] public double fleetToNextAu = -1;
        [NonSerialized] public string note = "";

        public double PlanProfit => planRevenue - planCost;
        public double CurProfit => state == 0 ? curRevenue - curCost : curRevenue - planCost;
        public int HoldQty => state == 0 ? qty : Math.Max(1, boughtQty);
        public string StateText => state == 0 ? "待买入" : state == 1 ? "已买入 " + boughtQty + " 个" : "已完成";
    }

    [Serializable]
    class PlanFile { public List<Plan> plans = new List<Plan>(); }

    public static class Plans
    {
        public static List<Plan> list = new List<Plan>();
        static string Path => System.IO.Path.Combine(Config.Dir, "plans.json");
        const int HistoryMax = 60;

        public static void Load()
        {
            try
            {
                if (File.Exists(Path))
                {
                    var f = JsonUtility.FromJson<PlanFile>(File.ReadAllText(Path));
                    if (f?.plans != null) list = f.plans;
                }
            }
            catch (Exception e) { Debug.LogWarning("[TradeHelper] plans read failed: " + e.Message); }
            foreach (var p in list)
            {
                if (p.history == null) p.history = new List<PlanPoint>();
                if (p.addedKeepFresh == null) p.addedKeepFresh = new List<long>();
            }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Config.Dir);
                File.WriteAllText(Path, JsonUtility.ToJson(new PlanFile { plans = list }, true));
            }
            catch (Exception e) { Debug.LogWarning("[TradeHelper] plans write failed: " + e.Message); }
        }

        static void KeepFresh(Plan p, long uid, Config cfg, Settlement st)
        {
            if (uid < 0) return;
            if (!cfg.keepFresh.Contains(uid)) { cfg.keepFresh.Add(uid); p.addedKeepFresh.Add(uid); }
            st.Enqueue(uid);
        }

        public static void ApplyKeepFresh(Plan p, Config cfg, Settlement st)
        {
            if (p.keepFresh)
            {
                if (p.state == 0) KeepFresh(p, p.buyUid, cfg, st);
                if (p.state != 2) KeepFresh(p, p.sellUid, cfg, st);
            }
            else ReleaseKeepFresh(p, cfg);
            cfg.Save();
        }

        // Undo only the "keep up to date" ticks this plan added, and only if no other open plan still needs them.
        static void ReleaseKeepFresh(Plan p, Config cfg)
        {
            foreach (var uid in new List<long>(p.addedKeepFresh))
            {
                bool needed = list.Exists(o => o != p && o.state != 2 && o.keepFresh && (o.buyUid == uid || o.sellUid == uid));
                if (!needed) cfg.keepFresh.Remove(uid);
                p.addedKeepFresh.Remove(uid);
            }
        }

        public static Plan FromTrade(TradeIdea t, Snapshot snap, Config cfg, Settlement st)
        {
            var key = t.buy.type + "_" + t.buy.itemId;
            var p = new Plan
            {
                id = Guid.NewGuid().ToString("N"), gid = t.gid, key = key, name = t.name, qty = t.qty,
                buyUid = t.buy.market.uid, buyName = t.buy.market.name, buyGalaxy = t.buy.market.galaxyId,
                sellUid = t.sell.market.uid, sellName = t.sell.market.name, sellGalaxy = t.sell.market.galaxyId,
                createdAt = DateTime.Now.ToString("MM-dd HH:mm"), createdMM = GameMM(),
                planCost = t.cost, planRevenue = t.revenue, ownedAtStart = snap.Owned(key), state = 0,
            };
            list.Add(p);
            ApplyKeepFresh(p, cfg, st);
            Save();
            return p;
        }

        public static Plan FromSell(SellIdea s, Snapshot snap, Config cfg, Settlement st)
        {
            var p = new Plan
            {
                id = Guid.NewGuid().ToString("N"), gid = s.market.gid, key = s.key, name = s.name, qty = s.qty,
                sellUid = s.market.market.uid, sellName = s.market.market.name, sellGalaxy = s.market.market.galaxyId,
                buyName = "（已持有）", createdAt = DateTime.Now.ToString("MM-dd HH:mm"), createdMM = GameMM(),
                planCost = 0, planRevenue = s.revenue, ownedAtStart = snap.Owned(s.key), state = 1, boughtQty = s.qty,
            };
            list.Add(p);
            ApplyKeepFresh(p, cfg, st);
            Save();
            return p;
        }

        public static void Remove(Plan p, Config cfg)
        {
            ReleaseKeepFresh(p, cfg);
            list.Remove(p);
            cfg.Save();
            Save();
        }

        public static void MarkBought(Plan p, Config cfg, Settlement st)
        {
            p.state = 1;
            if (p.boughtQty <= 0) p.boughtQty = p.qty;
            ReleaseBuySide(p, cfg);
            Save();
        }

        public static void MarkDone(Plan p, Config cfg)
        {
            p.state = 2;
            ReleaseKeepFresh(p, cfg);
            cfg.Save();
            Save();
        }

        static void ReleaseBuySide(Plan p, Config cfg)
        {
            if (!p.addedKeepFresh.Contains(p.buyUid) || p.buyUid == p.sellUid) return;
            bool needed = list.Exists(o => o != p && o.state != 2 && o.keepFresh && (o.buyUid == p.buyUid || o.sellUid == p.buyUid));
            if (!needed) cfg.keepFresh.Remove(p.buyUid);
            p.addedKeepFresh.Remove(p.buyUid);
            cfg.Save();
        }

        static long GameMM()
        {
            try { return MarketScanner.Game?.elapsedMM ?? 0; } catch { return 0; }
        }

        // Re-price every open plan against a fresh snapshot.
        public static void Track(Snapshot snap, Config cfg, Settlement st)
        {
            bool changed = false;
            foreach (var p in list)
            {
                p.note = "";
                p.buyMarket = snap.markets.Find(m => m.uid == p.buyUid);
                p.sellMarket = snap.markets.Find(m => m.uid == p.sellUid);
                p.owned = snap.Owned(p.key);
                GoodsQuote bq = null, sq = null;
                if (p.buyMarket != null) p.buyMarket.byGid.TryGetValue(p.gid, out bq);
                if (p.sellMarket != null) p.sellMarket.byGid.TryGetValue(p.gid, out sq);

                // Detect the purchase from the inventory: the holding grew since the plan was made.
                if (p.state == 0 && p.owned - p.ownedAtStart >= 1)
                {
                    p.state = 1;
                    p.boughtQty = Math.Min(p.qty, p.owned - p.ownedAtStart);
                    ReleaseBuySide(p, cfg);
                    changed = true;
                }
                else if (p.state == 1 && p.buyUid >= 0 && p.owned <= p.ownedAtStart)
                {
                    // Holding back to where it was: sold (or moved somewhere this panel does not see).
                    p.note = "库存里已经看不到这批货：卖掉了的话点“完成”";
                }

                int hold = p.HoldQty;
                if (p.state == 0)
                {
                    if (bq == null) { p.curCost = double.NaN; p.note = "买入站现在没有这个货"; }
                    else
                    {
                        p.buyStock = bq.stock;
                        // If the station now has fewer than planned, price what can actually be bought.
                        hold = Math.Min(p.qty, Math.Max(0, bq.stock));
                        p.curCost = hold > 0 ? bq.BuyCost(hold) : double.NaN;
                        if (bq.stock < p.qty) p.note = "买入站库存只剩 " + bq.stock + " 个（按能买到的数量算）";
                        if (bq.contraband) p.note = "买入站现在把它列为违禁品，买不了";
                    }
                }
                p.curRevenue = sq != null && !sq.contraband && hold > 0 ? sq.SellRevenue(hold) : double.NaN;
                if (sq != null && sq.contraband) p.note = "卖出站现在把它列为违禁品，卖不了";

                // Best places to sell the holding right now.
                p.alternatives.Clear();
                foreach (var m in snap.markets)
                {
                    GoodsQuote q;
                    if (!m.byGid.TryGetValue(p.gid, out q) || q.contraband) continue;
                    if (hold > 0) p.alternatives.Add(new KeyValuePair<MarketInfo, double>(m, q.SellRevenue(hold)));
                }
                p.alternatives.Sort((a, b) => b.Value.CompareTo(a.Value));
                if (p.alternatives.Count > 5) p.alternatives.RemoveRange(5, p.alternatives.Count - 5);

                string next = p.state == 0 ? p.buyGalaxy : p.sellGalaxy;
                p.fleetToNextAu = snap.RouteAu(snap.fleetGalaxyId, next);

                if (p.state != 2 && !double.IsNaN(p.curRevenue))
                {
                    var pt = new PlanPoint
                    {
                        time = DateTime.Now.ToString("HH:mm:ss"), gameMM = GameMM(),
                        cost = p.state == 0 ? p.curCost : p.planCost, revenue = p.curRevenue, profit = p.CurProfit,
                        buyPending = p.buyMarket != null ? p.buyMarket.pendingDays : -1, sellPending = p.sellMarket != null ? p.sellMarket.pendingDays : -1,
                    };
                    var last = p.history.Count > 0 ? p.history[p.history.Count - 1] : null;
                    if (last == null || Math.Abs(last.profit - pt.profit) > 0.5 || last.gameMM != pt.gameMM)
                    {
                        p.history.Add(pt);
                        if (p.history.Count > HistoryMax) p.history.RemoveRange(0, p.history.Count - HistoryMax);
                        changed = true;
                    }
                }
            }
            if (changed) Save();
        }
    }
}
