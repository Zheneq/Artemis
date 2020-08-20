using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace ArtemisServer.GameServer
{
    class Utils
    {
        public static Dictionary<int, int> GetActorIndexToDeltaHP(List<ClientResolutionAction> actions)
        {
            Dictionary<int, int> actorIndexToDeltaHP = new Dictionary<int, int>();
            foreach (var action in actions)
            {
                action.GetAllHitResults(out var actorHitResList, out var posHitResList);
                foreach (var targetedActor in actorHitResList)
                {
                    int actorIndex = targetedActor.Key.ActorIndex;
                    // TODO: how does absorb count here? (does it count at all, does absorb from previous phase somehow affect calculations?)
                    int deltaHP = targetedActor.Value.Healing - targetedActor.Value.Damage;
                    if (!actorIndexToDeltaHP.ContainsKey(actorIndex))
                    {
                        actorIndexToDeltaHP.Add(actorIndex, deltaHP);
                    }
                    else
                    {
                        actorIndexToDeltaHP[actorIndex] += deltaHP;
                    }
                }
            }
            return actorIndexToDeltaHP;
        }

        public static void Add<K>(Dictionary<ActorData, Dictionary<K, int>> dst, Dictionary<ActorData, Dictionary<K, int>> src)
        {
            foreach (var target in src)
            {
                ActorData targetActor = target.Key;
                if (!dst.ContainsKey(targetActor))
                {
                    dst[targetActor] = new Dictionary<K, int>();
                }
                foreach (var symbolToValue in target.Value)
                {
                    if (!dst[targetActor].ContainsKey(symbolToValue.Key))
                    {
                        dst[targetActor][symbolToValue.Key] = 0;
                    }
                    dst[targetActor][symbolToValue.Key] += symbolToValue.Value;
                }
            }
        }

        public static void Add<T>(Dictionary<ActorData, List<T>> dst, Dictionary<ActorData, List<T>> src)
        {
            foreach (var target in src)
            {
                ActorData targetActor = target.Key;
                if (!dst.ContainsKey(targetActor))
                {
                    dst[targetActor] = new List<T>(target.Value);
                }
                else
                {
                    dst[targetActor].AddRange(target.Value);
                }
            }
        }

        public static void Add<T>(Dictionary<ActorData, List<T>> dst, Dictionary<ActorData, T> src)
        {
            foreach (var target in src)
            {
                ActorData targetActor = target.Key;
                if (!dst.ContainsKey(targetActor))
                {
                    dst[targetActor] = new List<T> { target.Value };
                }
                else
                {
                    dst[targetActor].Add(target.Value);
                }
            }
        }

        public static Barrier ConsBarrier(
            ActorData caster,
            StandardBarrierData data,
            Vector3 targetPos,
            Vector3 facingDir,
            SequenceSource seqSource,
            List<GameObject> prefabOverride = null)
        {
            Log.Info($"Spawning barrier by {caster.DisplayName}: max duration {data.m_maxDuration}, max hits {data.m_maxHits}, end on caster death {data.m_endOnCasterDeath}");
            return new Barrier(
                    ArtemisServerResolutionManager.Get().NextBarrierGuid,
                    "",
                    targetPos,
                    facingDir,
                    data.m_width,
                    data.m_bidirectional,
                    data.m_blocksVision,
                    data.m_blocksAbilities,
                    data.m_blocksMovement,
                    data.m_blocksPositionTargeting,
                    data.m_considerAsCover,
                    data.m_maxDuration,
                    caster,
                    prefabOverride ?? data.m_barrierSequencePrefabs,
                    true,
                    data.m_onEnemyMovedThrough,
                    data.m_onAllyMovedThrough,
                    data.m_maxHits,
                    data.m_endOnCasterDeath,
                    seqSource,
                    caster.GetTeam());
        }

        public static ActorData GetActorByIndex(int actorIndex)
        {
            foreach (ActorData actor in GameFlowData.Get().GetActors())
            {
                if (actor.ActorIndex == actorIndex)
                {
                    return actor;
                }
            }
            return null;
        }

        public static Dictionary<int, ActorData> GetActorByIndex()
        {
            var actors = GameFlowData.Get().GetActors();
            var result = new Dictionary<int, ActorData>(actors.Count);
            foreach (ActorData actor in actors)
            {
                result.Add(actor.ActorIndex, actor);
            }
            return result;
        }
    }
}
