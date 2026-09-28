using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SmartWishbone
{
    [HarmonyPatch(typeof(ObjectDB))]
    internal class ObjectDBPatch
    {
        internal const string wishboneEffectName = "Wishbone";

        [HarmonyPatch(nameof(ObjectDB.Awake)), HarmonyPostfix]
        public static void Awake_Postfix(ObjectDB __instance)
        {
            if (SceneManager.GetActiveScene().name != "main")
            {
                return;
            }

            SE_Finder vanillaWishboneEffect = null;
            SE_Finder newWishboneEffect = null;

            for (int i = 0; i < __instance.m_StatusEffects.Count; i++)
            {
                StatusEffect item = __instance.m_StatusEffects[i];

                if (!(item is SE_Finder oldWishboneEffect) || item.name != wishboneEffectName)
                {
                    continue;
                }

                vanillaWishboneEffect = oldWishboneEffect;
                newWishboneEffect = ScriptableObject.CreateInstance<SE_CustomFinder>();
                SE_CustomFinder.originalWishboneSprite = vanillaWishboneEffect.m_icon;

                try
                {
                    foreach (var field in typeof(SE_Finder).GetFields())
                    {
                        field.SetValue(newWishboneEffect, field.GetValue(oldWishboneEffect));
                    }

                    foreach (var field in typeof(StatusEffect).GetFields())
                    {
                        field.SetValue(newWishboneEffect, field.GetValue(oldWishboneEffect));
                    }

                    newWishboneEffect.name = oldWishboneEffect.name;

                    __instance.m_StatusEffects[i] = newWishboneEffect;
                }
                catch (System.Exception)
                {
                    Debug.LogError("Copying wishbone buff failed, aborting.");
                    __instance.m_StatusEffects[i] = item;
                }

                break;
            }

            if (newWishboneEffect == null || vanillaWishboneEffect == null)
            {
                Debug.LogError("Couldn't find vanilla wishbone buff. Something is wrong.");
                return;
            }

            foreach (GameObject gameObject in __instance.m_items)
            {
                var itemDrop = gameObject ? gameObject.GetComponent<ItemDrop>() : null;

                if (!itemDrop)
                {
                    continue;
                }

                var wishbone = itemDrop.m_itemData.m_shared;

                if (wishbone.m_equipStatusEffect == vanillaWishboneEffect)
                {
                    wishbone.m_equipStatusEffect = newWishboneEffect;

                    Helper.Log($"Replaced equip effect of {wishbone.m_name}");
                }

                if (wishbone.m_setStatusEffect == vanillaWishboneEffect)
                {
                    wishbone.m_setStatusEffect = newWishboneEffect;

                    Helper.Log($"Replaced equip effect of {wishbone.m_name}");
                }
            }

            TrackableData.UpdateTargets();
        }
    }
}