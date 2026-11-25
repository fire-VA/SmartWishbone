using UnityEngine;

namespace SmartWishbone
{
    internal class SE_CustomFinder : SE_Finder
    {
        // How often the closest matching beacon is searched for again.
        private const float BeaconUpdateIntervalSeconds = 1f;

        private float m_customUpdateBeaconTimer = 0f;

        internal static Sprite originalWishboneSprite;

        public static string GetDefaultMessageForTarget()
        {
            return GetMessageForTarget(Localization.instance.GetWishboneTranslation("WishboneStartMessageDefault"));
        }

        public static string GetMessageForTarget(string target)
        {
            return string.Format(Localization.instance.GetWishboneTranslation("WishboneStartMessage"), target);
        }

        public override void Setup(Character character)
        {
            bool setIcon = false;

            this.m_startMessageType = MessageHud.MessageType.TopLeft;

            if (TrackableSwitcher.currentTarget != null)
            {
                this.m_startMessage = GetMessageForTarget(TrackableSwitcher.currentTarget.displayName);

                if (TrackableSwitcher.currentTarget.displaySprite != null)
                {
                    this.m_icon = TrackableSwitcher.currentTarget.displaySprite;
                    setIcon = true;
                }
            }
            else
            {
                this.m_startMessage = GetDefaultMessageForTarget();
            }

            if (!setIcon)
            {
                this.m_icon = originalWishboneSprite;
            }

            base.Setup(character);
        }

        public override void UpdateStatusEffect(float dt)
        {
            this.m_customUpdateBeaconTimer += dt;

            if (this.m_customUpdateBeaconTimer > BeaconUpdateIntervalSeconds)
            {
                this.m_customUpdateBeaconTimer = 0f;
                Beacon beacon = BeaconHelper.FindClosestBeaconInRange(this.m_character.transform.position, TrackableSwitcher.currentTarget);

                if (beacon != this.m_beacon)
                {
                    this.m_beacon = beacon;

                    if (this.m_beacon)
                    {
                        this.m_lastDistance = Utils.DistanceXZ(this.m_character.transform.position, this.m_beacon.transform.position);
                        this.m_pingTimer = 0f;
                    }
                }
            }

            this.m_updateBeaconTimer = -dt;
            base.UpdateStatusEffect(dt);
        }
    }
}