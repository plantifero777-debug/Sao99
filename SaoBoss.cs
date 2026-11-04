using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using TShockAPI;

namespace SaoPlugin
{
    public partial class SaoPlugin
    {
        public const double BossCrystalChance = 0.30;   // 30 % por derrota de jefe

        // Todos los jugadores que han golpeado a cada NPC (clave: índice del NPC)
        readonly Dictionary<int, HashSet<int>> npcParticipants = new Dictionary<int, HashSet<int>>();

        // Registra un golpe: último golpe (cristales de élite) y participación (reparto de XP)
        void RegisterHit(int npcIndex, int playerIndex)
        {
            if (npcIndex < 0 || npcIndex >= Main.npc.Length) return;

            lastHitter[npcIndex] = playerIndex;

            if (!npcParticipants.TryGetValue(npcIndex, out var set))
            {
                set = new HashSet<int>();
                npcParticipants[npcIndex] = set;
            }
            set.Add(playerIndex);
        }

        // Saca la lista de participantes de un NPC que acaba de morir (solo jugadores activos)
        List<int> TakeParticipants(int npcIndex)
        {
            var result = new List<int>();
            if (npcParticipants.TryGetValue(npcIndex, out var set))
            {
                foreach (int idx in set)
                {
                    var p = TShock.Players[idx];
                    if (p != null && p.Active) result.Add(idx);
                }
            }
            npcParticipants.Remove(npcIndex);
            return result;
        }

        // Reparte la XP entre todos los participantes por igual (mínimo 1 cada uno).
        // Si un participante está en party, la party entera recibe su parte con +10 %, sin duplicados.
        void DistributeXp(List<int> participants, int totalXp)
        {
            if (participants.Count == 0 || totalXp <= 0) return;

            int share = Math.Max(1, totalXp / participants.Count);
            var rewarded = new HashSet<int>();

            foreach (int idx in participants)
            {
                if (rewarded.Contains(idx)) continue;

                if (partyOf.TryGetValue(idx, out var party))
                {
                    int gained = (int)Math.Ceiling(share * PartyXpBonus);
                    foreach (int m in party.Members.ToArray())
                    {
                        if (rewarded.Contains(m)) continue;
                        var mp = TShock.Players[m];
                        if (mp == null || !mp.Active) continue;
                        rewarded.Add(m);
                        AddXp(mp, gained);
                    }
                }
                else
                {
                    var p = TShock.Players[idx];
                    if (p == null || !p.Active) continue;
                    rewarded.Add(idx);
                    AddXp(p, share);
                }
            }
        }

        // Muerte de un jefe: reparto de XP y cristal de vida al 30 % para quien dio el último golpe
        void HandleBossKill(NPC npc, int totalXp, int killerIdx, List<int> participants)
        {
            DistributeXp(participants, totalXp);

            if (rng.NextDouble() < BossCrystalChance)
            {
                var killer = (killerIdx >= 0 && killerIdx < TShock.Players.Length) ? TShock.Players[killerIdx] : null;
                if (killer != null && killer.Active)
                {
                    killer.GiveItem(ItemID.LifeCrystal, 1);
                    TShock.Utils.Broadcast("¡" + killer.Name + " ha conseguido un cristal de vida del jefe!", Color.LightGreen);
                }
            }
        }

        // Quita participantes de NPCs que ya no existen (despawn sin morir)
        void CleanupParticipants()
        {
            foreach (int id in npcParticipants.Keys.ToList())
            {
                if (id < 0 || id >= Main.npc.Length || !Main.npc[id].active) npcParticipants.Remove(id);
            }
        }
    }
}
