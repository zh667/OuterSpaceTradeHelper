using System;
using TMPro;
using UnityEngine;

namespace TradeHelper.Ui
{
    // The game's own look: fonts and sprites are loaded through the game's ResourceLoader (the same addresses its
    // trade screen uses); colours were read from the trade screen prefab. Anything that fails to load falls back
    // to a plain generated sprite in the same colour, so the panel always works.
    public static class Theme
    {
        public static TMP_FontAsset Font, NumFont;
        public static Sprite Window, Frame, ButtonGray, ButtonYellow, Tag, White, Box, Close, Filter, Arrow, Input, Pill, InputPill, Halo;
        public static bool GameSprites;

        public static readonly Color Bg = Hex(0x282723, 0.97f);
        public static readonly Color BgDark = Hex(0x1C1B19, 0.98f);
        public static readonly Color Row = Hex(0xFFFFFF, 0.035f);
        public static readonly Color RowAlt = Hex(0xFFFFFF, 0.0f);
        public static readonly Color RowSel = Hex(0xF4B30D, 0.18f);
        public static readonly Color Line = Hex(0x535657, 1f);
        public static readonly Color HeadBg = Hex(0x323435, 0.9f);
        public static readonly Color BtnLight = Hex(0xC6C9CA, 1f);
        public static readonly Color DropBg = Hex(0x282723, 1f);
        public static readonly Color Line2 = Hex(0x75777A, 1f);
        public static readonly Color Text = Hex(0xE6EAEC, 1f);
        public static readonly Color Text2 = Hex(0xA4A5A6, 1f);
        public static readonly Color TextDark = Hex(0x282723, 1f);
        public static readonly Color Accent = Hex(0xF4B30D, 1f);
        public static readonly Color Good = Hex(0x4AC37D, 1f);
        public static readonly Color Bad = Hex(0xD66079, 1f);
        public static readonly Color Warn = Hex(0xD8982F, 1f);

        static bool loaded;

        public static Color Hex(int rgb, float a) => new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, a);

        static Sprite Load(string folder, string file)
        {
            // Address casing is not visible in the bundles (they are lower-cased), so try the likely spellings.
            string[] folders = { folder, folder.ToLowerInvariant(), "UI_" + char.ToUpperInvariant(folder[3]) + folder.Substring(4) };
            foreach (var f in folders)
            {
                try
                {
                    var s = ResourceLoader.Load<Sprite>("UGUI/" + f + "/" + file);
                    if (s != null) return s;
                }
                catch { }
            }
            return null;
        }

        static Sprite Generated(int size, int border)
        {
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[size * size];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(255, 255, 255, 255);
            t.SetPixels32(px); t.Apply(false, true);
            return Sprite.Create(t, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(border, border, border, border));
        }

        public static void Load()
        {
            if (loaded) return;
            loaded = true;
            try { Font = ResourceStatic.shareFontSDF(); } catch { }
            if (Font == null)
                try { Font = ResourceLoader.Load<TMP_FontAsset>("Font/SourceHan-Medium SDF.asset"); } catch { }
            if (Font == null) Font = TMP_Settings.defaultFontAsset;
            try { NumFont = ResourceLoader.Load<TMP_FontAsset>("Font/KodeMono SDF.asset"); } catch { }
            if (NumFont == null) NumFont = Font;

            var plain = Generated(8, 3);
            White = Load("UI_pixel", "white.png") ?? plain;
            Window = Load("UI_flame", "common_flame_blackbg2_4cr1pxline.png");
            Frame = Load("UI_flame", "common_kuang_1px_3cr.png");
            ButtonGray = Load("UI_button", "common_flame_btnbg_2.png");
            ButtonYellow = Load("UI_button", "common_flame_btnbg_1.png");
            Tag = Load("UI_button", "common_flamebtn_tag1_true.png");
            Box = Load("UI_flame", "flame_white_4cr.png");
            Close = Load("UI_button", "common_btn_close1.png");
            Filter = Load("UI_icon", "common_icon_filter.png");
            Arrow = Load("UI_elm", "common_elm_arrow_2cr.png");
            Input = Load("UI_icon", "icon_input.png");
            Pill = Load("UI_flame", "common_flame_mood_46.png");
            InputPill = Load("UI_flame", "common_flame_jiaonangbg_28h_100.png");
            Halo = Load("UI_elm", "elm_halo.png");
            GameSprites = Window != null && ButtonGray != null;
            if (Window == null) Window = plain;
            if (Frame == null) Frame = plain;
            if (Box == null) Box = plain;
            if (ButtonGray == null) ButtonGray = plain;
            if (ButtonYellow == null) ButtonYellow = plain;
            if (Tag == null) Tag = plain;
            if (Pill == null) Pill = Box;
            if (InputPill == null) InputPill = Frame;
            Debug.Log("[TradeHelper] theme font=" + (Font != null ? Font.name : "null") + " num=" + (NumFont != null ? NumFont.name : "null") + " gameSprites=" + GameSprites + " pill=" + (Pill != plain) + " inputPill=" + (InputPill != Frame) + " halo=" + (Halo != null));
        }
    }
}
