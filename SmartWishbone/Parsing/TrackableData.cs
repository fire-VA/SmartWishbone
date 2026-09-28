using SmartWishbone.ServerSync;
using System.Collections.Generic;
using System.Linq;

namespace SmartWishbone
{
    internal static class TrackableData
    {
        internal static Target[] targets;

        private static Dictionary<string, Trackable> personalData = new Dictionary<string, Trackable>();
        private static Dictionary<string, Trackable> syncedData = new Dictionary<string, Trackable>();

        internal static Dictionary<string, Trackable> Data
        {
            get
            {
                if (ZNet.instance && !ZNet.instance.IsServer())
                {
                    return syncedData;
                }
                else
                {
                    return personalData;
                }
            }
        }

        internal static void ToggleTarget(string prefabName)
        {
            if (!ZNet.instance)
            {
                return;
            }

            if (!ZNet.instance.IsServer())
            {
                RPCHandler.SendTrackableToggleRPC(prefabName);
                return;
            }

            if (personalData.ContainsKey(prefabName))
            {
                Helper.Log($"Removed trackable {prefabName}");

                personalData.Remove(prefabName);
            }
            else
            {
                Helper.Log($"Added trackable {prefabName}");

                personalData.Add(prefabName, new Trackable(prefabName, "Auto"));

                TryAddBeacons(new List<string>() { prefabName });
            }

            TrackableDataLoader.SaveTrackableData(personalData);

            // this triggers a config sync/rpc for all clients
            WishboneConfig.CurrentDataCache.Value = TrackableDataLoader.ParseCustomDictToString(personalData);

            UpdateTargets();
        }

        internal static void SetPersonalData(Dictionary<string, Trackable> newData)
        {
            personalData = newData;
        }

        internal static void ClearSyncedData()
        {
            syncedData.Clear();
        }

        internal static void ReceiveNewDataFromServer(Dictionary<string, Trackable> newData)
        {
            var newValues = newData.Keys.Except(syncedData.Keys).ToList();

            Helper.Log($"Previous data count: {syncedData.Count}, new data count: {newData.Count}, difference count {newValues.Count}");

            TryAddBeacons(newValues);

            syncedData = newData;

            UpdateTargets();
        }

        internal static void TryAddBeacons(List<string> newNames)
        {
            if (newNames == null || newNames.Count == 0)
            {
                return;
            }

            var allObjects = UnityEngine.Object.FindObjectsByType<Destructible>(UnityEngine.FindObjectsInactive.Include, UnityEngine.FindObjectsSortMode.None);

            foreach (var obj in allObjects)
            {
                if (obj && obj.gameObject && newNames.Contains(Utils.GetPrefabName(obj.gameObject)))
                {
                    BeaconAddPatches.TryAddBeacon(obj);
                }
            }
        }

        internal static void UpdateTargets()
        {
            var tempTargets = new Dictionary<string, Dictionary<string, Trackable>>();

            foreach (var target in Data)
            {
                var split = target.Value.displayItem.Split('|');

                foreach (var displayName in split)
                {
                    if (tempTargets.TryGetValue(displayName, out var value))
                    {
                        value[target.Key] = target.Value;
                    }
                    else
                    {
                        tempTargets[displayName] = new Dictionary<string, Trackable>
                        {
                            [target.Key] = target.Value
                        };
                    }
                }
            }

            var targetList = new List<Target>();

            foreach (var target in tempTargets)
            {
                var newTarget = new Target(target.Key, target.Value);

                var shared = GetPossibleItem(newTarget.targetName);

                if (shared != null)
                {
                    newTarget.optionalItem = shared;
                    newTarget.displaySprite = shared.m_icons[0];
                    newTarget.displayName = Localization.instance.Localize(shared.m_name);
                }
                else
                {
                    if (newTarget.targetName.IndexOf('$') == 0)
                    {
                        newTarget.displayName = Localization.instance.Localize(newTarget.targetName);
                    }
                    else
                    {
                        newTarget.displayName = newTarget.targetName;
                    }
                }

                targetList.Add(newTarget);
            }

            targets = targetList.OrderBy((a) => a.optionalItem == null).ThenBy((a) => a.displayName).ToArray();
        }

        private static ItemDrop.ItemData.SharedData GetPossibleItem(string itemName)
        {
            if (ObjectDB.instance == null)
            {
                return null;
            }

            var possibleItem = ObjectDB.instance.GetItemPrefab(itemName.GetStableHashCode());

            if (possibleItem != null && possibleItem.TryGetComponent<ItemDrop>(out var drop))
            {
                return drop.m_itemData.m_shared;
            }
            else
            {
                return null;
            }
        }
    }
}