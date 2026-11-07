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
            // A save that used a mod not installed in the test copy makes the game ask whether to load it anyway
            // (UserBean.loadArchive), which would stop the test: drop such entries from this in-memory copy only.
            try
            {
                if (info.containsMods != null)
                {
                    var installed = new HashSet<string>();
                    foreach (var dir in Directory.GetDirectories(Path.Combine(Path.GetDirectoryName(Application.dataPath), "mods")))
                    {
                        var mf = Path.Combine(dir, "Manifest", "manifest.xml");
                        if (!File.Exists(mf)) continue;
                        var mm = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(mf), "<packageId>\\s*([^<\\s]+)\\s*</packageId>");
                        if (mm.Success) installed.Add(mm.Groups[1].Value);
                    }
                    int before = info.containsMods.Count;
                    info.containsMods.RemoveAll(m => m == null || !installed.Contains(m.Split('_')[0]));
                    if (info.containsMods.Count != before) Log("save lists " + (before - info.containsMods.Count) + " mod(s) not installed in the test copy; ignored");
                }
            }
            catch (Exception e) { Log("mod list check: " + e.Message); }
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
            if (req.tests == "smugglerlife")
            {
                yield return Guard(SmugglerLife(), "smuggler life and fleet cargo (measurement)");
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
                host.ShowTab(6);
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
            Run(() => CheckShips(snap));
            Run(() => InventoryPlaces(snap));
            Run(() => ProbeCalls(snap));
            Run(() => StationPlaces());

            if (req.tests == "all" || req.tests.Contains("trade")) yield return Guard(TradeTest(), "real trade: buy, plan stages, sell");
            if (req.tests == "all" || req.tests.Contains("ledger")) yield return Guard(LedgerTest(), "checkout ledger: the game's own checkout");
            if (req.tests == "all" || req.tests.Contains("ship")) yield return Guard(ShipTest(), "ship trade: the game's own checkout");
            if (req.tests == "all" || req.tests.Contains("caravan")) yield return Guard(CaravanTest(), "caravans: list, prices = game, checkout, smuggler");
            {
                // The advice page lists every category; picking one without a paying route shows its best margin.
                var cc = new Check { name = "advice page: all categories, notes for the unprofitable" };
                result.checks.Add(cc);
                host.SetVisible(true);
                host.ShowTab(0);
                yield return new WaitForSecondsRealtime(1.5f);
                var tv = Ui.TradesView.Current;
                string pick = null;
                foreach (var k in host.Latest.categoryNote.Keys) { pick = k; if (k == "战舰") break; }
                if (tv != null && pick != null) tv.PickCategory(pick);
                yield return new WaitForSecondsRealtime(0.5f);
                yield return Shot("advice-category");
                bool listed = tv != null && tv.CategoryOptions.Count == host.Latest.tradeCategories.Count + 1;
                cc.passed = listed && (pick == null || tv.EmptyText.Contains("差价最好") || tv.EmptyText.Contains("没法倒卖"));
                if (pick == null) cc.info = true;   // every category pays in this save: nothing to pick
                cc.summary = (tv == null ? "no page" : tv.CategoryOptions.Count - 1 + " categories in the dropdown; picked " + pick + ": " + tv.EmptyText);
                if (tv != null) tv.PickCategory("全部类别");
                host.SetVisible(false);
            }
            if (req.tests == "all" || req.tests.Contains("ui")) yield return Guard(UiInputTest(), "ui: search boxes take clicks");
            if (req.tests == "all" || req.tests.Contains("ui") || req.tests.Contains("resize")) yield return Guard(ResizeTest(), "panel resize");
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
            long cp0 = 0, payB = 0, payS = 0; int probeType = 0; string probeId = null; bool buyOk = false; float evNow = 1f;

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
                // A galaxy price event (e.g. regional inflation x1.15) may have begun since the scan: prices scale with it.
                evNow = FWGalaxyEventTradeEffects.Get(qb.market.ex.uid, qb.gid).Price;
                buyOk = SameMoney(payB, idea.cost * evNow / Math.Max(1e-6, qb.eventFactor));
            });
            if (abort != null) { c.passed = false; c.summary = abort; yield break; }
            c.details.Add("buy " + k + " " + qb.name + " at " + qb.market.name + ": charged " + payB + ", mod cost " + idea.cost.ToString("0") + (evNow != qb.eventFactor ? " (price event since the scan: x" + qb.eventFactor + " -> x" + evNow + ")" : "") + (buyOk ? " ok" : " DIFFERENT"));

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

            // Marked done by hand before selling (what a player did): flagged while the goods are still in the inventory,
            // and 改回已买入 brings it back.
            Plans.MarkDone(plan, host.Cfg);
            yield return Refresh();
            bool heldFlag = plan.state == 2 && plan.stillHeld == k && plan.StateText == "③ 已完成？库存还有" && plan.note.Contains("改回已买入");
            c.details.Add("marked done while still held: " + plan.StateText + " / " + plan.note + (heldFlag ? " ok" : " WRONG"));
            Plans.Reopen(plan, host.Cfg, host.settlement);
            yield return Refresh();
            heldFlag &= plan.state == 1 && plan.stillHeld == 0 && plan.missing == 0;
            c.details.Add("改回已买入: " + plan.StateText + (plan.state == 1 ? " ok" : " WRONG"));

            // One unit leaves the hold without a sale (used up or moved, as bases do with materials): not counted as sold;
            // the plan shows it missing. Putting it back clears that.
            FWThingBean taken = null;
            Locked(() =>
            {
                var st = ctx.GetTradeThings()?.Find(t => t != null && t.type == probeType && t.id == probeId && t.getSum() > 0);
                if (st != null) { taken = st.copyBean(); taken.setSum(1); ctx.RemoveSoldItems(new List<FWThingBean> { taken.copyBean() }); }
            });
            yield return Refresh();
            bool notSold = taken != null && plan.state == 1 && plan.soldQty == 0 && plan.missing == 1 && plan.StateText == "② 库存缺 1" && plan.note.Contains("没有卖出记录");
            c.details.Add("one unit gone without a sale: " + plan.StateText + ", sold " + plan.soldQty + " / " + plan.note + (notSold ? " ok" : " WRONG"));
            if (taken != null) Locked(() => ctx.AddBoughtItems(new List<FWThingBean> { taken.copyBean() }, qb.market.ex));
            yield return Refresh();
            notSold &= plan.state == 1 && plan.missing == 0 && plan.soldQty == 0;
            c.details.Add("unit put back: " + plan.StateText + (plan.missing == 0 ? " ok" : " WRONG"));

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
            c.passed = buyOk && stage2 && bestOk && heldFlag && notSold && decoyFlag && unbought && sellOk && stage3 && decoyKept && creditsOk && routeOk;
            c.summary = (c.passed ? "all steps match" : "some steps differ") + " (" + qb.name + " " + qb.market.name + " -> " + qs.market.name + ")";
        }

        // Where the player's goods are: every mothership deck and goods in transit are read (a player's goods moved to
        // the mothership's cargo deck were missed when only the fleet's deck was read).
        Check InventoryPlaces(Snapshot snap)
        {
            var c = new Check { name = "inventory: mothership decks and goods in transit", info = true };
            var gd = MarketScanner.Game;
            int decks = 0, deckThings = 0, freights = 0, freightThings = 0;
            foreach (var map in gd.mothership?.starMapList ?? new List<FWStarMapInfo>())
            {
                if (map?.starSpread == null) continue;
                decks++; deckThings += map.starSpread.things?.Count ?? 0;
                c.details.Add("deck " + map.starMapId + " " + map.name + ": " + (map.starSpread.things?.Count ?? 0) + " stacks, freights " + (map.freightList?.Count ?? 0));
                foreach (var f in map.freightList ?? new List<FWFreight>()) { freights++; freightThings += f?.things?.Count ?? 0; }
            }
            foreach (var loc in snap.inventory) c.details.Add(loc.name + ": " + loc.items.Count + " goods, " + loc.items.Values.Sum() + " units");
            foreach (var key in new[] { "17_45020", "12_69", "12_99", "12_22" })
            {
                var where = new List<string>();
                foreach (var loc in snap.inventory) { int v; if (loc.items.TryGetValue(key, out v)) where.Add(loc.name + " " + v); }
                c.details.Add(MarketScanner.ItemName(snap, key) + ": " + (where.Count == 0 ? "none" : string.Join("、", where)));
            }
            int exps = 0, expThings = 0;
            foreach (var ex in gd.expeditionSeq ?? new List<FWExpedition>()) { exps++; expThings += ex?.things?.Count ?? 0; }
            c.details.Add("transports under way: " + exps + " (" + expThings + " stacks)");
            c.passed = true;
            c.summary = decks + " mothership decks (" + deckThings + " stacks), " + freights + " freights to mothership decks (" + freightThings + " stacks)";
            return c;
        }

        // Ships: every ship in an up-to-date store priced by the mod as the trade page prices it (one ship at a time,
        // FWExchangeSpace.getSpecialGoodsPriceToSell), buying and reselling elsewhere, and the player's own ships.
        Check CheckShips(Snapshot snap)
        {
            var c = new Check { name = "ship prices = trade page" };
            int n = 0, bad = 0, markets = 0, withDefects = 0; double worst = 0;
            var byGid = new Dictionary<string, List<GoodsQuote>>();
            foreach (var m in snap.markets)
            {
                if (!m.Exact) continue;
                foreach (var q in m.goods)
                {
                    if (!q.ship || q.specialGoods == null || EventChanged(q)) continue;
                    List<GoodsQuote> l;
                    if (!byGid.TryGetValue(q.gid, out l)) byGid[q.gid] = l = new List<GoodsQuote>();
                    l.Add(q);
                    if (q.storeShips == null || q.storeShips.Count == 0) continue;
                    markets++;
                    double sum = 0;
                    for (int i = 0; i < q.storeShips.Count; i++)
                    {
                        if (q.shipDefects[i] > 0) withDefects++;
                        double game = m.ex.getSpecialGoodsPriceToSell(q.storeShips[i]);
                        double mod = q.BuyUnit(i); n++;
                        sum += game;
                        double r = Rel(mod, game); if (r > worst) worst = r;
                        if (r > 1e-3) { bad++; if (c.details.Count < 12) c.details.Add(m.name + " / " + q.name + " #" + i + " (" + q.shipDefects[i] + " defects): mod " + mod.ToString("0") + " game " + game.ToString("0")); }
                        if (i < 5)
                        {
                            double cost = q.BuyCost(i + 1), gcost = sum * Math.Max(0.0, 1.0 + q.tax); n++;
                            r = Rel(cost, gcost); if (r > worst) worst = r;
                            if (r > 1e-3) { bad++; if (c.details.Count < 12) c.details.Add(m.name + " / " + q.name + " x" + (i + 1) + ": mod cost " + cost.ToString("0") + " page " + gcost.ToString("0")); }
                        }
                    }
                }
            }
            foreach (var l in byGid.Values)
                foreach (var b in l)
                {
                    if (b.storeShips == null || b.storeShips.Count == 0) continue;
                    int k = Math.Min(5, b.storeShips.Count), used = 0;
                    foreach (var s in l)
                    {
                        if (s == b || used >= 3) continue;
                        used++;
                        double game = 0;
                        for (int i = 0; i < k; i++) game += s.market.ex.getSpecialGoodsPriceToSell(b.storeShips[i]);
                        game *= Math.Max(0.0, 1.0 - s.tax);
                        double mod = s.SellBoughtRevenue(0, b, 0, k); n++;
                        double r = Rel(mod, game); if (r > worst) worst = r;
                        if (r > 1e-3) { bad++; if (c.details.Count < 12) c.details.Add(b.market.name + " -> " + s.market.name + " / " + s.name + " x" + k + ": mod " + mod.ToString("0") + " page " + game.ToString("0")); }
                    }
                }
            c.passed = n > 0 && bad == 0;
            c.summary = markets + " ship lines in up-to-date stores (" + withDefects + " ships with defects), " + n + " quotes, " + bad + " off by more than 0.1%, worst " + (worst * 100).ToString("0.###") + "%";
            return c;
        }

        // Buys one defect-free ship at one market and sells it at another through the game's own trade page and checkout,
        // following a plan made from the advice's numbers.
        IEnumerator ShipTest()
        {
            var c = new Check { name = "ship trade: the game's own checkout" };
            result.checks.Add(c);
            yield return Refresh();
            var snap = host.Latest;
            var gd = MarketScanner.Game;
            GoodsQuote qb = null, qs = null;
            foreach (var m in snap.markets)
            {
                if (qb != null) break;
                if (!m.Exact || m.planet || !m.Explored) continue;
                foreach (var q in m.goods)
                {
                    if (!q.ship || !q.buyable || q.contraband || q.shipDefects == null || q.shipDefects.Length == 0 || q.shipDefects[0] != 0) continue;
                    if (q.buy1 > gd.creditPoint * 0.8) continue;   // the save must be able to pay for it
                    foreach (var m2 in snap.markets)
                    {
                        GoodsQuote q2;
                        if (m2 == m || !m2.Exact || m2.planet || !m2.Explored || !m2.byGid.TryGetValue(q.gid, out q2) || q2.contraband) continue;
                        qb = q; qs = q2; break;
                    }
                    if (qb != null) break;
                }
            }
            if (qb == null && DoCheckout != null) { c.info = true; c.passed = true; c.summary = "skipped: no affordable defect-free ship with two up-to-date markets (credits " + gd.creditPoint + ")"; yield break; }
            if (qb == null || DoCheckout == null) { c.passed = false; c.summary = "DoCheckout not found"; yield break; }
            var idea = new TradeIdea { gid = qb.gid, name = qb.name, category = qb.category, buy = qb, sell = qs, qty = 1, cost = qb.BuyCost(1), revenue = qs.SellBoughtRevenue(0, qb, 0, 1) };
            Plan plan = null;
            Locked(() => plan = Plans.FromTrade(idea, snap, host.Cfg, host.settlement));
            yield return Refresh();
            bool ok = true;
            long target = qb.storeShips[0].uid;
            var before = new HashSet<long>();
            Locked(() => { foreach (var s in gd.galaxyBlock.playerFleet.ships) if (s != null) before.Add(s.getUID()); });

            int n0 = Ledger.entries.Count; long cp0 = gd.creditPoint; int put = 0;
            PageTradeInventory page = null; ExchangeTransferHandler h = null; ExchangeInventoryContext xc; FleetInventoryContext fc;
            Locked(() =>
            {
                page = OpenTradePage(qb.market.ex, out h, out xc, out fc);
                foreach (var sh in page.GetShipsForPanel(PanelRole.ShopSystem))
                    if (sh?.bean != null && sh.getUid() == target && h.HandleShipTransfer(sh, PanelRole.ShopSystem, h.CurrentPlayerRole)) put++;
            });
            yield return new WaitForSecondsRealtime(1.5f);
            Locked(() => DoCheckout.Invoke(page, null));
            yield return null; yield return null;
            var e1 = Ledger.entries.Count > n0 ? Ledger.entries[Ledger.entries.Count - 1] : null;
            long d1 = gd.creditPoint - cp0;
            bool buyOk = put == 1 && e1 != null && e1.exact && e1.creditDelta == d1 && Math.Abs(-d1 - idea.cost) <= 1.5 && e1.items.Exists(x => x.buy && x.key == plan.key);
            c.details.Add("buy " + qb.name + " at " + qb.market.name + ": credits " + d1 + ", advice said " + (-idea.cost).ToString("0") + ", ledger " + (e1 == null ? "none" : "exact=" + e1.exact + " " + e1.creditDelta) + (buyOk ? " ok" : " WRONG"));
            ok &= buyOk;
            Locked(() => page.RequestClose());
            yield return Refresh();
            bool held = plan.state == 1 && plan.boughtQty == 1 && plan.CostKnown && Math.Abs(plan.paidCost + d1) <= 1;
            c.details.Add("plan after buying: " + plan.StateText + ", cost " + plan.paidCost.ToString("0") + (held ? " ok" : " WRONG"));
            ok &= held;

            n0 = Ledger.entries.Count; cp0 = gd.creditPoint; put = 0;
            Locked(() =>
            {
                page = OpenTradePage(qs.market.ex, out h, out xc, out fc);
                foreach (var sh in page.GetShipsForPanel(h.CurrentPlayerRole))
                    if (put == 0 && sh?.bean != null && sh.getShipId() == qb.itemId && !before.Contains(sh.getUid()) && h.HandleShipTransfer(sh, h.CurrentPlayerRole, PanelRole.ShopSystem)) put++;
            });
            yield return new WaitForSecondsRealtime(1.5f);
            Locked(() => DoCheckout.Invoke(page, null));
            yield return null; yield return null;
            var e2 = Ledger.entries.Count > n0 ? Ledger.entries[Ledger.entries.Count - 1] : null;
            long d2 = gd.creditPoint - cp0;
            bool sellOk = put == 1 && e2 != null && e2.exact && e2.creditDelta == d2 && Math.Abs(d2 - idea.revenue) <= 1.5;
            c.details.Add("sell at " + qs.market.name + ": credits " + d2 + ", advice said " + idea.revenue.ToString("0") + ", ledger " + (e2 == null ? "none" : "exact=" + e2.exact + " " + e2.creditDelta) + (sellOk ? " ok" : " WRONG"));
            ok &= sellOk;
            Locked(() => page.RequestClose());
            yield return Refresh();
            bool done = plan.state == 2 && plan.actualPaid && Math.Abs(plan.soldRevenue - d2) <= 1 && plan.doneNote != null && plan.doneNote.Contains(qs.market.name);
            c.details.Add("plan after selling: " + plan.StateText + " / " + plan.doneNote + ", profit " + plan.CurProfit.ToString("0") + (done ? " ok" : " WRONG"));
            ok &= done;
            c.passed = ok;
            c.summary = (ok ? "matches the advice and the credits" : "some steps differ") + " (" + qb.name + " " + qb.market.name + " -> " + qs.market.name + ")";
        }

        // Drags the panel's corner and edge the way the event system does (begin/drag/end on the handle), and picks a size
        // preset: the panel is built again at the new size, keeps its tab and data, and the plans table fits.
        IEnumerator ResizeTest()
        {
            var c = new Check { name = "panel resize" };
            result.checks.Add(c);
            host.SetVisible(true);
            host.ShowTab(1);
            yield return new WaitForSecondsRealtime(1f);
            var win = host.Win;
            bool ok = win != null;
            int rows0 = Ui.PlansView.Current?.TableForTest?.RowCount ?? -1;
            float scale = host.CanvasGo != null ? host.CanvasGo.GetComponent<Canvas>().scaleFactor : 1f;

            Func<string, Ui.ResizeHandle> handle = name =>
            {
                foreach (var h in host.CanvasGo.GetComponentsInChildren<Ui.ResizeHandle>(true)) if (h.name == name) return h;
                return null;
            };
            // 1) bottom-right corner: 150 narrower, 80 shorter.
            float w0 = win.W, h0 = win.H;
            var br = handle("ResizeBR");
            if (br == null) { c.passed = false; c.summary = "no resize handles"; host.SetVisible(false); yield break; }
            var es = UnityEngine.EventSystems.EventSystem.current;
            var p0 = new Vector2(1000, 400);
            br.OnBeginDrag(new UnityEngine.EventSystems.PointerEventData(es) { position = p0 });
            br.OnDrag(new UnityEngine.EventSystems.PointerEventData(es) { position = p0 + new Vector2(-150, 80) * scale });
            br.OnEndDrag(new UnityEngine.EventSystems.PointerEventData(es) { position = p0 + new Vector2(-150, 80) * scale });
            yield return new WaitForSecondsRealtime(1f);
            var t = Ui.PlansView.Current?.TableForTest;
            bool corner = Mathf.Abs(win.W - (w0 - 150)) < 1.5f && Mathf.Abs(win.H - (h0 - 80)) < 1.5f && t != null && t.ColumnsWidth <= t.Width - 19 && t.RowCount == rows0;
            c.details.Add("corner drag: " + w0 + "x" + h0 + " -> " + win.W + "x" + win.H + ", plans table columns " + (t?.ColumnsWidth ?? -1) + " in " + (t?.Width ?? -1) + ", rows " + (t?.RowCount ?? -1) + "/" + rows0 + (corner ? " ok" : " WRONG"));
            yield return Shot("resize-small");
            ok &= corner;

            // 2) left edge: 50 narrower, right edge stays where it was.
            float w1 = win.W;
            var lh = handle("ResizeL");
            var rt = (RectTransform)host.CanvasGo.transform.Find("Window");
            float right1 = rt.anchoredPosition.x + w1 / 2;
            lh.OnBeginDrag(new UnityEngine.EventSystems.PointerEventData(es) { position = p0 });
            lh.OnDrag(new UnityEngine.EventSystems.PointerEventData(es) { position = p0 + new Vector2(50, 0) * scale });
            lh.OnEndDrag(new UnityEngine.EventSystems.PointerEventData(es) { position = p0 + new Vector2(50, 0) * scale });
            yield return new WaitForSecondsRealtime(1f);
            rt = (RectTransform)host.CanvasGo.transform.Find("Window");
            float right2 = rt.anchoredPosition.x + win.W / 2;
            bool edge = Mathf.Abs(win.W - (w1 - 50)) < 1.5f && Mathf.Abs(right2 - right1) < 1.5f;
            c.details.Add("left edge drag: width " + w1 + " -> " + win.W + ", right side " + right1.ToString("0") + " -> " + right2.ToString("0") + (edge ? " ok" : " WRONG"));
            ok &= edge;

            // 3) can't go under the minimum.
            var bh = handle("ResizeB");
            bh.OnBeginDrag(new UnityEngine.EventSystems.PointerEventData(es) { position = p0 });
            bh.OnDrag(new UnityEngine.EventSystems.PointerEventData(es) { position = p0 + new Vector2(0, 2000) * scale });
            bh.OnEndDrag(new UnityEngine.EventSystems.PointerEventData(es) { position = p0 + new Vector2(0, 2000) * scale });
            yield return new WaitForSecondsRealtime(1f);
            bool min = Mathf.Abs(win.H - Ui.Window.MinH) < 1.5f;
            c.details.Add("bottom edge dragged far up: height " + win.H + " (minimum " + Ui.Window.MinH + ")" + (min ? " ok" : " WRONG"));
            ok &= min;

            // 4) the large preset from the settings page.
            win.ApplySize(1800, 1000);
            yield return new WaitForSecondsRealtime(1f);
            host.ShowTab(1);
            yield return new WaitForSecondsRealtime(0.5f);
            t = Ui.PlansView.Current?.TableForTest;
            bool big = Mathf.Abs(win.W - 1800) < 1.5f && Mathf.Abs(win.H - 1000) < 1.5f && t != null && t.ColumnsWidth <= t.Width - 19 && t.ColumnsWidth > 1600;
            c.details.Add("preset 1800x1000: " + win.W + "x" + win.H + ", plans table columns " + (t?.ColumnsWidth ?? -1) + " in " + (t?.Width ?? -1) + (big ? " ok" : " WRONG"));
            yield return Shot("resize-large");
            ok &= big;

            win.ApplySize(Ui.Window.DefaultW, Ui.Window.DefaultH);
            yield return new WaitForSecondsRealtime(1f);
            host.SetVisible(false);
            c.passed = ok;
            c.summary = ok ? "corner, edge, minimum and preset all resize and rebuild correctly" : "some resize steps differ";
        }

        // Every search box of the panel, clicked the way the game's event system would: the top raycast hit at its centre
        // must be the box, and a pointer click there must focus it. Also notes the event system and cursor state.
        IEnumerator UiInputTest()
        {
            var c = new Check { name = "ui: search boxes take clicks" };
            result.checks.Add(c);
            host.SetVisible(true);
            bool ok = true; int boxes = 0;
            var es = UnityEngine.EventSystems.EventSystem.current;
            c.details.Add("event system: " + (es == null ? "none" : es.name + " enabled=" + es.enabled + " module=" + (es.currentInputModule != null ? es.currentInputModule.GetType().Name + " enabled=" + es.currentInputModule.enabled : "none"))
                          + "; cursor visible=" + Cursor.visible + " lock=" + Cursor.lockState);
            // Once on its own, once over the game's open trade page (whose own widgets might take the focus back).
            PageTradeInventory gamePage = null;
            foreach (int pass in new[] { 0, 1 })
            foreach (int tab in new[] { 0, 2, 3 })
            {
                if (pass == 1 && gamePage == null)
                {
                    var mk = host.Latest?.markets.Find(m => m.Exact && !m.planet && m.Explored);
                    if (mk == null) { c.details.Add("no up-to-date market to open the game's trade page"); ok = false; break; }
                    Locked(() => { ExchangeTransferHandler h0; ExchangeInventoryContext x0; FleetInventoryContext f0; gamePage = OpenTradePage(mk.ex, out h0, out x0, out f0); });
                    yield return new WaitForSecondsRealtime(1f);
                    host.SetVisible(true);
                }
                host.ShowTab(tab);
                yield return new WaitForSecondsRealtime(1f);
                var cv = host.CanvasGo;
                if (cv == null || es == null) { ok = false; break; }
                foreach (var inp in cv.GetComponentsInChildren<TMPro.TMP_InputField>(false))
                {
                    boxes++;
                    var corners = new Vector3[4];
                    ((RectTransform)inp.transform).GetWorldCorners(corners);
                    var ped = new UnityEngine.EventSystems.PointerEventData(es) { position = (corners[0] + corners[2]) / 2, button = UnityEngine.EventSystems.PointerEventData.InputButton.Left };
                    var hits = new List<UnityEngine.EventSystems.RaycastResult>();
                    es.RaycastAll(ped, hits);
                    string top = string.Join(" > ", hits.Take(3).Select(h => h.gameObject.name + "@" + (h.module != null ? h.module.name : "?")));
                    bool onTop = hits.Count > 0 && hits[0].gameObject.transform.IsChildOf(inp.transform);
                    if (hits.Count > 0)
                    {
                        ped.pointerPressRaycast = hits[0]; ped.pointerCurrentRaycast = hits[0];
                        var target = UnityEngine.EventSystems.ExecuteEvents.GetEventHandler<UnityEngine.EventSystems.IPointerClickHandler>(hits[0].gameObject) ?? hits[0].gameObject;
                        UnityEngine.EventSystems.ExecuteEvents.Execute(target, ped, UnityEngine.EventSystems.ExecuteEvents.pointerDownHandler);
                        UnityEngine.EventSystems.ExecuteEvents.Execute(target, ped, UnityEngine.EventSystems.ExecuteEvents.pointerUpHandler);
                        UnityEngine.EventSystems.ExecuteEvents.Execute(target, ped, UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);
                    }
                    yield return new WaitForSecondsRealtime(0.5f);
                    bool focused = inp.isFocused;
                    c.details.Add((pass == 1 ? "over the game's trade page, " : "") + "tab " + tab + " box '" + (inp.placeholder is TMPro.TMP_Text ph ? ph.text : inp.name) + "': top hits " + top + (onTop ? "" : " (NOT the box)") + ", still focused 0.5 s after the click " + focused);
                    ok &= onTop && focused;
                    inp.DeactivateInputField();
                    es.SetSelectedGameObject(null);
                    yield return null;
                }
            }
            yield return Shot("ui");
            host.SetVisible(false);
            if (gamePage != null) Locked(() => gamePage.RequestClose());
            c.passed = ok && boxes > 0;
            c.summary = boxes + " search boxes; " + (c.passed ? "all take clicks" : "some do not take clicks");
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

        // ---- Caravans and suspicious caravans (smuggler fleets) ----
        static FWCaravanFleet FindCaravanFleet(FWGameData gd, long uid)
        {
            foreach (var f in gd.factionFleetRepository)
                if (f != null && f.uid == uid) return (f.fleet ?? f.materializedState) as FWCaravanFleet;
            return null;
        }

        static bool IsCrewKey(string k) => k.StartsWith("58_") || k.StartsWith("20_");

        static FWThingBean FindThing(IEnumerable<FWBaseBean> things, string key)
        {
            foreach (var b in things)
                if (b is FWThingBean t && t.getSum() > 0 && Caravans.ItemKey(t) == key) return t;
            return null;
        }

        // The mod's unit prices of one caravan against the game's caravan trade page (CaravanInventoryContext).
        static string ComparePrices(FWGameData gd, CaravanInfo ci, out int compared, out int differ)
        {
            compared = 0; differ = 0;
            var cf = FindCaravanFleet(gd, ci.uid);
            if (cf == null) return "fleet gone";
            var cc = new CaravanInventoryContext(cf);
            var hold = gd.galaxyBlock.playerFleet.getThings();
            var sb = new List<string>();
            foreach (var l in ci.lines)
            {
                if (double.IsNaN(l.unit)) { sb.Add(l.name + " not priced (" + l.note + ")"); continue; }
                var t = l.sells ? FindThing(cf.getCaravan().allGoods, l.key) : FindThing(hold.ConvertAll(x => (FWBaseBean)x), l.key);
                if (t == null && !l.sells && l.qty == 0)
                {
                    var p = l.key.Split(new[] { '_' }, 2);
                    t = new FWThingBean { type = int.Parse(p[0]), id = p[1], sum = 1 };
                }
                if (t == null) { sb.Add(l.name + " item not found"); continue; }
                double g;
                try { g = l.sells ? cc.GetSellUnitPrice(t) : cc.GetPurchaseUnitPrice(t); }
                catch (Exception e) { sb.Add(l.name + " game price failed: " + e.Message); differ++; continue; }
                compared++;
                bool same = Math.Abs(g - l.unit) <= Math.Max(0.01, Math.Abs(g) * 1e-6);   // the page's unit is a float
                if (!same) differ++;
                if (!same || sb.Count < 6) sb.Add((l.sells ? "sells " : "buys ") + l.name + " mod " + l.unit.ToString("0.00") + " game " + g.ToString("0.00") + (same ? "" : " DIFF"));
            }
            return string.Join("; ", sb);
        }

        // One checkout on the game's caravan trade page: buy one unit of `buyKey` and/or sell one unit of `sellKey`.
        // The credits must move by the mod's first-unit prices (rounded like DoCaravanCheckout) and the ledger must match.
        IEnumerator CaravanCheckout(Check c, CaravanInfo ci, CaravanLine buy, CaravanLine sell, Action<bool> done)
        {
            var gd = MarketScanner.Game;
            PageTradeInventory page = null; CaravanTransferHandler h = null; CaravanInventoryContext cc = null; FleetInventoryContext fc = null;
            int nb = 0, ns = 0; bool factorsSame = false;
            Locked(() =>
            {
                var cf = FindCaravanFleet(gd, ci.uid);
                fc = new FleetInventoryContext(AirShipLocalData.Instance.getCurFleet(), false);
                cc = new CaravanInventoryContext(cf);
                h = new CaravanTransferHandler(cc, fc);
                page = UIHelp.showPageTradeInventoryFleet();
                page.InitData(cc, fc, h);
                if (buy != null) nb = PutInCart(h, cc.GetItems(), it => it.RawData is FWThingBean t && Caravans.ItemKey(t) == buy.key, 1, true);
                if (sell != null) ns = PutInCart(h, fc.GetItems(), it => it.RawData is FWThingBean t && Caravans.ItemKey(t) == sell.key, 1, false);
            });
            yield return new WaitForSecondsRealtime(1.5f);
            yield return Shot(ci.black ? "smuggler-trade-page" : "caravan-trade-page");
            {
                // Exact numbers: the mod's units, the game's page units and the cart's own quote.
                string g = null;
                Locked(() =>
                {
                    try {
                    var parts = new List<string>();
                    if (buy != null) { var t = FindThing(FindCaravanFleet(gd, ci.uid).getCaravan().allGoods, buy.key); parts.Add("buy mod " + buy.unit.ToString("0.000000") + " game " + (t == null ? "?" : cc.GetSellUnitPrice(t).ToString("0.000000"))); }
                    if (sell != null) { var t = FindThing(gd.galaxyBlock.playerFleet.getThings().ConvertAll(x => (FWBaseBean)x), sell.key); parts.Add("sell mod " + sell.unit.ToString("0.000000") + " game " + (t == null ? "?" : cc.GetPurchaseUnitPrice(t).ToString("0.000000"))); }
                    parts.Add("factor buy " + cc.GetPriceFactor(true).ToString("0.0000000000") + " mod " + ci.FactorBuy.ToString("0.0000000000") + ", sell " + cc.GetPriceFactor(false).ToString("0.0000000000") + " mod " + ci.FactorSell.ToString("0.0000000000"));
                    factorsSame = cc.GetPriceFactor(true) == ci.FactorBuy && cc.GetPriceFactor(false) == ci.FactorSell;
                    g = string.Join("; ", parts);
                    } catch (Exception ex) { g = "game price failed: " + ex.Message; }
                });
                c.details.Add("exact: " + g + (factorsSame ? " (factors identical)" : " FACTORS DIFFER"));
            }
            int n0 = Ledger.entries.Count; long cp0 = gd.creditPoint;
            string coErr = null;
            Locked(() => { try { DoCheckout.Invoke(page, null); } catch (Exception ex) { coErr = (ex.InnerException ?? ex).Message; } });
            if (coErr != null) c.details.Add("checkout failed: " + coErr);
            yield return null; yield return null;
            var e = Ledger.entries.Count > n0 ? Ledger.entries[Ledger.entries.Count - 1] : null;
            long d = gd.creditPoint - cp0;
            long expect = (sell != null ? FWTradePricePressure.RoundPayment(sell.unit, false) : 0) - (buy != null ? FWTradePricePressure.RoundPayment(buy.unit, true) : 0);
            bool ok = factorsSame && (buy == null || nb == 1) && (sell == null || ns == 1) && d == expect && e != null && e.exact && e.creditDelta == d && e.market != null && e.market.Contains(ci.name);
            c.details.Add((ci.black ? "smuggler" : "caravan") + " checkout: buy " + (buy?.name ?? "-") + " x" + nb + ", sell " + (sell?.name ?? "-") + " x" + ns + ", credits " + d + ", mod predicted " + expect
                          + ", ledger " + (e == null ? "none" : e.market + " " + e.creditDelta + " exact=" + e.exact + " " + string.Join("; ", e.items.ConvertAll(x => x.name + " x" + x.qty + " raw " + x.raw.ToString("0.000000") + " net " + x.net.ToString("0.000000")))) + (ok ? " ok" : " WRONG"));
            Locked(() => page.RequestClose());
            yield return new WaitForSecondsRealtime(0.5f);
            done(ok);
        }

        // A smuggler as the mod shows it: the dialogue's deals as parsed from the game's trigger scripts, and for the
        // hold sale, each listed goods counted the way the dialogue counts it (Global.Player.GetItemCount(type, id, 1) =
        // the fleet's grouped things) and paid base x rate.
        static bool CheckSmuggler(FWGameData gd, CaravanInfo si, out string s)
        {
            var d = Caravans.Deals();
            var sb = new List<string>();
            bool ok = si.smuggler && si.kind == "可疑商队" && d.error == null && d.sells.Count >= 3 && d.buys.Count > 20 && d.rate > 0;
            sb.Add("deals: " + string.Join(", ", d.sells.ConvertAll(o => o.type + "_" + o.id + " x" + o.count + " for " + o.cost + (o.once != null ? " once" : ""))) + "; buys " + d.buys.Count + " goods x" + d.rate + " once=" + d.buyOnce);
            ok &= si.lines.FindAll(l => l.sells).Count == d.sells.Count;
            var fleet = AirShipLocalData.Instance.getCurFleet();
            int listed = 0, held = 0, bad = 0;
            foreach (var kv in d.buys)
            {
                listed++;
                var p = kv.Key.Split(new[] { '_' }, 2);
                int n = 0;
                try { n = fleet.getThingBeanByTypeId_Group(int.Parse(p[0]), p[1], false)?.getSum() ?? 0; } catch { }
                var line = si.lines.Find(l => !l.sells && l.key == kv.Key);
                int shown = line == null ? 0 : line.qty;
                if (n > 0) held++;
                bool same = shown == n && (line == null || Math.Abs(line.unit - kv.Value * d.rate) < 0.01);
                if (!same) { bad++; sb.Add("hold " + kv.Key + " game " + n + " mod " + shown + (line != null ? " unit " + line.unit + " total " + line.total : "")); }
            }
            sb.Add("hold sale: " + held + " of " + listed + " listed goods in the hold, total " + si.smugglerSaleTotal + ", " + bad + " differ");
            ok &= bad == 0;
            s = string.Join("; ", sb);
            return ok;
        }

        // Measurement for the player's questions: how much the fleet carries, where transports go, and how long a
        // freshly spawned smuggler stays in the galaxy view with the clock running, and why it leaves.
        IEnumerator SmugglerLife()
        {
            var c = new Check { name = "smuggler life and fleet cargo (measurement)", info = true, passed = true };
            result.checks.Add(c);
            var gd = MarketScanner.Game;
            var fleet = AirShipLocalData.Instance.getCurFleet();
            Locked(() =>
            {
                float w = 0; var rows = new List<string>();
                foreach (var t in fleet.bean.getThings())
                {
                    if (t == null) continue;
                    float uw = t.getDef() != null ? t.getDef().Weight : -1;
                    w += uw * t.sum;
                    rows.Add(t.type + "_" + t.id + " x" + t.sum + " w" + uw + "=" + (uw * t.sum).ToString("0"));
                }
                rows.Sort((x, y) => -float.Parse(x.Substring(x.LastIndexOf('=') + 1)).CompareTo(float.Parse(y.Substring(y.LastIndexOf('=') + 1))));
                c.details.Add("fleet " + fleet.bean.getUid() + ": ships " + fleet.bean.getShips().Count + ", capacity (SumCapacity) " + fleet.SumCapacity + ", cargo weight " + w.ToString("0") + "; heaviest: " + string.Join(", ", rows.GetRange(0, Math.Min(12, rows.Count))));
            });
            Locked(() =>
            {
                var ex = gd.expeditionSeq;
                c.details.Add("transports in flight: " + (ex == null ? 0 : ex.Count));
                if (ex != null)
                    foreach (var e in ex)
                    {
                        if (e == null) continue;
                        var fs = new List<string>();
                        foreach (var f in e.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
                        {
                            object v = null; try { v = f.GetValue(e); } catch { }
                            if (v == null || v is System.Collections.ICollection || v is string sv && sv.Length == 0) continue;
                            fs.Add(f.Name + "=" + v);
                        }
                        int n = 0; try { foreach (var t in e.things) n += t.sum; } catch { }
                        c.details.Add("  transport: items " + n + "; " + string.Join(" ", fs.GetRange(0, Math.Min(25, fs.Count))));
                    }
            });
            // A fresh smuggler in the fleet's galaxy (test copy: next check now), then the clock at x3.
            string fg = fleet.getGalaxyId();
            Func<FWFactionFleet> sm = () =>
            {
                FWFactionFleet r = null;
                foreach (var f in gd.factionFleetRepository)
                    if (f != null && f.IsNomadicOwned && f.galaxyId == fg && ImplDef<G_ForceFleetType_Def>.getDataID(f.gForceFleetTypeResId)?.ActivityType == (int)GalaxyNPCFleetType.Smuggler && (r == null || f.activityAppearTime > r.activityAppearTime)) r = f;
                return r;
            };
            Locked(() => { var g = gd.getGalaxy(fg); if (g != null) g.nextNomadicFleetRefreshTime = gd.elapsedMM; });
            yield return Refresh();
            var spot = host.Latest.smugglerSpots.Find(x => x.galaxyId == fg);
            c.details.Add("forecast for " + fg + " before entering: " + (spot == null ? "missing" : spot.StateText + (spot.predicted ? ", spawn point " + spot.predStart.ToString("F2") + ", route " + spot.predRoute.Count + " points, day " + spot.predDay + ", slot " + spot.predSlot : ", no prediction: " + spot.predNote)));
            try { MainUI_Head.GoGalaxyFleet(); } catch (Exception e) { c.details.Add("GoGalaxyFleet: " + e.Message); }
            float t0 = Time.realtimeSinceStartup;
            FWFactionFleet s0 = null;
            while (Time.realtimeSinceStartup - t0 < 60)
            {
                yield return new WaitForSecondsRealtime(1);
                FWFactionFleet x = null; Locked(() => x = sm());
                if (x != null && !x.resultSettled && x.fleet != null) { s0 = x; break; }
            }
            if (s0 == null) { c.details.Add("no smuggler came into the scene"); yield break; }
            long spawnMM = s0.activityAppearTime;
            var mgr = Script.FreeWorldAirShip.Galaxy.NPCFleet.GalaxyNPCFleetManager.Instance;
            {
                // The prediction against what the game made.
                var real = new List<Vector2>(); Locked(() => { foreach (var pt in s0.activityRoutePoints) real.Add(new Vector2(pt.x, pt.y)); });
                bool same = spot != null && spot.predicted && real.Count == spot.predRoute.Count;
                float worst = 0;
                for (int k = 0; same && k < real.Count; k++) worst = Mathf.Max(worst, Vector2.Distance(real[k], spot.predRoute[k]));
                same &= worst < 0.01f;
                c.details.Add("spawn prediction: game route " + real.Count + " points starting " + (real.Count > 0 ? real[0].ToString("F2") : "?") + "; predicted " + (spot != null && spot.predicted ? spot.predRoute.Count + " starting " + spot.predStart.ToString("F2") : "none") + "; worst point gap " + worst.ToString("0.000") + (same ? " SAME" : " DIFFERENT"));
                c.passed &= same;
                var npc0 = mgr?.GetFleetByUid(s0.uid);
                if (npc0 != null)
                {
                    var w1 = npc0.transform.position; var w2 = Ui.Tracker.LocalToWorld(npc0.GetLocalPosition());
                    var pf0 = npc0.GetModule<Script.FreeWorldAirShip.Galaxy.NPCFleet.Modules.GalaxyNPCFleetPathFindingModule>();
                    string pinfo = pf0 == null ? "no path module" : "path index " + pf0.CurrentPathIndex + " next " + pf0.GetNextPathPoint().ToString("F2") + " route[idx] " + (pf0.CurrentPathIndex < real.Count ? real[pf0.CurrentPathIndex].ToString("F2") : "?");
                    c.details.Add("coordinates: fleet object " + w1.ToString("F2") + " vs local->world " + w2.ToString("F2") + " (gap " + Vector3.Distance(w1, w2).ToString("0.000") + "); " + pinfo);
                }
            }
            // The point marker on the predicted spawn point, now that the galaxy view is up.
            if (spot != null && spot.predicted)
            {
                Ui.Tracker.TrackPoint(fg, "", spot.predStart, "出生点");
                yield return new WaitForSecondsRealtime(1);
                var cam = Ui.Tracker.LastCamera;
                if (cam != null)
                {
                    var pr = cam.WorldToScreenPoint(Ui.Tracker.PointWorld); var mk = Ui.Tracker.ScreenPos;
                    c.details.Add("spawn point marker: shown " + Ui.Tracker.Shown + ", " + (Ui.Tracker.OnScreen ? "on screen at " + mk.ToString("F0") + " for projection " + pr.ToString("F0") : "on the edge " + mk.ToString("F0")) + "; " + Ui.Tracker.Status);
                }
                yield return Shot("spawn-point");
                Ui.Tracker.Stop();
            }
            yield return Refresh();
            // Every caravan and the smuggler in this galaxy: route, the mod's estimate a little after it is seen, and when
            // and why it really leaves. Clock x3 as the player plays.
            var watch = new Dictionary<long, string>();
            var est = new Dictionary<long, KeyValuePair<long, long>>();   // uid -> (estimate, game time it was made)
            var done = new HashSet<long>();
            MapManager.setLogicSpeed(3f);
            float tr = Time.realtimeSinceStartup; long lastLog = -1;
            while (Time.realtimeSinceStartup - tr < 900)
            {
                yield return new WaitForSecondsRealtime(1);
                long now = gd.elapsedMM;
                var snapNow = host.Latest;
                if (Time.realtimeSinceStartup - tr > 5 && (long)(Time.realtimeSinceStartup - tr) % 20 == 0) yield return Refresh();
                int open = 0;
                foreach (var ci in host.Latest.caravans)
                {
                    if (ci.galaxyId != fg || done.Contains(ci.uid)) continue;
                    if (!watch.ContainsKey(ci.uid))
                    {
                        double len = 0; for (int k = 1; ci.route != null && k < ci.route.Count; k++) len += Vector2.Distance(ci.route[k - 1], ci.route[k]);
                        watch[ci.uid] = (ci.black ? "smuggler " : "caravan ") + ci.FullName + " (route " + (ci.route?.Count ?? 0) + " points, length " + len.ToString("0.0") + (ci.journey ? ", crossing galaxies" : "") + ", latest " + Ui.CaravansView.Left(ci.leftMM) + ")";
                    }
                    long e = Motion.LeaveMM(ci.uid);
                    if (e >= 0 && (!est.ContainsKey(ci.uid) ? now - spawnMM > 30 * 60000L : now - est[ci.uid].Value >= 2 * 60 * 60000L && !est.ContainsKey(-ci.uid)))
                    {
                        // The first estimate (kept for the accuracy check) and a second one 2 h later.
                        if (est.ContainsKey(ci.uid)) est[-ci.uid] = new KeyValuePair<long, long>(e, now); else est[ci.uid] = new KeyValuePair<long, long>(e, now);
                        c.details.Add("  estimate for " + watch[ci.uid] + " at +" + ((now - spawnMM) / 60000.0).ToString("0.0") + " h: leaves in about " + (e / 60000.0).ToString("0.0") + " h (" + Motion.Describe(ci.uid) + ")");
                    }
                    bool settled = false; string reason = null;
                    Locked(() => { string g; var ff = FactionFleetRegistryService.Find(gd, ci.uid, out g); settled = ff == null || ff.resultSettled; reason = ff?.settlementReason ?? "gone from the records"; });
                    if (settled)
                    {
                        done.Add(ci.uid);
                        string acc = "";
                        KeyValuePair<long, long> kv;
                        if (est.TryGetValue(ci.uid, out kv))
                        {
                            double real = (now - kv.Value) / 60000.0, guess = kv.Key / 60000.0;
                            acc = "; the estimate said " + guess.ToString("0.0") + " h, it took " + real.ToString("0.0") + " h (off " + (real > 0 ? (Math.Abs(guess - real) / real * 100).ToString("0") : "?") + "%)";
                            if (est.TryGetValue(-ci.uid, out kv)) { double r2 = (now - kv.Value) / 60000.0, g2 = kv.Key / 60000.0; acc += "; 2 h later it said " + g2.ToString("0.0") + " h, it took " + r2.ToString("0.0") + " h"; }
                        }
                        c.details.Add("gone: " + watch[ci.uid] + " after +" + ((now - spawnMM) / 60000.0).ToString("0.0") + " h of the measurement (" + (Time.realtimeSinceStartup - tr).ToString("0") + " s real): " + reason + acc);
                    }
                    else open++;
                }
                long gh = (now - spawnMM) / 60000;
                if (gh != lastLog && gh % 2 == 0)
                {
                    lastLog = gh;
                    var parts = new List<string>();
                    foreach (var kv in watch)
                    {
                        if (done.Contains(kv.Key)) continue;
                        var n = mgr?.GetFleetByUid(kv.Key);
                        if (n == null) { parts.Add(kv.Key % 100000 + ": no scene object"); continue; }
                        string pfs = "?";
                        try { var pf = n.GetModule<Script.FreeWorldAirShip.Galaxy.NPCFleet.Modules.GalaxyNPCFleetPathFindingModule>(); pfs = pf == null ? "no path module" : "valid " + pf.HasValidPath + " " + pf.CurrentPathIndex + "/" + pf.PathPoints.Count; } catch (Exception ex) { pfs = ex.Message; }
                        parts.Add(kv.Key % 100000 + ": " + n.CurrentPrimaryState + ", inertial " + n.HasInertialMotion + ", sector " + n.SectorManaged + ", docked " + n.IsDocked + ", journey " + n.HasJourney + ", path " + pfs + ", est " + (Motion.LeaveMM(kv.Key) >= 0 ? (Motion.LeaveMM(kv.Key) / 60000.0).ToString("0.0") + " h" : "-") + " (" + Motion.Describe(kv.Key) + ")");
                    }
                    c.details.Add("  +" + gh + " game h: " + open + " still here; " + string.Join(" | ", parts));
                }
                if (open == 0 && done.Count > 0 && done.Contains(s0.uid) && Time.realtimeSinceStartup - tr > 60) break;
            }
            foreach (var kv in watch) if (!done.Contains(kv.Key)) c.details.Add("still here at the end: " + kv.Value + (Motion.LeaveMM(kv.Key) >= 0 ? ", estimate " + (Motion.LeaveMM(kv.Key) / 60000.0).ToString("0.0") + " h" : ""));
            MapManager.setLogicSpeed(1f); MapManager.pauseLogic();
            c.summary = string.Join(" | ", c.details.FindAll(d => d.StartsWith("gone:") || d.StartsWith("spawn prediction")));
        }

        IEnumerator CaravanTest()
        {
            var c = new Check { name = "caravans: list, prices = game, checkout, smuggler" };
            result.checks.Add(c);
            yield return Refresh();
            var gd = MarketScanner.Game;
            var snap = host.Latest;
            bool ok = true;
            int expected = 0;
            int present = 0;
            Locked(() =>
            {
                foreach (var f in gd.factionFleetRepository)
                {
                    if (f == null || f.resultSettled) continue;
                    bool has = ((f.fleet ?? f.materializedState) as FWCaravanFleet)?.getCaravan() != null;
                    int t = f.runtimeFleetTypeOverride >= 0 && f.runtimeFleetTypeOverride < 11 ? f.runtimeFleetTypeOverride : (ImplDef<G_ForceFleetType_Def>.getDataID(f.gForceFleetTypeResId)?.ActivityType ?? -1);
                    if (has || t == (int)GalaxyNPCFleetType.Trade || t == (int)GalaxyNPCFleetType.Smuggler) expected++;
                    if (has) present++;
                }
            });
            Func<CaravanInfo, string> desc = x => (x.present ? "" : "[not in scene] ") + x.kind + " " + x.name + " @" + x.galaxyName + " " + x.status + " au " + x.fleetAu.ToString("0") + " left " + Ui.CaravansView.Left(x.leftMM) + " cash " + x.cash
                + " pm " + x.priceModifier + (x.metPlayer ? "" : "(no record)") + " rel " + x.relation + " tax " + x.tax.ToString("0.###") + " source " + x.source + " priced " + x.priced
                + " lines " + x.lines.FindAll(l => l.sells).Count + " sell/" + x.lines.FindAll(l => !l.sells).Count + " buy " + x.priceNote;
            c.details.Add("caravans in save " + expected + ", mod lists " + snap.caravans.Count + ": " + string.Join(" | ", snap.caravans.ConvertAll(x => desc(x))));
            // Fleets spawn and leave while the game runs: compare counts taken right after a fresh scan, a few times.
            for (int tries = 0; tries < 4 && expected != snap.caravans.Count; tries++)
            {
                yield return Refresh(); snap = host.Latest;
                expected = 0;
                Locked(() =>
                {
                    foreach (var f in gd.factionFleetRepository)
                    {
                        if (f == null || f.resultSettled) continue;
                        bool has = ((f.fleet ?? f.materializedState) as FWCaravanFleet)?.getCaravan() != null;
                        int t = f.runtimeFleetTypeOverride >= 0 && f.runtimeFleetTypeOverride < 11 ? f.runtimeFleetTypeOverride : (ImplDef<G_ForceFleetType_Def>.getDataID(f.gForceFleetTypeResId)?.ActivityType ?? -1);
                        if (has || t == (int)GalaxyNPCFleetType.Trade || t == (int)GalaxyNPCFleetType.Smuggler) expected++;
                    }
                });
                c.details.Add("recount after a fresh scan: save " + expected + ", mod " + snap.caravans.Count);
            }
            ok &= expected == snap.caravans.Count;
            if (snap.caravans.Count == 0) { c.passed = false; c.summary = "no caravan in the test save"; yield break; }
            // Caravans get their goods when the game puts them in the scene: open the galaxy the fleet is in, as the
            // player does, and wait for the scene's fleets.
            // The player's path that showed nothing: the star field (至星域) zoomed onto the fleet's galaxy has no NPC
            // fleets; the galaxy scene (至星系) does. Go to the star field the way the button does, check nothing is in
            // the scene there and the tracker says so, then let the tracker's "locate" enter the galaxy scene.
            {
                Func<string> stateNow = () => { try { return Script.FreeWorldAirShip.Galaxy.GalaxyField.GalaxyFieldScene.Instance?.CurState.ToString() ?? "no galaxy scene"; } catch (Exception e) { return e.Message; } };
                c.details.Add("start: scene state " + stateNow() + ", " + present + " present in the save");
                string fg0 = snap.fleetGalaxyId;
                var spot0 = snap.smugglerSpots.Find(x => x.galaxyId == fg0);
                long next0 = -1; int smugglers0 = 0;
                Locked(() =>
                {
                    var g0 = gd.getGalaxy(fg0); if (g0 != null) next0 = g0.nextNomadicFleetRefreshTime;
                    foreach (var f in gd.factionFleetRepository)
                        if (f != null && !f.resultSettled && f.IsNomadicOwned && f.galaxyId == fg0 && ImplDef<G_ForceFleetType_Def>.getDataID(f.gForceFleetTypeResId)?.ActivityType == (int)GalaxyNPCFleetType.Smuggler) smugglers0++;
                });
                if (spot0 != null && spot0.state == SmugglerSpot.Wait && smugglers0 == 0)
                {
                    // Test copy only: bring this galaxy's next check to now, so entering it spawns a smuggler for the
                    // dialogue test; the forecast must follow.
                    string before = spot0.StateText + " " + Ui.CaravansView.Left(spot0.waitMM);
                    Locked(() => { var g0 = gd.getGalaxy(fg0); if (g0 != null) g0.nextNomadicFleetRefreshTime = gd.elapsedMM; });
                    yield return Refresh(); snap = host.Latest;
                    spot0 = snap.smugglerSpots.Find(x => x.galaxyId == fg0);
                    Locked(() => { var g0 = gd.getGalaxy(fg0); if (g0 != null) next0 = g0.nextNomadicFleetRefreshTime; });
                    c.details.Add("test copy: next smuggler check in " + fg0 + " set to now (forecast was " + before + ", now " + (spot0?.StateText ?? "missing") + ")");
                }
                c.details.Add("smuggler forecast for the fleet's galaxy " + fg0 + ": " + (spot0 == null ? "missing" : spot0.StateText + (spot0.state == SmugglerSpot.Wait ? " " + Ui.CaravansView.Left(spot0.waitMM) : "")) + "; save: next check in " + ((next0 - gd.elapsedMM) / 60000.0).ToString("0.0") + " h, smugglers there " + smugglers0 + "; " + snap.smugglerSpots.Count + " galaxies in the forecast, " + snap.smugglerSpots.FindAll(x => x.state == SmugglerSpot.Due).Count + " due, " + snap.smugglerSpots.FindAll(x => x.state == SmugglerSpot.Never).Count + " never");
                if (stateNow() != "GalaxyField")
                {
                    try { MainUI_Head.GoGalaxyField(null); } catch (Exception e) { c.details.Add("GoGalaxyField failed: " + e.Message); }
                    float tf = Time.realtimeSinceStartup;
                    while (Time.realtimeSinceStartup - tf < 40 && stateNow() != "GalaxyField") yield return new WaitForSecondsRealtime(1);
                }
                yield return new WaitForSecondsRealtime(6);
                yield return Refresh(); snap = host.Latest;
                string fleetGalaxy = null;
                try { fleetGalaxy = AirShipLocalData.Instance.getCurFleet().getGalaxyId(); } catch { }
                var here = snap.caravans.FindAll(x => x.galaxyId == fleetGalaxy);
                int inField = snap.caravans.FindAll(x => x.present).Count;
                yield return Shot("star-field");
                bool fieldOk = stateNow() == "GalaxyField" && inField == 0;
                c.details.Add("star field: state " + stateNow() + ", caravans in the scene " + inField + " (expected 0), in the fleet's galaxy " + fleetGalaxy + ": " + here.Count + (fieldOk ? " ok" : " WRONG"));
                ok &= fieldOk;
                if (here.Count > 0)
                {
                    var tf0 = here.Find(x => x.black) ?? here[0];
                    Func<long, string> fstate = uid =>
                    {
                        string r = "";
                        Locked(() =>
                        {
                            string g; var ff = FactionFleetRegistryService.Find(gd, uid, out g);
                            if (ff == null) { r = "gone from the fleet records"; return; }
                            var mg = Script.FreeWorldAirShip.Galaxy.NPCFleet.GalaxyNPCFleetManager.Instance;
                            bool admitted = false;
                            try { var gs0 = GalaxyMapData.getInstance().getGalaxyStar(ff.galaxyId); admitted = gs0 != null && gs0.fwGalaxyBean.getFwGalaxy().GetAdmittedFleetBeanList().Exists(x => (x is FWFactionFleet xf && xf.getUid() == uid) || (x is FWFleet xl && xl.getUid() == uid)); } catch { }
                            r = "galaxy " + ff.galaxyId + " settled " + ff.resultSettled + " presence " + ff.presenceMode + " docked " + ff.dockedSpaceportUid + " journey " + (ff.journey != null ? ff.journey.phase.ToString() : "none")
                                + " external " + ff.HasExternalPresence + " admitted " + admitted + " scene object " + (ff.fleet != null) + " in the game's table " + (mg != null && mg.HasFleet(uid))
                                + " leaves in " + (ff.activityDisappearTime > 0 ? ((ff.activityDisappearTime - gd.elapsedMM) / 60000.0).ToString("0.0") + " h" : "-") + " target " + ff.activityTargetKey;
                        });
                        return r;
                    };
                    long clock0 = gd.elapsedMM;
                    foreach (var h in here) c.details.Add("before entering: " + h.FullName + " " + h.uid + ": " + fstate(h.uid));
                    host.SetVisible(false);
                    Ui.Tracker.Track(tf0, false);
                    yield return null; yield return null;
                    bool msgOk = !Ui.Tracker.Shown && Ui.Tracker.Status.Contains("至星系");
                    c.details.Add("tracker in the star field: " + Ui.Tracker.Status + (msgOk ? " ok" : " WRONG"));
                    ok &= msgOk;
                    bool focused = Ui.Tracker.Focus();
                    float tw = Time.realtimeSinceStartup;
                    while (Time.realtimeSinceStartup - tw < 60 && !(Ui.Tracker.InGalaxyView && Ui.Tracker.Shown)) yield return new WaitForSecondsRealtime(1);
                    yield return new WaitForSecondsRealtime(2);
                    yield return Refresh(); snap = host.Latest;
                    var tf1 = snap.caravans.Find(x => x.uid == tf0.uid);
                    c.details.Add("game clock moved " + ((gd.elapsedMM - clock0) / 60000.0).ToString("0.00") + " h while entering");
                    foreach (var h in here) c.details.Add("after entering: " + h.FullName + " " + h.uid + ": " + fstate(h.uid));
                    bool enterOk = focused && Ui.Tracker.InGalaxyView && Ui.Tracker.Shown && tf1 != null && tf1.present;
                    yield return Shot("located");
                    c.details.Add("locate from the star field: Focus " + focused + ", state " + stateNow() + " after " + (Time.realtimeSinceStartup - tw).ToString("0") + " s, found " + Ui.Tracker.Found + ", " + tf0.FullName + " in the scene " + (tf1 != null && tf1.present) + "; " + Ui.Tracker.Status + (enterOk ? " ok" : " WRONG"));
                    ok &= enterOk;
                    Ui.Tracker.Stop();
                }
                else c.details.Add("no caravan in the fleet's galaxy: locate-from-star-field not checked");
                if (stateNow() != "GalaxyScene")
                {
                    try { MainUI_Head.GoGalaxyFleet(); } catch (Exception e) { c.details.Add("GoGalaxyFleet failed: " + e.Message); }
                    float tg = Time.realtimeSinceStartup;
                    while (Time.realtimeSinceStartup - tg < 60 && stateNow() != "GalaxyScene") yield return new WaitForSecondsRealtime(1);
                    yield return new WaitForSecondsRealtime(3);
                }
                {
                    // The forecast came true: entering the galaxy view made the game check this galaxy.
                    int smugglers1 = 0; long next1 = -1; string newName = null;
                    Locked(() =>
                    {
                        var g1 = gd.getGalaxy(fg0); if (g1 != null) next1 = g1.nextNomadicFleetRefreshTime;
                        foreach (var f in gd.factionFleetRepository)
                            if (f != null && !f.resultSettled && f.IsNomadicOwned && f.galaxyId == fg0 && ImplDef<G_ForceFleetType_Def>.getDataID(f.gForceFleetTypeResId)?.ActivityType == (int)GalaxyNPCFleetType.Smuggler) { smugglers1++; newName = f.Name; }
                    });
                    bool fOk;
                    string expect;
                    if (spot0 == null) { fOk = false; expect = "no forecast"; }
                    else if (spot0.state == SmugglerSpot.Due) { fOk = smugglers1 == 1 && next1 > next0; expect = "a smuggler appears and the next check moves 5 days on"; }
                    else if (spot0.state == SmugglerSpot.Wait) { fOk = smugglers1 == smugglers0 && next1 == next0; expect = "nothing changes"; }
                    else if (spot0.state == SmugglerSpot.Here) { fOk = smugglers1 >= 1; expect = "the smuggler is still listed"; }
                    else if (spot0.state == SmugglerSpot.Never) { fOk = smugglers1 == 0; expect = "no smuggler"; }
                    else { fOk = true; expect = "(unknown: not checked)"; }
                    c.details.Add("forecast after entering the galaxy view (state " + stateNow() + "): smugglers there " + smugglers1 + (newName != null ? " (" + newName + ")" : "") + ", next check in " + ((next1 - gd.elapsedMM) / 60000.0).ToString("0.0") + " h; expected: " + expect + (fOk ? " ok" : " WRONG"));
                    ok &= fOk;
                }
            }
            if (!snap.caravans.Exists(x => x.present))
            {
                c.details.Add("none in the scene yet (state " + (Script.FreeWorldAirShip.Galaxy.GalaxyField.GalaxyFieldScene.Instance?.CurState.ToString() ?? "?") + "); opening the fleet's galaxy");
                try { AirShipUIUtils.ShowGalaxyFleet(); } catch (Exception e) { c.details.Add("ShowGalaxyFleet failed: " + e.Message); }
                float tw = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - tw < 60)
                {
                    yield return new WaitForSecondsRealtime(3);
                    yield return Refresh(); snap = host.Latest;
                    if (snap.caravans.Exists(x => x.present)) break;
                }
                yield return Shot("galaxy-scene");
                c.details.Add("after opening the galaxy: " + string.Join(" | ", snap.caravans.ConvertAll(x => desc(x))));
            }
            // How the trade-type fleets sit in the scene (diagnostic).
            Locked(() =>
            {
                foreach (var f in gd.factionFleetRepository)
                {
                    if (f == null || f.resultSettled) continue;
                    int t = f.runtimeFleetTypeOverride >= 0 && f.runtimeFleetTypeOverride < 11 ? f.runtimeFleetTypeOverride : (ImplDef<G_ForceFleetType_Def>.getDataID(f.gForceFleetTypeResId)?.ActivityType ?? -1);
                    if (t != (int)GalaxyNPCFleetType.Trade && t != (int)GalaxyNPCFleetType.Smuggler) continue;
                    GalaxyNPCFleetType rt; string why;
                    bool resolved = FleetPlayerAntiDisturbService.TryResolveType(f.fleet ?? f.materializedState, out rt, out why);
                    c.details.Add("fleet " + f.Name + " uid " + f.uid + " galaxy " + f.galaxyId + ": scene object " + (f.fleet == null ? "none" : f.fleet.GetType().Name) + ", kept " + (f.materializedState == null ? "none" : f.materializedState.GetType().Name)
                        + ", caravan " + (((f.fleet ?? f.materializedState) as FWCaravanFleet)?.getCaravan() != null) + ", presence " + f.presenceMode + ", docked " + f.dockedSpaceportUid + ", journey " + (f.journey != null)
                        + ", target " + f.activityTargetKey + ", type " + (resolved ? rt.ToString() : "? " + why) + ", appear in " + ((f.activityAppearTime - gd.elapsedMM) / 60000) + " h");
                }
            });
            if (!snap.caravans.Exists(x => x.present)) { c.passed = false; c.summary = "no caravan came into the scene"; yield break; }
            {
                // Track one: the marker must find its scene object, and after the camera move it must sit near the
                // screen centre.
                var tc = snap.caravans.Find(x => x.present);
                host.SetVisible(false);
                Ui.Tracker.Track(tc, true);
                yield return new WaitForSecondsRealtime(4f);
                var center = new Vector2(Screen.width / 2f, Screen.height / 2f);
                float dist = (Ui.Tracker.ScreenPos - center).magnitude / Mathf.Max(1, Screen.height);
                yield return Shot("tracking");
                var tcam = Ui.Tracker.LastCamera;
                string camInfo = tcam == null ? "no camera" : "camera " + tcam.name + " enabled " + tcam.isActiveAndEnabled + " at " + tcam.transform.position + " fwd " + tcam.transform.forward + " ortho " + tcam.orthographic + "; fleet at " + Ui.Tracker.FleetWorld + " -> screen " + tcam.WorldToScreenPoint(Ui.Tracker.FleetWorld) + " of " + Screen.width + "x" + Screen.height
                    + "; scene state " + (Script.FreeWorldAirShip.Galaxy.GalaxyField.GalaxyFieldScene.Instance?.CurState.ToString() ?? "?");
                c.details.Add("tracking camera: " + camInfo);
                // The marker must be where the game's projection puts the fleet, or on the edge towards it.
                var pr = Ui.Tracker.Projected; var sp2 = Ui.Tracker.ScreenPos;
                bool where;
                if (Ui.Tracker.OnScreen) where = Mathf.Abs(sp2.x - pr.x) < 1f && Mathf.Abs(sp2.y - pr.y) < 1f;
                else
                {
                    var cdir = pr.z > 0 ? new Vector2(pr.x, pr.y) - center : Ui.Tracker.PlaneDirection(tcam, Ui.Tracker.FleetWorld); var mdir = sp2 - center;
                    bool onEdge = sp2.x <= 49 || sp2.x >= Screen.width - 49 || sp2.y <= 49 || sp2.y >= Screen.height - 49;
                    where = onEdge && Vector2.Dot(cdir.normalized, mdir.normalized) > 0.999f;
                }
                bool trackOk = Ui.Tracker.Shown && Ui.Tracker.InGalaxyView && where;
                c.details.Add("tracking " + tc.FullName + ": galaxy view " + Ui.Tracker.InGalaxyView + ", found " + Ui.Tracker.Found + ", on screen " + Ui.Tracker.OnScreen + ", marker " + Ui.Tracker.ScreenPos + " for projection " + Ui.Tracker.Projected + " (" + (Ui.Tracker.OnScreen ? "on screen" : "edge, towards it") + "); " + Ui.Tracker.Status + (trackOk ? " ok" : " WRONG"));
                ok &= trackOk;
                Ui.Tracker.Stop();
            }

            {
                // The marker must follow a moving fleet. Run the game clock, take the scene fleet that moved the most
                // (a caravan when one moves), track it and compare every half second: the marker against the game's
                // projection of the fleet (or the screen edge towards it), and against its drawn ship models.
                var follow = new Check { name = "tracking follows a moving fleet" };
                result.checks.Add(follow);
                var mgr = Script.FreeWorldAirShip.Galaxy.NPCFleet.GalaxyNPCFleetManager.Instance;
                var start = new Dictionary<long, Vector3>();
                foreach (var f in UnityEngine.Object.FindObjectsOfType<Script.FreeWorldAirShip.Galaxy.NPCFleet.GalaxyNPCFleet>())
                    if (f != null && mgr != null && mgr.GetFleetByUid(f.FleetUId) == f) start[f.FleetUId] = f.transform.position;
                float speed0 = MapManager.getLogicSpeed(); bool play0 = MapManager.isPlay;
                MapManager.setLogicSpeed(5f);
                yield return new WaitForSecondsRealtime(3);
                long mover = -1; float best = 0; bool moverIsCaravan = false;
                var cuids = new HashSet<long>(snap.caravans.ConvertAll(x => x.uid));
                foreach (var kv in start)
                {
                    var f = mgr != null ? mgr.GetFleetByUid(kv.Key) : null;
                    if (f == null) continue;
                    float d = (f.transform.position - kv.Value).magnitude;
                    bool car = cuids.Contains(kv.Key) && d > 0.01f;
                    if ((car && !moverIsCaravan) || (car == moverIsCaravan && d > best)) { best = d; mover = kv.Key; moverIsCaravan = car; }
                }
                follow.details.Add(start.Count + " fleets in the game's table; clock x5 (now " + MapManager.getNowSpeed() + "); most moved " + mover + " by " + best.ToString("0.###") + (moverIsCaravan ? " (a caravan)" : ""));
                if (mover < 0 || best < 0.01f) { follow.passed = false; follow.summary = "no fleet moved"; }
                else
                {
                    var ti = snap.caravans.Find(x => x.uid == mover) ?? new CaravanInfo { uid = mover, name = "test fleet " + mover, galaxyId = snap.fleetGalaxyId, galaxyName = "" };
                    Ui.Tracker.Track(ti, true);
                    yield return new WaitForSecondsRealtime(1);
                    bool allOk = true; int moved = 0; Vector2 lastMarker = Ui.Tracker.ScreenPos; float worstModel = 0;
                    var center = new Vector2(Screen.width / 2f, Screen.height / 2f);
                    for (int k = 0; k < 10; k++)
                    {
                        yield return new WaitForSecondsRealtime(0.5f);
                        var cam = Ui.Tracker.LastCamera; var f = mgr.GetFleetByUid(mover);
                        if (cam == null || f == null || !Ui.Tracker.Shown)
                        {
                            // The game may leave the galaxy view by itself while the clock runs (the player's fleet
                            // arriving somewhere). The marker must then be hidden; the samples so far still count.
                            string stNow = Script.FreeWorldAirShip.Galaxy.GalaxyField.GalaxyFieldScene.Instance?.CurState.ToString() ?? "?";
                            bool leftView = !Ui.Tracker.InGalaxyView;
                            if (!leftView) allOk = false;
                            follow.details.Add("sample " + k + ": " + (leftView ? "the game left the galaxy view (state " + stNow + "); marker hidden " + !Ui.Tracker.Shown : "fleet or camera gone WRONG") + "; " + Ui.Tracker.Status);
                            break;
                        }
                        Vector3 pr = cam.WorldToScreenPoint(f.transform.position);
                        var mk = Ui.Tracker.ScreenPos;
                        bool where;
                        if (Ui.Tracker.OnScreen) where = Mathf.Abs(mk.x - pr.x) < 2f && Mathf.Abs(mk.y - pr.y) < 2f;
                        else where = Vector2.Dot((pr.z > 0 ? new Vector2(pr.x, pr.y) - center : Ui.Tracker.PlaneDirection(cam, f.transform.position)).normalized, (mk - center).normalized) > 0.999f;
                        // The ship models' screen position (what the player sees).
                        var rs = f.GetComponentsInChildren<Renderer>(false); Vector3 sum = Vector3.zero; int rn = 0;
                        foreach (var r in rs) if (r.enabled && r.gameObject.activeInHierarchy) { sum += r.bounds.center; rn++; }
                        float modelGap = -1;
                        if (rn > 0 && Ui.Tracker.OnScreen) { var mp = cam.WorldToScreenPoint(sum / rn); modelGap = new Vector2(mp.x - mk.x, mp.y - mk.y).magnitude; worstModel = Mathf.Max(worstModel, modelGap); }
                        string lw; bool inside = Ui.Tracker.LabelInside(out lw);
                        if (!inside) { allOk = false; follow.details.Add("sample " + k + ": name label outside the screen: " + lw + " WRONG"); }
                        else if (k == 0) follow.details.Add("name label " + lw + " ok");
                        if ((mk - lastMarker).magnitude > 0.5f) moved++;
                        lastMarker = mk;
                        allOk &= where;
                        follow.details.Add("sample " + k + ": fleet " + f.transform.position.ToString("F3") + " -> screen " + pr.ToString("F0") + ", marker " + mk.ToString("F0") + (Ui.Tracker.OnScreen ? " on screen" : " on the edge") + ", models " + rn + (modelGap >= 0 ? " " + modelGap.ToString("0") + " px from the marker" : "") + ", objects with this uid " + Ui.Tracker.SceneObjects() + (where ? "" : " WRONG"));
                    }
                    yield return Shot("tracking-moving");
                    bool fOk = allOk && moved >= 3;
                    follow.passed = fOk;
                    follow.summary = (fOk ? "marker followed " : "marker did NOT follow ") + ti.FullName + ": moved in " + moved + " of 10 samples, worst gap to the ship models " + worstModel.ToString("0") + " px";
                    Ui.Tracker.Stop();
                }
                MapManager.setLogicSpeed(speed0);
                if (!play0) MapManager.pauseLogic();
            }

            // Their source markets up to date (the mod prices normal goods only then).
            var need = new List<long>();
            Locked(() =>
            {
                foreach (var x in snap.caravans)
                {
                    if (!x.present) continue;
                    var car = FindCaravanFleet(gd, x.uid)?.getCaravan();
                    var m = car == null ? null : snap.markets.Find(mm => mm.uid == car.sourceExchangeSpaceUid);
                    if (m != null && !m.Exact && !need.Contains(m.uid)) need.Add(m.uid);
                }
            });
            foreach (var u in need) host.settlement.Enqueue(u);
            float t0 = Time.realtimeSinceStartup;
            while (host.settlement.Pending > 0 && Time.realtimeSinceStartup - t0 < 300) yield return new WaitForSecondsRealtime(1);
            yield return Refresh(); snap = host.Latest;
            c.details.Add("caught up " + need.Count + " source market(s)");

            // Buy one crew goods (DNA / creature) from a caravan that sells one, through the crew cart.
            {
                var cc0 = snap.caravans.Find(x => x.present && !x.smuggler && x.lines.Exists(l => l.sells && IsCrewKey(l.key) && !double.IsNaN(l.unit) && l.unit < gd.creditPoint / 2));
                if (cc0 == null) c.details.Add("no caravan sells crew goods here: crew checkout not tested");
                else
                {
                    var cl = cc0.lines.Find(l => l.sells && IsCrewKey(l.key) && !double.IsNaN(l.unit) && l.unit < gd.creditPoint / 2);
                    PageTradeInventory page = null; CaravanTransferHandler h = null; bool put = false;
                    Locked(() =>
                    {
                        var cf = FindCaravanFleet(gd, cc0.uid);
                        var fc = new FleetInventoryContext(AirShipLocalData.Instance.getCurFleet(), false);
                        var cc = new CaravanInventoryContext(cf);
                        h = new CaravanTransferHandler(cc, fc);
                        page = UIHelp.showPageTradeInventoryFleet();
                        page.InitData(cc, fc, h);
                        foreach (var cr in cc.GetCrew())
                            if (Caravans.ItemKey(CrewDisplayInstance.Unwrap(cr) as FWBaseBean) == cl.key) { put = h.HandleCrewTransfer(cr, PanelRole.ShopSystem, h.CurrentPlayerRole); break; }
                    });
                    yield return new WaitForSecondsRealtime(1.5f);
                    yield return Shot("crew-trade-page");
                    int n0 = Ledger.entries.Count; long cp0 = gd.creditPoint;
                    Locked(() => DoCheckout.Invoke(page, null));
                    yield return null; yield return null;
                    var e = Ledger.entries.Count > n0 ? Ledger.entries[Ledger.entries.Count - 1] : null;
                    long d = gd.creditPoint - cp0, expect = -FWTradePricePressure.RoundPayment(cl.unit, true);
                    bool crewOk = put && h.BuyCrewCart.Count == 0 && d == expect && e != null && e.exact && e.creditDelta == d && e.items.Exists(x => x.buy && x.key == cl.key && x.name == cl.name);
                    c.details.Add("crew checkout at " + cc0.name + ": buy " + cl.name + " (" + cl.key + "), in cart " + put + ", credits " + d + ", mod predicted " + expect
                                  + ", ledger " + (e == null ? "none" : e.creditDelta + " exact=" + e.exact + " " + string.Join("; ", e.items.ConvertAll(x => x.name + "[" + x.key + "] x" + x.qty + " " + x.net.ToString("0.0")))) + (crewOk ? " ok" : " WRONG"));
                    ok &= crewOk;
                    Locked(() => page.RequestClose());
                    yield return Refresh(); snap = host.Latest;
                    var after = snap.caravans.Find(x => x.uid == cc0.uid);
                    var l2 = after?.lines.Find(l => l.sells && l.key == cl.key);
                    c.details.Add("its stock of " + cl.name + " " + cl.qty + " -> " + (l2 == null ? 0 : l2.qty));
                    ok &= (l2 == null ? 0 : l2.qty) == cl.qty - 1;
                }
            }

            foreach (var nat in snap.caravans.FindAll(x => x.black))
            {
                bool r = false; string s = null;
                Locked(() => r = CheckSmuggler(gd, nat, out s));
                c.details.Add("smuggler the game spawned: " + desc(nat) + "; " + s + (r ? " ok" : " WRONG"));
                ok &= r;
                host.SetVisible(true); host.ShowTab(4);
                yield return new WaitForSecondsRealtime(0.5f);
                Ui.CaravansView.Current?.Select(nat.uid);
                yield return new WaitForSecondsRealtime(1f);
                yield return Shot("smuggler-spawned");
                host.SetVisible(false);
            }
            var ci = snap.caravans.Find(x => x.present && !x.smuggler && x.priced && !x.hostile && x.lines.Exists(l => l.sells && !double.IsNaN(l.unit))) ?? snap.caravans.Find(x => x.present && !x.smuggler && x.priced);
            if (ci == null) c.details.Add("no ordinary caravan in the scene now: trade-page price and checkout checks not run this time");
            if (ci != null)
            {
            c.details.Add("picked: " + desc(ci));

            // Prices against the game's page. The game's getFleetRelation creates the relation record on first use; the
            // second pass is after that (what the player sees once they have met the caravan).
            for (int pass = 1; pass <= 2; pass++)
            {
                int cmp = 0, dif = 0; string s = null;
                Locked(() => s = ComparePrices(gd, ci, out cmp, out dif));
                c.details.Add("prices pass " + pass + ": " + cmp + " compared, " + dif + " differ: " + s);
                if (pass == 2) ok &= cmp > 0 && dif == 0;
                yield return Refresh(); snap = host.Latest;
                ci = snap.caravans.Find(x => x.uid == ci.uid) ?? ci;
            }

            // A dialogue that changes the caravan's prices (the game's own effect, ET_CSAPI.player_addpricemodifier):
            // the mod must follow and still equal the game's page.
            {
                int pm0 = ci.priceModifier; bool okApi = false;
                long npc = -1;
                // The dialogue passes the NPC's uid (FWCaravan.NpcUid), not the fleet's.
                Locked(() => { npc = FindCaravanFleet(gd, ci.uid).getCaravan().NpcUid; okApi = ET_CSAPI.player_addpricemodifier(npc.ToString(), -40); });
                yield return Refresh(); snap = host.Latest;
                ci = snap.caravans.Find(x => x.uid == ci.uid) ?? ci;
                int cmp = 0, dif = 0; string s = null;
                Locked(() => s = ComparePrices(gd, ci, out cmp, out dif));
                bool pmOk = okApi && ci.priceModifier == pm0 - 40 && cmp > 0 && dif == 0;
                c.details.Add("dialogue price change -40 (npc " + npc + "): game api " + okApi + ", mod price modifier " + pm0 + " -> " + ci.priceModifier + " (buy x" + ci.FactorBuy.ToString("0.###") + ", sell x" + ci.FactorSell.ToString("0.###") + "); prices " + cmp + " compared, " + dif + " differ: " + s + (pmOk ? " ok" : " WRONG"));
                ok &= pmOk;
            }

            // UI
            host.SetVisible(true);
            host.ShowTab(4);
            yield return new WaitForSecondsRealtime(1f);
            Ui.CaravansView.Current?.Select(ci.uid);
            yield return new WaitForSecondsRealtime(1f);
            yield return Shot("caravans");
            {
                // Hover a cut-off cell: the tip box must show its whole text.
                Ui.TipOnHover pick = null;
                foreach (var t in host.CanvasGo.GetComponentsInChildren<Ui.TipOnHover>(false))
                    if (string.IsNullOrEmpty(t.explain) && Ui.TipOnHover.CutShort(t.text)) { pick = t; break; }
                if (pick == null) c.details.Add("tooltip: no cut-off text on this page");
                else
                {
                    var cr = pick.text.rectTransform; var corners = new Vector3[4]; cr.GetWorldCorners(corners);
                    var ev = new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current) { position = (corners[0] + corners[2]) / 2 };
                    pick.OnPointerEnter(ev);
                    yield return new WaitForSecondsRealtime(0.5f);
                    yield return Shot("tooltip");
                    bool tipOk = Ui.Tooltip.Visible && Ui.Tooltip.Text == pick.text.text;
                    c.details.Add("tooltip over \"" + pick.text.text + "\": shown " + Ui.Tooltip.Visible + (tipOk ? " ok" : " WRONG"));
                    ok &= tipOk;
                    pick.OnPointerExit(ev);
                }
            }
            {
                // A column header's explanation: what the mouse hits over the "那个站" header (through the sort
                // button) must lead to the header's tip, and the tip must show the explanation.
                Ui.TipOnHover head = null;
                foreach (var t in host.CanvasGo.GetComponentsInChildren<Ui.TipOnHover>(false))
                    if (!string.IsNullOrEmpty(t.explain) && t.text != null && t.text.text.StartsWith("那个站")) { head = t; break; }
                if (head == null) { c.details.Add("header explanation: no tip on the 那个站 header WRONG"); ok = false; }
                else
                {
                    var hr = (RectTransform)head.transform; var hc = new Vector3[4]; hr.GetWorldCorners(hc);
                    var ev = new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current) { position = (hc[0] + hc[2]) / 2 };
                    var hits = new List<UnityEngine.EventSystems.RaycastResult>();
                    UnityEngine.EventSystems.EventSystem.current.RaycastAll(ev, hits);
                    bool reaches = false;
                    if (hits.Count > 0) for (var x = hits[0].gameObject.transform; x != null; x = x.parent) if (x == head.transform) { reaches = true; break; }
                    head.OnPointerEnter(ev);
                    // Checked in the same frame: a real mouse over the test window would move the hover elsewhere.
                    bool hOk = reaches && Ui.Tooltip.Visible && Ui.Tooltip.Text != null && Ui.Tooltip.Text.Contains("最高价是哪个空间站");
                    yield return Shot("header-tip");
                    c.details.Add("header explanation over 那个站: mouse reaches it " + reaches + " (hit " + (hits.Count > 0 ? hits[0].gameObject.name : "nothing") + "), shown " + Ui.Tooltip.Visible + (hOk ? " ok" : " WRONG"));
                    ok &= hOk;
                    head.OnPointerExit(ev);
                }
                int withTips = 0, headers = 0;
                foreach (var t in host.CanvasGo.GetComponentsInChildren<Ui.TipOnHover>(true)) if (!string.IsNullOrEmpty(t.explain)) withTips++;
                foreach (var h in host.CanvasGo.GetComponentsInChildren<Transform>(true)) if (h.name == "H") headers++;
                c.details.Add("column headers " + headers + ", with explanations " + withTips);
                host.Win.ToggleTerms();
                yield return new WaitForSecondsRealtime(0.5f);
                yield return Shot("terms");
                bool termsOk = host.Win.TermsShown;
                c.details.Add("terms popup shown " + termsOk + (termsOk ? " ok" : " WRONG"));
                ok &= termsOk;
                host.Win.ToggleTerms();
            }
            {
                // Plans page: no plan picked -> no detail box, table takes the room.
                host.ShowTab(1);
                yield return new WaitForSecondsRealtime(0.5f);
                var pv = Ui.PlansView.Current;
                if (pv != null)
                {
                    pv.Select(null);
                    yield return new WaitForSecondsRealtime(0.3f);
                    float h0 = pv.TableForTest.Height;
                    var any = Plans.list.Count > 0 ? Plans.list[0] : null;
                    Plan temp = null;
                    if (any == null && snap.trades.Count > 0) { Locked(() => temp = Plans.FromTrade(snap.trades[0], snap, host.Cfg, host.settlement)); any = temp; }
                    float h1 = h0;
                    if (any != null) { pv.Select(any); yield return new WaitForSecondsRealtime(0.3f); h1 = pv.TableForTest.Height; yield return Shot("plans-detail"); pv.Select(null); yield return new WaitForSecondsRealtime(0.3f); }
                    float h2 = pv.TableForTest.Height;
                    if (temp != null) Locked(() => Plans.Remove(temp, host.Cfg));
                    bool planUi = any != null && h0 > h1 && h2 == h0;
                    c.details.Add("plans page table height: none picked " + h0 + ", one picked " + h1 + ", closed again " + h2 + (planUi ? " ok" : " WRONG"));
                    ok &= planUi;
                }
            }
            host.SetVisible(false);

            // A real checkout: buy one unit, and sell one unit of something it takes from the hold.
            var b1 = ci.lines.Find(l => l.sells && !IsCrewKey(l.key) && !double.IsNaN(l.unit) && l.qty >= 1 && l.unit < gd.creditPoint / 2);
            var s1 = ci.lines.Find(l => !l.sells && !double.IsNaN(l.unit) && l.qty >= 1 && l.unit <= ci.cash);
            if (b1 == null && s1 == null) { c.details.Add("nothing to buy or sell with this caravan"); ok = false; }
            else
            {
                bool r = false;
                yield return CaravanCheckout(c, ci, b1, s1, v => r = v);
                ok &= r;
                yield return Refresh(); snap = host.Latest;
                var after = snap.caravans.Find(x => x.uid == ci.uid);
                if (b1 != null && after != null)
                {
                    var l2 = after.lines.Find(l => l.sells && l.key == b1.key);
                    bool stockOk = (l2 == null ? 0 : l2.qty) == b1.qty - 1;
                    c.details.Add("caravan stock of " + b1.name + " " + b1.qty + " -> " + (l2 == null ? 0 : l2.qty) + (stockOk ? " ok" : " WRONG"));
                    ok &= stockOk;
                }
            }

            }

            // A smuggler: the one the game spawned if there is one, else made by the game on the picked fleet
            // (FWExchangeSpace.createBlackMarketCaravan, as a smuggler
            // spawn does): the mod must show the smuggler dialogue's deals, count the hold the way the dialogue does, and
            // notice a deal already made (the dialogue's ET record).
            FWCaravan original = null, black = null;
            var cfl = (FWCaravanFleet)null;
            var natural = snap.caravans.Find(x => x.present && x.smuggler);
            var host0 = natural ?? ci;
            Locked(() =>
            {
                if (host0 == null) return;
                cfl = FindCaravanFleet(gd, host0.uid);
                original = cfl?.getCaravan();
                if (original == null) return;
                if (natural != null) { black = original; return; }
                var ex = original.getSourceExchangeSpace();
                if (ex != null)
                {
                    black = ex.createBlackMarketCaravan("0", gd.elapsedMM, null, gd);
                    black.NpcUid = original.NpcUid;
                    cfl.caravan = black;
                }
            });
            if (host0 != null) ci = host0;
            if (black == null) { c.details.Add("could not make a black-market caravan"); ok = false; }
            else
            {
                // Put a few listed goods in the fleet's hold with the dialogue's own item call (Player.AddItem, to the fleet):
                // a stackable goods and weapons (one stack per piece), so the count is checked across stacks.
                Locked(() => { ET_CSAPI.player_additem(3, 12, "9", 1); ET_CSAPI.player_additem(2, 551, "17", 1); ET_CSAPI.player_additem(1, 551, "17", 1); });
                yield return Refresh(); snap = host.Latest;
                var si = snap.caravans.Find(x => x.uid == ci.uid);
                bool r = false; string s = null;
                Locked(() => r = si != null && CheckSmuggler(gd, si, out s) && si.smugglerSaleKinds >= 2);
                c.details.Add((natural != null ? "smuggler the game spawned, " : "smuggler made on ") + ci.name + ": " + (si == null ? "not listed" : desc(si) + "; " + s) + (r ? " ok" : " WRONG"));
                ok &= r;
                // The hold sale's own steps (IllegalFleet-SellContraband), with the game functions its Lua calls:
                // before = GetItemCount(t, id, 1); AddItem(-before, t, id, 1); after = GetItemCount(...);
                // removed = min(before, before - after) is what gets paid. Report what leaves the hold and what is paid.
                foreach (var key in new[] { "12_9", "551_17" })
                {
                    var p = key.Split('_');
                    int ty = int.Parse(p[0]); string id = p[1];
                    int heldBefore = 0, heldAfter = 0, before = 0, after = 0;
                    Locked(() =>
                    {
                        foreach (var t in gd.galaxyBlock.playerFleet.getThings()) if (t != null && t.type == ty && t.id == id) heldBefore += t.getSum();
                        before = ET_CSAPI.global_player_getitemcount(ty, id, 1);
                        ET_CSAPI.player_additem(-before, ty, id, 1);
                        after = ET_CSAPI.global_player_getitemcount(ty, id, 1);
                        foreach (var t in gd.galaxyBlock.playerFleet.getThings()) if (t != null && t.type == ty && t.id == id) heldAfter += t.getSum();
                    });
                    int removed = Math.Max(0, Math.Min(before, before - after));
                    var ml = si?.lines.Find(l => !l.sells && l.key == key);
                    double basePrice = Caravans.Deals().buys.Find(b => b.Key == key).Value * Caravans.Deals().rate;
                    bool stepOk = ml != null && ml.qty == heldBefore - heldAfter && Math.Abs(ml.total - Math.Floor(removed * basePrice)) < 0.01;
                    c.details.Add("hold sale steps for " + key + ": hold " + heldBefore + " -> " + heldAfter + " (taken " + (heldBefore - heldAfter) + "), counted before " + before + " after " + after + ", paid for " + removed
                                  + "; mod predicted take " + (ml == null ? 0 : ml.qty) + " pay " + (ml == null ? 0 : ml.total) + " note: " + (ml?.note ?? "") + (stepOk ? " ok" : " WRONG"));
                    ok &= stepOk;
                }
                // Mark the hold sale as done for this NPC, as the dialogue does (Utils.CreateTempVar).
                var d = Caravans.Deals();
                if (si != null && d.buyOnce != null)
                {
                    Locked(() => ET_CSAPI.utils_createtempvar(d.buyOnce + black.NpcUid, 1L));
                    yield return Refresh(); snap = host.Latest;
                    si = snap.caravans.Find(x => x.uid == ci.uid);
                    bool done = si != null && si.smugglerSold && si.lines.FindAll(l => !l.sells).TrueForAll(l => l.note.Contains("已经收过"));
                    c.details.Add("after the dialogue's record " + d.buyOnce + black.NpcUid + ": mod says sold=" + (si != null && si.smugglerSold) + (done ? " ok" : " WRONG"));
                    ok &= done;
                    Locked(() => ET_CSAPI.utils_deletetempvar(d.buyOnce + black.NpcUid));
                }
                // The real dialogue, as a player does it, with a smuggler the game spawned: open its dialogue and pick
                // "出售货舱里的可疑货物". The ledger must record the deal: what left the hold, at the fixed price, checked
                // against the credits. First a single stack (the game pays for all of it), then a goods split in two
                // stacks (the game takes the first stack and pays for less), with the dialogue's once-record cleared
                // in between (test copy only).
                if (natural != null && d.buyOnce != null)
                {
                    TK_NPCSetBean npcBean = Script.FreeWorldAirShip.Galaxy.NPCFleet.GalaxyNPCFleetManager.Instance?.GetFleetByUid(natural.uid)?.NpcSetBean;
                    for (int pass = 0; pass < 2; pass++)
                    {
                        Locked(() =>
                        {
                            ET_CSAPI.utils_deletetempvar(d.buyOnce + black.NpcUid);
                            // Empty the hold of everything on the smuggler's list first (left over from the steps above).
                            foreach (var kv in d.buys)
                            {
                                var kp = kv.Key.Split('_'); int ty = int.Parse(kp[0]); string id = kp[1];
                                for (int guard = 0; guard < 20; guard++)
                                {
                                    int n = 0;
                                    foreach (var t in gd.galaxyBlock.playerFleet.getThings()) if (t != null && t.type == ty && t.id == id) n += t.getSum();
                                    if (n == 0) break;
                                    ET_CSAPI.player_additem(-n, ty, id, 1);
                                }
                            }
                            if (pass == 0) ET_CSAPI.player_additem(3, 12, "9", 1);
                            else { ET_CSAPI.player_additem(2, 551, "17", 1); ET_CSAPI.player_additem(1, 551, "17", 1); }
                        });
                        yield return null;
                        int ledger0 = Ledger.entries.Count; long credits0 = gd.creditPoint;
                        string why = null;
                        GalaxyEventsDialog dlg = null;
                        try
                        {
                            Script.FreeWorldAirShip.Galaxy.NPCFleet.GalaxyNPCFleetInteractionShellBootstrap.EnsureForFleet(npcBean as TK_NPCSetBean_Fleet);
                            var opt = new GalaxyEventSelectsTipOptionGalaxyEvent();
                            opt.setData(npcBean);
                            if (opt.eventData?.defbean == null) why = "no dialogue for this smuggler";
                            else { AirShipLocalData.Instance.setNpcAction(AirShipLocalData.NpcActiontype.Chat); dlg = UIHelp.showGalaxyEventsDialog(); dlg.setData(opt.eventData); }
                        }
                        catch (Exception e) { why = "opening the dialogue failed: " + e.Message; }
                        if (dlg == null) { c.details.Add("smuggler dialogue: " + why + " WRONG"); ok = false; break; }
                        // Wait for the options, then click the hold sale.
                        UnityEngine.UI.Button pickBtn = null; string seen = "";
                        float to = Time.realtimeSinceStartup;
                        while (Time.realtimeSinceStartup - to < 15 && pickBtn == null)
                        {
                            yield return new WaitForSecondsRealtime(0.5f);
                            var names = new List<string>();
                            foreach (var item in dlg.GetComponentsInChildren<EventsOptionItem>(false))
                            {
                                var t = item.transform.Find("obj/title")?.GetComponent<TMPro.TextMeshProUGUI>();
                                if (t == null) continue;
                                names.Add(t.text);
                                if (t.text.Contains("出售货舱里的可疑货物")) pickBtn = item.transform.Find("obj").GetComponent<UnityEngine.UI.Button>();
                            }
                            seen = string.Join(" / ", names);
                        }
                        yield return Shot("smuggler-dialogue-" + pass);
                        if (pickBtn == null) { c.details.Add("smuggler dialogue: option not found (options: " + seen + ") WRONG"); ok = false; try { dlg.close(); } catch { } break; }
                        // As the mouse does: over the option (the dialogue focuses it), then a click; the dialogue may
                        // take a first click to focus the option, so click again when nothing happened.
                        var pev = new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current);
                        UnityEngine.EventSystems.ExecuteEvents.Execute(pickBtn.gameObject, pev, UnityEngine.EventSystems.ExecuteEvents.pointerEnterHandler);
                        yield return new WaitForSecondsRealtime(0.3f);
                        int clicks = 0;
                        float tc = Time.realtimeSinceStartup;
                        while (Time.realtimeSinceStartup - tc < 20 && gd.creditPoint == credits0)
                        {
                            if (clicks == 0 || (clicks < 3 && Time.realtimeSinceStartup - tc > 3f * clicks && pickBtn != null && pickBtn.gameObject.activeInHierarchy)) { pickBtn.onClick.Invoke(); clicks++; }
                            yield return new WaitForSecondsRealtime(0.5f);
                        }
                        yield return new WaitForSecondsRealtime(1f);
                        yield return Shot("smuggler-dialogue-done-" + pass);
                        // End the talk as a player does ("结束通讯"), so the next talk starts from the first page.
                        for (int k = 0; k < 6 && GalaxyEventsDialog.instance != null && GalaxyEventsDialog.instance.gameObject.activeInHierarchy; k++)
                        {
                            UnityEngine.UI.Button endBtn = null;
                            foreach (var item in GalaxyEventsDialog.instance.GetComponentsInChildren<EventsOptionItem>(false))
                            {
                                var t = item.transform.Find("obj/title")?.GetComponent<TMPro.TextMeshProUGUI>();
                                if (t != null && t.text.Contains("结束通讯")) endBtn = item.transform.Find("obj").GetComponent<UnityEngine.UI.Button>();
                            }
                            if (endBtn != null)
                            {
                                UnityEngine.EventSystems.ExecuteEvents.Execute(endBtn.gameObject, new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current), UnityEngine.EventSystems.ExecuteEvents.pointerEnterHandler);
                                yield return new WaitForSecondsRealtime(0.3f);
                                endBtn.onClick.Invoke();
                            }
                            yield return new WaitForSecondsRealtime(1.5f);
                        }
                        try { if (GalaxyEventsDialog.instance != null && GalaxyEventsDialog.instance.gameObject.activeInHierarchy) GalaxyEventsDialog.instance.close(); } catch { }
                        yield return new WaitForSecondsRealtime(0.5f);
                        long got = gd.creditPoint - credits0;
                        var le = Ledger.entries.Count > ledger0 ? Ledger.entries[Ledger.entries.Count - 1] : null;
                        string items = le == null ? "" : string.Join(", ", le.items.ConvertAll(x => (x.buy ? "bought " : "sold ") + x.key + " x" + x.qty + " " + x.net.ToString("0")));
                        bool lOk = got > 0 && le != null && le.market != null && le.market.StartsWith("走私船") && le.creditDelta == got
                                   && (pass == 0 ? le.exact && le.items.Count == 1 && le.items[0].key == "12_9" && le.items[0].qty == 3
                                                 : !le.exact && le.items.Exists(x => x.key == "551_17" && x.qty == 2) && le.note.Contains("少付"));
                        c.details.Add("smuggler dialogue pass " + (pass + 1) + " (" + clicks + " click(s); options: " + seen + "): credits " + got + "; ledger " + (le == null ? "nothing" : le.market + " " + le.creditDelta + " exact=" + le.exact + " [" + items + "] " + le.note) + (lOk ? " ok" : " WRONG"));
                        ok &= lOk;
                    }
                    Locked(() => ET_CSAPI.utils_deletetempvar(d.buyOnce + black.NpcUid));
                }
                host.SetVisible(true); host.ShowTab(4);
                yield return new WaitForSecondsRealtime(0.5f);
                Ui.CaravansView.Current?.Select(ci.uid);
                yield return new WaitForSecondsRealtime(1f);
                yield return Shot("smuggler");
                host.SetVisible(false);
                Locked(() => cfl.caravan = original);
                yield return Refresh();
            }
            {
                // Leaving the galaxy view for the star field: the game keeps the fleets' objects there, hidden and still
                // (GalaxyNPCFleetManager.RetainForReuse). The marker must not stand on such an object.
                var tp = snap.caravans.Find(x => x.present);
                if (tp != null)
                {
                    Ui.Tracker.Track(tp, false);
                    yield return new WaitForSecondsRealtime(1);
                    if (!Ui.Tracker.InGalaxyView)
                    {
                        c.details.Add("not in the galaxy view now (state " + (Script.FreeWorldAirShip.Galaxy.GalaxyField.GalaxyFieldScene.Instance?.CurState.ToString() ?? "?") + "): entering it with the tracker's locate");
                        Ui.Tracker.Focus();
                        float te = Time.realtimeSinceStartup;
                        while (Time.realtimeSinceStartup - te < 60 && !(Ui.Tracker.InGalaxyView && Ui.Tracker.Shown)) yield return new WaitForSecondsRealtime(1);
                    }
                    bool shownInScene = Ui.Tracker.Shown;
                    try { MainUI_Head.GoGalaxyField(null); } catch (Exception e) { c.details.Add("GoGalaxyField failed: " + e.Message); }
                    float tf = Time.realtimeSinceStartup;
                    while (Time.realtimeSinceStartup - tf < 40 && Script.FreeWorldAirShip.Galaxy.GalaxyField.GalaxyFieldScene.Instance?.CurState != Script.FreeWorldAirShip.Galaxy.GalaxyField.State.GalaxyField) yield return new WaitForSecondsRealtime(1);
                    yield return new WaitForSecondsRealtime(3);
                    // The map turns the fleet's dot on a moment after the star field opens.
                    float td = Time.realtimeSinceStartup;
                    while (Time.realtimeSinceStartup - td < 15 && !Ui.Tracker.Shown) yield return new WaitForSecondsRealtime(0.5f);
                    var mg = Script.FreeWorldAirShip.Galaxy.NPCFleet.GalaxyNPCFleetManager.Instance;
                    var kept = mg != null ? mg.GetFleetByUid(tp.uid) : null;
                    bool keptHidden = kept != null && !Ui.Tracker.ShipsShown(kept);
                    yield return Shot("star-field-after-scene");
                    // In the star field the marker goes on the game's map dot for the fleet; the dot must sit where the
                    // fleet's object projects (two independent sources agreeing), and the marker where the dot projects
                    // (or on the edge towards it).
                    var fcam = Ui.Tracker.LastCamera;
                    bool dotOk = false; string dotInfo = "no camera";
                    if (fcam != null && kept != null)
                    {
                        Vector3 dp = fcam.WorldToScreenPoint(Ui.Tracker.FleetWorld), op = fcam.WorldToScreenPoint(kept.transform.position);
                        var mk = Ui.Tracker.ScreenPos; var cen = new Vector2(Screen.width / 2f, Screen.height / 2f);
                        bool dotOnFleet = new Vector2(dp.x - op.x, dp.y - op.y).magnitude < 4f;
                        bool markOk = Ui.Tracker.OnScreen ? new Vector2(mk.x - dp.x, mk.y - dp.y).magnitude < 2f : Vector2.Dot((new Vector2(dp.x, dp.y) - cen).normalized, (mk - cen).normalized) > 0.999f;
                        string lw2; bool lin = !Ui.Tracker.OnScreen || Ui.Tracker.LabelInside(out lw2);
                        dotOk = (Ui.Tracker.UsingMapDot || !Ui.Tracker.OnScreen) && Ui.Tracker.Shown && dotOnFleet && markOk && lin;
                        dotInfo = Ui.Tracker.MapEntryState() + "; map dot at " + dp.ToString("F0") + ", fleet object at " + op.ToString("F0") + ", marker " + mk.ToString("F0") + (Ui.Tracker.OnScreen ? " on screen" : " on the edge");
                    }
                    // When the game draws no dot for the fleet (its map entry off: e.g. under the game's station panel),
                    // the marker must be off too, with the reason at the top.
                    bool noDot = Ui.Tracker.MapEntryState().Contains("entry active False") || Ui.Tracker.MapEntryState().Contains("dot active False");
                    if (!dotOk && noDot && !Ui.Tracker.Shown && Ui.Tracker.Status.Contains("没有画它")) { dotOk = true; dotInfo += "; the game draws no dot for it, and the marker is off"; }
                    bool hiddenOk = shownInScene && dotOk;
                    c.details.Add("back to the star field: marker shown in the galaxy view " + shownInScene + ", on the map dot " + Ui.Tracker.UsingMapDot + " (" + dotInfo + "); the game still holds its object " + (kept != null) + (kept != null ? " with ships hidden " + keptHidden : "") + "; " + Ui.Tracker.Status + (hiddenOk ? " ok" : " WRONG"));
                    ok &= hiddenOk;
                    Ui.Tracker.Stop();
                    // Probe (diagnostic): the star field shows dots in this galaxy after the galaxy view was open. Where
                    // the game's kept fleet objects project now and after the clock runs, to compare with the dots in the
                    // two screenshots.
                    Func<string> probe = () =>
                    {
                        var parts = new List<string>();
                        var cam0 = Script.FreeWorldAirShip.Galaxy.GalaxyField.GalaxyFieldScene.Instance?.MainCamera;
                        foreach (var f in UnityEngine.Object.FindObjectsOfType<Script.FreeWorldAirShip.Galaxy.NPCFleet.GalaxyNPCFleet>())
                        {
                            if (f == null) continue;
                            var sp = cam0 != null ? cam0.WorldToScreenPoint(f.transform.position) : Vector3.zero;
                            var act = new List<string>();
                            foreach (Transform ch in f.transform) if (ch.gameObject.activeInHierarchy) act.Add(ch.name);
                            var gr = f.GetComponentsInChildren<UnityEngine.UI.Graphic>(false).Length;
                            var rr = new List<string>();
                            foreach (var r in f.GetComponentsInChildren<Renderer>(false)) if (r.enabled) rr.Add(r.GetType().Name + ":" + r.name);
                            parts.Add(f.FleetUId + " at " + f.transform.position.ToString("F2") + " screen " + sp.ToString("F0") + " active children [" + string.Join(",", act) + "] renderers [" + string.Join(",", rr.GetRange(0, Math.Min(4, rr.Count))) + (rr.Count > 4 ? ",+" + (rr.Count - 4) : "") + "] ui " + gr);
                        }
                        return string.Join(" | ", parts);
                    };
                    c.details.Add("probe star field t0: " + probe());
                    {
                        // Which fleets the galaxy map has entries for (diagnostic).
                        var mvs = UnityEngine.Object.FindObjectsOfType<Script.FreeWorldAirShip.Galaxy.GalaxyField.GalaxyMapView>();
                        Transform gi = null;
                        var names = new List<string>();
                        foreach (var mv in mvs)
                        {
                            var g2 = mv != null ? mv.transform.Find("DetailVisualRoot/DetailUIRoot/GalaxyInfo") : null;
                            if (g2 == null) continue;
                            var nn = new List<string>();
                            foreach (Transform ch in g2) nn.Add(ch.name + (ch.gameObject.activeSelf ? "" : "(off)"));
                            names.Add(mv.name + " active " + mv.gameObject.activeInHierarchy + " [" + string.Join(", ", nn.GetRange(0, Math.Min(20, nn.Count))) + "]");
                            if (nn.Count > 1 && gi == null) gi = g2;
                        }
                        c.details.Add("map views " + mvs.Length + ": " + string.Join(" ; ", names));
                        var cam1 = Script.FreeWorldAirShip.Galaxy.GalaxyField.GalaxyFieldScene.Instance?.MainCamera;
                        var rows = new List<string>();
                        foreach (var f in UnityEngine.Object.FindObjectsOfType<Script.FreeWorldAirShip.Galaxy.NPCFleet.GalaxyNPCFleet>())
                        {
                            if (f == null || cam1 == null) continue;
                            var spp = cam1.WorldToScreenPoint(f.transform.position);
                            string tn = "?"; try { GalaxyNPCFleetType rt; string why; if (FleetPlayerAntiDisturbService.TryResolveType(f.NpcSetBean?.getFleetBean()?.bean, out rt, out why)) tn = rt.ToString(); } catch { }
                            rows.Add(f.FleetUId + " " + tn + " screen " + spp.ToString("F0") + " entry " + (gi != null && gi.Find(f.FleetUId.ToString()) != null));
                        }
                        c.details.Add("fleets vs map entries: " + string.Join(" | ", rows));
                    }
                    {
                        // What draws the dots: renderers and UI graphics within 6 px of three fleets' projections.
                        var cam0 = Script.FreeWorldAirShip.Galaxy.GalaxyField.GalaxyFieldScene.Instance?.MainCamera;
                        Func<Transform, string> path = t => { var ps = new List<string>(); for (var x = t; x != null && ps.Count < 6; x = x.parent) ps.Add(x.name); ps.Reverse(); return string.Join("/", ps); };
                        int nf = 0;
                        foreach (var f in UnityEngine.Object.FindObjectsOfType<Script.FreeWorldAirShip.Galaxy.NPCFleet.GalaxyNPCFleet>())
                        {
                            if (f == null || cam0 == null || nf++ >= 3) continue;
                            Vector3 fp = cam0.WorldToScreenPoint(f.transform.position);
                            var near = new List<string>();
                            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
                            {
                                if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
                                Vector3 rp = cam0.WorldToScreenPoint(r.bounds.center);
                                if (rp.z > 0 && Mathf.Abs(rp.x - fp.x) < 6 && Mathf.Abs(rp.y - fp.y) < 6) near.Add(r.GetType().Name + " " + path(r.transform));
                            }
                            foreach (var g in UnityEngine.Object.FindObjectsOfType<UnityEngine.UI.Graphic>())
                            {
                                if (!g.isActiveAndEnabled || g.canvas == null) continue;
                                var cv = g.canvas.rootCanvas; Camera uc = cv.renderMode == RenderMode.ScreenSpaceOverlay ? null : cv.worldCamera;
                                Vector2 gp = RectTransformUtility.WorldToScreenPoint(uc, g.rectTransform.position);
                                if (Mathf.Abs(gp.x - fp.x) < 6 && Mathf.Abs(gp.y - fp.y) < 6) near.Add("UI " + g.GetType().Name + " " + path(g.transform));
                            }
                            c.details.Add("probe near fleet " + f.FleetUId + " pending remove " + f.HasPendingRemove + " at screen " + fp.ToString("F0") + ": " + (near.Count == 0 ? "nothing" : string.Join(" ; ", near.GetRange(0, Math.Min(8, near.Count)))));
                        }
                    }
                    float sp0 = MapManager.getLogicSpeed(); bool pl0 = MapManager.isPlay;
                    MapManager.setLogicSpeed(5f);
                    yield return new WaitForSecondsRealtime(4);
                    yield return Shot("star-field-after-scene-later");
                    c.details.Add("probe star field t+4s at x5: " + probe());
                    MapManager.setLogicSpeed(sp0);
                    if (!pl0) MapManager.pauseLogic();
                }
            }
            c.passed = ok;
            c.summary = ok ? "caravans listed as in the save, prices equal the game's caravan page, checkouts match the credits and the ledger, smuggler shown and traded" : "see details";
        }

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
            // A completed plan has nothing left to price: its detail shows no "?" and no best markets.
            host.SetVisible(true);
            host.ShowTab(1);
            yield return new WaitForSecondsRealtime(1);
            Ui.PlansView.Current?.Select(plan);
            yield return null;
            string shown = Ui.PlansView.Current != null ? System.Text.RegularExpressions.Regex.Replace(Ui.PlansView.Current.DetailText, "<[^>]+>", "") : "";
            bool detailOk = shown.Length > 0 && !shown.Contains("?") && !shown.Contains("现在卖") && shown.Contains("到手按结账记录");
            c.details.Add("completed plan detail: " + shown.Replace("\n", " / ") + (detailOk ? " ok" : " WRONG"));
            ok &= detailOk;
            host.SetVisible(false);

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
            host.ShowTab(7);
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
                    if (!q.special || q.ship || sg == null || sg.UsesEquipmentPressure || !q.buyable) continue;
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
                    if (!q.special || q.ship || q.specialGoods == null || q.specialGoods.UsesEquipmentPressure) continue;
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
                    if (!q.special || q.ship || q.specialGoods == null || q.specialGoods.UsesEquipmentPressure) continue;
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
            foreach (var t in snap.trades.FindAll(x => x.buy.ship).Take(5))
                c.details.Add("ship: " + t.name + " x" + t.qty + " " + t.buy.market.name + " -> " + t.sell.market.name + " cost " + t.cost.ToString("0") + " revenue " + t.revenue.ToString("0") + " profit " + t.Profit.ToString("0"));
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
            // Every category is listed; every one without a paying route has a note.
            var payingCats = new HashSet<string>(snap.trades.ConvertAll(t => t.category));
            foreach (var cat in snap.tradeCategories)
            {
                string note;
                bool has = snap.categoryNote.TryGetValue(cat, out note);
                if (!payingCats.Contains(cat) && !has) { bad++; c.details.Add("category without a note: " + cat); }
                else if (has) c.details.Add("note: " + note);
            }
            c.details.Add("categories: " + string.Join("、", snap.tradeCategories) + " (" + payingCats.Count + " with paying routes)");
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
