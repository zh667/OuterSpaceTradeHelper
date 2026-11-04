using UnityEngine;

// The game looks this type up by its exact global name ("ModPluginLauncher") and calls launcher().
public class ModPluginLauncher : BaseModLauncher
{
    public override void launcher()
    {
        base.launcher();
        if (TradeHelper.TradeHelperBehaviour.Instance != null) return;
        var go = new GameObject("TradeHelper");
        Object.DontDestroyOnLoad(go);
        go.AddComponent<TradeHelper.TradeHelperBehaviour>();
        Debug.Log("[TradeHelper] loaded " + TradeHelper.TradeHelperBehaviour.Version);
    }
}
