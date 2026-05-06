namespace Multiplayer.Common;

public enum Packets : byte
{
    // Client_ means the origin is the client
    // Server_ means the origin is the server

    // Special
    Client_Protocol, // Must be zeroth for future proofing
    Server_SteamAccept, // Packet for the special Steam state, must be first

    // Joining
    Client_Username,
    Client_InitData,
    Client_JoinData,
    Client_WorldRequest,

    // Playing
    Client_WorldReady,
    Client_Command,
    Client_WorldDataUpload,
    Client_Chat,
    Client_KeepAlive,
    Client_SteamRequest,
    Client_SyncInfo,
    Client_Cursor,
    Client_Desynced,
    Client_Freeze,
    Client_Debug,
    Client_Selected,
    Client_PingLocation,
    Client_Traces,
    Client_Autosaving,
    Client_RequestRejoin,
    Client_SetFaction,
    Client_FrameTime,

    // Joining
    Server_ProtocolOk,
    Server_InitDataRequest,
    Server_UsernameOk,
    Server_JoinData,

    // Loading
    Server_WorldDataStart,
    Server_WorldData,

    // Playing
    Server_Command,
    Server_MapResponse,
    Server_Notification,
    Server_TimeControl,
    Server_Chat,
    Server_PlayerList,
    Server_KeepAlive,
    Server_SyncInfo,
    Server_Cursor,
    Server_Freeze,
    Server_Debug,
    Server_Selected,
    Server_PingLocation,
    Server_Traces,
    Server_SetFaction,

    // All states (Joining, Loading, Playing)
    Server_Disconnect,

    // Bootstrap
    Client_BootstrapSettings,
    Client_BootstrapSave,
    Server_Bootstrap,

    // New entries must go at the end (before Count) to keep the on-wire ids of existing packets stable.
    Client_MapLoaded,

    // Section 8: paired desync save. Desynced client requests the host's most recent worldData
    // snapshot so the report folder contains both the local AND the host's reference state for
    // post-mortem diff. Snapshot is the last join-point upload, not a fresh save — see
    // Server_HostSaveTransfer for the limitation note. Fragmented because the gzipped save can
    // exceed the per-packet limit.
    Client_RequestHostSave,
    Server_HostSaveTransfer,

    Count,
    Max = 63 // max packet id
}
