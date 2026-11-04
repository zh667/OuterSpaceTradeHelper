using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace TradeHelper
{
    public class TradeHelperBehaviour : MonoBehaviour
    {
        public const string Version = "0.4.2";
        public static TradeHelperBehaviour Instance;

        Config cfg;
        public readonly Settlement settlement = new Settlement();
        Ui.Window window;
        Snapshot latest, building;
        IEnumerator job;
        int jobFrames;
        float nextAutoRefresh, nextExport, themeAt = -1;
        bool exportAfterScan, refreshAfterSettlement;
        string lastStatus = "";
        FWGameData seenGame;
        float openWaitStart = -1;

        void Awake()
        {
            Instance = this;
            cfg = Config.Load();
            Plans.Load();
            nextExport = Time.unscaledTime + 60f;
            settlement.MarketFinished += _ => refreshAfterSettlement = true;
            if (SelfTest.Requested()) gameObject.AddComponent<SelfTest>();
        }

        public void SetVisible(bool on)
        {
            if (on)
            {
                if (window == null) { window = new Ui.Window(this, cfg); window.Build(); if (latest != null) window.OnSnapshot(latest); }
                window.canvasGo.SetActive(true);
                if (latest == null && openWaitStart < 0) openWaitStart = Time.unscaledTime;
                if (latest == null || (DateTime.Now - latest.takenAt).TotalSeconds > 5) RequestRefresh(false);
                UpdateStatus();
            }
            else if (window != null)
            {
                Ui.Kit.Drop.CloseAll?.Invoke();
                window.canvasGo.SetActive(false);
            }
        }

        bool Visible => window != null && window.Visible;
        public Snapshot Latest => latest;
        public Config Cfg => cfg;
        public void ShowTab(int t) { if (window != null) window.SelectTab(t); }
        public bool Scanning => job != null;

        // Starts a background scan (no-op when one is already running unless restart is asked).
        public void RequestRefresh(bool restart)
        {
            if (job != null && !restart) return;
            building = new Snapshot();
            job = MarketScanner.Scan(building, cfg);
            jobFrames = 0;
            nextAutoRefresh = Time.unscaledTime + Mathf.Max(5f, cfg.autoRefreshSeconds <= 0 ? 1e9f : cfg.autoRefreshSeconds);
        }

        // Saves a screenshot of the whole screen (panel included) for feedback.
        public void Screenshot() { StartCoroutine(CaptureAtEndOfFrame()); }

        IEnumerator CaptureAtEndOfFrame()
        {
            yield return new WaitForEndOfFrame();
            Texture2D tex = null;
            try
            {
                var dir = System.IO.Path.Combine(cfg.DataDir, "shots");
                System.IO.Directory.CreateDirectory(dir);
                var p = System.IO.Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png");
                tex = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0);
                tex.Apply(false);
                System.IO.File.WriteAllBytes(p, ImageConversion.EncodeToPNG(tex));
                lastStatus = "截图已保存 " + p;
            }
            catch (Exception e) { lastStatus = "截图失败: " + e.Message; }
            finally { if (tex != null) Destroy(tex); }
        }

        // A plan was added from a table: re-price plans now and refresh the tables that show "追踪中".
        public void PlansChanged()
        {
            if (latest != null) { long t = Perf.Now; Plans.Track(latest, cfg, settlement); Perf.Add(Perf.Cat.Plans, t); }
            if (window != null) window.RefreshViews();
        }

        public void ExportNow()
        {
            if (latest == null) { exportAfterScan = true; RequestRefresh(false); return; }
            DoExport(latest);
        }

        void DoExport(Snapshot s)
        {
            try { lastStatus = "已导出 " + Export.Write(s, s.trades, s.sells, cfg.DataDir); }
            catch (Exception e) { lastStatus = "导出失败: " + e.Message; }
        }

        void PumpJob()
        {
            if (job == null) return;
            jobFrames++;
            // Twice the budget while the panel is open with nothing to show yet.
            float budget = Mathf.Max(1f, cfg.scanMsPerFrame) * (Visible && latest == null ? 2f : 1f);
            Budget.StartFrame(budget);
            long pt = Perf.Now;
            try
            {
                while (!Budget.Over)
                {
                    long ts = Perf.Now;
                    var phaseBefore = building != null ? building.phase : null;
                    var al = Perf.AllocBegin();
                    bool more = job.MoveNext();
                    double stepMs = Perf.Ms(Perf.Now - ts);
                    if (building != null) CountAlloc(building, phaseBefore, al);
                    if (building != null && stepMs > building.maxStepMs) { building.maxStepMs = stepMs; building.maxStepPhase = building.phase; }
                    if (!more)
                    {
                        job = null;
                        latest = building; building = null;
                        var line = "扫描完成 " + DateTime.Now.ToString("HH:mm:ss") + "：" + latest.markets.Count + " 站, " + latest.trades.Count + " 条跑商, " + latest.sells.Count + " 条卖货, 用时 " +
                                   latest.scanMs + "ms 分 " + jobFrames + " 帧（每帧上限 " + budget.ToString("0") + "ms）, 沿用未变的站 " + latest.marketsReused + "/" + latest.markets.Count +
                                   (latest.adviceReused ? ", 建议沿用" : ", 建议重算") +
                                   ", 分配 读站 " + Mb(latest.allocRead) + " 库存 " + Mb(latest.allocInventory) + " 跑商建议 " + Mb(latest.allocTrades) + " 卖货建议 " + Mb(latest.allocSells) +
                                   (latest.allocSkipped > 0 ? "（" + latest.allocSkipped + " 步赶上回收没算）" : "") + ", 最长一步 " + latest.maxStepMs.ToString("0.0") + "ms（" + latest.maxStepPhase + "）; " + Budget.Report();
                        Perf.LastScan = line;
                        Perf.Write(line, cfg);
                        Debug.Log("[TradeHelper] " + line);
                        MarketScanner.SaveDiskCache();
                        long tp = Perf.Now;
                        Plans.Track(latest, cfg, settlement);
                        Perf.Add(Perf.Cat.Plans, tp);
                        if (openWaitStart >= 0)
                        {
                            Perf.Write("首次打开面板等数据 " + (Time.unscaledTime - openWaitStart).ToString("0.0") + " 秒", cfg);
                            openWaitStart = -1;
                        }
                        if (window != null) window.OnSnapshot(latest);
                        if (exportAfterScan) { exportAfterScan = false; DoExport(latest); }
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                job = null; building = null;
                lastStatus = "刷新失败: " + e.Message;
                Debug.LogWarning("[TradeHelper] scan: " + e);
            }
            Perf.Add(Perf.Cat.Scan, pt);
        }

        static string Mb(long b) => (b / 1048576.0).ToString("0.0") + "MB";

        // Attributes a scan step's allocation to the phase it ran in (the phase is set at the start of each part).
        static void CountAlloc(Snapshot b, string phaseBefore, Perf.Alloc a)
        {
            if (GC.CollectionCount(0) != a.gc) { b.allocSkipped++; return; }
            long d = GC.GetTotalMemory(false) - a.mem;
            if (d <= 0) return;
            string ph = b.phase ?? phaseBefore ?? "";
            if (ph.StartsWith("读站")) b.allocRead += d;
            else if (ph == "读库存") b.allocInventory += d;
            else if (ph == "算跑商建议") b.allocTrades += d;
            else if (ph == "算卖货建议") b.allocSells += d;
            else b.allocRead += d;
        }

        void UpdateStatus()
        {
            if (window == null) return;
            string s = "";
            if (latest != null) s = "信用点 " + Ui.Fmt.Money(latest.credits) + "   舰队在 " + latest.GalaxyName(latest.fleetGalaxyId) + "   ";
            if (job != null) s += (latest == null ? "首次读取中 " : "正在刷新 ") + (building != null ? building.marketsDone + "/" + building.marketsTotal + " 站" : "…");
            else if (latest != null) s += "刷新于 " + latest.takenAt.ToString("HH:mm:ss") + "（" + latest.scanMs + " ms，分 " + jobFrames + " 帧）";
            if (settlement.Pending > 0) s += "   后台补算 " + settlement.Pending + " 站";
            if (!string.IsNullOrEmpty(lastStatus)) s += "   " + lastStatus;
            window.SetStatus(s);
        }

        void Update()
        {
            var alloc = Perf.AllocBegin();
            try
            {
                var gd = MarketScanner.Game;
                if (!ReferenceEquals(gd, seenGame))
                {
                    // Another save (or the main menu): drop everything that belongs to the previous one.
                    seenGame = gd;
                    job = null; building = null; latest = null;
                    settlement.Clear();
                    MarketScanner.ClearCache();
                    if (window != null) window.snap = null;
                    if (gd != null && Visible) RequestRefresh(true);
                }
                if (gd == null)
                {
                    if (Visible) SetVisible(false);
                    return;
                }
                // Load the game's font and sprites a few seconds after a save is loaded, so the first F8 is instant.
                if (themeAt < 0) themeAt = Time.unscaledTime + 5f;
                else if (themeAt > 0 && Time.unscaledTime >= themeAt) { themeAt = 0; try { Ui.Theme.Load(); } catch (Exception e) { Debug.LogWarning("[TradeHelper] theme: " + e); } }

                long t0 = Perf.Now;
                settlement.Tick(cfg);
                Perf.Add(Perf.Cat.Settle, t0);
                if (refreshAfterSettlement && settlement.Pending == 0 && Visible) { refreshAfterSettlement = false; RequestRefresh(false); }
                // Read the markets in the background right after a save loads, so the first F8 already has data.
                if (latest == null && job == null && themeAt == 0) RequestRefresh(false);
                PumpJob();
                if (Visible)
                {
                    long tu = Perf.Now;
                    if (cfg.autoRefreshSeconds > 0 && Time.unscaledTime >= nextAutoRefresh) RequestRefresh(false);
                    window.Tick();
                    UpdateStatus();
                    Perf.Add(Perf.Cat.Ui, tu);
                }
                if (cfg.exportEverySeconds > 0 && Time.unscaledTime >= nextExport)
                {
                    nextExport = Time.unscaledTime + cfg.exportEverySeconds;
                    exportAfterScan = true;
                    RequestRefresh(false);
                }
                Perf.AllocEnd(alloc);
                Perf.EndFrame(cfg);
            }
            catch (Exception e) { Debug.LogWarning("[TradeHelper] update: " + e); }
        }

        // Hide the panel before the game tears its objects down: text meshes disabled after their materials are gone
        // throw (seen when quitting with the panel open).
        void OnApplicationQuit()
        {
            try { if (window != null && window.canvasGo != null) window.canvasGo.SetActive(false); } catch { }
        }

        static bool TypingInInput()
        {
            var es = EventSystem.current;
            var go = es != null ? es.currentSelectedGameObject : null;
            if (go == null) return false;
            var tmp = go.GetComponent<TMP_InputField>();
            if (tmp != null && tmp.isFocused) return true;
            var legacy = go.GetComponent<UnityEngine.UI.InputField>();
            return legacy != null && legacy.isFocused;
        }

        void OnGUI()
        {
            long t = Perf.Now;
            try { HotkeyCheck(); } finally { Perf.Add(Perf.Cat.Gui, t); }
        }

        void HotkeyCheck()
        {
            var e = Event.current;
            if (e == null || e.type != EventType.KeyDown || e.keyCode != cfg.HotkeyCode) return;
            if (MarketScanner.Game == null || TypingInInput()) return;
            SetVisible(!Visible);
            e.Use();
        }
    }
}
