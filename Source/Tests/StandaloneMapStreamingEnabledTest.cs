using FluentAssertions;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;

namespace Tests;

[TestFixture]
[NonParallelizable]
public class StandaloneMapStreamingEnabledTest
{
    // Streaming-ON paths: per-player gating in CommandHandler.Send, SendWorldData skipping mapData,
    // PlayerCount → MapResponse → Client_MapLoaded ack flow with buffered ordered drain. Streaming-OFF
    // mirror lives in StandaloneMapStreamingTest.cs.
    //
    // State machine:
    //   inFlightMapIds — Set of mapIds the server has issued MapResponse for and is awaiting ack.
    //   loadedMapIds   — Set of mapIds the client has acked (only via inFlightMapIds → loadedMapIds).
    //   pendingMapCmds — Server_Command payloads buffered while inFlightMapIds.Count > 0; drained in
    //                    arrival order when the LAST in-flight ack settles.
    // A cmd is *relevant* to a player when mapId<0 OR loadedMapIds.Contains OR inFlightMapIds.Contains.
    // Irrelevant cmds are dropped from this player's stream and replayed via MapResponse snapshot
    // when (if) the player ever switches to that map.

    private MultiplayerServer server = null!;
    private int nextPlayerId;
    private Action<string>? prevServerLogError;

    [SetUp]
    public void SetUp()
    {
        prevServerLogError = ServerLog.error;
        ServerLog.error = _ => { };
        server = MultiplayerServer.instance = new MultiplayerServer(new ServerSettings
        {
            gameName = "Test",
            direct = false,
            lan = false
        })
        {
            IsStandaloneServer = true,
        };
        server.worldData.savedGame = Array.Empty<byte>();
        server.worldData.sessionData = Array.Empty<byte>();
        nextPlayerId = 1;
    }

    [TearDown]
    public void TearDown()
    {
        MultiplayerServer.instance = null;
        ServerLog.error = prevServerLogError;
    }

    private (ServerPlayer player, RecordingConnection conn) AddPlayer(string username, int currentMapId, bool hasReportedCurrentMap = true, params int[] loadedMaps)
    {
        var conn = new RecordingConnection(username);
        var player = new ServerPlayer(nextPlayerId++, conn)
        {
            currentMapId = currentMapId,
            hasReportedCurrentMap = hasReportedCurrentMap,
        };
        foreach (var m in loadedMaps) player.loadedMapIds.Add(m);
        conn.serverPlayer = player;
        conn.ChangeState(ConnectionStateEnum.ServerPlaying);
        server.playerManager.Players.Add(player);
        return (player, conn);
    }

    [Test]
    public void Streaming_InitialJoinSendsNoMapData()
    {
        server.worldData.mapData[3] = new byte[] { 1, 2 };
        server.worldData.mapData[5] = new byte[] { 3, 4 };
        server.worldData.mapCmds[ScheduledCommand.Global] = new() { ScheduledCommand.Serialize(new ScheduledCommand(CommandType.Designator, 0, 0, -1, 0, [])) };
        server.worldData.mapCmds[5] = new() { ScheduledCommand.Serialize(new ScheduledCommand(CommandType.Designator, 0, 0, 5, 1, [])) };

        var conn = new RecordingConnection("joiner");
        var player = new ServerPlayer(nextPlayerId++, conn);
        conn.serverPlayer = player;
        server.playerManager.Players.Add(player);

        var state = new ServerLoadingState(conn);
        state.SendWorldData();

        var worldDataMessages = conn.SentMessages.Where(m => m.id == Packets.Server_WorldData).ToList();
        worldDataMessages.Should().HaveCount(1, "test payload is small; SendFragmented should emit a single non-fragmented packet");

        var reader = new ByteReader(worldDataMessages[0].body);
        reader.ReadInt32(); // factionId
        reader.ReadInt32(); // gameTimer
        reader.ReadInt32(); // sentCmds
        reader.ReadBool();  // frozen
        reader.ReadPrefixedBytes(); // savedGame
        reader.ReadPrefixedBytes(); // sessionData

        int mapCmdsCount = reader.ReadInt32();
        mapCmdsCount.Should().Be(1, "streaming ships only entries with mapId<0 (globals)");
        int firstMapId = reader.ReadInt32();
        firstMapId.Should().BeLessThan(0);
        int firstMapCmdsLen = reader.ReadInt32();
        for (int i = 0; i < firstMapCmdsLen; i++) reader.ReadPrefixedBytes();

        int mapDataCount = reader.ReadInt32();
        mapDataCount.Should().Be(0, "no per-map data is baked into the streaming initial payload");
    }

    [Test]
    public void Streaming_InitialJoinSeedsPerPlayerSentCmdsBaseline()
    {
        server.worldData.mapData[3] = new byte[] { 1 };
        server.commands.Send(CommandType.PauseAll, 0, ScheduledCommand.Global, []);
        server.commands.Send(CommandType.PauseAll, 0, ScheduledCommand.Global, []);
        var beforeBaseline = server.commands.SentCmds;
        beforeBaseline.Should().Be(2);

        var conn = new RecordingConnection("joiner");
        var player = new ServerPlayer(nextPlayerId++, conn);
        conn.serverPlayer = player;
        server.playerManager.Players.Add(player);

        var state = new ServerLoadingState(conn);
        state.SendWorldData();

        player.sentCmdsCount.Should().Be(beforeBaseline,
            "per-player counter must match the SentCmds baseline shipped in WorldData so the client's ProcessTimeControl gate stays consistent after join");
    }

    // ---------------- PlayerCount transitions ----------------

    [Test]
    public void Streaming_PlayerCountToUnloadedMap_SendsMapResponseAndAddsInFlight()
    {
        server.worldData.mapData[5] = new byte[] { 9 };
        var (player, conn) = AddPlayer("p", -1, hasReportedCurrentMap: false);

        var state = player.conn.GetState<ServerPlayingState>()!;
        state.HandleClientCommand(new ClientCommandPacket(
            CommandType.PlayerCount, ScheduledCommand.Global, ByteWriter.GetBytes(-1, 5)));

        player.currentMapId.Should().Be(5);
        player.inFlightMapIds.Should().Contain(5);
        player.loadedMapIds.Should().NotContain(5);
        conn.SentPackets.Should().Contain(Packets.Server_MapResponse);
    }

    [Test]
    public void Streaming_PlayerCountToLoadedMap_DoesNotResendOrAddInFlight()
    {
        server.worldData.mapData[5] = new byte[] { 9 };
        var (player, conn) = AddPlayer("p", -1, hasReportedCurrentMap: false, loadedMaps: 5);

        var state = player.conn.GetState<ServerPlayingState>()!;
        state.HandleClientCommand(new ClientCommandPacket(
            CommandType.PlayerCount, ScheduledCommand.Global, ByteWriter.GetBytes(-1, 5)));

        player.currentMapId.Should().Be(5);
        player.inFlightMapIds.Should().BeEmpty();
        conn.SentPackets.Should().NotContain(Packets.Server_MapResponse);
    }

    [Test]
    public void Streaming_PlayerCountToWorldView_NoMapResponseNoInFlight()
    {
        var (player, conn) = AddPlayer("p", 3, loadedMaps: 3);

        var state = player.conn.GetState<ServerPlayingState>()!;
        state.HandleClientCommand(new ClientCommandPacket(
            CommandType.PlayerCount, ScheduledCommand.Global, ByteWriter.GetBytes(3, -2)));

        player.currentMapId.Should().Be(-2);
        player.inFlightMapIds.Should().BeEmpty();
        conn.SentPackets.Should().NotContain(Packets.Server_MapResponse);
    }

    [Test]
    public void Streaming_PlayerCountToUntrackedMap_NoMapResponseNoInFlight()
    {
        // newMapId=99 not in worldData.mapData → CanUseStandaloneMapStreaming returns false.
        var (player, conn) = AddPlayer("p", 3, loadedMaps: 3);

        var state = player.conn.GetState<ServerPlayingState>()!;
        state.HandleClientCommand(new ClientCommandPacket(
            CommandType.PlayerCount, ScheduledCommand.Global, ByteWriter.GetBytes(3, 99)));

        player.currentMapId.Should().Be(99);
        player.inFlightMapIds.Should().BeEmpty();
        conn.SentPackets.Should().NotContain(Packets.Server_MapResponse);
    }

    // ---------------- CommandHandler.Send routing ----------------

    [Test]
    public void Streaming_CmdToLoadedMap_DeliveredAndCounted()
    {
        server.worldData.mapData[5] = new byte[] { 9 };
        var (player, conn) = AddPlayer("p", 5, loadedMaps: 5);

        server.commands.Send(CommandType.Designator, 0, 5, []);

        conn.SentPackets.Count(p => p == Packets.Server_Command).Should().Be(1);
        player.sentCmdsCount.Should().Be(1);
        player.pendingMapCmds.Should().BeEmpty();
    }

    [Test]
    public void Streaming_CmdToInFlightMap_BufferedNotDelivered()
    {
        server.worldData.mapData[5] = new byte[] { 9 };
        var (player, conn) = AddPlayer("p", 5);
        player.inFlightMapIds.Add(5);

        server.commands.Send(CommandType.Designator, 0, 5, []);

        conn.SentPackets.Should().NotContain(Packets.Server_Command);
        // sentCmdsCount tracks per-recipient seq baseline — it advances on commit (buffer-add or
        // immediate-send), not on wire-send specifically. The buffered packet was finalized with
        // seq=0; the next commit must use seq=1, so sentCmdsCount is already 1 here.
        player.sentCmdsCount.Should().Be(1);
        player.pendingMapCmds.Should().HaveCount(1);
    }

    [Test]
    public void Streaming_CmdToUnrelatedMap_NotDeliveredNoBuffer()
    {
        server.worldData.mapData[3] = new byte[] { 1 };
        server.worldData.mapData[7] = new byte[] { 2 };
        var (player, conn) = AddPlayer("p", 3, loadedMaps: 3);

        server.commands.Send(CommandType.Designator, 0, 7, []);

        conn.SentPackets.Should().NotContain(Packets.Server_Command);
        player.sentCmdsCount.Should().Be(0);
        player.pendingMapCmds.Should().BeEmpty();
    }

    [Test]
    public void Streaming_PendingPlayer_DropsCmdForUnrelatedMap()
    {
        // Player loading map 5, has map 3 loaded. Cmd for map 7 (neither in-flight nor loaded) must
        // NOT enter the buffer — otherwise the post-ack drain would deliver a cmd for an unloaded
        // map and the client would either lose it via TickPatch.deferredCmds or stall it.
        server.worldData.mapData[3] = new byte[] { 1 };
        server.worldData.mapData[5] = new byte[] { 2 };
        server.worldData.mapData[7] = new byte[] { 3 };
        var (player, conn) = AddPlayer("p", 5, loadedMaps: 3);
        player.inFlightMapIds.Add(5);

        server.commands.Send(CommandType.Designator, 0, 7, []);

        player.pendingMapCmds.Should().BeEmpty();
        player.sentCmdsCount.Should().Be(0);
        conn.SentPackets.Should().NotContain(Packets.Server_Command);
    }

    [Test]
    public void Streaming_PendingPlayer_BuffersGlobalCmdToPreserveOrder()
    {
        server.worldData.mapData[5] = new byte[] { 9 };
        var (player, conn) = AddPlayer("p", 5);
        player.inFlightMapIds.Add(5);

        server.commands.Send(CommandType.PauseAll, 0, ScheduledCommand.Global, []);

        conn.SentPackets.Should().NotContain(Packets.Server_Command);
        // Same invariant as Streaming_CmdToInFlightMap_BufferedNotDelivered: buffer-add commits a
        // seq to this player's stream, so sentCmdsCount advances by 1 even though no bytes hit
        // the wire yet. Drain in HandleMapLoaded sends the bytes without further increments.
        player.sentCmdsCount.Should().Be(1);
        player.pendingMapCmds.Should().HaveCount(1);
    }

    [Test]
    public void Streaming_PendingPlayer_BuffersLoadedMapCmdToPreserveOrder()
    {
        server.worldData.mapData[3] = new byte[] { 1 };
        server.worldData.mapData[5] = new byte[] { 2 };
        var (player, conn) = AddPlayer("p", 5, loadedMaps: 3);
        player.inFlightMapIds.Add(5);

        server.commands.Send(CommandType.Designator, 0, 3, []);

        conn.SentPackets.Should().NotContain(Packets.Server_Command);
        player.pendingMapCmds.Should().HaveCount(1);
    }

    [Test]
    public void Streaming_GlobalCmd_DeliveredToAllAndCounted()
    {
        server.worldData.mapData[3] = new byte[] { 1 };
        var (a, connA) = AddPlayer("a", 3, loadedMaps: 3);
        var (b, connB) = AddPlayer("b", -2);
        var (c, connC) = AddPlayer("c", -1, hasReportedCurrentMap: false);

        server.commands.Send(CommandType.PauseAll, 0, ScheduledCommand.Global, []);

        connA.SentPackets.Should().Contain(Packets.Server_Command);
        connB.SentPackets.Should().Contain(Packets.Server_Command);
        connC.SentPackets.Should().Contain(Packets.Server_Command);
        a.sentCmdsCount.Should().Be(1);
        b.sentCmdsCount.Should().Be(1);
        c.sentCmdsCount.Should().Be(1);
    }

    [Test]
    public void Streaming_MultiplePlayers_EachGetsOwnSentCmdsCount()
    {
        server.worldData.mapData[5] = new byte[] { 1 };
        server.worldData.mapData[7] = new byte[] { 2 };
        var (a, _) = AddPlayer("a", 5, loadedMaps: 5);
        var (b, _) = AddPlayer("b", 7, loadedMaps: 7);

        server.commands.Send(CommandType.Designator, 0, 5, []);
        server.commands.Send(CommandType.Designator, 0, 5, []);
        server.commands.Send(CommandType.Designator, 0, 7, []);

        a.sentCmdsCount.Should().Be(2);
        b.sentCmdsCount.Should().Be(1);
    }

    // ---------------- Client_MapLoaded ack handling ----------------

    [Test]
    public void Streaming_MapLoadedAck_ForInFlightMap_DrainsBufferAndMarksLoaded()
    {
        server.worldData.mapData[5] = new byte[] { 9 };
        var (player, conn) = AddPlayer("p", 5);
        player.inFlightMapIds.Add(5);
        // Tests that bypass HandleClientCommand → SendMapResponse must seed the transferId
        // generation themselves; HandleMapLoaded now drops acks whose id doesn't match.
        player.mapTransferIds[5] = 1;

        server.commands.Send(CommandType.Designator, 0, 5, []);
        server.commands.Send(CommandType.Designator, 0, 5, []);
        server.commands.Send(CommandType.Designator, 0, 5, []);

        player.pendingMapCmds.Should().HaveCount(3);
        conn.SentPackets.Should().NotContain(Packets.Server_Command);

        var state = player.conn.GetState<ServerPlayingState>()!;
        state.HandleMapLoaded(new ClientMapLoadedPacket(5, 1));

        conn.SentPackets.Count(p => p == Packets.Server_Command).Should().Be(3);
        player.sentCmdsCount.Should().Be(3);
        player.inFlightMapIds.Should().BeEmpty();
        player.loadedMapIds.Should().Contain(5);
        player.pendingMapCmds.Should().BeEmpty();
    }

    [Test]
    public void Streaming_UnsolicitedMapLoadedAck_Ignored()
    {
        // Client sends Client_MapLoaded(99) without the server ever sending MapResponse(99). The
        // server must NOT add 99 to loadedMapIds — otherwise future map-99 cmds get routed to a
        // client that has no map data for it.
        server.worldData.mapData[5] = new byte[] { 9 };
        var (player, conn) = AddPlayer("p", 5);
        // No inFlightMapIds setup.

        var state = player.conn.GetState<ServerPlayingState>()!;
        state.HandleMapLoaded(new ClientMapLoadedPacket(99, 1));

        player.loadedMapIds.Should().NotContain(99);
        player.inFlightMapIds.Should().BeEmpty();
        player.pendingMapCmds.Should().BeEmpty();
    }

    [Test]
    public void Streaming_RapidMapSwitch_DrainsAfterLastInFlightAcks()
    {
        // PlayerCount(→5) → MapResponse(5), buffered cmd (global), PlayerCount(→7) before ack(5)
        // → MapResponse(7). Buffer must survive across the second transition. Final drain owed
        // once both 5 and 7 are acked, in arrival order.
        server.worldData.mapData[5] = new byte[] { 9 };
        server.worldData.mapData[7] = new byte[] { 8 };
        var (player, conn) = AddPlayer("p", -1, hasReportedCurrentMap: false);
        var state = player.conn.GetState<ServerPlayingState>()!;

        state.HandleClientCommand(new ClientCommandPacket(
            CommandType.PlayerCount, ScheduledCommand.Global, ByteWriter.GetBytes(-1, 5)));
        player.inFlightMapIds.Should().Contain(5);

        server.commands.Send(CommandType.PauseAll, 0, ScheduledCommand.Global, [0xAA]);
        var bufferAfterFirstGlobal = player.pendingMapCmds.Count;
        bufferAfterFirstGlobal.Should().BeGreaterThan(0);

        state.HandleClientCommand(new ClientCommandPacket(
            CommandType.PlayerCount, ScheduledCommand.Global, ByteWriter.GetBytes(5, 7)));
        player.inFlightMapIds.Should().Contain(7).And.Contain(5);
        player.pendingMapCmds.Count.Should().BeGreaterThanOrEqualTo(bufferAfterFirstGlobal,
            "rapid switch must NOT clear earlier buffered entries");

        // Ack(5) first: still in-flight (7), no drain.
        state.HandleMapLoaded(new ClientMapLoadedPacket(5, player.mapTransferIds[5]));
        player.loadedMapIds.Should().Contain(5);
        player.inFlightMapIds.Should().NotContain(5).And.Contain(7);
        conn.SentPackets.Should().NotContain(Packets.Server_Command, "drain only on final ack");

        // Ack(7) drains everything.
        var bufferedFinal = player.pendingMapCmds.Count;
        state.HandleMapLoaded(new ClientMapLoadedPacket(7, player.mapTransferIds[7]));
        player.loadedMapIds.Should().Contain(7);
        player.inFlightMapIds.Should().BeEmpty();
        player.pendingMapCmds.Should().BeEmpty();
        conn.SentPackets.Count(p => p == Packets.Server_Command).Should().Be(bufferedFinal);
        player.sentCmdsCount.Should().Be(bufferedFinal);
    }

    [Test]
    public void Streaming_LeavePendingToWorldView_BufferDrainsOnEventualAck()
    {
        // pending=5, buffer accumulates, then PlayerCount(5, -2) (world view). Sentinel doesn't
        // remove inFlight={5}, so when ack(5) eventually arrives, the buffer drains. Critical:
        // the buffer is NOT stranded just because the player switched away mid-load.
        server.worldData.mapData[5] = new byte[] { 9 };
        var (player, conn) = AddPlayer("p", -1, hasReportedCurrentMap: false);
        var state = player.conn.GetState<ServerPlayingState>()!;

        state.HandleClientCommand(new ClientCommandPacket(
            CommandType.PlayerCount, ScheduledCommand.Global, ByteWriter.GetBytes(-1, 5)));
        server.commands.Send(CommandType.PauseAll, 0, ScheduledCommand.Global, [0xAA]);
        var bufferedBefore = player.pendingMapCmds.Count;
        bufferedBefore.Should().BeGreaterThan(0);

        // World view transition while map 5 is still in flight. inFlightMapIds should still include 5.
        state.HandleClientCommand(new ClientCommandPacket(
            CommandType.PlayerCount, ScheduledCommand.Global, ByteWriter.GetBytes(5, -2)));
        player.inFlightMapIds.Should().Contain(5,
            "leaving for a sentinel must not abandon the in-flight transfer");

        // Subsequent global STILL buffers because inFlight is non-empty (preserves order).
        server.commands.Send(CommandType.PauseAll, 0, ScheduledCommand.Global, [0xBB]);
        player.pendingMapCmds.Count.Should().BeGreaterThan(bufferedBefore);

        // Eventual ack drains everything.
        var bufferedFinal = player.pendingMapCmds.Count;
        state.HandleMapLoaded(new ClientMapLoadedPacket(5, player.mapTransferIds[5]));
        player.inFlightMapIds.Should().BeEmpty();
        conn.SentPackets.Count(p => p == Packets.Server_Command).Should().Be(bufferedFinal);
        player.sentCmdsCount.Should().Be(bufferedFinal);
    }

    [Test]
    public void Streaming_Rejoin_ResetsStreamingState()
    {
        // After Client_RequestRejoin the client throws away its world and gets a fresh
        // Server_WorldData with no maps — the server-side mirror must clear, otherwise the next
        // PlayerCount on a previously-loaded map would skip MapResponse and route map-scoped
        // cmds to a client that no longer has the map data.
        server.worldData.mapData[5] = new byte[] { 9 };
        var (player, conn) = AddPlayer("p", 5, loadedMaps: 5);
        player.inFlightMapIds.Add(7);
        player.pendingMapCmds.Add([1, 2, 3]);
        player.sentCmdsCount = 42;
        player.mapTransferIds[5] = 3;
        player.mapTransferIds[7] = 1;

        var state = player.conn.GetState<ServerPlayingState>()!;
        state.HandleRejoin(new ByteReader(Array.Empty<byte>()));

        player.loadedMapIds.Should().BeEmpty();
        player.inFlightMapIds.Should().BeEmpty();
        player.pendingMapCmds.Should().BeEmpty();
        player.pendingBufferOverflowed.Should().BeFalse();
        // mapTransferIds is intentionally NOT cleared on rejoin: the generation namespace must
        // stay monotonic across the connection lifetime so a delayed ack from before the rejoin
        // can never match the post-rejoin generation. Clearing here would let an old K=1 ack
        // drain a new K=1 transfer's buffer.
        player.mapTransferIds.Should().ContainKey(5).WhoseValue.Should().Be(3);
        player.mapTransferIds.Should().ContainKey(7).WhoseValue.Should().Be(1);
        // sentCmdsCount: SendWorldData (synchronous on the loading state's async machine) re-seeds
        // it from the SentCmds baseline; ResetTimeVotes then sends one cmd through the streaming
        // branch which lands on the just-rejoined player. The invariant we care about is that
        // post-rejoin sentCmdsCount tracks SentCmds atomically — not that it equals zero.
        player.sentCmdsCount.Should().Be(server.commands.SentCmds);
    }

    [Test]
    public void Streaming_DuplicatePlayerCount_DoesNotResendMapResponse()
    {
        // PlayerCount(→5) twice in a row before the client acks. Second one must NOT issue another
        // MapResponse — the original transfer is still in flight; double-resending would force the
        // client into a second ReloadGame even after the first ack already settled the buffer.
        server.worldData.mapData[5] = new byte[] { 9 };
        var (player, conn) = AddPlayer("p", -1, hasReportedCurrentMap: false);
        var state = player.conn.GetState<ServerPlayingState>()!;

        state.HandleClientCommand(new ClientCommandPacket(
            CommandType.PlayerCount, ScheduledCommand.Global, ByteWriter.GetBytes(-1, 5)));
        state.HandleClientCommand(new ClientCommandPacket(
            CommandType.PlayerCount, ScheduledCommand.Global, ByteWriter.GetBytes(5, 5)));

        conn.SentPackets.Count(p => p == Packets.Server_MapResponse).Should().Be(1);
        player.inFlightMapIds.Should().Contain(5);
    }

    [Test]
    public void Streaming_OverflowedClient_DispatchReturnsNoOpForAllPackets()
    {
        // Per-handler early returns cover the few mutation paths we audited (ClientCommand,
        // MapLoaded, Rejoin), but the canonical seal is at packet dispatch. GetPacketHandler must
        // route every packet from a terminal player to a no-op until the queued Disconnect runs —
        // otherwise host/arbiter overflow could still hit WorldDataUpload / Autosaving / Debug
        // / DesyncCheck etc. and an ordinary overflow could mutate via Chat / Cursor / Selected /
        // SetFaction / Freeze / FrameTime.
        server.worldData.mapData[5] = new byte[] { 9 };
        var (player, _) = AddPlayer("p", 5);
        player.inFlightMapIds.Add(5);
        ServerLog.error = _ => { };
        for (int i = 0; i <= ServerPlayer.MaxPendingMapCmds; i++)
            server.commands.Send(CommandType.Designator, 0, 5, []);
        player.pendingBufferOverflowed.Should().BeTrue();

        var state = player.conn.GetState<ServerPlayingState>()!;

        // Spot-check several different packet ids — all should map to the same terminal no-op.
        Packets[] sampleIds =
        [
            Packets.Client_Command, Packets.Client_MapLoaded, Packets.Client_RequestRejoin,
            Packets.Client_Chat, Packets.Client_Cursor, Packets.Client_SetFaction,
            Packets.Client_Freeze, Packets.Client_Debug, Packets.Client_FrameTime,
            Packets.Client_Selected, Packets.Client_PingLocation, Packets.Client_KeepAlive,
            Packets.Client_WorldDataUpload, Packets.Client_SyncInfo, Packets.Client_Autosaving,
        ];

        var handlers = sampleIds
            .Select(id => state.GetPacketHandler(id))
            .ToList();

        handlers.Should().AllSatisfy(h => h.Should().NotBeNull(
            "every packet from an overflowed player gets a no-op handler so HandleReceiveRaw doesn't throw and doesn't log 'not fully consumed'"));
        // All sampled ids map to the SAME singleton no-op (not the real handlers).
        handlers.Distinct().Should().HaveCount(1, "GetPacketHandler returns one shared no-op for terminal players");

        // Validate the no-op contract: invoking it on a non-empty buffer seeks to end and mutates nothing.
        var w = new ByteWriter();
        w.WriteInt32(0xDEADBEEF.GetHashCode());
        w.WriteInt32(123);
        var rd = new ByteReader(w.ToArray());
        handlers[0]!.Method(state, rd);
        rd.Left.Should().Be(0, "no-op consumes the packet body so the up-stream 'not fully consumed' check stays quiet");

        // Fragment=false: if a fragmented packet arrives from a terminal player, HandleReceiveFragment
        // throws PacketReadException before any FragmentedPacket buffer (up to MaxFragmentPacketTotalSize)
        // gets allocated. With Fragment=true the attacker could open a fresh memory-DoS window in the
        // disconnect gap, even with the handler itself being a no-op.
        handlers[0]!.Fragment.Should().BeFalse(
            "terminal handler must not allow fragment assembly — that would re-open allocation in the disconnect gap");
    }

    [Test]
    public void Streaming_OverflowedClient_HandleClientCommandRejectedBeforePlayerCountMutation()
    {
        // Closes the gap where PlayerCount in HandleClientCommand mutates currentMapId,
        // hasReportedCurrentMap, inFlightMapIds and triggers SendMapResponse BEFORE the
        // CommandHandler.Send guard fires. Early-return at HandleClientCommand entry is the
        // only place this can be stopped.
        server.worldData.mapData[5] = new byte[] { 9 };
        server.worldData.mapData[7] = new byte[] { 8 };
        var (player, conn) = AddPlayer("p", 5);
        player.inFlightMapIds.Add(5);
        ServerLog.error = _ => { };

        for (int i = 0; i <= ServerPlayer.MaxPendingMapCmds; i++)
            server.commands.Send(CommandType.Designator, 0, 5, []);
        player.pendingBufferOverflowed.Should().BeTrue();

        var prevCurrentMap = player.currentMapId;
        var inFlightBefore = player.inFlightMapIds.ToHashSet();
        var mapResponsesBefore = conn.SentPackets.Count(p => p == Packets.Server_MapResponse);

        var state = player.conn.GetState<ServerPlayingState>()!;
        state.HandleClientCommand(new ClientCommandPacket(
            CommandType.PlayerCount, ScheduledCommand.Global, ByteWriter.GetBytes(5, 7)));

        player.currentMapId.Should().Be(prevCurrentMap, "currentMapId must not move for a terminal player");
        player.hasReportedCurrentMap.Should().BeTrue("AddPlayer set this true; it must remain unchanged");
        player.inFlightMapIds.Should().BeEquivalentTo(inFlightBefore, "no new in-flight map should be queued");
        conn.SentPackets.Count(p => p == Packets.Server_MapResponse).Should().Be(mapResponsesBefore,
            "no MapResponse should be issued for a terminal player");
    }

    [Test]
    public void Streaming_OverflowedClient_HandleRejoinDoesNotResetLatch()
    {
        // Rejoin would otherwise clear loadedMapIds/inFlightMapIds/pendingMapCmds and reset
        // pendingBufferOverflowed=false, putting the player back into a fresh loading flow and
        // defeating the queued terminal Disconnect. Must drop the rejoin request.
        server.worldData.mapData[5] = new byte[] { 9 };
        var (player, conn) = AddPlayer("p", 5, loadedMaps: 5);
        player.inFlightMapIds.Add(7);
        ServerLog.error = _ => { };

        for (int i = 0; i <= ServerPlayer.MaxPendingMapCmds; i++)
            server.commands.Send(CommandType.Designator, 0, 7, []);
        player.pendingBufferOverflowed.Should().BeTrue();

        var loadedBefore = player.loadedMapIds.ToHashSet();
        var inFlightBefore = player.inFlightMapIds.ToHashSet();
        var bufferedBefore = player.pendingMapCmds.Count;
        var prevState = player.conn.State;

        var state = player.conn.GetState<ServerPlayingState>()!;
        state.HandleRejoin(new ByteReader(Array.Empty<byte>()));

        player.pendingBufferOverflowed.Should().BeTrue("latch must remain set");
        player.loadedMapIds.Should().BeEquivalentTo(loadedBefore, "no streaming-state reset for a terminal player");
        player.inFlightMapIds.Should().BeEquivalentTo(inFlightBefore);
        player.pendingMapCmds.Count.Should().Be(bufferedBefore);
        player.conn.State.Should().Be(prevState, "no state transition: client cannot escape the queued Disconnect via rejoin");
    }

    [Test]
    public void Streaming_OverflowedSource_FurtherCmdsRejectedBeforeMutatingWorldData()
    {
        // After overflow latches the player, any further cmds *from* that player must not mutate
        // worldData.mapCmds or increment SentCmds, even though the queued Disconnect hasn't run.
        // Otherwise pathological clients keep polluting authoritative state in the gap.
        server.worldData.mapData[5] = new byte[] { 9 };
        var (player, _) = AddPlayer("p", 5);
        player.inFlightMapIds.Add(5);
        ServerLog.error = _ => { };

        // Trip the cap.
        for (int i = 0; i <= ServerPlayer.MaxPendingMapCmds; i++)
            server.commands.Send(CommandType.Designator, 0, 5, []);

        player.pendingBufferOverflowed.Should().BeTrue();
        var sentCmdsBefore = server.commands.SentCmds;
        var mapCmdsBefore = server.worldData.mapCmds[5].Count;

        // Pathological client keeps sending Client_Command-like flows: simulate by routing Send
        // with the overflow'd player as sourcePlayer. CommandHandler must short-circuit before
        // recording into worldData.
        server.commands.Send(CommandType.Designator, 0, 5, [], sourcePlayer: player);
        server.commands.Send(CommandType.PauseAll, 0, ScheduledCommand.Global, [], sourcePlayer: player);

        server.commands.SentCmds.Should().Be(sentCmdsBefore, "SentCmds must not advance for an overflowed source");
        server.worldData.mapCmds[5].Count.Should().Be(mapCmdsBefore, "worldData must not accept further cmds from an overflowed source");
    }

    [Test]
    public void Streaming_OverflowedClient_AckDoesNotDrainOrResetLatch()
    {
        // After overflow → Disconnect queued, an ack landing in the gap must not drain the partial
        // buffer or clear the latch — that would re-open live routing for a player about to leave.
        server.worldData.mapData[5] = new byte[] { 9 };
        var (player, conn) = AddPlayer("p", 5);
        player.inFlightMapIds.Add(5);
        ServerLog.error = _ => { };

        for (int i = 0; i <= ServerPlayer.MaxPendingMapCmds; i++)
            server.commands.Send(CommandType.Designator, 0, 5, []);

        player.pendingBufferOverflowed.Should().BeTrue();
        var bufferedBefore = player.pendingMapCmds.Count;

        // Ack arrives in the gap before the queued Disconnect runs.
        var state = player.conn.GetState<ServerPlayingState>()!;
        state.HandleMapLoaded(new ClientMapLoadedPacket(5, 1));

        player.pendingBufferOverflowed.Should().BeTrue("latch must remain set; this ack does NOT recover the player");
        player.inFlightMapIds.Should().Contain(5, "in-flight set must NOT be drained by an ack while overflowed");
        player.pendingMapCmds.Count.Should().Be(bufferedBefore, "buffer must not drain");
        conn.SentPackets.Should().NotContain(Packets.Server_Command, "no live cmds may be emitted to a terminal player");
    }

    [Test]
    public void Streaming_PendingBufferCap_TerminallyDisconnectsClient()
    {
        // Pathological client: requested a map and never acked. Buffer hits the cap → server
        // schedules a disconnect (terminal policy). Silently dropping cmds and keeping the player
        // would produce a guaranteed desync, which is worse than an explicit disconnect.
        server.worldData.mapData[5] = new byte[] { 9 };
        var (player, _) = AddPlayer("p", 5);
        player.inFlightMapIds.Add(5);

        int errorCount = 0;
        ServerLog.error = _ => { errorCount++; };

        for (int i = 0; i <= ServerPlayer.MaxPendingMapCmds; i++)
            server.commands.Send(CommandType.Designator, 0, 5, []);

        player.pendingMapCmds.Count.Should().Be(ServerPlayer.MaxPendingMapCmds,
            "buffer caps at MaxPendingMapCmds; further cmds are dropped because a disconnect is now pending");
        player.pendingBufferOverflowed.Should().BeTrue();
        errorCount.Should().Be(1, "overflow logs exactly once per terminal event");

        // Further cmds while disconnect is pending: still capped, no duplicate disconnect enqueued
        // and no log spam (latch suppresses both).
        server.commands.Send(CommandType.Designator, 0, 5, []);
        server.commands.Send(CommandType.Designator, 0, 5, []);
        player.pendingMapCmds.Count.Should().Be(ServerPlayer.MaxPendingMapCmds);
        errorCount.Should().Be(1);

        // Drain the action queue and confirm the player actually leaves the server.
        server.queue.RunQueue(_ => { });
        server.playerManager.Players.Should().NotContain(player,
            "Disconnect should remove the player from the server");
    }

    [Test]
    public void Streaming_NormalisesSavedGameCurrentMapIndex()
    {
        // Real-shape mini-save: GZipped XML with <game><currentMapIndex>0</currentMapIndex></game>.
        // After streaming-join normalisation the round-tripped XML must have currentMapIndex stripped
        // so RimWorld's Scribe defaults it to -1 (world view) on a client with zero maps.
        var doc = new System.Xml.XmlDocument();
        doc.LoadXml("<savegame><game><currentMapIndex>0</currentMapIndex><other>keep</other></game></savegame>");
        var ms = new MemoryStream();
        using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
            doc.Save(gz);
        server.worldData.savedGame = ms.ToArray();

        var normalised = server.worldData.GetSavedGameForStreaming();
        normalised.Should().NotBeNull();
        normalised.Should().NotBeEquivalentTo(server.worldData.savedGame, "normalisation must produce a different payload");

        var decoded = new System.Xml.XmlDocument();
        using (var memIn = new MemoryStream(normalised!))
        using (var gzIn = new System.IO.Compression.GZipStream(memIn, System.IO.Compression.CompressionMode.Decompress))
            decoded.Load(gzIn);
        decoded.SelectSingleNode("//game/currentMapIndex").Should().BeNull();
        decoded.SelectSingleNode("//game/other").Should().NotBeNull("untouched siblings stay");

        // Cache: same source array → same returned reference.
        ReferenceEquals(server.worldData.GetSavedGameForStreaming(), normalised).Should().BeTrue();

        // Cache invalidates on savedGame replacement.
        server.worldData.savedGame = ms.ToArray();
        ReferenceEquals(server.worldData.GetSavedGameForStreaming(), normalised).Should().BeFalse();
    }

    [Test]
    public void Streaming_SavedGameNormalisationFallsBackOnGarbage()
    {
        // Empty buffer (test/uninitialised). GetSavedGameForStreaming must return as-is, not throw.
        server.worldData.savedGame = Array.Empty<byte>();
        server.worldData.GetSavedGameForStreaming().Should().BeSameAs(server.worldData.savedGame);

        // Non-GZip garbage. Same fallback.
        server.worldData.savedGame = new byte[] { 1, 2, 3, 4, 5 };
        ServerLog.error = _ => { }; // silence error log from the fallback path
        server.worldData.GetSavedGameForStreaming().Should().BeSameAs(server.worldData.savedGame);
    }

    [Test]
    public void Streaming_DrainPreservesInsertionOrder()
    {
        server.worldData.mapData[3] = new byte[] { 1 };
        server.worldData.mapData[5] = new byte[] { 2 };
        var (player, conn) = AddPlayer("p", 5, loadedMaps: 3);
        player.inFlightMapIds.Add(5);
        player.mapTransferIds[5] = 1;

        server.commands.Send(CommandType.PauseAll, 0, ScheduledCommand.Global, [0xAA]);
        server.commands.Send(CommandType.Designator, 0, 5, [0xBB]);
        server.commands.Send(CommandType.PauseAll, 0, ScheduledCommand.Global, [0xCC]);
        server.commands.Send(CommandType.Designator, 0, 5, [0xDD]);
        server.commands.Send(CommandType.Designator, 0, 3, [0xEE]); // loaded map: also buffered

        player.pendingMapCmds.Should().HaveCount(5);

        var state = player.conn.GetState<ServerPlayingState>()!;
        state.HandleMapLoaded(new ClientMapLoadedPacket(5, 1));

        var commandMessages = conn.SentMessages.Where(m => m.id == Packets.Server_Command).ToList();
        commandMessages.Should().HaveCount(5);

        var markers = commandMessages.Select(m =>
        {
            var packet = new ServerCommandPacket();
            packet.Bind(new PacketReader(new ByteReader(m.body)));
            return packet.data;
        }).Where(d => d.Length == 1).Select(d => d[0]).ToArray();
        markers.Should().Equal((byte)0xAA, (byte)0xBB, (byte)0xCC, (byte)0xDD, (byte)0xEE);
        player.sentCmdsCount.Should().Be(5);
    }

    // ---------------- transferId / snapshotCommandSeq on the wire ----------------

    [Test]
    public void Streaming_MapLoadedAck_WithStaleTransferId_Ignored()
    {
        // A second SendMapResponse for the same mapId bumps the generation. An ack carrying the
        // OLD generation must NOT drain or mark the map loaded — that ack belongs to a transfer
        // superseded by a Rejoin or by a re-stream, and honouring it would either drain a buffer
        // built for the new transfer or remove the in-flight current generation prematurely.
        server.worldData.mapData[5] = new byte[] { 9 };
        var (player, conn) = AddPlayer("p", -1, hasReportedCurrentMap: false);
        var state = player.conn.GetState<ServerPlayingState>()!;

        // First transfer: PlayerCount → SendMapResponse → mapTransferIds[5] = 1.
        state.HandleClientCommand(new ClientCommandPacket(
            CommandType.PlayerCount, ScheduledCommand.Global, ByteWriter.GetBytes(-1, 5)));
        player.mapTransferIds[5].Should().Be(1);
        var firstTransferId = player.mapTransferIds[5];

        // Force a second SendMapResponse for the SAME mapId without going through the rejoin/clear
        // path that would reset the dictionary. inFlightMapIds.Clear() lets us re-trigger; the
        // generation MUST advance from 1 → 2 (that's the whole point of mapTransferIds).
        player.inFlightMapIds.Clear();
        server.SendMapResponse(player, 5);
        var secondTransferId = player.mapTransferIds[5];
        secondTransferId.Should().Be(firstTransferId + 1, "second SendMapResponse must increment generation");

        // Buffer a cmd while the (current) transfer is in flight.
        player.inFlightMapIds.Add(5);
        server.commands.Send(CommandType.Designator, 0, 5, [0xCC]);
        var bufferedBefore = player.pendingMapCmds.Count;
        bufferedBefore.Should().BeGreaterThan(0, "buffer must hold at least the cmd we just sent");

        // Stale ack carrying the OLD generation. Must be a no-op.
        var commandsBefore = conn.SentPackets.Count(p => p == Packets.Server_Command);
        state.HandleMapLoaded(new ClientMapLoadedPacket(5, firstTransferId));

        player.pendingMapCmds.Count.Should().Be(bufferedBefore, "stale ack must NOT drain the buffer");
        player.loadedMapIds.Should().NotContain(5, "stale ack must NOT mark the map loaded");
        player.inFlightMapIds.Should().Contain(5, "stale ack must NOT clear inFlight for the current transfer");
        conn.SentPackets.Count(p => p == Packets.Server_Command).Should().Be(commandsBefore,
            "stale ack must NOT push buffered cmds to the wire");

        // Current ack drains as expected.
        state.HandleMapLoaded(new ClientMapLoadedPacket(5, secondTransferId));
        player.loadedMapIds.Should().Contain(5);
        player.pendingMapCmds.Should().BeEmpty();
        conn.SentPackets.Count(p => p == Packets.Server_Command).Should().Be(commandsBefore + bufferedBefore);
    }

    [Test]
    public void Streaming_TransferIds_StayMonotonicAcrossRejoin()
    {
        // Regression: HandleRejoin used to call mapTransferIds.Clear(), which made the next
        // SendMapResponse for that mapId reuse generation 1. A delayed Client_MapLoaded(mapId, 1)
        // from before the rejoin would then match the new transfer and drain its pending buffer.
        // Generations must stay strictly monotonic for the lifetime of the connection.
        server.worldData.mapData[5] = new byte[] { 9 };
        var (player, conn) = AddPlayer("p", -1, hasReportedCurrentMap: false);
        var state = player.conn.GetState<ServerPlayingState>()!;

        // Build up a non-trivial generation pre-rejoin.
        state.HandleClientCommand(new ClientCommandPacket(
            CommandType.PlayerCount, ScheduledCommand.Global, ByteWriter.GetBytes(-1, 5)));
        player.mapTransferIds[5].Should().Be(1);
        player.inFlightMapIds.Clear();
        server.SendMapResponse(player, 5);
        player.mapTransferIds[5].Should().Be(2);

        // Rejoin clears load/in-flight/buffer state but MUST NOT reset the generation namespace.
        state.HandleRejoin(new ByteReader(Array.Empty<byte>()));
        player.mapTransferIds.Should().ContainKey(5, "rejoin must preserve the per-mapId generation counter");
        player.mapTransferIds[5].Should().Be(2,
            "rejoin must preserve the existing generation so the next SendMapResponse advances to 3");

        // After rejoin, drive the loading-state path (which now also doesn't clear) and stream
        // the map again. The new generation must be strictly greater than any pre-rejoin value.
        var loadingState = new ServerLoadingState(player.conn);
        loadingState.SendWorldData();
        player.mapTransferIds[5].Should().Be(2,
            "SendWorldData must preserve the generation namespace too");

        player.inFlightMapIds.Clear();
        server.SendMapResponse(player, 5);
        player.mapTransferIds[5].Should().Be(3, "post-rejoin SendMapResponse must advance the generation");
    }

    [Test]
    public void Streaming_MapResponse_CarriesIncrementingTransferId()
    {
        // Each SendMapResponse for the same (player, mapId) must stamp a strictly increasing
        // transferId on the wire. That id is what the client echoes back in Client_MapLoaded; the
        // server-side generation gate uses it to discard stale acks.
        server.worldData.mapData[5] = new byte[] { 9 };
        var (player, conn) = AddPlayer("p", -1, hasReportedCurrentMap: false);

        server.SendMapResponse(player, 5);
        // Allow a re-trigger by clearing the in-flight bookkeeping (the test simulates two
        // distinct streaming attempts; we don't go through HandleClientCommand here because that
        // path also gates on `inFlightMapIds.Add` returning true).
        player.inFlightMapIds.Clear();
        server.SendMapResponse(player, 5);

        var responses = conn.SentMessages.Where(m => m.id == Packets.Server_MapResponse).ToList();
        responses.Should().HaveCount(2);

        // Wire layout: [mapId int32][transferId int32][snapshotCommandSeq int32]...
        var firstReader = new ByteReader(responses[0].body);
        firstReader.ReadInt32().Should().Be(5);
        int firstTransferId = firstReader.ReadInt32();

        var secondReader = new ByteReader(responses[1].body);
        secondReader.ReadInt32().Should().Be(5);
        int secondTransferId = secondReader.ReadInt32();

        firstTransferId.Should().Be(1, "first SendMapResponse stamps generation 1 on a fresh entry");
        secondTransferId.Should().Be(2, "second SendMapResponse must stamp the next generation");
        secondTransferId.Should().BeGreaterThan(firstTransferId, "transferId must be strictly monotonic per (player, mapId)");
    }

    [Test]
    public void Streaming_MapResponse_CarriesSnapshotCommandSeq()
    {
        // snapshotCommandSeq documents which player.sentCmdsCount baseline the snapshot's mapCmds
        // were taken under. Even though the client doesn't currently consume it (snapshot cmds
        // bypass HandleCommand → no seq enforcement), having it on the wire makes future
        // verification possible without another protocol bump.
        server.worldData.mapData[5] = new byte[] { 9 };
        server.worldData.mapData[7] = new byte[] { 8 };
        var (player, conn) = AddPlayer("p", 5, loadedMaps: 5);

        // N cmds for the loaded map land on the wire and bump player.sentCmdsCount.
        server.commands.Send(CommandType.Designator, 0, 5, []);
        server.commands.Send(CommandType.Designator, 0, 5, []);
        server.commands.Send(CommandType.Designator, 0, 5, []);
        var sentCmdsAtMapResponse = player.sentCmdsCount;
        sentCmdsAtMapResponse.Should().Be(3);

        // Trigger a fresh MapResponse for a different mapId. snapshotCommandSeq on the wire must
        // equal the player.sentCmdsCount value AT MapResponse send time.
        server.SendMapResponse(player, 7);

        var response = conn.SentMessages.Single(m => m.id == Packets.Server_MapResponse);
        var reader = new ByteReader(response.body);
        reader.ReadInt32().Should().Be(7); // mapId
        reader.ReadInt32().Should().Be(1); // transferId (first for mapId=7)
        int snapshotCommandSeq = reader.ReadInt32();
        snapshotCommandSeq.Should().Be(sentCmdsAtMapResponse,
            "snapshotCommandSeq on the wire must equal player.sentCmdsCount at MapResponse send time");
    }
}
