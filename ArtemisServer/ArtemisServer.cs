using ArtemisServer;
using ArtemisServer.BridgeServer;
using ArtemisServer.Map;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Networking.NetworkSystem;
using UnityEngine.SceneManagement;

namespace Artemis
{
    public class ArtemisServer
    {
        private static ArtemisServer instance;
        bool IsMapLoaded;
        LobbyGameInfo GameInfo;
        LobbyTeamInfo TeamInfo;
        public static String Address = "127.0.0.1";
        public static int Port = 6061;
        ArtemisServerComponent artemisServerComponent;

        public void Start()
        {
            if (instance != null)
            {
                Log.Error("ArtemisServer::Start reentry!");
                return;
            }

            instance = this;

            Log.Info("Starting Server...");
            UIFrontendLoadingScreen.Get().StartDisplayError("Starting Server...");
            //ConnectionConfig config = new ConnectionConfig();
            //config.ConnectTimeout = 999999999;
            //HostTopology hostTopology = new HostTopology(config, 16);
            //NetworkServer.Configure(hostTopology);
            NetworkServer.useWebSockets = true;
            NetworkServer.Listen(Port);

            // Regiser Network Handlers
            GameMessageManager.RegisterAllHandlers();

            Log.Info($"Streaming assets path {Application.streamingAssetsPath}");
            Log.Info($"Persistent data path {Application.persistentDataPath}");
            Log.Info($"Data path {Application.dataPath}");
            // Load map bundle
            AssetBundle MapsBundle = AssetBundle.LoadFromFile(Path.Combine(Application.dataPath, @"Bundles\scenes\maps.bundle"));
            foreach (String scene in AssetBundleManager.Get().GetScenesInBundle("maps"))
            {
                Log.Info($"Available scene: {scene}");
            }

            foreach (Scene sn in SceneManager.GetAllScenes()) {
                Log.Info(sn.name);
            }

            GameObject artemisServerObject = new GameObject("ArtemisServerComponent");
            artemisServerComponent = artemisServerObject.AddComponent<ArtemisServerComponent>();
            GameObject.DontDestroyOnLoad(artemisServerObject);

            WebsocketManager.Init();
            ClientGamePrefabInstantiatorFix();
        }

        private void ClientGamePrefabInstantiatorFix()
        {
            // to keep highlight utils for now
            GameObject highlightUtilsPrefab = null;
            ClientGamePrefabInstantiator prefabInstantiator = ClientGamePrefabInstantiator.Get();
            foreach (var prefab in prefabInstantiator.m_prefabs)
            {
                Log.Info($"client prefab: {prefab.name}");
                if (prefab.name == "HighlightUtilsSingleton")
                {
                    highlightUtilsPrefab = prefab;
                }
                else
                {
                    GameObject.Destroy(prefab);
                }
            }
            if (highlightUtilsPrefab != null)
            {
                prefabInstantiator.m_prefabs = new GameObject[] { highlightUtilsPrefab };
            }
            else
            {
                prefabInstantiator.m_prefabs = null;  // just in case
            }
        }

        public void Reset()
        {
            IsMapLoaded = false;
        }

        public static void StartGame()
        {
            SceneManager.sceneLoaded += instance.OnSceneLoaded;
            instance.LoadMap();
        }

        public void AddCharacterActor(LobbyPlayerInfo playerInfo)
        {
            CharacterResourceLink resourceLink = GameWideData.Get().GetCharacterResourceLink(playerInfo.CharacterType);

            Log.Info($"Add Character {resourceLink.GetDisplayName()} for player {playerInfo.Handle}");

            GameObject atsd = SpawnObject("ActorTeamSensitiveData_Friendly", false);
            GameObject character = GameObject.Instantiate(resourceLink.ActorDataPrefab);

            ActorData actorData = character.GetComponent<ActorData>();

            actorData.SetupAbilityMods(playerInfo.CharacterInfo.CharacterMods); //#

            actorData.PlayerIndex = playerInfo.PlayerId;
            PlayerData playerData = character.GetComponent<PlayerData>();
            playerData.PlayerIndex = playerInfo.PlayerId;
            
            actorData.SetTeam(playerInfo.TeamId);
            actorData.UpdateDisplayName(playerInfo.Handle);
            actorData.PlayerIndex = playerInfo.PlayerId;
            actorData.SetClientFriendlyTeamSensitiveData(atsd.GetComponent<ActorTeamSensitiveData>());
            NetworkServer.Spawn(atsd);
            NetworkServer.Spawn(character);
        }

        private void LoadMap()
        {
            string map = GameInfo.GameConfig.Map.ToString();
            UIFrontendLoadingScreen.Get().StartDisplayError("Loading " + map);
            SceneManager.LoadScene(map, LoadSceneMode.Single);
        }

        private GameObject SpawnObject(string name, bool network = true)
        {
            Log.Info($"Spawning {name}");
            GameObject prefab = artemisServerComponent.GetNetworkPrefabByName(name);

            if (prefab == null)
            {
                Log.Error($"Not found: {name}");
                return null;
            }

            Log.Info($"Prefab {name}");
            UnityUtils.DumpGameObject(prefab);

            GameObject obj = GameObject.Instantiate(prefab);
            Log.Info($"Instantiated {name}");
            if (network)
            {
                NetworkServer.Spawn(obj);
                Log.Info($"Network spawned {name}");
            }
            return obj;
        }

        public void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Log.Info($"Loaded scene map ({mode}): {scene.name}");
            UIFrontendLoadingScreen.Get().StartDisplayError($"{scene.name} loaded");

            // Disable VisualsLoader so we dont go to the enviroment scene
            if (VisualsLoader.Get() != null)
            {
                VisualsLoader.Get().enabled = false;  // Breaks client UI (at least) if not disabled
            }

            // Avoid creating characters two times because OnSceneLoaded() gets called two times because VisualsLoader changes the current scene...
            if (this.IsMapLoaded)
            {
                Log.Error("Exiting on scene loaded, already loaded");
                return;
            }
            if (scene.name != "VR_Practice")
            {
                Log.Error("Exiting on scene loaded, not VR_Practice");
                return;
            }
            IsMapLoaded = true;
            InitializeGame(scene);
        }

        private void InitializeGame(Scene scene)
        {
            GameManager.Get().SetTeamInfo(TeamInfo);
            GameManager.Get().SetGameInfo(GameInfo);

            SpawnObject("ApplicationSingletonsNetId");
            var gameSceneSingletons = SpawnObject("GameSceneSingletons");
            var cameraMan = gameSceneSingletons.GetComponent<CameraManager>();
            if (cameraMan != null)
            {
                GameObject.Destroy(cameraMan);
            }
            else
            {
                Log.Info("CameraManager is null");
            }
            var SharedEffectBarrierManager = SpawnObject("SharedEffectBarrierManager");
            var SharedActionBuffer = SpawnObject("SharedActionBuffer");
            SharedActionBuffer.GetComponent<SharedActionBuffer>().Networkm_actionPhase = ActionBufferPhase.Done;

            foreach (GameObject sceneObject in scene.GetRootGameObjects())
            {
                if (sceneObject.GetComponent<NetworkIdentity>() != null && !sceneObject.activeSelf)
                {
                    Log.Info($"Activating scene object '{sceneObject.name}'");
                    sceneObject.SetActive(true);
                    NetworkServer.Spawn(sceneObject);
                }
            }

            bool destroyVisualsLoader = false;
            if (destroyVisualsLoader)
            {
                GameObject visualsLoader = GameObject.Find("VisualsLoader");
                if (visualsLoader != null)
                {
                    Log.Info("Trying to destroy VisualsLoader");
                    GameObject.Destroy(visualsLoader);
                }
            }

            Log.Info("Board is " + Board.Get());

            List<LobbyPlayerInfo> playerInfoList = GameManager.Get().TeamInfo.TeamPlayerInfo;
            IsMapLoaded = true;
            for (int i = 0; i < playerInfoList.Count; i++)
            {
                LobbyPlayerInfo playerInfo = playerInfoList[i];
                AddCharacterActor(playerInfo);
            }

            // Show what objects are present in the current scene
            UnityUtils.DumpSceneObjects();
        }

        public static ArtemisServer Get() { return instance; }

        public void ClientLoaded(NetworkConnection connection, int playerIndex)
        {
            Player player = GameFlow.Get().GetPlayerFromConnectionId(connection.connectionId);
            foreach (ActorData playerActor in GameFlowData.Get().GetAllActorsForPlayer(playerIndex))
            {
                GameObject character = playerActor.gameObject;
                character.GetComponent<PlayerData>().m_player = player;  // PATCH internal -> public PlayerData::m_player
                Log.Info($"Registered player with account id {player.m_accountId} as player {playerIndex} ({character.name})");
                NetworkServer.AddPlayerForConnection(connection, character, 0);
            }
        }

        public void Launch()
        {
            foreach (NetworkIdentity networkIdentity in NetworkServer.objects.Values)
            {
                Log.Info($"Network idenity: '{networkIdentity.name}' [{networkIdentity.connectionToClient?.connectionId}] {networkIdentity.observers.Count} observers");
            }

            GameFlowData.Get().enabled = true;
            GameFlowData.Get().Networkm_gameState = GameState.StartingGame;
            GameFlowData.Get().Networkm_gameState = GameState.Deployment;
            GameFlowData.Get().Networkm_gameState = GameState.BothTeams_Decision;
            GameFlowData.Get().Networkm_willEnterTimebankMode = false;
            GameFlowData.Get().Networkm_timeRemainingInDecisionOverflow = 10;

            foreach (var actor in GameFlowData.Get().GetActors())
            {
                var turnSm = actor.gameObject.GetComponent<ActorTurnSM>();
                turnSm.CallRpcTurnMessage((int)TurnMessage.TURN_START, 0);
                //actor.MoveFromBoardSquare = actor.TeamSensitiveData_authority.MoveFromBoardSquare;
                //UpdatePlayerMovement(player);
            }
            //BarrierManager.Get().CallRpcUpdateBarriers();
        }

        public static void SetGameInfo(LobbyGameInfo gameInfo)
        {
            instance.GameInfo = gameInfo;
            Log.Info("Setting Game Info");
        }
        public static void SetTeamInfo(LobbyTeamInfo teamInfo)
        {
            instance.TeamInfo = teamInfo;
            Log.Info("Setting Team Info");
        }
    }
}
