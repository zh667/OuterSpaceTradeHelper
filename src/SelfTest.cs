using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

            if (req.tests == "stress")
            {
                yield return Guard(Stress(), "stress");
                Finish(null);
                yield break;
            }
            if (req.tests == "settle-many" || req.tests == "keepfresh")
            {
                yield return Guard(req.tests == "settle-many" ? ManyDaysCatchUp() : KeepAllFresh(), req.tests);
                Finish(null);
                yield break;
            }
            if (req.tests == "settle")
            {
                // Only the catch-up measurement: wait for the game's own day change so markets owe days.
                yield return Guard(CatchUpSmoothness(true), "catch-up smoothness");
                host.SetVisible(true);
                yield return new WaitForSecondsRealtime(3);
                host.ShowTab(5);
                yield return new WaitForSecondsRealtime(2);
                yield return Shot("settlement");
                Finish(null);
                yield break;
            }

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
            Run(() => CheckExploration(snap));
            Run(() => ProbeCalls(snap));
            Run(() => StationPlaces());

            if (req.tests == "all" || req.tests.Contains("trade")) yield return Guard(TradeTest(), "real trade: buy, plan stages, sell");
            if (req.tests == "all" || req.tests.Contains("ledger")) yield return Guard(LedgerTest(), "checkout ledger: the game's own checkout");
            yield return Guard(CatchUpSmoothness(false), "catch-up smoothness");

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
            host.ShowTab(3);
            yield return new WaitForSecondsRealtime(2);
            yield return Shot("markets");
            Finish(null);
        }

        void Run(Func<Check> f)
        {
            var gd = MarketScanner.Game;
            GameLock.Enter(gd);
            try { result.checks.Add(f()); }
            catch (Exception e) { result.checks.Add(new Check { name = f.Method.Name, passed = false, summary = "exception: " + e }); }
            finally { GameLock.Exit(gd); }
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

        // Background catch-up of markets that owe days: frame times while the mod's settlement queue works.
        IEnumerator CatchUpSmoothness(bool waitForDay)
        {
            var c = new Check { name = "catch-up smoothness", info = true, passed = true };
            result.checks.Add(c);
            Predicate<MarketInfo> owes = m => m.initialized && !m.Exact && !m.planet && m.Discovered;
            float tw = Time.realtimeSinceStartup;
            // A loaded save starts paused: run the game clock fast until a day change, then back to normal speed.
            if (waitForDay && host.Latest.markets.FindAll(owes).Count < 10) MapManager.setLogicSpeed(20f);
            while (waitForDay && host.Latest.markets.FindAll(owes).Count < 10 && Time.realtimeSinceStartup - tw < 1200)
            {
                yield return new WaitForSecondsRealtime(10);
                yield return Refresh();
            }
            if (waitForDay)
            {
                MapManager.setLogicSpeed(1f);
                yield return new WaitForSecondsRealtime(5);
                yield return Refresh();
                c.details.Add("waited " + (Time.realtimeSinceStartup - tw).ToString("0") + " s for markets to owe days (clock x20), measured at x1");
            }
            var stale = host.Latest.markets.FindAll(owes);
            stale.Sort((a, b) => b.pendingDays.CompareTo(a.pendingDays));
            int n = Math.Min(20, stale.Count), days = 0;
            if (n == 0) { c.summary = "no market owes days"; yield break; }
            for (int i = 0; i < n; i++) { host.settlement.Enqueue(stale[i].uid); days += stale[i].pendingDays; }
            yield return null;
            int frames = 0, over50 = 0, over100 = 0, gcs = GC.CollectionCount(0); double sum = 0, worst = 0;
            float t0 = Time.realtimeSinceStartup;
            while (host.settlement.Pending > 0 && Time.realtimeSinceStartup - t0 < 600)
            {
                yield return null;
                double dt = Time.unscaledDeltaTime * 1000.0;
                frames++; sum += dt; if (dt > worst) worst = dt;
                if (dt > 50) over50++;
                if (dt > 100) over100++;
            }
            float secs = Time.realtimeSinceStartup - t0;
            c.summary = n + " markets, " + days + " days in " + secs.ToString("0.0") + " s; " + frames + " frames, average " + (sum / Math.Max(1, frames)).ToString("0.0") + " ms, worst " + worst.ToString("0") + " ms, " + over50 + " frames over 50 ms, " + over100 + " over 100 ms, " + (GC.CollectionCount(0) - gcs) + " garbage collections; left " + host.settlement.Pending;
            result.perf["catchUpSmoothness"] = c.summary;
            yield return Refresh();
            int stillOwing = 0;
            foreach (var m0 in stale.GetRange(0, n)) { var m = host.Latest.markets.Find(x => x.uid == m0.uid); if (m != null && !m.Exact) stillOwing++; }
            c.details.Add(stillOwing + " of them still owe days after the catch-up");
        }

        // Frame statistics over a stretch of frames.
        class Frames
        {
            public int n, over50, over100, over250; public double sum, worst; int gc0 = GC.CollectionCount(0); float t0 = Time.realtimeSinceStartup;
            public long allocated; long lastMem = GC.GetTotalMemory(false); int lastGc = GC.CollectionCount(0);
            public void Add(double ms)
            {
                n++; sum += ms; if (ms > worst) worst = ms; if (ms > 50) over50++; if (ms > 100) over100++; if (ms > 250) over250++;
                long mem = GC.GetTotalMemory(false); int g = GC.CollectionCount(0);
                if (g == lastGc && mem > lastMem) allocated += mem - lastMem;
                lastMem = mem; lastGc = g;
            }
            public string Text() => (Time.realtimeSinceStartup - t0).ToString("0.0") + " s, " + n + " frames, average " + (sum / Math.Max(1, n)).ToString("0.0") + " ms, worst " + worst.ToString("0") + " ms, over 50 ms: " + over50 + ", over 100 ms: " + over100 + ", over 250 ms: " + over250 + ", garbage collections " + (GC.CollectionCount(0) - gc0);
        }

        // Scenario B: every market owes many days (the clock runs fast with nothing settled), then all are caught up.
        IEnumerator ManyDaysCatchUp()
        {
            var c = new Check { name = "settle-many", info = true, passed = true };
            result.checks.Add(c);
            MapManager.setLogicSpeed(100f);
            float tw = Time.realtimeSinceStartup;
            yield return new WaitForSecondsRealtime(240);
            MapManager.setLogicSpeed(1f);
            yield return new WaitForSecondsRealtime(5);
            yield return Refresh();
            var owe = host.Latest.markets.FindAll(m => m.initialized && !m.planet && m.pendingDays > 0);
            int days = 0, most = 0; foreach (var m in owe) { days += m.pendingDays; if (m.pendingDays > most) most = m.pendingDays; }
            c.details.Add("clock x100 for 240 s: " + owe.Count + " markets owe " + days + " days (most " + most + ")");
            var idle = new Frames();
            for (float t = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t < 20;) { yield return null; idle.Add(Time.unscaledDeltaTime * 1000.0); }
            c.details.Add("idle at x1 before: " + idle.Text());
            foreach (var m in owe) host.settlement.Enqueue(m.uid);
            long mod0 = Perf.ModAllocatedTotal;
            var f = new Frames();
            float tc = Time.realtimeSinceStartup;
            while (host.settlement.Pending > 0 && Time.realtimeSinceStartup - tc < 1500) { yield return null; f.Add(Time.unscaledDeltaTime * 1000.0); }
            c.details.Add("catching up all: " + f.Text() + "; left " + host.settlement.Pending);
            long modAlloc = Perf.ModAllocatedTotal - mod0;
            c.details.Add("garbage collector: incremental=" + UnityEngine.Scripting.GarbageCollector.isIncremental + ", mode=" + UnityEngine.Scripting.GarbageCollector.GCMode
                          + ", heap " + (GC.GetTotalMemory(false) / 1048576) + " MB");
            c.details.Add("allocated during the catch-up: " + (f.allocated / 1048576.0).ToString("0") + " MB in total (" + (f.allocated / 1048576.0 / Math.Max(1, days)).ToString("0.0") + " MB per market-day), of which the mod's own main-thread code " + (modAlloc / 1048576.0).ToString("0") + " MB");
            c.summary = owe.Count + " markets, " + days + " days: " + f.Text();
            result.perf["settleMany"] = c.summary;
        }

        // Scenario C: every market kept up to date at normal speed; the mod catches each one up after every day change.
        IEnumerator KeepAllFresh()
        {
            var c = new Check { name = "keepfresh", info = true, passed = true };
            result.checks.Add(c);
            var cfg = host.Cfg;
            foreach (var m in host.Latest.markets) if (!m.planet && m.initialized && !cfg.keepFresh.Contains(m.uid)) cfg.keepFresh.Add(m.uid);
            cfg.Save();
            c.details.Add(cfg.keepFresh.Count + " markets kept up to date");
            // Bring them all up to date first.
            foreach (var uid in cfg.keepFresh) host.settlement.Enqueue(uid);
            float tc = Time.realtimeSinceStartup;
            while (host.settlement.Pending > 0 && Time.realtimeSinceStartup - tc < 900) yield return null;
            c.details.Add("first catch-up done in " + (Time.realtimeSinceStartup - tc).ToString("0") + " s");
            // Then play at x3 (the game's fastest button) for 8 minutes and watch the frames.
            MapManager.setLogicSpeed(3f);
            var all = new Frames(); var busy = new Frames();
            int startDays = host.settlement.daysDoneTotal;
            for (float t = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t < 480;)
            {
                yield return null;
                double ms = Time.unscaledDeltaTime * 1000.0;
                all.Add(ms);
                if (host.settlement.Pending > 0 || host.settlement.currentUid >= 0) busy.Add(ms);
            }
            MapManager.setLogicSpeed(1f);
            c.details.Add("8 min at x3: " + all.Text());
            c.details.Add("frames while catching up: " + busy.Text());
            c.details.Add("days settled meanwhile: " + (host.settlement.daysDoneTotal - startDays));
            c.summary = "x3 for 8 min, " + cfg.keepFresh.Count + " markets kept fresh: " + all.Text();
            result.perf["keepFresh"] = c.summary;
        }

        // Scenario for the freeze seen in play: every market kept up to date at x3 while the mod scans every second, so its
        // main-thread reads overlap the game's worker-thread catch-ups as often as possible. A heartbeat file is written
        // every few seconds; tools/run_isolated.py reports a freeze when it stops.
        IEnumerator Stress()
        {
            var c = new Check { name = "stress", info = true, passed = true };
            result.checks.Add(c);
            var cfg = host.Cfg;
            foreach (var m in host.Latest.markets) if (!m.planet && m.initialized && !cfg.keepFresh.Contains(m.uid)) cfg.keepFresh.Add(m.uid);
            cfg.Save();
            MapManager.setLogicSpeed(3f);
            var f = new Frames();
            int scans = 0; float nextBeat = 0, nextScan = 0;
            string beat = Path.Combine(GameDir, "selftest-heartbeat.txt");
            for (float t = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t < 720;)
            {
                yield return null;
                f.Add(Time.unscaledDeltaTime * 1000.0);
                if (Time.realtimeSinceStartup >= nextScan && !host.Scanning) { host.RequestRefresh(true); scans++; nextScan = Time.realtimeSinceStartup + 1f; }
                if (Time.realtimeSinceStartup >= nextBeat)
                {
                    nextBeat = Time.realtimeSinceStartup + 5f;
                    try { File.WriteAllText(beat, DateTime.Now.ToString("HH:mm:ss") + " scans=" + scans + " settled=" + host.settlement.daysDoneTotal + " pending=" + host.settlement.Pending); } catch { }
                }
            }
            MapManager.setLogicSpeed(1f);
            c.summary = "12 min at x3, " + cfg.keepFresh.Count + " markets kept fresh, " + scans + " scans, " + host.settlement.daysDoneTotal + " days settled: " + f.Text();
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
        // Runs game calls of a test step under the game's locks, in the game's order (blocking is fine in tests; holding
        // the locks across frames is not, so each step is synchronous).
        static void Locked(Action a)
        {
            var gd = MarketScanner.Game;
            GameLock.Enter(gd);
            try { a(); } finally { GameLock.Exit(gd); }
        }

        IEnumerator TradeTest()
        {
            var c = new Check { name = "real trade: buy, plan stages, sell" };
            result.checks.Add(c);
            var snap = host.Latest;
            var gd = MarketScanner.Game;
            GoodsQuote qb = null, qs = null;
            const int k = 3;
            string abort = null;
            FleetInventoryContext ctx = null;
            TradeIdea idea = null; Plan plan = null;
            long cp0 = 0, payB = 0, payS = 0; int probeType = 0; string probeId = null; bool buyOk = false;

            Locked(() =>
            {
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
                if (qb == null) { abort = "no special goods with two up-to-date markets"; return; }
                var fleet = AirShipLocalData.Instance.getCurFleet();
                if (fleet == null) { abort = "no current fleet"; return; }
                ctx = new FleetInventoryContext(fleet, false);
                idea = new TradeIdea { gid = qb.gid, name = qb.name, category = qb.category, buy = qb, sell = qs, qty = k,
                                       cost = qb.BuyCost(k), revenue = qs.SellBoughtRevenue(0, qb, 0, k) };
                plan = Plans.FromTrade(idea, snap, host.Cfg, host.settlement);
                cp0 = gd.creditPoint;

                // Buy.
                var sgB = qb.specialGoods;
                var store = new List<FWBaseBean>();
                foreach (var g in sgB.goodsList) if (g?.goods != null) store.Add(g.goods);
                store.Sort((a, b) => { int n = sgB.getPDurability(b).CompareTo(sgB.getPDurability(a)); return n != 0 ? n : Uid(b).CompareTo(Uid(a)); });
                var take = store.GetRange(0, k);
                float tB = FWGlobalParameter.ApplyTradeTaxRate(qb.market.ex.getTaxRate());
                payB = FWTradePricePressure.RoundPayment(sgB.getPriceForPlayerBuy(take) * Math.Max(0.0, 1.0 + tB), true);
                gd.addCP(-payB, "TradeHelper selftest buy");
                var bought = new List<FWThingBean>();
                foreach (var it in take)
                {
                    if (it is FWThingBean tb) bought.Add(tb.copyBean());
                    for (int i = sgB.goodsList.Count - 1; i >= 0; i--) if (sgB.goodsList[i] != null && sgB.goodsList[i].goods == it) { sgB.goodsList.RemoveAt(i); break; }
                }
                if (bought.Count != k) { abort = "bought " + bought.Count + " hold items instead of " + k; return; }
                probeType = bought[0].type; probeId = bought[0].id;
                ctx.AddBoughtItems(bought, qb.market.ex);   // the hold takes the items over (the list may be emptied)
                buyOk = SameMoney(payB, idea.cost);
            });
            if (abort != null) { c.passed = false; c.summary = abort; yield break; }
            c.details.Add("buy " + k + " " + qb.name + " at " + qb.market.name + ": charged " + payB + ", mod cost " + idea.cost.ToString("0") + (buyOk ? " ok" : " DIFFERENT"));

            yield return Refresh();
            bool stage2 = plan.state == 1 && plan.boughtQty == k && plan.autoBought;
            c.details.Add("after buying: " + plan.StateText + (stage2 ? " ok" : " WRONG (expected stage 2 with " + k + ")"));
            // "现在最好卖到 / 比计划": selling at the plan's own market gives the plan's current profit, and the best market
            // is at least as good.
            var own = plan.alternatives.Find(a => a.Key != null && a.Key.uid == plan.sellUid);
            bool ownCounted = plan.sellMarket != null && (plan.sellMarket.Explored || host.Cfg.includeUndiscovered);
            bool bestOk = plan.alternatives.Count > 0 && !double.IsNaN(plan.BestProfit) && (!ownCounted || plan.BestProfit >= plan.CurProfit - 2)
                          && plan.alternatives.TrueForAll(a => a.Key.Explored || host.Cfg.includeUndiscovered)
                          && (own.Key == null || SameMoney(plan.ProfitIfSoldFor(own.Value), plan.CurProfit));
            c.details.Add("best market now: " + (plan.alternatives.Count > 0 ? plan.alternatives[0].Key.name : "none") + ", profit " + plan.BestProfit.ToString("0")
                          + " vs plan " + plan.PlanProfit.ToString("0") + " / at the plan's market " + plan.CurProfit.ToString("0")
                          + (own.Key != null ? " (own market listed: " + plan.ProfitIfSoldFor(own.Value).ToString("0") + ")" : " (own market not in the top 5)") + (bestOk ? " ok" : " WRONG"));
            host.SetVisible(true);
            host.ShowTab(1);
            yield return new WaitForSecondsRealtime(1);
            Ui.PlansView.Current?.Select(plan);
            yield return new WaitForSecondsRealtime(1);
            yield return Shot("plans-holding");
            string shown = Ui.PlansView.Current != null ? Ui.PlansView.Current.DetailText : "";
            int at = shown.IndexOf("现在卖 ");
            bool detailOk = at >= 0 && shown.IndexOf("比计划", at) > 0;
            c.details.Add("plan detail: " + (at >= 0 ? System.Text.RegularExpressions.Regex.Replace(shown.Substring(at), "<[^>]+>", "").Split('\n')[0] : "no best-market line") + (detailOk ? " ok" : " WRONG"));
            bestOk &= detailOk;
            host.SetVisible(false);

            // A second plan of the same goods, earlier in the queue, selling at the same market, marked bought by hand
            // although nothing more was bought (what a player did): it must be shown as not in the inventory, must not
            // push the real plan down the market's queue, and the sale below must count for the real plan.
            var decoy = JsonConvert.DeserializeObject<Plan>(JsonConvert.SerializeObject(plan));
            decoy.createdMM = plan.createdMM - 1;
            decoy.state = 0; decoy.boughtQty = 0; decoy.autoBought = false; decoy.history = new List<PlanPoint>(); decoy.addedKeepFresh = new List<long>();
            Plans.list.Insert(0, decoy);
            Plans.MarkBought(decoy, host.Cfg, host.settlement);
            yield return Refresh();
            bool decoyFlag = decoy.missing == k && plan.missing == 0 && decoy.StateText == "② 库存里没有" && decoy.note.Contains("手动")
                             && plan.sellAhead == 0 && double.IsNaN(decoy.CurProfit) && decoy.alternatives.Count == 0;
            c.details.Add("plan marked bought by hand without buying: " + decoy.StateText + " missing " + decoy.missing + ", real plan missing " + plan.missing
                          + ", real plan queued behind " + plan.sellAhead + ", decoy profit " + decoy.CurProfit + " alternatives " + decoy.alternatives.Count + (decoyFlag ? " ok" : " WRONG"));
            Plans.MarkUnbought(decoy, host.Cfg, host.settlement);
            bool unbought = decoy.state == 0 && decoy.boughtQty == 0;
            Plans.MarkBought(decoy, host.Cfg, host.settlement);   // keep it (claiming units) through the sale
            yield return Refresh();
            c.details.Add("改回待买入: " + (unbought ? "back to stage 1 ok" : "WRONG"));

            // Sell the bought items at the plan's sell market.
            double estimate = plan.curRevenue;
            bool sellOk = false;
            Locked(() =>
            {
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
                if (sold.Count != k) { abort = "only " + sold.Count + " of the goods in the fleet hold"; return; }
                payS = FWTradePricePressure.RoundPayment(sgS.getPriceForPlayerSell(sold) * Math.Max(0.0, 1.0 - tS), false);
                gd.addCP(payS, "TradeHelper selftest sell");
                foreach (var r in removed) for (int i = 0; i < r.getSum(); i++) { var one = r.copyBean(); one.setSum(1); sgS.addGoods(one); }
                ctx.RemoveSoldItems(removed);
                sellOk = SameMoney(payS, estimate);
            });
            if (abort != null) { c.passed = false; c.summary = abort; yield break; }
            c.details.Add("sell " + k + " at " + qs.market.name + ": paid " + payS + ", plan said " + estimate.ToString("0") + (sellOk ? " ok" : " DIFFERENT"));

            yield return Refresh();
            bool stage3 = plan.state == 2 && plan.autoDone && plan.doneNote != null && plan.doneNote.Contains(qs.market.name);
            c.details.Add("after selling: " + plan.StateText + " / " + plan.doneNote + (stage3 ? " ok" : " WRONG"));
            bool decoyKept = decoy.state == 1 && decoy.soldQty == 0;
            c.details.Add("sale counted for the plan selling there: real plan " + plan.StateText + ", other plan " + decoy.StateText + " sold " + decoy.soldQty + (decoyKept ? " ok" : " WRONG"));
            Plans.Remove(decoy, host.Cfg);
            bool creditsOk = gd.creditPoint == cp0 - payB + payS;
            c.details.Add("credits " + cp0 + " -> " + gd.creditPoint + (creditsOk ? " ok" : " WRONG"));
            var route = Plans.Routes().Find(r => r.name == qb.name && r.to == plan.sellName && !r.held);
            bool routeOk = route != null && route.count == 1 && route.units == k && SameMoney(route.profit, payS - idea.cost);
            c.details.Add("route summary: " + (route == null ? "missing" : route.count + " time(s), " + route.units + " units, profit " + route.profit.ToString("0") + " (expected " + (payS - idea.cost).ToString("0") + ")") + (routeOk ? " ok" : " WRONG"));
            c.passed = buyOk && stage2 && bestOk && decoyFlag && unbought && sellOk && stage3 && decoyKept && creditsOk && routeOk;
            c.summary = (c.passed ? "all steps match" : "some steps differ") + " (" + qb.name + " " + qb.market.name + " -> " + qs.market.name + ")";
        }

        // Opens the game's trade page for a market the way PageInGalaxySpace.openExchangePage does.
        static PageTradeInventory OpenTradePage(FWExchangeSpace ex, out ExchangeTransferHandler h, out ExchangeInventoryContext xc, out FleetInventoryContext fc)
        {
            fc = new FleetInventoryContext(AirShipLocalData.Instance.getCurFleet(), false);
            xc = new ExchangeInventoryContext(ex, "贸易市场");
            h = new ExchangeTransferHandler(xc, fc);
            var page = UIHelp.showPageTradeInventory();
            page.InitData(xc, fc, h, null);
            return page;
        }

        static int PutInCart(ExchangeTransferHandler h, List<StorageItemInstance> from, Predicate<StorageItemInstance> match, int n, bool buy)
        {
            int added = 0;
            foreach (var it in from)
            {
                if (added >= n || !match(it)) continue;
                int before = (buy ? h.BuyCart : h.SellCart).ConvertAll(x => x.Quantity).Sum();
                h.HandleItemTransfer(it, buy ? PanelRole.ShopSystem : h.CurrentPlayerRole, buy ? h.CurrentPlayerRole : PanelRole.ShopSystem, n - added);
                added += (buy ? h.BuyCart : h.SellCart).ConvertAll(x => x.Quantity).Sum() - before;
            }
            return added;
        }

        static readonly System.Reflection.MethodInfo DoCheckout = typeof(PageTradeInventory).GetMethod("DoCheckout", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        // Buys special goods (fleet wings) and a normal goods at one market and sells them at another through the game's
        // own trade page and checkout (PageTradeInventory.DoCheckout), with a plan for the special goods. The ledger must
        // match the credits exactly per checkout, the plan must take its cost and revenue from the ledger, and a credit
        // change slipped into the checkout frame must be reported as not matching.
        IEnumerator LedgerTest()
        {
            var c = new Check { name = "checkout ledger: the game's own checkout" };
            result.checks.Add(c);
            yield return Refresh();
            var snap = host.Latest;
            var gd = MarketScanner.Game;
            const int k = 3, kn = 2;
            GoodsQuote qb = null, qs = null, nb = null, ns = null;
            Locked(() =>
            {
                foreach (var m in snap.markets)
                {
                    if (qb != null) break;
                    if (!m.Exact || m.planet || !m.Explored) continue;
                    foreach (var q in m.goods)
                    {
                        if (!q.special || q.specialGoods == null || q.specialGoods.UsesEquipmentPressure || !q.buyable || q.contraband || q.stock < k) continue;
                        if (q.specialGoods.goodsList == null || !q.specialGoods.goodsList.TrueForAll(g => g != null && g.goods is FWThingBean)) continue;
                        foreach (var m2 in snap.markets)
                        {
                            GoodsQuote q2, n2 = null;
                            if (m2 == m || !m2.Exact || m2.planet || !m2.Explored || !m2.byGid.TryGetValue(q.gid, out q2) || q2.contraband || q2.specialGoods == null) continue;
                            var n1 = m.goods.Find(x => !x.special && x.normal != null && x.buyable && !x.contraband && x.stock >= kn && m2.byGid.TryGetValue(x.gid, out n2) && !n2.contraband);
                            if (n1 == null) continue;
                            qb = q; qs = q2; nb = n1; ns = m2.byGid[n1.gid]; break;
                        }
                        if (qb != null) break;
                    }
                }
            });
            if (qb == null) { c.passed = false; c.summary = "no special goods with two up-to-date markets that share a normal goods"; yield break; }
            if (DoCheckout == null) { c.passed = false; c.summary = "PageTradeInventory.DoCheckout not found"; yield break; }
            var idea = new TradeIdea { gid = qb.gid, name = qb.name, category = qb.category, buy = qb, sell = qs, qty = k, cost = qb.BuyCost(k), revenue = qs.SellBoughtRevenue(0, qb, 0, k) };
            Plan plan = null;
            Locked(() => plan = Plans.FromTrade(idea, snap, host.Cfg, host.settlement));
            yield return Refresh();
            bool ok = true;

            // 1) Buy k special + kn normal at A in one checkout.
            int n0 = Ledger.entries.Count; long cp0 = gd.creditPoint;
            PageTradeInventory page = null; ExchangeTransferHandler h = null; ExchangeInventoryContext xc; FleetInventoryContext fc;
            int addS = 0, addN = 0;
            Locked(() =>
            {
                page = OpenTradePage(qb.market.ex, out h, out xc, out fc);
                var shop = xc.GetItems();
                addS = PutInCart(h, shop, it => it.SourceGoods == qb.specialGoods, k, true);
                addN = PutInCart(h, shop, it => it.SourceGoods == nb.normal, kn, true);
            });
            yield return new WaitForSecondsRealtime(1.5f);   // the ledger finds the page and prices the cart
            Locked(() => DoCheckout.Invoke(page, null));
            yield return null; yield return null;
            var e1 = Ledger.entries.Count > n0 ? Ledger.entries[Ledger.entries.Count - 1] : null;
            long d1 = gd.creditPoint - cp0;
            bool buyOk = addS == k && addN == kn && e1 != null && e1.exact && e1.creditDelta == d1 && d1 < 0 && e1.market == qb.market.name
                         && Math.Abs(e1.items.ConvertAll(x => x.net).Sum() - d1) < 1.5
                         && e1.items.FindAll(x => x.buy && x.key == plan.key).Sum(x => x.qty) == k && e1.items.FindAll(x => x.buy && x.key != plan.key).Sum(x => x.qty) == kn;
            c.details.Add("buy at " + qb.market.name + ": cart " + addS + "+" + addN + ", credits " + d1 + ", ledger " + (e1 == null ? "none" : e1.creditDelta + " exact=" + e1.exact + " lines " + string.Join("; ", e1.items.ConvertAll(x => x.name + " x" + x.qty + " " + x.net.ToString("0.0")))) + (buyOk ? " ok" : " WRONG"));
            ok &= buyOk;
            Locked(() => page.RequestClose());
            yield return Refresh();
            double specialCost = e1 != null ? -e1.items.FindAll(x => x.key == plan.key).Sum(x => x.net) : double.NaN;
            bool costOk = plan.state == 1 && plan.CostKnown && Math.Abs(plan.paidCost - specialCost) < 0.01;
            c.details.Add("plan after buying: " + plan.StateText + ", cost from ledger " + plan.paidCost.ToString("0.0") + " (planned " + plan.planCost.ToString("0") + ")" + (costOk ? " ok" : " WRONG"));
            ok &= costOk;

            // 2) Sell them at B in one checkout.
            n0 = Ledger.entries.Count; cp0 = gd.creditPoint;
            Locked(() =>
            {
                page = OpenTradePage(qs.market.ex, out h, out xc, out fc);
                var hold = fc.GetItems();
                addS = PutInCart(h, hold, it => it.RawData is FWThingBean t && t.type + "_" + t.id == plan.key, k, false);
                var nkey = nb.normal.getItemType() + "_" + nb.normal.getItemId();
                addN = PutInCart(h, hold, it => it.RawData is FWThingBean t && t.type + "_" + t.id == nkey, kn, false);
            });
            yield return new WaitForSecondsRealtime(1.5f);
            Locked(() => DoCheckout.Invoke(page, null));
            yield return null; yield return null;
            var e2 = Ledger.entries.Count > n0 ? Ledger.entries[Ledger.entries.Count - 1] : null;
            long d2 = gd.creditPoint - cp0;
            bool sellOk = addS == k && addN == kn && e2 != null && e2.exact && e2.creditDelta == d2 && d2 > 0 && e2.market == qs.market.name
                          && Math.Abs(e2.items.ConvertAll(x => x.net).Sum() - d2) < 1.5 && e2.items.FindAll(x => !x.buy && x.key == plan.key).Sum(x => x.qty) == k;
            c.details.Add("sell at " + qs.market.name + ": cart " + addS + "+" + addN + ", credits " + d2 + ", ledger " + (e2 == null ? "none" : e2.creditDelta + " exact=" + e2.exact + " lines " + string.Join("; ", e2.items.ConvertAll(x => x.name + " x" + x.qty + " " + x.net.ToString("0.0")))) + (sellOk ? " ok" : " WRONG"));
            ok &= sellOk;
            Locked(() => page.RequestClose());
            yield return Refresh();
            // Items of different durability are separate cart lines.
            double specialGot = e2 != null ? e2.items.FindAll(x => x.key == plan.key && !x.buy).Sum(x => x.net) : double.NaN;
            bool doneOk = plan.state == 2 && plan.actualPaid && Math.Abs(plan.soldRevenue - specialGot) < 0.01 && plan.doneNote != null && plan.doneNote.Contains(qs.market.name) && plan.doneNote.Contains("结账记录");
            c.details.Add("plan after selling: " + plan.StateText + ", revenue " + plan.soldRevenue.ToString("0.0") + ", profit " + plan.CurProfit.ToString("0.0") + " / " + plan.doneNote + (doneOk ? " ok" : " WRONG"));
            ok &= doneOk;

            // 3) A credit change in the checkout frame (something else paying out at the same moment) must not pass as exact.
            n0 = Ledger.entries.Count; cp0 = gd.creditPoint;
            Locked(() =>
            {
                page = OpenTradePage(qs.market.ex, out h, out xc, out fc);
                addN = PutInCart(h, xc.GetItems(), it => it.SourceGoods == ns.normal, 1, true);
            });
            yield return new WaitForSecondsRealtime(1.5f);
            Locked(() => { gd.addCP(7, "TradeHelper selftest"); DoCheckout.Invoke(page, null); });
            yield return null; yield return null;
            var e3 = Ledger.entries.Count > n0 ? Ledger.entries[Ledger.entries.Count - 1] : null;
            bool flagOk = e3 != null && !e3.exact && e3.creditDelta - e3.billDelta == 7 && e3.note != null && e3.note.Contains("差 7");
            c.details.Add("credit change slipped into the checkout: " + (e3 == null ? "no entry" : "exact=" + e3.exact + " / " + e3.note) + (flagOk ? " ok" : " WRONG"));
            ok &= flagOk;
            Locked(() => page.RequestClose());

            host.SetVisible(true);
            host.ShowTab(6);
            yield return new WaitForSecondsRealtime(1.5f);
            yield return Shot("ledger");
            host.SetVisible(false);
            c.passed = ok;
            c.summary = (ok ? "all checkouts match" : "some steps differ") + " (" + qb.name + " " + qb.market.name + " -> " + qs.market.name + ", with " + nb.name + ")";
        }

        // A galaxy event window (e.g. regional inflation x1.15) may open or close between the scan and the check.
        static bool EventChanged(GoodsQuote q)
        {
            try { return FWGalaxyEventTradeEffects.Get(q.market.ex.uid, q.gid).Price != q.eventFactor; } catch { return false; }
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
            int n = 0, bad = 0, evSkip = 0; double worst = 0;
            int[] ks = { 1, 10, 100 };
            foreach (var m in snap.markets)
            {
                if (!m.Exact) continue;
                foreach (var q in m.goods)
                {
                    if (q.normal == null || q.special) continue;
                    if (EventChanged(q)) { evSkip++; continue; }
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
            c.summary = n + " quotes, " + bad + " off by more than 0.01%, worst " + (worst * 100).ToString("0.####") + "%; " + evSkip + " goods skipped (event changed since the scan)";
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
            int n = 0, bad = 0, evSkip = 0; double worst = 0;
            foreach (var m in snap.markets)
            {
                if (!m.Exact) continue;
                foreach (var q in m.goods)
                {
                    var sg = q.specialGoods;
                    if (!q.special || sg == null || sg.UsesEquipmentPressure || !q.buyable) continue;
                    if (EventChanged(q)) { evSkip++; continue; }
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
            c.summary = n + " quotes, " + bad + " off by more than 0.1%, worst " + (worst * 100).ToString("0.###") + "%; " + evSkip + " goods skipped (event changed since the scan)";
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
                    if (b.storeDur == null || !b.buyable || EventChanged(b)) continue;
                    var items = StoreItems(b.specialGoods);
                    int k = Math.Min(5, items.Count);
                    int used = 0;
                    foreach (var s in l)
                    {
                        if (s == b || used >= 3 || EventChanged(s)) continue;
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

        // Every galaxy market is named after the station(s) that open it.
        Check StationPlaces()
        {
            var c = new Check { name = "market names", passed = true };
            foreach (var m in host.Latest.markets)
                if (!m.planet && (m.name.StartsWith("寂幕旋区") || m.name.StartsWith("碧云转接") || m.name.StartsWith("库汇域")))
                    c.details.Add(m.name + " | " + m.where);
            int bad = host.Latest.markets.FindAll(m => !m.planet && !m.name.Contains("·")).Count;
            c.passed = bad == 0;
            c.summary = host.Latest.markets.FindAll(m => !m.planet).Count + " galaxy markets, " + bad + " without a station name";
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
                else if (!t.buy.market.Explored || !t.sell.market.Explored) why = "not explored";
                if (why != null) { bad++; if (c.details.Count < 10) c.details.Add(why + ": " + t.name + " " + t.buy.market.name + " -> " + t.sell.market.name); }
            }
            for (int i = 0; i < Math.Min(5, snap.trades.Count); i++)
            {
                var t = snap.trades[i];
                c.details.Add("top " + (i + 1) + ": " + t.name + " x" + t.qty + " " + t.buy.market.name + " -> " + t.sell.market.name + " profit " + t.Profit.ToString("0"));
            }
            foreach (var s in snap.sells)
            {
                string why = null;
                if (s.market.market.planet) why = "ground market";
                else if (s.market.contraband) why = "contraband";
                else if (!s.market.market.Explored) why = "not explored";
                if (why != null) { bad++; if (c.details.Count < 15) c.details.Add("sell " + why + ": " + s.name + " -> " + s.market.market.name); }
            }
            c.passed = bad == 0;
            c.summary = snap.trades.Count + " trade ideas, " + snap.sells.Count + " sell ideas, " + bad + " break the rules";
            return c;
        }

        // Every market's exploration state is the game's (GalaxyExplorationService), and the markets of galaxies only
        // discovered on the map are named, since advice leaves them out.
        Check CheckExploration(Snapshot snap)
        {
            var c = new Check { name = "exploration state = game" };
            var gd = MarketScanner.Game;
            int[] count = new int[3];
            int differ = 0;
            var discoveredOnly = new List<string>();
            foreach (var m in snap.markets)
            {
                if (m.planet) continue;
                var level = Script.FreeWorldAirShip.Galaxy.Exploration.GalaxyExplorationService.GetGalaxyLevel(gd, m.galaxyId);
                int want = level >= GalaxyKnowledgeLevel.Visited ? 2 : level >= GalaxyKnowledgeLevel.Detected ? 1 : 0;
                if (m.observe < 0 || m.observe > 2 || m.observe != want) { differ++; if (c.details.Count < 10) c.details.Add("differs: " + m.name + " mod " + m.observe + " game " + level); continue; }
                count[want]++;
                if (want == 1) discoveredOnly.Add(m.name);
            }
            c.details.Add("discovered on the map but not explored: " + (discoveredOnly.Count == 0 ? "none" : string.Join("、", discoveredOnly)));
            c.passed = differ == 0;
            c.summary = "explored " + count[2] + ", discovered only " + count[1] + ", not discovered " + count[0] + "; " + differ + " differ from the game";
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
