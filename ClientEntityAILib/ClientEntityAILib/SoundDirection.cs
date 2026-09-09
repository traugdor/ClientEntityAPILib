namespace ClientEntityAILib
{
    /// <summary>
    /// A direction relative to whichever facing PlaySound is anchored to (the entity's own Yaw,
    /// or the player's, depending on relativeToEntity) - not a world-absolute compass direction.
    /// </summary>
    public enum SoundDirection
    {
        Front,
        FrontRight,
        Right,
        BackRight,
        Back,
        BackLeft,
        Left,
        FrontLeft
    }
}
