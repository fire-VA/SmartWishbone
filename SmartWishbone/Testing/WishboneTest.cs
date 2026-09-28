using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using ServerSync;
using UnityEngine;

namespace SmartWishbone
{
    // F5 'wishbone_test': an in-game self-test that prints PASS / FAIL / SKIP lines to the console and the log.
    internal static class WishboneTest
    {
        private const string CommandName = "wishbone_test";
        private const string LogPrefix = "[WishboneTest] ";
        private const string WishboneItemName = "Wishbone";
        private const string SilverTarget = "SilverOre";
        private const string GoldTarget = "GoldOre";
        private const string ProbePrefix = "rock";
        private const float SpawnDistance = 6f;
        private const float SpawnSpacing = 5f;
        private const float BeaconSettleSeconds = 1f;
        private const float SyncTimeoutSeconds = 10f;
        private const float SyncPollSeconds = 0.25f;
        private const float LogoutResetSettleSeconds = 2f;

        private static readonly string[][] Veins =
        {
            new[] { "silvervein", SilverTarget },
            new[] { "goldvein", GoldTarget },
            new[] { "TrollFrost_Dead", GoldTarget },
        };

        private sealed class SpawnedVein
        {
            public GameObject Object;
            public string Prefab;
            public string ExpectedTarget;
        }

        private static readonly List<SpawnedVein> spawned = new List<SpawnedVein>();
        private static Terminal output;
        private static int passed;
        private static int failed;
        private static int skipped;
        private static bool running;

        internal static void Register()
        {
            new Terminal.ConsoleCommand(CommandName,
                "[Smart Wishbone] self-test: checks the wishbone swap and the server's trackable list, spawns a silver vein, "
                + "a gold vein and a frozen troll in front of you, checks which target finds each, switches targets, adds and "
                + "removes a trackable through the server, then removes what it spawned. "
                + "Usage: wishbone_test [keep|clean] (keep = leave the veins for a walk-around, clean = remove them)",
                args =>
                {
                    output = args.Context;
                    string mode = args.Length >= 2 ? args[1].ToLowerInvariant() : string.Empty;

                    if (mode == "clean")
                    {
                        Cleanup(false);
                        return;
                    }

                    if (running)
                    {
                        Print("already running.");
                        return;
                    }

                    if (!Player.m_localPlayer || !ZNetScene.instance || !ObjectDB.instance || !ZNet.instance)
                    {
                        Print("start it in a world, with your character spawned.");
                        return;
                    }

                    SmartWishbonePlugin.Instance.StartCoroutine(Run(mode == "keep"));
                }, isCheat: true);
        }

        private static IEnumerator Run(bool keep)
        {
            running = true;
            passed = 0;
            failed = 0;
            skipped = 0;

            try
            {
                Print($"Smart Wishbone {SmartWishbonePlugin.VERSION} self-test ({Role()}).");

                CheckStatusEffect();
                CheckData();

                Cleanup(true);
                SpawnVeins();
                yield return new WaitForSeconds(BeaconSettleSeconds);

                CheckTargets();
                CheckClosestBeacons();

                yield return CheckSwitching();
                yield return CheckServerRoundTrip();
                yield return CheckLogoutReset();

                if (keep)
                {
                    Print($"kept {spawned.Count} test object(s) and the equipped wishbone; '{CommandName} clean' removes the objects.");
                }
                else
                {
                    Cleanup(false);
                }

                Print($"done: {passed} passed, {failed} failed, {skipped} skipped.");
            }
            finally
            {
                running = false;
            }
        }

        private static string Role()
        {
            if (ZNet.instance.IsDedicated())
            {
                return "dedicated server";
            }

            return ZNet.instance.IsServer() ? "host or single player" : "client of a server";
        }

        private static void CheckStatusEffect()
        {
            var effect = ObjectDB.instance.GetStatusEffect(ObjectDBPatch.wishboneEffectName.GetStableHashCode());
            Check(effect is SE_CustomFinder,
                "the Wishbone status effect is Smart Wishbone's.",
                $"the Wishbone status effect is {(effect ? effect.GetType().Name : "missing")}, not Smart Wishbone's.");

            var prefab = ObjectDB.instance.GetItemPrefab(WishboneItemName);
            var drop = prefab ? prefab.GetComponent<ItemDrop>() : null;
            Check(drop && drop.m_itemData.m_shared.m_equipStatusEffect is SE_CustomFinder,
                "the Wishbone item equips it.",
                "the Wishbone item does not equip Smart Wishbone's status effect.");
        }

        private static void CheckData()
        {
            var data = TrackableData.Data;

            if (ZNet.instance.IsServer())
            {
                Check(data.Count > 0, $"{data.Count} trackables loaded.", "no trackables loaded (check SmartWishbone.Data.yaml).");
                return;
            }

            Check(data.Count > 0 && TrackableData.receivedFromServerCount > 0,
                $"the server's trackable list arrived: {data.Count} trackables, {TrackableData.receivedFromServerCount} update(s).",
                $"no trackable list from the server ({data.Count} trackables, {TrackableData.receivedFromServerCount} updates): config sync is not working.");
        }

        private static void SpawnVeins()
        {
            var player = Player.m_localPlayer;
            Vector3 forward = player.transform.forward;
            forward.y = 0f;
            forward.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, forward);

            for (int i = 0; i < Veins.Length; i++)
            {
                string prefabName = Veins[i][0];
                var prefab = ZNetScene.instance.GetPrefab(prefabName);

                if (!prefab)
                {
                    Skip($"this game has no '{prefabName}' prefab.");
                    continue;
                }

                Vector3 position = player.transform.position + forward * SpawnDistance + right * ((i - 1) * SpawnSpacing);
                position.y = ZoneSystem.instance.GetGroundHeight(position);

                var instance = UnityEngine.Object.Instantiate(prefab, position, Quaternion.LookRotation(-forward));
                spawned.Add(new SpawnedVein { Object = instance, Prefab = prefabName, ExpectedTarget = Veins[i][1] });
            }

            Print($"spawned {string.Join(", ", spawned.Select(v => v.Prefab).ToArray())} {SpawnDistance:0} m in front of you.");
        }

        private static void CheckTargets()
        {
            var targets = TrackableData.targets ?? new Target[0];

            foreach (var vein in spawned)
            {
                if (!TrackableData.Data.ContainsKey(vein.Prefab))
                {
                    Skip($"'{vein.Prefab}' is not in the trackable data.");
                    continue;
                }

                var beacons = vein.Object ? vein.Object.GetComponentsInChildren<Beacon>() : new Beacon[0];

                if (beacons.Length == 0)
                {
                    Check(false, string.Empty, $"'{vein.Prefab}' has no beacon.");
                    continue;
                }

                var matched = targets
                    .Where(t => beacons.Any(b => BeaconHelper.TryGetTrackable(t.possibleTargets, b, out _)))
                    .Select(t => t.targetName)
                    .ToList();

                Check(matched.Count == 1 && matched[0] == vein.ExpectedTarget,
                    $"'{vein.Prefab}' belongs to {Display(vein.ExpectedTarget)} only.",
                    $"'{vein.Prefab}' belongs to [{string.Join(", ", matched.Select(Display).ToArray())}], expected {Display(vein.ExpectedTarget)} only.");
            }
        }

        private static void CheckClosestBeacons()
        {
            var player = Player.m_localPlayer;

            foreach (string targetName in new[] { SilverTarget, GoldTarget })
            {
                var target = (TrackableData.targets ?? new Target[0]).FirstOrDefault(t => t.targetName == targetName);
                var expected = spawned.Where(v => v.ExpectedTarget == targetName && v.Object).ToList();

                if (target == null || expected.Count == 0)
                {
                    Skip($"no {Display(targetName)} target or test object to home in on.");
                    continue;
                }

                if (!target.possibleTargets.Values.Any(TrackableExtension.FulFillsCondition))
                {
                    var keys = target.possibleTargets.Values.Select(t => t.condition).Where(c => c != null).Distinct().ToArray();
                    Skip($"{Display(targetName)} is locked on this world until {string.Join(" / ", keys)} (EnforceWorldConditions); nothing changed.");
                    continue;
                }

                var beacon = BeaconHelper.FindClosestBeaconInRange(player.transform.position, target);
                bool found = beacon && expected.Any(v => beacon.transform.IsChildOf(v.Object.transform));
                string what = beacon
                    ? $"'{BeaconHelper.GetOwnerPrefabName(beacon)}' at {Vector3.Distance(player.transform.position, beacon.transform.position):0} m"
                    : "nothing";

                Check(found,
                    $"with {Display(targetName)} selected the wishbone homes in on the spawned {what}.",
                    $"with {Display(targetName)} selected the wishbone found {what}, not a spawned test object.");
            }
        }

        private static IEnumerator CheckSwitching()
        {
            var player = Player.m_localPlayer;

            if (!HasFinder(player))
            {
                var inventory = player.GetInventory();
                var wishbone = inventory.GetAllItems().FirstOrDefault(i => i.m_dropPrefab && i.m_dropPrefab.name == WishboneItemName)
                    ?? inventory.AddItem(WishboneItemName, 1, 1, 0, 0L, string.Empty, true);

                if (wishbone != null)
                {
                    player.EquipItem(wishbone);
                    Print("equipped a Wishbone in your utility slot (added one to your inventory if you had none).");
                }

                yield return null;
            }

            if (!HasFinder(player))
            {
                Check(false, string.Empty, "the Smart Wishbone effect is not on you after equipping a Wishbone.");
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

            Check(error == null && HasFinder(player),
                $"switching targets works ({before} -> {after}).",
                error != null ? $"switching targets threw {error.GetType().Name}: {error.Message}" : "the Smart Wishbone effect was lost when switching targets.");

            try
            {
                TrackableSwitcher.SwitchTarget(false);
            }
            catch (Exception)
            {
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

            TrackableData.ToggleTarget(probe);
            float waited = 0f;

            while (!TrackableData.Data.ContainsKey(probe) && waited < SyncTimeoutSeconds)
            {
                yield return new WaitForSeconds(SyncPollSeconds);
                waited += SyncPollSeconds;
            }

            bool added = TrackableData.Data.ContainsKey(probe);

            Check(added,
                isServer ? $"adding '{probe}' as a trackable works." : $"adding '{probe}' went to the server and came back in {waited:0.0} s.",
                $"'{probe}' was not added within {SyncTimeoutSeconds:0} s (UsersAllowedToAddTrackables may not allow you, or config sync is not working).");

            if (!added)
            {
                yield break;
            }

            TrackableData.ToggleTarget(probe);
            waited = 0f;

            while (TrackableData.Data.ContainsKey(probe) && waited < SyncTimeoutSeconds)
            {
                yield return new WaitForSeconds(SyncPollSeconds);
                waited += SyncPollSeconds;
            }

            Check(!TrackableData.Data.ContainsKey(probe),
                isServer ? $"removing '{probe}' works." : $"removing '{probe}' came back from the server too ({waited:0.0} s).",
                $"'{probe}' was not removed within {SyncTimeoutSeconds:0} s.");
        }

        // Does what Game.Logout does to the synced list, then has the server resend it: an admin client's reset must
        // never reach the server (it did in the first 1.0 build and emptied the list for every player).
        private static IEnumerator CheckLogoutReset()
        {
            if (ZNet.instance.IsServer())
            {
                Skip("the logout-reset check needs a client of a server.");
                yield break;
            }

            int before = TrackableData.Data.Count;
            int updates = TrackableData.receivedFromServerCount;
            string role = ConfigSync.lockExempt
                ? "you are an admin, so a pushed reset would have been accepted"
                : "you are not an admin, so the server would refuse a pushed reset anyway";

            WishboneConfig.SetConfigDataWithoutEvent(string.Empty);
            yield return new WaitForSeconds(LogoutResetSettleSeconds);

            WishboneConfig.serverSyncInstance.RequestSync();
            float waited = 0f;

            while (TrackableData.receivedFromServerCount == updates && waited < SyncTimeoutSeconds)
            {
                yield return new WaitForSeconds(SyncPollSeconds);
                waited += SyncPollSeconds;
            }

            if (TrackableData.receivedFromServerCount == updates)
            {
                Check(false, string.Empty, $"the server did not resend its list within {SyncTimeoutSeconds:0} s.");
                yield break;
            }

            Check(TrackableData.Data.Count == before,
                $"a logout reset stayed on this client: the server still sends {before} trackables ({role}).",
                $"after a logout reset the server sends {TrackableData.Data.Count} trackables instead of {before}: the reset reached the server ({role}). A dedi restart rebuilds the list from SmartWishbone.Data.yaml.");
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

        private static void Cleanup(bool quiet)
        {
            int removed = 0;

            foreach (var vein in spawned)
            {
                if (!vein.Object)
                {
                    continue;
                }

                var view = vein.Object.GetComponent<ZNetView>();

                if (view && view.IsValid() && ZNetScene.instance)
                {
                    view.ClaimOwnership();
                    ZNetScene.instance.Destroy(vein.Object);
                }
                else
                {
                    UnityEngine.Object.Destroy(vein.Object);
                }

                removed++;
            }

            spawned.Clear();

            if (!quiet || removed > 0)
            {
                Print($"removed {removed} test object(s).");
            }
        }

        private static void Check(bool ok, string pass, string fail)
        {
            if (ok)
            {
                passed++;
                Print("PASS " + pass);
            }
            else
            {
                failed++;
                Print("FAIL " + fail);
            }
        }

        private static void Skip(string reason)
        {
            skipped++;
            Print("SKIP " + reason);
        }

        private static void Print(string line)
        {
            if (output)
            {
                output.AddString(LogPrefix + line);
            }

            Debug.Log(LogPrefix + line);
        }
    }
}
