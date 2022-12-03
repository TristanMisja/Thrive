﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Newtonsoft.Json;

/// <summary>
///   Manages online game sessions.
/// </summary>
public class NetworkManager : Node
{
    public const int DEFAULT_SERVER_ID = 1;

    private static NetworkManager? instance;

    private readonly Dictionary<int, NetPlayerInfo> playerList = new();

    private NetworkedMultiplayerENet? peer;
    private UPNP? upnp;

    private float networkTickTimer;
    private float elapsedGameTime;

    private NetworkManager()
    {
        instance = this;
    }

    [Signal]
    public delegate void UPNPCallResultReceived(UPNP.UPNPResult result, UPNPActionStep step);

    [Signal]
    public delegate void RegistrationToServerResultReceived(int peerId, RegistrationToServerResult result);

    [Signal]
    public delegate void ConnectionFailed(string reason);

    [Signal]
    public delegate void ServerStateUpdated();

    [Signal]
    public delegate void Kicked(string reason);

    [Signal]
    public delegate void ChatReceived(string message);

    [Signal]
    public delegate void PlayerJoined(int peerId);

    [Signal]
    public delegate void PlayerLeft(int peerId);

    [Signal]
    public delegate void ReadyForSessionReceived(int peerId, bool ready);

    [Signal]
    public delegate void PlayerStatusChanged(int peerId, NetPlayerStatus status);

    [Signal]
    public delegate void PlayerWorldReady(int peerId);

    public event EventHandler<float>? NetworkTick;

    public enum UPNPActionStep
    {
        Setup,
        PortMapping,
    }

    public enum RegistrationToServerResult
    {
        Success,
        ServerFull,
    }

    public static NetworkManager Instance => instance ?? throw new InstanceNotLoadedYetException();

    public ServerSettings? Settings { get; private set; }

    public bool Connected => peer?.GetConnectionStatus() == NetworkedMultiplayerPeer.ConnectionStatus.Connected;

    public float UpdateInterval { get; set; } = Constants.MULTIPLAYER_DEFAULT_UPDATE_INTERVAL_SECONDS;

    public float TimePassedConnecting { get; private set; }

    public int? PeerId { get; private set; }

    /// <summary>
    ///   All peers connected in the network (INCLUDING SELF).
    /// </summary>
    public IReadOnlyDictionary<int, NetPlayerInfo> PlayerList => playerList;

    public NetPlayerInfo? PlayerInfo => PeerId.HasValue ? GetPlayerInfo(PeerId.Value) : null;

    public bool IsServer { get; private set; }

    public bool IsDedicated => IsServer && !HasPlayer(1);

    public bool IsAuthoritative => PeerId.HasValue && IsServer && IsNetworkMaster();

    public bool IsClient => PeerId.HasValue && !IsServer && !IsNetworkMaster();

    [PuppetSync]
    public bool GameInSession { get; private set; }

    public float ElapsedGameTimeMinutes { get; private set; }

    public float ElapsedGameTimeSeconds { get; private set; }

    public string FormattedGameTime => $"{ElapsedGameTimeMinutes:00}:{ElapsedGameTimeSeconds:00}";

    public string FormattedGameTimeHumanized =>
        $"{ElapsedGameTimeMinutes} minutes {ElapsedGameTimeSeconds} seconds";

    public override void _Ready()
    {
        GetTree().Connect("network_peer_connected", this, nameof(OnPeerConnected));
        GetTree().Connect("network_peer_disconnected", this, nameof(OnPeerDisconnected));
        GetTree().Connect("server_disconnected", this, nameof(OnServerDisconnected));
        GetTree().Connect("connection_failed", this, nameof(OnConnectionFailed));

        Connect(nameof(UPNPCallResultReceived), this, nameof(OnUPNPCallResultReceived));

        ProcessPriority = 100;
    }

    public override void _Process(float delta)
    {
        if (peer == null)
            return;

        if (peer.GetConnectionStatus() == NetworkedMultiplayerPeer.ConnectionStatus.Connecting)
        {
            TimePassedConnecting += delta;
        }

        if (GameInSession)
        {
            if (IsAuthoritative)
            {
                elapsedGameTime += delta;

                var minutes = Mathf.FloorToInt(elapsedGameTime / 60);
                var seconds = Mathf.FloorToInt(elapsedGameTime % 60);

                RpcUnreliable(nameof(SyncGameTime), minutes, seconds);
            }

            networkTickTimer += delta;

            if (networkTickTimer > UpdateInterval)
            {
                NetworkTick?.Invoke(this, delta + networkTickTimer);
                networkTickTimer = 0;
            }
        }
    }

    /// <summary>
    ///   Creates a server without creating player for this peer.
    /// </summary>
    public Error CreateServer(ServerSettings settings)
    {
        TimePassedConnecting = 0;

        peer = new NetworkedMultiplayerENet();
        peer.SetBindIp(settings.Address);

        // TODO: enable DTLS for secure transport?

        var error = peer.CreateServer(settings.Port, Constants.MULTIPLAYER_DEFAULT_MAX_CLIENTS);
        if (error != Error.Ok)
        {
            PrintError("An error occurred while trying to create server: ", error);
            return error;
        }

        Settings = settings;

        GetTree().NetworkPeer = peer;

        IsServer = true;

        Print("Created server with the following settings ", settings);

        return error;
    }

    /// <summary>
    ///   Creates a server while creating player for this peer.
    /// </summary>
    public Error CreatePlayerHostedServer(string playerName, ServerSettings settings)
    {
        var result = CreateServer(settings);
        if (result != Error.Ok)
            return result;

        if (settings.UseUPNP)
        {
            // Automatic port mapping/forwarding with UPnP
            TaskExecutor.Instance.AddTask(new Task(() => SetupUPNP()));
        }

        OnConnectedToServer(playerName);

        Print("Server is player hosted");

        return result;
    }

    public Error ConnectToServer(string address, int port, string playerName)
    {
        TimePassedConnecting = 0;

        peer = new NetworkedMultiplayerENet();

        // TODO: enable DTLS for secure transport?

        GetTree().CheckAndConnect(
            "connected_to_server", this, nameof(OnConnectedToServer), new Godot.Collections.Array { playerName },
            (int)ConnectFlags.Oneshot);

        var result = peer.CreateClient(address, port);
        if (result != Error.Ok)
        {
            PrintError("An error occurred while trying to create client: ", result);
            return result;
        }

        GetTree().NetworkPeer = peer;

        IsServer = false;

        return result;
    }

    public void Disconnect()
    {
        Print("Disconnecting...");
        peer?.CloseConnection();

        if (upnp?.GetDeviceCount() > 0)
            upnp?.DeletePortMapping(Settings!.Port);

        playerList.Clear();
        GameInSession = false;
        SyncGameTime(0, 0);
        PeerId = null;
    }

    public bool HasPlayer(int peerId)
    {
        return playerList.ContainsKey(peerId);
    }

    public NetPlayerInfo? GetPlayerInfo(int peerId)
    {
        playerList.TryGetValue(peerId, out NetPlayerInfo result);
        return result;
    }

    public void SetPlayerInfoInts(int peerId, string key, int value)
    {
        if (IsClient)
            return;

        Rpc(nameof(SyncPlayerInfoInts), peerId, key, value);
    }

    public void SetPlayerInfoFloats(int peerId, string key, float value)
    {
        if (IsClient)
            return;

        Rpc(nameof(SyncPlayerInfoFloats), peerId, key, value);
    }

    public void StartGame()
    {
        if (IsClient && !GameInSession)
            return;

        if (!IsDedicated && PlayerInfo!.Status == NetPlayerStatus.Active)
            return;

        if (IsAuthoritative && !GameInSession)
            Rset(nameof(GameInSession), true);

        NotifyGameLoad();

        if (IsAuthoritative)
        {
            foreach (var player in playerList)
            {
                if (player.Key == DEFAULT_SERVER_ID)
                    continue;

                if (player.Value.Status == NetPlayerStatus.Lobby)
                    RpcId(player.Key, nameof(NotifyGameLoad));
            }
        }
    }

    public void EndGame()
    {
        if (!IsDedicated && PlayerInfo!.Status == NetPlayerStatus.Lobby)
            return;

        if (IsAuthoritative && GameInSession)
            Rset(nameof(GameInSession), false);

        NotifyGameExit();

        if (IsAuthoritative)
        {
            foreach (var player in playerList)
            {
                if (player.Key == DEFAULT_SERVER_ID)
                    continue;

                if (player.Value.Status == NetPlayerStatus.Active)
                    RpcId(player.Key, nameof(NotifyGameExit));
            }
        }
    }

    public void SetReadyForSessionStatus(bool ready)
    {
        if (IsAuthoritative)
            return;

        RpcId(1, nameof(NotifyReadyForSessionStatusChange), GetTree().GetNetworkUniqueId(), ready);
    }

    public void Kick(int id, string reason)
    {
        RpcId(id, nameof(NotifyKick), reason);
    }

    /// <summary>
    ///   Sends a chat message to all peers.
    /// </summary>
    public void BroadcastChat(string message, bool asSystem = false)
    {
        if (!Connected)
            return;

        Rpc(nameof(NotifyChatSend), message, asSystem);
    }

    /// <summary>
    ///   Checks if we are a network server and if so sends a chat message to all peers as system.
    /// </summary>
    public void SystemBroadcastChat(string message)
    {
        if (IsAuthoritative)
            BroadcastChat(message, true);
    }

    /// <summary>
    ///   Differentiates between print call from server or client. We do this so that they will stand out more
    ///   on the output log.
    /// </summary>
    public void Print(params object[] what)
    {
        var str = string.Concat(Array.ConvertAll(what, x => x?.ToString() ?? "null"));
        var serverOrHost = IsDedicated ? "[Server] " : "[Host] ";
        GD.Print(IsAuthoritative ? serverOrHost : "[Client] ", str);
    }

    public void PrintError(params object[] what)
    {
        var str = string.Concat(Array.ConvertAll(what, x => x?.ToString() ?? "null"));
        var serverOrHost = IsDedicated ? "[Server] " : "[Host] ";
        GD.PrintErr(IsAuthoritative ? serverOrHost : "[Client] ", str);
    }

    private void SetupUPNP()
    {
        upnp ??= new UPNP();

        var result = (UPNP.UPNPResult)upnp.Discover();

        if (result != UPNP.UPNPResult.Success)
            PrintError("UPnP devices discovery failed: ", result);

        EmitSignal(nameof(UPNPCallResultReceived), result, UPNPActionStep.Setup);
    }

    private void AddPortMapping(int port)
    {
        if (upnp == null)
            return;

        if (upnp.GetDeviceCount() <= 0)
            return;

        if (upnp.GetGateway()?.IsValidGateway() == true)
        {
            // TODO: not tested and not sure if this really works since I can't seem to get my router's UPnP to work?
            var pmResult = (UPNP.UPNPResult)upnp.AddPortMapping(port, 0, "ThriveGame");

            // TODO: error handling

            EmitSignal(nameof(UPNPCallResultReceived), pmResult, UPNPActionStep.PortMapping);
            return;
        }

        EmitSignal(nameof(UPNPCallResultReceived), UPNP.UPNPResult.Success, UPNPActionStep.PortMapping);
    }

    private void OnPeerConnected(int id)
    {
        // Will probaby be useful later.
    }

    private void OnPeerDisconnected(int id)
    {
        if (!HasPlayer(id))
            return;

        Print("User ", GetPlayerInfo(id)!.Name, " (", id, ") has disconnected");

        SystemBroadcastChat($"[b]{GetPlayerInfo(id)!.Name}[/b] has disconnected.");

        NotifyPlayerDisconnected(id);
    }

    private void OnConnectedToServer(string playerName)
    {
        PeerId = GetTree().GetNetworkUniqueId();

        var info = new NetPlayerInfo { Name = playerName };

        // TODO: some kind of authentication
        RpcId(1, nameof(NotifyPlayerConnected), PeerId.Value, JsonConvert.SerializeObject(info));

        TimePassedConnecting = 0;
    }

    private void OnServerDisconnected()
    {
        Print("Disconnected from server");

        playerList.Clear();
        GameInSession = false;
        SyncGameTime(0, 0);
        PeerId = null;
        EmitSignal(nameof(ServerStateUpdated));
    }

    private void OnConnectionFailed()
    {
        var reason = TranslationServer.Translate("BAD_CONNECTION");

        if (Mathf.RoundToInt(TimePassedConnecting) >= Constants.MULTIPLAYER_DEFAULT_TIMEOUT_LIMIT_SECONDS)
            reason = TranslationServer.Translate("TIMEOUT");

        EmitSignal(nameof(ConnectionFailed), reason);

        TimePassedConnecting = 0;
    }

    private void OnUPNPCallResultReceived(UPNP.UPNPResult result, UPNPActionStep step)
    {
        switch (step)
        {
            case UPNPActionStep.Setup:
            {
                if (result == UPNP.UPNPResult.Success)
                    TaskExecutor.Instance.AddTask(new Task(() => AddPortMapping(Settings!.Port)));

                break;
            }
        }
    }

    private void OnGameReady(object sender, EventArgs args)
    {
        Rpc(nameof(NotifyWorldReady), PeerId!.Value);
    }

    /// <summary>
    ///   How this works: a client send their info to the server (in this case when they're connected), the server then
    ///   registers the sender and gives them the current server state and tell all other players about their arrival.
    /// </summary>
    [RemoteSync]
    private void NotifyPlayerConnected(int id, string info)
    {
        var deserializedInfo = JsonConvert.DeserializeObject<NetPlayerInfo>(info);
        if (deserializedInfo == null)
            return;

        if (IsAuthoritative)
        {
            if (playerList.Count >= Settings!.MaxPlayers)
            {
                NotifyRegistrationToServerResult(id, RegistrationToServerResult.ServerFull);
                peer!.DisconnectPeer(id);
                return;
            }

            if (GameInSession)
                RsetId(id, nameof(GameInSession), true);

            if (!IsDedicated)
            {
                // Since we're a listen-server, send our own player info to the newly connected player
                RpcId(id, nameof(NotifyPlayerConnected), PeerId!.Value, JsonConvert.SerializeObject(PlayerInfo));
                RpcId(id, nameof(NotifyPlayerStatusChange), PeerId.Value, PlayerInfo!.Status);
            }

            foreach (var player in playerList)
            {
                if (player.Key == DEFAULT_SERVER_ID)
                    continue;

                // Forward newly connected player info to the other players
                RpcId(player.Key, nameof(NotifyPlayerConnected), id, info);

                // Forward other players info to the newly connected player
                RpcId(id, nameof(NotifyPlayerConnected), player.Key, JsonConvert.SerializeObject(player.Value));
            }

            if (id != DEFAULT_SERVER_ID)
            {
                // Return sender client's own info so they have themselves registered locally
                RpcId(id, nameof(NotifyPlayerConnected), id, info);

                // And finally give the client our server configurations
                RpcId(id, nameof(NotifyServerConfigs), JsonConvert.SerializeObject(Settings));
            }

            // TODO: might not be true...
            Print("User ", deserializedInfo.Name, " (", id, ") has connected");
            SystemBroadcastChat($"[b]{deserializedInfo.Name}[/b] has connected.");
        }

        if (HasPlayer(id))
            return;

        playerList.Add(id, deserializedInfo);
        EmitSignal(nameof(ServerStateUpdated));

        if (IsAuthoritative)
        {
            // Tell all peers (and ourselves if this is client hosted) that a new peer have
            // been successfully registered to the server
            NotifyRegistrationToServerResult(id, RegistrationToServerResult.Success);
        }
    }

    [Remote]
    private void NotifyPlayerDisconnected(int id)
    {
        if (!HasPlayer(id))
            return;

        playerList.Remove(id);
        EmitSignal(nameof(ServerStateUpdated));
    }

    [Remote]
    private void NotifyServerConfigs(string settings)
    {
        try
        {
            Settings = JsonConvert.DeserializeObject<ServerSettings>(settings);
        }
        catch (Exception e)
        {
            PrintError("Error occured while trying to read server configurations: ", e);
        }
    }

    [RemoteSync]
    private void NotifyRegistrationToServerResult(int peerId, RegistrationToServerResult result)
    {
        if (IsAuthoritative)
        {
            foreach (var player in playerList)
            {
                if (player.Key != DEFAULT_SERVER_ID)
                    RpcId(player.Key, nameof(NotifyRegistrationToServerResult), peerId, result);
            }
        }

        EmitSignal(nameof(RegistrationToServerResultReceived), peerId, result);
    }

    [RemoteSync]
    private void NotifyPlayerStatusChange(int id, NetPlayerStatus environment)
    {
        if (IsAuthoritative)
        {
            foreach (var player in playerList)
            {
                if (player.Key == DEFAULT_SERVER_ID)
                    continue;

                RpcId(player.Key, nameof(NotifyPlayerStatusChange), id, environment);
            }
        }

        var info = GetPlayerInfo(id);
        if (info != null)
        {
            info.Status = environment;
            EmitSignal(nameof(PlayerStatusChanged), id, environment);
        }
    }

    [Remote]
    private void NotifyKick(string reason)
    {
        if (GetTree().GetRpcSenderId() != DEFAULT_SERVER_ID)
        {
            PrintError("Kicking is only permissible from host/server");
            return;
        }

        Disconnect();
        EmitSignal(nameof(Kicked), reason);
    }

    [RemoteSync]
    private void NotifyReadyForSessionStatusChange(int peerId, bool ready)
    {
        if (IsAuthoritative)
        {
            foreach (var player in playerList)
            {
                if (player.Key == DEFAULT_SERVER_ID)
                    continue;

                RpcId(player.Key, nameof(NotifyReadyForSessionStatusChange), peerId, ready);
            }
        }

        var info = GetPlayerInfo(peerId)!;
        info.ReadyForSession = ready;

        EmitSignal(nameof(ReadyForSessionReceived), peerId, ready);
    }

    [Remote]
    private void NotifyGameLoad()
    {
        Rpc(nameof(NotifyWorldPreLoad), PeerId!.Value);

        TransitionManager.Instance.AddSequence(ScreenFade.FadeType.FadeOut, 0.4f, () =>
        {
            IStage stage = null!;

            switch (Settings?.SelectedGameMode)
            {
                case MultiplayerGameState.MicrobialArena:
                    var scene = SceneManager.Instance.LoadScene(MultiplayerGameState.MicrobialArena);
                    stage = (MicrobialArena)scene.Instance();
                    break;
            }

            stage.GameReady += OnGameReady;
            SceneManager.Instance.SwitchToScene(stage.GameStateRoot);
            Rpc(nameof(NotifyWorldPostLoad), PeerId!.Value);
        });
    }

    [Remote]
    private void NotifyGameExit()
    {
        Rpc(nameof(NotifyWorldPreExit), PeerId!.Value);

        TransitionManager.Instance.AddSequence(ScreenFade.FadeType.FadeOut, 0.3f, () =>
        {
            var menu = SceneManager.Instance.ReturnToMenu();
            menu.OpenMultiplayerMenu(MultiplayerGUI.Submenu.Lobby);
            Rpc(nameof(NotifyWorldPostExit), PeerId!.Value);
        });

        SyncGameTime(0, 0);
    }

    [RemoteSync]
    private void NotifyWorldPreLoad(int peerId)
    {
        var playerInfo = GetPlayerInfo(peerId);

        if (playerInfo == null)
            return;

        playerInfo.Status = NetPlayerStatus.Joining;
        EmitSignal(nameof(PlayerStatusChanged), peerId, playerInfo.Status);
    }

    [RemoteSync]
    private void NotifyWorldPostLoad(int peerId)
    {
        var playerInfo = GetPlayerInfo(peerId);

        if (playerInfo == null)
            return;

        EmitSignal(nameof(PlayerJoined), peerId);
        SystemBroadcastChat($"[b]{playerInfo.Name}[/b] has joined.");
    }

    [RemoteSync]
    private void NotifyWorldPreExit(int peerId)
    {
        var playerInfo = GetPlayerInfo(peerId);

        if (playerInfo == null)
            return;

        playerInfo.Status = NetPlayerStatus.Leaving;
        EmitSignal(nameof(PlayerStatusChanged), peerId, playerInfo.Status);
        EmitSignal(nameof(PlayerLeft), peerId);
    }

    [RemoteSync]
    private void NotifyWorldPostExit(int peerId)
    {
        var playerInfo = GetPlayerInfo(peerId);

        if (playerInfo == null)
            return;

        playerInfo.Status = NetPlayerStatus.Lobby;
        EmitSignal(nameof(PlayerStatusChanged), peerId, playerInfo.Status);
        EmitSignal(nameof(ReadyForSessionReceived), peerId, playerInfo.ReadyForSession);
        EmitSignal(nameof(ServerStateUpdated));

        SystemBroadcastChat($"[b]{playerInfo.Name}[/b] has left.");
    }

    [RemoteSync]
    private void NotifyWorldReady(int peerId)
    {
        var playerInfo = GetPlayerInfo(peerId);

        if (playerInfo == null)
            return;

        playerInfo.Status = NetPlayerStatus.Active;
        EmitSignal(nameof(PlayerStatusChanged), peerId, playerInfo.Status);
        EmitSignal(nameof(PlayerWorldReady), peer);
    }

    [RemoteSync]
    private void NotifyChatSend(string message, bool asSystem)
    {
        var senderId = GetTree().GetRpcSenderId();
        var senderState = GetPlayerInfo(senderId);

        var formatted = string.Empty;
        if (senderState == null || (senderId == DEFAULT_SERVER_ID && asSystem))
        {
            formatted = $"[color=#d8d8d8][system]: {message}[/color]";
        }
        else
        {
            formatted = $"[b]({senderState.GetStatusReadableShort()}) [{senderState.Name}]:[/b] {message}";
        }

        EmitSignal(nameof(ChatReceived), formatted);
    }

    [PuppetSync]
    private void SyncGameTime(float minutes, float seconds)
    {
        ElapsedGameTimeMinutes = minutes;
        ElapsedGameTimeSeconds = seconds;
    }

    [PuppetSync]
    private void SyncPlayerInfoInts(int peerId, string key, int value)
    {
        var sender = GetTree().GetRpcSenderId();
        if (sender != DEFAULT_SERVER_ID)
            return;

        var info = GetPlayerInfo(peerId);
        if (info == null)
            return;

        info.Ints[key] = value;
    }

    [PuppetSync]
    private void SyncPlayerInfoFloats(int peerId, string key, float value)
    {
        var sender = GetTree().GetRpcSenderId();
        if (sender != DEFAULT_SERVER_ID)
            return;

        var info = GetPlayerInfo(peerId);
        if (info == null)
            return;

        info.Floats[key] = value;
    }
}
