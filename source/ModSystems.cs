using CombatOverhaul.Armor;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.Client.NoObf;
using Vintagestory.Server;

namespace ToolsAnimations;

public sealed class ToolsAnimationsSystem : ModSystem
{
    public BlockBreakingSystemClient? ClientBlockBreakingSystem { get; private set; }
    public BlockBreakingSystemServer? ServerBlockBreakingSystem { get; private set; }

    public override void Start(ICoreAPI api)
    {
        api.RegisterItemClass("ToolsAnimations:Axe", typeof(Axe));
        api.RegisterItemClass("ToolsAnimations:Pickaxe", typeof(Pickaxe));

        api.RegisterBlockBehaviorClass("ToolsAnimations:Splittable", typeof(Splittable));
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        ClientBlockBreakingSystem = new(api);
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        ServerBlockBreakingSystem = new(api);
    }
}
