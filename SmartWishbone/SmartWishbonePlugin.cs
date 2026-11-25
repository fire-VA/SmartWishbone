using BepInEx;
using HarmonyLib;
using System.Reflection;

namespace SmartWishbone
{
    [BepInPlugin(GUID, NAME, VERSION)]
    public class SmartWishbonePlugin : BaseUnityPlugin
    {
        public const string GUID = "goldenrevolver.SmartWishbone";
        public const string NAME = "Smart Wishbone";
        public const string VERSION = "1.0.15";

        internal static SmartWishbonePlugin Instance;
        internal static BepInEx.Logging.ManualLogSource Log;

        protected void Awake()
        {
            Instance = this;
            Log = Logger;

            WishboneConfig.LoadConfig(this);

            Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly());

#if FIRES_TESTS
            WishboneTest.Register();
#endif
        }
    }

    [HarmonyPatch(typeof(Game))]
    internal static class PatchGame
    {
        [HarmonyPatch(nameof(Game.OnApplicationQuit)), HarmonyPrefix]
        internal static void OnApplicationQuitPatch()
        {
            // The data cache is runtime-only; remove it so it is not saved to the config file.

            WishboneConfig.CurrentDataCache.SettingChanged -= WishboneConfig.CurrentDataCache_SettingChanged;
            WishboneConfig.config.Remove(WishboneConfig.CurrentDataCache.Definition);

            DataFileWriter.Drain();
        }

        [HarmonyPatch(nameof(Game.Logout)), HarmonyPrefix]
        internal static void ResetOnLogout()
        {
            TrackableData.ClearSyncedData();
            WishboneConfig.SetConfigDataWithoutEvent(string.Empty);
        }
    }

    [HarmonyPatch(typeof(FejdStartup))]
    internal class FejdStartupPatch
    {
        private static bool isFirstStartup = true;

        [HarmonyPatch(nameof(FejdStartup.Awake)), HarmonyPostfix]
        private static void FejdStartupAwakePatch()
        {
            WishboneConfig.SetConfigDataWithoutEvent(string.Empty);

            if (!isFirstStartup)
            {
                return;
            }

            isFirstStartup = false;

            try
            {
                LocalizationLoader.SetupTranslations();

                TrackableData.SetPersonalData(TrackableDataLoader.LoadTrackableData());
            }
            catch (System.Exception e)
            {
                Helper.LogError(e);
            }
        }
    }
}