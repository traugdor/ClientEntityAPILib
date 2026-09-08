using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;

namespace ClientEntityAILib
{
    internal static class EntitySpeedDerivation
    {
        // Remedy & Ruin's own shipped, hand-tuned DrifterBehavior speeds - used whenever an
        // entity's JSON doesn't define a matching AI task to derive a speed from.
        internal const double FallbackSlowSpeed = 1.2;
        internal const double FallbackFastSpeed = 3.0;

        private const string SlowTaskCode = "wander";
        private const string FastTaskCode = "seekentity";

        public static (double slowSpeed, double fastSpeed) Derive(EntityProperties props)
        {
            double groundDragFactor = DeriveGroundDragFactor(props);

            double? slowMoveSpeed = FindTaskMoveSpeed(props, SlowTaskCode);
            double? fastMoveSpeed = FindTaskMoveSpeed(props, FastTaskCode);

            double slowSpeed = slowMoveSpeed.HasValue ? ToBlocksPerSecond(slowMoveSpeed.Value, groundDragFactor) : FallbackSlowSpeed;
            double fastSpeed = fastMoveSpeed.HasValue ? ToBlocksPerSecond(fastMoveSpeed.Value, groundDragFactor) : FallbackFastSpeed;

            return (slowSpeed, fastSpeed);
        }

        private static double DeriveGroundDragFactor(EntityProperties props)
        {
            JsonObject physics = props?.Attributes?["physics"];
            double multiplier = (physics != null && physics.Exists) ? physics["groundDragFactor"].AsDouble(1.0) : 1.0;
            return 0.3 * multiplier;
        }

        private static double ToBlocksPerSecond(double moveSpeed, double groundDragFactor)
        {
            return moveSpeed * 60.0 * (1.0 - groundDragFactor) / groundDragFactor;
        }

        private static double? FindTaskMoveSpeed(EntityProperties props, string taskCode)
        {
            JsonObject[] behaviors = props?.Server?.BehaviorsAsJsonObj;
            if (behaviors == null) return null;

            for (int i = 0; i < behaviors.Length; i++)
            {
                JsonObject behavior = behaviors[i];
                if (behavior["code"].AsString() != "taskai") continue;

                JsonObject aitasks = behavior["aitasks"];
                if (!aitasks.Exists) continue;

                foreach (JsonObject task in aitasks)
                {
                    if (task["code"].AsString() == taskCode && task["movespeed"].Exists)
                    {
                        return task["movespeed"].AsDouble();
                    }
                }
            }

            return null;
        }
    }
}
