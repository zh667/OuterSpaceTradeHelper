# 跑商助手 · Outer Space Trade Helper

《Outer Space / 秩序：新曙光》（Steam 3080070）的游戏内跑商助手模组。自用起步，GPL-3.0 开源。游戏内按 **F8** 打开。

## 功能
- **跑商建议**：各站之间扣两边税、算上买卖时价格变化后的最佳买卖站和数量；净赚、利润率、航程、每光年净赚，可按任意列排序，可按数据新鲜度、舰队距离、最低净赚、类别、关键字筛选。
- **卖库存**：母舰、基地、舰队货仓、DNA 库的东西卖到哪最值钱。
- **各站市场**：每个站的库存、买一个（含税）、卖一个（到手）、税率、能否买、违禁；价格标“实时”或“估”。
- **我的库存**：按位置/类别汇总。
- **后台补算**：选择要补算/初始化的空间站（含“一键初始化所有未开过的站”“保持实时”）。调用游戏自带的 `ReleasePendingSettlementBatch` 每帧算一点，效果等于你打开了那个站。
- **导出**：`latest.json` 和截图默认在 `%USERPROFILE%/AppData/LocalLow/CatStudio/Outer Space/TradeHelper/`，可在设置页改到游戏目录里本模组的 `data` 文件夹或任意文件夹（`config.json` 固定在默认位置）。

## 准确性
- 站点没有欠结算天数时，价格直接来自游戏自己的计价函数（`GetBasePriceFactor`、`QuoteTrade`、`getPBase`、`getTaxRate`、`getEPool`），与游戏交易所一致。
- 欠天数的站不会被这些函数碰到（它们会触发补算），只读存着的数据估价，并标“估·欠 N 天”。
- 距离与游戏一致：星门航线（`GalaxyDistanceUnit.TryCalculateGalaxyFieldRouteToAu`），显示格式用游戏的 `UIUntils.distanceAu`。

## 外观
界面用 uGUI + TextMeshPro，字体和贴图在运行时通过游戏自己的 `ResourceLoader` 加载（思源黑体 SDF、交易界面的面板/按钮贴图），取不到时退回同色的纯色图。

## 构建与安装
Windows：`python tools/build.py`（加 `install` 复制到 `<游戏>/mods/TradeHelper`，旧安装先备份到 `dist/replaced-*`）。目标 net472，直接引用游戏 `Outer Space_Data/Managed` 的 DLL。模组布局：`Manifest/manifest.xml` + `v_common/Assemblies/TradeHelper.dll`。启用：游戏模组菜单里启用后重启游戏。

协作规则见 `AGENTS.md`。
