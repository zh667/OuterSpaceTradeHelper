# 跑商助手（Outer Space / 秩序：新曙光 模组）

自用，未上传创意工坊。游戏内按 F8 打开。

- 各站市场：每个开过的空间站/星球市场的库存、买价（含税）、卖价（扣税到手）、税率、是否违禁、价格是实时还是估算（站欠着结算天数时只能估）。
- 我的库存：母舰、基地、舰队货仓、DNA 库。
- 跑商建议：扣两边税、算上买卖滑点后的最佳买卖站和数量；卖库存建议。
- 后台补算：玩家自选站点，调用游戏自带的 `FWExchangeSpace.ReleasePendingSettlementBatch` 分批补算（效果等于打开那个站）；可勾“保持实时”；从没开过的站可“首次初始化”（调用 `GetItemsForUse`，同打开交易所）。
- 导出：`%USERPROFILE%/AppData/LocalLow/CatStudio/Outer Space/TradeHelper/latest.json`，配置在同目录 config.json。

## 构建
在 Windows 上：`python tools/build.py`（`install` 参数会复制到 `<游戏>/mods/TradeHelper`，旧的先备份到 dist/）。
引用游戏 `Outer Space_Data/Managed` 里的 DLL，目标 net472，不用 NuGet。模组布局：`Manifest/manifest.xml` + `v_common/Assemblies/TradeHelper.dll`，入口类必须叫全局 `ModPluginLauncher`。
