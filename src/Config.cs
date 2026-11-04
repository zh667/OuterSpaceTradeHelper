using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace TradeHelper
{
    [Serializable]
    public class Config
    {
        public string hotkey = "F8";
        // Exchange uids the player chose to keep caught up in the background.
        public List<long> keepFresh = new List<long>();
        // Advice only uses markets that owe at most this many settlement days (0 = only up-to-date markets).
        public int maxStaleDays = 0;
        // Background catch-up budget: settle at most this many days per frame, stop early after this many ms.
        public int daysPerFrame = 1;
        public float msPerFrame = 4f;
        // Time budget for scanning/advice work per frame.
        public float scanMsPerFrame = 4f;
        public float autoRefreshSeconds = 30f;
        // Auto export interval (real seconds, 0 = off).
        public float exportEverySeconds = 300f;

        // Remembered sort/filter state per table (column index, descending) — see UI views.
        public int tradesSort = 7, sellsSort = 5, goodsSort = 0, marketsSort = 3, invSort = 2, settleSort = 2;
        public bool tradesDesc = true, sellsDesc = true, goodsDesc = false, marketsDesc = false, invDesc = true, settleDesc = false;
        public float maxFleetLy = 0;   // 0 = no limit
        public float minProfit = 0;
        public bool goodsBuyableOnly = false;
        public bool hideContraband = true;

        [NonSerialized] public string path;

        public static string Dir => Path.Combine(Application.persistentDataPath, "TradeHelper");

        public static Config Load()
        {
            var p = Path.Combine(Dir, "config.json");
            Config c = null;
            try
            {
                if (File.Exists(p)) c = JsonUtility.FromJson<Config>(File.ReadAllText(p));
            }
            catch (Exception e) { Debug.LogWarning("[TradeHelper] config read failed: " + e.Message); }
            if (c == null) c = new Config();
            if (c.keepFresh == null) c.keepFresh = new List<long>();
            c.path = p;
            return c;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(path ?? Path.Combine(Dir, "config.json"), JsonUtility.ToJson(this, true));
            }
            catch (Exception e) { Debug.LogWarning("[TradeHelper] config write failed: " + e.Message); }
        }

        public KeyCode HotkeyCode
        {
            get
            {
                try { return (KeyCode)Enum.Parse(typeof(KeyCode), hotkey, true); }
                catch { return KeyCode.F8; }
            }
        }
    }
}
