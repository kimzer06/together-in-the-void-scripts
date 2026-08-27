using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections.Generic;

public class GamePlayerRespawnPerScene : NetworkBehaviour
{
    [Header("Prefabs for slot 0 / slot 1 (must have NetworkObject)")]
    [SerializeField] private GameObject prefabSlot0;
    [SerializeField] private GameObject prefabSlot1;

    [Header("Spawn Points")]
    [SerializeField] private Transform spawn0;
    [SerializeField] private Transform spawn1;

    [Header("Scene Restriction")]
    [SerializeField] private bool restrictToScene = true;
    [SerializeField] private string gameplaySceneName = "Level02";

    private bool IsAllowedScene(string sceneName)
        => !restrictToScene || sceneName == gameplaySceneName;

    // กันสปอนซ้ำ “ต่อซีน”
    private readonly HashSet<ulong> _spawnedThisScene = new();
    private int _lastSceneBuildIndex = -1;

    private void OnEnable()
    {
        if (!NetworkManager.Singleton) return;
        NetworkManager.Singleton.OnServerStarted += OnServerStarted;
        NetworkManager.Singleton.SceneManager.OnLoadComplete += OnLoadCompletePerClient;
        NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
    }

    private void OnDisable()
    {
        if (!NetworkManager.Singleton) return;
        NetworkManager.Singleton.OnServerStarted -= OnServerStarted;
        NetworkManager.Singleton.SceneManager.OnLoadComplete -= OnLoadCompletePerClient;
        NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
    }

    private void ResetPerSceneIfNeeded()
    {
        var si = SceneManager.GetActiveScene().buildIndex;
        if (si != _lastSceneBuildIndex)
        {
            _spawnedThisScene.Clear();
            _lastSceneBuildIndex = si;
        }
    }

    private void OnServerStarted()
    {
        if (!IsServer) return;
        ResetPerSceneIfNeeded();

        var sceneName = SceneManager.GetActiveScene().name;
        if (!IsAllowedScene(sceneName)) return;

        // ใช้ RespawnOne ทีละคนแทน RespawnAll ก็ได้
        foreach (var c in NetworkManager.Singleton.ConnectedClientsList)
            RespawnOne(c.ClientId, "OnServerStarted");
    }

    // ยิง 1 ครั้งต่อ 1 client ที่โหลดเสร็จ → ต้อง handle เฉพาะคนนั้นเท่านั้น!
    private void OnLoadCompletePerClient(ulong clientId, string sceneName, LoadSceneMode mode)
    {
        if (!IsServer) return;
        if (!IsAllowedScene(sceneName)) return;

        ResetPerSceneIfNeeded();
        RespawnOne(clientId, "OnLoadComplete");
    }

    private void OnClientConnected(ulong clientId)
    {
        if (!IsServer) return;
        ResetPerSceneIfNeeded();

        var sceneName = SceneManager.GetActiveScene().name;
        if (!IsAllowedScene(sceneName)) return;

        RespawnOne(clientId, "OnClientConnected");
    }

    private void RespawnOne(ulong clientId, string reason)
    {
        // กันสปอนซ้ำในซีนเดียวกัน
        if (_spawnedThisScene.Contains(clientId)) return;

        var S = LobbySelectionState.I;
        if (!S) { Debug.LogError("[Respawn] Missing LobbySelectionState"); return; }

        int slot = (clientId == NetworkManager.ServerClientId) ? S.hostSlot.Value : S.clientSlot.Value;
        if (slot == 255) { Debug.LogError($"[Respawn] Client {clientId} has no slot"); return; }

        var prefab  = (slot == 0) ? prefabSlot0 : prefabSlot1;
        var spawnPt = (slot == 0) ? spawn0       : spawn1;
        if (!prefab || !spawnPt) { Debug.LogError($"[Respawn] Missing prefab or spawn for slot {slot}"); return; }

        // ถ้ามี PlayerObject เก่าอยู่ → Despawn ก่อน
        if (NetworkManager.Singleton.ConnectedClients.TryGetValue(clientId, out var conn) &&
            conn.PlayerObject != null && conn.PlayerObject.IsSpawned)
        {
            conn.PlayerObject.Despawn(true);
        }

        var go = Instantiate(prefab, spawnPt.position, spawnPt.rotation);
        var no = go.GetComponent<NetworkObject>();
        if (!no) { Debug.LogError($"[Respawn] Prefab {prefab.name} missing NetworkObject!"); Destroy(go); return; }

        no.SpawnAsPlayerObject(clientId);

        // ===== FIX: กัน ClientNetworkTransform race condition =====
        // ClientNetworkTransform (owner-authoritative) อาจส่งค่าตำแหน่ง default (0,0,0)
        // กลับมาก่อนที่จะรู้ตำแหน่ง spawn จริง → บังคับ sync ตำแหน่งให้ Client อีกครั้ง
        ForceSpawnPositionClientRpc(spawnPt.position, spawnPt.rotation,
            new ClientRpcParams
            {
                Send = new ClientRpcSendParams
                {
                    TargetClientIds = new ulong[] { clientId }
                }
            });

        _spawnedThisScene.Add(clientId);
        Debug.Log($"[Respawn] Spawned NEW {prefab.name} for {clientId} at {spawnPt.name} ({reason})");
    }

    /// <summary>
    /// บังคับให้ Client (Owner) เซ็ตตำแหน่ง spawn ที่ถูกต้อง
    /// กัน race condition ของ ClientNetworkTransform ที่อาจส่ง (0,0,0) กลับ Server
    /// </summary>
    [ClientRpc]
    private void ForceSpawnPositionClientRpc(Vector3 position, Quaternion rotation, ClientRpcParams rpcParams = default)
    {
        var playerObj = NetworkManager.Singleton.LocalClient?.PlayerObject;
        if (playerObj == null) return;

        playerObj.transform.SetPositionAndRotation(position, rotation);

        if (playerObj.TryGetComponent<Rigidbody>(out var rb))
        {
            rb.position = position;
            rb.rotation = rotation;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        Physics.SyncTransforms();
        Debug.Log($"[Respawn] Client forced spawn position to {position}");
    }

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        DrawSpawnGizmo(spawn0, Color.green,  "Slot 0");
        DrawSpawnGizmo(spawn1, Color.cyan,   "Slot 1");
    }

    private void DrawSpawnGizmo(Transform point, Color color, string label)
    {
        if (!point) return;

        Gizmos.color = color;

        // วงกลมจุดเกิด
        Gizmos.DrawWireSphere(point.position, 0.3f);

        // ลูกศรแสดงทิศหน้า
        Vector3 origin = point.position + Vector3.up * 0.5f;
        Vector3 forward = point.forward;
        float arrowLen = 1.5f;
        Vector3 tip = origin + forward * arrowLen;

        Gizmos.DrawLine(origin, tip);

        // หัวลูกศร
        Vector3 right = point.right;
        Vector3 up    = point.up;
        float   headSize = 0.25f;
        Gizmos.DrawLine(tip, tip - forward * headSize + right * headSize);
        Gizmos.DrawLine(tip, tip - forward * headSize - right * headSize);
        Gizmos.DrawLine(tip, tip - forward * headSize + up    * headSize);
        Gizmos.DrawLine(tip, tip - forward * headSize - up    * headSize);

        // Label
        UnityEditor.Handles.color = color;
        UnityEditor.Handles.Label(origin + Vector3.up * 0.3f, label);
    }
#endif
}
