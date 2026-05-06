using FluentAssertions;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;

namespace Tests;

[NonParallelizable]
public class ServerPlayingStateTest
{
    private MultiplayerServer server = null!;
    private ServerPlayer host = null!;
    private ServerPlayer alice = null!;
    private ServerPlayer bob = null!;
    private Action<string>? prevServerLogError;

    [SetUp]
    public void SetUp()
    {
        // Tests are insensitive to caller-set ServerLog.error; restore in TearDown to keep the suite order-independent.
        prevServerLogError = ServerLog.error;
        ServerLog.error = _ => { };
        BuildServer(debugMode: false);
    }

    [TearDown]
    public void TearDown()
    {
        MultiplayerServer.instance = null;
        ServerLog.error = prevServerLogError;
    }

    // ---------- HandleSetFaction ----------

    [Test]
    public void SetFaction_PlayerCanChangeOwnFaction()
    {
        ClearSentPackets();
        var newFaction = alice.FactionId + 1;

        InvokeSetFaction(alice, target: alice.id, factionId: newFaction);

        alice.FactionId.Should().Be(newFaction);
        SentPacketsOf(alice).Should().Contain(Packets.Server_SetFaction);
    }

    [Test]
    public void SetFaction_NonHostCannotChangeAnotherPlayersFaction()
    {
        ClearSentPackets();
        var bobOriginalFaction = bob.FactionId;

        InvokeSetFaction(alice, target: bob.id, factionId: bobOriginalFaction + 999);

        bob.FactionId.Should().Be(bobOriginalFaction);
        SentPacketsAcrossAllPlayers().Should().NotContain(Packets.Server_SetFaction);
    }

    [Test]
    public void SetFaction_HostCanChangeAnotherPlayersFaction()
    {
        ClearSentPackets();
        var newFaction = bob.FactionId + 7;

        InvokeSetFaction(host, target: bob.id, factionId: newFaction);

        bob.FactionId.Should().Be(newFaction);
        SentPacketsOf(bob).Should().Contain(Packets.Server_SetFaction);
    }

    // ---------- HandleDebug ----------

    [Test]
    public void Debug_BlockedWhenDebugModeDisabled()
    {
        SeedServerState();
        ClearSentPackets();

        InvokeDebug(host); // even host is blocked when debug mode is off

        AssertServerStateUnchanged();
        SentPacketsAcrossAllPlayers().Should().NotContain(Packets.Server_Debug);
    }

    [Test]
    public void Debug_BlockedForNonHostWhenScopeIsHostOnly()
    {
        BuildServer(debugMode: true, scope: DevModeScope.HostOnly);
        SeedServerState();
        ClearSentPackets();

        InvokeDebug(alice);

        AssertServerStateUnchanged();
        SentPacketsAcrossAllPlayers().Should().NotContain(Packets.Server_Debug);
    }

    [Test]
    public void Debug_AllowedForHostWhenDebugModeEnabled()
    {
        BuildServer(debugMode: true, scope: DevModeScope.HostOnly);
        SeedServerState();
        ClearSentPackets();

        InvokeDebug(host);

        server.worldData.mapCmds.Should().BeEmpty();
        server.gameTimer.Should().Be(server.startingTimer);
        SentPacketsOf(host).Should().Contain(Packets.Server_Debug);
    }

    [Test]
    public void Debug_AllowedForAnyPlayerWhenScopeIsEveryone()
    {
        BuildServer(debugMode: true, scope: DevModeScope.Everyone);
        SeedServerState();
        ClearSentPackets();

        InvokeDebug(alice);

        server.worldData.mapCmds.Should().BeEmpty();
        server.gameTimer.Should().Be(server.startingTimer);
        SentPacketsOf(alice).Should().Contain(Packets.Server_Debug);
    }

    // ---------- HandleTraces ----------

    [Test]
    public void Traces_NonHostIsRejected()
    {
        ClearSentPackets();

        InvokeTraces(alice, targetPlayerId: bob.id);

        SentPacketsAcrossAllPlayers().Should().NotContain(Packets.Server_Traces);
    }

    [Test]
    public void Traces_HostForwardsToTargetPlayer()
    {
        ClearSentPackets();

        InvokeTraces(host, targetPlayerId: bob.id);

        SentPacketsOf(bob).Should().Contain(Packets.Server_Traces);
    }

    private static void InvokeTraces(ServerPlayer sender, int targetPlayerId) =>
        sender.conn.GetState<ServerPlayingState>()!
            .HandleTraces(new ClientTracesPacket
            {
                playerId = targetPlayerId,
                rawTraces = Array.Empty<byte>(),
                rawJittedMethods = Array.Empty<byte>(),
            });

    // ---------- HandleWorldDataUpload ----------

    [Test]
    public void WorldDataUpload_RejectsTooManyMaps()
    {
        var reader = BuildWorldUpload(maps: 65);

        Action act = () => host.conn.GetState<ServerPlayingState>()!.HandleWorldDataUpload(reader);

        act.Should().Throw<ReaderException>().WithMessage("*Too many maps*");
    }

    [Test]
    public void WorldDataUpload_RejectsNegativeMapsCount()
    {
        var w = new ByteWriter();
        w.WriteInt32(-1);
        var reader = new ByteReader(w.ToArray());

        Action act = () => host.conn.GetState<ServerPlayingState>()!.HandleWorldDataUpload(reader);

        act.Should().Throw<ReaderException>().WithMessage("*Too many maps*");
    }

    [Test]
    public void WorldDataUpload_RejectsTooBigSavedGame()
    {
        var w = new ByteWriter();
        w.WriteInt32(0); // maps count
        w.WriteInt32(17 * 1024 * 1024); // savedGame length above 16MB cap
        var reader = new ByteReader(w.ToArray());

        Action act = () => host.conn.GetState<ServerPlayingState>()!.HandleWorldDataUpload(reader);

        act.Should().Throw<ReaderException>().WithMessage("*too long*");
    }

    [Test]
    public void WorldDataUpload_AcceptsValidPayload()
    {
        var savedGame = new byte[] { 1, 2, 3 };
        var sessionData = new byte[] { 4, 5 };
        var map0 = new byte[] { 9 };
        var map1 = new byte[] { 8, 7 };

        var w = new ByteWriter();
        w.WriteInt32(2);
        w.WriteInt32(0); w.WritePrefixedBytes(map0);
        w.WriteInt32(1); w.WritePrefixedBytes(map1);
        w.WritePrefixedBytes(savedGame);
        w.WritePrefixedBytes(sessionData);
        var reader = new ByteReader(w.ToArray());

        host.conn.GetState<ServerPlayingState>()!.HandleWorldDataUpload(reader);

        server.worldData.savedGame.Should().Equal(savedGame);
        server.worldData.sessionData.Should().Equal(sessionData);
        server.worldData.mapData.Should().HaveCount(2);
        server.worldData.mapData[0].Should().Equal(map0);
        server.worldData.mapData[1].Should().Equal(map1);
    }

    [Test]
    public void WorldDataUpload_RejectsNonHost()
    {
        var beforeSavedGame = server.worldData.savedGame;
        var reader = BuildWorldUpload(maps: 0, savedGame: new byte[] { 42 });

        alice.conn.GetState<ServerPlayingState>()!.HandleWorldDataUpload(reader);

        server.worldData.savedGame.Should().BeSameAs(beforeSavedGame);
    }

    [Test]
    public void WorldDataUpload_RejectsNegativeMapId()
    {
        // Negative ids are reserved sentinels and never name a real map; accepting them would let a
        // client poison worldData.mapData with keys that downstream lookups never expect.
        var w = new ByteWriter();
        w.WriteInt32(1);
        w.WriteInt32(-5);
        w.WritePrefixedBytes(new byte[] { 1 });
        w.WritePrefixedBytes(Array.Empty<byte>());
        w.WritePrefixedBytes(Array.Empty<byte>());
        var reader = new ByteReader(w.ToArray());

        Action act = () => host.conn.GetState<ServerPlayingState>()!.HandleWorldDataUpload(reader);

        act.Should().Throw<ReaderException>().WithMessage("*Negative mapId*");
    }

    [Test]
    public void WorldDataUpload_RejectsDuplicateMapId()
    {
        // Duplicate ids would silently overwrite the first map's bytes; reject at parse time.
        var w = new ByteWriter();
        w.WriteInt32(2);
        w.WriteInt32(3); w.WritePrefixedBytes(new byte[] { 1 });
        w.WriteInt32(3); w.WritePrefixedBytes(new byte[] { 2 });
        w.WritePrefixedBytes(Array.Empty<byte>());
        w.WritePrefixedBytes(Array.Empty<byte>());
        var reader = new ByteReader(w.ToArray());

        Action act = () => host.conn.GetState<ServerPlayingState>()!.HandleWorldDataUpload(reader);

        act.Should().Throw<ReaderException>().WithMessage("*Duplicate mapId*");
    }

    // ---------- HandleChat ----------

    [Test]
    public void Chat_DropsMessageOverLengthCap()
    {
        // Length cap drops the message — no chat broadcast, no disconnect. AfterTrim is what gets
        // measured: surrounding whitespace can't be used to bypass the cap.
        ClearSentPackets();

        var tooLong = new string('a', ServerPlayingState.MaxChatMsgLength + 1);
        alice.conn.GetState<ServerPlayingState>()!.HandleChat(new ClientChatPacket { msg = tooLong });

        SentPacketsAcrossAllPlayers().Should().NotContain(Packets.Server_Chat);
    }

    [Test]
    public void Chat_AllowsMessageAtExactCap()
    {
        ClearSentPackets();

        var atCap = new string('a', ServerPlayingState.MaxChatMsgLength);
        alice.conn.GetState<ServerPlayingState>()!.HandleChat(new ClientChatPacket { msg = atCap });

        SentPacketsAcrossAllPlayers().Should().Contain(Packets.Server_Chat);
    }

    // ---------- HandleFrameTime ----------

    [Test]
    public void FrameTime_ClampsNaN()
    {
        // NaN must never reach Player.frameTime; the downstream maxFrameTime scan would propagate it.
        alice.conn.GetState<ServerPlayingState>()!
            .HandleFrameTime(new ClientFrameTimePacket(float.NaN));

        alice.frameTime.Should().Be(MultiplayerServer.StandardTimePerTick);
    }

    [Test]
    public void FrameTime_ClampsPositiveInfinity()
    {
        alice.conn.GetState<ServerPlayingState>()!
            .HandleFrameTime(new ClientFrameTimePacket(float.PositiveInfinity));

        alice.frameTime.Should().Be(MultiplayerServer.StandardTimePerTick * 4f);
    }

    [Test]
    public void FrameTime_ClampsHugeFiniteValue()
    {
        alice.conn.GetState<ServerPlayingState>()!
            .HandleFrameTime(new ClientFrameTimePacket(1e9f));

        alice.frameTime.Should().Be(MultiplayerServer.StandardTimePerTick * 4f);
    }

    [Test]
    public void FrameTime_ClampsBelowMin()
    {
        alice.conn.GetState<ServerPlayingState>()!
            .HandleFrameTime(new ClientFrameTimePacket(-1f));

        alice.frameTime.Should().Be(MultiplayerServer.StandardTimePerTick);
    }

    [Test]
    public void FrameTime_PassesValidValueThrough()
    {
        var ok = MultiplayerServer.StandardTimePerTick * 1.5f;
        alice.conn.GetState<ServerPlayingState>()!
            .HandleFrameTime(new ClientFrameTimePacket(ok));

        alice.frameTime.Should().Be(ok);
    }

    // ---------- Per-class rate limits ----------

    [Test]
    public void RateLimit_PingDropsBurstWithinInterval()
    {
        // Two pings back-to-back at the same NetTimer must collapse to a single broadcast — the
        // second one returns early before SendToPlaying.
        ClearSentPackets();
        var state = alice.conn.GetState<ServerPlayingState>()!;

        state.HandlePing(new ClientPingLocPacket());
        state.HandlePing(new ClientPingLocPacket());

        var pings = SentPacketsAcrossAllPlayers().Count(p => p == Packets.Server_PingLocation);
        pings.Should().Be(server.playerManager.Players.Count(),
            "first ping fans out to all playing players; second is rate-limited");
    }

    [Test]
    public void RateLimit_FreezeDropsRapidToggle()
    {
        // Freeze flips state — the first call commits, the rapid follow-up must NOT toggle back.
        var state = alice.conn.GetState<ServerPlayingState>()!;
        alice.frozen = false;

        state.HandleFreeze(new ClientFreezePacket(true));
        alice.frozen.Should().BeTrue();

        state.HandleFreeze(new ClientFreezePacket(false));
        alice.frozen.Should().BeTrue("rapid follow-up is rate-limited; the unfreeze must not land");
    }

    [Test]
    public void RateLimit_SelectedDropsBurstWithinInterval()
    {
        ClearSentPackets();
        var state = alice.conn.GetState<ServerPlayingState>()!;
        var packet = new ClientSelectedPacket
        {
            newlySelectedIds = Array.Empty<int>(),
            unselectedIds = Array.Empty<int>(),
        };

        state.HandleSelected(packet);
        state.HandleSelected(packet);

        var fanouts = SentPacketsAcrossAllPlayers().Count(p => p == Packets.Server_Selected);
        fanouts.Should().Be(server.playerManager.Players.Count() - 1,
            "first selected fans out to playing players (excluding sender); second is rate-limited");
    }

    private static ByteReader BuildWorldUpload(int maps, byte[]? savedGame = null, byte[]? sessionData = null)
    {
        var w = new ByteWriter();
        w.WriteInt32(maps);
        for (int i = 0; i < maps; i++)
        {
            w.WriteInt32(i);
            w.WritePrefixedBytes(Array.Empty<byte>());
        }
        w.WritePrefixedBytes(savedGame ?? Array.Empty<byte>());
        w.WritePrefixedBytes(sessionData ?? Array.Empty<byte>());
        return new ByteReader(w.ToArray());
    }

    // ---------- helpers ----------

    private void BuildServer(bool debugMode, DevModeScope scope = DevModeScope.HostOnly)
    {
        server = MultiplayerServer.instance = new MultiplayerServer(new ServerSettings
        {
            gameName = "Test",
            direct = false,
            lan = false,
            debugMode = debugMode,
            devModeScope = scope
        })
        {
            hostUsername = "host"
        };
        server.worldData.savedGame = Array.Empty<byte>();

        host = JoinPlayingPlayer("host");
        alice = JoinPlayingPlayer("alice");
        bob = JoinPlayingPlayer("bob");
    }

    private static ServerPlayer JoinPlayingPlayer(string username)
    {
        var conn = new RecordingConnection(username);
        var player = MultiplayerServer.instance!.playerManager.OnConnected(conn);
        player.FactionId = 100 + player.id; // give a stable, unique faction
        conn.ChangeState(ConnectionStateEnum.ServerPlaying);
        return player;
    }

    private static void InvokeSetFaction(ServerPlayer sender, int target, int factionId) =>
        sender.conn.GetState<ServerPlayingState>()!
            .HandleSetFaction(new ClientSetFactionPacket(target, factionId));

    private static void InvokeDebug(ServerPlayer sender) =>
        sender.conn.GetState<ServerPlayingState>()!
            .HandleDebug(default);

    private void SeedServerState()
    {
        server.gameTimer = 1000;
        server.startingTimer = 0;
        server.worldData.mapCmds.GetOrAddNew(0).Add([1, 2, 3]);
    }

    private void AssertServerStateUnchanged()
    {
        server.gameTimer.Should().Be(1000);
        server.worldData.mapCmds.Should().NotBeEmpty();
    }

    private void ClearSentPackets()
    {
        foreach (var p in server.playerManager.Players)
            ((RecordingConnection)p.conn).SentPackets.Clear();
    }

    private static List<Packets> SentPacketsOf(ServerPlayer player) =>
        ((RecordingConnection)player.conn).SentPackets;

    private IEnumerable<Packets> SentPacketsAcrossAllPlayers() =>
        server.playerManager.Players.SelectMany(p => ((RecordingConnection)p.conn).SentPackets);
}
