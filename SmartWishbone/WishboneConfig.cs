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
        // Lets the new data settle before the wishbone re-picks its target.
        private const float TargetUpdateDelaySeconds = 0.1f;

        internal static ConfigEntry<KeyboardShortcut> ToggleTrackingObjectKey;
        internal static ConfigEntry<KeyboardShortcut> NextTargetKey;
        internal static ConfigEntry<KeyboardShortcut> PreviousTargetKey;

        internal static ConfigEntry<bool> EnableDebugLogs;

        internal static ConfigEntry<Toggle> UseServerSync;

        internal static ConfigEntry<float> SearchDistanceOverride;
        internal static ConfigEntry<RangeStyle> SearchDistanceOverrideStyle;
        internal static ConfigEntry<bool> EnforceWorldConditions;

        internal static ConfigEntry<UserLevel> UsersAllowedToAddTrackables;

        internal static ConfigEntry<int> TrackableTogglesPerMinute;
        internal static ConfigEntry<int> MaxServerTrackables;

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

            NextTargetKey = config.Bind(sectionName, nameof(NextTargetKey), new KeyboardShortcut(KeyCode.N), "Switches the wishbone to the next kind of thing it tracks.");
            PreviousTargetKey = config.Bind(sectionName, nameof(PreviousTargetKey), new KeyboardShortcut(KeyCode.B), "Switches the wishbone to the previous kind of thing it tracks.");
            ToggleTrackingObjectKey = config.Bind(sectionName, nameof(ToggleTrackingObjectKey), new KeyboardShortcut(KeyCode.J), "Adds or removes the object you are looking at from the wishbone tracking list.");

            sectionName = "1 - Host/ Server Settings";

            UseServerSync = config.BindForceEnabledSyncLocker(serverSyncInstance, sectionName, nameof(UseServerSync));

            SearchDistanceOverride = config.BindSynced(serverSyncInstance, sectionName, nameof(SearchDistanceOverride), 0f,
                new ConfigDescription("Changes the wishbone search range in metres. 0 = normal range.", null, new ConfigurationManagerAttributes { IsAdvanced = true }));
            SearchDistanceOverrideStyle = config.BindSynced(serverSyncInstance, sectionName, nameof(SearchDistanceOverrideStyle), RangeStyle.AddTo,
                new ConfigDescription("AddTo adds the range above to the normal range; SetTo replaces it.", null, new ConfigurationManagerAttributes { IsAdvanced = true }));

            EnforceWorldConditions = config.BindSynced(serverSyncInstance, sectionName, nameof(EnforceWorldConditions), true, "Only track things once their world conditions (such as boss progress) are met.");
            EnforceWorldConditions.SettingChanged += EnforceWorldConditions_SettingChanged;

            UsersAllowedToAddTrackables = config.BindSynced(serverSyncInstance, sectionName, nameof(UsersAllowedToAddTrackables), UserLevel.OnlyAdmins, "Who may add new objects to the tracking list.");

            TrackableTogglesPerMinute = config.BindSynced(serverSyncInstance, sectionName, nameof(TrackableTogglesPerMinute), 10,
                new ConfigDescription("Most tracking list changes one player may ask the server for per minute.", new AcceptableValueRange<int>(1, 120), new ConfigurationManagerAttributes { IsAdvanced = true }));
            MaxServerTrackables = config.BindSynced(serverSyncInstance, sectionName, nameof(MaxServerTrackables), 500,
                new ConfigDescription("Most objects the server tracking list may hold. Removing is always allowed.", new AcceptableValueRange<int>(1, 10000), new ConfigurationManagerAttributes { IsAdvanced = true }));

            sectionName = "9 - Other Settings";

            CurrentDataCache = config.BindSynced(serverSyncInstance, sectionName, nameof(CurrentDataCache), string.Empty, HiddenDisplay("Internal: the tracking list sent by the server."));
            CurrentDataCache.SettingChanged += CurrentDataCache_SettingChanged;

            EnableDebugLogs = config.Bind(sectionName, nameof(EnableDebugLogs), false,
                new ConfigDescription("Writes extra troubleshooting messages to the log.", null, new ConfigurationManagerAttributes { Browsable = false }));

            // Older installs saved EnableDebugLogs = true in their cfg; switch it off once, then respect the player's choice.
            var debugLogsDefaultOffApplied = config.Bind(sectionName, "DebugLogsDefaultOffApplied", false,
                new ConfigDescription("Internal: the one-time switch of debug logs to off has run.", null, new ConfigurationManagerAttributes { Browsable = false }));

            if (!debugLogsDefaultOffApplied.Value)
            {
                EnableDebugLogs.Value = false;
                debugLogsDefaultOffApplied.Value = true;
            }
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
            yield return new WaitForSeconds(TargetUpdateDelaySeconds);

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