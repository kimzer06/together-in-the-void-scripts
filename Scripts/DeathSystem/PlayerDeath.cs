using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using Unity.Cinemachine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

[DisallowMultipleComponent]
public class PlayerDeath : NetworkBehaviour
{
    [Header("Respawn / Checkpoint")]
    [SerializeField] private Transform respawnPoint;

    [Header("Networked Death Body (Optional)")]
    [SerializeField] private NetworkObject networkDeathBodyPrefab;
    [SerializeField] private float networkDeathBodyLifetime = 10f;

    [Header("Local Death View")]
    [SerializeField] private float deathViewDuration = 3f;
    [SerializeField] private MonoBehaviour[] movementScriptsToDisable;
    [SerializeField] private Collider[] collidersToDisable;
    [SerializeField] private CharacterController characterController;
    [SerializeField] private MonoBehaviour[] cameraScriptsToDisable;

    private Camera playerCamera;
    private Rigidbody _rb;
    private Behaviour _cinemachineBrain;
    private bool _cinemachineBrainWasEnabledForSlideZoneDeath;
    private bool _cinemachineBrainDisabledForSlideZoneDeath;
    private Vector3 _slideZoneDeathCamWorldPos;
    private Quaternion _slideZoneDeathCamWorldRot;
    private bool _slideZoneDeathLockCamTransform;
    private Transform _camOriginalParent;
    private Vector3 _camOriginalLocalPos;
    private Quaternion _camOriginalLocalRot;
    
    private bool _isDyingLocal = false;
    private bool _initialSpawnSaved;
    private Vector3 _initialSpawnPos;
    private Quaternion _initialSpawnRot;
    private bool serverIsProcessingDeath = false;
    
    // Respawn immunity (ป้องกันการตายซ้ำซ้อนหลัง teleport)
    private float _respawnImmunityEndTime = 0f;
    [SerializeField] private float respawnImmunityDuration = 0.5f;

    [Header("Respawn immunity (Emission blink)")]
    [Tooltip("กระพิบ emission ขณะมี immunity หลัง ForceRespawnAt (เช่น Slide Zone เกิดที่เพื่อน)")]
    [SerializeField] private bool immunityEmissionBlinkEnabled = true;
    [Tooltip("กระพิบเฉพาะเมื่อ immunity ยาวพอ (ไม่รบกวน respawn สั้นๆ แบบทั่วไป)")]
    [SerializeField] private float immunityBlinkMinDurationSeconds = 1f;
    [Tooltip("ช่วงเริ่มต้น (กระพิบช้า) — วินาทีต่อครั้ง")]
    [SerializeField] private float emissionBlinkInterval = 0.12f;
    [Tooltip("ช่วงใกล้หมดเวลาอมตะ (รัวขึ้น) — ควรน้อยกว่าหรือเท่ากับช่วงเริ่ม ถ้าเท่ากันจะกระพิบเร็วคงที่")]
    [SerializeField] private float emissionBlinkIntervalEnd = 0.035f;
    [SerializeField] private Color immunityEmissionTint = new Color(0.35f, 0.85f, 1f, 1f);
    [SerializeField] private float emissionBlinkHigh = 2f;
    [SerializeField] private float emissionBlinkLow = 0.12f;
    [Tooltip("ถ้ามีรายการ — กระพิบเฉพาะ Renderer เหล่านี้เท่านั้น (ไม่สแกนลูกเพิ่ม)")]
    [SerializeField] private Renderer[] emissionBlinkRenderersExplicit;
    [Tooltip("ถ้าไม่ใช้รายการด้านบน — ระบุ root (เช่น mesh ตัวละคร) จะรวม Renderer ใต้ root เหล่านี้เท่านั้น")]
    [SerializeField] private Transform[] emissionBlinkRoots;

    private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
    private static readonly int EmissiveColorId = Shader.PropertyToID("_EmissiveColor");
    private Coroutine _emissionBlinkCo;
    private MaterialPropertyBlock _emissionMpb;
    private List<Renderer> _emissionBlinkRendererList;
    private HashSet<Renderer> _emissionBlinkSeen;
    
    /// <summary>ตรวจสอบว่าผู้เล่นอยู่ในช่วง respawn immunity</summary>
    public bool IsRespawnImmune => Time.time < _respawnImmunityEndTime;

    /// <summary>ตรวจสอบสถานะว่าผู้เล่นถูกซ่อนไว้จากความตาย/ดีสอาเบิลอยู่หรือไม่</summary>
    public bool IsHiddenState => IsHidden.Value;

    // SlideZone kill flow: RPC disable จะเข้าฝั่ง Client ก่อน/พร้อมกับ trigger exit
    // เลยต้องใช้ flag local แทนการพึ่ง IsHidden.Value ที่อาจซิงค์ช้ากว่า
    private bool _isSlideZoneDeathInProgress = false;
    public bool IsSlideZoneDeathInProgress => _isSlideZoneDeathInProgress;

    /// <summary>
    /// ตั้ง flag local ทันทีเมื่อ Client "รู้ตัว" ว่าจะถูก SlideZone ฆ่า
    /// เพื่อกัน timing ที่ SplineSlideZone.OnTriggerExit ทำงานก่อน RPC จาก server มาถึง
    /// </summary>
    public void MarkSlideZoneDeathPendingLocal()
    {
        _isSlideZoneDeathInProgress = true;
    }

    /// <summary>
    /// Client freeze สำหรับ SlideZone hit ทันที เพื่อไม่ให้ผู้เล่น/กล้องไหลไปตาม spline
    /// ก่อน RPC จาก server จะมาสั่ง disable จริง
    /// </summary>
    public void Client_DisableForSlideZoneLocal()
    {
        _isSlideZoneDeathInProgress = true;
        _slideZoneDeathLockCamTransform = true;

        // หยุดการเคลื่อนที่/ฟิสิกส์ทันที (กันการไหลต่อก่อน RPC)
        ToggleMovement(false);
        ToggleColliders(false);

        var tpc = GetComponent<StarterAssets.ThirdPersonController_Rigidbody>();
        if (tpc) tpc.enabled = false;

        if (_rb)
        {
            if (!_rb.isKinematic)
            {
                _rb.linearVelocity = Vector3.zero;
                _rb.angularVelocity = Vector3.zero;
            }
            _rb.isKinematic = true;
        }

        // Freeze กล้อง: ปิด CinemachineBrain ของกล้อง local ทันที
        if (_cinemachineBrain != null && !_cinemachineBrainDisabledForSlideZoneDeath)
        {
            _cinemachineBrainWasEnabledForSlideZoneDeath = _cinemachineBrain.enabled;
            _cinemachineBrain.enabled = false;
            _cinemachineBrainDisabledForSlideZoneDeath = true;
        }

        // ล็อคกล้องแบบ hard เผื่อมีสคริปต์อื่นขยับตำแหน่งเล็กน้อย
        if (playerCamera != null)
        {
            _slideZoneDeathCamWorldPos = playerCamera.transform.position;
            _slideZoneDeathCamWorldRot = playerCamera.transform.rotation;
        }

        // ซ่อน renderers ทันทีเพื่อให้เห็น deadbody อย่างถูกต้อง
        ToggleRenderers(false);
    }

    // --- ตัวแปรใหม่สำหรับซิงค์การมองเห็น ---
    private NetworkVariable<bool> IsHidden = new NetworkVariable<bool>(
        false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // !! [ใหม่] เพิ่มการอ้างอิงถึง Ability Manager !!
    private PlayerAbilityManager _abilityManager;


    private void Awake()
    {
        TryGetComponent(out _rb);

        // !! [ใหม่] ค้นหา PlayerAbilityManager ตอน Awake !!
        TryGetComponent(out _abilityManager);

        if (!_initialSpawnSaved)
        {
            _initialSpawnSaved = true;
            _initialSpawnPos = transform.position;
            _initialSpawnRot = transform.rotation;
        }
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        IsHidden.OnValueChanged += OnHiddenStateChanged;
        OnHiddenStateChanged(false, IsHidden.Value); 

        if (IsOwner)
        {
            playerCamera = Camera.main;
            if (playerCamera)
            {
                var leftover = playerCamera.GetComponent<DeathCameraLook>();
                if (leftover) Destroy(leftover);
                _cinemachineBrain = playerCamera.GetComponent<CinemachineBrain>();
            }
        }
        else
        {
            var virtualCam = GetComponentInChildren<CinemachineCamera>();
            if (virtualCam != null) virtualCam.enabled = false;
            var audioListener = GetComponentInChildren<AudioListener>();
            if (audioListener != null) audioListener.enabled = false;
        }
    }

    private void OnHiddenStateChanged(bool previousValue, bool newValue)
    {
        ToggleRenderers(!newValue);
    }
    
    public void Kill()
    {
        if (IsServer) Server_StartDeath();
        else RequestDeathServerRpc();
    }
    
    /// <summary>
    /// สำหรับ Slide Zone: ตายพร้อม spawn death body แต่ไม่ respawn อัตโนมัติ
    /// ผู้เล่นจะถูกซ่อนไว้จนกว่าจะเรียก ForceRespawnAt()
    /// </summary>
    public void KillForSlideZone()
    {
        if (IsServer) Server_StartDeathForSlideZone();
        else RequestDeathForSlideZoneServerRpc();
    }
    
    /// <summary>
    /// บังคับ respawn ที่ตำแหน่งที่กำหนด (สำหรับ Slide Zone)
    /// เรียกหลังจาก KillForSlideZone() เพื่อแสดงผู้เล่นและย้ายตำแหน่ง
    /// </summary>
    public void ForceRespawnAt(Vector3 position, Quaternion rotation)
    {
        if (IsServer) Server_ForceRespawnAt(position, rotation, respawnImmunityDuration);
        else ForceRespawnAtServerRpc(position, rotation, respawnImmunityDuration);
    }
    
    /// <summary>
    /// บังคับ respawn ที่ตำแหน่งที่กำหนด พร้อมกำหนดระยะเวลาอมตะเอง
    /// </summary>
    public void ForceRespawnAt(Vector3 position, Quaternion rotation, float immunityDuration)
    {
        if (IsServer) Server_ForceRespawnAt(position, rotation, immunityDuration);
        else ForceRespawnAtServerRpc(position, rotation, immunityDuration);
    }

    public void SetRespawnPoint(Transform t) => respawnPoint = t;

    [ServerRpc(RequireOwnership = false)]
    private void RequestDeathServerRpc(ServerRpcParams rpc = default) => Server_StartDeath();

    private void Server_StartDeath()
    {
        if (serverIsProcessingDeath) return;
        serverIsProcessingDeath = true;

        IsHidden.Value = true; // สั่งให้ทุกคนซ่อนผู้เล่นคนนี้

        Vector3 deathPos = transform.position;
        Quaternion deathRot = transform.rotation;

        if (networkDeathBodyPrefab)
        {
            var body = Instantiate(networkDeathBodyPrefab, deathPos, deathRot);
            body.Spawn();
            var auto = body.GetComponent<DeathBodyAutoDespawn_Net>();
            if (auto) auto.SetLifetime(networkDeathBodyLifetime);
        }

        Owner_RunLocalDeathClientRpc(deathPos, deathRot, new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new ulong[] { OwnerClientId } }
        });

        Invoke(nameof(Server_ClearProcessingFlag), 0.25f);
    }

    private void Server_ClearProcessingFlag() => serverIsProcessingDeath = false;
    
    // === SLIDE ZONE DEATH (ไม่ respawn อัตโนมัติ) ===
    
    [ServerRpc(RequireOwnership = false)]
    private void RequestDeathForSlideZoneServerRpc(ServerRpcParams rpc = default) => Server_StartDeathForSlideZone();
    
    private void Server_StartDeathForSlideZone()
    {
        if (serverIsProcessingDeath) return;
        serverIsProcessingDeath = true;
        
        IsHidden.Value = true; // ซ่อนผู้เล่น
        
        Vector3 deathPos = transform.position;
        Quaternion deathRot = transform.rotation;
        
        // Spawn death body (สำหรับ visual)
        if (networkDeathBodyPrefab)
        {
            var body = Instantiate(networkDeathBodyPrefab, deathPos, deathRot);
            body.Spawn();
            var auto = body.GetComponent<DeathBodyAutoDespawn_Net>();
            if (auto) auto.SetLifetime(networkDeathBodyLifetime);
        }
        
        // ปิด movement บน client (แต่ไม่ respawn)
        Owner_DisableForSlideZoneClientRpc(new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new ulong[] { OwnerClientId } }
        });
        
        Invoke(nameof(Server_ClearProcessingFlag), 0.25f);
    }
    
    [ClientRpc]
    private void Owner_DisableForSlideZoneClientRpc(ClientRpcParams rpcParams = default)
    {
        Client_DisableForSlideZoneLocal();
        Debug.Log("[PlayerDeath] Disabled for SlideZone (waiting for ForceRespawnAt)");
    }
    
    [ServerRpc(RequireOwnership = false)]
    private void ForceRespawnAtServerRpc(Vector3 position, Quaternion rotation, float immunityDuration) => Server_ForceRespawnAt(position, rotation, immunityDuration);
    
    private void Server_ForceRespawnAt(Vector3 position, Quaternion rotation, float immunityDuration)
    {
        // Re-apply ability rules
        if (_abilityManager != null)
        {
            _abilityManager.Server_ReapplyAllRulesOnRespawn();
        }

        // ★ เซต immunity บนเซิร์ฟเวอร์ด้วย เพื่อให้ death zone ที่ทำงานฝั่ง server (เช่น HeatDeathZone)
        // เช็คได้ว่าผู้เล่นยังอยู่ในช่วงอมตะ
        _respawnImmunityEndTime = Time.time + immunityDuration;

        // สำคัญ: เซิร์ฟเวอร์ต้อง "ย้ายตำแหน่งจริง" ด้วย
        // กันกรณีที่ client respawn แล้ว แต่ server ยังอยู่ตำแหน่งเดิม
        // ทำให้ logic อย่าง spline snap (ที่ฝั่ง serverคำนวณจาก rigidbody position) ใช้ข้อมูลผิด
        if (_rb)
        {
            _rb.position = position;
            _rb.rotation = rotation;
            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
        }
        else
        {
            transform.SetPositionAndRotation(position, rotation);
        }
        
        // แสดงผู้เล่น
        IsHidden.Value = false;
        
        // ส่งคำสั่ง respawn ไปให้ client (พร้อมระยะเวลาอมตะ)
        Owner_ForceRespawnAtClientRpc(position, rotation, immunityDuration, new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new ulong[] { OwnerClientId } }
        });

        // กระพิบ emission ให้ทุก client เห็น (ไม่ใช่แค่ owner)
        if (immunityEmissionBlinkEnabled && immunityDuration >= immunityBlinkMinDurationSeconds)
            ImmunityEmissionBlinkAllClientsClientRpc(immunityDuration);
    }
    
    [ClientRpc]
    private void Owner_ForceRespawnAtClientRpc(Vector3 position, Quaternion rotation, float immunityDuration, ClientRpcParams rpcParams = default)
    {
        _isSlideZoneDeathInProgress = false;
        _slideZoneDeathLockCamTransform = false;

        // คืนค่า กล้อง
        if (_cinemachineBrain != null && _cinemachineBrainDisabledForSlideZoneDeath)
        {
            _cinemachineBrain.enabled = _cinemachineBrainWasEnabledForSlideZoneDeath;
            _cinemachineBrainDisabledForSlideZoneDeath = false;
        }

        ToggleRenderers(true);
        // เปิด immunity ตามระยะเวลาที่กำหนด
        _respawnImmunityEndTime = Time.time + immunityDuration;
        
        // ย้ายตำแหน่ง (ขณะที่ colliders ยังปิด)
        transform.SetPositionAndRotation(position, rotation);
        
        // Sync physics ก่อนเปิด colliders
        Physics.SyncTransforms();
        
        // เปิด movement กลับ
        if (_rb)
        {
            _rb.isKinematic = false;
            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
        }
        
        var tpc = GetComponent<StarterAssets.ThirdPersonController_Rigidbody>();
        if (tpc) tpc.enabled = true;
        
        ToggleColliders(true);
        ToggleMovement(true);
        
        _isDyingLocal = false;
        
        Debug.Log($"[PlayerDeath] Force respawned at {position} (immune for {immunityDuration}s)");
    }

    [ClientRpc]
    private void ImmunityEmissionBlinkAllClientsClientRpc(float immunityDuration)
    {
        StopImmunityEmissionBlinkLocal();
        if (immunityDuration <= 0.01f) return;
        _emissionBlinkCo = StartCoroutine(ImmunityEmissionBlink_Co(immunityDuration));
    }

    private IEnumerator ImmunityEmissionBlink_Co(float duration)
    {
        // รอให้ IsHidden / renderer ซิงค์ (โดยเฉพาะ client ที่ไม่ใช่ owner)
        yield return null;
        yield return null;

        CollectEmissionBlinkRenderers();
        if (_emissionBlinkRendererList == null || _emissionBlinkRendererList.Count == 0)
        {
            _emissionBlinkCo = null;
            yield break;
        }

        float endTime = Time.time + duration;
        float totalDuration = Mathf.Max(0.01f, duration);
        bool bright = true;
        while (Time.time < endTime)
        {
            ApplyImmunityEmissionBlink(bright);
            bright = !bright;

            // ค่อยๆ รัวขึ้นเมื่อใกล้หมดเวลา (ช่วงเริ่มช้า → ช่วงท้ายเร็ว)
            float remaining = endTime - Time.time;
            float progress = 1f - Mathf.Clamp01(remaining / totalDuration); // 0 = เริ่ม, 1 = ใกล้จบ
            float easeT = Mathf.SmoothStep(0f, 1f, progress);
            float interval;
            if (emissionBlinkIntervalEnd >= emissionBlinkInterval - 0.0001f)
                interval = emissionBlinkInterval;
            else
                interval = Mathf.Lerp(emissionBlinkInterval, emissionBlinkIntervalEnd, easeT);

            yield return new WaitForSeconds(Mathf.Max(0.02f, interval));
        }

        ClearEmissionPropertyBlocksLocal();
        _emissionBlinkCo = null;
    }

    /// <summary>
    /// ลำดับ: Renderer ที่ระบุตรงๆ → root ที่ระบุ (รวมลูก) → ทั้ง hierarchy ของผู้เล่น
    /// </summary>
    private void CollectEmissionBlinkRenderers()
    {
        if (_emissionBlinkRendererList == null) _emissionBlinkRendererList = new List<Renderer>(32);
        _emissionBlinkRendererList.Clear();

        if (emissionBlinkRenderersExplicit != null && emissionBlinkRenderersExplicit.Length > 0)
        {
            foreach (var r in emissionBlinkRenderersExplicit)
                if (r) _emissionBlinkRendererList.Add(r);
            return;
        }

        if (emissionBlinkRoots != null && emissionBlinkRoots.Length > 0)
        {
            if (_emissionBlinkSeen == null) _emissionBlinkSeen = new HashSet<Renderer>();
            else _emissionBlinkSeen.Clear();

            foreach (var root in emissionBlinkRoots)
            {
                if (!root) continue;
                var rs = root.GetComponentsInChildren<Renderer>(true);
                foreach (var r in rs)
                {
                    if (!r) continue;
                    if (_emissionBlinkSeen.Add(r))
                        _emissionBlinkRendererList.Add(r);
                }
            }
            return;
        }

        GetComponentsInChildren<Renderer>(true, _emissionBlinkRendererList);
    }

    private void ApplyImmunityEmissionBlink(bool bright)
    {
        if (_emissionMpb == null) _emissionMpb = new MaterialPropertyBlock();
        float mul = bright ? emissionBlinkHigh : emissionBlinkLow;
        Color c = immunityEmissionTint * mul;

        if (_emissionBlinkRendererList == null || _emissionBlinkRendererList.Count == 0)
            CollectEmissionBlinkRenderers();

        foreach (var r in _emissionBlinkRendererList)
        {
            if (!r || !r.enabled) continue;
            for (int i = 0; i < r.sharedMaterials.Length; i++)
            {
                var mat = r.sharedMaterials[i];
                if (!mat) continue;
                _emissionMpb.Clear();
                r.GetPropertyBlock(_emissionMpb, i);
                if (mat.HasProperty(EmissionColorId))
                    _emissionMpb.SetColor(EmissionColorId, c);
                else if (mat.HasProperty(EmissiveColorId))
                    _emissionMpb.SetColor(EmissiveColorId, c);
                else
                    continue;
                r.SetPropertyBlock(_emissionMpb, i);
            }
        }
    }

    private void ClearEmissionPropertyBlocksLocal()
    {
        CollectEmissionBlinkRenderers();
        if (_emissionBlinkRendererList == null) return;
        foreach (var r in _emissionBlinkRendererList)
        {
            if (!r) continue;
            for (int i = 0; i < r.sharedMaterials.Length; i++)
                r.SetPropertyBlock(null, i);
        }
    }

    private void StopImmunityEmissionBlinkLocal()
    {
        if (_emissionBlinkCo != null)
        {
            StopCoroutine(_emissionBlinkCo);
            _emissionBlinkCo = null;
        }
        ClearEmissionPropertyBlocksLocal();
    }

    public override void OnNetworkDespawn()
    {
        StopImmunityEmissionBlinkLocal();
        base.OnNetworkDespawn();
    }

    private void LateUpdate()
    {
        // กันกล้องไหลแบบ hard ในช่วง SlideZone death (กันเคส Cinemachine damping / target drift)
        if (!IsOwner) return;
        if (!_isSlideZoneDeathInProgress) return;
        if (!_slideZoneDeathLockCamTransform) return;
        if (playerCamera == null) return;

        playerCamera.transform.SetPositionAndRotation(_slideZoneDeathCamWorldPos, _slideZoneDeathCamWorldRot);
    }

    [ClientRpc]
    private void Owner_RunLocalDeathClientRpc(Vector3 deathPos, Quaternion deathRot, ClientRpcParams rpcParams = default)
    {
        StartCoroutine(DeathFlow_Co(deathPos, deathRot));
    }
    
    // -----------------------------------------------------------------
    // !! [แก้ไขฟังก์ชันนี้] !!
    // -----------------------------------------------------------------
    [ServerRpc]
    private void RequestRespawnServerRpc()
    {
        // !! [ใหม่] !!
        // ก่อนที่เราจะสั่งให้ผู้เล่นมองเห็นได้ (Un-hide)
        // เราต้องสั่งให้ Server "รีเซ็ต" และ "บังคับใช้กฎ" กับผู้เล่นคนนี้ใหม่
        if (_abilityManager != null)
        {
            // เรียกฟังก์ชันใหม่ที่เราเพิ่งเพิ่มใน PlayerAbilityManager
            _abilityManager.Server_ReapplyAllRulesOnRespawn();
        }
        else
        {
            // แจ้งเตือนไว้ เผื่อเราลืมใส่ PlayerAbilityManager บน Prefab
            Debug.LogWarning($"PlayerDeath on {gameObject.name} could not find PlayerAbilityManager to re-apply rules!");
        }

        // [โลจิกเดิม]
        IsHidden.Value = false; // สั่งให้ทุกคนแสดงผู้เล่นคนนี้อีกครั้ง
    }
    
    private IEnumerator DeathFlow_Co(Vector3 deathPos, Quaternion deathRot)
    {
        if (_isDyingLocal) yield break;
        _isDyingLocal = true;

        // (โค้ดส่วนที่เหลือในนี้ "เหมือนเดิม" ทั้งหมด)

        ToggleMovement(false);
        ToggleColliders(false);

        var tpc = GetComponent<StarterAssets.ThirdPersonController_Rigidbody>();
        if (tpc) tpc.enabled = false;

        bool wasCCEnabled = false;
        if (characterController)
        {
            wasCCEnabled = characterController.enabled;
            characterController.enabled = false;
        }

        if (_rb)
        {
            _rb.isKinematic = true;
            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
        }

        if (playerCamera)
        {
            _camOriginalParent = playerCamera.transform.parent;
            _camOriginalLocalPos = playerCamera.transform.localPosition;
            _camOriginalLocalRot = playerCamera.transform.localRotation;

            if (_cinemachineBrain) _cinemachineBrain.enabled = false;
            if (cameraScriptsToDisable != null)
                foreach (var mb in cameraScriptsToDisable) if (mb) mb.enabled = false;

            playerCamera.transform.SetParent(null, true);

            var look = playerCamera.gameObject.AddComponent<DeathCameraLook>();
            
            float startYaw, startPitch;
            if (tpc != null && tpc.CinemachineCameraTarget != null)
            {
                var e = tpc.CinemachineCameraTarget.transform.rotation.eulerAngles;
                startYaw = e.y;
                startPitch = e.x;
                if (startPitch > 180f) startPitch -= 360f;
            }
            else
            {
                startYaw = playerCamera.transform.eulerAngles.y;
                startPitch = playerCamera.transform.eulerAngles.x;
            }

            if (tpc) look.ConfigureFromTPC(tpc);
            #if ENABLE_INPUT_SYSTEM
            look.UseLookActionFrom(gameObject, "Look");
            #endif

            float startDistance = Vector3.Distance(playerCamera.transform.position, deathPos + Vector3.up * 1.2f);
            look.SetFocusOrbit(deathPos, startYaw, startPitch, startDistance);
        }

        Vector3 spawnPos = _initialSpawnPos;
        Quaternion spawnRot = _initialSpawnRot;
        if (respawnPoint)
        {
            spawnPos = respawnPoint.position;
            spawnRot = respawnPoint.rotation;
        }
        transform.SetPositionAndRotation(spawnPos, spawnRot);

        yield return new WaitForSeconds(deathViewDuration);

        // คืนกล้อง
        if (playerCamera)
        {
            var look = playerCamera.GetComponent<DeathCameraLook>();
            if (look) Destroy(look);

            playerCamera.transform.SetParent(_camOriginalParent, true);
            playerCamera.transform.localPosition = _camOriginalLocalPos;
            playerCamera.transform.localRotation = _camOriginalLocalRot;

            if (_cinemachineBrain) _cinemachineBrain.enabled = true;
            if (cameraScriptsToDisable != null)
                foreach (var mb in cameraScriptsToDisable) if (mb) mb.enabled = true;
        }
        
        // บอก Server ว่าพร้อมเกิดแล้ว (แล้ว Server จะสั่งให้ทุกคนเห็นเราเอง)
        RequestRespawnServerRpc();

        // คืนค่าอื่นๆ ที่เป็น Local
        ToggleColliders(true);
        if (characterController) characterController.enabled = wasCCEnabled;

        if (_rb) _rb.isKinematic = false;
        if (tpc) tpc.enabled = true;
        ToggleMovement(true);

        _isDyingLocal = false;
        
        // === FIX: รีเซ็ตสถานะที่ถูกตั้งโดยเมนู (OpenMenu) ===
        // เมื่อเปิดเมนู OpenMenu จะตั้ง MovementLocked/LockCameraPosition 
        // แต่ OnRestartFromCheckpoint ไม่ได้ปิดเมนูผ่าน OpenMenu 
        // จึงต้องรีเซ็ตเองตรงนี้หลัง death flow เสร็จ
        if (tpc != null)
        {
            tpc.SetMovementLocked(false);
            tpc.LockCameraPosition = false;
        }
        
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        
        // ปิด OpenMenu menuObject (ถ้ายังเปิดอยู่)
        var openMenu = FindObjectOfType<OpenMenu>();
        if (openMenu != null)
            openMenu.ForceCloseMenu();
    }

    private void ToggleMovement(bool enable)
    {
        if (movementScriptsToDisable == null) return;
        foreach (var mb in movementScriptsToDisable) if (mb) mb.enabled = enable;
    }

    private void ToggleColliders(bool enable)
    {
        if (collidersToDisable == null) return;
        foreach (var c in collidersToDisable) if (c) c.enabled = enable;
    }

    private void ToggleRenderers(bool enable)
    {
        if (!enable) StopImmunityEmissionBlinkLocal();
        var rends = GetComponentsInChildren<Renderer>(true);
        foreach (var r in rends) if (r) r.enabled = enable;
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (respawnPoint)
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(respawnPoint.position, 0.25f);
        }
    }
#endif
}