using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace TradeHelper
{
    public class TradeHelperBehaviour : MonoBehaviour
    {
        public const string Version = "0.2.0";
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

        void Awake()
        {
            Instance = this;
            cfg = Config.Load();
            nextExport = Time.unscaledTime + 60f;
            settlement.MarketFinished += _ => refreshAfterSettlement = true;
        }

        public void SetVisible(bool on)
        {
            if (on)
            {
                if (window == null) { window = new Ui.Window(this, cfg); window.Build(); if (latest != null) window.OnSnapshot(latest); }
                window.canvasGo.SetActive(true);
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
            Budget.StartFrame(Mathf.Max(1f, cfg.scanMsPerFrame));
            try
            {
                while (!Budget.Over)
                {
                    if (!job.MoveNext())
                    {
                        job = null;
                        latest = building; building = null;
                        Debug.Log("[TradeHelper] scan done: " + latest.markets.Count + " markets, " + latest.trades.Count + " trades, " + latest.sells.Count + " sells, " +
                                  latest.scanMs + " ms over " + jobFrames + " frames; " + Budget.Report());
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
        }

        void UpdateStatus()
        {
            if (window == null) return;
            string s = "";
            if (latest != null) s = "信用点 " + Ui.Fmt.Money(latest.credits) + "   舰队在 " + latest.GalaxyName(latest.fleetGalaxyId) + "   ";
            if (job != null) s += "正在刷新…";
            else if (latest != null) s += "刷新于 " + latest.takenAt.ToString("HH:mm:ss") + "（" + latest.scanMs + " ms，分 " + jobFrames + " 帧）";
            if (settlement.Pending > 0) s += "   后台补算 " + settlement.Pending + " 站";
            if (!string.IsNullOrEmpty(lastStatus)) s += "   " + lastStatus;
            window.SetStatus(s);
        }

        void Update()
        {
            try
            {
                var gd = MarketScanner.Game;
                if (!ReferenceEquals(gd, seenGame))
                {
                    // Another save (or the main menu): drop everything that belongs to the previous one.
                    seenGame = gd;
                    job = null; building = null; latest = null;
                    settlement.Clear();
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

                settlement.Tick(cfg);
                if (refreshAfterSettlement && settlement.Pending == 0 && Visible) { refreshAfterSettlement = false; RequestRefresh(false); }
                PumpJob();
                if (Visible)
                {
                    if (cfg.autoRefreshSeconds > 0 && Time.unscaledTime >= nextAutoRefresh) RequestRefresh(false);
                    window.Tick();
                    UpdateStatus();
                }
                if (cfg.exportEverySeconds > 0 && Time.unscaledTime >= nextExport)
                {
                    nextExport = Time.unscaledTime + cfg.exportEverySeconds;
                    exportAfterScan = true;
                    RequestRefresh(false);
                }
            }
            catch (Exception e) { Debug.LogWarning("[TradeHelper] update: " + e); }
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
            var e = Event.current;
            if (e == null || e.type != EventType.KeyDown || e.keyCode != cfg.HotkeyCode) return;
            if (MarketScanner.Game == null || TypingInInput()) return;
            SetVisible(!Visible);
            e.Use();
        }
    }
}
