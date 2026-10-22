namespace FnvSaveExplorer.Core;

/// <summary>
/// The one job every per-plugin-database builder in this project needs and nothing else: re-keying a
/// plugin-local FormID into a save's load-order FormID space. Extracted from <see cref="PluginDatabase"/>
/// (the first consumer) so a narrower consumer — <see cref="WeaponStatsDatabase"/> — can reuse it without
/// depending on <see cref="PluginDatabase"/>'s much larger surface (names, quests, dialogue, actors, ...).
///
/// <para><b>Why a remap exists at all.</b> Inside its own file a plugin numbers forms against <i>its own</i>
/// master list, so a form's high byte there rarely equals its high byte in a save's load order. A local high
/// byte <c>j &lt; masterCount</c> maps to that master's index in the save's load order (matched by file name);
/// <c>j == masterCount</c> maps to the plugin itself.</para>
/// </summary>
public static class PluginFormIdRemap
{
    /// <summary>Builds one plugin's local-high-byte → save-load-order-index table.</summary>
    /// <param name="pluginMasters">The plugin's own <c>MAST</c> list, in order (<see cref="TesPlugin.Masters"/>).</param>
    /// <param name="pluginSaveIndex">This plugin's own index in the save's load order.</param>
    /// <param name="saveIndexOf">Save load-order plugin name (case-insensitive) → its index.</param>
    public static int[] BuildTable(IReadOnlyList<string> pluginMasters, int pluginSaveIndex, IReadOnlyDictionary<string, int> saveIndexOf)
    {
        var remap = new int[256];
        Array.Fill(remap, -1);
        for (var j = 0; j < pluginMasters.Count && j < remap.Length; j++)
            remap[j] = saveIndexOf.TryGetValue(pluginMasters[j], out var idx) ? idx : -1;
        if (pluginMasters.Count < remap.Length)
            remap[pluginMasters.Count] = pluginSaveIndex; // the plugin's own forms
        return remap;
    }

    /// <summary>Re-keys a plugin-local FormID into save space using a <see cref="BuildTable"/> result, or
    /// null when its master isn't in this save's load order.</summary>
    public static uint? Resolve(uint localFormId, int[] remapTable)
    {
        var saveHigh = remapTable[(int)(localFormId >> 24)];
        return saveHigh < 0 ? null : ((uint)saveHigh << 24) | (localFormId & 0x00FFFFFF);
    }
}
