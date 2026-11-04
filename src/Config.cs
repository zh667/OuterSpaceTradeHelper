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
        // Galaxies the player has not discovered are not on the map; leave them out of advice unless asked.
        public bool includeUndiscovered = false;

        // Where latest.json and screenshots go: 0 = the game's data folder on C: (LocalLow), 1 = inside the mod's own
        // folder in the game directory (<game>/mods/TradeHelper/data), 2 = a folder the player typed.
        public int dataDirMode = 0;
        public string dataDirCustom = "";

        [NonSerialized] public string path;

        // config.json itself always stays here so the mod can find it before knowing the player's choice.
        public static string Dir => Path.Combine(Application.persistentDataPath, "TradeHelper");

        public static string GameModDataDir
        {
            get
            {
                try { return Path.Combine(Directory.GetParent(Application.dataPath).FullName, "mods", "TradeHelper", "data"); }
                catch { return null; }
            }
        }

        // Resolved export folder; falls back to Dir when the chosen one is unusable.
        public string DataDir
        {
            get
            {
                string d = null;
                if (dataDirMode == 1) d = GameModDataDir;
                else if (dataDirMode == 2 && !string.IsNullOrEmpty(dataDirCustom))
                {
                    try { if (Path.IsPathRooted(dataDirCustom.Trim())) d = dataDirCustom.Trim(); } catch { }
                }
                if (string.IsNullOrEmpty(d)) return Dir;
                try { Directory.CreateDirectory(d); return d; }
                catch (Exception e) { Debug.LogWarning("[TradeHelper] data folder unusable (" + d + "): " + e.Message); return Dir; }
            }
        }

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
            if (c.dataDirMode == 2 && string.IsNullOrEmpty(c.dataDirCustom)) c.dataDirMode = 0; // custom chosen but never filled in
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
