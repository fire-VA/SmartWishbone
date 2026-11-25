using BepInEx;

namespace SmartWishbone
{
    internal class Trackable
    {
        public string prefabName;
        public string displayItem;
        public string condition;
        public double range = 20;

        public Trackable()
        {
        }

        public Trackable(string prefabName, string displayItem) : this()
        {
            this.prefabName = prefabName;
            this.displayItem = displayItem;
        }

        public Trackable(string prefabName, string displayItem, string condition, double range) : this(prefabName, displayItem)
        {
            this.condition = condition;
            this.range = range;
        }
    }

    internal static class TrackableExtension
    {
        internal static bool FulFillsCondition(this Trackable trackable)
        {
            if (!WishboneConfig.EnforceWorldConditions.Value)
            {
                return true;
            }

            return trackable.condition.IsNullOrWhiteSpace() || HasBossKey(trackable.condition);
        }

        // RPGmaker (FiresNPCs PrivateKeyManager) stores private keys in the local player's m_customData
        private const string RpgPrivateKeysCustomData = "VA_PrivateKeys";

        // A boss key counts when it is a world (global) key OR the local player's private key: vanilla player
        // unique keys, or RPGmaker's UsePrivateKeys store. No local player = global only.
        // Core 0.2.810+ FiresCore.World.BossProgress.Has(string), resolved once by reflection: no compile-time reference,
        // works without Core. Not found or throwing = our own fallback below.
        private static bool coreHasResolved;
        private static System.Reflection.MethodInfo coreHas;
        private static bool coreHasFailedLogged;

        internal static bool HasBossKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return true;
            }

            if (!coreHasResolved)
            {
                coreHasResolved = true;
                try
                {
                    var m = HarmonyLib.AccessTools.Method("FiresCore.World.BossProgress:Has", new[] { typeof(string) });
                    coreHas = m != null && m.IsStatic && m.ReturnType == typeof(bool) ? m : null;
                }
                catch
                {
                    coreHas = null;
                }

                Helper.Log(coreHas != null
                    ? "Boss progress: using Core BossProgress.Has"
                    : "Boss progress: Core BossProgress.Has not found, using own check (global, unique key, VA_PrivateKeys)");
            }

            if (coreHas != null)
            {
                try
                {
                    return (bool)coreHas.Invoke(null, new object[] { key });
                }
                catch (System.Exception e)
                {
                    if (!coreHasFailedLogged)
                    {
                        coreHasFailedLogged = true;
                        Helper.Log($"Boss progress: Core BossProgress.Has failed ({e.GetBaseException().Message}), using own check");
                    }
                }
            }

            return HasBossKeyFallback(key);
        }

        private static bool HasBossKeyFallback(string key)
        {

            if (ZoneSystem.instance && ZoneSystem.instance.GetGlobalKey(key))
            {
                return true;
            }

            var player = Player.m_localPlayer;
            if (!player)
            {
                return false;
            }

            if (player.HaveUniqueKey(key))
            {
                return true;
            }

            if (player.m_customData != null && player.m_customData.TryGetValue(RpgPrivateKeysCustomData, out string saved) && !string.IsNullOrEmpty(saved))
            {
                string wanted = key.Trim();
                foreach (var k in saved.Split(','))
                {
                    if (string.Equals(k.Trim(), wanted, System.StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}