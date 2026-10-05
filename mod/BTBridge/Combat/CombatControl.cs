using System.Linq;
using BattleTech;

namespace BTBridge.Combat
{
    public enum PlayerControl
    {
        /// <summary>Vanilla: the human plays. Combat state is still readable for advice.</summary>
        Human,
        /// <summary>The external agent decides every activation; the stock AI supplies suggestions.</summary>
        Agent,
        /// <summary>The stock AI plays the player's lance (AI vs AI), no agent involved.</summary>
        BuiltinAI,
    }

    public enum EnemyControl
    {
        /// <summary>Vanilla stock AI.</summary>
        StockAI,
        /// <summary>The external agent commands every AI team hostile to the player.</summary>
        Agent,
    }

    /// <summary>
    /// Who commands each side. Modes are latched when a mission builds its teams
    /// (EncounterLayerData.CreatePlayerOneTeam), so changes apply from the next mission.
    /// </summary>
    public static class CombatControl
    {
        public const string Player1Guid = "bf40fd39-ccf9-47c4-94a6-061809681140";

        public static PlayerControl RequestedPlayer = PlayerControl.Human;
        public static EnemyControl RequestedEnemy = EnemyControl.StockAI;

        /// <summary>Seconds to wait for the agent before taking the stock AI's suggestion; 0 waits forever.</summary>
        public static float DecisionTimeoutSeconds;

        public static PlayerControl ActivePlayer { get; private set; } = PlayerControl.Human;
        public static EnemyControl ActiveEnemy { get; private set; } = EnemyControl.StockAI;

        public static void OnMissionTeamsCreated()
        {
            ActivePlayer = RequestedPlayer;
            ActiveEnemy = RequestedEnemy;
        }

        public static bool PlayerIsAiDriven => ActivePlayer != PlayerControl.Human;

        public static Team PlayerTeam(CombatGameState combat) => combat?.Teams.FirstOrDefault(t => t.GUID == Player1Guid);

        /// <summary>"player", "enemy" (hostile to the player) or "other" (allied/neutral).</summary>
        public static string SideOf(Team team)
        {
            if (team == null)
            {
                return "other";
            }
            if (team.GUID == Player1Guid)
            {
                return "player";
            }
            var player = PlayerTeam(team.Combat);
            return player != null && player.IsEnemy(team) ? "enemy" : "other";
        }

        public static bool IsAgentTeam(Team team)
        {
            if (!(team is AITeam))
            {
                return false;
            }
            switch (SideOf(team))
            {
                case "player": return ActivePlayer == PlayerControl.Agent;
                case "enemy": return ActiveEnemy == EnemyControl.Agent;
                default: return false;
            }
        }

        /// <summary>The team whose eyes a side sees through: the player team, or the first hostile AI team.</summary>
        public static Team ViewerFor(CombatGameState combat, string side)
        {
            if (side == "enemy")
            {
                var player = PlayerTeam(combat);
                return combat.Teams.FirstOrDefault(t => player != null && player.IsEnemy(t) && t.units.Count > 0);
            }
            return PlayerTeam(combat) ?? combat.LocalPlayerTeam;
        }
    }
}
