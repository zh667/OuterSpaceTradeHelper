using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TradeHelper.Ui
{
    // Small uGUI builders. Everything is positioned absolutely from the top-left of its parent (no layout groups),
    // which keeps rebuilds cheap.
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

        public static TextMeshProUGUI Txt(Transform parent, string text, float size, Color c, TextAlignmentOptions align, TMP_FontAsset font = null)
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
            t.text = text;
            return t;
        }

        public static TextMeshProUGUI TxtAt(Transform parent, string text, float x, float y, float w, float h, float size, Color c, TextAlignmentOptions align = TextAlignmentOptions.MidlineLeft)
        {
            var t = Txt(parent, text, size, c, align);
            Place(t.rectTransform, x, y, w, h);
            return t;
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
                    bg.sprite = value ? Theme.ButtonYellow : Theme.ButtonGray;
                    bg.color = Theme.GameSprites ? Color.white : (value ? Theme.Accent : Theme.Line);
                    label.color = value ? Theme.TextDark : Theme.Text;
                }
            }
            public bool Interactable { set { button.interactable = value; label.alpha = value ? 1f : 0.4f; } }
            public string Text { set { label.text = value; } }
        }

        public static Btn Button(Transform parent, string text, float x, float y, float w, float h, Action onClick, bool primary = false, float size = 16)
        {
            var rt = Place(New("Btn", parent), x, y, w, h);
            var b = new Btn();
            b.bg = Img(rt, Theme.ButtonGray, Color.white, true);
            b.button = rt.gameObject.AddComponent<Button>();
            b.button.targetGraphic = b.bg;
            var cb = b.button.colors; cb.highlightedColor = new Color(1.15f, 1.15f, 1.15f, 1f); cb.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f); cb.disabledColor = new Color(0.6f, 0.6f, 0.6f, 0.6f); b.button.colors = cb;
            b.label = Txt(rt, text, size, Theme.Text, TextAlignmentOptions.Center);
            Stretch(b.label.rectTransform, 6, 0, 6, 0);
            b.Primary = primary;
            if (onClick != null) b.button.onClick.AddListener(() => { try { onClick(); } catch (Exception e) { Debug.LogWarning("[TradeHelper] click: " + e); } });
            return b;
        }

        // Checkbox styled like the game's filter toggles: small framed box + label.
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
            Img(box, Theme.Frame, Theme.Line2);
            var mk = Place(New("Mark", box), 4, 4, 10, 10);
            var c = new Check { mark = Img(mk, Theme.White, Theme.Accent), changed = changed };
            c.label = TxtAt(rt, text, 24, 0, w - 24, 28, 16, Theme.Text);
            c.On = on;
            var btn = rt.gameObject.AddComponent<Button>();
            btn.targetGraphic = hit;
            btn.onClick.AddListener(() => { c.On = !c.On; try { c.changed?.Invoke(c.On); } catch (Exception e) { Debug.LogWarning("[TradeHelper] toggle: " + e); } });
            return c;
        }

        public static TMP_InputField Input(Transform parent, string placeholder, float x, float y, float w, float h, Action<string> changed)
        {
            var rt = Place(New("Input", parent), x, y, w, h);
            var bg = Img(rt, Theme.Frame, Theme.Line2, true);
            var area = Stretch(New("Text Area", rt), 8, 2, 8, 2);
            area.gameObject.AddComponent<RectMask2D>();
            var ph = Txt(area, placeholder, 16, Theme.Text2, TextAlignmentOptions.MidlineLeft);
            Stretch(ph.rectTransform);
            ph.fontStyle = FontStyles.Italic;
            var tx = Txt(area, "", 16, Theme.Text, TextAlignmentOptions.MidlineLeft);
            Stretch(tx.rectTransform);
            tx.richText = false;
            var input = rt.gameObject.AddComponent<TMP_InputField>();
            input.targetGraphic = bg;
            input.textViewport = area;
            input.textComponent = tx;
            input.placeholder = ph;
            input.fontAsset = Theme.Font;
            input.pointSize = 16;
            input.lineType = TMP_InputField.LineType.SingleLine;
            input.caretColor = Theme.Accent;
            input.selectionColor = new Color(Theme.Accent.r, Theme.Accent.g, Theme.Accent.b, 0.35f);
            if (changed != null) input.onValueChanged.AddListener(s => { try { changed(s); } catch (Exception e) { Debug.LogWarning("[TradeHelper] input: " + e); } });
            return input;
        }

        // Dropdown in the game's style ("默认排序 ▼" button that opens a list). The popup lives on the window's top
        // layer so it draws above tables.
        public class Drop
        {
            public Btn button;
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

            public void Refresh() { button.Text = prefix + (options.Count > 0 ? options[index] : "") + "  ▼"; }

            public void Toggle()
            {
                if (popup != null) { Close(); return; }
                CloseAll?.Invoke();
                var btnRt = (RectTransform)button.button.transform;
                var corners = new Vector3[4];
                btnRt.GetWorldCorners(corners);
                Vector2 local;
                RectTransformUtility.ScreenPointToLocalPointInRectangle(popupLayer, RectTransformUtility.WorldToScreenPoint(null, corners[0]), null, out local);
                // Transparent click-catcher behind the list: clicking anywhere else closes it.
                blocker = Stretch(New("Blocker", popupLayer));
                var blkImg = Img(blocker, Theme.White, new Color(0, 0, 0, 0), true);
                var blkBtn = blocker.gameObject.AddComponent<Button>();
                blkBtn.targetGraphic = blkImg; blkBtn.transition = Selectable.Transition.None;
                blkBtn.onClick.AddListener(Close);
                float w = Math.Max(btnRt.rect.width, 180), rowH = 30;
                int count = options.Count;
                float h = Math.Min(count, 14) * rowH + 8;
                popup = New("Popup", popupLayer);
                popup.anchorMin = popup.anchorMax = new Vector2(0, 1);
                popup.pivot = new Vector2(0, 1);
                // popupLayer is stretched over the window: convert from its pivot-centred local point.
                var r = popupLayer.rect;
                popup.anchoredPosition = new Vector2(local.x - r.xMin, local.y - r.yMax);
                popup.sizeDelta = new Vector2(w, h);
                Img(popup, Theme.Window, Theme.GameSprites ? Color.white : Theme.BgDark, true);
                var view = Stretch(New("View", popup), 4, 4, 4, 4);
                view.gameObject.AddComponent<RectMask2D>();
                var content = New("Content", view);
                content.anchorMin = new Vector2(0, 1); content.anchorMax = new Vector2(1, 1); content.pivot = new Vector2(0, 1);
                content.sizeDelta = new Vector2(0, count * rowH);
                var sr = view.gameObject.AddComponent<ScrollRect>();
                sr.horizontal = false; sr.viewport = view; sr.content = content; sr.scrollSensitivity = 30; sr.movementType = ScrollRect.MovementType.Clamped;
                Img(view, Theme.White, new Color(0, 0, 0, 0), true);
                for (int i = 0; i < count; i++)
                {
                    int idx = i;
                    var row = New("Opt", content);
                    row.anchorMin = new Vector2(0, 1); row.anchorMax = new Vector2(1, 1); row.pivot = new Vector2(0, 1);
                    row.anchoredPosition = new Vector2(0, -i * rowH); row.sizeDelta = new Vector2(0, rowH);
                    var bg = Img(row, Theme.White, i == index ? Theme.RowSel : new Color(0, 0, 0, 0), true);
                    var b = row.gameObject.AddComponent<Button>();
                    b.targetGraphic = bg;
                    var cb = b.colors; cb.highlightedColor = new Color(1, 1, 1, 1); b.colors = cb;
                    var t = Txt(row, options[i], 16, i == index ? Theme.Accent : Theme.Text, TextAlignmentOptions.MidlineLeft);
                    Stretch(t.rectTransform, 10, 0, 6, 0);
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
            d.button = Button(parent, "", x, y, w, 30, null, false, 15);
            d.button.label.alignment = TextAlignmentOptions.MidlineLeft;
            d.button.button.onClick.AddListener(d.Toggle);
            d.SetOptions(options, sel);
            return d;
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
}
