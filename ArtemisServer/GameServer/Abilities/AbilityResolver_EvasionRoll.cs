using System.Collections.Generic;
using UnityEngine;

namespace ArtemisServer.GameServer.Abilities
{
    class AbilityResolver_EvasionRoll : AbilityResolver
    {
        private BoardSquare m_dest;

        public AbilityResolver_EvasionRoll(ActorData actor, Ability ability, AbilityPriority priority, ActorTargeting.AbilityRequestData abilityRequestData)
            : base(actor, ability, priority, abilityRequestData)
        {
            if (abilityRequestData == null || abilityRequestData.m_targets.Count == 0)
            {
                Log.Error($"Invalid request data for AbilityResolver_EvasionRoll!");
                return;
            }

            m_dest = Board.Get().GetSquare(abilityRequestData.m_targets[0].GridPos);
        }

        protected override BoardSquarePathInfo MakeDash()
        {
            return KnockbackUtils.BuildStraightLineChargePath(m_caster, m_dest);
        }

        protected override void MakeBarriers(SequenceSource seqSource)
        {
            // TODO modded trapwire, modded effect on caster
        }

        protected override void Make_000C_X_0014_Z(out List<byte> x, out List<byte> y)
        {
            byte _x = (byte)m_caster.CurrentBoardSquare.x;
            byte _y = (byte)m_caster.CurrentBoardSquare.y;

            x = new List<byte>() { _x, _x };
            y = new List<byte>() { _y, _y };
        }

        protected override Dictionary<ActorData, int> MakeAnimActorToDeltaHP()
        {
            Dictionary<ActorData, int> res = base.MakeAnimActorToDeltaHP();
            res.Add(m_caster, 0);
            return res;
        }

        protected override Vector3 GetTargetPos()
        {
            return Board.Get().GetSquare(base.GetTargetPos()).GetWorldPosition();
        }
    }
}
