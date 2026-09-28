using BepInEx;
using BepInEx.Configuration;
using ServerSync;
using System.Collections;
using UnityEngine;
using static SmartWishbone.ServerSyncWrapper;

namespace SmartWishbone
{
    public class WishboneConfig
    {
        internal static ConfigEntry<KeyboardShortcut> ToggleTrackingObjectKey;
        internal static ConfigEntry<KeyboardShortcut> NextTargetKey;
        internal static ConfigEntry<KeyboardShortcut> PreviousTargetKey;

        // TODO option to hide UI icon

        // TODO implement
        //internal static ConfigEntry<Target> ShowTargetMessage;

        internal static ConfigEntry<bool> EnableDebugLogs;

        internal static ConfigEntry<Toggle> UseServerSync;

        internal static ConfigEntry<float> SearchDistanceOverride;
        internal static ConfigEntry<RangeStyle> SearchDistanceOverrideStyle;
        internal static ConfigEntry<bool> EnforceWorldConditions;

        internal static ConfigEntry<UserLevel> UsersAllowedToAddTrackables;

        internal static ConfigEntry<string> CurrentDataCache;

        internal static ConfigFile config;
        private static BaseUnityPlugin plugin;
        internal static ConfigSync serverSyncInstance;

        public static void LoadConfig(BaseUnityPlugin plugin)
        {
            WishboneConfig.config = plugin.Config;
            WishboneConfig.plugin = plugin;

            serverSyncInstance = CreateRequiredConfigSync(SmartWishbonePlugin.GUID, SmartWishbonePlugin.NAME, SmartWishbonePlugin.VERSION);

            string sectionName = "0 - Client Settings";

            NextTargetKey = config.Bind(sectionName, nameof(NextTargetKey), new KeyboardShortcut(KeyCode.N));
            PreviousTargetKey = config.Bind(sectionName, nameof(PreviousTargetKey), new KeyboardShortcut(KeyCode.B));
            ToggleTrackingObjectKey = config.Bind(sectionName, nameof(ToggleTrackingObjectKey), new KeyboardShortcut(KeyCode.J));

            //ShowTargetMessage = config.Bind(sectionName, nameof(ShowTargetMessage), Target.OnlyInUniversalMode);

            sectionName = "1 - Host/ Server Settings";

            UseServerSync = config.BindForceEnabledSyncLocker(serverSyncInstance, sectionName, nameof(UseServerSync));

            SearchDistanceOverride = config.BindSynced(serverSyncInstance, sectionName, nameof(SearchDistanceOverride), 0f, $"Only does something while it's not equal to zero, affected by {nameof(SearchDistanceOverrideStyle)}.");
            SearchDistanceOverrideStyle = config.BindSynced(serverSyncInstance, sectionName, nameof(SearchDistanceOverrideStyle), RangeStyle.AddTo);

            EnforceWorldConditions = config.BindSynced(serverSyncInstance, sectionName, nameof(EnforceWorldConditions), true);
            EnforceWorldConditions.SettingChanged += EnforceWorldConditions_SettingChanged;

            UsersAllowedToAddTrackables = config.BindSynced(serverSyncInstance, sectionName, nameof(UsersAllowedToAddTrackables), UserLevel.OnlyAdmins);

            sectionName = "9 - Other Settings";

            CurrentDataCache = config.BindSynced(serverSyncInstance, sectionName, nameof(CurrentDataCache), string.Empty, HiddenDisplay("You can ignore this. This is just for runtime synchronization."));
            CurrentDataCache.SettingChanged += CurrentDataCache_SettingChanged;

            EnableDebugLogs = config.Bind(sectionName, nameof(EnableDebugLogs), true);
        }

        private static void EnforceWorldConditions_SettingChanged(object sender, System.EventArgs e)
        {
            plugin.StartCoroutine(WaitForUpdate());
        }

        internal static void CurrentDataCache_SettingChanged(object sender, System.EventArgs e)
        {
            Helper.Log("Received settíng changed event");

            if (!ZNet.instance || ZNet.instance.IsServer())
            {
                return;
            }

            Helper.Log("Received new trackable data from server");

            var list = TrackableDataLoader.ParseStringToCustomList(CurrentDataCache.Value);
            var newData = TrackableDataLoader.ValidateData(list);

            TrackableData.ReceiveNewDataFromServer(newData);
        }

        internal static void SetConfigDataWithoutEvent(string newValue)
        {
            // resets and the server's own startup value must never be sent: on logout an admin client would otherwise push
            // an empty trackable list to the server, which saves it and hands it to every other player
            bool wasProcessingServerUpdate = ConfigSync.ProcessingServerUpdate;
            ConfigSync.ProcessingServerUpdate = true;
            CurrentDataCache.SettingChanged -= CurrentDataCache_SettingChanged;

            try
            {
                CurrentDataCache.Value = newValue;
            }
            finally
            {
                CurrentDataCache.SettingChanged += CurrentDataCache_SettingChanged;
                ConfigSync.ProcessingServerUpdate = wasProcessingServerUpdate;
            }
        }

        private static IEnumerator WaitForUpdate()
        {
            yield return new WaitForSeconds(0.1f);

            TrackableSwitcher.UpdateTarget();
        }

        public enum RangeStyle
        {
            SetTo,
            AddTo
        }

        public enum UserLevel
        {
            Noone,
            OnlyAdmins,
            Everyone
        }

        public enum Target
        {
            Always,
            OnlyInUniversalMode,
            Never
        }
    }
}