using HarmonyLib;
using System;

namespace SmartWishbone.ServerSync
{
    [HarmonyPatch(typeof(ZNet))]
    internal class RPCHandler
    {
        private const string rpc_server_toggle = SmartWishbonePlugin.GUID + "ServerToggleRPC";

        [HarmonyPatch(nameof(ZNet.Awake)), HarmonyPostfix]
        private static void AwakePostfix(ZNet __instance)
        {
            if (__instance.IsServer())
            {
                WishboneConfig.SetConfigDataWithoutEvent(TrackableDataLoader.ParseCustomDictToString(TrackableData.Data));
                ZRoutedRpc.instance.Register(rpc_server_toggle, new Action<long, string>(ReceiveTrackableToggleRPC));
            }
        }

        internal static void SendTrackableToggleRPC(string trackableName)
        {
            if (!ZNet.instance || ZNet.instance.IsServer())
            {
                return;
            }

            Helper.Log($"Send new trackable toggle request for: {trackableName}");
            ZRoutedRpc.instance.InvokeRoutedRPC(rpc_server_toggle, new object[] { trackableName });
        }

        private static void ReceiveTrackableToggleRPC(long sender, string trackableName)
        {
            if (!ZNet.instance || !ZNet.instance.IsServer())
            {
                return;
            }

            var allowed = WishboneConfig.UsersAllowedToAddTrackables.Value;

            if (allowed == WishboneConfig.UserLevel.Noone)
            {
                return;
            }

            // Security: identity comes from the sender's peer; the server writes these names into its data file.
            var peer = ZNet.instance.GetPeer(sender);
            string hostName = peer?.m_socket?.GetHostName();

            if (peer == null || hostName == null)
            {
                return;
            }

            bool isAdmin = ZNet.instance.ListContainsId(ZNet.instance.m_adminList, hostName);

            if (!isAdmin && allowed != WishboneConfig.UserLevel.Everyone)
            {
                Helper.Log("Received trackable request but sender is not an admin");
                return;
            }

            if (!IsWithinRateLimit(peer.m_uid))
            {
                Helper.LogWarning($"Refused trackable request from peer {peer.m_uid}: over {WishboneConfig.TrackableTogglesPerMinute.Value} per minute (TrackableTogglesPerMinute)");
                return;
            }

            // Admins: a known prefab or [A-Za-z0-9_]; everyone else: a known prefab only.
            if (isAdmin ? !IsValidTrackableName(trackableName) : !IsKnownPrefab(trackableName))
            {
                Helper.LogWarning("Rejected trackable request with an invalid name");
                return;
            }

            Helper.Log($"Received valid trackable request for '{trackableName}'");

            TrackableData.ToggleTarget(trackableName);
        }
        private static readonly System.Collections.Generic.Dictionary<long, System.Collections.Generic.Queue<float>> recentToggles =
            new System.Collections.Generic.Dictionary<long, System.Collections.Generic.Queue<float>>();

        private static bool IsWithinRateLimit(long peerId)
        {
            float now = UnityEngine.Time.realtimeSinceStartup;

            // forget players that went quiet, so the table stays small
            if (recentToggles.Count > 256)
            {
                var stale = new System.Collections.Generic.List<long>();
                foreach (var kv in recentToggles)
                {
                    if (kv.Value.Count == 0 || now - kv.Value.Peek() > 60f) stale.Add(kv.Key);
                }
                foreach (var id in stale) recentToggles.Remove(id);
            }

            if (!recentToggles.TryGetValue(peerId, out var times))
            {
                times = new System.Collections.Generic.Queue<float>();
                recentToggles[peerId] = times;
            }

            while (times.Count > 0 && now - times.Peek() > 60f)
            {
                times.Dequeue();
            }

            if (times.Count >= WishboneConfig.TrackableTogglesPerMinute.Value)
            {
                return false;
            }

            times.Enqueue(now);
            return true;
        }

        internal static bool IsKnownPrefab(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > maxTrackableNameLength)
            {
                return false;
            }

            if (ZNetScene.instance && ZNetScene.instance.GetPrefab(name) != null)
            {
                return true;
            }

            return ObjectDB.instance && ObjectDB.instance.GetItemPrefab(name) != null;
        }

        private const int maxTrackableNameLength = 64;

        // A name must be a known prefab, or plain [A-Za-z0-9_] within the length cap.
        internal static bool IsValidTrackableName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > maxTrackableNameLength)
            {
                return false;
            }

            if (IsKnownPrefab(name))
            {
                return true;
            }

            foreach (char c in name)
            {
                if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_'))
                {
                    return false;
                }
            }

            return true;
        }
    }
}