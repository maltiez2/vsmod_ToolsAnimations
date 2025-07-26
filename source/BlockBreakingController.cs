using ProtoBuf;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;
using Vintagestory.GameContent;

namespace ToolsAnimations;


public sealed class BlockBreakingController
{
    public BlockBreakingController(ICoreClientAPI api)
    {
        _api = api;
        _game = api.World as ClientMain ?? throw new Exception();
        _damagedBlocks = (Dictionary<BlockPos, BlockDamage>?)_clientMain_damagedBlocks?.GetValue(_game) ?? throw new Exception();
    }

    public static float TreeDamageMultiplier { get; set; } = 4;

    public void DamageBlock(BlockSelection blockSelection, Block block, float blockDamage, EnumTool tool, int toolTier) => ContinueBreakSurvival(blockSelection, block, blockDamage, tool, toolTier);

    private readonly ICoreClientAPI _api;
    private BlockDamage? _curBlockDmg;
    private readonly ClientMain _game;

    private static readonly FieldInfo? _clientMain_damagedBlocks = typeof(ClientMain).GetField("damagedBlocks", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly MethodInfo? _clientMain_OnPlayerTryDestroyBlock = typeof(ClientMain).GetMethod("OnPlayerTryDestroyBlock", BindingFlags.Public | BindingFlags.Instance);
    private static readonly MethodInfo? _clientMain_loadOrCreateBlockDamage = typeof(ClientMain).GetMethod("loadOrCreateBlockDamage", BindingFlags.Public | BindingFlags.Instance);

    private readonly Dictionary<BlockPos, BlockDamage> _damagedBlocks;

    private const int _treeResistanceThreshold = 300;
    private const int _treeResistanceDivider = 3;

    private void OnPlayerTryDestroyBlock(BlockSelection blockSelection) => _clientMain_OnPlayerTryDestroyBlock?.Invoke(_game, [blockSelection]);
    private BlockDamage loadOrCreateBlockDamage(BlockSelection blockSelection, Block block, EnumTool? tool, IPlayer byPlayer) => (BlockDamage?)_clientMain_loadOrCreateBlockDamage?.Invoke(_game, [blockSelection, block, tool, byPlayer]) ?? throw new Exception();

    private void InitBlockBreakSurvival(BlockSelection blockSelection)
    {
        Block block = blockSelection.Block ?? _game.BlockAccessor.GetBlock(blockSelection.Position);
        LoadOrCreateBlockDamage(blockSelection, block);
        _curBlockDmg.LastBreakEllapsedMs = _game.ElapsedMilliseconds;
        _curBlockDmg.BeginBreakEllapsedMs = _game.ElapsedMilliseconds;
    }
    private void ContinueBreakSurvival(BlockSelection blockSelection, Block block, float blockDamage, EnumTool tool, int ToolTier)
    {
        InitBlockBreakSurvival(blockSelection);

        LoadOrCreateBlockDamage(blockSelection, block);
        long elapsedMs = _game.ElapsedMilliseconds;
        int diff = (int)(elapsedMs - _curBlockDmg.LastBreakEllapsedMs);
        long decorBreakPoint = _curBlockDmg.BeginBreakEllapsedMs + 225;
        if (elapsedMs >= decorBreakPoint && _curBlockDmg.LastBreakEllapsedMs < decorBreakPoint && _game.BlockAccessor.GetChunkAtBlockPos(blockSelection.Position) is WorldChunk c)
        {
            BlockPos pos = blockSelection.Position;
            int chunksize = 32;
            c.BreakDecor(_game, pos, blockSelection.Face);
            _game.WorldMap.MarkChunkDirty(pos.X / chunksize, pos.Y / chunksize, pos.Z / chunksize, priority: true);
            _game.SendPacketClient(ClientPackets.BlockInteraction(blockSelection, 2, 0));
        }
        if (tool == EnumTool.Axe)
        {
            FindTree(_api.World, blockSelection.Position, out int resistance, out int woodTier);
            resistance = AdjustTreeResistance(resistance);
            if (resistance > 0)
            {
                if (ToolTier < woodTier - 3)
                {
                    blockDamage *= 0;
                }
                else
                {
                    blockDamage *= _curBlockDmg.Block.Resistance / resistance * TreeDamageMultiplier;
                }
            }
        }

        _curBlockDmg.RemainingResistance -= blockDamage;


        _curBlockDmg.Facing = blockSelection.Face;
        if (_curBlockDmg.Position != blockSelection.Position || _curBlockDmg.Block != block)
        {
            _curBlockDmg.RemainingResistance = block.GetResistance(_game.BlockAccessor, blockSelection.Position);
            _curBlockDmg.Block = block;
            _curBlockDmg.Position = blockSelection.Position;
        }
        if (_curBlockDmg.RemainingResistance <= 0f)
        {
            _game.eventManager.TriggerBlockBroken(_curBlockDmg);
            OnPlayerTryDestroyBlock(blockSelection);
            _damagedBlocks.Remove(blockSelection.Position);
            //UpdateCurrentSelection();
        }
        else
        {
            _game.eventManager.TriggerBlockBreaking(_curBlockDmg);
        }
        _curBlockDmg.LastBreakEllapsedMs = elapsedMs;
    }

    private void LoadOrCreateBlockDamage(BlockSelection blockSelection, Block block)
    {
        BlockDamage prevDmg = _curBlockDmg;
        EnumTool? tool = _api.World.Player.Entity.ActiveHandItemSlot?.Itemstack?.Collectible?.Tool;
        _curBlockDmg = loadOrCreateBlockDamage(blockSelection, block, tool, _api.World.Player);
        if (prevDmg != null && !prevDmg.Position.Equals(blockSelection.Position))
        {
            _curBlockDmg.LastBreakEllapsedMs = _game.ElapsedMilliseconds;
        }
    }

    public static Stack<BlockPos> FindTree(IWorldAccessor world, BlockPos startPos, out int resistance, out int woodTier)
    {
        Queue<Vec4i> queue = new();
        Queue<Vec4i> queue2 = new();
        HashSet<BlockPos> hashSet = new();
        Stack<BlockPos> stack = new();
        resistance = 0;
        woodTier = 0;
        Block block = world.BlockAccessor.GetBlock(startPos);
        if (block.Code == null)
        {
            return stack;
        }

        string text = block.Attributes?["treeFellingGroupCode"].AsString();
        int num = block.Attributes?["treeFellingGroupSpreadIndex"].AsInt() ?? 0;
        JsonObject attributes = block.Attributes;
        if (attributes != null && !attributes["treeFellingCanChop"].AsBool(defaultValue: true))
        {
            return stack;
        }

        EnumTreeFellingBehavior enumTreeFellingBehavior = EnumTreeFellingBehavior.Chop;
        if (block is ICustomTreeFellingBehavior customTreeFellingBehavior)
        {
            enumTreeFellingBehavior = customTreeFellingBehavior.GetTreeFellingBehavior(startPos, null, num);
            if (enumTreeFellingBehavior == EnumTreeFellingBehavior.NoChop)
            {
                resistance = stack.Count;
                return stack;
            }
        }

        if (num < 2)
        {
            return stack;
        }

        if (text == null)
        {
            return stack;
        }

        queue.Enqueue(new Vec4i(startPos.X, startPos.Y, startPos.Z, num));
        hashSet.Add(startPos);
        int[] array = new int[7];
        while (queue.Count > 0)
        {
            Vec4i vec4i = queue.Dequeue();
            stack.Push(new BlockPos(vec4i.X, vec4i.Y, vec4i.Z, startPos.dimension));
            resistance += vec4i.W + 1;
            if (woodTier == 0)
            {
                woodTier = vec4i.W;
            }

            if (stack.Count > 2500)
            {
                break;
            }

            block = world.BlockAccessor.GetBlock(vec4i.X, vec4i.Y, vec4i.Z, 1);
            if (block is ICustomTreeFellingBehavior customTreeFellingBehavior2)
            {
                enumTreeFellingBehavior = customTreeFellingBehavior2.GetTreeFellingBehavior(startPos, null, num);
            }

            if (enumTreeFellingBehavior != 0)
            {
                onTreeBlock(vec4i, world.BlockAccessor, hashSet, startPos, enumTreeFellingBehavior == EnumTreeFellingBehavior.ChopSpreadVertical, text, queue, queue2, array);
            }
        }

        int num2 = 0;
        int num3 = -1;
        for (int i = 0; i < array.Length; i++)
        {
            if (array[i] > num2)
            {
                num2 = array[i];
                num3 = i;
            }
        }

        if (num3 >= 0)
        {
            text = num3 + 1 + text;
        }

        while (queue2.Count > 0)
        {
            Vec4i vec4i2 = queue2.Dequeue();
            stack.Push(new BlockPos(vec4i2.X, vec4i2.Y, vec4i2.Z, startPos.dimension));
            resistance += vec4i2.W + 1;
            if (stack.Count > 2500)
            {
                break;
            }

            onTreeBlock(vec4i2, world.BlockAccessor, hashSet, startPos, enumTreeFellingBehavior == EnumTreeFellingBehavior.ChopSpreadVertical, text, queue2, null, null);
        }

        return stack;
    }

    private int AdjustTreeResistance(int resistance)
    {
        if (resistance <= _treeResistanceThreshold) return resistance;

        return _treeResistanceThreshold + (resistance - _treeResistanceThreshold) / _treeResistanceDivider;
    }

    private static void onTreeBlock(Vec4i pos, IBlockAccessor blockAccessor, HashSet<BlockPos> checkedPositions, BlockPos startPos, bool chopSpreadVertical, string treeFellingGroupCode, Queue<Vec4i> queue, Queue<Vec4i> leafqueue, int[] adjacentLeaves)
    {
        for (int i = 0; i < Vec3i.DirectAndIndirectNeighbours.Length; i++)
        {
            Vec3i vec3i = Vec3i.DirectAndIndirectNeighbours[i];
            BlockPos blockPos = new(pos.X + vec3i.X, pos.Y + vec3i.Y, pos.Z + vec3i.Z);
            float num = GameMath.Sqrt(blockPos.HorDistanceSqTo(startPos.X, startPos.Z));
            float num2 = blockPos.Y - startPos.Y;
            float num3 = (chopSpreadVertical ? 0.5f : 2f);
            if (num - 1f >= num3 * num2 || checkedPositions.Contains(blockPos))
            {
                continue;
            }

            Block block = blockAccessor.GetBlock(blockPos, 1);
            if (block.Code == null || block.Id == 0)
            {
                continue;
            }

            string text = block.Attributes?["treeFellingGroupCode"].AsString();
            Queue<Vec4i> queue2;
            if (text != treeFellingGroupCode)
            {
                if (text == null || leafqueue == null || block.BlockMaterial != EnumBlockMaterial.Leaves || text.Length != treeFellingGroupCode.Length + 1 || !text.EndsWithOrdinal(treeFellingGroupCode))
                {
                    continue;
                }

                queue2 = leafqueue;
                int num4 = GameMath.Clamp(text[0] - 48, 1, 7);
                adjacentLeaves[num4 - 1]++;
            }
            else
            {
                queue2 = queue;
            }

            int num5 = block.Attributes?["treeFellingGroupSpreadIndex"].AsInt() ?? 0;
            if (pos.W >= num5)
            {
                checkedPositions.Add(blockPos);
                if (!chopSpreadVertical || vec3i.Equals(0, 1, 0) || num5 <= 0)
                {
                    queue2.Enqueue(new Vec4i(blockPos.X, blockPos.Y, blockPos.Z, num5));
                }
            }
        }
    }
}

[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class BlockSplitPacket
{
    public BlockSplitPacket() { }
}

[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class ToolDamagedPacket
{
    public int DurabilityDamage { get; set; } = 1;
    public bool MainHand { get; set; } = true;
}

public class BlockBreakingSystemClient
{
    public const string NetworkChannelId = "CombatOverhaul:blockBreaking";

    public BlockBreakingSystemClient(ICoreClientAPI api)
    {
        _api = api;
        _channel = api.Network.RegisterChannel(NetworkChannelId)
            .RegisterMessageType<BlockSplitPacket>()
            .RegisterMessageType<ToolDamagedPacket>();
    }

    public void SplitBlock()
    {
        _channel.SendPacket(new BlockSplitPacket());
    }

    public void DamageTool(int durabilityDamage, bool mainHand)
    {
        _channel.SendPacket(new ToolDamagedPacket() { DurabilityDamage = durabilityDamage, MainHand = mainHand });
    }

    private readonly ICoreClientAPI _api;
    private readonly IClientNetworkChannel _channel;
}

public class BlockBreakingSystemServer
{
    public const string NetworkChannelId = BlockBreakingSystemClient.NetworkChannelId;

    public BlockBreakingSystemServer(ICoreServerAPI api)
    {
        _api = api;
        api.Network.RegisterChannel(NetworkChannelId)
            .RegisterMessageType<BlockSplitPacket>()
            .RegisterMessageType<ToolDamagedPacket>()
            .SetMessageHandler<BlockSplitPacket>(SplitPacketHandler)
            .SetMessageHandler<ToolDamagedPacket>(ToolDamagePacketHandler);
    }

    private readonly ICoreServerAPI _api;

    private void ToolDamagePacketHandler(IServerPlayer player, ToolDamagedPacket packet)
    {
        ItemSlot slot = packet.MainHand ? player.Entity.RightHandItemSlot : player.Entity.LeftHandItemSlot;

        slot.Itemstack?.Item?.DamageItem(player.Entity.World, player.Entity, slot, packet.DurabilityDamage);
    }

    private void SplitPacketHandler(IServerPlayer player, BlockSplitPacket packet)
    {
        BlockSelection selection = player.Entity.BlockSelection;

        if (selection?.Position == null) return;

        Splittable? behavior = selection.Block.GetBehavior<Splittable>();
        if (behavior == null) return;

        _api.World.BlockAccessor.BreakBlock(selection.Position, player, 0f);
        _api.World.BlockAccessor.MarkBlockDirty(selection.Position, player);

        _api.World.SpawnItemEntity(behavior.GetDrop(_api), selection.Position.ToVec3d());

        player.Entity.ActiveHandItemSlot.Itemstack.Item.DamageItem(_api.World, player.Entity, player.Entity.ActiveHandItemSlot, 1);
        player.Entity.ActiveHandItemSlot.MarkDirty();
    }
}
