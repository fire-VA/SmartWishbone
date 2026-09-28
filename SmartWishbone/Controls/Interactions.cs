using UnityEngine;

namespace SmartWishbone
{
    internal class Interactions
    {
        internal static void TrackObject(Player player)
        {
            GameObject hoverObject = player.GetHoverObject();

            if (!hoverObject)
            {
                return;
            }

            GameObject hoverRoot = hoverObject.transform.root.gameObject;

            if (!hoverRoot)
            {
                return;
            }

            Destructible destructible = hoverRoot.GetComponent<Destructible>();

            if (!destructible || !destructible.gameObject)
            {
                Helper.LogWarning("Invalid hover object: not destructible");
                return;
            }

            string prefabName = BeaconHelper.GetOwnerPrefabName(destructible);

            var child = hoverRoot.GetComponentInChildren<Beacon>();

            if (child && !TrackableData.Data.ContainsKey(prefabName))
            {
                string beaconName = Utils.GetPrefabName(child.gameObject);

                if (TrackableData.Data.ContainsKey(beaconName))
                {
                    prefabName = beaconName;
                }
            }

            Helper.Log($"Valid hover object: {prefabName}");

            TrackableData.ToggleTarget(prefabName);
        }
    }
}