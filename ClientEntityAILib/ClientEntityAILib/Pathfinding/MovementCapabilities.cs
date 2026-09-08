using System;

namespace ClientEntityAILib.Pathfinding
{
    /// <summary>
    /// Hard legality gate for what an entity can physically do - not a preference. An entity with
    /// only CanSwim set is already incapable of leaving water; there is no need for
    /// TerrainPreference to enforce that separately.
    /// </summary>
    [Flags]
    public enum MovementType
    {
        CanWalk = 1,
        CanFly = 2,
        CanSwim = 4,
        CanClimb = 8
    }

    /// <summary>
    /// Soft cost bias between terrain types, applied only when an entity's MovementType makes more
    /// than one terrain type legal (e.g. CanWalk | CanSwim). Both means no bias.
    /// </summary>
    public enum TerrainPreference
    {
        Water,
        Land,
        Both
    }
}
