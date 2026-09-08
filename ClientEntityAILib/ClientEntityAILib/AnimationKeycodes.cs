namespace ClientEntityAILib
{
    /// <summary>
    /// Animation codes to attempt for an entity's three movement states. AnimManager.StartAnimation
    /// no-ops on an unrecognized code rather than throwing, so a name that doesn't match the spawned
    /// entity's actual animations is safe - the entity simply doesn't animate for that state.
    /// MoveFast falls back to MoveSlow's code when left null or empty.
    /// </summary>
    public class AnimationKeycodes
    {
        public string Idle;
        public string MoveSlow;
        public string MoveFast;
    }
}
