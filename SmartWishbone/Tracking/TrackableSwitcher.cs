using BepInEx;
using HarmonyLib;
using System.Linq;
using UnityEngine;

namespace SmartWishbone
{
    [HarmonyPatch(typeof(Player))]
    internal class PlayerPatch
    {
        [HarmonyPatch(nameof(Player.SetLocalPlayer)), HarmonyPostfix]
        public static void SetLocalPlayer_Postfix(Player __instance)
        {
            if (Player.m_localPlayer != __instance)
            {
                return;
            }

            TrackableSwitcher.UpdateTarget();
        }
    }

    internal class TrackableSwitcher
    {
        internal static Target currentTarget;

        private const string playerPrefsCurrentTargetKey = "goldenrevolver.smartWishbone.currentTarget";

        internal static string GetCurrentTarget()
        {
            return PlayerPrefs.GetString(playerPrefsCurrentTargetKey, null);
        }

        internal static void RemoveCurrentTarget()
        {
            currentTarget = null;
            PlayerPrefs.DeleteKey(playerPrefsCurrentTargetKey);
        }

        internal static void SetCurrentTarget(Target target)
        {
            currentTarget = target;
            PlayerPrefs.SetString(playerPrefsCurrentTargetKey, target.targetName);
        }

        internal static void UpdateTarget()
        {
            InternalSwitchTarget(0);
        }

        internal static void RestoreTarget(string targetName)
        {
            if (targetName.IsNullOrWhiteSpace())
            {
                RemoveCurrentTarget();
            }
            else
            {
                PlayerPrefs.SetString(playerPrefsCurrentTargetKey, targetName);
            }

            UpdateTarget();
        }

        internal static void SwitchTarget(bool forward)
        {
            InternalSwitchTarget(forward ? 1 : -1);
        }

        private static void InternalSwitchTarget(int positionChange)
        {
            TrySwitchToNextTarget(positionChange);

            if (!Player.m_localPlayer || Player.m_localPlayer.m_seman == null)
            {
                return;
            }

            for (int i = Player.m_localPlayer.m_seman.m_statusEffects.Count - 1; i >= 0; i--)
            {
                StatusEffect item = Player.m_localPlayer.m_seman.m_statusEffects[i];

                if (item is SE_CustomFinder)
                {
                    Player.m_localPlayer.m_seman.RemoveStatusEffect(item, true);
                    Player.m_localPlayer.m_seman.AddStatusEffect(item);
                }
            }
        }

        internal static void TrySwitchToNextTarget(int positionChange)
        {
            if (!InternalTrySwitchToNextTarget(positionChange))
            {
                RemoveCurrentTarget();
            }
        }

        internal static bool InternalTrySwitchToNextTarget(int positionChange)
        {
            string lastTarget = GetCurrentTarget();

            if (lastTarget.IsNullOrWhiteSpace())
            {
                if (positionChange == 0)
                {
                    return false;
                }
                else if (positionChange > 0)
                {
                    return SearchForNextUsefulIndex(-1, true);
                }
                else
                {
                    return SearchForNextUsefulIndex(TrackableData.targets.Length, false);
                }
            }

            for (int i = 0; i < TrackableData.targets.Length; i++)
            {
                var target = TrackableData.targets[i];

                if (lastTarget != target.targetName)
                {
                    continue;
                }

                if (positionChange == 0)
                {
                    if (target.possibleTargets.Values.Any(TrackableExtension.FulFillsCondition))
                    {
                        SetCurrentTarget(target);
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                }

                // if this doesn't work, switch to universal search
                return SearchForNextUsefulIndex(i, positionChange > 0);
            }

            return false;
        }

        internal static bool SearchForNextUsefulIndex(int index, bool forward)
        {
            int indexToCheck = index;
            bool isLast = forward && indexToCheck == TrackableData.targets.Length - 1;
            bool isFirst = !forward && indexToCheck == 0;

            while (!isLast && !isFirst)
            {
                if (forward)
                {
                    indexToCheck++;
                }
                else
                {
                    indexToCheck--;
                }

                var newTarget = TrackableData.targets[indexToCheck];

                if (newTarget.possibleTargets.Values.Any(TrackableExtension.FulFillsCondition))
                {
                    SetCurrentTarget(newTarget);
                    return true;
                }

                isLast = forward && indexToCheck == TrackableData.targets.Length - 1;
                isFirst = !forward && indexToCheck == 0;
            }

            return false;
        }
    }
}