using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace TradeHelper.Ui
{
    // Diagnostics for "the search boxes cannot be clicked" (seen by a player, not in the isolated test): while the panel
    // is open, the first clicks of a session are written to the game's log with what the game's event system hits at
    // that point, what is selected, and the cursor state; search boxes log when they gain or lose the focus and how
    // many keys reached them. Logging only.
    public static class InputProbe
    {
        const int MaxClicks = 40;
        static int clicks, keys;
        static float checkAt = -1;
        static readonly List<RaycastResult> hits = new List<RaycastResult>();

        public static void OnGui(GameObject canvas)
        {
            var e = Event.current;
            if (e == null) return;
            if (e.type == EventType.KeyDown && e.character != '\0' && FocusedBox(canvas) != null) keys++;
            if (e.type != EventType.MouseDown || clicks >= MaxClicks) return;
            clicks++;
            var pos = new Vector2(e.mousePosition.x, Screen.height - e.mousePosition.y);
            var es = EventSystem.current;
            string top = "no EventSystem";
            if (es != null)
            {
                hits.Clear();
                try { es.RaycastAll(new PointerEventData(es) { position = pos }, hits); } catch (Exception x) { top = "raycast failed: " + x.Message; }
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < Math.Min(3, hits.Count); i++)
                    sb.Append(i == 0 ? "" : " > ").Append(hits[i].gameObject.name).Append('@').Append(hits[i].module != null ? hits[i].module.name : "?");
                if (hits.Count > 0) top = sb.ToString(); else if (es != null && top == "no EventSystem") top = "nothing";
            }
            Debug.Log("[TradeHelper] click " + clicks + " at " + pos.x.ToString("0") + "," + pos.y.ToString("0") + " (screen " + Screen.width + "x" + Screen.height + "): hits " + top
                      + "; selected " + (es != null && es.currentSelectedGameObject != null ? es.currentSelectedGameObject.name : "none")
                      + "; module " + (es != null && es.currentInputModule != null ? es.currentInputModule.GetType().Name + (es.currentInputModule.enabled ? "" : " (disabled)") : "none")
                      + "; cursor visible " + Cursor.visible + " lock " + Cursor.lockState);
            checkAt = Time.unscaledTime + 0.3f;
        }

        public static void Tick(GameObject canvas)
        {
            if (checkAt < 0 || Time.unscaledTime < checkAt) return;
            checkAt = -1;
            var box = FocusedBox(canvas);
            Debug.Log("[TradeHelper] after click " + clicks + ": search box focused " + (box != null ? box.name + " (keys so far " + keys + ")" : "none")
                      + "; selected " + (EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null ? EventSystem.current.currentSelectedGameObject.name : "none"));
        }

        static TMPro.TMP_InputField FocusedBox(GameObject canvas)
        {
            if (canvas == null) return null;
            foreach (var f in canvas.GetComponentsInChildren<TMPro.TMP_InputField>(false)) if (f.isFocused) return f;
            return null;
        }

        public static void Watch(TMPro.TMP_InputField f, string label)
        {
            f.onSelect.AddListener(_ => Debug.Log("[TradeHelper] search box '" + label + "' got the focus"));
            f.onDeselect.AddListener(_ => Debug.Log("[TradeHelper] search box '" + label + "' lost the focus (selected now " + (EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null ? EventSystem.current.currentSelectedGameObject.name : "none") + ")"));
        }
    }
}
