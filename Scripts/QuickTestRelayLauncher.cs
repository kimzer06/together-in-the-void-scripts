using System.Threading.Tasks;
using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Core;
using Unity.Services.Authentication;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using System.Linq;
using UnityEngine.SceneManagement;
using UnityEngine.InputSystem;

public class QuickTestRelayLauncher : MonoBehaviour
{
    [Header("Target Scene")]
    [Tooltip("ใส่ชื่อ Scene ที่ต้องการโหลดหลังจากเชื่อมต่อสำเร็จ")]
    [SerializeField] private string targetSceneName = "Gameplay";

    [Header("Character Selection (Auto)")]
    [Tooltip("ติ๊กเพื่อให้ Host เล่นเป็น Player B แทน")]
    public bool hostIsPlayerB = false;

    [Header("Testing Mode")]
    [Tooltip("ทดสอบเข้าผ่าน Loading Scene (ปกติปิดไว้เพื่อความรวดเร็ว)")]
    public bool useLoadingScreen = false;

    [Tooltip("Host จะใช้ตัวละครหมายเลขนี้")]
    [SerializeField] private byte hostCharacterIndex = 0;
    [Tooltip("Client จะใช้ตัวละครหมายเลขนี้")]
    [SerializeField] private byte clientCharacterIndex = 1;

    [Header("Network Settings")]
    [SerializeField] private UnityTransport transport;
    [SerializeField] private int maxConnections = 3;
    [SerializeField] private bool useDtls = false;

    [Header("Debug UI (Optional)")]
    [Tooltip("แสดงปุ่ม Host/Join ใน Game View")]
    [SerializeField] private bool showDebugUI = true;
    
    [Header("Developer Console")]
    [Tooltip("เปิดให้พิมพ์คำสั่งผ่าน Developer Console (~ key)")]
    [SerializeField] private bool enableConsoleInput = true;

    private bool servicesInitialized = false;
    private string pendingJoinCode = "";
    private bool isConnecting = false;
    
    // Console input
    private bool showConsole = false;
    private string consoleInput = "";
    private string consoleLog = "Quick Test Console - พิมพ์ 'host' หรือ 'join CODE'\n";

    private void Awake()
    {
        Cursor.visible = true;
        Cursor.lockState = CursorLockMode.None;

        if (NetworkManager.Singleton)
        {
            NetworkManager.Singleton.SceneManager.OnSceneEvent += OnSceneEvent;
        }

        Debug.Log("==============================================");
        Debug.Log("[QuickTest] Quick Test Relay Launcher Ready!");
        Debug.Log("[QuickTest] Commands:");
        Debug.Log("[QuickTest]   host        - สร้าง Host และแสดง Join Code");
        Debug.Log("[QuickTest]   join XXXXXX - Join ด้วย code");
        Debug.Log($"[QuickTest] Target Scene: {targetSceneName}");
        Debug.Log($"[QuickTest] Host Char: {hostCharacterIndex}, Client Char: {clientCharacterIndex}");
        Debug.Log("==============================================");
    }

    private void Update()
    {
        // Toggle console with ` or ~ key (using New Input System)
        if (enableConsoleInput && Keyboard.current != null && Keyboard.current.backquoteKey.wasPressedThisFrame)
        {
            showConsole = !showConsole;
        }
    }

    private void OnSceneEvent(SceneEvent e)
    {
        Debug.Log($"[QuickTest] Scene Event: {e.SceneEventType} - {e.SceneName}");
        
        // เมื่อโหลด scene สำเร็จ ล็อคเคอร์เซอร์ถ้าเป็น gameplay
        if (e.SceneEventType == SceneEventType.LoadComplete && e.SceneName == targetSceneName)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    #region Unity Services Initialization
    
    private async Task EnsureServicesAsync()
    {
        if (servicesInitialized) return;

        await UnityServices.InitializeAsync();
        if (!AuthenticationService.Instance.IsSignedIn)
            await AuthenticationService.Instance.SignInAnonymouslyAsync();

        if (!transport) transport = FindObjectOfType<UnityTransport>();
        if (!transport) Debug.LogError("[QuickTest] UnityTransport not found in scene.");

        servicesInitialized = true;
        AddLog("Unity Services initialized.");
    }
    
    #endregion

    #region Host Logic
    
    public async void StartHost()
    {
        if (isConnecting)
        {
            AddLog("Already connecting...");
            return;
        }

        isConnecting = true;
        AddLog("Starting Host...");

        try
        {
            await EnsureServicesAsync();

            var regions = await RelayService.Instance.ListRegionsAsync();
            var region = regions.FirstOrDefault(x => x.Id == "ap-southeast1") ?? regions[0];

            Allocation alloc = await RelayService.Instance.CreateAllocationAsync(maxConnections, region.Id);
            string code = (await RelayService.Instance.GetJoinCodeAsync(alloc.AllocationId)).ToUpperInvariant();

            // Cache join code
            LobbySelectionState.CachedJoinCode = code;

            transport.SetRelayServerData(
                alloc.RelayServer.IpV4,
                (ushort)alloc.RelayServer.Port,
                alloc.AllocationIdBytes,
                alloc.Key,
                alloc.ConnectionData,
                System.Array.Empty<byte>(),
                useDtls
            );

            NetworkManager.Singleton.OnServerStarted += OnServerStartedCallback;
            bool ok = NetworkManager.Singleton.StartHost();
            
            if (!ok)
            {
                AddLog("StartHost() failed!");
                isConnecting = false;
                return;
            }

            // Copy join code ไปยัง clipboard อัตโนมัติ
            GUIUtility.systemCopyBuffer = code;

            Debug.Log("==============================================");
            Debug.Log($"[QuickTest] HOST STARTED!");
            Debug.Log($"[QuickTest] JOIN CODE: {code}");
            Debug.Log($"[QuickTest] (Copied to clipboard!)");
            Debug.Log($"[QuickTest] บนเครื่อง Client พิมพ์: join {code}");
            Debug.Log("==============================================");
            
            AddLog($"Host started! Join Code: {code} (Copied!)");
        }
        catch (RelayServiceException e)
        {
            Debug.LogError($"[QuickTest] Relay Host error: {e}");
            AddLog($"Error: {e.Message}");
            isConnecting = false;
        }
    }

    private void OnServerStartedCallback()
    {
        NetworkManager.Singleton.OnServerStarted -= OnServerStartedCallback;
        
        // รอให้ Client เชื่อมต่อ
        NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnectedAsHost;
        AddLog("Waiting for client to connect...");
    }

    private void OnClientConnectedAsHost(ulong clientId)
    {
        // ถ้าเป็น Host เอง (clientId 0) ไม่ต้องทำอะไร
        if (clientId == NetworkManager.ServerClientId) return;

        AddLog($"Client {clientId} connected! Setting up characters...");
        Debug.Log($"[QuickTest] Client {clientId} connected!");

        // Unsubscribe เพื่อไม่ให้ทำซ้ำ
        NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnectedAsHost;

        // ตั้งค่า character selection
        SetupCharacterSelectionAndLoadScene();
    }

    #endregion

    #region Client Logic
    
    public async void JoinWithCode(string code)
    {
        if (isConnecting)
        {
            AddLog("Already connecting...");
            return;
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            AddLog("Join code is empty!");
            Debug.LogError("[QuickTest] Join code is empty.");
            return;
        }

        code = code.Trim().ToUpperInvariant();
        isConnecting = true;
        AddLog($"Joining with code: {code}...");

        try
        {
            await EnsureServicesAsync();

            LobbySelectionState.CachedJoinCode = code;

            JoinAllocation joinAlloc = await RelayService.Instance.JoinAllocationAsync(code);

            transport.SetRelayServerData(
                joinAlloc.RelayServer.IpV4,
                (ushort)joinAlloc.RelayServer.Port,
                joinAlloc.AllocationIdBytes,
                joinAlloc.Key,
                joinAlloc.ConnectionData,
                joinAlloc.HostConnectionData,
                useDtls
            );

            bool ok = NetworkManager.Singleton.StartClient();
            
            if (!ok)
            {
                AddLog("StartClient() failed!");
                isConnecting = false;
                return;
            }

            Debug.Log($"[QuickTest] Joining as Client. Waiting for host to load scene...");
            AddLog("Connected! Waiting for scene load...");
        }
        catch (RelayServiceException e)
        {
            Debug.LogError($"[QuickTest] Relay Client error: {e}");
            AddLog($"Error: {e.Message}");
            isConnecting = false;
        }
    }
    
    #endregion

    #region Character Selection & Scene Loading
    
    private void SetupCharacterSelectionAndLoadScene()
    {
        var state = LobbySelectionState.I;
        if (state == null)
        {
            Debug.LogError("[QuickTest] LobbySelectionState.I is null!");
            AddLog("Error: LobbySelectionState not found!");
            return;
        }

        byte finalHostIdx = hostIsPlayerB ? (byte)1 : hostCharacterIndex;
        byte finalClientIdx = hostIsPlayerB ? (byte)0 : clientCharacterIndex;

        // ตั้งค่า character slots
        state.hostSlot.Value = finalHostIdx;
        state.clientSlot.Value = finalClientIdx;
        state.hostReady.Value = true;
        state.clientReady.Value = true;
        state.gameStarted.Value = true;

        Debug.Log($"[QuickTest] Characters set - Host: {finalHostIdx}, Client: {finalClientIdx}");
        AddLog($"Characters: Host={finalHostIdx}, Client={finalClientIdx}");
        AddLog($"Loading scene: {targetSceneName}...");

        // โหลด target scene
        if (useLoadingScreen)
        {
            LoadingScreenManager.LoadSceneNetworked(targetSceneName);
        }
        else
        {
            NetworkManager.Singleton.SceneManager.LoadScene(targetSceneName, LoadSceneMode.Single);
        }
    }
    
    #endregion

    #region Console UI
    
    private void AddLog(string msg)
    {
        consoleLog += $"[{System.DateTime.Now:HH:mm:ss}] {msg}\n";
        Debug.Log($"[QuickTest] {msg}");
    }

    private void ProcessCommand(string cmd)
    {
        cmd = cmd.Trim().ToLower();
        
        if (cmd == "host")
        {
            StartHost();
        }
        else if (cmd.StartsWith("join "))
        {
            string code = cmd.Substring(5).Trim().ToUpperInvariant();
            JoinWithCode(code);
        }
        else if (cmd == "help")
        {
            AddLog("Commands: host, join CODE, help, clear");
        }
        else if (cmd == "clear")
        {
            consoleLog = "";
        }
        else
        {
            AddLog($"Unknown command: {cmd}");
        }
    }

    private void OnGUI()
    {
        // Debug UI Buttons
        if (showDebugUI && !isConnecting)
        {
            GUILayout.BeginArea(new Rect(10, 10, 300, 220));
            GUILayout.BeginVertical("box");
            
            GUILayout.Label($"Quick Test - Target: {targetSceneName}");
            
            hostIsPlayerB = GUILayout.Toggle(hostIsPlayerB, "Play as Player B (Host)");
            useLoadingScreen = GUILayout.Toggle(useLoadingScreen, "Use Loading Screen");
            GUILayout.Space(5);
            
            if (GUILayout.Button("HOST (สร้าง Join Code)", GUILayout.Height(40)))
            {
                StartHost();
            }
            
            GUILayout.Space(10);
            GUILayout.Label("Join Code:");
            pendingJoinCode = GUILayout.TextField(pendingJoinCode);
            
            if (GUILayout.Button("JOIN (เข้าร่วม)", GUILayout.Height(40)))
            {
                JoinWithCode(pendingJoinCode);
            }
            
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        // Console (toggle with ` key)
        if (enableConsoleInput && showConsole)
        {
            float consoleHeight = 200;
            GUILayout.BeginArea(new Rect(0, Screen.height - consoleHeight, Screen.width, consoleHeight));
            
            GUI.Box(new Rect(0, 0, Screen.width, consoleHeight), "");
            
            GUILayout.BeginVertical();
            
            // Log area
            GUILayout.BeginScrollView(Vector2.up * 10000);
            GUILayout.Label(consoleLog);
            GUILayout.EndScrollView();
            
            // Input
            GUILayout.BeginHorizontal();
            GUI.SetNextControlName("ConsoleInput");
            consoleInput = GUILayout.TextField(consoleInput, GUILayout.ExpandWidth(true));
            
            if (GUILayout.Button("Send", GUILayout.Width(60)) || 
                (Event.current.isKey && Event.current.keyCode == KeyCode.Return && GUI.GetNameOfFocusedControl() == "ConsoleInput"))
            {
                if (!string.IsNullOrEmpty(consoleInput))
                {
                    ProcessCommand(consoleInput);
                    consoleInput = "";
                }
            }
            GUILayout.EndHorizontal();
            
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }
    }
    
    #endregion
}
