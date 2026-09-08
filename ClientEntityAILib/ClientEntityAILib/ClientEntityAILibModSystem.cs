using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace ClientEntityAILib
{
    public class ClientEntityAILibModSystem : ModSystem
    {

        // Called on server and client
        // Useful for registering block/entity classes on both sides
        public override void Start(ICoreAPI api)
        {
            Mod.Logger.Notification("ClientEntityAILib is Active!");
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            Mod.Logger.Notification("ClientEntityAILib is Active!");
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            Mod.Logger.Notification("ClientEntityAILib is Active!");
        }

    }
}
