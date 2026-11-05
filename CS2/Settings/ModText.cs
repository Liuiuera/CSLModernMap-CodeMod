using System.Collections.Generic;
using Game.SceneFlow;

namespace CSLModernMap.Settings
{
    internal static class ModText
    {
        internal static string Get(string key)
        {
            if (!GameManager.instance.localizationManager.activeDictionary.TryGetValue(key, out var value))
                throw new KeyNotFoundException(key);
            return value;
        }
    }
}
