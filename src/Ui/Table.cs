using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TradeHelper.Ui
{
    public class Column<T>
    {
        public string title;
        public float width;
        public Func<T, string> text;
        public Func<T, IComparable> key;     // null = not sortable
        public Func<T, Color> color;
        public TextAlignmentOptions align = TextAlignmentOptions.MidlineLeft;
        public bool numeric;
        // Button cell
        public Func<T, string> buttonText;
        public Func<T, bool> buttonEnabled;
        public Func<T, bool> buttonPrimary;
        public Action<T> onButton;
        // Checkbox cell
        public Func<T, bool> checkGet;
        public Action<T, bool> checkSet;
    }

    // Virtualised table: only the rows that fit on screen exist; scrolling rebinds them. Click a header to sort by
    // that column (click again to reverse).
    public class Table<T>
    {
        public readonly List<Column<T>> columns;
        public Func<T, bool> filter;
        public Action<T> onRowClick;
        public Func<T, bool> isSelected;
        public Action<int, bool> sortChanged;
        public int sortCol;
        public bool desc;
        public List<T> all = new List<T>();
        public readonly List<T> view = new List<T>();

        const float RowH = 30f, HeadH = 32f;
        readonly RectTransform root, content, viewport;
        readonly ScrollRect scroll;
        readonly List<RowUi> rows = new List<RowUi>();
        readonly List<TextMeshProUGUI> headers = new List<TextMeshProUGUI>();
        readonly TextMeshProUGUI empty;
        public string emptyText = "没有数据";

        class RowUi
        {
            public RectTransform rt;
            public Image bg;
            public TextMeshProUGUI[] cells;
            public Kit.Btn[] buttons;
            public Kit.Check[] checks;
            public int index = -1;
        }

        public Table(Transform parent, float x, float y, float w, float h, List<Column<T>> cols, int sortCol, bool desc)
        {
            columns = cols;
            this.sortCol = sortCol; this.desc = desc;
            root = Kit.Place(Kit.New("Table", parent), x, y, w, h);
            var head = Kit.Place(Kit.New("Head", root), 0, 0, w, HeadH);
            Kit.Img(head, Theme.White, Theme.Hex(0x000000, 0.35f));
            float cx = 8;
            for (int i = 0; i < cols.Count; i++)
            {
                int ci = i;
                var c = cols[i];
                var hrt = Kit.Place(Kit.New("H", head), cx, 0, c.width, HeadH);
                var t = Kit.Txt(hrt, c.title, 15, Theme.Text2, c.align);
                Kit.Stretch(t.rectTransform, 4, 0, 4, 0);
                headers.Add(t);
                if (c.key != null)
                {
                    var hit = Kit.Img(hrt, Theme.White, new Color(0, 0, 0, 0), true);
                    var b = hrt.gameObject.AddComponent<Button>();
                    b.targetGraphic = hit;
                    b.onClick.AddListener(() => { if (sortCol == ci) desc = !desc; else { sortCol = ci; desc = c.numeric; } Resort(); sortChanged?.Invoke(sortCol, desc); });
                }
                cx += c.width;
            }
            var line = Kit.Place(Kit.New("Line", root), 0, HeadH, w, 1);
            Kit.Img(line, Theme.White, Theme.Line);

            viewport = Kit.Place(Kit.New("View", root), 0, HeadH + 1, w, h - HeadH - 1);
            Kit.Img(viewport, Theme.White, new Color(0, 0, 0, 0), true);
            viewport.gameObject.AddComponent<RectMask2D>();
            content = Kit.New("Content", viewport);
            content.anchorMin = new Vector2(0, 1); content.anchorMax = new Vector2(1, 1); content.pivot = new Vector2(0, 1);
            content.sizeDelta = new Vector2(0, 0);
            scroll = viewport.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false; scroll.viewport = viewport; scroll.content = content;
            scroll.movementType = ScrollRect.MovementType.Clamped; scroll.scrollSensitivity = 45; scroll.inertia = false;
            scroll.onValueChanged.AddListener(_ => Bind());
            int visible = Mathf.CeilToInt((h - HeadH) / RowH) + 2;
            for (int r = 0; r < visible; r++) rows.Add(MakeRow(w));
            empty = Kit.TxtAt(viewport, emptyText, 0, 20, w, 30, 16, Theme.Text2, TextAlignmentOptions.Center);
            UpdateHeaders();
        }

        RowUi MakeRow(float w)
        {
            var r = new RowUi();
            r.rt = Kit.New("Row", content);
            r.rt.sizeDelta = new Vector2(w, RowH);
            r.bg = Kit.Img(r.rt, Theme.White, Theme.Row, true);
            var rowBtn = r.rt.gameObject.AddComponent<Button>();
            rowBtn.targetGraphic = r.bg;
            rowBtn.transition = Selectable.Transition.None;
            rowBtn.onClick.AddListener(() => { if (r.index >= 0 && r.index < view.Count && onRowClick != null) { onRowClick(view[r.index]); Bind(); } });
            r.cells = new TextMeshProUGUI[columns.Count];
            r.buttons = new Kit.Btn[columns.Count];
            r.checks = new Kit.Check[columns.Count];
            float cx = 8;
            for (int i = 0; i < columns.Count; i++)
            {
                var c = columns[i];
                if (c.onButton != null)
                {
                    int ci = i;
                    r.buttons[i] = Kit.Button(r.rt, "", cx + 2, 2, c.width - 6, RowH - 4, () => { if (r.index >= 0 && r.index < view.Count) { columns[ci].onButton(view[r.index]); Bind(); } }, false, 14);
                }
                else if (c.checkGet != null)
                {
                    int ci = i;
                    r.checks[i] = Kit.Toggle(r.rt, "", cx + 4, 1, c.width - 6, false, v => { if (r.index >= 0 && r.index < view.Count) columns[ci].checkSet(view[r.index], v); });
                }
                else
                {
                    var t = Kit.Txt(r.rt, "", 15, Theme.Text, c.align, c.numeric ? Theme.NumFont : Theme.Font);
                    Kit.Place(t.rectTransform, cx + 4, 0, c.width - 8, RowH);
                    r.cells[i] = t;
                }
                cx += c.width;
            }
            return r;
        }

        public void SetData(List<T> data)
        {
            all = data ?? new List<T>();
            Resort();
        }

        public void Resort()
        {
            view.Clear();
            foreach (var x in all) if (filter == null || filter(x)) view.Add(x);
            if (sortCol >= 0 && sortCol < columns.Count && columns[sortCol].key != null)
            {
                var k = columns[sortCol].key;
                var keys = new Dictionary<int, IComparable>();
                var idx = new List<int>();
                for (int i = 0; i < view.Count; i++) { idx.Add(i); keys[i] = SafeKey(k, view[i]); }
                // Stable sort so ties keep the previous order.
                var sorted = new List<int>(idx);
                MergeSort(sorted, (a, b) =>
                {
                    int c = Compare(keys[a], keys[b]);
                    return desc ? -c : c;
                });
                var copy = new List<T>(view);
                for (int i = 0; i < sorted.Count; i++) view[i] = copy[sorted[i]];
            }
            content.sizeDelta = new Vector2(0, view.Count * RowH);
            UpdateHeaders();
            Bind();
        }

        static IComparable SafeKey(Func<T, IComparable> k, T x)
        {
            try { return k(x); } catch { return null; }
        }

        static int Compare(IComparable a, IComparable b)
        {
            if (a == null && b == null) return 0;
            if (a == null) return 1;   // unknown values go last in both directions
            if (b == null) return -1;
            try { return a.CompareTo(b); } catch { return 0; }
        }

        static void MergeSort(List<int> a, Comparison<int> cmp)
        {
            if (a.Count < 2) return;
            var tmp = new int[a.Count];
            var arr = a.ToArray();
            Split(arr, tmp, 0, arr.Length, cmp);
            for (int i = 0; i < arr.Length; i++) a[i] = arr[i];
        }

        static void Split(int[] a, int[] tmp, int lo, int hi, Comparison<int> cmp)
        {
            if (hi - lo < 2) return;
            int mid = (lo + hi) / 2;
            Split(a, tmp, lo, mid, cmp); Split(a, tmp, mid, hi, cmp);
            int i = lo, j = mid, k = lo;
            while (i < mid && j < hi) tmp[k++] = cmp(a[j], a[i]) < 0 ? a[j++] : a[i++];
            while (i < mid) tmp[k++] = a[i++];
            while (j < hi) tmp[k++] = a[j++];
            for (k = lo; k < hi; k++) a[k] = tmp[k];
        }

        void UpdateHeaders()
        {
            for (int i = 0; i < headers.Count; i++)
            {
                var c = columns[i];
                headers[i].text = c.title + (i == sortCol && c.key != null ? (desc ? " ▼" : " ▲") : "");
                headers[i].color = i == sortCol ? Theme.Accent : Theme.Text2;
            }
        }

        public void Bind()
        {
            float y = content.anchoredPosition.y;
            int first = Mathf.Max(0, Mathf.FloorToInt(y / RowH));
            empty.gameObject.SetActive(view.Count == 0);
            empty.text = emptyText;
            for (int r = 0; r < rows.Count; r++)
            {
                var row = rows[r];
                int i = first + r;
                if (i >= view.Count) { if (row.rt.gameObject.activeSelf) row.rt.gameObject.SetActive(false); row.index = -1; continue; }
                if (!row.rt.gameObject.activeSelf) row.rt.gameObject.SetActive(true);
                row.index = i;
                row.rt.anchoredPosition = new Vector2(0, -i * RowH);
                var item = view[i];
                bool sel = isSelected != null && isSelected(item);
                row.bg.color = sel ? Theme.RowSel : (i % 2 == 0 ? Theme.Row : Theme.RowAlt);
                for (int c = 0; c < columns.Count; c++)
                {
                    var col = columns[c];
                    try
                    {
                        if (row.buttons[c] != null)
                        {
                            row.buttons[c].Text = col.buttonText != null ? col.buttonText(item) : "";
                            row.buttons[c].Interactable = col.buttonEnabled == null || col.buttonEnabled(item);
                            if (col.buttonPrimary != null) row.buttons[c].Primary = col.buttonPrimary(item);
                        }
                        else if (row.checks[c] != null) row.checks[c].On = col.checkGet(item);
                        else
                        {
                            row.cells[c].text = col.text != null ? col.text(item) : "";
                            row.cells[c].color = col.color != null ? col.color(item) : Theme.Text;
                        }
                    }
                    catch (Exception e) { if (row.cells[c] != null) row.cells[c].text = "!"; Debug.LogWarning("[TradeHelper] cell: " + e.Message); }
                }
            }
        }

        public void SetActive(bool on) => root.gameObject.SetActive(on);
    }
}
