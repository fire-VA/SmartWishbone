using BepInEx;
using HarmonyLib;
using ServerSync;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;

namespace SmartWishbone
{
    // F5 'wishbone_test': an in-game self-test. Output shape: BEGIN, one "i/N step PASS|FAIL|SKIP - ..." line per step, END.
    internal static class WishboneTest
    {
        private const string CommandName = "wishbone_test";
        private const string Tag = "[WishboneTest] ";
        private const string ModName = "SmartWishbone";
        private const string ResultsFolder = "FiresTests";
        private const string ResultsFile = "wishbone_test.txt";
        private const string SpawnedFilePrefix = "wishbone_spawned_";
        private const string WishboneItemName = "Wishbone";
        private const string SilverTarget = "SilverOre";
        private const string GoldTarget = "GoldOre";
        private const string ProbePrefix = "rock";
        private const string BotFlag = "-firesbot";
        private const string BotRejoinCommand = "bot rejoin 1 5";
        private const string BotFollowCommand = "bot follow";
        private const string BotRejoinReply = "bot: rejoin";
        private const string BotRejoinDoneReply = "bot: rejoin done";
        private const float SpawnDistance = 6f;
        private const float SpawnSpacing = 5f;
        private const float BeaconSettleSeconds = 1f;
        private const float SyncTimeoutSeconds = 10f;
        private const float PollSeconds = 0.25f;
        private const float LogoutResetSettleSeconds = 2f;
        private const float BotAnswerTimeoutSeconds = 15f;
        private const float BotRejoinTimeoutSeconds = 150f;
        private const int EstimateSeconds = 10;
        private const int BotEstimateSeconds = 80;

        private const string PeerRpc = SmartWishbonePlugin.GUID + " WishboneTestPeer";
        private const byte PeerRequest = 0;
        private const byte PeerReply = 1;
        private const float PeerReplySeconds = 3f;
        private const string ServerLabel = "server";

        private static readonly string[] Steps =
        {
            "effect", "data", "peers", "silver", "gold", "troll", "homing_silver", "homing_gold", "switch", "roundtrip", "logout", "bot_logout",
        };

        private sealed class PeerAnswer
        {
            public string Who;
            public int Count;
            public string Hash;
            public string Targets;
        }

        private static readonly List<PeerAnswer> peerAnswers = new List<PeerAnswer>();

        // Every peer with this build answers a test's question about its trackable list, so one client can compare itself
        // with the server and the other players.
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Awake))]
        private static class RegisterPeerRpc
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                ZRoutedRpc.instance.Register<ZPackage>(PeerRpc, OnPeerRpc);
            }
        }

        private static readonly Dictionary<string, string[]> Veins = new Dictionary<string, string[]>
        {
            ["silver"] = new[] { "silvervein", SilverTarget },
            ["gold"] = new[] { "goldvein", GoldTarget },
            ["troll"] = new[] { "TrollFrost_Dead", GoldTarget },
        };

        private sealed class SpawnedVein
        {
            public GameObject Object;
            public string Prefab;
            public string ExpectedTarget;
        }

        private static readonly List<SpawnedVein> spawned = new List<SpawnedVein>();
        private static Terminal output;
        private static bool running;
        private static bool reported;
        private static int passed;
        private static int failed;
        private static int skipped;
        private static int stepIndex;
        private static int stepCount;
        private static string currentStep;
        private static string dllHash;

        private static ItemDrop.ItemData equippedWishbone;
        private static ItemDrop.ItemData previousUtility;
        private static bool addedWishbone;
        private static string savedTarget;
        private static bool usedBot;

        internal static void Register()
        {
            new Terminal.ConsoleCommand(CommandName,
                "[Smart Wishbone] self-test (" + string.Join(", ", Steps) + "). "
                + "Usage: wishbone_test [list|keep|clean|<step> ...]; keep = leave the spawned veins and the wishbone, clean = remove leftovers. "
                + "Steps that spawn objects or give a Wishbone need an admin or the host",
                args =>
                {
                    output = args.Context;
                    var words = args.Args.Skip(1).Select(a => a.ToLowerInvariant()).ToList();

                    if (words.Contains("list"))
                    {
                        Echo($"{Steps.Length} steps: {string.Join(", ", Steps)}. Args: list, keep, clean, or step names.");
                        return;
                    }

                    if (running)
                    {
                        Echo("already running.");
                        return;
                    }

                    if (!Player.m_localPlayer || !ZNetScene.instance || !ObjectDB.instance || !ZNet.instance || ZDOMan.instance == null)
                    {
                        Echo("start it in a world, with your character spawned.");
                        return;
                    }

                    if (words.Contains("clean"))
                    {
                        Echo($"removed {Cleanup()} test object(s).");
                        return;
                    }

                    var unknown = words.Where(w => w != "keep" && !Steps.Contains(w)).ToList();

                    if (unknown.Count > 0)
                    {
                        Echo($"unknown argument(s) {string.Join(", ", unknown)}; 'wishbone_test list' shows the steps.");
                        return;
                    }

                    var selected = Steps.Where(words.Contains).ToList();
                    SmartWishbonePlugin.Instance.StartCoroutine(Run(selected.Count > 0 ? selected : Steps.ToList(), words.Contains("keep")));
                });
        }

        private static IEnumerator Run(List<string> steps, bool keep)
        {
            running = true;
            passed = 0;
            failed = 0;
            skipped = 0;
            stepIndex = 0;
            stepCount = steps.Count;
            usedBot = false;
            equippedWishbone = null;
            previousUtility = null;
            addedWishbone = false;
            savedTarget = TrackableSwitcher.GetCurrentTarget();
            float started = Time.realtimeSinceStartup;
            int estimate = EstimateSeconds + (steps.Contains("bot_logout") && OtherPlayerCount() > 0 ? BotEstimateSeconds : 0);

            try
            {
                Line($"BEGIN {ModName} {SmartWishbonePlugin.VERSION} ({DllHash()}) on {Role()} '{Player.m_localPlayer.GetPlayerName()}' "
                    + $"world '{ZNet.instance.GetWorldName()}': {steps.Count} steps, ~{estimate} s");

                int leftovers = Cleanup();

                if (leftovers > 0)
                {
                    Detail($"removed {leftovers} object(s) left by an earlier run.");
                }

                foreach (string step in steps)
                {
                    stepIndex++;
                    currentStep = step;
                    reported = false;

                    if (Veins.ContainsKey(step) || step.StartsWith("homing_"))
                    {
                        if (!IsAdminOrHost())
                        {
                            Skip("needs an admin or the host: it spawns test objects.");
                            continue;
                        }

                        if (spawned.Count == 0)
                        {
                            SpawnVeins();
                            yield return new WaitForSeconds(BeaconSettleSeconds);
                        }
                    }

                    var body = Body(step);

                    while (true)
                    {
                        bool more;

                        try
                        {
                            more = body.MoveNext();
                        }
                        catch (Exception e)
                        {
                            Fail($"threw {e.GetType().Name}: {e.Message}");
                            break;
                        }

                        if (!more)
                        {
                            break;
                        }

                        yield return body.Current;
                    }

                    if (!reported)
                    {
                        Fail("ended without a result.");
                    }
                }
            }
            finally
            {
                try
                {
                    if (!keep)
                    {
                        int removed = Cleanup();
                        Detail($"removed {removed} test object(s).");
                        RestorePlayer();
                    }
                    else
                    {
                        Detail($"kept {spawned.Count} test object(s) and the equipped wishbone; '{CommandName} clean' removes the objects.");
                    }

                    if (usedBot && Chat.instance)
                    {
                        Chat.instance.SendText(Talker.Type.Shout, BotFollowCommand);
                    }
                }
                catch (Exception e)
                {
                    failed++;
                    Detail($"cleanup threw {e.GetType().Name}: {e.Message}");
                }

                Line($"END {(failed > 0 ? "FAIL" : "PASS")}: {passed} pass, {failed} fail, {skipped} skip in {Time.realtimeSinceStartup - started:0.0} s");
                running = false;
            }
        }

        private static IEnumerator Body(string step)
        {
            switch (step)
            {
                case "effect": return Wrap(CheckStatusEffect);
                case "data": return Wrap(CheckData);
                case "peers": return CheckPeers();
                case "silver":
                case "gold":
                case "troll": return Wrap(() => CheckVein(step));
                case "homing_silver": return Wrap(() => CheckHoming(SilverTarget));
                case "homing_gold": return Wrap(() => CheckHoming(GoldTarget));
                case "switch": return CheckSwitching();
                case "roundtrip": return CheckServerRoundTrip();
                case "logout": return CheckLogoutReset();
                case "bot_logout": return CheckBotLogout();
                default: return Wrap(() => Fail("unknown step."));
            }
        }

        private static IEnumerator Wrap(Action check)
        {
            check();
            yield break;
        }

        private static string Role()
        {
            if (Environment.GetCommandLineArgs().Any(a => string.Equals(a, BotFlag, StringComparison.OrdinalIgnoreCase)))
            {
                return "bot";
            }

            return ZNet.instance.IsServer() ? "server" : "client";
        }

        private static void CheckStatusEffect()
        {
            var effect = ObjectDB.instance.GetStatusEffect(ObjectDBPatch.wishboneEffectName.GetStableHashCode());
            var prefab = ObjectDB.instance.GetItemPrefab(WishboneItemName);
            var drop = prefab ? prefab.GetComponent<ItemDrop>() : null;

            if (!(effect is SE_CustomFinder))
            {
                Fail($"the Wishbone status effect is {(effect ? effect.GetType().Name : "missing")}, not Smart Wishbone's.");
            }
            else if (!drop || !(drop.m_itemData.m_shared.m_equipStatusEffect is SE_CustomFinder))
            {
                Fail("the Wishbone item does not equip Smart Wishbone's status effect.");
            }
            else
            {
                Pass("the Wishbone status effect and item are Smart Wishbone's.");
            }
        }

        private static void CheckData()
        {
            var data = TrackableData.Data;

            if (ZNet.instance.IsServer())
            {
                if (data.Count > 0)
                {
                    Pass($"{data.Count} trackables loaded.");
                }
                else
                {
                    Fail("no trackables loaded (check SmartWishbone.Data.yaml).");
                }

                return;
            }

            if (data.Count > 0 && TrackableData.receivedFromServerCount > 0)
            {
                Pass($"{data.Count} trackables from the server ({TrackableData.receivedFromServerCount} update(s)).");
            }
            else
            {
                Fail($"no trackable list from the server ({data.Count} trackables, {TrackableData.receivedFromServerCount} updates): config sync is not working.");
            }
        }

        private static void SpawnVeins()
        {
            var player = Player.m_localPlayer;
            Vector3 forward = player.transform.forward;
            forward.y = 0f;
            forward.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            int slot = -1;

            foreach (var vein in Veins.Values)
            {
                var prefab = ZNetScene.instance.GetPrefab(vein[0]);

                if (!prefab)
                {
                    slot++;
                    continue;
                }

                Vector3 position = player.transform.position + forward * SpawnDistance + right * (slot++ * SpawnSpacing);
                position.y = ZoneSystem.instance.GetGroundHeight(position);

                var instance = UnityEngine.Object.Instantiate(prefab, position, Quaternion.LookRotation(-forward));
                spawned.Add(new SpawnedVein { Object = instance, Prefab = vein[0], ExpectedTarget = vein[1] });

                var view = instance.GetComponent<ZNetView>();

                if (view && view.GetZDO() != null)
                {
                    AppendSpawnedId(view.GetZDO().m_uid);
                }
            }

            Detail($"spawned {string.Join(", ", spawned.Select(v => v.Prefab).ToArray())} {SpawnDistance:0} m in front of you.");
        }

        private static void CheckVein(string step)
        {
            string prefabName = Veins[step][0];
            string expected = Veins[step][1];
            var vein = spawned.FirstOrDefault(v => v.Prefab == prefabName);

            if (vein == null || !vein.Object)
            {
                Skip($"this game has no '{prefabName}' to spawn.");
                return;
            }

            if (!TrackableData.Data.ContainsKey(prefabName))
            {
                Skip($"'{prefabName}' is not in the trackable data.");
                return;
            }

            var beacons = vein.Object.GetComponentsInChildren<Beacon>();

            if (beacons.Length == 0)
            {
                Fail($"'{prefabName}' has no beacon.");
                return;
            }

            var matched = (TrackableData.targets ?? new Target[0])
                .Where(t => beacons.Any(b => BeaconHelper.TryGetTrackable(t.possibleTargets, b, out _)))
                .Select(t => t.targetName)
                .ToList();

            if (matched.Count == 1 && matched[0] == expected)
            {
                Pass($"'{prefabName}' belongs to {Display(expected)} only.");
            }
            else
            {
                Fail($"'{prefabName}' belongs to [{string.Join(", ", matched.Select(Display).ToArray())}], expected {Display(expected)} only.");
            }
        }

        private static void CheckHoming(string targetName)
        {
            var player = Player.m_localPlayer;
            var target = (TrackableData.targets ?? new Target[0]).FirstOrDefault(t => t.targetName == targetName);
            var expected = spawned.Where(v => v.ExpectedTarget == targetName && v.Object).ToList();

            if (target == null || expected.Count == 0)
            {
                Skip($"no {Display(targetName)} target or test object.");
                return;
            }

            if (!target.possibleTargets.Values.Any(TrackableExtension.FulFillsCondition))
            {
                var keys = target.possibleTargets.Values.Select(t => t.condition).Where(c => c != null).Distinct().ToArray();
                Skip($"{Display(targetName)} is locked on this world until {string.Join(" / ", keys)}; the test never sets keys.");
                return;
            }

            var beacon = BeaconHelper.FindClosestBeaconInRange(player.transform.position, target);
            bool found = beacon && expected.Any(v => beacon.transform.IsChildOf(v.Object.transform));
            string what = beacon
                ? $"'{BeaconHelper.GetOwnerPrefabName(beacon)}' at {Vector3.Distance(player.transform.position, beacon.transform.position):0} m"
                : "nothing";

            if (found)
            {
                Pass($"with {Display(targetName)} selected it homes in on the spawned {what}.");
            }
            else
            {
                Fail($"with {Display(targetName)} selected it found {what}, not a spawned test object.");
            }
        }

        private static IEnumerator CheckSwitching()
        {
            var player = Player.m_localPlayer;

            if (!HasFinder(player))
            {
                var inventory = player.GetInventory();
                var wishbone = inventory.GetAllItems().FirstOrDefault(i => i.m_dropPrefab && i.m_dropPrefab.name == WishboneItemName);

                if (wishbone == null)
                {
                    if (!IsAdminOrHost())
                    {
                        Skip("carry or equip a Wishbone first; giving you one needs an admin or the host.");
                        yield break;
                    }

                    wishbone = inventory.AddItem(WishboneItemName, 1, 1, 0, 0L, string.Empty, true);
                    addedWishbone = wishbone != null;
                }

                if (wishbone != null)
                {
                    previousUtility = player.m_utilityItem;
                    player.EquipItem(wishbone);
                    equippedWishbone = wishbone;
                    Detail("equipped a Wishbone in your utility slot for the test.");
                }

                yield return null;
            }

            if (!HasFinder(player))
            {
                Fail("the Smart Wishbone effect is not on you after equipping a Wishbone.");
                yield break;
            }

            string before = CurrentTargetName();
            Exception error = null;

            try
            {
                TrackableSwitcher.SwitchTarget(true);
            }
            catch (Exception e)
            {
                error = e;
            }

            string after = CurrentTargetName();

            if (error != null)
            {
                Fail($"switching targets threw {error.GetType().Name}: {error.Message}");
            }
            else if (!HasFinder(player))
            {
                Fail("the Smart Wishbone effect was lost when switching targets.");
            }
            else
            {
                Pass($"switching targets works ({before} -> {after}).");
            }
        }

        private static IEnumerator CheckServerRoundTrip()
        {
            string probe = PickProbePrefab();

            if (probe == null)
            {
                Skip("no plain rock prefab to add as a test trackable.");
                yield break;
            }

            bool isServer = ZNet.instance.IsServer();
            var allowed = WishboneConfig.UsersAllowedToAddTrackables.Value;

            if (!isServer && (allowed == WishboneConfig.UserLevel.Noone || (allowed == WishboneConfig.UserLevel.OnlyAdmins && !IsAdminOrHost())))
            {
                Skip($"the server's UsersAllowedToAddTrackables is {allowed}, so you cannot add trackables.");
                yield break;
            }

            TrackableData.ToggleTarget(probe);
            float addSeconds = 0f;

            while (!TrackableData.Data.ContainsKey(probe) && addSeconds < SyncTimeoutSeconds)
            {
                yield return new WaitForSeconds(PollSeconds);
                addSeconds += PollSeconds;
            }

            if (!TrackableData.Data.ContainsKey(probe))
            {
                Fail($"'{probe}' was not added within {SyncTimeoutSeconds:0} s (UsersAllowedToAddTrackables may not allow you, or config sync is not working).");
                yield break;
            }

            TrackableData.ToggleTarget(probe);
            float removeSeconds = 0f;

            while (TrackableData.Data.ContainsKey(probe) && removeSeconds < SyncTimeoutSeconds)
            {
                yield return new WaitForSeconds(PollSeconds);
                removeSeconds += PollSeconds;
            }

            if (TrackableData.Data.ContainsKey(probe))
            {
                Fail($"'{probe}' was added but not removed within {SyncTimeoutSeconds:0} s.");
                yield break;
            }

            Detail("the server rewrote its SmartWishbone.Data.yaml on the add and the remove (same entries, comments dropped).");
            Pass(isServer
                ? $"adding and removing '{probe}' works."
                : $"'{probe}' added through the server in {addSeconds:0.0} s and removed in {removeSeconds:0.0} s.");
        }

        // Does what Game.Logout does to the synced list, then has the server resend it: an admin's reset must never reach
        // the server (it did in the first 1.0 build and emptied the list for every player).
        private static IEnumerator CheckLogoutReset()
        {
            if (ZNet.instance.IsServer())
            {
                Skip("needs a client of a server.");
                yield break;
            }

            int before = TrackableData.Data.Count;
            int updates = TrackableData.receivedFromServerCount;
            string admin = ConfigSync.lockExempt ? "admin" : "not an admin";

            WishboneConfig.SetConfigDataWithoutEvent(string.Empty);
            yield return new WaitForSeconds(LogoutResetSettleSeconds);

            WishboneConfig.serverSyncInstance.RequestSync();
            float waited = 0f;

            while (TrackableData.receivedFromServerCount == updates && waited < SyncTimeoutSeconds)
            {
                yield return new WaitForSeconds(PollSeconds);
                waited += PollSeconds;
            }

            if (TrackableData.receivedFromServerCount == updates)
            {
                Fail($"the server did not resend its list within {SyncTimeoutSeconds:0} s.");
            }
            else if (TrackableData.Data.Count != before)
            {
                Fail($"after a logout reset the server sends {TrackableData.Data.Count} trackables, not {before} (you are {admin}); a dedi restart rebuilds it from SmartWishbone.Data.yaml.");
            }
            else
            {
                Pass($"a logout reset stayed local; the server still sends {before} trackables (you are {admin}).");
            }
        }

        // [perf]'s two-admin case: the test bot logs out and back in; this player's list must not change.
        private static IEnumerator CheckBotLogout()
        {
            if (Role() == "bot")
            {
                Skip("this is the bot.");
                yield break;
            }

            if (OtherPlayerCount() == 0 || !Chat.instance)
            {
                Skip("no other player online, so no test bot to log out.");
                yield break;
            }

            int before = TrackableData.Data.Count;
            string cacheBefore = WishboneConfig.CurrentDataCache.Value;
            int replies = CountChat(BotRejoinReply);
            int doneReplies = CountChat(BotRejoinDoneReply);

            Chat.instance.SendText(Talker.Type.Shout, BotRejoinCommand);
            usedBot = true;
            float waited = 0f;

            while (CountChat(BotRejoinReply) == replies && waited < BotAnswerTimeoutSeconds)
            {
                yield return new WaitForSeconds(PollSeconds);
                waited += PollSeconds;
            }

            if (CountChat(BotRejoinReply) == replies)
            {
                Skip($"no test bot answered '{BotRejoinCommand}' within {BotAnswerTimeoutSeconds:0} s (none following you).");
                yield break;
            }

            while (CountChat(BotRejoinDoneReply) == doneReplies && waited < BotRejoinTimeoutSeconds)
            {
                yield return new WaitForSeconds(PollSeconds);
                waited += PollSeconds;
            }

            if (CountChat(BotRejoinDoneReply) == doneReplies)
            {
                Fail($"the bot did not finish its rejoin within {BotRejoinTimeoutSeconds:0} s.");
                yield break;
            }

            int afterBot = TrackableData.Data.Count;

            if (!ZNet.instance.IsServer())
            {
                int updates = TrackableData.receivedFromServerCount;
                WishboneConfig.serverSyncInstance.RequestSync();
                float resync = 0f;

                while (TrackableData.receivedFromServerCount == updates && resync < SyncTimeoutSeconds)
                {
                    yield return new WaitForSeconds(PollSeconds);
                    resync += PollSeconds;
                }
            }

            int afterResync = TrackableData.Data.Count;
            bool cacheKept = !ZNet.instance.IsServer() || WishboneConfig.CurrentDataCache.Value == cacheBefore;

            SendPeerQuery();
            yield return new WaitForSeconds(PeerReplySeconds);
            bool peersAgree = ComparePeers(out string peers);
            bool botAnswered = peerAnswers.Any(a => a.Who != ServerLabel);

            if (afterBot != before || afterResync != before || !cacheKept)
            {
                Fail($"after the bot's logout you had {afterBot} trackables and the server sends {afterResync}, not {before}.");
            }
            else if (!peersAgree || !botAnswered)
            {
                Fail($"you kept {before} trackables, but after its rejoin: {(botAnswered ? peers : "the bot did not answer the list check")}");
            }
            else
            {
                Pass($"the bot logged out and back in ({waited:0} s); {peers}");
            }
        }

        // The other players and the server must hold the same trackable list and targets as this player.
        private static IEnumerator CheckPeers()
        {
            if (OtherPlayerCount() == 0 && ZNet.instance.IsServer())
            {
                Skip("no other peer to compare with.");
                yield break;
            }

            SendPeerQuery();
            yield return new WaitForSeconds(PeerReplySeconds);

            if (ComparePeers(out string summary))
            {
                Pass(summary);
            }
            else
            {
                Fail(summary);
            }
        }

        private static void SendPeerQuery()
        {
            peerAnswers.Clear();
            var request = new ZPackage();
            request.Write(PeerRequest);
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, PeerRpc, request);
        }

        private static bool ComparePeers(out string summary)
        {
            int count = TrackableData.Data.Count;
            string hash = DataHash();
            string targets = TargetList();
            var answers = peerAnswers.ToList();

            if (answers.Count == 0)
            {
                summary = $"no peer answered within {PeerReplySeconds:0} s (does the server run this SmartWishbone build?).";
                return false;
            }

            var differing = answers.Where(a => a.Hash != hash || a.Targets != targets).ToList();

            if (differing.Count > 0)
            {
                summary = $"you hold {count} trackables ({hash}); different: "
                    + string.Join("; ", differing.Select(a => $"{a.Who} {a.Count} ({a.Hash}) targets [{a.Targets}]").ToArray());
                return false;
            }

            summary = $"{string.Join(", ", answers.Select(a => a.Who).ToArray())} hold the same {count} trackables ({hash}) "
                + $"and {targets.Split(',').Length} targets as you.";
            return true;
        }

        private static void OnPeerRpc(long sender, ZPackage package)
        {
            byte kind = package.ReadByte();

            if (kind == PeerRequest)
            {
                if (sender == ZDOMan.GetSessionID())
                {
                    return;
                }

                var reply = new ZPackage();
                reply.Write(PeerReply);
                reply.Write(Player.m_localPlayer ? Player.m_localPlayer.GetPlayerName() : ServerLabel);
                reply.Write(TrackableData.Data.Count);
                reply.Write(DataHash());
                reply.Write(TargetList());
                ZRoutedRpc.instance.InvokeRoutedRPC(sender, PeerRpc, reply);
                return;
            }

            if (kind == PeerReply && running)
            {
                peerAnswers.Add(new PeerAnswer
                {
                    Who = package.ReadString(),
                    Count = package.ReadInt(),
                    Hash = package.ReadString(),
                    Targets = package.ReadString(),
                });
            }
        }

        private static string DataHash()
        {
            var lines = TrackableData.Data.Values
                .Select(t => $"{t.prefabName}|{t.displayItem}|{t.condition}|{t.range.ToString(System.Globalization.CultureInfo.InvariantCulture)}")
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();

            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", lines)));
                return BitConverter.ToString(hash).Replace("-", string.Empty).Substring(0, 12);
            }
        }

        private static string TargetList()
        {
            return string.Join(",", (TrackableData.targets ?? new Target[0]).Select(t => t.targetName).OrderBy(s => s, StringComparer.Ordinal).ToArray());
        }

        private static bool IsAdminOrHost()
        {
            return ZNet.instance && ZNet.instance.LocalPlayerIsAdminOrHost();
        }

        private static int OtherPlayerCount()
        {
            return Math.Max(0, ZNet.instance.GetPlayerList().Count - 1);
        }

        private static int CountChat(string text)
        {
            return Chat.instance ? Chat.instance.m_chatBuffer.Count(l => l.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0) : 0;
        }

        private static string PickProbePrefab()
        {
            foreach (var prefab in ZNetScene.instance.m_prefabs)
            {
                if (!prefab || !prefab.name.StartsWith(ProbePrefix, StringComparison.OrdinalIgnoreCase) || TrackableData.Data.ContainsKey(prefab.name))
                {
                    continue;
                }

                if (prefab.GetComponent<Destructible>() && !prefab.GetComponentInChildren<Beacon>(true))
                {
                    return prefab.name;
                }
            }

            return null;
        }

        private static bool HasFinder(Player player)
        {
            return player && player.m_seman != null && player.m_seman.m_statusEffects.Any(se => se is SE_CustomFinder);
        }

        private static string CurrentTargetName()
        {
            return TrackableSwitcher.currentTarget != null ? TrackableSwitcher.currentTarget.displayName : "everything";
        }

        private static string Display(string targetName)
        {
            var target = (TrackableData.targets ?? new Target[0]).FirstOrDefault(t => t.targetName == targetName);
            return target != null ? $"{target.displayName} ({targetName})" : targetName;
        }

        private static void RestorePlayer()
        {
            var player = Player.m_localPlayer;

            if (player && equippedWishbone != null)
            {
                player.UnequipItem(equippedWishbone, false);

                if (previousUtility != null && player.GetInventory().ContainsItem(previousUtility))
                {
                    player.EquipItem(previousUtility, false);
                }

                if (addedWishbone)
                {
                    player.GetInventory().RemoveItem(equippedWishbone);
                }

                Detail("put your utility slot back as it was.");
            }

            equippedWishbone = null;
            previousUtility = null;
            addedWishbone = false;

            TrackableSwitcher.RestoreTarget(savedTarget);
        }

        // Removes this session's test objects and any recorded by an earlier run in this world that are loaded here.
        // An id stays recorded until its object is removed or its ZDO is gone from this world.
        private static int Cleanup()
        {
            int removed = 0;
            var destroyed = new HashSet<string>();

            foreach (var vein in spawned)
            {
                if (!vein.Object)
                {
                    continue;
                }

                var ownView = vein.Object.GetComponent<ZNetView>();

                if (ownView && ownView.GetZDO() != null)
                {
                    destroyed.Add(ownView.GetZDO().m_uid.ToString());
                }

                if (DestroyObject(vein.Object))
                {
                    removed++;
                }
            }

            spawned.Clear();

            string path = SpawnedFilePath();

            if (!File.Exists(path))
            {
                return removed;
            }

            var keep = new List<string>();

            foreach (string line in File.ReadAllLines(path))
            {
                if (!TryParseZdoId(line, out ZDOID id) || destroyed.Contains(line.Trim()))
                {
                    continue;
                }

                var zdo = ZDOMan.instance.GetZDO(id);

                if (zdo == null)
                {
                    keep.Add(line);
                    continue;
                }

                var view = ZNetScene.instance.FindInstance(zdo);

                if (view && DestroyObject(view.gameObject))
                {
                    removed++;
                }
                else
                {
                    zdo.SetOwner(ZDOMan.GetSessionID());
                    ZDOMan.instance.DestroyZDO(zdo);
                    removed++;
                }
            }

            if (keep.Count > 0)
            {
                File.WriteAllLines(path, keep.ToArray());
            }
            else
            {
                File.Delete(path);
            }

            return removed;
        }

        private static bool DestroyObject(GameObject go)
        {
            var view = go.GetComponent<ZNetView>();

            if (view && view.IsValid())
            {
                view.ClaimOwnership();
                ZNetScene.instance.Destroy(go);
                return true;
            }

            UnityEngine.Object.Destroy(go);
            return true;
        }

        private static bool TryParseZdoId(string text, out ZDOID id)
        {
            id = ZDOID.None;
            var parts = text.Trim().Split(':');

            if (parts.Length != 2 || !long.TryParse(parts[0], out long user) || !uint.TryParse(parts[1], out uint number))
            {
                return false;
            }

            id = new ZDOID(user, number);
            return true;
        }

        private static void AppendSpawnedId(ZDOID id)
        {
            try
            {
                string path = SpawnedFilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(path, id + Environment.NewLine);
            }
            catch (Exception e)
            {
                SmartWishbonePlugin.Log.LogWarning($"{Tag}could not record spawned id {id}: {e.Message}");
            }
        }

        private static string SpawnedFilePath()
        {
            string world = ZNet.instance ? ZNet.instance.GetWorldName() ?? "unknown" : "unknown";

            foreach (char c in Path.GetInvalidFileNameChars())
            {
                world = world.Replace(c, '_');
            }

            return Path.Combine(Path.Combine(Paths.BepInExRootPath, ResultsFolder), SpawnedFilePrefix + world + ".txt");
        }

        private static string DllHash()
        {
            if (dllHash != null)
            {
                return dllHash;
            }

            try
            {
                using (var sha = SHA256.Create())
                {
                    byte[] hash = sha.ComputeHash(File.ReadAllBytes(typeof(WishboneTest).Assembly.Location));
                    dllHash = BitConverter.ToString(hash).Replace("-", string.Empty).Substring(0, 12);
                }
            }
            catch (Exception)
            {
                dllHash = "unknown";
            }

            return dllHash;
        }

        private static void Pass(string reason)
        {
            passed++;
            Result("PASS", reason);
        }

        private static void Fail(string reason)
        {
            failed++;
            Result("FAIL", reason);
        }

        private static void Skip(string reason)
        {
            skipped++;
            Result("SKIP", reason);
        }

        private static void Result(string verdict, string reason)
        {
            reported = true;
            Line($"{stepIndex}/{stepCount} {currentStep} {verdict} - {reason}");
        }

        private static void Line(string text)
        {
            string line = Tag + text;
            SmartWishbonePlugin.Log.LogMessage(line);

            if (output)
            {
                output.AddString(line);
            }

            WriteResult(line);
        }

        private static void Echo(string text)
        {
            if (output)
            {
                output.AddString(Tag + text);
            }
        }

        private static void Detail(string text)
        {
            WriteResult(Tag + "  " + text);
        }

        private static void WriteResult(string line)
        {
            try
            {
                string folder = Path.Combine(Paths.BepInExRootPath, ResultsFolder);
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, ResultsFile), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
            catch (Exception e)
            {
                SmartWishbonePlugin.Log.LogWarning($"{Tag}could not write the results file: {e.Message}");
            }
        }
    }
}
