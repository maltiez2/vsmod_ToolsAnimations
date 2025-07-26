﻿using CombatOverhaul;
using CombatOverhaul.Animations;
using CombatOverhaul.Colliders;
using CombatOverhaul.Implementations;
using CombatOverhaul.Inputs;
using CombatOverhaul.MeleeSystems;
using OpenTK.Mathematics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace ToolsAnimations;

public class PickaxeStats
{
    public string ReadyAnimation { get; set; } = "";
    public string IdleAnimation { get; set; } = "";
    public string[] SwingForwardAnimation { get; set; } = Array.Empty<string>();
    public string[] SwingBackAnimation { get; set; } = Array.Empty<string>();
    public string SwingTpAnimation { get; set; } = "";
    public bool RenderingOffset { get; set; } = false;
    public float[] Collider { get; set; } = Array.Empty<float>();
    public Dictionary<string, string> HitParticleEffects { get; set; } = new();
    public float HitStaggerDurationMs { get; set; } = 100;

    public MeleeAttackStats Attack { get; set; } = new();
    public string AttackAnimation { get; set; } = "";
    public string AttackTpAnimation { get; set; } = "";
    public float AnimationStaggerOnHitDurationMs { get; set; } = 100;

    public float AnimationSpeedBonusFromMiningSpeed { get; set; } = 0.3f;

    public bool TwoHanded { get; set; } = false;
    public float OptimalRange { get; set; } = 4.5f;
    public float SpeedReductionAtMaxRange { get; set; } = 0.5f;

    public bool DamageOnHit { get; set; } = false;
    public float ChanceToDamageOnHit { get; set; } = 1f;
    public float ItemDurabilityFactor { get; set; } = 1f;
}

public enum PickaxeState
{
    Idle,
    SwingForward,
    SwingBack,
    AttackWindup,
    Attacking,
    AttackCooldown
}

public class PickaxeClient : IClientWeaponLogic, IOnGameTick, IRestrictAction
{
    public PickaxeClient(ICoreClientAPI api, Pickaxe item)
    {
        Item = item;
        ItemId = item.Id;
        Stats = item.Attributes.AsObject<PickaxeStats>();
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
    public bool RestrictRightHandAction() => PlayerBehavior?.GetState(mainHand: false) != (int)PickaxeState.Idle;
    public bool RestrictLeftHandAction() => PlayerBehavior?.GetState(mainHand: true) != (int)PickaxeState.Idle;

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
        if (state == (int)PickaxeState.Attacking)
        {
            TryAttack(slot, player, mainHand);
        }
    }

    protected PickaxeStats Stats;
    protected LineSegmentCollider Collider;
    protected ICoreClientAPI Api;
    protected FirstPersonAnimationsBehavior? AnimationBehavior;
    protected ThirdPersonAnimationsBehavior? TpAnimationBehavior;
    protected ActionsManagerPlayerBehavior? PlayerBehavior;
    protected SoundsSynchronizerClient SoundsSystem;
    protected BlockBreakingSystemClient BlockBreakingNetworking;
    protected BlockBreakingController BlockBreakingSystem;
    protected Pickaxe Item;
    protected TimeSpan SwingStart;
    protected TimeSpan SwingEnd;
    protected TimeSpan ExtraSwingTime;
    protected readonly TimeSpan MaxDelta = TimeSpan.FromSeconds(0.1);
    protected readonly MeleeAttack MeleeAttack;
    protected readonly Random Rand = new();
    protected int AnimationIndexCounter = 0;
    protected int CurrentAnimationIndex = 0;

    protected const float DefaultMiningSpeed = 4;
    protected const float SteelMiningSpeed = 9;
    protected const float MaxAnimationSpeedBonus = 1.5f;
    protected const float MaxDistance = 4.5f;

    [ActionEventHandler(EnumEntityAction.LeftMouseDown, ActionState.Active)]
    protected virtual bool Swing(ItemSlot slot, EntityPlayer player, ref int state, ActionEventData eventData, bool mainHand, AttackDirection direction)
    {
        if (eventData.AltPressed && !mainHand) return false;
        if (player.BlockSelection?.Block == null || player.EntitySelection?.Entity != null) return false;
        if (Stats.TwoHanded && !CheckForOtherHandEmpty(mainHand, player)) return false;
        if (ActionRestricted(player, mainHand)) return false;

        switch ((PickaxeState)state)
        {
            case PickaxeState.Idle:
                {
                    float animationSpeedMultiplier = 1;
                    float miningSpeedFactor = 1;
                    BlockSelection? selection = player.BlockSelection;
                    if (selection?.Position != null)
                    {
                        float miningSpeed = GetMiningSpeed(slot.Itemstack, selection, selection.Block, player);
                        animationSpeedMultiplier = GetAnimationSpeedFromMiningSpeed(miningSpeed, Stats.AnimationSpeedBonusFromMiningSpeed);
                        miningSpeedFactor = DistanceMiningSpeedReduction(selection, player);
                    }

                    int animationsNumber = Stats.SwingForwardAnimation.Length;
                    CurrentAnimationIndex = GetAnimationIndex(animationsNumber);

                    AnimationBehavior?.Play(
                        mainHand,
                        Stats.SwingForwardAnimation[CurrentAnimationIndex],
                        animationSpeed: (PlayerBehavior?.ManipulationSpeed ?? 1) * animationSpeedMultiplier * miningSpeedFactor,
                        category: AnimationCategory(mainHand),
                        callback: () => SwingForwardAnimationCallback(slot, player, mainHand));
                    TpAnimationBehavior?.Play(mainHand, Stats.SwingForwardAnimation[CurrentAnimationIndex], AnimationCategory(mainHand), (PlayerBehavior?.ManipulationSpeed ?? 1) * animationSpeedMultiplier * miningSpeedFactor);

                    state = (int)PickaxeState.SwingForward;

                    ExtraSwingTime = SwingEnd - SwingStart;
                    SwingStart = TimeSpan.FromMilliseconds(Api.ElapsedMilliseconds);
                    if (SwingStart - SwingEnd > MaxDelta) ExtraSwingTime = TimeSpan.Zero;

                    break;
                }
            case PickaxeState.SwingForward:
                {
                    SwingForward(slot, player, ref state, eventData, mainHand, direction);
                    break;
                }
            case PickaxeState.SwingBack:
                {
                    break;
                }
        }

        return true;
    }
    protected virtual bool SwingForwardAnimationCallback(ItemSlot slot, EntityPlayer player, bool mainHand)
    {
        BlockSelection? selection = player.BlockSelection;

        if (selection?.Position == null)
        {
            AnimationBehavior?.Play(
                mainHand,
                Stats.SwingBackAnimation[CurrentAnimationIndex],
                animationSpeed: PlayerBehavior?.ManipulationSpeed ?? 1,
                category: AnimationCategory(mainHand),
                callback: () => SwingBackAnimationCallback(mainHand));
            TpAnimationBehavior?.Play(mainHand, Stats.SwingBackAnimation[CurrentAnimationIndex], AnimationCategory(mainHand), PlayerBehavior?.ManipulationSpeed ?? 1);
            PlayerBehavior?.SetState((int)PickaxeState.SwingBack, mainHand);
            AnimationBehavior?.StopVanillaAnimation(Stats.SwingTpAnimation, mainHand);
            return true;
        }

        float miningSpeed = GetMiningSpeed(slot.Itemstack, selection, selection.Block, player);
        float animationSpeedMultiplier = GetAnimationSpeedFromMiningSpeed(miningSpeed, Stats.AnimationSpeedBonusFromMiningSpeed);

        AnimationBehavior?.Play(
            mainHand,
            Stats.SwingBackAnimation[CurrentAnimationIndex],
            animationSpeed: PlayerBehavior?.ManipulationSpeed * animationSpeedMultiplier ?? animationSpeedMultiplier,
            category: AnimationCategory(mainHand),
            callback: () => SwingBackAnimationCallback(mainHand));
        TpAnimationBehavior?.Play(mainHand, Stats.SwingBackAnimation[CurrentAnimationIndex], AnimationCategory(mainHand), PlayerBehavior?.ManipulationSpeed * animationSpeedMultiplier ?? animationSpeedMultiplier);
        PlayerBehavior?.SetState((int)PickaxeState.SwingBack, mainHand);
        AnimationBehavior?.StopVanillaAnimation(Stats.SwingTpAnimation, mainHand);


        SoundsSystem.Play(selection.Block.Sounds.GetHitSound(Item.Tool ?? EnumTool.Pickaxe).ToString(), randomizedPitch: true);
        TimeSpan currentTime = TimeSpan.FromMilliseconds(Api.ElapsedMilliseconds);
        TimeSpan delta = currentTime - SwingStart + ExtraSwingTime;

        AnimationBehavior?.SetSpeedModifier(HitImpactFunction);

        float miningSpeedFactor = DistanceMiningSpeedReduction(selection, player);

        BlockBreakingSystem?.DamageBlock(selection, selection.Block, miningSpeed * (float)delta.TotalSeconds * miningSpeedFactor, Item.Tool ?? 0, Item.ToolTier);

        if (Stats.DamageOnHit && Rand.NextDouble() <= Stats.ChanceToDamageOnHit)
        {
            BlockBreakingNetworking.DamageTool(1, mainHand);
        }

        SwingStart = currentTime;

        return true;
    }
    protected virtual bool SwingBackAnimationCallback(bool mainHand)
    {
        AnimationBehavior?.PlayReadyAnimation(mainHand);
        TpAnimationBehavior?.PlayReadyAnimation(mainHand);
        PlayerBehavior?.SetState((int)PickaxeState.Idle, mainHand);
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

    [ActionEventHandler(EnumEntityAction.LeftMouseDown, ActionState.Active)]
    protected virtual bool Attack(ItemSlot slot, EntityPlayer player, ref int state, ActionEventData eventData, bool mainHand, AttackDirection direction)
    {
        if (eventData.AltPressed && !mainHand) return false;
        if (Stats.AttackAnimation == "") return false;
        if (player.BlockSelection?.Block != null) return false;
        if (Stats.TwoHanded && !CheckForOtherHandEmpty(mainHand, player)) return false;
        if (ActionRestricted(player, mainHand)) return false;

        switch ((PickaxeState)state)
        {
            case PickaxeState.Idle:
                state = (int)PickaxeState.AttackWindup;
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
        PlayerBehavior?.SetState((int)PickaxeState.Idle, mainHand);

        return true;
    }
    protected virtual void AttackAnimationCallbackHandler(string callbackCode, bool mainHand)
    {
        switch (callbackCode)
        {
            case "start":
                PlayerBehavior?.SetState((int)PickaxeState.Attacking, mainHand);
                break;
            case "stop":
                PlayerBehavior?.SetState((int)PickaxeState.AttackCooldown, mainHand);
                break;
            case "ready":
                PlayerBehavior?.SetState((int)PickaxeState.Idle, mainHand);
                break;
        }
    }


    protected static string AnimationCategory(bool mainHand = true) => mainHand ? "main" : "mainOffhand";
    protected virtual float GetMiningSpeed(IItemStack itemStack, BlockSelection blockSel, Block block, EntityPlayer forPlayer)
    {
        float traitRate = 1f;

        float toolTierMultiplier = block.RequiredMiningTier <= Item.ToolTier ? 1 : 0;

        EnumBlockMaterial mat = block.GetBlockMaterial(Api.World.BlockAccessor, blockSel.Position);

        if (mat == EnumBlockMaterial.Ore || mat == EnumBlockMaterial.Stone)
        {
            traitRate = forPlayer.Stats.GetBlended("miningSpeedMul");
        }

        if (Item.MiningSpeed == null || !Item.MiningSpeed.ContainsKey(mat)) return traitRate;

        if (Item.MiningSpeed == null || !Item.MiningSpeed.ContainsKey(mat)) return traitRate * toolTierMultiplier;

        return Item.MiningSpeed[mat] * GlobalConstants.ToolMiningSpeedModifier * traitRate * toolTierMultiplier;
    }
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
    protected static float GetAnimationSpeedFromMiningSpeed(float miningSpeed, float bonusMultiplier)
    {
        return 1f + GameMath.Clamp(bonusMultiplier * (GameMath.Clamp(miningSpeed - DefaultMiningSpeed, 0, miningSpeed) / (SteelMiningSpeed - DefaultMiningSpeed)), 0, MaxAnimationSpeedBonus);
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

    protected virtual float DistanceMiningSpeedReduction(BlockSelection selection, EntityPlayer player)
    {
        if (Stats.OptimalRange >= MaxDistance) return 1f;

        float distance = player.Pos.XYZ.Add(player.LocalEyePos).DistanceTo(selection.FullPosition);

        float distanceFraction = GameMath.Clamp(1 - MathF.Max(distance - Stats.OptimalRange, 0) / MathF.Max(MaxDistance - Stats.OptimalRange, 1E-6f), 0, 1);

        float speedFactor = Stats.SpeedReductionAtMaxRange + distanceFraction * MathF.Max(1 - Stats.SpeedReductionAtMaxRange, 0);

        return speedFactor;
    }

    protected virtual int GetAnimationIndex(int animationsCount)
    {
        if (AnimationIndexCounter >= int.MaxValue) AnimationIndexCounter = 0;

        AnimationIndexCounter++;

        return AnimationIndexCounter % animationsCount;
    }
}

public class Pickaxe : Item, IHasWeaponLogic, ISetsRenderingOffset, IHasIdleAnimations, IOnGameTick, IRestrictAction
{
    public PickaxeClient? Client { get; private set; }

    public IClientWeaponLogic? ClientLogic => Client;
    public bool RenderingOffset { get; private set; }
    public AnimationRequestByCode IdleAnimation { get; private set; }
    public AnimationRequestByCode ReadyAnimation { get; private set; }
    public bool RestrictRightHandAction() => Client?.RestrictRightHandAction() ?? false;
    public bool RestrictLeftHandAction() => Client?.RestrictLeftHandAction() ?? false;
    public bool DamageItemOnBroken { get; private set; } = true;
    public float DurabilityFactor { get; set; } = 1f;

    public float BlockBreakDamage { get; set; } = 0;

    public override void OnLoaded(ICoreAPI api)
    {
        base.OnLoaded(api);

        PickaxeStats Stats = Attributes.AsObject<PickaxeStats>();

        DurabilityFactor = Stats.ItemDurabilityFactor;

        if (api is ICoreClientAPI clientAPI)
        {
            Client = new(clientAPI, this);
            
            DamageItemOnBroken = !Stats.DamageOnHit;
            RenderingOffset = Stats.RenderingOffset;

            IdleAnimation = new(Stats.IdleAnimation, 1, 1, "main", TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(0.2), false);
            ReadyAnimation = new(Stats.ReadyAnimation, 1, 1, "main", TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(0.2), false);
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

    public override bool OnBlockBrokenWith(IWorldAccessor world, Entity byEntity, ItemSlot itemslot, BlockSelection blockSel, float dropQuantityMultiplier = 1f)
    {
        if (DamageItemOnBroken)
        {
            return base.OnBlockBrokenWith(world, byEntity, itemslot, blockSel, dropQuantityMultiplier);
        }
        
        EnumItemDamageSource[] previousDamagedBy = DamagedBy;
        DamagedBy = [];

        bool result = base.OnBlockBrokenWith(world, byEntity, itemslot, blockSel, dropQuantityMultiplier);

        DamagedBy = previousDamagedBy;
        return result;
    }

    public override int GetMaxDurability(ItemStack itemstack)
    {
        int result = base.GetMaxDurability(itemstack);

        return (int)(result * DurabilityFactor);
    }

    public void OnGameTick(ItemSlot slot, EntityPlayer player, ref int state, bool mainHand) => Client?.OnGameTick(slot, player, ref state, mainHand);
}