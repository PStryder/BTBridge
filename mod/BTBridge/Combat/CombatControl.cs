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

    /// <summary>
    /// Who controls the player's lance. The mode is read when a mission builds its teams
    /// (EncounterLayerData.CreatePlayerOneTeam), so changes apply from the next mission.
    /// </summary>
    public static class CombatControl
    {
        public const string Player1Guid = "bf40fd39-ccf9-47c4-94a6-061809681140";

        /// <summary>Mode for the next mission.</summary>
        public static PlayerControl Requested = PlayerControl.Human;

        /// <summary>Seconds to wait for the agent before taking the stock AI's suggestion; 0 waits forever.</summary>
        public static float DecisionTimeoutSeconds;

        /// <summary>Mode the current mission's player team was actually created with.</summary>
        public static PlayerControl ActiveMode { get; private set; } = PlayerControl.Human;

        public static void OnPlayerTeamCreated(PlayerControl mode) => ActiveMode = mode;

        public static bool IsAgentTeam(Team team) =>
            ActiveMode == PlayerControl.Agent && team is AITeam && team.GUID == Player1Guid;
    }
}
