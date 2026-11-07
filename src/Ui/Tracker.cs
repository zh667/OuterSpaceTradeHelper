using System;
using Script.FreeWorldAirShip.Galaxy.GalaxyField;
using Script.FreeWorldAirShip.Galaxy.NPCFleet;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TradeHelper.Ui
{
    // Marks one caravan on the galaxy map: a ring and its name over the fleet's object in the galaxy scene
    // (GalaxyNPCFleet with that FleetUId), drawn on an overlay of its own so it stays while the panel is closed.
    // Off screen, the marker sits on the screen edge in the fleet's direction. Read-only: it only reads the
    // fleet's position and, when asked, moves the camera with the game's own call (ScrollGalaxyPos, as "back to my
    // fleet" does).
    public static class Tracker
    {
        public static long uid = -1;
        public static string name, galaxyName;
        public static string Status = "";
        public static bool OnScreen, Found;
        public static Vector2 ScreenPos;
        public static Camera LastCamera;
        public static Vector3 Projected;
        public static Vector2 Direction;
        public static Vector3 FleetWorld => npc != null && npc ? npc.transform.position : Vector3.zero;

        static GameObject canvasGo;
        static RectTransform root, marker, ring;
        static TextMeshProUGUI label, info;
        static GalaxyNPCFleet npc;
        static float nextFind;

        public static bool IsTracked(long id) => uid == id && id >= 0;

        static string Arrow(Vector2 d)
        {
            float a = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;   // screen y is up
            string[] arrows = { "→", "↗", "↑", "↖", "←", "↙", "↓", "↘" };
            int i = Mathf.RoundToInt(((a % 360) + 360) % 360 / 45f) % 8;
            return arrows[i];
        }

        public static void Track(CaravanInfo c, bool focus = true)
        {
            if (c == null) { Stop(); return; }
            uid = c.uid; name = c.FullName; galaxyName = c.galaxyName;
            npc = null; nextFind = 0;
            Find();
            if (focus) Focus();
        }

        public static void Stop()
        {
            uid = -1; npc = null; Status = "";
            if (canvasGo != null) canvasGo.SetActive(false);
        }

        static float focusUntil;

        // In the galaxy view (where fleets are drawn)? In the mothership (Ground) the scene still runs behind it.
        public static bool InGalaxyView
        {
            get
            {
                var gs = GalaxyFieldScene.Instance;
                if (gs == null) return false;
                var st = gs.CurState;
                return st == State.GalaxyScene || st == State.GalaxyField;
            }
        }

        // Move the galaxy camera onto the fleet (the game's own camera move). From the mothership, first open the
        // galaxy the player's fleet is in (AirShipUIUtils.ShowGalaxyFleet, as the game's "my fleet" button does); the
        // camera follows once the galaxy view is up.
        public static bool Focus()
        {
            Find();
            var gs = GalaxyFieldScene.Instance;
            if (npc == null || gs == null) return false;
            try
            {
                if (!InGalaxyView) AirShipUIUtils.ShowGalaxyFleet();
                focusUntil = Time.unscaledTime + 4f;
                return true;
            }
            catch (Exception e) { Debug.LogWarning("[TradeHelper] tracker focus: " + e.Message); return false; }
        }

        static void DoFocus()
        {
            var gs = GalaxyFieldScene.Instance;
            var cam = gs.GalaxyFieldCameraController;
            // Instant (ScrollGalaxyPos with no move time = ApplyLayerCutPose): the animated move runs on game time and
            // would not move at all while the game is paused.
            cam.ScrollGalaxyPos(npc.transform.position, gs.GetGalaxySceneDefaultDistance(), 0f, null);
        }

        static void Find()
        {
            if (uid < 0) { npc = null; return; }
            if (npc != null && npc && npc.FleetUId == uid) return;
            npc = null;
            if (Time.unscaledTime < nextFind) return;
            nextFind = Time.unscaledTime + 1f;
            foreach (var f in UnityEngine.Object.FindObjectsOfType<GalaxyNPCFleet>())
                if (f != null && f.FleetUId == uid) { npc = f; break; }
        }

        static void Build()
        {
            canvasGo = new GameObject("TradeHelperTracker");
            UnityEngine.Object.DontDestroyOnLoad(canvasGo);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 29990;   // under the panel
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            root = (RectTransform)canvasGo.transform;
            marker = Kit.New("Marker", root);
            marker.pivot = new Vector2(0.5f, 0.5f);
            marker.sizeDelta = new Vector2(64, 64);
            ring = Kit.New("Ring", marker);
            ring.anchorMin = ring.anchorMax = ring.pivot = new Vector2(0.5f, 0.5f);
            ring.sizeDelta = new Vector2(64, 64);
            var img = ring.gameObject.AddComponent<Image>();
            img.sprite = Theme.Halo ?? Theme.Box;
            img.color = Theme.Accent;
            img.raycastTarget = false;
            label = Kit.Txt(marker, "", 18, Theme.Accent, TextAlignmentOptions.Bottom);
            label.rectTransform.anchorMin = label.rectTransform.anchorMax = new Vector2(0.5f, 1f);
            label.rectTransform.pivot = new Vector2(0.5f, 0f);
            label.rectTransform.sizeDelta = new Vector2(420, 26);
            label.rectTransform.anchoredPosition = new Vector2(0, 4);
            label.outlineWidth = 0.2f; label.outlineColor = Color.black;
            info = Kit.Txt(root, "", 16, Theme.Accent, TextAlignmentOptions.Center);
            info.rectTransform.anchorMin = info.rectTransform.anchorMax = new Vector2(0.5f, 1f);
            info.rectTransform.pivot = new Vector2(0.5f, 1f);
            info.rectTransform.sizeDelta = new Vector2(900, 28);
            info.rectTransform.anchoredPosition = new Vector2(0, -96);
            info.outlineWidth = 0.2f; info.outlineColor = Color.black;
        }

        // Every frame from TradeHelperBehaviour.Update (main thread).
        public static void Tick()
        {
            if (uid < 0) return;
            if (canvasGo == null) Build();
            if (!canvasGo.activeSelf) canvasGo.SetActive(true);
            Find();
            var gs = GalaxyFieldScene.Instance;
            Camera cam = null;
            // The camera the game projects fleet markers with (GalaxyElementFleet.transformNguiPos ->
            // GalaxySceneInterface.getCamera() = GalaxyFieldScene.MainCamera).
            try { cam = gs != null ? gs.MainCamera : null; } catch { }
            LastCamera = cam;
            Found = npc != null && npc && npc.gameObject.activeInHierarchy;
            bool view = InGalaxyView;
            // A requested camera move happens once the galaxy view is up (it may be opening).
            if (focusUntil > 0 && Found && view)
            {
                try { DoFocus(); } catch (Exception e) { Debug.LogWarning("[TradeHelper] tracker focus: " + e.Message); }
                focusUntil = 0;
            }
            else if (focusUntil > 0 && Time.unscaledTime > focusUntil) focusUntil = 0;
            if (!Found || !view || cam == null || !cam.isActiveAndEnabled)
            {
                marker.gameObject.SetActive(false);
                OnScreen = false;
                Status = !Found ? "追踪中：" + name + " 不在场景里（它在 " + galaxyName + "；你的舰队到那个星系、打开星系地图它才会出现）"
                                : "追踪中：" + name + "：打开星系地图才看得到标记（在“商队”页点“定位追踪的商队”会自动打开）";
                info.text = Status;
                return;
            }
            Vector3 sp = cam.WorldToScreenPoint(npc.transform.position);
            Projected = sp;
            bool behind = sp.z < 0;
            if (behind) sp = -sp;
            float w = Screen.width, h = Screen.height, m = 48;
            OnScreen = !behind && sp.x >= 0 && sp.x <= w && sp.y >= 0 && sp.y <= h;
            Vector2 p;
            if (OnScreen) p = new Vector2(sp.x, sp.y);
            else
            {
                // Off screen: on the edge, along the line from the screen centre towards the fleet.
                Vector2 c = new Vector2(w / 2f, h / 2f), d = new Vector2(sp.x, sp.y) - c;
                if (d.sqrMagnitude < 1e-4f) d = Vector2.up;
                float k = Mathf.Min((w / 2f - m) / Mathf.Max(1e-4f, Mathf.Abs(d.x)), (h / 2f - m) / Mathf.Max(1e-4f, Mathf.Abs(d.y)));
                p = c + d * k;
                Direction = d.normalized;
            }
            ScreenPos = p;
            Vector2 local;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(root, p, null, out local);
            marker.gameObject.SetActive(true);
            marker.anchorMin = marker.anchorMax = new Vector2(0.5f, 0.5f);
            marker.anchoredPosition = local;
            float pulse = 1f + 0.18f * Mathf.Sin(Time.unscaledTime * 5f);
            ring.localScale = new Vector3(pulse, pulse, 1);
            label.text = OnScreen ? name : Arrow(Direction) + " " + name + "（在这个方向，拖动或缩小地图）";
            Status = OnScreen ? "追踪中：" + name : "追踪中：" + name + "（在屏幕外）";
            info.text = "";
        }
    }
}
