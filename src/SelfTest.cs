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

            var snap = host.Latest;
            Run(() => CheckTax(snap));
            Run(() => CheckNormal(snap));
            Run(() => CheckSpecialBuy(snap));
            Run(() => CheckSpecialResale(snap));
            Run(() => CheckSpecialSellModel(snap));
            Run(() => CheckAdvice(snap));

            // A full scan from scratch, then a refresh with nothing changed.
            yield return TimedScan("fullScan", true);
            yield return TimedScan("unchangedRefresh", false);

            host.SetVisible(true);
            yield return new WaitForSecondsRealtime(4);
            yield return Shot("panel");
            Finish(null);
        }

        void Run(Func<Check> f)
        {
            try { result.checks.Add(f()); }
            catch (Exception e) { result.checks.Add(new Check { name = f.Method.Name, passed = false, summary = "exception: " + e }); }
        }

        IEnumerator TimedScan(string name, bool fromScratch)
        {
            if (fromScratch) MarketScanner.ClearCache();
            int gc0 = GC.CollectionCount(0);
            float t = Time.realtimeSinceStartup;
            host.RequestRefresh(true);
            yield return null;
            while (host.Scanning && Time.realtimeSinceStartup - t < 400) yield return null;
            result.perf[name + "Seconds"] = Math.Round(Time.realtimeSinceStartup - t, 2);
            result.perf[name + "Collections"] = GC.CollectionCount(0) - gc0;
            result.perf[name] = Perf.LastScan;
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
