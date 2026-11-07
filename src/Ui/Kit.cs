using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TradeHelper.Ui
{
    // Small uGUI builders that copy the styles of the game's trade screen (pagetradeinventory prefab):
    //   buttons   = white rounded sprite, ColorTint normal #C6C9CA / hover #E6EAEC / pressed #75777A, dark label
    //   primary   = yellow common_flame_btnbg_1, dark label
    //   tabs      = no background; selected tab gets a light plate + dark label + underline
    //   dropdowns = common_flame_mood_46 tinted #282723 (hover #535657), 1px #75777A frame, light label, arrow
    //   inputs    = common_flame_jiaonangbg_28h_100 outline pill, pencil icon #75777A
    // Everything is positioned absolutely from the top-left of its parent (no layout groups).
    public static class Kit
    {
        public static RectTransform New(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = 5; // UI
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            return rt;
        }

        public static RectTransform Place(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
            return rt;
        }

        public static RectTransform Stretch(RectTransform rt, float l = 0, float t = 0, float r = 0, float b = 0)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0, 1);
            rt.offsetMin = new Vector2(l, b); rt.offsetMax = new Vector2(-r, -t);
            return rt;
        }

        public static Image Img(RectTransform rt, Sprite s, Color c, bool raycast = false)
        {
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = s;
            img.color = c;
            img.raycastTarget = raycast;
            if (s != null && s.border != Vector4.zero) img.type = Image.Type.Sliced;
            return img;
        }

        // Single-line label. Text that does not fit is shrunk (down to minSize when given) and then cut with "…".
        public static TextMeshProUGUI Txt(Transform parent, string text, float size, Color c, TextAlignmentOptions align, TMP_FontAsset font = null, float minSize = 0)
        {
            var rt = New("T", parent);
            var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
            t.font = font ?? Theme.Font;
            t.fontSize = size;
            t.color = c;
            t.alignment = align;
            t.enableWordWrapping = false;
            t.overflowMode = TextOverflowModes.Ellipsis;
            t.raycastTarget = false;
            t.richText = true;
            t.margin = Vector4.zero;
            if (minSize > 0 && minSize < size)
            {
                t.enableAutoSizing = true;
                t.fontSizeMin = minSize;
                t.fontSizeMax = size;
            }
            t.text = text;
            return t;
        }

        public static TextMeshProUGUI TxtAt(Transform parent, string text, float x, float y, float w, float h, float size, Color c, TextAlignmentOptions align = TextAlignmentOptions.MidlineLeft)
        {
            var t = Txt(parent, text, size, c, align);
            Place(t.rectTransform, x, y, w, h);
            return t;
        }

        // A text that may be cut short ("…"): when the pointer is over it and it does not fit, the full text shows in a
        // box next to the pointer (Tooltip). The text takes pointer hover only; clicks and scrolling still reach what is
        // under it (events bubble to the parents).
        public static TextMeshProUGUI Tip(TextMeshProUGUI t)
        {
            if (t == null) return t;
            t.raycastTarget = true;
            var h = t.GetComponent<TipOnHover>() ?? t.gameObject.AddComponent<TipOnHover>();
            h.text = t;
            return t;
        }

        static ColorBlock Tint(Color normal, Color hover, Color pressed)
        {
            var cb = ColorBlock.defaultColorBlock;
            cb.normalColor = normal; cb.highlightedColor = hover; cb.pressedColor = pressed; cb.selectedColor = normal;
            cb.disabledColor = new Color(normal.r, normal.g, normal.b, normal.a * 0.45f);
            cb.colorMultiplier = 1; cb.fadeDuration = 0.08f;
            return cb;
        }

        public class Btn
        {
            public Button button;
            public Image bg;
            public TextMeshProUGUI label;
            bool primary;
            public bool Primary
            {
                get => primary;
                set
                {
                    primary = value;
                    if (value)
                    {
                        bg.sprite = Theme.ButtonYellow; bg.type = Theme.ButtonYellow.border != Vector4.zero ? Image.Type.Sliced : Image.Type.Simple;
                        bg.color = Theme.GameSprites ? Color.white : Theme.Accent;
                        button.colors = Tint(Color.white, Theme.Hex(0xFFF2C8, 1f), Theme.Hex(0xC8C8C8, 1f));
                    }
                    else
                    {
                        bg.sprite = Theme.Box; bg.type = Image.Type.Sliced;
                        bg.color = Color.white;
                        button.colors = Tint(Theme.BtnLight, Theme.Text, Theme.Line2);
                    }
                    label.color = Theme.TextDark;
                }
            }
            public bool Interactable { set { button.interactable = value; label.alpha = value ? 1f : 0.45f; } }
            public string Text { set { label.text = value; } }
        }

        public static Btn Button(Transform parent, string text, float x, float y, float w, float h, Action onClick, bool primary = false, float size = 16)
        {
            var rt = Place(New("Btn", parent), x, y, w, h);
            var b = new Btn();
            b.bg = Img(rt, Theme.Box, Color.white, true);
            b.button = rt.gameObject.AddComponent<Button>();
            b.button.targetGraphic = b.bg;
            b.label = Txt(rt, text, size, Theme.TextDark, TextAlignmentOptions.Center, null, Math.Max(10, size - 5));
            Stretch(b.label.rectTransform, 8, 0, 8, 0);
            b.Primary = primary;
            if (onClick != null) b.button.onClick.AddListener(() => { try { onClick(); } catch (Exception e) { Debug.LogWarning("[TradeHelper] click: " + e); } });
            return b;
        }

        // Tab in the game's Tabs_Bar style.
        public class Tab
        {
            public Button button;
            public Image plate, underline;
            public TextMeshProUGUI label;
            public bool Selected
            {
                set
                {
                    plate.enabled = value;
                    underline.enabled = value;
                    label.color = value ? Theme.TextDark : Theme.Text;
                }
            }
        }

        public static Tab TabButton(Transform parent, string text, float x, float y, float w, float h, Action onClick)
        {
            var rt = Place(New("Tab", parent), x, y, w, h);
            var t = new Tab();
            var hit = Img(rt, Theme.White, Color.white, true);
            t.button = rt.gameObject.AddComponent<Button>();
            t.button.targetGraphic = hit;
            t.button.colors = Tint(Theme.Hex(0xC6C9CA, 0f), Theme.Hex(0xC6C9CA, 0.10f), Theme.Hex(0xC6C9CA, 0.20f));
            var plate = Stretch(New("Plate", rt), 0, 0, 0, 4);
            t.plate = Img(plate, Theme.Box, Theme.Text);
            var ul = Place(New("Underline", rt), 0, h - 4, w, 4);
            t.underline = Img(ul, Theme.White, Theme.Accent);
            t.label = Txt(rt, text, 19, Theme.Text, TextAlignmentOptions.Center, null, 14);
            Stretch(t.label.rectTransform, 6, 0, 6, 4);
            t.Selected = false;
            if (onClick != null) t.button.onClick.AddListener(() => { try { onClick(); } catch (Exception e) { Debug.LogWarning("[TradeHelper] tab: " + e); } });
            return t;
        }

        // Checkbox: framed box + yellow mark + label.
        public class Check
        {
            public Image mark;
            public TextMeshProUGUI label;
            bool on;
            public Action<bool> changed;
            public bool On { get => on; set { on = value; mark.enabled = value; } }
        }

        public static Check Toggle(Transform parent, string text, float x, float y, float w, bool on, Action<bool> changed)
        {
            var rt = Place(New("Check", parent), x, y, w, 28);
            var hit = Img(rt, Theme.White, new Color(0, 0, 0, 0), true);
            var box = Place(New("Box", rt), 0, 5, 18, 18);
            Img(box, Theme.Box, Theme.DropBg);
            var frame = Stretch(New("Frame", box));
            Img(frame, Theme.Frame, Theme.Line2);
            var mk = Place(New("Mark", box), 4, 4, 10, 10);
            var c = new Check { mark = Img(mk, Theme.Box, Theme.Accent), changed = changed };
            c.label = TxtAt(rt, text, 26, 0, w - 26, 28, 16, Theme.Text);
            c.label.enableAutoSizing = true; c.label.fontSizeMin = 11; c.label.fontSizeMax = 16;
            c.On = on;
            var btn = rt.gameObject.AddComponent<Button>();
            btn.targetGraphic = hit;
            btn.transition = Selectable.Transition.None;
            btn.onClick.AddListener(() => { c.On = !c.On; try { c.changed?.Invoke(c.On); } catch (Exception e) { Debug.LogWarning("[TradeHelper] toggle: " + e); } });
            return c;
        }

        public static TMP_InputField Input(Transform parent, string placeholder, float x, float y, float w, float h, Action<string> changed)
        {
            var rt = Place(New("Input", parent), x, y, w, h);
            var bg = Img(rt, Theme.InputPill, Color.white, true);
            if (Theme.Input != null)
            {
                var icon = Place(New("Icon", rt), w - 30, (h - 18) / 2, 24, 18);
                Img(icon, Theme.Input, Theme.Line2).preserveAspect = true;
            }
            var area = Stretch(New("Text Area", rt), 12, 2, Theme.Input != null ? 34 : 12, 2);
            area.gameObject.AddComponent<RectMask2D>();
            var ph = Txt(area, placeholder, 16, Theme.Text2, TextAlignmentOptions.MidlineLeft);
            Stretch(ph.rectTransform);
            var tx = Txt(area, "", 16, Theme.Text, TextAlignmentOptions.MidlineLeft);
            Stretch(tx.rectTransform);
            tx.richText = false;
            tx.overflowMode = TextOverflowModes.Overflow;
            var input = rt.gameObject.AddComponent<TMP_InputField>();
            input.targetGraphic = bg;
            input.colors = Tint(Theme.Text2, Theme.Text, Theme.Text2);
            input.textViewport = area;
            input.textComponent = tx;
            input.placeholder = ph;
            input.fontAsset = Theme.Font;
            input.pointSize = 16;
            input.lineType = TMP_InputField.LineType.SingleLine;
            input.caretColor = Theme.Accent;
            input.customCaretColor = true;
            input.selectionColor = new Color(Theme.Accent.r, Theme.Accent.g, Theme.Accent.b, 0.35f);
            InputProbe.Watch(input, placeholder);
            if (changed != null) input.onValueChanged.AddListener(s => { try { changed(s); } catch (Exception e) { Debug.LogWarning("[TradeHelper] input: " + e); } });
            return input;
        }

        // Dropdown in the game's TMP_Dropdown style. The list lives on the window's top layer, with a transparent
        // click-catcher behind it so clicking anywhere else closes it.
        public class Drop
        {
            public Button button;
            public TextMeshProUGUI label;
            public List<string> options = new List<string>();
            public int index;
            public Action<int> changed;
            public string prefix;
            public RectTransform popupLayer;
            RectTransform popup, blocker;

            public void SetOptions(List<string> opts, int sel)
            {
                options = opts;
                index = Mathf.Clamp(sel, 0, Math.Max(0, opts.Count - 1));
                Refresh();
            }

            public void Refresh()
            {
                label.text = (string.IsNullOrEmpty(prefix) ? "" : "<color=#A4A5A6>" + prefix + "</color>") + (options.Count > 0 ? options[index] : "");
            }

            public void Toggle()
            {
                if (popup != null) { Close(); return; }
                CloseAll?.Invoke();
                var btnRt = (RectTransform)button.transform;
                var corners = new Vector3[4];
                btnRt.GetWorldCorners(corners);
                Vector2 local;
                RectTransformUtility.ScreenPointToLocalPointInRectangle(popupLayer, RectTransformUtility.WorldToScreenPoint(null, corners[0]), null, out local);
                var r = popupLayer.rect;

                blocker = Stretch(New("Blocker", popupLayer));
                var blkImg = Img(blocker, Theme.White, new Color(0, 0, 0, 0), true);
                var blkBtn = blocker.gameObject.AddComponent<Button>();
                blkBtn.targetGraphic = blkImg; blkBtn.transition = Selectable.Transition.None;
                blkBtn.onClick.AddListener(Close);

                float w = Math.Max(btnRt.rect.width, 200), rowH = 34;
                int count = options.Count;
                float h = Math.Min(count, 12) * rowH + 8;
                popup = New("Popup", popupLayer);
                popup.anchorMin = popup.anchorMax = new Vector2(0, 1);
                popup.pivot = new Vector2(0, 1);
                popup.anchoredPosition = new Vector2(local.x - r.xMin, local.y - r.yMax - 2);
                popup.sizeDelta = new Vector2(w, h);
                Img(popup, Theme.Box, Theme.DropBg, true);
                Img(Stretch(New("Frame", popup)), Theme.Frame, Theme.Line2);
                var view = Stretch(New("View", popup), 4, 4, 4, 4);
                view.gameObject.AddComponent<RectMask2D>();
                Img(view, Theme.White, new Color(0, 0, 0, 0), true);
                var content = New("Content", view);
                content.anchorMin = new Vector2(0, 1); content.anchorMax = new Vector2(1, 1); content.pivot = new Vector2(0, 1);
                content.sizeDelta = new Vector2(0, count * rowH);
                var sr = view.gameObject.AddComponent<ScrollRect>();
                sr.horizontal = false; sr.viewport = view; sr.content = content; sr.scrollSensitivity = 34; sr.movementType = ScrollRect.MovementType.Clamped;
                for (int i = 0; i < count; i++)
                {
                    int idx = i;
                    var row = New("Opt", content);
                    row.anchorMin = new Vector2(0, 1); row.anchorMax = new Vector2(1, 1); row.pivot = new Vector2(0, 1);
                    row.anchoredPosition = new Vector2(0, -i * rowH); row.sizeDelta = new Vector2(0, rowH);
                    var bg = Img(row, Theme.Box, Color.white, true);
                    var b = row.gameObject.AddComponent<Button>();
                    b.targetGraphic = bg;
                    b.colors = Tint(Theme.Hex(0xF4B30D, i == index ? 0.10f : 0f), Theme.Hex(0xF4B30D, 0.20f), Theme.Hex(0xF4B30D, 0.08f));
                    if (i == index && Theme.Halo != null)
                    {
                        var dot = Place(New("Dot", row), 8, (rowH - 12) / 2, 12, 12);
                        Img(dot, Theme.Halo, Theme.Accent);
                    }
                    var t = Txt(row, options[i], 16, i == index ? Theme.Accent : Theme.Text, TextAlignmentOptions.MidlineLeft, null, 12);
                    Stretch(t.rectTransform, 26, 0, 8, 0);
                    b.onClick.AddListener(() => { index = idx; Refresh(); Close(); try { changed?.Invoke(idx); } catch (Exception e) { Debug.LogWarning("[TradeHelper] drop: " + e); } });
                }
                popup.SetAsLastSibling();
            }

            public void Close()
            {
                if (popup != null) UnityEngine.Object.Destroy(popup.gameObject);
                if (blocker != null) UnityEngine.Object.Destroy(blocker.gameObject);
                popup = null; blocker = null;
            }

            public static Action CloseAll;
        }

        public static Drop Dropdown(Transform parent, RectTransform popupLayer, string prefix, float x, float y, float w, List<string> options, int sel, Action<int> changed)
        {
            var d = new Drop { prefix = prefix, popupLayer = popupLayer, changed = changed };
            var rt = Place(New("Dropdown", parent), x, y, w, 30);
            var bg = Img(rt, Theme.Pill, Color.white, true);
            Img(Stretch(New("Frame", rt)), Theme.Frame, Theme.Line2);
            d.button = rt.gameObject.AddComponent<Button>();
            d.button.targetGraphic = bg;
            d.button.colors = Tint(Theme.DropBg, Theme.Line, Theme.Hex(0x323435, 1f));
            d.label = Txt(rt, "", 16, Theme.Text, TextAlignmentOptions.MidlineLeft, null, 11);
            Stretch(d.label.rectTransform, 12, 0, 28, 0);
            var arrow = Txt(rt, "▼", 12, Theme.Text2, TextAlignmentOptions.Center);
            Place(arrow.rectTransform, w - 26, 0, 20, 30);
            d.button.onClick.AddListener(d.Toggle);
            d.SetOptions(options, sel);
            return d;
        }
    }

    // One of the panel's resize zones (edges = which sides it moves). See Window.AddResizeHandles.
    public class ResizeHandle : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        public const int Left = 1, Right = 2, Bottom = 4, Top = 8;
        public static readonly Color Idle = new Color(0, 0, 0, 0);
        public int edges;
        public Window window;
        bool dragging;
        Image img;

        Image Img => img != null ? img : (img = GetComponent<Image>());
        static Color Hover => new Color(Theme.Accent.r, Theme.Accent.g, Theme.Accent.b, 0.55f);

        public void OnPointerEnter(PointerEventData e) { if (Img != null) Img.color = Hover; }
        public void OnPointerExit(PointerEventData e) { if (Img != null && !dragging) Img.color = Idle; }
        public void OnBeginDrag(PointerEventData e) { dragging = true; window?.BeginResize(e.position); }
        public void OnDrag(PointerEventData e) { window?.DragResize(edges, e.position); }
        public void OnEndDrag(PointerEventData e)
        {
            dragging = false;
            if (Img != null) Img.color = Idle;
            window?.EndResize();
        }
    }

    public class DragHandle : MonoBehaviour, IDragHandler
    {
        public RectTransform target;
        public Canvas canvas;
        public void OnDrag(PointerEventData e)
        {
            if (target == null) return;
            float s = canvas != null ? canvas.scaleFactor : 1f;
            target.anchoredPosition += e.delta / Mathf.Max(0.01f, s);
        }
    }

    public class TipOnHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        public TextMeshProUGUI text;
        public string explain;          // always shown under the (full) text when set

        public static bool CutShort(TextMeshProUGUI t)
        {
            if (t == null || string.IsNullOrEmpty(t.text)) return false;
            if (t.isTextTruncated) return true;
            var r = t.rectTransform.rect;
            return !t.enableWordWrapping && t.preferredWidth > r.width + 1;
        }

        public void OnPointerEnter(PointerEventData e)
        {
            if (!string.IsNullOrEmpty(explain)) Tooltip.Show("<b>" + Strip(text != null ? text.text : "") + "</b>\n" + explain, e.position, this);
            else if (CutShort(text)) Tooltip.Show(text.text, e.position, this);
        }

        // A header's text without the sort arrow.
        static string Strip(string s) => (s ?? "").Replace(" ▼", "").Replace(" ▲", "");
        public void OnPointerExit(PointerEventData e) { Tooltip.Hide(this); }
        void OnDisable() { Tooltip.Hide(this); }
    }

    // One floating box on the panel's popup layer.
    public static class Tooltip
    {
        public static RectTransform layer;
        static RectTransform box;
        static TextMeshProUGUI label;
        static object owner;
        public static bool Visible => box != null && box.gameObject.activeInHierarchy;
        public static string Text => label != null ? label.text : null;
        const float MaxW = 640, Pad = 10;

        public static void Show(string s, Vector2 screen, object who)
        {
            if (layer == null) return;
            if (box == null || box.parent != layer)
            {
                box = Kit.New("Tooltip", layer);
                Kit.Img(box, Theme.Box ?? Theme.White, Theme.Hex(0x1C1B19, 0.97f));
                label = Kit.Txt(box, "", 15, Theme.Text, TextAlignmentOptions.TopLeft);
                label.enableWordWrapping = true;
                label.overflowMode = TextOverflowModes.Overflow;
                Kit.Stretch(label.rectTransform, Pad, Pad, Pad, Pad);
            }
            owner = who;
            label.text = s;
            float w = Mathf.Min(MaxW, label.GetPreferredValues(s, MaxW - 2 * Pad, 0).x + 2 * Pad + 2);
            float h = label.GetPreferredValues(s, w - 2 * Pad, 0).y + 2 * Pad + 2;
            Vector2 local;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(layer, screen, null, out local);
            var lr = layer.rect;
            // Layer pivot is its top-left corner: x grows right, y grows up (so negative downwards).
            float x = local.x + 18, y = local.y - 22;
            if (x + w > lr.width) x = Mathf.Max(0, local.x - w - 8);
            if (-y + h > lr.height) y = -Mathf.Max(0, lr.height - h);
            Kit.Place(box, x, -y, w, h);
            box.SetAsLastSibling();
            box.gameObject.SetActive(true);
        }

        public static void Hide(object who = null)
        {
            if (box == null || (who != null && who != owner)) return;
            box.gameObject.SetActive(false);
            owner = null;
        }
    }
}
