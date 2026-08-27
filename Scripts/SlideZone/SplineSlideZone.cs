using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Splines;
using Unity.Netcode;
using StarterAssets;
using Unity.Cinemachine;
using Unity.Mathematics;

/// <summary>
/// Spline-based Cooperative Slide Zone: ใช้ SplineContainer กำหนดเส้นทาง slide
/// รองรับทางโค้ง/เอียง/ขึ้นลงได้อิสระ
/// ต้องการผู้เล่น 2 คนเข้าโซนพร้อมกันก่อนเริ่ม slide
/// </summary>
[RequireComponent(typeof(Collider))]
public class SplineSlideZone : NetworkBehaviour, ISlideZone
{
    #region Configuration
    
    [Header("Spline Configuration")]
    [Tooltip("SplineContainer ที่กำหนดเส้นทาง slide")]
    public SplineContainer splineContainer;
    
    [Tooltip("จุดเริ่มต้นสำหรับ respawn เมื่อตายทั้ง 2 คน")]
    public Transform slideStartPoint;
    
    [Tooltip("จำนวนผู้เล่นที่ต้องการเพื่อเริ่ม slide")]
    public int requiredPlayers = 2;
    
    [Header("Slide Settings")]
    [Tooltip("ความเร็ว slide ตาม spline")]
    public float slideSpeed = 10f;
    
    [Tooltip("ความเร็วบังคับซ้าย/ขวา")]
    public float steerSpeed = 8f;
    
    [Tooltip("ระยะ offset สูงสุดซ้าย/ขวาจากแกน spline (เมตร)")]
    public float steerWidth = 3f;
    
    [Tooltip("กลับทิศทางการบังคับ?")]
    public bool invertSteer = false;
    
    [Header("Gravity on Slope")]
    [Tooltip("ตัวคูณ gravity ตาม slope (0 = ไม่ใช้, 5 = เร่ง/ชะลอมาก)")]
    public float gravityMultiplier = 5f;
    
    [Header("Spline / Mesh alignment")]
    [Tooltip("ความหนาของพื้นจาก SplineMeshGenerator (ต้องตรงกับค่าใน scene) — ผิวเดินอยู่ที่ spline centerline + up × (thickness/2)")]
    public float splineMeshThickness = 0.5f;
    [Tooltip("margin เพิ่มหลังครึ่งความหนา (เมตร) เพื่อไม่ให้ capsule ฝังผิว")]
    public float splineSurfaceLiftExtra = 0.05f;
    
    [Header("Checkpoints (จุดเซฟระหว่างทาง)")]
    [Tooltip("จุด respawn เรียงตามลำดับบน spline — เมื่อผู้เล่นคนใดผ่านจุดนี้ จะเปลี่ยนจุดเกิดใหม่เมื่อตายทั้ง 2 คน")]
    public Transform[] checkpointSpawnPoints;
    
    [Tooltip("ระยะห่างจากจุด checkpoint ที่ผู้เล่นต้องเข้าใกล้เพื่อ activate (เมตร)")]
    public float checkpointActivationRadius = 5f;

    [Header("Portal Reset Points (รีเซ็ตพอร์ทัลเมื่อผู้เล่นชน/เข้าใกล้จุด)")]
    [Tooltip("จุดที่เมื่อผู้เล่นเข้าใกล้/ชน จะสั่ง ResetPairServerRpc() ของ PortalPairManager_Net (ใช้ทำช่วงที่ไม่อยากให้พอร์ทัลค้าง)")]
    public Transform[] portalResetPoints;

    [Tooltip("ระยะห่างจากจุด portal reset ที่ผู้เล่นต้องเข้าใกล้เพื่อ trigger (เมตร)")]
    public float portalResetActivationRadius = 4f;
    
    [Header("Respawn Settings")]
    [Tooltip("เวลารอก่อน respawn เมื่อตายคนเดียว (วินาที)")]
    public float singleDeathRespawnDelay = 5f;
    
    [Tooltip("เวลารอก่อน respawn เมื่อตายทั้ง 2 คน (วินาที)")]
    public float bothDeathRespawnDelay = 2f;
    
    [Tooltip("ระยะเวลาอมตะหลัง respawn ที่เพื่อน (วินาที)")]
    public float teammateRespawnInvincibilityDuration = 3f;
    
    [Header("Cameras (Optional)")]
    [Tooltip("กล้อง Cinemachine สำหรับ slide view")]
    public GameObject slideVCam;
    
    [Header("UI (Optional)")]
    [Tooltip("UI แสดงเมื่อรอผู้เล่นอีกคน")]
    public GameObject waitingUI;
    
    #endregion
    
    #region Private Variables
    
    // === SERVER ONLY ===
    private HashSet<ulong> _serverPlayersInZone = new();
    private Dictionary<ulong, NetworkObject> _serverPlayerObjects = new();
    private Dictionary<ulong, PlayerDeath> _serverPlayerDeaths = new();
    private HashSet<ulong> _deadPlayers = new();
    /// <summary>ผู้เล่นที่เข้ารอบสไลด์นี้แล้ว (เซิร์ฟเวอร์) — ใช้แทนการเช็กแค่ overlap โซน เพราะตายแล้วมักโดน exit ก่อน RPC ตายตัวหลังถึง</summary>
    private HashSet<ulong> _slideSessionClients = new();
    private Dictionary<ulong, Coroutine> _respawnCoroutines = new();
    private Coroutine _respawnAllCoroutine = null;
    private bool _isProcessingRespawnAll = false;
    /// <summary>เซิร์ฟเวอร์: เคยเริ่มสไลด์ในรอบนี้แล้ว (แม้โซนว่าง _isSlideActive=false) — ใช้ยอมรับ obstacle death หลังถูกลากออกนอก trigger</summary>
    private bool _slideRunStartedOnServer = false;
    
    // === LOCAL ===
    private ThirdPersonController_Rigidbody _localPlayerController;
    private PlayerDeath _localPlayerDeath;
    private bool _localPlayerInZone = false;
    
    /// <summary>Slide zone ที่ local player อยู่ในขณะนี้ (null ถ้าไม่อยู่ใน slide zone)</summary>
    public static SplineSlideZone ActiveZoneForLocalPlayer { get; private set; }
    
    // === PLAYER-TO-PLAYER COLLISION IGNORE ===
    /// <summary>เก็บคู่ collider ที่ถูก ignore ไว้เพื่อ restore ตอนออกจากโซน (local client)</summary>
    private List<(Collider a, Collider b)> _ignoredCollisionPairs = new();
    
    // === NETWORK STATE ===
    private NetworkVariable<bool> _isSlideActive = new(false);
    private NetworkVariable<int> _playerCountInZone = new(0);
    private NetworkVariable<int> _currentCheckpointIndex = new(-1);
    
    private CinemachineCamera _vcamComponent;

    // === PORTAL RESET (SERVER ONLY) ===
    private HashSet<int> _activatedPortalResetPointIndices = new();
    
    #endregion
    
    #region Unity Lifecycle
    
    private void Awake()
    {
        var col = GetComponent<Collider>();
        if (col != null) col.isTrigger = true;
        
        if (slideVCam != null)
        {
            _vcamComponent = slideVCam.GetComponent<CinemachineCamera>();
        }
        
        if (waitingUI != null) waitingUI.SetActive(false);
    }
    
    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        _isSlideActive.OnValueChanged += OnSlideActiveChanged;
        _playerCountInZone.OnValueChanged += OnPlayerCountChanged;
    }
    
    public override void OnNetworkDespawn()
    {
        base.OnNetworkDespawn();
        _isSlideActive.OnValueChanged -= OnSlideActiveChanged;
        _playerCountInZone.OnValueChanged -= OnPlayerCountChanged;
        CancelAllRespawnCoroutines();
        // คืน collision ระหว่างผู้เล่นกรณี despawn ระหว่าง slide
        SetPlayerToPlayerCollisionIgnore(false);
    }
    
    private void Update()
    {
        if (!IsServer) return;
        // checkpoint ต้องอิงผู้เล่นใน session + ตำแหน่งจริง แม้ออกนอก collider โซนแล้ว (ประตูขยับ)
        if (CountAliveClientsInSlideSession() <= 0) return;
        ServerUpdateCheckpointProgress();
        ServerUpdatePortalResetPoints();
    }
    
    private void OnTriggerEnter(Collider other)
    {
        if (!IsSpawned) return;
        if (!other.TryGetComponent<NetworkObject>(out var netObj)) return;
        if (!netObj.IsOwner) return;
        
        var controller = other.GetComponent<ThirdPersonController_Rigidbody>();
        var death = other.GetComponent<PlayerDeath>();
        if (controller == null) return;
        
        _localPlayerController = controller;
        _localPlayerDeath = death;
        _localPlayerInZone = true;
        ActiveZoneForLocalPlayer = this;
        
        ulong clientId = netObj.OwnerClientId;
        
        // Setup VCam target
        if (_vcamComponent != null && controller.CinemachineCameraTarget != null)
        {
            _vcamComponent.Target.TrackingTarget = controller.CinemachineCameraTarget.transform;
        }
        
        // ตั้ง SplineContainer ให้ controller
        if (splineContainer != null)
        {
            controller._splineSlideContainer = splineContainer;
            // หา t ที่ใกล้ตำแหน่งผู้เล่นที่สุดบน spline
            float3 nearestPoint;
            float t;
            SplineUtility.GetNearestPoint(
                splineContainer.Spline,
                (float3)splineContainer.transform.InverseTransformPoint(controller.transform.position),
                out nearestPoint, out t
            );
            controller._splineSlideT = t;
            // ไม่รีเซ็ต steerOffset ให้กลับกลางตอนเข้าโซน
            // เพื่อให้เริ่มสไลด์จากตำแหน่งปัจจุบันได้เนียนขึ้น
            controller._splineCurrentSpeed = 0f;
        }
        
        Debug.Log($"[SplineSlideZone] Local player {clientId} entered zone.");
        PlayerEnteredZoneServerRpc();
    }
    
    private void OnTriggerExit(Collider other)
    {
        if (!IsSpawned) return;
        if (!other.TryGetComponent<NetworkObject>(out var netObj)) return;
        if (!netObj.IsOwner) return;
        
        var controller = other.GetComponent<ThirdPersonController_Rigidbody>();
        if (controller == null) return;

        // ตอนผู้เล่น "ตาย" ระบบ SlideZone ตั้ง IsHidden=true ซึ่งอาจทำให้ trigger exit ถูกเรียกจากการ disable collider
        // ถ้าเรายอมให้ไคลเอนต์เคลียร์ slide/กล้องตอนนี้ จะทำให้ state ฝั่ง Client เพี้ยน (โดยเฉพาะเรื่อง camera/TrackingTarget)
        var death = other.GetComponent<PlayerDeath>();
        // SlideZone kill: Client อาจเรียก trigger exit ตอนที่ colliders ถูก disable
        // ถ้าเราหยุด/เคลียร์ TrackingTarget ตอนนั้น state ฝั่ง Client จะเพี้ยน
        if (death != null && death.IsSlideZoneDeathInProgress)
            return;
        
        ulong clientId = netObj.OwnerClientId;
        
        // ออกจาก slide mode
        controller.SetSlideModeServerRpc(false, default);
        controller._splineSlideContainer = null;
        
        _localPlayerController = null;
        _localPlayerDeath = null;
        _localPlayerInZone = false;
        if (ActiveZoneForLocalPlayer == this)
            ActiveZoneForLocalPlayer = null;
        
        if (_vcamComponent != null)
        {
            _vcamComponent.Target.TrackingTarget = null;
        }
        
        Debug.Log($"[SplineSlideZone] Local player {clientId} exited zone.");
        PlayerExitedZoneServerRpc();
    }
    
    #endregion
    
    #region Server RPCs
    
    [ServerRpc(RequireOwnership = false)]
    private void PlayerEnteredZoneServerRpc(ServerRpcParams rpcParams = default)
    {
        if (!IsServer) return;
        ulong clientId = rpcParams.Receive.SenderClientId;
        
        if (!_serverPlayersInZone.Contains(clientId))
        {
            _serverPlayersInZone.Add(clientId);
            _slideSessionClients.Add(clientId);
            
            if (NetworkManager.Singleton.ConnectedClients.TryGetValue(clientId, out var client))
            {
                if (client.PlayerObject != null)
                {
                    _serverPlayerObjects[clientId] = client.PlayerObject;
                    var death = client.PlayerObject.GetComponent<PlayerDeath>();
                    if (death != null) _serverPlayerDeaths[clientId] = death;
                }
            }
            
            _playerCountInZone.Value = _serverPlayersInZone.Count;
            Debug.Log($"[SplineSlideZone][Server] Player {clientId} entered. Total: {_serverPlayersInZone.Count}/{requiredPlayers}");
            CheckAndStartSlide(clientId);
        }
    }
    
    [ServerRpc(RequireOwnership = false)]
    private void PlayerExitedZoneServerRpc(ServerRpcParams rpcParams = default)
    {
        if (!IsServer) return;
        ulong clientId = rpcParams.Receive.SenderClientId;
        
        if (_serverPlayersInZone.Contains(clientId))
        {
            _serverPlayersInZone.Remove(clientId);
            
            // ผู้เล่นที่ตายบนสไลด์มักได้ OnTriggerExit หลังปิด collider — ห้ามลบ cache/รายการตายตรงนี้
            // ไม่งั้น RespawnAllAtStart_Co จะอ่าน checkpoint หลัง ResetCheckpoints และไม่มี PlayerDeath ใน dict
            bool deadPendingRespawn = _deadPlayers.Contains(clientId);
            if (!deadPendingRespawn)
            {
                _slideSessionClients.Remove(clientId);
                _serverPlayerObjects.Remove(clientId);
                _serverPlayerDeaths.Remove(clientId);
                CancelRespawnCoroutine(clientId);
            }
            
            _playerCountInZone.Value = _serverPlayersInZone.Count;
            Debug.Log($"[SplineSlideZone][Server] Player {clientId} exited. Total: {_serverPlayersInZone.Count}/{requiredPlayers} (deadPendingRespawn={deadPendingRespawn})");
            
            if (_serverPlayersInZone.Count == 0)
            {
                _isSlideActive.Value = false;
                // ★ คืน collision ระหว่างผู้เล่นเมื่อทุกคนออกจากโซน
                RestorePlayerCollisionsClientRpc();
                // กันเคสทั้งคู่ตาย: exit มาก่อน/ระหว่างรอ respawn — อย่ารีเซ็ต checkpoint จนกว่า coroutine จะจบ
                if (_respawnAllCoroutine == null && !_isProcessingRespawnAll)
                    ResetCheckpoints();
            }
        }
    }
    
    [ServerRpc(RequireOwnership = false)]
    private void PlayerHitObstacleServerRpc(ulong clientId, ServerRpcParams rpcParams = default)
    {
        if (!IsServer) return;
        var senderClientId = rpcParams.Receive.SenderClientId;
        if (clientId != senderClientId) clientId = senderClientId;
        TryServerHandleSlideObstacleDeath(clientId);
    }
    
    /// <summary>
    /// สำหรับ EnterTriggerToDead บนวัตถุขยับ (เช่น DoorAxisActivatableNet): ประตูลากตัวออกนอก collider สไลด์
    /// ก่อน trigger ตาย → ฝั่ง client จะไม่มี IsSliding / ไม่อยู่ในโซน แต่เซิร์ฟเวอร์ยังมี _slideSessionClients
    /// ลอง slide death ก่อน ไม่ได้ค่อย Kill() ปกติ
    /// </summary>
    [ServerRpc(RequireOwnership = false)]
    private void RequestSlideOrNormalDeathFromHazardTriggerServerRpc(ServerRpcParams rpcParams = default)
    {
        if (!IsServer) return;
        ulong senderClientId = rpcParams.Receive.SenderClientId;
        ServerTrySlideObstacleDeathOrNormalKill(senderClientId);
    }
    
    /// <summary>ลอง KillForSlideZone + respawn slide; ถ้าไม่เข้าเงื่อนไขใช้ Kill() ทั่วไป (กันค้างหลัง Client_Disable)</summary>
    private void ServerTrySlideObstacleDeathOrNormalKill(ulong clientId)
    {
        if (TryServerHandleSlideObstacleDeath(clientId))
            return;
        if (NetworkManager.Singleton != null
            && NetworkManager.Singleton.ConnectedClients.TryGetValue(clientId, out var client)
            && client.PlayerObject != null)
        {
            var pd = client.PlayerObject.GetComponent<PlayerDeath>();
            if (pd != null)
                pd.Kill();
        }
    }
    
    /// <summary>
    /// สำหรับฮาซาร์ดบนเซิร์ฟเวอร์ (หิน, ความร้อน ฯลฯ): ถ้าผู้เล่นอยู่ใน slide ที่กำลังเล่น
    /// ให้ใช้ flow เดียวกับ SlideZoneHitbox (KillForSlideZone + respawn checkpoint/เพื่อน)
    /// </summary>
    /// <returns>true ถ้าจัดการแทน Kill() ทั่วไปแล้ว</returns>
    public bool TryServerHandleSlideObstacleDeath(ulong clientId)
    {
        if (!IsServer || !IsSpawned) return false;
        if (_isProcessingRespawnAll) return false;
        if (!_slideSessionClients.Contains(clientId)) return false;
        if (_deadPlayers.Contains(clientId)) return true; // ผู้เล่นตายไปแล้วในรอบนี้ — บอก caller ว่า "จัดการแล้ว" ไม่ต้อง Kill() ซ้ำ
        // ★ ข้ามผู้เล่นที่อยู่ในช่วง respawn immunity
        EnsureServerCachesForClient(clientId);
        if (_serverPlayerDeaths.TryGetValue(clientId, out var pd) && pd != null && pd.IsRespawnImmune)
            return true; // return true เพื่อบอก caller ว่า "จัดการแล้ว" (ไม่ต้อง Kill() ซ้ำ)
        // รอบแรก: ต้องสไลด์ active หรือเคยเริ่มรอบนี้แล้ว (ออกนอก trigger แต่ยังอยู่ใน session)
        if (!_isSlideActive.Value && _deadPlayers.Count == 0 && !_slideRunStartedOnServer) return false;
        
        EnsureServerCachesForClient(clientId);
        
        _deadPlayers.Add(clientId);
        CancelRespawnCoroutine(clientId);
        
        if (_serverPlayerDeaths.TryGetValue(clientId, out var playerDeath) && playerDeath != null)
        {
            playerDeath.KillForSlideZone();
        }
        
        int aliveInSession = CountAliveClientsInSlideSession();
        Debug.Log($"[SplineSlideZone][Server] Player {clientId} slide obstacle death. Dead: {_deadPlayers.Count}, AliveInSession: {aliveInSession}");
        
        if (aliveInSession <= 0)
        {
            CancelAllRespawnCoroutines();
            // เก็บตำแหน่ง + รายชื่อ client ทันที — กัน trigger exit รีเซ็ต checkpoint / ลบ dict ระหว่างรอ delay
            GetCurrentRespawnPosRot(out Vector3 bothDeadPos, out Quaternion bothDeadRot);
            var clientsToRespawnAll = new List<ulong>(_deadPlayers);
            _respawnAllCoroutine = StartCoroutine(RespawnAllAtStart_Co(bothDeadPos, bothDeadRot, clientsToRespawnAll));
        }
        else
        {
            var co = StartCoroutine(RespawnAtTeammate_Co(clientId));
            _respawnCoroutines[clientId] = co;
        }
        
        return true;
    }
    
    #endregion
    
    #region Client RPCs
    
    [ClientRpc]
    private void StartSlideForPlayerClientRpc(SlideConfig config, ClientRpcParams rpcParams = default)
    {
        if (_localPlayerController != null && _localPlayerInZone)
        {
            // ตั้ง SplineContainer ให้ controller (อาจเป็น rejoin)
            // ★ FIX: recalculate t ทุกครั้งที่ slide เริ่ม/resume
            // เพื่อให้ _splineSlideT ตรงกับตำแหน่งจริงหลัง respawn
            if (splineContainer != null)
            {
                _localPlayerController._splineSlideContainer = splineContainer;
                float3 nearestPoint;
                float t;
                SplineUtility.GetNearestPoint(
                    splineContainer.Spline,
                    (float3)splineContainer.transform.InverseTransformPoint(_localPlayerController.transform.position),
                    out nearestPoint, out t
                );
                _localPlayerController._splineSlideT = t;
                _localPlayerController._splineCurrentSpeed = 0f;
            }
            
            _localPlayerController.SetSlideModeServerRpc(true, config);
            Debug.Log("[SplineSlideZone] Slide started for local player!");
        }
    }
    
    [ClientRpc]
    private void StopSlideForPlayerClientRpc(ClientRpcParams rpcParams = default)
    {
        if (_localPlayerController != null)
        {
            _localPlayerController.SetSlideModeServerRpc(false, default);
            Debug.Log("[SplineSlideZone] Slide stopped for local player!");
        }
    }
    
    /// <summary>
    /// เรียกบนทุก client เพื่อให้ผู้เล่นใน slide zone ทะลุผ่านกันได้ (ไม่ชนกัน)
    /// </summary>
    [ClientRpc]
    private void IgnorePlayerCollisionsClientRpc()
    {
        SetPlayerToPlayerCollisionIgnore(true);
    }
    
    /// <summary>
    /// เรียกบนทุก client เพื่อคืนค่า collision ระหว่างผู้เล่น
    /// </summary>
    [ClientRpc]
    private void RestorePlayerCollisionsClientRpc()
    {
        SetPlayerToPlayerCollisionIgnore(false);
    }
    
    #endregion
    
    #region Checkpoint Logic
    
    private void ServerUpdateCheckpointProgress()
    {
        if (checkpointSpawnPoints == null || checkpointSpawnPoints.Length == 0) return;
        
        int nextIdx = _currentCheckpointIndex.Value + 1;
        if (nextIdx >= checkpointSpawnPoints.Length) return;
        if (checkpointSpawnPoints[nextIdx] == null) return;
        
        Vector3 cpPos = checkpointSpawnPoints[nextIdx].position;
        float radiusSq = checkpointActivationRadius * checkpointActivationRadius;
        
        foreach (ulong clientId in _slideSessionClients)
        {
            if (_deadPlayers.Contains(clientId)) continue;
            EnsureServerCachesForClient(clientId);
            if (!_serverPlayerObjects.TryGetValue(clientId, out var netObj) || netObj == null) continue;
            
            float distSq = (netObj.transform.position - cpPos).sqrMagnitude;
            if (distSq <= radiusSq)
            {
                _currentCheckpointIndex.Value = nextIdx;
                Debug.Log($"[SplineSlideZone][Server] Checkpoint {nextIdx} reached by player {clientId}! " +
                          $"('{checkpointSpawnPoints[nextIdx].name}', dist={Mathf.Sqrt(distSq):F1}m)");
                ServerUpdateCheckpointProgress();
                return;
            }
        }
    }
    
    private void GetCurrentRespawnPosRot(out Vector3 position, out Quaternion rotation)
    {
        int idx = _currentCheckpointIndex.Value;
        if (idx >= 0 && checkpointSpawnPoints != null && idx < checkpointSpawnPoints.Length
            && checkpointSpawnPoints[idx] != null)
        {
            position = checkpointSpawnPoints[idx].position;
            rotation = checkpointSpawnPoints[idx].rotation;
            return;
        }
        position = slideStartPoint != null ? slideStartPoint.position : transform.position;
        rotation = slideStartPoint != null ? slideStartPoint.rotation : Quaternion.identity;
    }
    
    private void ResetCheckpoints()
    {
        if (IsServer)
        {
            _currentCheckpointIndex.Value = -1;
            _slideSessionClients.Clear();
            _slideRunStartedOnServer = false;
            _activatedPortalResetPointIndices.Clear();
        }
    }
    
    #endregion

    #region Portal Reset Logic

    private void ServerUpdatePortalResetPoints()
    {
        if (!IsServer || !IsSpawned) return;
        if (portalResetPoints == null || portalResetPoints.Length == 0) return;
        if (PortalPairManager_Net.Instance == null) return;

        float radiusSq = portalResetActivationRadius * portalResetActivationRadius;

        for (int i = 0; i < portalResetPoints.Length; i++)
        {
            if (_activatedPortalResetPointIndices.Contains(i)) continue;
            var pt = portalResetPoints[i];
            if (pt == null) continue;

            Vector3 p = pt.position;
            foreach (ulong clientId in _slideSessionClients)
            {
                if (_deadPlayers.Contains(clientId)) continue;
                EnsureServerCachesForClient(clientId);
                if (!_serverPlayerObjects.TryGetValue(clientId, out var netObj) || netObj == null) continue;

                float distSq = (netObj.transform.position - p).sqrMagnitude;
                if (distSq <= radiusSq)
                {
                    _activatedPortalResetPointIndices.Add(i);
                    Debug.Log($"[SplineSlideZone][Server] PortalReset point {i} triggered by player {clientId}! " +
                              $"('{pt.name}', dist={Mathf.Sqrt(distSq):F1}m)");

                    // Reset portal pair (ServerRpc is RequireOwnership=false so server can call directly too)
                    PortalPairManager_Net.Instance.ResetPairServerRpc();
                    break;
                }
            }
        }
    }

    #endregion
    
    #region Helper Methods
    
    /// <summary>จำนวนผู้เข้าร่วมรอบสไลด์ที่ยังไม่ถูกนับว่าตาย (เซิร์ฟเวอร์)</summary>
    private int CountAliveClientsInSlideSession()
    {
        int n = 0;
        foreach (ulong cid in _slideSessionClients)
        {
            if (!_deadPlayers.Contains(cid)) n++;
        }
        return n;
    }
    
    /// <summary>กู้ cache PlayerDeath/NetworkObject หลัง exit โซน — กัน KillForSlideZone/ForceRespawn พลาด</summary>
    private void EnsureServerCachesForClient(ulong clientId)
    {
        bool haveDeath = _serverPlayerDeaths.TryGetValue(clientId, out var d) && d != null;
        bool haveObj = _serverPlayerObjects.TryGetValue(clientId, out var no) && no != null;
        if (haveDeath && haveObj) return;
        if (NetworkManager.Singleton == null) return;
        if (!NetworkManager.Singleton.ConnectedClients.TryGetValue(clientId, out var cli) || cli.PlayerObject == null)
            return;
        var po = cli.PlayerObject;
        _serverPlayerObjects[clientId] = po;
        var death = po.GetComponent<PlayerDeath>();
        if (death != null)
            _serverPlayerDeaths[clientId] = death;
    }
    
    private void CancelRespawnCoroutine(ulong clientId)
    {
        if (_respawnCoroutines.TryGetValue(clientId, out var co) && co != null)
        {
            StopCoroutine(co);
            _respawnCoroutines.Remove(clientId);
        }
    }
    
    private void CancelAllRespawnCoroutines()
    {
        foreach (var kvp in _respawnCoroutines)
        {
            if (kvp.Value != null) StopCoroutine(kvp.Value);
        }
        _respawnCoroutines.Clear();
        
        if (_respawnAllCoroutine != null)
        {
            StopCoroutine(_respawnAllCoroutine);
            _respawnAllCoroutine = null;
        }
    }
    
    private void CheckAndStartSlide()
    {
        if (!IsServer) return;
        
        int activeCount = _serverPlayersInZone.Count;
        
        if (activeCount >= requiredPlayers && !_isSlideActive.Value)
        {
            _isSlideActive.Value = true;
            _slideRunStartedOnServer = true;
            var config = CreateSlideConfig();
            
            // ★ ให้ผู้เล่นทะลุผ่านกันได้ระหว่าง slide
            IgnorePlayerCollisionsClientRpc();
            
            foreach (var clientId in _serverPlayersInZone)
            {
                var rpcParams = new ClientRpcParams
                {
                    Send = new ClientRpcSendParams { TargetClientIds = new ulong[] { clientId } }
                };
                StartSlideForPlayerClientRpc(config, rpcParams);
            }
            
            Debug.Log($"[SplineSlideZone][Server] Slide started! Players: {activeCount}");
        }
    }

    private void CheckAndStartSlide(ulong enteredClientId)
    {
        if (!IsServer) return;

        int activeCount = _serverPlayersInZone.Count;

        if (activeCount >= requiredPlayers && !_isSlideActive.Value)
        {
            CheckAndStartSlide();
            return;
        }

        if (_isSlideActive.Value && _serverPlayersInZone.Contains(enteredClientId))
        {
            var config = CreateSlideConfig();
            var rpcParams = new ClientRpcParams
            {
                Send = new ClientRpcSendParams { TargetClientIds = new ulong[] { enteredClientId } }
            };
            StartSlideForPlayerClientRpc(config, rpcParams);
        }
    }
    
    private SlideConfig CreateSlideConfig()
    {
        return new SlideConfig
        {
            // World Axis fields (ไม่ใช้ในโหมด spline แต่ต้องมี)
            slideAxis = 0,
            invertSlide = false,
            slideSpeed = slideSpeed,
            steerAxis = 0,
            invertSteer = invertSteer,
            steerSpeed = steerSpeed,
            // Spline fields
            useSpline = true,
            steerWidth = steerWidth,
            gravityMultiplier = gravityMultiplier,
            // ต้องพอให้เท้าอยู่เหนือผิว mesh ไม่ใช่กลางเนื้อ spline (สำคัญมากกับตัวละครสูง + ทางโค้ง)
            splineSurfaceLift = Mathf.Max(0.01f, splineMeshThickness * 0.5f + splineSurfaceLiftExtra)
        };
    }
    
    private void OnSlideActiveChanged(bool prev, bool current)
    {
        if (waitingUI != null)
        {
            waitingUI.SetActive(!current && _localPlayerInZone);
        }
    }
    
    private void OnPlayerCountChanged(int prev, int current)
    {
        if (waitingUI != null && _localPlayerInZone)
        {
            bool showWaiting = current > 0 && current < requiredPlayers && !_isSlideActive.Value;
            waitingUI.SetActive(showWaiting);
        }
    }
    
    /// <summary>
    /// รวบรวม Collider ของผู้เล่นทุกคนแล้วสั่ง Physics.IgnoreCollision ระหว่างกัน
    /// ignore=true: ทะลุผ่านกัน, ignore=false: คืนค่าชนปกติ
    /// ทำงานบน local client (เพราะ physics จำลองแยกแต่ละเครื่อง)
    /// </summary>
    private void SetPlayerToPlayerCollisionIgnore(bool ignore)
    {
        if (ignore)
        {
            // ล้างคู่เก่าก่อน
            _ignoredCollisionPairs.Clear();
            
            // รวม Collider ของผู้เล่นทุกคนที่เชื่อมต่ออยู่
            var playerColliders = new List<Collider[]>();
            if (NetworkManager.Singleton == null) return;
            
            foreach (var kvp in NetworkManager.Singleton.ConnectedClients)
            {
                var playerObj = kvp.Value.PlayerObject;
                if (playerObj == null) continue;
                var cols = playerObj.GetComponentsInChildren<Collider>(true);
                if (cols != null && cols.Length > 0)
                    playerColliders.Add(cols);
            }
            
            // Ignore collision ทุกคู่ของผู้เล่น
            for (int i = 0; i < playerColliders.Count; i++)
            {
                for (int j = i + 1; j < playerColliders.Count; j++)
                {
                    foreach (var colA in playerColliders[i])
                    {
                        if (colA == null || !colA.enabled) continue;
                        foreach (var colB in playerColliders[j])
                        {
                            if (colB == null || !colB.enabled) continue;
                            Physics.IgnoreCollision(colA, colB, true);
                            _ignoredCollisionPairs.Add((colA, colB));
                        }
                    }
                }
            }
            
            Debug.Log($"[SplineSlideZone] Player-to-player collision IGNORED ({_ignoredCollisionPairs.Count} pairs).");
        }
        else
        {
            // คืนค่า collision ทุกคู่ที่เคย ignore ไว้
            foreach (var pair in _ignoredCollisionPairs)
            {
                if (pair.a != null && pair.b != null)
                    Physics.IgnoreCollision(pair.a, pair.b, false);
            }
            
            Debug.Log($"[SplineSlideZone] Player-to-player collision RESTORED ({_ignoredCollisionPairs.Count} pairs).");
            _ignoredCollisionPairs.Clear();
        }
    }
    
    #endregion
    
    #region Respawn Coroutines
    
    private IEnumerator RespawnAtTeammate_Co(ulong deadClientId)
    {
        Debug.Log($"[SplineSlideZone][Server] Player {deadClientId} waiting {singleDeathRespawnDelay}s to respawn at teammate...");
        yield return new WaitForSeconds(singleDeathRespawnDelay);
        
        if (_isProcessingRespawnAll || !_slideSessionClients.Contains(deadClientId))
            yield break;
        
        // หาเพื่อนที่ยังมีชีวิต (fallback ใช้ checkpoint ล่าสุด หรือ slideStartPoint)
        GetCurrentRespawnPosRot(out Vector3 respawnPos, out Quaternion respawnRot);
        bool foundTeammate = false;
        NetworkObject teammateNetObj = null;
        
        foreach (var clientId in _serverPlayersInZone)
        {
            if (clientId == deadClientId) continue;
            if (_deadPlayers.Contains(clientId)) continue;
            
            // 1) หา teammate object (ใช้จาก cache ก่อน, แล้ว fallback ไปอ่านจาก ConnectedClients)
            NetworkObject netObj = null;
            if (_serverPlayerObjects.TryGetValue(clientId, out var cached) && cached != null)
            {
                netObj = cached;
            }
            else
            {
                if (NetworkManager.Singleton.ConnectedClients.TryGetValue(clientId, out var client) && client.PlayerObject != null)
                {
                    netObj = client.PlayerObject;
                }
            }

            if (netObj == null) continue;

            teammateNetObj = netObj;
            foundTeammate = true;
            break;
        }
        
        // Fallback: บางครั้งเพื่อนอาจออกจาก trigger/zone ก่อน coroutine ทำงาน
        // ให้ลองหา “ผู้เล่นคนอื่นที่ยังไม่ตาย” จาก ConnectedClients แทน
        if (!foundTeammate || teammateNetObj == null)
        {
            foreach (var kvp in NetworkManager.Singleton.ConnectedClients)
            {
                ulong clientId = kvp.Key;
                if (clientId == deadClientId) continue;
                if (_deadPlayers.Contains(clientId)) continue;

                var otherNetObj = kvp.Value.PlayerObject;
                if (otherNetObj == null) continue;

                var otherDeath = otherNetObj.GetComponent<PlayerDeath>();
                // ถ้าเพื่อนซ่อนอยู่ (ตาย/กำลังตาย) ก็อย่าใช้ตำแหน่งนั้นในการ respawn
                if (otherDeath != null && otherDeath.IsHiddenState) continue;

                teammateNetObj = otherNetObj;
                foundTeammate = true;
                Debug.Log($"[SplineSlideZone][Server] RespawnAtTeammate fallback found teammate={clientId} (not in _serverPlayersInZone).");
                break;
            }
        }

        // ★ ใช้ตำแหน่งเพื่อนล่าสุด (ไม่ต้องรอ Grounded เพราะ slide mode อยู่บน spline เสมอ)
        if (foundTeammate && teammateNetObj != null)
        {
            respawnPos = teammateNetObj.transform.position;
            respawnRot = teammateNetObj.transform.rotation;
        }
        else
        {
            Debug.LogWarning($"[SplineSlideZone][Server] RespawnAtTeammate: teammate not found. dead={deadClientId}, playersInZone={_serverPlayersInZone.Count}, deadPlayers={_deadPlayers.Count}, slideStartPoint={(slideStartPoint!=null)}");
            // คง respawnPos/respawnRot เดิม (เริ่มต้นจาก slideStartPoint หรือ origin ของ SplineSlideZone)
        }

        // กันกรณีตำแหน่งที่ซิงค์จาก NetworkTransform/physics ต่ำเกินไปจนตัวทะลุพื้นทันที
        respawnPos += Vector3.up * 0.15f;
        
        // Respawn
        EnsureServerCachesForClient(deadClientId);
        if (_serverPlayerDeaths.TryGetValue(deadClientId, out var playerDeath) && playerDeath != null)
        {
            playerDeath.ForceRespawnAt(respawnPos, respawnRot, teammateRespawnInvincibilityDuration);
        }
        
        _deadPlayers.Remove(deadClientId);
        _respawnCoroutines.Remove(deadClientId);
        
        // เริ่ม slide ใหม่ให้ผู้เล่นที่ respawn
        if (_isSlideActive.Value)
        {
            var config = CreateSlideConfig();
            var rpcParams = new ClientRpcParams
            {
                Send = new ClientRpcSendParams { TargetClientIds = new ulong[] { deadClientId } }
            };
            StartSlideForPlayerClientRpc(config, rpcParams);
        }
        
        Debug.Log($"[SplineSlideZone][Server] Player {deadClientId} respawned at teammate (immune for {teammateRespawnInvincibilityDuration}s).");
    }
    
    private IEnumerator RespawnAllAtStart_Co(Vector3 respawnPos, Quaternion respawnRot, List<ulong> clientsToRespawn)
    {
        _isProcessingRespawnAll = true;
        
        Debug.Log($"[SplineSlideZone][Server] All players died. Waiting {bothDeathRespawnDelay}s to respawn (captured checkpoint pos, {clientsToRespawn.Count} client(s))...");
        yield return new WaitForSeconds(bothDeathRespawnDelay);
        
        respawnPos += Vector3.up * 0.15f;
        
        foreach (var clientId in clientsToRespawn)
        {
            EnsureServerCachesForClient(clientId);
            if (_serverPlayerDeaths.TryGetValue(clientId, out var playerDeath) && playerDeath != null)
            {
                playerDeath.ForceRespawnAt(respawnPos, respawnRot);
            }
        }
        
        foreach (var clientId in clientsToRespawn)
        {
            _deadPlayers.Remove(clientId);
            if (!_serverPlayersInZone.Contains(clientId))
            {
                _serverPlayerObjects.Remove(clientId);
                _serverPlayerDeaths.Remove(clientId);
            }
        }
        
        _respawnAllCoroutine = null;
        
        yield return null;
        _isProcessingRespawnAll = false;
        
        if (_serverPlayersInZone.Count >= requiredPlayers)
        {
            _isSlideActive.Value = true;
            var config = CreateSlideConfig();
            foreach (var clientId in _serverPlayersInZone)
            {
                var rpcParams = new ClientRpcParams
                {
                    Send = new ClientRpcSendParams { TargetClientIds = new ulong[] { clientId } }
                };
                StartSlideForPlayerClientRpc(config, rpcParams);
            }
            Debug.Log("[SplineSlideZone][Server] All players respawned and slide restarted!");
        }
        else
        {
            _isSlideActive.Value = false;
            if (_serverPlayersInZone.Count == 0)
                ResetCheckpoints();
        }
    }
    
    #endregion
    
    #region Public Methods (สำหรับ SlideZoneHitbox / MainMenuUI เรียก)
    
    /// <summary>
    /// เรียกเมื่อผู้เล่นชนสิ่งกีดขวางในโซน slide
    /// </summary>
    public void NotifyPlayerHitObstacle(ulong clientId)
    {
        PlayerHitObstacleServerRpc(clientId);
    }
    
    /// <summary>
    /// เรียกจาก EnterTriggerToDead (โดยเฉพาะบนวัตถุที่ขยับ) — อย่าใช้แค่ IsPlayerInActiveSlide บน client
    /// </summary>
    public void RequestSlideOrNormalDeathFromHazardTrigger()
    {
        if (!IsSpawned) return;
        RequestSlideOrNormalDeathFromHazardTriggerServerRpc();
    }
    
    /// <summary>
    /// ตรวจสอบว่าผู้เล่นอยู่ในโซนและ slide กำลังทำงานหรือไม่
    /// </summary>
    public bool IsPlayerInActiveSlide(ulong clientId)
    {
        bool localSliding = _localPlayerController != null && _localPlayerController.IsSliding.Value;
        // โซนว่าง/ปิด active เร็วกว่า RPC ตาย — ใช้สถานะสไลด์จากตัวละครเป็นหลักสำหรับ EnterTriggerToDead พร้อมกัน
        return localSliding || (_localPlayerInZone && _isSlideActive.Value);
    }
    
    /// <summary>
    /// เรียกจากปุ่ม "Restart from Checkpoint" ใน MainMenuUI
    /// ใช้ flow ของ slide zone: KillForSlideZone → รอ → ForceRespawnAt checkpoint → resume slide
    /// </summary>
    public void RequestRestartAtCheckpoint()
    {
        if (!IsSpawned) return;
        RestartAtCheckpointServerRpc();
    }
    
    [ServerRpc(RequireOwnership = false)]
    private void RestartAtCheckpointServerRpc(ServerRpcParams rpcParams = default)
    {
        if (!IsServer) return;
        ulong clientId = rpcParams.Receive.SenderClientId;
        
        if (!_slideSessionClients.Contains(clientId)) return;
        if (_deadPlayers.Contains(clientId)) return;
        if (_isProcessingRespawnAll) return;
        
        _deadPlayers.Add(clientId);
        CancelRespawnCoroutine(clientId);
        
        EnsureServerCachesForClient(clientId);
        if (_serverPlayerDeaths.TryGetValue(clientId, out var playerDeath) && playerDeath != null)
        {
            playerDeath.KillForSlideZone();
        }
        
        int aliveInSession = CountAliveClientsInSlideSession();
        Debug.Log($"[SplineSlideZone][Server] Player {clientId} menu-restart at checkpoint. Dead: {_deadPlayers.Count}, AliveInSession: {aliveInSession}");
        
        if (aliveInSession <= 0)
        {
            CancelAllRespawnCoroutines();
            GetCurrentRespawnPosRot(out Vector3 bothDeadPos, out Quaternion bothDeadRot);
            var clientsToRespawnAll = new List<ulong>(_deadPlayers);
            _respawnAllCoroutine = StartCoroutine(RespawnAllAtStart_Co(bothDeadPos, bothDeadRot, clientsToRespawnAll));
        }
        else
        {
            var co = StartCoroutine(RespawnAtCheckpoint_Co(clientId));
            _respawnCoroutines[clientId] = co;
        }
    }
    
    private IEnumerator RespawnAtCheckpoint_Co(ulong clientId)
    {
        yield return new WaitForSeconds(bothDeathRespawnDelay);
        
        if (_isProcessingRespawnAll || !_slideSessionClients.Contains(clientId))
            yield break;
        
        GetCurrentRespawnPosRot(out Vector3 respawnPos, out Quaternion respawnRot);
        respawnPos += Vector3.up * 0.15f;
        
        EnsureServerCachesForClient(clientId);
        if (_serverPlayerDeaths.TryGetValue(clientId, out var playerDeath) && playerDeath != null)
        {
            playerDeath.ForceRespawnAt(respawnPos, respawnRot);
        }
        
        _deadPlayers.Remove(clientId);
        _respawnCoroutines.Remove(clientId);
        
        if (_isSlideActive.Value)
        {
            var config = CreateSlideConfig();
            var rpcParams = new ClientRpcParams
            {
                Send = new ClientRpcSendParams { TargetClientIds = new ulong[] { clientId } }
            };
            StartSlideForPlayerClientRpc(config, rpcParams);
        }
        
        Debug.Log($"[SplineSlideZone][Server] Player {clientId} respawned at checkpoint {_currentCheckpointIndex.Value}.");
    }
    
    #endregion
    
    #region Editor Gizmos
    
#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        // วาดขอบเขตของโซน
        Gizmos.color = Color.yellow;
        var col = GetComponent<Collider>();
        if (col is BoxCollider box)
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireCube(box.center, box.size);
            Gizmos.matrix = Matrix4x4.identity;
        }
        
        // วาดจุดเริ่มต้น
        if (slideStartPoint != null)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(slideStartPoint.position, 0.5f);
            Gizmos.DrawLine(slideStartPoint.position, slideStartPoint.position + slideStartPoint.forward * 2f);
        }
        
        // วาด checkpoints + activation radius
        if (checkpointSpawnPoints != null)
        {
            for (int i = 0; i < checkpointSpawnPoints.Length; i++)
            {
                if (checkpointSpawnPoints[i] == null) continue;
                
                // จุด respawn
                Gizmos.color = Color.cyan;
                Gizmos.DrawWireSphere(checkpointSpawnPoints[i].position, 0.4f);
                Gizmos.DrawLine(checkpointSpawnPoints[i].position,
                    checkpointSpawnPoints[i].position + checkpointSpawnPoints[i].forward * 1.5f);
                
                // activation radius
                Gizmos.color = new Color(0f, 1f, 1f, 0.15f);
                Gizmos.DrawWireSphere(checkpointSpawnPoints[i].position, checkpointActivationRadius);
                
                UnityEditor.Handles.color = Color.cyan;
                UnityEditor.Handles.Label(
                    checkpointSpawnPoints[i].position + Vector3.up * 0.8f,
                    $"CP {i}");
            }
        }

        // วาด portal reset points + activation radius
        if (portalResetPoints != null)
        {
            for (int i = 0; i < portalResetPoints.Length; i++)
            {
                if (portalResetPoints[i] == null) continue;

                Gizmos.color = new Color(1f, 0.35f, 0f, 1f); // orange
                Gizmos.DrawWireSphere(portalResetPoints[i].position, 0.45f);

                Gizmos.color = new Color(1f, 0.35f, 0f, 0.15f);
                Gizmos.DrawWireSphere(portalResetPoints[i].position, portalResetActivationRadius);

                UnityEditor.Handles.color = new Color(1f, 0.5f, 0f, 1f);
                UnityEditor.Handles.Label(
                    portalResetPoints[i].position + Vector3.up * 0.9f,
                    $"PR {i}");
            }
        }
        
        // วาด steer width ตาม spline (ถ้ามี)
        if (splineContainer != null && splineContainer.Spline != null)
        {
            Gizmos.color = new Color(0f, 1f, 1f, 0.5f); // cyan
            var spline = splineContainer.Spline;
            int steps = 30;
            for (int i = 0; i <= steps; i++)
            {
                float t = (float)i / steps;
                SplineUtility.Evaluate(spline, t, out float3 pos, out float3 tangent, out float3 up);
                
                Transform tf = splineContainer.transform;
                Vector3 wPos = tf.TransformPoint((Vector3)pos);
                Vector3 wTangent = tf.TransformDirection(((Vector3)tangent).normalized);
                Vector3 wUp = tf.TransformDirection(((Vector3)up).normalized);
                if (((Vector3)wUp).sqrMagnitude < 0.01f) wUp = Vector3.up;
                Vector3 wRight = Vector3.Cross(wUp, wTangent).normalized;
                
                // วาดความกว้าง steer
                Gizmos.DrawLine(wPos - wRight * steerWidth, wPos + wRight * steerWidth);
                
                // วาดจุดกลาง
                Gizmos.color = Color.magenta;
                Gizmos.DrawSphere(wPos, 0.1f);
                Gizmos.color = new Color(0f, 1f, 1f, 0.5f);
            }
        }
    }
#endif
    
    #endregion
}
