using Unity.Netcode;
using UnityEngine;
using Unity.Collections;
using UnityEngine.SceneManagement; 

public class LobbySelectionState : NetworkBehaviour
{
    public static LobbySelectionState I { get; private set; }

    // ใช้โชว์ JoinCode ได้ทันที ไม่ต้องรอ NetworkVar
    public static string CachedJoinCode = "";

    [Header("Scene Config")]
    // ต้องมั่นใจว่าชื่อนี้ตรงกับซีนแรกใน Build Settings 
    [SerializeField] private string initialSceneName = "StartScene"; 

    public NetworkVariable<ulong> slot0Owner = new(ulong.MaxValue);
    public NetworkVariable<ulong> slot1Owner = new(ulong.MaxValue);

    public NetworkVariable<byte> hostSlot = new(255);
    public NetworkVariable<byte> clientSlot = new(255);

    public NetworkVariable<bool> hostReady = new(false);
    public NetworkVariable<bool> clientReady = new(false);

    public NetworkVariable<bool> gameStarted = new(false);

    // JoinCode sync: ทุกคนอ่านได้ เขียนได้เฉพาะ Host
    public NetworkVariable<FixedString32Bytes> joinCode =
        new("", NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    void Awake()
    {
        if (I && I != this) { Destroy(gameObject); return; }
        I = this;

        if (transform.parent != null)
        {
            Debug.LogError("[LobbySelectionState] ต้องเป็น Root GameObject เท่านั้น (อย่าเป็นลูกของใคร).");
        }
        DontDestroyOnLoad(gameObject);
    }

    private bool transportFailed;
    private bool isCleaningUp;
    private bool criticalHooked;

    // ───────── Hook critical callbacks เมื่อ network พร้อมจริงๆ ─────────
    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        HookCriticalCallbacks();
    }

    // Fallback: ถ้า OnNetworkSpawn ไม่ถูกเรียก (เช่น client ที่ยังไม่ sync)
    void Start()
    {
        HookCriticalCallbacks();
    }

    private void HookCriticalCallbacks()
    {
        if (criticalHooked) return;
        if (NetworkManager.Singleton == null) return;

        NetworkManager.Singleton.OnTransportFailure += OnTransportFailure;
        NetworkManager.Singleton.OnClientStopped += OnClientStopped;
        criticalHooked = true;
        Debug.Log("[LobbySelectionState] Critical callbacks hooked (OnTransportFailure + OnClientStopped).");
    }

    void OnEnable()
    {
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnServerStarted += OnServerStarted;
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnectOnServer;
        }
    }

    void OnDisable()
    {
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnServerStarted -= OnServerStarted;
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnectOnServer;
        }
    }

    void OnDestroy()
    {
        if (NetworkManager.Singleton != null && criticalHooked)
        {
            NetworkManager.Singleton.OnTransportFailure -= OnTransportFailure;
            NetworkManager.Singleton.OnClientStopped -= OnClientStopped;
            criticalHooked = false;
        }
    }
    
    // ───────── Client Disconnect: reset slot ฝั่ง server ─────────
    private void OnClientDisconnectOnServer(ulong clientId)
    {
        // ทำเฉพาะฝั่ง Server และไม่ใช่ Host เอง disconnect
        if (!NetworkManager.Singleton.IsServer) return;
        if (clientId == NetworkManager.ServerClientId) return;

        Debug.Log($"[LobbySelectionState] Client {clientId} disconnected → reset client slot & ready.");
        clientSlot.Value = 255;
        clientReady.Value = false;
    }

    // ───────── Transport Failure (Relay ตาย เพราะ Host ออก) ─────────
    private void OnTransportFailure()
    {
        Debug.Log("[LobbySelectionState] Transport failure detected (Relay allocation lost).");
        transportFailed = true;

        // แจ้งเตือนทันที
        ConnectionNotificationUI.ShowGlobal("Player A left...", new Color(1f, 0.35f, 0.35f));

        // ทำ cleanup ทั้งหมดเลย เพราะ OnClientStopped อาจไม่ถูกเรียก
        CleanupAndReturnToMenu();
    }

    // ───────── Host/Client Disconnect Handler ─────────
    private void OnClientStopped(bool wasHost)
    {
        // ถ้า transportFailed ทำ cleanup ไปแล้ว → ข้าม
        if (transportFailed) return;

        Debug.Log($"[LobbySelectionState] OnClientStopped (wasHost={wasHost}). Returning to: {initialSceneName}.");

        // แจ้งเตือนถ้าเราเป็น Client ที่โดน Host ตัด (กรณี disconnect ปกติ)
        if (!wasHost)
        {
            ConnectionNotificationUI.ShowGlobal("Player A left...", new Color(1f, 0.35f, 0.35f));
        }

        CleanupAndReturnToMenu();
    }

    // ───────── Public: Leave Room (Back Button) ─────────
    private string overriddenScene;

    public void LeaveRoom(string targetScene = null)
    {
        if (NetworkManager.Singleton == null) return;
        if (isCleaningUp) return;

        // ถ้าระบุซีนมา ให้ใช้ซีนนั้นแทน initialSceneName
        if (!string.IsNullOrEmpty(targetScene))
            overriddenScene = targetScene;

        // ทั้ง Host และ Client cleanup ตรงๆ ไม่ต้อง delay
        // (Client ฝั่งตรงข้ามจะได้ OnTransportFailure/OnClientStopped อัตโนมัติ
        //  ซึ่งจะแจ้งเตือนและกลับ StartScene เอง)
        CleanupAndReturnToMenu();
    }

    // ───────── Shared Cleanup ─────────
    private void CleanupAndReturnToMenu()
    {
        if (isCleaningUp) return;
        isCleaningUp = true;

        // ★ Cache reference ก่อน Shutdown เพราะ Singleton อาจ null หลัง Shutdown
        GameObject nmGO = (NetworkManager.Singleton != null) 
            ? NetworkManager.Singleton.gameObject 
            : null;

        // 1. Shutdown NetworkManager
        if (NetworkManager.Singleton != null &&
            (NetworkManager.Singleton.IsConnectedClient || NetworkManager.Singleton.IsListening))
        {
            NetworkManager.Singleton.Shutdown();
        }

        // 2. ทำลายตัวเอง (LobbySelectionState) — ใช้ DestroyImmediate เพื่อทำลายก่อน LoadScene
        if (I == this) 
        {
            I = null;
        }
        DestroyImmediate(gameObject); 

        // 3. ทำลาย NetworkManager Object — ใช้ DestroyImmediate เพื่อทำลายก่อน LoadScene
        if (nmGO)
        {
            Debug.Log("[LobbySelectionState] Forcing immediate destruction of NetworkManager GameObject.");
            DestroyImmediate(nmGO); 
        }

        // 4. โหลดซีนแบบ Local
        string scene = !string.IsNullOrEmpty(overriddenScene) ? overriddenScene : initialSceneName;
        SceneManager.LoadScene(scene);
    }


    private void OnServerStarted()
    {
        if (!NetworkManager.Singleton.IsServer) return;

        var no = GetComponent<NetworkObject>();
        if (no && !no.IsSpawned)
        {
            no.Spawn(true);
            Debug.Log("[LobbyState] Spawned NetworkObject.");
        }

        if (!string.IsNullOrEmpty(CachedJoinCode))
        {
            joinCode.Value = CachedJoinCode;
            Debug.Log($"[LobbyState] Applied CachedJoinCode to NetworkVar: {joinCode.Value}");
        }
    }

    public bool IsTaken(int slot, out ulong owner)
    {
        owner = slot == 0 ? slot0Owner.Value : slot1Owner.Value;
        return owner != ulong.MaxValue;
    }

    public void ClearReadyOf(ulong cid)
    {
        if (cid == NetworkManager.ServerClientId) hostReady.Value = false;
        else clientReady.Value = false;
    }

    public void SetReady(ulong cid, bool v)
    {
        if (cid == NetworkManager.ServerClientId) hostReady.Value = v;
        else clientReady.Value = v;
    }

    public bool EveryoneReady()
    {
        bool hostPicked = hostSlot.Value != 255;
        bool clientPicked = clientSlot.Value != 255;
        bool ready = hostPicked && clientPicked && hostReady.Value && clientReady.Value;

        Debug.Log($"[LobbyState] EveryoneReady? hostPicked={hostPicked}, clientPicked={clientPicked}, hostReady={hostReady.Value}, clientReady={clientReady.Value} => {ready}");
        return ready;
    }
}