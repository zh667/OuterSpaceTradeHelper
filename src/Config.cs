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
        // A market counts as "fresh" (exact prices) when it owes at most this many settlement days.
        public int freshDays = 0;
        // Background settlement budget per frame.
        public int daysPerFrame = 3;
        public float msPerFrame = 6f;
        // Auto export interval (real seconds, 0 = off).
        public float exportEverySeconds = 300f;
        public bool onlyFreshInAdvice = false;

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
