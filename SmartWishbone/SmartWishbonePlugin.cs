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
        public const string VERSION = "1.0.6";

        internal static SmartWishbonePlugin Instance;
        internal static BepInEx.Logging.ManualLogSource Log;

        protected void Awake()
        {
            Instance = this;
            Log = Logger;

            WishboneConfig.LoadConfig(this);

            Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly());
        }
    }

    [HarmonyPatch(typeof(Game))]
    internal static class PatchGame
    {
        [HarmonyPatch(nameof(Game.OnApplicationQuit)), HarmonyPrefix]
        internal static void OnApplicationQuitPatch()
        {
            // prevent saving it to disk (well, remove it before the user notices)
            // TODO doesn't seem to update the config file in time

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