using CombatOverhaul;
using CombatOverhaul.Animations;
using CombatOverhaul.Colliders;
using CombatOverhaul.Implementations;
using CombatOverhaul.Inputs;
using CombatOverhaul.MeleeSystems;
using OpenTK.Mathematics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace ToolsAnimations;

public class AxeStats
{
    public string ReadyAnimation { get; set; } = "";
    public string IdleAnimation { get; set; } = "";
    public string SwingForwardAnimation { get; set; } = "";
    public string SwingBackAnimation { get; set; } = "";
    public string SplitAnimation { get; set; } = "";
    public string SplitBackAnimation { get; set; } = "";
    public string SwingTpAnimation { get; set; } = "";
    public string SplitTpAnimation { get; set; } = "";
    public bool RenderingOffset { get; set; } = false;
    public float[] Collider { get; set; } = Array.Empty<float>();
    public Dictionary<string, string> HitParticleEffects { get; set; } = new();
    public float HitStaggerDurationMs { get; set; } = 100;
    public bool CanSplitLogs { get; set; } = true;
    public bool HandleLMBInputs { get; set; } = true;

    public MeleeAttackStats Attack { get; set; } = new();
    public string AttackAnimation { get; set; } = "";
    public string AttackTpAnimation { get; set; } = "";
    public float AnimationStaggerOnHitDurationMs { get; set; } = 100;

    public bool TwoHanded { get; set; } = false;
}

public enum AxeState
{
    Idle,
    SwingForward,
    SwingBack,
    SplittingWindUp,
    Splitting,
    SplittingBack,
    AttackWindup,
    Attacking,
    AttackCooldown
}

public class AxeClient : IClientWeaponLogic, IOnGameTick, IRestrictAction
{
    public AxeClient(ICoreClientAPI api, Axe item)
    {
        Item = item;
        ItemId = item.Id;
        Stats = item.Attributes.AsObject<AxeStats>();
        Api = api;

        Collider = new(Stats.Collider);

        CombatOverhaulSystem system = api.ModLoader.GetModSystem<CombatOverhaulSystem>();
        ToolsAnimationsSystem toolSystem = api.ModLoader.GetModSystem<ToolsAnimationsSystem>();
        SoundsSystem = system.ClientSoundsSynchronizer ?? throw new Exception();
        BlockBreakingNetworking = toolSystem.ClientBlockBreakingSystem ?? throw new Exception();
        BlockBreakingSystem = new(api);

        MeleeAttack = new(api, Stats.Attack);

#if DEBUG
        DebugWindowManager.RegisterCollider(item.Code.ToString(), "tool head", value => Collider = value, () => Collider);
#endif
    }
    public int ItemId { get; }
    public DirectionsConfiguration DirectionsType => DirectionsConfiguration.None;

    public virtual void OnSelected(ItemSlot slot, EntityPlayer player, bool mainHand, ref int state)
    {

    }
    public virtual void OnDeselected(EntityPlayer player, bool mainHand, ref int state)
    {
        AnimationBehavior?.StopVanillaAnimation(Stats.SwingTpAnimation, mainHand);
    }
    public virtual void OnRegistered(ActionsManagerPlayerBehavior behavior, ICoreClientAPI api)
    {
        PlayerBehavior = behavior;
        AnimationBehavior = behavior.entity.GetBehavior<FirstPersonAnimationsBehavior>();
        TpAnimationBehavior = behavior.entity.GetBehavior<ThirdPersonAnimationsBehavior>();
    }
    public virtual void RenderDebugCollider(ItemSlot inSlot, IClientPlayer byPlayer)
    {
        /*if (DebugWindowManager._currentCollider != null)
        {
            DebugWindowManager._currentCollider.Value.Transform(byPlayer.Entity.Pos, byPlayer.Entity, inSlot, Api, right: true)?.Render(Api, byPlayer.Entity, ColorUtil.ColorFromRgba(255, 125, 125, 255));
            return;
        }*/

        Collider.Transform(byPlayer.Entity.Pos, byPlayer.Entity, inSlot, Api, right: true)?.Render(Api, byPlayer.Entity);
    }

    public virtual void OnGameTick(ItemSlot slot, EntityPlayer player, ref int state, bool mainHand)
    {
        if (state == (int)AxeState.Attacking)
        {
            TryAttack(slot, player, mainHand);
        }
    }

    public bool RestrictRightHandAction() => PlayerBehavior?.GetState(mainHand: false) != (int)AxeState.Idle;
    public bool RestrictLeftHandAction() => PlayerBehavior?.GetState(mainHand: true) != (int)AxeState.Idle;

    protected AxeStats Stats;
    protected LineSegmentCollider Collider;
    protected ICoreClientAPI Api;
    protected FirstPersonAnimationsBehavior? AnimationBehavior;
    protected ThirdPersonAnimationsBehavior? TpAnimationBehavior;
    protected ActionsManagerPlayerBehavior? PlayerBehavior;
    protected SoundsSynchronizerClient SoundsSystem;
    protected BlockBreakingSystemClient BlockBreakingNetworking;
    protected BlockBreakingController BlockBreakingSystem;
    protected Axe Item;
    protected TimeSpan SwingStart;
    protected TimeSpan SwingEnd;
    protected TimeSpan ExtraSwingTime;
    protected readonly TimeSpan MaxDelta = TimeSpan.FromSeconds(0.1);
    protected readonly MeleeAttack MeleeAttack;

    [ActionEventHandler(EnumEntityAction.LeftMouseDown, ActionState.Active)]
    protected virtual bool Swing(ItemSlot slot, EntityPlayer player, ref int state, ActionEventData eventData, bool mainHand, AttackDirection direction)
    {
        if (eventData.AltPressed && !mainHand) return false;
        if (player.BlockSelection?.Block == null) return false;
        if (Stats.TwoHanded && !CheckForOtherHandEmpty(mainHand, player)) return false;
        if (ActionRestricted(player, mainHand)) return false;

        switch ((AxeState)state)
        {
            case AxeState.Idle:
                {
                    AnimationBehavior?.Play(
                        mainHand,
                        Stats.SwingForwardAnimation,
                        animationSpeed: PlayerBehavior?.ManipulationSpeed ?? 1,
                        category: AnimationCategory(mainHand),
                        callback: () => SwingForwardAnimationCallback(slot, player, mainHand));
                    TpAnimationBehavior?.Play(mainHand, Stats.SwingForwardAnimation, AnimationCategory(mainHand), PlayerBehavior?.ManipulationSpeed ?? 1);

                    state = (int)AxeState.SwingForward;

                    ExtraSwingTime = SwingEnd - SwingStart;
                    SwingStart = TimeSpan.FromMilliseconds(Api.ElapsedMilliseconds);
                    if (SwingStart - SwingEnd > MaxDelta) ExtraSwingTime = TimeSpan.Zero;

                    break;
                }
            case AxeState.SwingForward:
                {
                    SwingForward(slot, player, ref state, eventData, mainHand, direction);
                    break;
                }
            case AxeState.SwingBack:
                {
                    break;
                }
        }

        return Stats.HandleLMBInputs;
    }
    protected virtual bool SwingForwardAnimationCallback(ItemSlot slot, EntityPlayer player, bool mainHand)
    {
        AnimationBehavior?.Play(
            mainHand,
            Stats.SwingBackAnimation,
            animationSpeed: PlayerBehavior?.ManipulationSpeed ?? 1,
            category: AnimationCategory(mainHand),
            callback: () => SwingBackAnimationCallback(mainHand));
        TpAnimationBehavior?.Play(mainHand, Stats.SwingBackAnimation, AnimationCategory(mainHand), PlayerBehavior?.ManipulationSpeed ?? 1);
        PlayerBehavior?.SetState((int)AxeState.SwingBack, mainHand);
        AnimationBehavior?.StopVanillaAnimation(Stats.SwingTpAnimation, mainHand);

        BlockSelection selection = player.BlockSelection;

        if (selection?.Position == null) return true;

        SoundsSystem.Play(selection.Block.Sounds.GetHitSound(Item.Tool ?? EnumTool.Pickaxe).ToString(), randomizedPitch: true);
        TimeSpan currentTime = TimeSpan.FromMilliseconds(Api.ElapsedMilliseconds);
        TimeSpan delta = currentTime - SwingStart + ExtraSwingTime;

        float miningSpeed = GetMiningSpeed(slot.Itemstack, selection, selection.Block, player);

        AnimationBehavior?.SetSpeedModifier(HitImpactFunction);

        BlockBreakingSystem?.DamageBlock(selection, selection.Block, miningSpeed * (float)delta.TotalSeconds, Item.Tool ?? 0, Item.ToolTier);

        SwingStart = currentTime;

        return true;
    }
    protected virtual bool SwingBackAnimationCallback(bool mainHand)
    {
        AnimationBehavior?.PlayReadyAnimation(mainHand);
        TpAnimationBehavior?.PlayReadyAnimation(mainHand);
        PlayerBehavior?.SetState((int)AxeState.Idle, mainHand);
        AnimationBehavior?.StopVanillaAnimation(Stats.SwingTpAnimation, mainHand);

        SwingEnd = TimeSpan.FromMilliseconds(Api.ElapsedMilliseconds);

        return true;
    }
    protected virtual bool SwingForward(ItemSlot slot, EntityPlayer player, ref int state, ActionEventData eventData, bool mainHand, AttackDirection direction)
    {
        LineSegmentCollider? collider = Collider.TransformLineSegment(player.Pos, player, slot, Api, mainHand);

        if (collider == null) return false;

        BlockSelection selection = player.BlockSelection;

        if (selection?.Position == null) return false;

        (Block block, Vector3d position, double parameter)? collision = collider.Value.IntersectBlock(Api, selection.Position);

        if (collision == null) return true;

        SwingForwardAnimationCallback(slot, player, mainHand);

        return true;
    }
    protected virtual bool HitImpactFunction(TimeSpan duration, ref TimeSpan delta)
    {
        TimeSpan totalDuration = TimeSpan.FromMilliseconds(Stats.HitStaggerDurationMs);

        delta = TimeSpan.Zero;

        return duration < totalDuration;
    }

    [ActionEventHandler(EnumEntityAction.RightMouseDown, ActionState.Active)]
    protected virtual bool Split(ItemSlot slot, EntityPlayer player, ref int state, ActionEventData eventData, bool mainHand, AttackDirection direction)
    {
        if (eventData.AltPressed && !mainHand) return false;
        if (!Stats.CanSplitLogs) return false;
        if (player.BlockSelection?.Block == null) return false;
        if (Stats.TwoHanded && !CheckForOtherHandEmpty(mainHand, player)) return false;
        if (!IsSplittable(player.BlockSelection.Block)) return false;
        if (state != (int)AxeState.Idle && state != (int)AxeState.Splitting && state != (int)AxeState.SplittingWindUp) return false;

        if (!Api.World.Claims.TryAccess(Api.World.Player, player.BlockSelection.Position, EnumBlockAccessFlags.BuildOrBreak)) return false; // @TODO add error message "TriggerIngameError"
        if (Api.ModLoader.GetModSystem<ModSystemBlockReinforcement>().IsReinforced(player.BlockSelection.Position)) return false;

        switch ((AxeState)state)
        {
            case AxeState.Idle:
                {
                    AnimationBehavior?.Play(
                        mainHand,
                        Stats.SplitAnimation,
                        animationSpeed: PlayerBehavior?.ManipulationSpeed ?? 1,
                        category: AnimationCategory(mainHand),
                        callback: () => SplitAnimationCallback(slot, player, mainHand),
                        callbackHandler: code => SplitAnimationCallbackHandler(code, mainHand));
                    TpAnimationBehavior?.Play(mainHand, Stats.SplitAnimation, AnimationCategory(mainHand), PlayerBehavior?.ManipulationSpeed ?? 1);

                    state = (int)AxeState.SplittingWindUp;

                    break;
                }
            case AxeState.Splitting:
                {
                    LineSegmentCollider? collider = Collider.TransformLineSegment(player.Pos, player, slot, Api, mainHand);
                    if (collider == null) return true;
                    BlockSelection selection = player.BlockSelection;
                    if (selection?.Position == null) return true;
                    (Block block, Vector3d position, double parameter)? collision = collider.Value.IntersectBlock(Api, selection.Position);
                    if (collision == null) return true;

                    SplitAnimationCallback(slot, player, mainHand);

                    break;
                }
        }
        return true;
    }
    protected virtual bool SplitAnimationCallbackHandler(string code, bool mainHand)
    {
        if (code == "start") PlayerBehavior?.SetState((int)AxeState.Splitting, mainHand);
        return true;
    }
    protected virtual bool SplitAnimationCallback(ItemSlot slot, EntityPlayer player, bool mainHand)
    {
        AnimationBehavior?.StopVanillaAnimation(Stats.SplitTpAnimation, mainHand);
        AnimationBehavior?.SetSpeedModifier(HitImpactFunction);
        AnimationBehavior?.Play(
                        mainHand,
                        Stats.SplitBackAnimation,
                        animationSpeed: PlayerBehavior?.ManipulationSpeed ?? 1,
                        category: AnimationCategory(mainHand),
                        callback: () => SplitBackAnimationCallback(mainHand));
        TpAnimationBehavior?.Play(mainHand, Stats.SplitBackAnimation, AnimationCategory(mainHand), PlayerBehavior?.ManipulationSpeed ?? 1);
        PlayerBehavior?.SetState((int)AxeState.SplittingBack, mainHand);

        BlockSelection selection = player.BlockSelection;
        if (selection?.Position == null) return true;

        Splittable? behavior = selection.Block.GetBehavior<Splittable>();
        if (behavior == null) return true;

        SoundsSystem.Play(selection.Block.Sounds.GetHitSound(Item.Tool ?? EnumTool.Pickaxe).ToString(), randomizedPitch: true);

        Api.World.BlockAccessor.BreakBlock(selection.Position, Api.World.Player, 0f);
        Api.World.BlockAccessor.MarkBlockDirty(selection.Position, Api.World.Player);

        Api.World.SpawnItemEntity(behavior.GetDrop(Api), selection.Position.ToVec3d());

        BlockBreakingNetworking.SplitBlock();

        slot.Itemstack.Item.DamageItem(Api.World, player, slot, 1);

        return true;
    }
    protected virtual bool SplitBackAnimationCallback(bool mainHand)
    {
        AnimationBehavior?.PlayReadyAnimation(mainHand);
        TpAnimationBehavior?.PlayReadyAnimation(mainHand);
        PlayerBehavior?.SetState((int)AxeState.Idle, mainHand);
        return true;
    }

    [ActionEventHandler(EnumEntityAction.LeftMouseDown, ActionState.Active)]
    protected virtual bool Attack(ItemSlot slot, EntityPlayer player, ref int state, ActionEventData eventData, bool mainHand, AttackDirection direction)
    {
        if (eventData.AltPressed && !mainHand) return false;
        if (player.BlockSelection?.Block != null) return false;
        if (Stats.TwoHanded && !CheckForOtherHandEmpty(mainHand, player)) return false;
        if (ActionRestricted(player, mainHand)) return false;

        switch ((AxeState)state)
        {
            case AxeState.Idle:
                state = (int)AxeState.AttackWindup;
                MeleeAttack.Start(player.Player);
                AnimationBehavior?.Play(
                    mainHand,
                    Stats.AttackAnimation,
                    animationSpeed: PlayerBehavior?.ManipulationSpeed ?? 1,
                    category: AnimationCategory(mainHand),
                    callback: () => AttackAnimationCallback(mainHand),
                    callbackHandler: code => AttackAnimationCallbackHandler(code, mainHand));
                TpAnimationBehavior?.Play(mainHand, Stats.AttackAnimation, AnimationCategory(mainHand), PlayerBehavior?.ManipulationSpeed ?? 1);

                return true;
            default:
                return false;
        }
    }
    protected virtual void TryAttack(ItemSlot slot, EntityPlayer player, bool mainHand)
    {
        MeleeAttack.Attack(
            player.Player,
            slot,
            mainHand,
            out IEnumerable<(Block block, Vector3d point)> terrainCollision,
            out IEnumerable<(Vintagestory.API.Common.Entities.Entity entity, Vector3d point)> entitiesCollision);

        if (entitiesCollision.Any() && Stats.AnimationStaggerOnHitDurationMs > 0)
        {
            AnimationBehavior?.SetSpeedModifier(AttackImpactFunction);
        }
    }
    protected virtual bool AttackImpactFunction(TimeSpan duration, ref TimeSpan delta)
    {
        TimeSpan totalDuration = TimeSpan.FromMilliseconds(Stats.AnimationStaggerOnHitDurationMs);

        delta = TimeSpan.Zero;

        return duration < totalDuration;
    }
    protected virtual bool AttackAnimationCallback(bool mainHand)
    {
        AnimationBehavior?.PlayReadyAnimation(mainHand);
        TpAnimationBehavior?.PlayReadyAnimation(mainHand);
        PlayerBehavior?.SetState((int)AxeState.Idle, mainHand);

        return true;
    }
    protected virtual void AttackAnimationCallbackHandler(string callbackCode, bool mainHand)
    {
        switch (callbackCode)
        {
            case "start":
                PlayerBehavior?.SetState((int)AxeState.Attacking, mainHand);
                break;
            case "stop":
                PlayerBehavior?.SetState((int)AxeState.AttackCooldown, mainHand);
                break;
            case "ready":
                PlayerBehavior?.SetState((int)AxeState.Idle, mainHand);
                break;
        }
    }

    protected static string AnimationCategory(bool mainHand = true) => mainHand ? "main" : "mainOffhand";
    protected virtual float GetMiningSpeed(IItemStack itemStack, BlockSelection blockSel, Block block, EntityPlayer forPlayer)
    {
        float traitRate = 1f;

        EnumBlockMaterial mat = block.GetBlockMaterial(Api.World.BlockAccessor, blockSel.Position);

        if (mat == EnumBlockMaterial.Ore || mat == EnumBlockMaterial.Stone)
        {
            traitRate = forPlayer.Stats.GetBlended("miningSpeedMul");
        }

        if (Item.MiningSpeed == null) return 0;

        if (!Item.MiningSpeed.ContainsKey(mat)) return 0;

        return Item.MiningSpeed[mat] * GlobalConstants.ToolMiningSpeedModifier * traitRate * GetIDGMultiplier(itemStack);
    }
    protected virtual bool IsSplittable(Block block) => block.HasBehavior<Splittable>();
    protected virtual float GetIDGMultiplier(IItemStack stack) => stack?.Collectible?.Attributes?["choppingprops"]?["fellingmultiplier"]?.AsFloat(1) ?? 1;

    protected static bool ActionRestricted(EntityPlayer player, bool mainHand = true)
    {
        if (mainHand)
        {
            return (player.LeftHandItemSlot.Itemstack?.Item as IRestrictAction)?.RestrictRightHandAction() ?? false;
        }
        else
        {
            return (player.RightHandItemSlot.Itemstack?.Item as IRestrictAction)?.RestrictLeftHandAction() ?? false;
        }
    }
    protected virtual bool CheckForOtherHandEmpty(bool mainHand, EntityPlayer player)
    {
        if (mainHand && !player.LeftHandItemSlot.Empty)
        {
            (player.World.Api as ICoreClientAPI)?.TriggerIngameError(this, "offhandShouldBeEmpty", Lang.Get("Offhand should be empty"));
            return false;
        }

        if (!mainHand && !player.RightHandItemSlot.Empty)
        {
            (player.World.Api as ICoreClientAPI)?.TriggerIngameError(this, "mainHandShouldBeEmpty", Lang.Get("Main hand should be empty"));
            return false;
        }

        return true;
    }
}

public class AxeServer
{
    public AxeServer(ICoreServerAPI api, Axe item)
    {

    }
}

public class Axe : ItemAxe, IHasWeaponLogic, ISetsRenderingOffset, IHasIdleAnimations, IOnGameTick, IRestrictAction
{
    public AxeClient? Client { get; private set; }
    public AxeServer? Server { get; private set; }

    public IClientWeaponLogic? ClientLogic => Client;
    public bool RenderingOffset { get; private set; }
    public AnimationRequestByCode IdleAnimation { get; private set; }
    public AnimationRequestByCode ReadyAnimation { get; private set; }
    public bool RestrictRightHandAction() => Client?.RestrictRightHandAction() ?? false;
    public bool RestrictLeftHandAction() => Client?.RestrictLeftHandAction() ?? false;

    public float BlockBreakDamage { get; set; } = 0;

    public override void OnLoaded(ICoreAPI api)
    {
        base.OnLoaded(api);

        if (api is ICoreClientAPI clientAPI)
        {
            Client = new(clientAPI, this);
            AxeStats Stats = Attributes.AsObject<AxeStats>();
            RenderingOffset = Stats.RenderingOffset;

            IdleAnimation = new(Stats.IdleAnimation, 1, 1, "main", TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(0.2), false);
            ReadyAnimation = new(Stats.ReadyAnimation, 1, 1, "main", TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(0.2), false);
        }

        if (api is ICoreServerAPI serverAPI)
        {
            Server = new(serverAPI, this);
        }
    }
    public override void OnHeldRenderOpaque(ItemSlot inSlot, IClientPlayer byPlayer)
    {
        base.OnHeldRenderOpaque(inSlot, byPlayer);

        if (DebugWindowManager.RenderDebugColliders)
        {
            Client?.RenderDebugCollider(inSlot, byPlayer);
        }
    }

    public override void OnHeldAttackStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, ref EnumHandHandling handling)
    {
        handling = EnumHandHandling.PreventDefault;
    }
    public override bool OnHeldAttackStep(float secondsPassed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSelection, EntitySelection entitySel)
    {
        return false;
    }

    public override float OnBlockBreaking(IPlayer player, BlockSelection blockSel, ItemSlot itemslot, float remainingResistance, float dt, int counter)
    {
        float result = GameMath.Clamp(remainingResistance - BlockBreakDamage, 0, remainingResistance);
        BlockBreakDamage = 0;

        base.OnBlockBreaking(player, blockSel, itemslot, remainingResistance, dt, counter);

        return result;
    }

    public void OnGameTick(ItemSlot slot, EntityPlayer player, ref int state, bool mainHand) => Client?.OnGameTick(slot, player, ref state, mainHand);
}

public class Splittable : BlockBehavior
{
    public JsonItemStack DroppedItem { get; private set; } = new();

    public Splittable(Block block) : base(block)
    {
    }

    public override void Initialize(JsonObject properties)
    {
        base.Initialize(properties);

        DroppedItem = properties["DroppedItem"].AsObject<JsonItemStack>();
    }

    public ItemStack GetDrop(ICoreAPI api)
    {
        DroppedItem.Resolve(api.World, "CombatOverhaul");
        return DroppedItem.ResolvedItemstack;
    }
}