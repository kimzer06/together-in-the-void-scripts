using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections.Generic;

public class GamePlayerSpawnerSameScene : NetworkBehaviour
{
    [Header("Prefabs for slot 0 / slot 1 (must have NetworkObject)")]
    [SerializeField] private GameObject prefabSlot0;  // ตัวละคร A
    [SerializeField] private GameObject prefabSlot1;  // ตัวละคร B

    [Header("Spawn Points for slot 0 / slot 1 (scene objects)")]
    [SerializeField] private Transform spawn0;
    [SerializeField] private Transform spawn1;

    [Header("Restrictions")]
    [Tooltip("ถ้าติ๊ก: จะสปอนเฉพาะซีนที่ชื่อระบุด้านล่าง; ถ้าไม่ติ๊ก: สปอนได้ทุกซีน")]
    [SerializeField] private bool restrictToScene = false;
    [SerializeField] private string gameplaySceneName = "Level01";

    // กันสปอนซ้ำในซีนเดียวกัน
    private readonly HashSet<ulong> _spawnedThisScene = new();

    private bool IsAllowedScene(string sceneName)
    {
        if (!restrictToScene) return true;
        return sceneName == gameplaySceneName;
    }

    private void OnEnable()
    {
        if (NetworkManager.Singleton == null) return;

        NetworkManager.Singleton.SceneManager.OnLoadComplete += OnLoadCompletePerClient;
        NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
        NetworkManager.Singleton.OnServerStarted += OnServerStarted;
    }

    private void OnDisable()
    {
        if (NetworkManager.Singleton == null) return;

        NetworkManager.Singleton.SceneManager.OnLoadComplete -= OnLoadCompletePerClient;
        NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
        NetworkManager.Singleton.OnServerStarted -= OnServerStarted;
    }

    private void Awake()
    {
        // กัน auto-spawn ของ NGO
        if (NetworkManager.Singleton != null &&
            NetworkManager.Singleton.NetworkConfig.PlayerPrefab != null)
        {
            Debug.LogWarning("[Spawner] NetworkConfig.PlayerPrefab ควรเป็น None เพื่อใช้ custom spawn");
        }
    }

    private void ClearPerScene()
    {
        _spawnedThisScene.Clear();
    }

    // กรณี Host เริ่มเกมในซีนที่จะเล่นอยู่แล้ว (ไม่มีโหลดซีน)
    private void OnServerStarted()
    {
        if (!IsServer) return;

        var sceneName = SceneManager.GetActiveScene().name;
        if (!IsAllowedScene(sceneName)) return;

        ClearPerScene();
        TrySpawnForAll("OnServerStarted");
    }

    // ยิงเมื่อใครสักคนโหลดซีนเสร็จ
    private void OnLoadCompletePerClient(ulong clientId, string sceneName, LoadSceneMode mode)
    {
        if (!IsServer) return;
        if (!IsAllowedScene(sceneName)) return;

        // เข้าซีนใหม่ → รีเซ็ตสถานะ แล้วสปอนให้ทุกคนที่ยังไม่มี
        ClearPerScene();
        TrySpawnForAll("OnLoadComplete");
    }

    // ยิงเมื่อ client ต่อเข้ามาหลังเกมเริ่ม
    private void OnClientConnected(ulong clientId)
    {
        if (!IsServer) return;

        var sceneName = SceneManager.GetActiveScene().name;
        if (!IsAllowedScene(sceneName)) return;

        TrySpawnForClient(clientId, "OnClientConnected");
    }

    private void TrySpawnForAll(string reason)
    {
        foreach (var c in NetworkManager.Singleton.ConnectedClientsList)
            TrySpawnForClient(c.ClientId, reason);
    }

    private void TrySpawnForClient(ulong clientId, string reason)
    {
        // กันซ้ำในซีนเดียวกัน
        if (_spawnedThisScene.Contains(clientId))
            return;

        // ถ้ามี PlayerObject อยู่แล้ว ไม่สปอนซ้ำ
        if (NetworkManager.Singleton.ConnectedClients.TryGetValue(clientId, out var conn) &&
            conn.PlayerObject != null && conn.PlayerObject.IsSpawned)
        {
            _spawnedThisScene.Add(clientId);
            return;
        }

        var S = LobbySelectionState.I;
        if (!S)
        {
            Debug.LogError("[Spawner] LobbySelectionState missing!");
            return;
        }

        int slot = (clientId == NetworkManager.ServerClientId) ? S.hostSlot.Value : S.clientSlot.Value;
        if (slot == 255)
        {
            Debug.LogError($"[Spawner] Client {clientId} has no slot selected!");
            return;
        }

        GameObject prefab = (slot == 0) ? prefabSlot0 : prefabSlot1;
        Transform spawnPoint = (slot == 0) ? spawn0 : spawn1;

        if (!prefab || !spawnPoint)
        {
            Debug.LogError($"[Spawner] Missing prefab or spawn point for slot {slot}");
            return;
        }

        // สร้างแล้วตั้งตำแหน่งก่อนสปอน (กันดีด 0,0,0)
        var go = Instantiate(prefab);
        go.transform.SetPositionAndRotation(spawnPoint.position, spawnPoint.rotation);

        var netObj = go.GetComponent<NetworkObject>();
        if (!netObj)
        {
            Debug.LogError($"[Spawner] Prefab {prefab.name} missing NetworkObject!");
            Destroy(go);
            return;
        }

        netObj.SpawnAsPlayerObject(clientId);
        _spawnedThisScene.Add(clientId);

        Debug.Log($"[Spawner] Spawned {prefab.name} for client {clientId} on slot {slot} ({reason})");
    }
}
