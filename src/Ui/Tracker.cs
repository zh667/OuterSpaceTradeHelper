using System;
using System.Collections.Generic;
using Script.FreeWorldAirShip.Galaxy.GalaxyField;
using Script.FreeWorldAirShip.Galaxy.NPCFleet;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TradeHelper.Ui
{
    // Marks one caravan where the game draws it, with a ring and its name on an overlay of its own (it stays while the
    // panel is closed): in the galaxy view (至星系) on the fleet's scene object while its ships are drawn; in the star
    // field (至星域) on the dot the game's galaxy map draws for that fleet (GalaxyMapView .../GalaxyInfo/<fleet uid>/
    // IconNPCImage), which exists only after the galaxy view has built the fleets. Off screen, the marker sits on the
    // screen edge in the fleet's direction. Read-only: it only reads positions and, when asked, enters the galaxy view
    // and moves the camera with the game's own calls.
    public static class Tracker
    {
        public static long uid = -1;
        public static string name, galaxyName, galaxyId;
        public static string Status = "";
        public static bool OnScreen, Found, Shown;
        public static Vector2 ScreenPos;
        public static Camera LastCamera;
        public static Vector3 Projected;
        public static Vector2 Direction;
        public static Vector3 FleetWorld => icon != null && icon ? icon.bounds.center : npc != null && npc ? npc.transform.position : Vector3.zero;
        public static bool InStarField, UsingMapDot;

        static GameObject canvasGo;
        static RectTransform root, marker, ring;
        static TextMeshProUGUI label, info;
        static GalaxyNPCFleet npc;
        static Renderer icon;            // the galaxy map's dot for the fleet (star field)
        static GalaxyMapView mapView;
        static float nextMapFind;

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
            uid = c.uid; name = c.FullName; galaxyName = c.galaxyName; galaxyId = c.galaxyId;
            npc = null;
            Find();
            if (focus) Focus();
        }

        // A fixed point instead of a fleet: where a smuggler will appear (local coordinates of the galaxy).
        public static bool pointMode;
        static Vector2 pointLocal;
        public static Vector3 PointWorld => LocalToWorld(galaxyId, pointLocal);

        // A galaxy-local position in world space: through the NPC fleets' parent when the game has that galaxy's fleets
        // laid out, else at the galaxy's place on the star field (GalaxyFieldScene.GetGalaxyPos) plus the local offset.
        public static Vector3 LocalToWorld(string gid, Vector2 local)
        {
            var mgr = GalaxyNPCFleetManager.Instance;
            if (mgr != null && mgr.GetGalaxyId() == gid) return LocalToWorld(local);
            var gs = GalaxyFieldScene.Instance;
            return (gs != null ? gs.GetGalaxyPos(gid) : Vector3.zero) + new Vector3(local.x, local.y, 0);
        }

        public static void TrackPoint(string gid, string gname, Vector2 local, string label)
        {
            Stop();
            pointMode = true; pointLocal = local; galaxyId = gid; galaxyName = gname; name = label;
        }

        // A galaxy-local position (as fleets' GalaxyNPCFleet.GetLocalPosition) in world space: the NPC fleets' parent.
        public static Vector3 LocalToWorld(Vector2 local)
        {
            var mgr = GalaxyNPCFleetManager.Instance;
            Transform root = mgr != null ? (mgr.transform.Find("Root") ?? mgr.transform) : null;
            return root != null ? root.TransformPoint(new Vector3(local.x, local.y, 0)) : new Vector3(local.x, local.y, 0);
        }

        public static void Stop()
        {
            uid = -1; npc = null; Status = ""; pointMode = false;
            if (canvasGo != null) canvasGo.SetActive(false);
        }

        static float focusUntil;

        // In the galaxy scene: the only view where the game builds NPC fleets (GalaxyNPCFleetManager.CanBuildPresentations
        // needs State.GalaxyScene). The star field (至星域) zoomed onto a galaxy looks alike but has no NPC fleets, and the
        // mothership (Ground) and planet (Expedition) views have none either.
        public static bool InGalaxyView
        {
            get
            {
                var gs = GalaxyFieldScene.Instance;
                return gs != null && gs.CurState == State.GalaxyScene;
            }
        }

        // The player's fleet galaxy (null when unknown).
        static string FleetGalaxy()
        {
            try { return AirShipLocalData.Instance?.getCurFleet()?.getGalaxyId(); } catch { return null; }
        }

        public static bool SameGalaxyAsFleet => galaxyId != null && galaxyId == FleetGalaxy();

        // Move the galaxy camera onto the fleet (the game's own camera move). Outside the galaxy scene, first enter the
        // galaxy the player's fleet is in the way the game's "至星系" button does (MainUI_Head.GoGalaxyFleet; from the
        // planet view the game's AirShipUIUtils.ShowGalaxyFleet); the camera follows once the fleet is in the scene.
        // False when there is nothing to show: no tracked caravan, or it is in another galaxy.
        public static bool Focus()
        {
            if (uid < 0 && !pointMode) return false;
            Find();
            var gs = GalaxyFieldScene.Instance;
            if (gs == null) return false;
            if (npc == null && !SameGalaxyAsFleet && !pointMode) return false;
            try
            {
                if (!InGalaxyView)
                {
                    if (gs.CurState == State.GalaxyExpedition) AirShipUIUtils.ShowGalaxyFleet();
                    else MainUI_Head.GoGalaxyFleet();
                }
                // Entering the scene loads the galaxy and builds its fleets over several frames.
                focusUntil = Time.unscaledTime + 20f;
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
            cam.ScrollGalaxyPos(pointMode ? PointWorld : npc.transform.position, gs.GetGalaxySceneDefaultDistance(), 0f, null);
        }

        // The fleet's live scene object, from the game's own table of the fleets it shows (GalaxyNPCFleetManager
        // .GetFleetByUid), looked up every frame. The game keeps hidden, unmoving GalaxyNPCFleet objects for reuse after
        // a view change (PrepareForRetention hides only the ship models), so searching the scene for any object with the
        // fleet's uid can pick one of those and leave the marker standing still.
        static void Find()
        {
            npc = null;
            if (uid < 0) return;
            try
            {
                var m = GalaxyNPCFleetManager.Instance;
                var f = m != null ? m.GetFleetByUid(uid) : null;
                if (f != null && f) npc = f;
            }
            catch { }
        }

        // The galaxy map's entry for the tracked fleet (star field): GalaxyMapView .../GalaxyInfo/<fleet uid>, with its dot
        // IconNPCImage. The map keeps the entry while the fleet is on its map and may switch the dot off while it is
        // off screen. Null when the map has no entry for the fleet.
        public static Transform MapEntry()
        {
            // The game keeps one galaxy map (GalaxyMapView clone) per galaxy it has shown; look in each, every second
            // for a new one, and keep the one holding the fleet.
            if (mapView != null && mapView)
            {
                var hit = mapView.transform.Find("DetailVisualRoot/DetailUIRoot/GalaxyInfo/" + uid);
                if (hit != null) return hit;
            }
            if (Time.unscaledTime < nextMapFind) return null;
            nextMapFind = Time.unscaledTime + 1f;
            mapView = null;
            foreach (var mv in UnityEngine.Object.FindObjectsOfType<GalaxyMapView>())
            {
                var hit = mv != null ? mv.transform.Find("DetailVisualRoot/DetailUIRoot/GalaxyInfo/" + uid) : null;
                if (hit != null) { mapView = mv; return hit; }
            }
            return null;
        }

        // The entry's dot while it is drawn, else null.
        static Renderer MapDot(Transform entry)
        {
            var t = entry != null ? entry.Find("IconNPCImage") : null;
            if (t == null || !t.gameObject.activeInHierarchy) return null;
            var r = t.GetComponent<Renderer>();
            if (r == null || !r.enabled) return null;
            var sc = t.lossyScale;
            if (Mathf.Abs(sc.x) < 1e-6f || Mathf.Abs(sc.y) < 1e-6f) return null;
            return r;
        }

        public static string MapEntryState()
        {
            var e = MapEntry();
            if (e == null) return "no map entry";
            var t = e.Find("IconNPCImage");
            var r = t != null ? t.GetComponent<Renderer>() : null;
            return "entry active " + e.gameObject.activeInHierarchy + ", dot " + (t == null ? "missing" : "active " + t.gameObject.activeInHierarchy + " renderer " + (r != null && r.enabled) + " scale " + t.lossyScale.ToString("F3"));
        }

        // Any ship model of the fleet drawn (mesh or sprite renderers under it, on and active).
        static readonly System.Collections.Generic.List<Renderer> renderers = new System.Collections.Generic.List<Renderer>();
        public static bool ShipsShown(GalaxyNPCFleet f)
        {
            f.GetComponentsInChildren(false, renderers);
            foreach (var r in renderers)
                if (r.enabled && (r is MeshRenderer || r is SkinnedMeshRenderer || r is SpriteRenderer)) return true;
            return false;
        }

        // Scene objects carrying the tracked fleet's uid (diagnostic for the self-test).
        public static int SceneObjects()
        {
            int n = 0;
            foreach (var f in UnityEngine.Object.FindObjectsOfType<GalaxyNPCFleet>()) if (f != null && f.FleetUId == uid) n++;
            return n;
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

        // Screen direction to a point behind the camera (its projection is mirrored there): the offset on the galaxy
        // plane from where the camera looks, split along the camera's right and its forward laid flat on the plane.
        public static Vector2 PlaneDirection(Camera cam, Vector3 target)
        {
            var t = cam.transform;
            Vector3 n = Vector3.forward;                         // the galaxy plane is z = const (fleets sit at z 0)
            Vector3 f = t.forward;
            float denom = Vector3.Dot(f, n);
            Vector3 look = Mathf.Abs(denom) > 1e-4f ? t.position + f * (Vector3.Dot(target - t.position, n) / denom) : t.position;
            Vector3 off = target - look;
            Vector3 up = Vector3.ProjectOnPlane(t.up, n); Vector3 right = Vector3.ProjectOnPlane(t.right, n);
            if (up.sqrMagnitude < 1e-6f) up = Vector3.ProjectOnPlane(f, n);
            return new Vector2(Vector3.Dot(off, right.normalized), Vector3.Dot(off, up.normalized));
        }

        static readonly List<RectTransform> segs = new List<RectTransform>(), dots = new List<RectTransform>();
        public static int PathSegmentsShown;

        static RectTransform Piece(List<RectTransform> pool, int i, string kind)
        {
            while (pool.Count <= i)
            {
                var rt = Kit.New(kind, root);
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
                var im = rt.gameObject.AddComponent<Image>();
                im.sprite = Theme.White; im.raycastTarget = false;
                im.color = kind == "Seg" ? new Color(Theme.Accent.r, Theme.Accent.g, Theme.Accent.b, 0.55f) : Theme.Accent;
                rt.SetAsFirstSibling();
                pool.Add(rt);
            }
            return pool[i];
        }

        static void HidePath()
        {
            foreach (var r in segs) if (r.gameObject.activeSelf) r.gameObject.SetActive(false);
            foreach (var r in dots) if (r.gameObject.activeSelf) r.gameObject.SetActive(false);
            PathSegmentsShown = 0;
        }

        // Galaxy-local points of the tracked fleet's galaxy, projected with the game's camera; parts behind the camera
        // are left out.
        static void DrawPath(Camera cam, List<Vector2> pts)
        {
            int si = 0, di = 0;
            Vector2 prev = default; bool havePrev = false;
            for (int i = 0; i < pts.Count; i++)
            {
                var sp = cam.WorldToScreenPoint(LocalToWorld(galaxyId, pts[i]));
                if (sp.z <= 0) { havePrev = false; continue; }
                Vector2 local;
                RectTransformUtility.ScreenPointToLocalPointInRectangle(root, new Vector2(sp.x, sp.y), null, out local);
                if (i > 0)
                {
                    var d = Piece(dots, di++, "Dot");
                    d.gameObject.SetActive(true); d.sizeDelta = new Vector2(8, 8); d.anchoredPosition = local;
                }
                if (havePrev)
                {
                    var seg = Piece(segs, si++, "Seg");
                    var v = local - prev;
                    seg.gameObject.SetActive(true);
                    seg.sizeDelta = new Vector2(v.magnitude, 3);
                    seg.anchoredPosition = (local + prev) / 2;
                    seg.localRotation = Quaternion.Euler(0, 0, Mathf.Atan2(v.y, v.x) * Mathf.Rad2Deg);
                }
                prev = local; havePrev = true;
            }
            for (int i = si; i < segs.Count; i++) if (segs[i].gameObject.activeSelf) segs[i].gameObject.SetActive(false);
            for (int i = di; i < dots.Count; i++) if (dots[i].gameObject.activeSelf) dots[i].gameObject.SetActive(false);
            PathSegmentsShown = si;
        }

        // The name label's text lies inside the screen (for the self-test; the overlay canvas works in screen pixels).
        public static bool LabelInside(out string where)
        {
            where = "no label";
            if (label == null || !label.gameObject.activeInHierarchy) return false;
            label.ForceMeshUpdate();
            var b = label.textBounds;
            var t = label.rectTransform;
            Vector3 a0 = t.TransformPoint(b.min), a1 = t.TransformPoint(b.max);
            where = "text from " + ((Vector2)a0).ToString("F0") + " to " + ((Vector2)a1).ToString("F0") + " of " + Screen.width + "x" + Screen.height;
            return a0.x >= -1 && a0.y >= -1 && a1.x <= Screen.width + 1 && a1.y <= Screen.height + 1;
        }

        // Star field without the fleet's dot.
        static string WhyNotOnStarField()
        {
            if (!SameGalaxyAsFleet && npc == null) return "它在 " + galaxyName + "，不在你舰队所在的星系；舰队到那个星系、进入星系画面后它才会出现";
            return "星域图上现在没有画它（游戏只在进过星系画面后才在星域图上画 NPC 舰队的点；或者它已经离开）。按左上角“至星系”（F11）进入星系画面，或在“商队”页点“定位追踪的商队”";
        }

        // Why the marker is not drawn, and what to do.
        public static string WhyNotShown(bool found, bool view)
        {
            if (!SameGalaxyAsFleet && !found) return "它在 " + galaxyName + "，不在你舰队所在的星系；舰队到那个星系、进入星系画面后它才会出现";
            if (!view) return "进入星系画面才看得到（左上角“至星系”，F11）。“商队”页的“定位追踪的商队”会自动进去";
            return "星系画面里还没有它（刚进入时要等几秒；一直没有就是它已经离开或在跨星系航行）";
        }

        // Every frame from TradeHelperBehaviour.Update (main thread).
        public static void Tick()
        {
            if (uid < 0 && !pointMode) return;
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
            // The game keeps a fleet's object, hidden and still, when the player leaves the galaxy view
            // (GalaxyNPCFleetManager.RetainForReuse hides only its ship models), and hides its ships at other times.
            // Mark it only while its ships are drawn.
            InStarField = gs != null && gs.CurState == State.GalaxyField;
            if (pointMode)
            {
                // Shown where the NPC fleets of that galaxy are laid out: its galaxy view, or the star field after it.
                var pm = GalaxyNPCFleetManager.Instance;
                Found = (pm != null && pm.GetGalaxyId() == galaxyId) || (InStarField && gs != null && gs.GetGalaxyPos(galaxyId) != Vector3.zero);
            }
            var entry = InStarField && !pointMode ? MapEntry() : null;
            icon = MapDot(entry);
            UsingMapDot = icon != null;
            // Star field: on the map's dot; when the map keeps the fleet's entry but has its dot off (off screen), on the
            // fleet's object, which is where the dot sits (checked by the self-test).
            Shown = pointMode ? Found : InStarField ? UsingMapDot || (entry != null && entry.gameObject.activeInHierarchy && Found && !npc.HasPendingRemove) : Found && ShipsShown(npc);
            bool view = InGalaxyView || InStarField;
            // A requested camera move happens once the galaxy view is up (it may be opening).
            if (focusUntil > 0 && Shown && InGalaxyView)
            {
                try { DoFocus(); } catch (Exception e) { Debug.LogWarning("[TradeHelper] tracker focus: " + e.Message); }
                focusUntil = 0;
            }
            else if (focusUntil > 0 && Time.unscaledTime > focusUntil) focusUntil = 0;
            if (!Shown || !view || cam == null || !cam.isActiveAndEnabled)
            {
                HidePath();
                marker.gameObject.SetActive(false);
                OnScreen = false;
                Status = "追踪中：" + name + "：" + (pointMode ? "进入 " + galaxyName + " 的星系画面才标得出（F11）" : InStarField ? WhyNotOnStarField() : Found && view && !Shown ? "它的船现在没显示（游戏把它藏起来了：多半正在进出跃迁门、停靠或刚离开），先不画标记" : WhyNotShown(Found, view));
                info.text = Status;
                return;
            }
            Vector3 target = pointMode ? PointWorld : UsingMapDot ? icon.bounds.center : npc.transform.position;
            Vector3 sp = cam.WorldToScreenPoint(target);
            Projected = sp;
            bool behind = sp.z <= 0;
            float w = Screen.width, h = Screen.height, m = 48;
            OnScreen = !behind && sp.x >= 0 && sp.x <= w && sp.y >= 0 && sp.y <= h;
            if (InStarField && !UsingMapDot && OnScreen && !pointMode)
            {
                // On screen the map would draw its dot; without one the game is not showing the fleet there.
                marker.gameObject.SetActive(false);
                OnScreen = false; Shown = false;
                Status = "追踪中：" + name + "：" + WhyNotOnStarField();
                info.text = Status;
                return;
            }
            Vector2 p;
            if (OnScreen) p = new Vector2(sp.x, sp.y);
            else
            {
                // Off screen: on the edge, along the line from the screen centre towards the fleet.
                Vector2 c = new Vector2(w / 2f, h / 2f), d = behind ? PlaneDirection(cam, target) : new Vector2(sp.x, sp.y) - c;
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
            // The name stays inside the screen: centred over the ring, or growing inwards from a side edge.
            var lr = label.rectTransform;
            float lx = p.x / Mathf.Max(1f, w);
            if (!OnScreen && lx < 0.2f) { lr.anchorMin = lr.anchorMax = new Vector2(0f, 1f); lr.pivot = new Vector2(0f, 0f); label.alignment = TextAlignmentOptions.BottomLeft; }
            else if (!OnScreen && lx > 0.8f) { lr.anchorMin = lr.anchorMax = new Vector2(1f, 1f); lr.pivot = new Vector2(1f, 0f); label.alignment = TextAlignmentOptions.BottomRight; }
            else { lr.anchorMin = lr.anchorMax = new Vector2(0.5f, 1f); lr.pivot = new Vector2(0.5f, 0f); label.alignment = TextAlignmentOptions.Bottom; }
            lr.anchoredPosition = new Vector2(0, 4);
            // Off screen near the top edge the name goes under the ring instead (the top has the game's bars).
            if (!OnScreen && p.y > h - 120) { lr.anchorMin = new Vector2(lr.anchorMin.x, 0f); lr.anchorMax = new Vector2(lr.anchorMax.x, 0f); lr.pivot = new Vector2(lr.pivot.x, 1f); lr.anchoredPosition = new Vector2(0, -4); }
            label.text = OnScreen ? name : Arrow(Direction) + " " + name;
            // The path it still has to fly (its route in the game's path module), so it can be met further along.
            List<Vector2> ahead = null;
            if (!pointMode) { try { ahead = Motion.PathAhead(uid); } catch { } }
            if (ahead != null) DrawPath(cam, ahead); else HidePath();
            Status = OnScreen ? "追踪中：" + name : "追踪中：" + name + "（在屏幕外）";
            info.text = OnScreen ? "" : "追踪中：" + name + " 在屏幕外，按箭头方向拖动或缩小地图";
        }
    }
}
