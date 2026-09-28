using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace ServerSync
{
    // Standalone copy of Fires Unified Core's AdminSyncing (see ConfigSync.cs). Its RPC is named after this
    // assembly, and IsAdmin(long) is internal so name-based reflection lookups from other mods skip it.
    //
    // Tracks the server's admin list and pushes per-peer admin status to
    // ConfigSync.lockExempt so clients can locally unlock the config UI
    // without round-tripping through the server. Server side polls the
    // admin list every AdminListPollIntervalSeconds and only broadcasts
    // when the set actually changes.
    internal static class AdminSyncing
    {
        private const string AdminStatusRpcSuffix = " AdminStatusSync";
        private const float AdminListPollIntervalSeconds = 4f;
        private const int CompressedPackageThresholdBytes = 10000;
        private const int CompressedPackageMagic = 4;
        private const long ServerPeerLoopbackId = 0L;

        private static bool _isServer;

        // Fired on every client after admin status is received and ConfigSync.lockExempt is set.
        // Subscribers refresh their own config-lock UI; the watcher, RPC channel and lockExempt push all live here.
        internal static event Action<bool> AdminStatusChanged;

        private static readonly string AdminStatusRpcName = typeof(AdminSyncing).Assembly.GetName().Name + AdminStatusRpcSuffix;

        [HarmonyPatch(typeof(ZNet), "Awake")]
        private static class AdminStatusSyncPatch
        {
            [HarmonyPostfix]
            private static void Postfix(ZNet __instance)
            {
                _isServer = __instance.IsServer();

                // lockExempt is static and would otherwise BLEED between sessions: admin on server A,
                // then joining server B (which may never push a status) kept the stale grant. Every
                // world join starts non-admin until THIS server says otherwise — fail closed.
                if (!_isServer) ConfigSync.lockExempt = false;

                ZRoutedRpc.instance.Register<ZPackage>(AdminStatusRpcName, RPC_AdminStatusSync);

                if (_isServer)
                    __instance.StartCoroutine(WatchAdminListChanges());
            }
        }

        internal static bool IsAdmin(long senderId)
        {
            if (!ZNet.instance) return false;
            var peer = ZNet.instance.GetPeer(senderId);
            if (peer == null) return false;
            return AdminListContains(peer.m_rpc.GetSocket().GetHostName());
        }

        /// <summary>
        /// The Fires family's check for whether the local player is an admin, true as soon as any signal says so:
        /// ZNet.IsServer, Valheim's synced admin list, or the Fires admin-status push. Code that ran too early
        /// should subscribe to <see cref="AdminStatusChanged"/> rather than cache a result. Never use it server-side
        /// for a remote player, since it is always true on a server; use <see cref="IsAdmin(long)"/> for that.
        /// </summary>
        internal static bool IsLocalAdmin()
        {
            var znet = ZNet.instance;
            if (znet == null) return false;
            try { if (znet.IsServer()) return true; } catch { }
            try { if (znet.LocalPlayerIsAdminOrHost()) return true; } catch { }
            try { if (ConfigSync.lockExempt) return true; } catch { }
            return false;
        }

        // adminlist.txt entries may be stored bare ("7656...") or platform-prefixed ("Steam_7656..."),
        // but the socket hostname is the bare form. Mirror Valheim's ZNet.ListContainsId so either stored
        // form resolves the same host - a raw Contains would drop a real admin whose entry is "Steam_...".
        internal static bool AdminListContains(string hostName)
        {
            if (string.IsNullOrEmpty(hostName)) return false;
            var list = GetAdminList();
            if (list == null) return false;
            if (list.Contains(hostName)) return true;
            string norm = NormalizeId(hostName);
            foreach (var entry in list.GetList())
                if (NormalizeId(entry).Equals(norm, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string NormalizeId(string id)
        {
            if (string.IsNullOrEmpty(id)) return string.Empty;
            int underscoreIndex = id.IndexOf('_');
            return (underscoreIndex >= 0 ? id.Substring(underscoreIndex + 1) : id).Trim();
        }

        // Peers already told their admin status (by m_uid), so a fresh connection gets pushed even when the admin
        // list never changes (the "static adminlist.txt → connecting admin never receives lockExempt" bug).
        private static readonly HashSet<long> _sentPeers = new HashSet<long>();

        private static IEnumerator WatchAdminListChanges()
        {
            var adminList = GetAdminList();
            var currentAdmins = new HashSet<string>(adminList.GetList());
            bool first = true;

            while (true)
            {
                var newAdmins = new HashSet<string>(adminList.GetList());
                bool changed = !newAdmins.SetEquals(currentAdmins);

                if (changed || first)
                {
                    // list changed (or first pass) → re-send EVERY ready peer its status.
                    _sentPeers.Clear();
                    PushAdminStatus(ZNet.instance.GetPeers().Where(p => p.IsReady()));
                    currentAdmins = newAdmins;
                    first = false;
                }
                else
                {
                    // no change → push to any newly-ready peer that hasn't been told (fresh connections).
                    PushAdminStatus(ZNet.instance.GetPeers().Where(p => p.IsReady() && !_sentPeers.Contains(p.m_uid)));
                }

                _sentPeers.RemoveWhere(uid => !ZNet.instance.GetPeers().Any(p => p.m_uid == uid));
                yield return new WaitForSeconds(AdminListPollIntervalSeconds);
            }
        }

        // Send each given peer its admin/non-admin status (via the normalized admin-list match) and mark it sent.
        internal static void PushAdminStatus(IEnumerable<ZNetPeer> targets)
        {
            var list = targets?.ToList();
            if (list == null || list.Count == 0) return;
            var adminPeers = list.Where(p => AdminListContains(p.m_rpc.GetSocket().GetHostName())).ToList();
            var nonAdminPeers = list.Except(adminPeers).ToList();
            SendAdminStatus(nonAdminPeers, isAdmin: false);
            SendAdminStatus(adminPeers, isAdmin: true);
            foreach (var peer in list) _sentPeers.Add(peer.m_uid);
        }

        private static void SendAdminStatus(List<ZNetPeer> peers, bool isAdmin)
        {
            if (!peers.Any()) return;
            var package = new ZPackage();
            package.Write(isAdmin);
            ZNet.instance.StartCoroutine(SendZPackage(peers, package));
        }

        private static void RPC_AdminStatusSync(long sender, ZPackage package)
        {
            if (_isServer)
            {
                HandleServerSideAdminRequest(sender);
                return;
            }
            HandleClientSideAdminStatusReceived(package);
        }

        private static void HandleServerSideAdminRequest(long sender)
        {
            var peer = ZNet.instance.GetPeer(sender);
            if (peer == null) return;
            if (!AdminListContains(peer.m_rpc.GetSocket().GetHostName())) return;

            var pkg = new ZPackage();
            pkg.Write(true);
            // ROUTED, to match the routed Register (see SendZPackage) — a direct peer.m_rpc.Invoke
            // lands on a channel the client never registered, so it is silently dropped.
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, AdminStatusRpcName, pkg);
        }

        private static void HandleClientSideAdminStatusReceived(ZPackage package)
        {
            bool isAdmin = package.ReadBool();
            ConfigSync.lockExempt = isAdmin;
            ConfigSync.RefreshReadOnlyFlagsForAll();
            AdminStatusChanged?.Invoke(isAdmin);
        }

        private static IEnumerator SendZPackage(List<ZNetPeer> peers, ZPackage package)
        {
            if (!ZNet.instance) yield break;

            byte[] data = package.GetArray();
            if (data.Length > CompressedPackageThresholdBytes)
                package = BuildCompressedPackage(data);

            foreach (var peer in peers.Where(p => p.IsReady()))
            {
                // ALWAYS routed — the handler is registered via ZRoutedRpc.instance.Register, so the
                // server MUST route (peer.m_uid) too. The old `if (_isServer) peer.m_rpc.Invoke(...)`
                // sent on the direct channel, which the client never registered → the push was
                // silently dropped, lockExempt stayed false, and real admins were gated OUT of their
                // own admin UI once IsAdmin stopped falling open on isSourceOfTruth (Core 0.1.14).
                // Every working Fires server→client RPC routes to peer.m_uid the same way.
                long target = peer.m_server ? ServerPeerLoopbackId : peer.m_uid;
                ZRoutedRpc.instance.InvokeRoutedRPC(target, AdminStatusRpcName, package);
            }
        }

        private static ZPackage BuildCompressedPackage(byte[] data)
        {
            var compressed = new ZPackage();
            compressed.Write(CompressedPackageMagic);

            using (var output = new MemoryStream())
            {
                using (var deflate = new System.IO.Compression.DeflateStream(output, System.IO.Compression.CompressionLevel.Optimal))
                    deflate.Write(data, 0, data.Length);

                compressed.Write(output.ToArray());
            }
            return compressed;
        }

        private static SyncedList GetAdminList()
        {
            return (SyncedList)AccessTools.Field(typeof(ZNet), "m_adminList").GetValue(ZNet.instance);
        }
    }
}
