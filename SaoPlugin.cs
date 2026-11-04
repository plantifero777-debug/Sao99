using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.Hooks;

namespace SaoPlugin
{
    public class PlayerData
    {
        public int Level = 1;
        public int Xp = 0;
        public int Points = 0;
        public int[] Stats = new int[3];   // 0 = Fuerza, 1 = Agilidad, 2 = Vitalidad
        public bool Ghost = false;
        public int Slot1 = 1;              // habilidad del gesto "agacharse + atacar" (0 = ninguna)
        public int Slot2 = 0;              // habilidad del gesto "mirar arriba + atacar"
        public bool Gestos = true;
        public bool Bar = false;           // /barra (solo en memoria)
    }

    [ApiVersion(2, 1)]
    public partial class SaoPlugin : TerrariaPlugin
    {
        public override string Name => "SAO";
        public override string Author => "Proyecto SAO";
        public override string Description => "Niveles, fantasmas, partys y habilidades estilo Sword Art Online";
        public override Version Version => new Version(1, 0, 0);

        // =====================================================================
        //  VALORES DEL DISEÑO (cámbialos aquí)
        // =====================================================================
        public const int MaxLevel = 100;
        public const int PointsPerLevel = 3;
        public const int XpPerLevelFactor = 50;     // XP para pasar de nivel n a n+1 = 50 * n
        public const int EnemyLifeDivisor = 5;      // XP por enemigo = vida máxima / 5
        public const int CostStep = 5;              // el coste de un punto sube 1 cada 5 puntos de esa estadística

        // Revivir
        public const int ReviveCost = 4;            // cristales de vida
        // Enemigos con nivel
        public const float LifePerLevel = 0.03f;    // +3 % de vida por nivel
        public const double EliteSpawnChance = 0.03;
        public const int MaxElites = 2;
        public const int EliteLevelBonus = 5;
        public const float EliteLifeMult = 3f;
        public const int EliteXpMult = 5;
        public const double EliteCrystalChance = 0.005; // 0,5 %
        // Party
        public const float PartyXpBonus = 1.10f;
        public const int TeleportCooldownSec = 30;
        public const int InviteExpireSec = 60;
        // Efectos de estadísticas
        public const float StrengthDamagePerPoint = 0.02f; // +2 % de daño por punto de Fuerza
        public const int AgiSwiftnessAt = 10;       // buff de velocidad
        public const int VitRegenAt = 10;           // buff de regeneración
        public const int VitLifeforceAt = 25;       // buff de vida máxima +20 %
        public const int VitEnduranceAt = 50;       // buff de resistencia (menos daño)

        static readonly Color XpColor = new Color(135, 206, 250); // azul celeste
        static readonly string[] StatKeys = { "fue", "agi", "vit" };
        static readonly string[] StatNames = { "Fuerza", "Agilidad", "Vitalidad" };

        // Datos en memoria por jugador conectado (clave: índice del jugador)
        readonly Dictionary<int, PlayerData> players = new Dictionary<int, PlayerData>();
        // Último jugador que golpeó a cada NPC (clave: índice del NPC)
        readonly Dictionary<int, int> lastHitter = new Dictionary<int, int>();
        readonly Random rng = new Random();
        int tick;

        public SaoPlugin(Main game) : base(game) { }

        public override void Initialize()
        {
            ServerApi.Hooks.GamePostInitialize.Register(this, OnPostInit);
            ServerApi.Hooks.GameUpdate.Register(this, OnUpdate);
            ServerApi.Hooks.NpcSpawn.Register(this, OnNpcSpawn);
            ServerApi.Hooks.NpcKilled.Register(this, OnNpcKilled);
            ServerApi.Hooks.ServerLeave.Register(this, OnLeave);
            PlayerHooks.PlayerPostLogin += OnPostLogin;
            GetDataHandlers.NPCStrike += OnNpcStrike;
            GetDataHandlers.KillMe += OnKillMe;
            GetDataHandlers.TileEdit += OnTileEdit;
            GetDataHandlers.NewProjectile += OnNewProjectile;
            GetDataHandlers.ChestOpen += OnChestOpen;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ServerApi.Hooks.GamePostInitialize.Deregister(this, OnPostInit);
                ServerApi.Hooks.GameUpdate.Deregister(this, OnUpdate);
                ServerApi.Hooks.NpcSpawn.Deregister(this, OnNpcSpawn);
                ServerApi.Hooks.NpcKilled.Deregister(this, OnNpcKilled);
                ServerApi.Hooks.ServerLeave.Deregister(this, OnLeave);
                PlayerHooks.PlayerPostLogin -= OnPostLogin;
                GetDataHandlers.NPCStrike -= OnNpcStrike;
                GetDataHandlers.KillMe -= OnKillMe;
                GetDataHandlers.TileEdit -= OnTileEdit;
                GetDataHandlers.NewProjectile -= OnNewProjectile;
                GetDataHandlers.ChestOpen -= OnChestOpen;
            }
            base.Dispose(disposing);
        }

        // Se ejecuta cuando TShock ya ha cargado todo (base de datos y comandos propios)
        void OnPostInit(EventArgs args)
        {
            CreateTable();

            // TShock ya usa /p para el chat de sus partys: lo quitamos para usar el nuestro
            Commands.ChatCommands.RemoveAll(c => c.Names.Contains("p"));

            Commands.ChatCommands.Add(new Command(CmdStats, "stats"));
            Commands.ChatCommands.Add(new Command(CmdUp, "up"));
            Commands.ChatCommands.Add(new Command(CmdBar, "barra"));
            Commands.ChatCommands.Add(new Command(CmdRevive, "revivir"));
            Commands.ChatCommands.Add(new Command(CmdPartyCreate, "p"));
            Commands.ChatCommands.Add(new Command(CmdParti, "parti"));
            Commands.ChatCommands.Add(new Command(CmdPartyTp, "pt"));
            Commands.ChatCommands.Add(new Command(CmdYes, "si"));
            Commands.ChatCommands.Add(new Command(CmdNo, "no"));
            Commands.ChatCommands.Add(new Command(CmdSkills, "hab"));
            Commands.ChatCommands.Add(new Command("sao.admin", CmdSao, "sao"));
            Commands.ChatCommands.Add(new Command(CmdAyuda, "ayuda"));
        }

        // ================= Fórmulas =================

        static int XpForNext(int level) => XpPerLevelFactor * level;

        // Coste del siguiente punto en una estadística que ya tiene 'current' puntos
        static int CostOf(int current) => 1 + current / CostStep;

        // ================= Base de datos =================

        // Ejecuta una sentencia SQL con parámetros @0, @1... usando ADO.NET estándar
        // TShock puede dejar la conexión cerrada entre consultas: la abrimos si hace falta
        bool OpenDb()
        {
            if (TShock.DB.State == System.Data.ConnectionState.Open) return false;
            TShock.DB.Open();
            return true;
        }

        void CloseDb(bool opened)
        {
            if (opened) TShock.DB.Close();
        }

        void Exec(string sql, params object[] args)
        {
            bool opened = OpenDb();
            try
            {
                using (var cmd = TShock.DB.CreateCommand())
                {
                    cmd.CommandText = sql;
                    for (int i = 0; i < args.Length; i++)
                    {
                        var p = cmd.CreateParameter();
                        p.ParameterName = "@" + i;
                        p.Value = args[i];
                        cmd.Parameters.Add(p);
                    }
                    cmd.ExecuteNonQuery();
                }
            }
            finally
            {
                CloseDb(opened);
            }
        }

        void CreateTable()
        {
            Exec(
                "CREATE TABLE IF NOT EXISTS SaoData (" +
                "UserID INTEGER PRIMARY KEY, Level INTEGER, Xp INTEGER, Points INTEGER, " +
                "Fue INTEGER, Agi INTEGER, Vit INTEGER, Ghost INTEGER, " +
                "Slot1 INTEGER, Slot2 INTEGER, Gestos INTEGER)");

            // Si la tabla viene de una versión antigua, añade las columnas que falten
            string[] extra = { "Slot1 INTEGER DEFAULT 1", "Slot2 INTEGER DEFAULT 0", "Gestos INTEGER DEFAULT 1" };
            foreach (var col in extra)
            {
                try { Exec("ALTER TABLE SaoData ADD COLUMN " + col); }
                catch { /* la columna ya existe */ }
            }
        }

        PlayerData Load(int userId)
        {
            var d = new PlayerData();
            bool opened = OpenDb();
            try
            {
                using (var cmd = TShock.DB.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM SaoData WHERE UserID = @0";
                    var p = cmd.CreateParameter();
                    p.ParameterName = "@0";
                    p.Value = userId;
                    cmd.Parameters.Add(p);

                    using (var reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            d.Level = Convert.ToInt32(reader["Level"]);
                            d.Xp = Convert.ToInt32(reader["Xp"]);
                            d.Points = Convert.ToInt32(reader["Points"]);
                            d.Stats[0] = Convert.ToInt32(reader["Fue"]);
                            d.Stats[1] = Convert.ToInt32(reader["Agi"]);
                            d.Stats[2] = Convert.ToInt32(reader["Vit"]);
                            d.Ghost = Convert.ToInt32(reader["Ghost"]) == 1;
                            d.Slot1 = Convert.ToInt32(reader["Slot1"]);
                            d.Slot2 = Convert.ToInt32(reader["Slot2"]);
                            d.Gestos = Convert.ToInt32(reader["Gestos"]) == 1;
                        }
                    }
                }
            }
            finally
            {
                CloseDb(opened);
            }
            return d;
        }

        void Save(TSPlayer p, PlayerData d)
        {
            if (p?.Account == null) return;
            Exec(
                "REPLACE INTO SaoData (UserID, Level, Xp, Points, Fue, Agi, Vit, Ghost, Slot1, Slot2, Gestos) " +
                "VALUES (@0, @1, @2, @3, @4, @5, @6, @7, @8, @9, @10)",
                p.Account.ID, d.Level, d.Xp, d.Points, d.Stats[0], d.Stats[1], d.Stats[2],
                d.Ghost ? 1 : 0, d.Slot1, d.Slot2, d.Gestos ? 1 : 0);
        }

        PlayerData GetData(TSPlayer p)
        {
            if (p == null || !p.Active) return null;
            players.TryGetValue(p.Index, out var d);
            return d;
        }

        // ================= Eventos de jugador =================

        void OnPostLogin(PlayerPostLoginEventArgs args)
        {
            if (args.Player?.Account == null) return;
            var d = Load(args.Player.Account.ID);
            players[args.Player.Index] = d;
            if (d.Ghost)
                args.Player.SendWarningMessage("Sigues siendo un fantasma. Otro jugador puede revivirte con /revivir " + args.Player.Name + ".");
        }

        void OnLeave(LeaveEventArgs args)
        {
            LeaveParty(args.Who);
            invites.Remove(args.Who);
            players.Remove(args.Who);
            ghostInventory.Remove(args.Who);
            cooldowns.Remove(args.Who);
            lastUse.Remove(args.Who);
            lastTp.Remove(args.Who);
        }

        // ================= Temporizador (60 ticks = 1 s) =================

        void OnUpdate(EventArgs args)
        {
            tick++;
            CheckGestures();
            if (tick % 30 == 0) CheckGhostPickups();
            if (tick % 180 == 0) RefreshEffects();   // cada 3 s
            if (tick % 600 == 0) CleanupMobs();      // cada 10 s
        }

        void RefreshEffects()
        {
            foreach (var p in TShock.Players)
            {
                if (p == null || !p.Active) continue;
                var d = GetData(p);
                if (d == null) continue;

                if (d.Ghost)
                {
                    p.GodMode = true;
                    p.SetBuff(BuffID.Invisibility, 600);
                }
                if (d.Stats[0] >= FuerzaWrathAt) p.SetBuff(BuffID.Wrath, 600);
                if (d.Stats[1] >= AgiSwiftnessAt) p.SetBuff(BuffID.Swiftness, 600);
                if (d.Stats[2] >= VitRegenAt) p.SetBuff(BuffID.Regeneration, 600);
                if (d.Stats[2] >= VitLifeforceAt) p.SetBuff(BuffID.Lifeforce, 600);
                if (d.Stats[2] >= VitEnduranceAt) p.SetBuff(BuffID.Endurance, 600);

                if (d.Bar)
                {
                    string xp = d.Level >= MaxLevel ? "MAX" : d.Xp + "/" + XpForNext(d.Level);
                    p.SendData(PacketTypes.CreateCombatText, "Lv " + d.Level + " · " + xp + " XP",
                        (int)Color.LightGray.PackedValue, p.X, p.Y - 70f);
                }
            }
        }

        // ================= XP y niveles =================

        void AddXp(TSPlayer p, int amount)
        {
            var d = GetData(p);
            if (d == null || d.Level >= MaxLevel) return;

            d.Xp += amount;
            int levelsGained = 0;

            while (d.Level < MaxLevel && d.Xp >= XpForNext(d.Level))
            {
                d.Xp -= XpForNext(d.Level);
                d.Level++;
                d.Points += PointsPerLevel;
                levelsGained++;
            }
            if (d.Level >= MaxLevel) d.Xp = 0;

            Save(p, d);

            // Texto flotante azul celeste con la XP ganada
            p.SendInfoMessage("+" + amount + " XP ganada");

            if (levelsGained > 0)
            {
                p.SendData(PacketTypes.CreateCombatText, "¡NIVEL " + d.Level + "!", (int)Color.Gold.PackedValue, p.X, p.Y - 30f);
                p.SendSuccessMessage("¡Has subido al nivel " + d.Level + "! Puntos de habilidad disponibles: " + d.Points + ". Usa /up para repartirlos.");
            }
        }

        // Reparte la XP de una muerte: al asesino, o a toda su party con bonus
        void GrantXp(TSPlayer killer, int baseXp)
        {
            if (partyOf.TryGetValue(killer.Index, out var party))
            {
                int xp = (int)Math.Ceiling(baseXp * PartyXpBonus);
                foreach (int idx in party.Members.ToArray())
                {
                    var m = TShock.Players[idx];
                    if (m == null || !m.Active) continue;
                    AddXp(m, xp);
                }
            }
            else
            {
                AddXp(killer, baseXp);
            }
        }

        // ================= Comandos: /stats, /up, /barra =================

        void CmdStats(CommandArgs a)
        {
            var d = GetData(a.Player);
            if (d == null)
            {
                a.Player.SendErrorMessage("Debes iniciar sesión para ver tus estadísticas.");
                return;
            }

            string xpLine = d.Level >= MaxLevel
                ? "Nivel máximo alcanzado."
                : "XP: " + d.Xp + "/" + XpForNext(d.Level);

            a.Player.SendInfoMessage("Nivel " + d.Level + " | " + xpLine + (d.Ghost ? " | FANTASMA" : ""));
            a.Player.SendInfoMessage("Puntos disponibles: " + d.Points);
            for (int i = 0; i < StatKeys.Length; i++)
            {
                a.Player.SendInfoMessage(StatNames[i] + " (" + StatKeys[i] + "): " + d.Stats[i] +
                    "  [siguiente punto: " + CostOf(d.Stats[i]) + "]");
            }
        }

        void CmdUp(CommandArgs a)
        {
            var d = GetData(a.Player);
            if (d == null)
            {
                a.Player.SendErrorMessage("Debes iniciar sesión para usar /up.");
                return;
            }

            if (a.Parameters.Count == 0)
            {
                ShowUpHelp(a.Player, d);
                return;
            }

            int idx = Array.IndexOf(StatKeys, a.Parameters[0].ToLower());
            if (idx < 0)
            {
                a.Player.SendErrorMessage("Estadística no válida: " + a.Parameters[0]);
                ShowUpHelp(a.Player, d);
                return;
            }

            int amount = 1;
            if (a.Parameters.Count > 1)
            {
                if (!int.TryParse(a.Parameters[1], out amount) || amount <= 0)
                {
                    a.Player.SendErrorMessage("La cantidad debe ser un número positivo.");
                    ShowUpHelp(a.Player, d);
                    return;
                }
            }

            // El coste total se calcula punto a punto (cada uno cuesta más que el anterior)
            int cost = 0;
            for (int i = 0; i < amount; i++) cost += CostOf(d.Stats[idx] + i);

            if (cost > d.Points)
            {
                a.Player.SendErrorMessage("Necesitas " + cost + " puntos y tienes " + d.Points + ".");
                ShowUpHelp(a.Player, d);
                return;
            }

            d.Points -= cost;
            d.Stats[idx] += amount;
            Save(a.Player, d);

            a.Player.SendSuccessMessage(StatNames[idx] + " +" + amount + " (gastaste " + cost + " puntos). " +
                "Ahora: " + d.Stats[idx] + ". Puntos restantes: " + d.Points + ".");
        }

        void ShowUpHelp(TSPlayer p, PlayerData d)
        {
            p.SendInfoMessage("Uso: /up <estadística> [cantidad]");
            p.SendInfoMessage("fue = Fuerza (más daño a partir de " + FuerzaWrathAt + " puntos)");
            p.SendInfoMessage("agi = Agilidad (velocidad a partir de " + AgiSwiftnessAt + " puntos)");
            p.SendInfoMessage("vit = Vitalidad (regeneración, vida y resistencia con más puntos)");
            p.SendInfoMessage("Ejemplos: /up fue 2   |   /up vit");
            p.SendInfoMessage("Puntos disponibles: " + d.Points);
        }

        void CmdBar(CommandArgs a)
        {
            var d = GetData(a.Player);
            if (d == null)
            {
                a.Player.SendErrorMessage("Debes iniciar sesión.");
                return;
            }
            d.Bar = !d.Bar;
            a.Player.SendInfoMessage("Barra de nivel: " + (d.Bar ? "activada" : "desactivada"));
        }
    }
}
