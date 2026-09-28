using System.Collections.Generic;
using UnityEngine;

namespace SmartWishbone
{
    internal static class BeaconHelper
    {
        public static Beacon FindClosestBeaconInRange(Vector3 point, Target target)
        {
            Beacon closestBeacon = null;
            float closestBeaconRange = 999999f;

            Dictionary<string, Trackable> targetList = target != null ? target.possibleTargets : TrackableData.Data;

            foreach (Beacon thisBeacon in Beacon.m_instances)
            {
                if (!thisBeacon || !thisBeacon.gameObject)
                {
                    continue;
                }

                double range = 20f;

                if (!TryGetTrackable(targetList, thisBeacon, out var targetInstance))
                {
                    continue;
                }
                else
                {
                    if (!targetInstance.FulFillsCondition())
                    {
                        continue;
                    }

                    range = targetInstance.range;
                }

                if (WishboneConfig.SearchDistanceOverride.Value != 0f)
                {
                    if (WishboneConfig.SearchDistanceOverrideStyle.Value == WishboneConfig.RangeStyle.SetTo)
                    {
                        range = WishboneConfig.SearchDistanceOverride.Value;
                    }
                    else
                    {
                        range += WishboneConfig.SearchDistanceOverride.Value;
                    }
                }

                float thisRange = Vector3.Distance(point, thisBeacon.transform.position);

                if (thisRange < range && (closestBeacon == null || thisRange < closestBeaconRange))
                {
                    closestBeacon = thisBeacon;
                    closestBeaconRange = thisRange;
                }
            }

            return closestBeacon;
        }

        internal static string GetOwnerPrefabName(Component component)
        {
            var netView = component.GetComponentInParent<ZNetView>();

            return Utils.GetPrefabName(netView ? netView.gameObject : component.transform.root.gameObject);
        }

        // since Valheim 1.0, silver veins, gold veins and frozen troll corpses all carry their vanilla beacon on a child named 'Becon',
        // so the object owning the beacon is checked first, and the beacon's own name only as a fallback for older data files
        internal static bool TryGetTrackable(Dictionary<string, Trackable> targetList, Beacon beacon, out Trackable trackable)
        {
            return targetList.TryGetValue(GetOwnerPrefabName(beacon), out trackable)
                || targetList.TryGetValue(Utils.GetPrefabName(beacon.gameObject), out trackable);
        }
    }
}