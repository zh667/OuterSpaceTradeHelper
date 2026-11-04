using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace TradeHelper
{
    // Automatic checks for the isolated test copy of the game (tools/run_isolated.py). Runs only when
    // <game>/TradeHelperSelfTest.json exists, and refuses to do anything unless the game keeps its data outside the
    // player's folder (the test copy's product name is "OS TH Tests"). Loads the named save, waits for the mod's first
    // scan, compares the mod's numbers with the game's own price functions, measures scans, takes screenshots, writes
    // <game>/selftest-result.json and quits.
    public class SelfTest : MonoBehaviour
    {
        class Request { public string save = "zh6657"; public string tests = "all"; }

        public class Check
        {
            public string name;
            public bool passed;
            public bool info;          // measured and reported, not part of pass/fail
            public string summary;
            public List<string> details = new List<string>();
        }

        class Result
        {
            public string version = TradeHelperBehaviour.Version;
            public string startedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            public string dataPath;
            public bool passed;
            public string error;
            public List<Check> checks = new List<Check>();
            public Dictionary<string, object> perf = new Dictionary<string, object>();
            public List<string> screenshots = new List<string>();
        }

        static string GameDir => Path.GetDirectoryName(Application.dataPath);
        static string RequestPath => Path.Combine(GameDir, "TradeHelperSelfTest.json");
        public static bool Requested() { try { return File.Exists(RequestPath); } catch { return false; } }

        Request req;
        readonly Result result = new Result();
        TradeHelperBehaviour host;

        void Log(string s) { Debug.Log("[TradeHelper] selftest: " + s); }

        IEnumerator Start()
        {
            host = GetComponent<TradeHelperBehaviour>();
            Application.runInBackground = true;
            try { req = JsonConvert.DeserializeObject<Request>(File.ReadAllText(RequestPath)) ?? new Request(); }
            catch { req = new Request(); }
            result.dataPath = Application.persistentDataPath;
            Log("data path " + result.dataPath);
            if (!result.dataPath.Replace('\\', '/').EndsWith("/OS TH Tests"))
            {
                // Never run against the player's own data.
                Finish("refused: data path is not the isolated test folder");
                yield break;
            }

            // Main menu: wait until the archive list can be read and contains the save.
            ArchiveInfo info = null;
            float t0 = Time.realtimeSinceStartup;
            while (info == null && Time.realtimeSinceStartup - t0 < 300)
            {
                yield return new WaitForSecondsRealtime(3);
                if (Time.realtimeSinceStartup < 15 || UserBean.tryGetInstance() == null) continue;
                try
                {
                    var list = ArchiveManager.scanUserDatas(false);
                    if (list != null) info = list.Find(a => a != null && a.name == req.save);
                }
                catch (Exception e) { Log("archive list not ready: " + e.Message); }
            }
            if (info == null) { Finish("save " + req.save + " not found in the test folder"); yield break; }
            yield return Shot("menu");
            Log("loading " + info.name);
            float tl = Time.realtimeSinceStartup;
            UserBean.getInstance().loadArchive(info);
            while (MarketScanner.Game == null && Time.realtimeSinceStartup - tl < 300) yield return new WaitForSecondsRealtime(1);
            if (MarketScanner.Game == null) { yield return Shot("load-timeout"); Finish("the save did not load in 300 s"); yield break; }
            result.perf["loadSeconds"] = Math.Round(Time.realtimeSinceStartup - tl, 1);

            // First scan (started by the mod on its own after the save loads).
            float ts = Time.realtimeSinceStartup;
            while ((host.Latest == null || host.Scanning) && Time.realtimeSinceStartup - ts < 400) yield return new WaitForSecondsRealtime(1);
            if (host.Latest == null) { yield return Shot("scan-timeout"); Finish("no scan finished in 400 s"); yield break; }
            result.perf["firstScanWaitSeconds"] = Math.Round(Time.realtimeSinceStartup - ts, 1);
            result.perf["firstScan"] = Perf.LastScan;

            // Prices are compared on up-to-date markets. If the save has few, bring the closest ones up to date with the
            // mod's own background catch-up (the same work as opening their exchanges).
            int exact = host.Latest.markets.FindAll(m => m.Exact && !m.planet).Count;
            result.perf["exactMarketsAtLoad"] = exact;
            if (exact < 10)
            {
                var stale = host.Latest.markets.FindAll(m => m.initialized && !m.Exact && !m.planet && m.Discovered);
                stale.Sort((a, b) => a.pendingDays.CompareTo(b.pendingDays));
                int n = Math.Min(15, stale.Count), days = 0;
                for (int i = 0; i < n; i++) { host.settlement.Enqueue(stale[i].uid); days += stale[i].pendingDays; }
                float tc = Time.realtimeSinceStartup;
                while (host.settlement.Pending > 0 && Time.realtimeSinceStartup - tc < 400) yield return new WaitForSecondsRealtime(1);
                result.perf["catchUp"] = n + " markets, " + days + " days, " + Math.Round(Time.realtimeSinceStartup - tc, 1) + " s, left " + host.settlement.Pending;
                yield return Refresh();
                result.perf["exactMarketsAfterCatchUp"] = host.Latest.markets.FindAll(m => m.Exact && !m.planet).Count;
            }

            var snap = host.Latest;
            Run(() => CheckTax(snap));
            Run(() => CheckNormal(snap));
            Run(() => CheckSpecialBuy(snap));
            Run(() => CheckSpecialResale(snap));
            Run(() => CheckSpecialSellModel(snap));
            Run(() => CheckAdvice(snap));
            Run(() => ProbeCalls(snap));

            if (req.tests == "all" || req.tests.Contains("trade")) yield return Guard(TradeTest(), "real trade: buy, plan stages, sell");

            // A full scan from scratch, then a refresh with nothing changed.
            yield return TimedScan("fullScan", true);
            yield return TimedScan("unchangedRefresh", false);
            // A new session's first scan, with the cache file the scans above wrote.
            MarketScanner.SaveDiskCache();
            MarketScanner.ClearCache(true);
            yield return TimedScan("diskCacheScan", false);
            result.perf["diskMarketsLoaded"] = MarketScanner.DiskMarketsLoaded;
            Run(() =>
            {
                int mk, cmp, diff;
                MarketScanner.CompareCacheWithLive(host.Latest, out mk, out cmp, out diff);
                return new Check { name = "cache from disk = game", passed = cmp > 0 && diff == 0 && MarketScanner.DiskMarketsLoaded > 0,
                                   summary = MarketScanner.DiskMarketsLoaded + " markets read from disk; " + mk + " markets, " + cmp + " goods compared live, " + diff + " differ" };
            });

            host.SetVisible(true);
            yield return new WaitForSecondsRealtime(4);
            yield return Shot("panel");
            host.ShowTab(1);
            yield return new WaitForSecondsRealtime(2);
            yield return Shot("plans");
            Finish(null);
        }

        void Run(Func<Check> f)
        {
            try { result.checks.Add(f()); }
            catch (Exception e) { result.checks.Add(new Check { name = f.Method.Name, passed = false, summary = "exception: " + e }); }
        }

        IEnumerator TimedScan(string name, bool fromScratch)
        {
            if (fromScratch) MarketScanner.ClearCache(false);
            int gc0 = GC.CollectionCount(0);
            float t = Time.realtimeSinceStartup;
            host.RequestRefresh(true);
            yield return null;
            while (host.Scanning && Time.realtimeSinceStartup - t < 400) yield return null;
            result.perf[name + "Seconds"] = Math.Round(Time.realtimeSinceStartup - t, 2);
            result.perf[name + "Collections"] = GC.CollectionCount(0) - gc0;
            result.perf[name] = Perf.LastScan;
        }

        // Runs a test coroutine; an exception ends that test as failed instead of stopping the whole self-test.
        IEnumerator Guard(IEnumerator e, string name)
        {
            while (true)
            {
                object cur;
                try
                {
                    if (!e.MoveNext()) yield break;
                    cur = e.Current;
                }
                catch (Exception ex)
                {
                    var c = result.checks.Find(x => x.name == name);
                    if (c == null) { c = new Check { name = name }; result.checks.Add(c); }
                    c.passed = false;
                    c.summary = "exception: " + ex.GetType().Name + ": " + ex.Message;
                    c.details.Add(ex.StackTrace ?? "");
                    yield break;
                }
                yield return cur;
            }
        }

        IEnumerator Refresh()
        {
            float t = Time.realtimeSinceStartup;
            host.RequestRefresh(true);
            yield return null;
            while (host.Scanning && Time.realtimeSinceStartup - t < 120) yield return null;
        }

        static long Uid(FWBaseBean b) => b is FWUidItem u ? u.uid : b is FWThingBean t ? t.uid : -1L;

        static bool SameMoney(double paid, double estimate) => Math.Abs(paid - estimate) <= Math.Max(2.0, Math.Abs(paid) * 1e-4);

        // A real purchase and sale of special goods in the test save, done the way the trade page's checkout does it for
        // non-equipment special goods (PageTradeInventory.DoCheckout + TakeSpecialThingGoods): price from
        // getPriceForPlayerBuy/Sell, the checkout tax, RoundPayment, items moved between the store and the fleet hold.
        // Checks the mod's cost and revenue against what was charged and paid, and that the plan moves through its
        // stages on its own and names the market it was sold at.
        IEnumerator TradeTest()
        {
            var c = new Check { name = "real trade: buy, plan stages, sell" };
            result.checks.Add(c);
            var snap = host.Latest;
            var gd = MarketScanner.Game;
            GoodsQuote qb = null, qs = null;
            const int k = 3;
            foreach (var m in snap.markets)
            {
                if (qb != null) break;
                if (!m.Exact || m.planet) continue;
                foreach (var q in m.goods)
                {
                    if (!q.special || q.specialGoods == null || q.specialGoods.UsesEquipmentPressure || q.storeDur == null || !q.buyable || q.contraband || q.stock < k) continue;
                    // Items that go into the fleet hold (not DNA, creatures or ships).
                    bool holdItems = q.specialGoods.goodsList != null && q.specialGoods.goodsList.TrueForAll(g => g != null && g.goods is FWThingBean);
                    if (!holdItems) continue;
                    foreach (var m2 in snap.markets)
                    {
                        GoodsQuote q2;
                        if (m2 == m || !m2.Exact || m2.planet || !m2.byGid.TryGetValue(q.gid, out q2) || q2.contraband || q2.specialGoods == null) continue;
                        qb = q; qs = q2; break;
                    }
                    if (qb != null) break;
                }
            }
            if (qb == null) { c.passed = false; c.summary = "no special goods with two up-to-date markets"; yield break; }
            var fleet = AirShipLocalData.Instance.getCurFleet();
            if (fleet == null) { c.passed = false; c.summary = "no current fleet"; yield break; }
            var ctx = new FleetInventoryContext(fleet, false);

            var idea = new TradeIdea { gid = qb.gid, name = qb.name, category = qb.category, buy = qb, sell = qs, qty = k,
                                       cost = qb.BuyCost(k), revenue = qs.SellBoughtRevenue(0, qb, 0, k) };
            var plan = Plans.FromTrade(idea, snap, host.Cfg, host.settlement);
            long cp0 = gd.creditPoint;

            // Buy.
            var sgB = qb.specialGoods;
            var store = new List<FWBaseBean>();
            foreach (var g in sgB.goodsList) if (g?.goods != null) store.Add(g.goods);
            store.Sort((a, b) => { int n = sgB.getPDurability(b).CompareTo(sgB.getPDurability(a)); return n != 0 ? n : Uid(b).CompareTo(Uid(a)); });
            var take = store.GetRange(0, k);
            float tB = FWGlobalParameter.ApplyTradeTaxRate(qb.market.ex.getTaxRate());
            long payB = FWTradePricePressure.RoundPayment(sgB.getPriceForPlayerBuy(take) * Math.Max(0.0, 1.0 + tB), true);
            gd.addCP(-payB, "TradeHelper selftest buy");
            var bought = new List<FWThingBean>();
            foreach (var it in take)
            {
                if (it is FWThingBean tb) bought.Add(tb.copyBean());
                for (int i = sgB.goodsList.Count - 1; i >= 0; i--) if (sgB.goodsList[i] != null && sgB.goodsList[i].goods == it) { sgB.goodsList.RemoveAt(i); break; }
            }
            if (bought.Count != k) { c.passed = false; c.summary = "bought " + bought.Count + " hold items instead of " + k; yield break; }
            int probeType = bought[0].type; string probeId = bought[0].id;
            ctx.AddBoughtItems(bought, qb.market.ex);   // the hold takes the items over (the list may be emptied)
            bool buyOk = SameMoney(payB, idea.cost);
            c.details.Add("buy " + k + " " + qb.name + " at " + qb.market.name + ": charged " + payB + ", mod cost " + idea.cost.ToString("0") + (buyOk ? " ok" : " DIFFERENT"));

            yield return Refresh();
            bool stage2 = plan.state == 1 && plan.boughtQty == k && plan.autoBought;
            c.details.Add("after buying: " + plan.StateText + (stage2 ? " ok" : " WRONG (expected stage 2 with " + k + ")"));

            // Sell the bought items at the plan's sell market.
            double estimate = plan.curRevenue;
            var sm = host.Latest.markets.Find(m => m.uid == qs.market.uid);
            GoodsQuote qs2 = null;
            if (sm != null) sm.byGid.TryGetValue(qb.gid, out qs2);
            var sgS = qs2 != null ? qs2.specialGoods : qs.specialGoods;
            // The hold merges bought items into existing stacks: take k units of that goods, best durability first.
            var things = ctx.GetTradeThings();
            var stacks = things != null ? things.FindAll(t => t != null && t.type == probeType && t.id == probeId && t.getSum() > 0) : new List<FWThingBean>();
            stacks.Sort((a, b) => sgS.getPDurability(b).CompareTo(sgS.getPDurability(a)));
            var sold = new List<FWBaseBean>();
            var removed = new List<FWThingBean>();
            foreach (var t in stacks)
            {
                if (sold.Count >= k) break;
                int n = Math.Min(t.getSum(), k - sold.Count);
                for (int i = 0; i < n; i++) sold.Add(t);
                var r = t.copyBean(); r.setSum(n); removed.Add(r);
            }
            float tS = FWGlobalParameter.ApplyTradeTaxRate(sm != null ? sm.ex.getTaxRate() : qs.market.ex.getTaxRate());
            if (sold.Count != k) { c.passed = false; c.summary = "only " + sold.Count + " of the goods in the fleet hold"; yield break; }
            long payS = FWTradePricePressure.RoundPayment(sgS.getPriceForPlayerSell(sold) * Math.Max(0.0, 1.0 - tS), false);
            gd.addCP(payS, "TradeHelper selftest sell");
            foreach (var r in removed) for (int i = 0; i < r.getSum(); i++) { var one = r.copyBean(); one.setSum(1); sgS.addGoods(one); }
            ctx.RemoveSoldItems(removed);
            bool sellOk = SameMoney(payS, estimate);
            c.details.Add("sell " + k + " at " + qs.market.name + ": paid " + payS + ", plan said " + estimate.ToString("0") + (sellOk ? " ok" : " DIFFERENT"));

            yield return Refresh();
            bool stage3 = plan.state == 2 && plan.autoDone && plan.doneNote != null && plan.doneNote.Contains(qs.market.name);
            c.details.Add("after selling: " + plan.StateText + " / " + plan.doneNote + (stage3 ? " ok" : " WRONG"));
            bool creditsOk = gd.creditPoint == cp0 - payB + payS;
            c.details.Add("credits " + cp0 + " -> " + gd.creditPoint + (creditsOk ? " ok" : " WRONG"));
            var route = Plans.Routes().Find(r => r.name == qb.name && r.to == plan.sellName && !r.held);
            bool routeOk = route != null && route.count == 1 && route.units == k && SameMoney(route.profit, payS - idea.cost);
            c.details.Add("route summary: " + (route == null ? "missing" : route.count + " time(s), " + route.units + " units, profit " + route.profit.ToString("0") + " (expected " + (payS - idea.cost).ToString("0") + ")") + (routeOk ? " ok" : " WRONG"));
            c.passed = buyOk && stage2 && sellOk && stage3 && creditsOk && routeOk;
            c.summary = (c.passed ? "all steps match" : "some steps differ") + " (" + qb.name + " " + qb.market.name + " -> " + qs.market.name + ")";
        }

        static double Rel(double a, double b) => Math.Abs(a - b) / Math.Max(1.0, Math.Max(Math.Abs(a), Math.Abs(b)));

        // The checkout's tax: PageTradeInventory.GetCurrentTaxRate().
        Check CheckTax(Snapshot snap)
        {
            var c = new Check { name = "tax = checkout rate" };
            int n = 0, bad = 0;
            foreach (var m in snap.markets)
            {
                if (m.ex == null) continue;
                float want = FWGlobalParameter.ApplyTradeTaxRate(m.ex.getTaxRate());
                n++;
                bool ok = Math.Abs(want - m.tax) < 1e-6f;
                foreach (var q in m.goods) if (Math.Abs(q.tax - m.tax) > 1e-6f) ok = false;
                if (!ok) { bad++; if (c.details.Count < 10) c.details.Add(m.name + ": mod " + m.tax + " game " + want); }
            }
            c.passed = n > 0 && bad == 0;
            c.summary = n + " markets, " + bad + " differ";
            return c;
        }

        // Normal goods in up-to-date markets: the mod's totals against the trade page's own preview quote.
        Check CheckNormal(Snapshot snap)
        {
            var c = new Check { name = "normal goods = game quote" };
            int n = 0, bad = 0; double worst = 0;
            int[] ks = { 1, 10, 100 };
            foreach (var m in snap.markets)
            {
                if (!m.Exact) continue;
                foreach (var q in m.goods)
                {
                    if (q.normal == null || q.special) continue;
                    foreach (int k in ks)
                    {
                        if (q.buyable && q.stock >= k)
                        {
                            double game = new FWTradePressurePreview().Quote(q.normal, -k).rawPrice * Math.Max(0.0, 1.0 + q.tax);
                            double mod = q.BuyCost(k); n++;
                            double r = Rel(mod, game); if (r > worst) worst = r;
                            if (r > 1e-4) { bad++; if (c.details.Count < 15) c.details.Add(m.name + " / " + q.name + " buy " + k + ": mod " + mod.ToString("0") + " game " + game.ToString("0")); }
                        }
                        double gs = new FWTradePressurePreview().Quote(q.normal, k).rawPrice * Math.Max(0.0, 1.0 - q.tax);
                        double ms = q.SellRevenue(k); n++;
                        double rs = Rel(ms, gs); if (rs > worst) worst = rs;
                        if (rs > 1e-4) { bad++; if (c.details.Count < 15) c.details.Add(m.name + " / " + q.name + " sell " + k + ": mod " + ms.ToString("0") + " game " + gs.ToString("0")); }
                    }
                }
            }
            c.passed = n > 0 && bad == 0;
            c.summary = n + " quotes, " + bad + " off by more than 0.01%, worst " + (worst * 100).ToString("0.####") + "%";
            return c;
        }

        static List<FWBaseBean> StoreItems(FWExchangeSpaceSpecialGoods sg)
        {
            var l = new List<FWBaseBean>();
            if (sg.goodsList != null) foreach (var g in sg.goodsList) if (g?.goods != null) l.Add(g.goods);
            l.Sort((a, b) => sg.getPDurability(b).CompareTo(sg.getPDurability(a)));
            return l;
        }

        // Special goods bought from a market: the mod's model against getPriceForPlayerBuy on the best items in store.
        Check CheckSpecialBuy(Snapshot snap)
        {
            var c = new Check { name = "special goods buy = game" };
            int n = 0, bad = 0; double worst = 0;
            foreach (var m in snap.markets)
            {
                if (!m.Exact) continue;
                foreach (var q in m.goods)
                {
                    var sg = q.specialGoods;
                    if (!q.special || sg == null || sg.UsesEquipmentPressure || !q.buyable) continue;
                    var items = StoreItems(sg);
                    for (int k = 1; k <= Math.Min(5, items.Count); k++)
                    {
                        double game = sg.getPriceForPlayerBuy(items.GetRange(0, k)) * Math.Max(0.0, 1.0 + q.tax);
                        double mod = q.BuyCost(k); n++;
                        double r = Rel(mod, game); if (r > worst) worst = r;
                        if (r > 1e-3) { bad++; if (c.details.Count < 15) c.details.Add(m.name + " / " + q.name + " x" + k + ": mod " + mod.ToString("0") + " game " + game.ToString("0") + " (" + ((mod / Math.Max(1, game) - 1) * 100).ToString("+0.0;-0.0") + "%)"); }
                    }
                }
            }
            c.passed = n > 0 && bad == 0;
            c.summary = n + " quotes, " + bad + " off by more than 0.1%, worst " + (worst * 100).ToString("0.###") + "%";
            return c;
        }

        // Special goods bought in one market and sold in another: the mod's revenue (durability of the bought items carried
        // over) against getPriceForPlayerSell on those very items.
        Check CheckSpecialResale(Snapshot snap)
        {
            var c = new Check { name = "special goods resale = game" };
            int n = 0, bad = 0; double worst = 0;
            var byGid = new Dictionary<string, List<GoodsQuote>>();
            foreach (var m in snap.markets)
            {
                if (!m.Exact) continue;
                foreach (var q in m.goods)
                {
                    if (!q.special || q.specialGoods == null || q.specialGoods.UsesEquipmentPressure) continue;
                    List<GoodsQuote> l;
                    if (!byGid.TryGetValue(q.gid, out l)) byGid[q.gid] = l = new List<GoodsQuote>();
                    l.Add(q);
                }
            }
            foreach (var l in byGid.Values)
            {
                foreach (var b in l)
                {
                    if (b.storeDur == null || !b.buyable) continue;
                    var items = StoreItems(b.specialGoods);
                    int k = Math.Min(5, items.Count);
                    int used = 0;
                    foreach (var s in l)
                    {
                        if (s == b || used >= 3) continue;
                        used++;
                        double game = s.specialGoods.getPriceForPlayerSell(items.GetRange(0, k)) * Math.Max(0.0, 1.0 - s.tax);
                        double mod = s.SellBoughtRevenue(0, b, 0, k); n++;
                        double r = Rel(mod, game); if (r > worst) worst = r;
                        if (r > 1e-3) { bad++; if (c.details.Count < 15) c.details.Add(b.market.name + " -> " + s.market.name + " / " + s.name + " x" + k + ": mod " + mod.ToString("0") + " game " + game.ToString("0")); }
                    }
                }
            }
            c.passed = bad == 0;
            c.summary = n + " resales, " + bad + " off by more than 0.1%, worst " + (worst * 100).ToString("0.###") + "%";
            return c;
        }

        // The model for selling special goods (used when the game cannot be asked) against the game, on the player's items.
        Check CheckSpecialSellModel(Snapshot snap)
        {
            var c = new Check { name = "special goods sell of held items (model vs game)", info = true };
            int n = 0, bad = 0; double worst = 0;
            foreach (var m in snap.markets)
            {
                if (!m.Exact) continue;
                foreach (var q in m.goods)
                {
                    if (!q.special || q.specialGoods == null || q.specialGoods.UsesEquipmentPressure) continue;
                    string key = q.type + "_" + q.itemId;
                    var held = snap.Held(key);
                    if (held == null || held.Count == 0) continue;
                    int k = Math.Min(held.Count, 30);
                    double game = q.SellHeld(0, k, held);
                    double mod = q.SellRevenue(k); n++;
                    double r = Rel(mod, game); if (r > worst) worst = r;
                    if (r > 0.005) { bad++; if (c.details.Count < 15) c.details.Add(m.name + " / " + q.name + " x" + k + ": model " + mod.ToString("0") + " game " + game.ToString("0")); }
                }
            }
            c.passed = bad == 0;
            c.summary = n + " quotes, " + bad + " off by more than 0.5%, worst " + (worst * 100).ToString("0.##") + "%";
            return c;
        }

        // Cost of each game call the scan makes per goods (time and memory per call), and whether summing a market's
        // sources once (RegionOf/EPoolOf/LeanOf) gives exactly the game's getEPool/getLStellar.
        Check ProbeCalls(Snapshot snap)
        {
            var c = new Check { name = "per-goods call cost and region sums = game" };
            var gd = MarketScanner.Game;
            var probes = new Dictionary<string, double[]>(); // name -> {ticks, bytes, calls, skipped}
            Action<string, Action> time = (name, a) =>
            {
                double[] v; if (!probes.TryGetValue(name, out v)) probes[name] = v = new double[4];
                int g0 = GC.CollectionCount(0); long m0 = GC.GetTotalMemory(false); long t0 = Perf.Now;
                a();
                long t1 = Perf.Now; long m1 = GC.GetTotalMemory(false);
                v[0] += t1 - t0; v[2]++;
                if (GC.CollectionCount(0) == g0) v[1] += Math.Max(0, m1 - m0); else v[3]++;
            };
            int n = 0, badPool = 0, badLean = 0, markets = 0;
            int snapReadable = 0, snapRaw = 0, snapMissing = 0, snapPoolSame = 0, snapPoolDiff = 0, snapLeanSame = 0, snapLeanDiff = 0;
            foreach (var m in snap.markets)
            {
                if (!m.Exact || m.ex == null) continue;
                markets++;
                MarketScanner.Region r = null;
                time("RegionOf (once per market)", () => r = MarketScanner.RegionOf(m.ex, gd));
                foreach (var it in new List<FWExchangeSpaceGoods>(m.ex.itemList))
                {
                    if (it == null || it.GoodID == null) continue;
                    n++;
                    float a = 0, b = 0, la = 0, lb = 0;
                    time("game getEPool", () => a = m.ex.getEPool(it.GoodID));
                    time("EPoolOf", () => b = MarketScanner.EPoolOf(r, it.GoodID));
                    time("game getLStellar", () => la = it.getLStellar());
                    time("LeanOf", () => lb = MarketScanner.LeanOf(r, it.GoodID));
                    time("getGoodsDef", () => it.getGoodsDef());
                    time("event price", () => FWGalaxyEventTradeEffects.Get(m.ex.uid, it.GoodID));
                    // The market's economics snapshot for its latest revision (what settlement computed).
                    FWExchangeEconomicDaySnapshot es = null;
                    if (m.ex.economicRevisions != null) m.ex.economicRevisions.TryGetValue(m.ex.latestEconomicRevision, out es);
                    if (es == null) snapMissing++;
                    else if (es.goods == null) snapRaw++;
                    else
                    {
                        snapReadable++;
                        float sp = float.NaN, sl = float.NaN;
                        time("snapshot Get", () => { try { sp = es.Get(it.GoodID, 0); sl = es.Get(it.GoodID, 3); } catch { } });
                        if (sp == a) snapPoolSame++; else { snapPoolDiff++; if (c.details.Count < 12) c.details.Add("snapshot EPool " + m.name + " " + it.GoodID + ": live " + a + " snapshot " + sp); }
                        if (sl == la) snapLeanSame++; else { snapLeanDiff++; if (c.details.Count < 12) c.details.Add("snapshot leaning " + m.name + " " + it.GoodID + ": live " + la + " snapshot " + sl); }
                    }
                    if (a != b) { badPool++; if (c.details.Count < 8) c.details.Add("EPool " + m.name + " " + it.GoodID + ": game " + a + " sum " + b); }
                    if (la != lb) { badLean++; if (c.details.Count < 8) c.details.Add("Leaning " + m.name + " " + it.GoodID + ": game " + la + " sum " + lb); }
                }
            }
            foreach (var kv in probes)
            {
                var v = kv.Value;
                c.details.Add(kv.Key + ": " + (Perf.Ms((long)v[0]) * 1000 / Math.Max(1, v[2])).ToString("0.0") + " us, " + (v[1] / Math.Max(1, v[2] - v[3])).ToString("0") + " B per call, total " + Perf.Ms((long)v[0]).ToString("0") + " ms over " + v[2] + " calls");
            }
            c.passed = n > 0 && badPool == 0 && badLean == 0;
            c.summary = markets + " markets, " + n + " goods; EPool differs " + badPool + ", leaning differs " + badLean
                        + " | snapshot readable " + snapReadable + " raw " + snapRaw + " missing " + snapMissing
                        + "; EPool same " + snapPoolSame + " diff " + snapPoolDiff + "; leaning same " + snapLeanSame + " diff " + snapLeanDiff;
            return c;
        }

        // What the advice may contain.
        Check CheckAdvice(Snapshot snap)
        {
            var c = new Check { name = "advice rules" };
            int bad = 0;
            foreach (var t in snap.trades)
            {
                string why = null;
                if (t.buy.market.planet || t.sell.market.planet) why = "ground market";
                else if (t.buy.contraband || t.sell.contraband) why = "contraband";
                else if (t.Profit <= 0) why = "no profit";
                else if (!t.buy.market.Discovered || !t.sell.market.Discovered) why = "undiscovered";
                if (why != null) { bad++; if (c.details.Count < 10) c.details.Add(why + ": " + t.name + " " + t.buy.market.name + " -> " + t.sell.market.name); }
            }
            for (int i = 0; i < Math.Min(5, snap.trades.Count); i++)
            {
                var t = snap.trades[i];
                c.details.Add("top " + (i + 1) + ": " + t.name + " x" + t.qty + " " + t.buy.market.name + " -> " + t.sell.market.name + " profit " + t.Profit.ToString("0"));
            }
            c.passed = bad == 0;
            c.summary = snap.trades.Count + " trade ideas, " + snap.sells.Count + " sell ideas, " + bad + " break the rules";
            return c;
        }

        IEnumerator Shot(string label)
        {
            yield return new WaitForEndOfFrame();
            Texture2D tex = null;
            try
            {
                if (Screen.width <= 0 || Screen.height <= 0) { result.screenshots.Add(label + ": no screen"); yield break; }
                var p = Path.Combine(GameDir, "selftest-" + label + ".png");
                tex = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0);
                tex.Apply(false);
                File.WriteAllBytes(p, ImageConversion.EncodeToPNG(tex));
                result.screenshots.Add(p);
            }
            catch (Exception e) { result.screenshots.Add(label + ": " + e.Message); }
            finally { if (tex != null) Destroy(tex); }
        }

        void Finish(string error)
        {
            result.error = error;
            bool ok = error == null;
            foreach (var c in result.checks) if (!c.info && !c.passed) ok = false;
            result.passed = ok;
            try
            {
                File.WriteAllText(Path.Combine(GameDir, "selftest-result.json"), JsonConvert.SerializeObject(result, Formatting.Indented));
            }
            catch (Exception e) { Debug.LogWarning("[TradeHelper] selftest result write failed: " + e); }
            Log("finished, passed=" + ok + (error != null ? " error=" + error : ""));
            try { host.SetVisible(false); } catch { }
            Application.Quit();
        }
    }
}
