using Theatrics;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using ArtemisServer.GameServer.Abilities;

namespace ArtemisServer.GameServer
{
    class ArtemisServerResolutionManager : MonoBehaviour
    {
        private static ArtemisServerResolutionManager instance;

        private Dictionary<ActorData, Dictionary<AbilityTooltipSymbol, int>> TargetedActorsThisTurn;
        private Dictionary<ActorData, Dictionary<AbilityTooltipSymbol, int>> TargetedActorsThisPhase;
        private List<ClientResolutionAction> ActionsThisTurn;
        private List<ClientResolutionAction> ActionsThisPhase;
        private List<ActorAnimation> Animations;
        private List<Barrier> Barriers;
        private Dictionary<ActorData, BoardSquarePathInfo> Dashes;
        internal AbilityPriority Phase { get; private set; }
        internal Turn Turn;

        private uint m_nextSeqSourceRootID = 0;
        private int m_nextBarrierGuid = 0;

        public uint NextSeqSourceRootID => m_nextSeqSourceRootID++;  // TODO check SequenceSource.s_nextID
        public int NextBarrierGuid => m_nextBarrierGuid++;

        private HashSet<long> TheatricsPendingClients = new HashSet<long>();

        protected virtual void Awake()
        {
            if (instance == null)
            {
                instance = this;
            }
        }

        private void AdvancePhase()
        {
            Phase++;
            if (Phase == AbilityPriority.DEPRICATED_Combat_Charge)
            {
                Phase++;
            }
        }

        public bool ResolveNextPhase()
        {
            bool lastPhase = false;

            TargetedActorsThisPhase = new Dictionary<ActorData, Dictionary<AbilityTooltipSymbol, int>>();
            ActionsThisPhase = new List<ClientResolutionAction>();
            Animations = new List<ActorAnimation>();
            Barriers = new List<Barrier>();
            Dashes = new Dictionary<ActorData, BoardSquarePathInfo>();

            var sab = Artemis.ArtemisServer.Get().SharedActionBuffer;

            if (Turn == null)
            {
                Turn = new Turn()
                {
                    TurnID = GameFlowData.Get().CurrentTurn
                };
                TargetedActorsThisTurn = new Dictionary<ActorData, Dictionary<AbilityTooltipSymbol, int>>();
                ActionsThisTurn = new List<ClientResolutionAction>();
            }

            while (ActionsThisPhase.Count == 0)
            {
                AdvancePhase();
                if (Phase >= AbilityPriority.NumAbilityPriorities)
                {
                    Log.Info("Abilities resolved");
                    lastPhase = true;
                    break;
                }
                Log.Info($"Resolving {Phase} abilities");

                foreach (ActorData actor in GameFlowData.Get().GetActors())
                {
                    GameFlowData.Get().activeOwnedActorData = actor;
                    ResolveAbilities(actor, Phase);
                }
                GameFlowData.Get().activeOwnedActorData = null;

                Utils.Add(TargetedActorsThisTurn, TargetedActorsThisPhase);
            }

            UpdateTheatricsPhase();

            if (lastPhase)
            {
                Turn = null;
                GameFlowData.Get().activeOwnedActorData = null;
                sab.Networkm_actionPhase = ActionBufferPhase.AbilitiesWait;
                sab.Networkm_abilityPhase = AbilityPriority.Prep_Defense;
                Phase = AbilityPriority.INVALID;
                return false;
            }

            if (Phase == AbilityPriority.Evasion)
            {
                ArtemisServerMovementManager.Get().ResolveDashes(Dashes);
            }

            if (Phase == AbilityPriority.Evasion)
            {
                ArtemisServerMovementManager.Get().SendDashes(Dashes);
            }

            SendToAll((short)MyMsgType.StartResolutionPhase, new StartResolutionPhase()
            {
                CurrentTurnIndex = GameFlowData.Get().CurrentTurn,
                CurrentAbilityPhase = Phase,
                NumResolutionActionsThisPhase = ActionsThisPhase.Count
            });

            foreach (Barrier barrier in Barriers)
            {
                BarrierManager.Get().AddBarrier(barrier, true, out var _);
                // TODO AddBarrier updates ability blocking. Should we update vision/movement/cover?
            }

            SendActions();

            sab.Networkm_abilityPhase = Phase;

            // TODO process ClientResolutionManager.SendResolutionPhaseCompleted
            return true;
        }

        private void SendActions()
        {
            // TODO friendly/hostile visibility
            Log.Info($"Sending {ActionsThisPhase.Count} actions");
            foreach (ClientResolutionAction action in ActionsThisPhase)
            {
                Log.Info($"Sending action: {action.GetDebugDescription()}, Caster actor: {action.GetCaster()?.ActorIndex}, Action: {action.GetSourceAbilityActionType()}");
                SendToAll((short)MyMsgType.SingleResolutionAction, new SingleResolutionAction()
                {
                    TurnIndex = GameFlowData.Get().CurrentTurn,
                    PhaseIndex = (int)Phase,
                    Action = action
                });
            }
            ActionsThisTurn.AddRange(ActionsThisPhase);
        }

        public void ResolveAbilities(ActorData actor, AbilityPriority priority)
        {
            AbilityData abilityData = actor.gameObject.GetComponent<AbilityData>();

            // I didn't find any code that calculates what an ability hits aside from UpdateTargeting which is
            // used to draw targeters on the client. In order for it to work on the server we need to
            // * set actor as active owned actor data -- calculations rely on this
            // * AppearAtBoardSquare to set actor's current board square
            // * patch TargeterUtils so that RemoveActorsInvisibleToClient isn't called on the server
            // * disable ActorData.IsVisibleToClient on server (break cover otherwise)
            // * ..?
            foreach (ActorTargeting.AbilityRequestData ard in actor.TeamSensitiveData_authority.GetAbilityRequestData())
            {
                Ability ability = abilityData.GetAbilityOfActionType(ard.m_actionType);

                if (ability.m_runPriority != priority)
                {
                    continue;
                }
                Log.Info($"Resolving {ability.m_abilityName} for {actor.DisplayName}");

                AbilityResolver resolver = GetAbilityResolver(actor, ability, priority, ard);
                resolver.Resolve();
                ActionsThisPhase.AddRange(resolver.Actions);
                Animations.AddRange(resolver.Animations);
                Utils.Add(TargetedActorsThisPhase, resolver.TargetedActors);
                Barriers.AddRange(resolver.Barriers);

                if (priority == AbilityPriority.Evasion && resolver.Dash != null)
                {
                    Dashes.Add(actor, resolver.Dash);
                }

                var e = ability.GetModdedEffectForEnemies();
                var a = ability.GetModdedEffectForAllies();
                var s = ability.GetModdedEffectForSelf();
                Log.Info($"\n" +
                    $"{ability.m_abilityName}: " +
                    $"\n\tEffect for enemies ({e?.m_applyEffect}):\n{DefaultJsonSerializer.Serialize(e?.m_effectData)}" +
                    $"\n\tEffect for allies ({a?.m_applyEffect}):\n{DefaultJsonSerializer.Serialize(a?.m_effectData)}" +
                    $"\n\tEffect for self ({s?.m_applyEffect}):\n{DefaultJsonSerializer.Serialize(s?.m_effectData)}");
            }
        }

        public IEnumerator WaitForTheatrics()
        {
            TheatricsPendingClients.Clear();

            // TODO Fix dash theatrics
            foreach (long clientId in TheatricsManager.Get().m_playerConnectionIdsInUpdatePhase)
            {
                TheatricsPendingClients.Add(clientId);
            }
            Log.Info($"Waiting for {TheatricsPendingClients.Count} ({TheatricsManager.Get().m_playerConnectionIdsInUpdatePhase.Count}) clients to perform theatrics");

            while (TheatricsPendingClients.Count > 0)  // TODO add timelimit
            {
                yield return new WaitForSeconds(1);
            }
        }

        private void UpdateTheatricsPhase()
        {
            while (Turn.Phases.Count < (int)Phase)
            {
                Turn.Phases.Add(new Phase(Turn)
                {
                    Index = (AbilityPriority)Turn.Phases.Count
                });
            }

            if (Phase < AbilityPriority.NumAbilityPriorities)
            {
                Dictionary<int, int> actorIndexToDeltaHP = Utils.GetActorIndexToDeltaHP(ActionsThisPhase);
                List<int> participants = new List<int>(actorIndexToDeltaHP.Keys);

                foreach (var action in ActionsThisPhase)
                {
                    int actorIndex = action.GetCaster().ActorIndex;
                    if (!participants.Contains(actorIndex))
                    {
                        participants.Add(actorIndex);
                    }
                }
                foreach (var anim in Animations)
                {
                    foreach (var actor in anim.HitActorsToDeltaHP)
                    {
                        int actorIndex = actor.Key.ActorIndex;
                        if (!participants.Contains(actorIndex))
                        {
                            participants.Add(actorIndex);
                            if (actor.Value != 0)
                            {
                                Log.Warning($"Found non-zero HitActorToDeltaHP (actorIndex={actorIndex} delta={actor.Value}) not present in actorIndexToDeltaHP!");
                                actorIndexToDeltaHP.Add(actorIndex, 0);
                            }
                        }
                    }
                }

                Phase phase = new Phase(Turn)
                {
                    Index = Phase,
                    ActorIndexToDeltaHP = actorIndexToDeltaHP,
                    ActorIndexToKnockback = new Dictionary<int, int>(), // TODO
                    Participants = participants,
                    Animations = Animations
                };

                Turn.Phases.Add(phase);
            }

            UpdateTheatrics();
        }

        private void UpdateTheatrics()
        {
            var Theatrics = TheatricsManager.Get();
            Theatrics.m_turn = Turn;
            Theatrics.m_turnToUpdate = Turn.TurnID;
            Theatrics.SetDirtyBit(uint.MaxValue);

            AbilityPriority phase = Phase == AbilityPriority.NumAbilityPriorities ? AbilityPriority.INVALID : Phase;

            Theatrics.PlayPhase(phase);
        }

        public void SendMovementActions(List<ClientResolutionAction> actions)
        {
            if (Phase != AbilityPriority.INVALID)
            {
                Log.Error($"SendMovementActions called in {Phase} phase! Ignoring");
                return;
            }

            SendToAll((short)MyMsgType.StartResolutionPhase, new StartResolutionPhase()
            {
                CurrentTurnIndex = GameFlowData.Get().CurrentTurn,
                CurrentAbilityPhase = Phase,
                NumResolutionActionsThisPhase = actions.Count
            });
            ActionsThisPhase = actions;
            SendActions();
        }

        public void SetDashMovementActions(List<ClientResolutionAction> actions)
        {
            if (Phase != AbilityPriority.Evasion)
            {
                Log.Error($"SetDashMovementActions called in {Phase} phase! Ignoring");
                return;
            }

            ActionsThisPhase.AddRange(actions);
        }

        public void OnClientResolutionPhaseCompleted(NetworkConnection conn, GameMessageManager.ClientResolutionPhaseCompleted msg)
        {
            Player player = GameFlow.Get().GetPlayerFromConnectionId(conn.connectionId);
            ActorData actor = GameFlowData.Get().FindActorByActorIndex(msg.ActorIndex);

            if (actor.gameObject.GetComponent<PlayerData>().m_player.m_connectionId != conn.connectionId)
            {
                Log.Warning($"OnClientResolutionPhaseCompleted: {actor.DisplayName} does not belong to player {player.m_accountId}!");
            }

            TheatricsPendingClients.Remove(player.m_accountId);
        }

        private AbilityResolver GetAbilityResolver(ActorData actor, Ability ability, AbilityPriority priority, ActorTargeting.AbilityRequestData abilityRequestData)
        {
            if (ability.m_abilityName == "Trick Shot")
            {
                Log.Info("AbilityResolver_TrickShot");
                return new AbilityResolver_TrickShot(actor, ability, priority, abilityRequestData);
            }
            else if (ability.m_abilityName == "Trapwire")
            {
                Log.Info("AbilityResolver_TrapWire");
                return new AbilityResolver_TrapWire(actor, ability, priority, abilityRequestData);
            }
            else if (ability.m_abilityName == "Backup Plan")
            {
                Log.Info("AbilityResolver_EvasionRoll");
                return new AbilityResolver_EvasionRoll(actor, ability, priority, abilityRequestData);
            }
            return new AbilityResolver(actor, ability, priority, abilityRequestData);
        }

        public void ApplyActions()
        {
            if (ActionsThisTurn == null)
            {
                Log.Error("ArtemisServerResolutionManager.ApplyTargets: No actions to apply!");
            }

            var results = new Dictionary<ActorData, List<Hit>>();
            foreach (var action in ActionsThisTurn)
            {
                action.GetAllHitResults(out var actorHitResults, out var posHitResults);  // TODO posHitResults.m_reactionsOnPosHit
                // TODO absorb in effects
                var hits = new Dictionary<ActorData, Hit>(actorHitResults.Count);
                foreach (var ahr in actorHitResults)
                {
                    hits.Add(ahr.Key, new Hit() { Caster = action.GetAllCaster(), HitResults = ahr.Value });
                }

                Utils.Add(results, hits);
            }

            Log.Info("Turn results:");
            foreach (var result in results)
            {
                ActorData target = result.Key;
                Log.Info($" - {target.DisplayName}");
                foreach (Hit hit in result.Value)
                {
                    int damage = hit.HitResults.Damage;
                    int healing = hit.HitResults.Healing;
                    int energy = hit.HitResults.TechPoints;
                    int absorb = 0; // TODO see above
                    int energyOnCaster = hit.HitResults.TechPointsOnCaster;
                    int deltaHP = Mathf.Min(absorb - damage, 0) + healing;
                    target.SetHitPoints(target.HitPoints + deltaHP);
                    target.SetTechPoints(target.TechPoints + energy);
                    hit.Caster.SetTechPoints(hit.Caster.TechPoints + energyOnCaster);
                    Log.Info($" - - dmg {damage}, abs {absorb}, hlg {healing}, deltaHP {deltaHP} ({target.HitPoints} total), nrg {energy} (+{energyOnCaster} on caster {hit.Caster.DisplayName})");
                }
            }
        }

        private void SendToAll(short msgType, MessageBase msg)
        {
            foreach (ActorData actor in GameFlowData.Get().GetActors())
            {
                //if (!actor.GetPlayerDetails().IsHumanControlled) { continue; }
                actor.connectionToClient?.Send(msgType, msg);
            }
        }

        protected virtual void OnDestroy()
        {
            if (instance == this)
            {
                instance = null;
            }
        }

        public static ArtemisServerResolutionManager Get()
        {
            return instance;
        }

        public class StartResolutionPhase : MessageBase
        {
            public int CurrentTurnIndex;
            public AbilityPriority CurrentAbilityPhase;
            public int NumResolutionActionsThisPhase;

            public override void Serialize(NetworkWriter writer)
            {
                writer.Write(CurrentTurnIndex);
                writer.Write((sbyte)CurrentAbilityPhase);
                writer.Write((sbyte)NumResolutionActionsThisPhase);
            }

            public override void Deserialize(NetworkReader reader)
            {
                CurrentTurnIndex = reader.ReadInt32();
                CurrentAbilityPhase = (AbilityPriority)reader.ReadSByte();
                NumResolutionActionsThisPhase = reader.ReadSByte();
            }
        }

        public class SingleResolutionAction : MessageBase
        {
            public int TurnIndex;
            public int PhaseIndex;
            public ClientResolutionAction Action;

            public override void Serialize(NetworkWriter writer)
            {
                writer.WritePackedUInt32((uint)TurnIndex);
                writer.Write((sbyte)PhaseIndex);
                IBitStream stream = new NetworkWriterAdapter(writer);
                Action.ClientResolutionAction_SerializeToStream(ref stream);
            }

            public override void Deserialize(NetworkReader reader)
            {
                TurnIndex = (int)reader.ReadPackedUInt32();
                PhaseIndex = reader.ReadSByte();
                IBitStream stream = new NetworkReaderAdapter(reader);
                Action = ClientResolutionAction.ClientResolutionAction_DeSerializeFromStream(ref stream);
            }
        }

        public class ResolutionActionsOutsideResolve : MessageBase
        {
            public List<ClientResolutionAction> Actions;

            public override void Serialize(NetworkWriter writer)
            {
                int num = Actions?.Count ?? 0;
                writer.Write((sbyte)num);
                if (num > 0)
                {
                    IBitStream stream = new NetworkWriterAdapter(writer);
                    foreach (ClientResolutionAction action in Actions)
                    {
                        action.ClientResolutionAction_SerializeToStream(ref stream);
                    }
                }
            }

            public override void Deserialize(NetworkReader reader)
            {
                int num = reader.ReadSByte();
                IBitStream stream = new NetworkReaderAdapter(reader);
                Actions = new List<ClientResolutionAction>(num);
                for (int i = 0; i < num; i++)
                {
                    Actions.Add(ClientResolutionAction.ClientResolutionAction_DeSerializeFromStream(ref stream));
                }
            }
        }

        private class Hit
        {
            public ClientActorHitResults HitResults;
            public ActorData Caster;
        }
    }
}
