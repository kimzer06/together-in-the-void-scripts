using Unity.Netcode;
using UnityEngine;
using UnityEngine.Serialization;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using UnityEngine.UI;
using Unity.Cinemachine;
using StarterAssets;
using UnityEngine.Audio;

[RequireComponent(typeof(PlayerRoleFromLobby))]
public class PortalSumoner_Net : NetworkBehaviour
{
    [Header("Refs (Owner เท่านั้น)")]
    [SerializeField] private Camera playerCam;
    [SerializeField] private CinemachineCamera vcam;
    [SerializeField] private Image crosshair;

    [Header("UI/Layer Names (optional)")]
    [SerializeField] private string crosshairName = "Crosshair";
    [SerializeField] private string placeableLayerName = "";

    [Header("Aim Settings (เหมือน TimeFreeze)")]
    [SerializeField] private float aimMaxDistance = 25f;
    [SerializeField] private Vector3 shoulderOffsetWhenAiming = new(0.6f, 0.2f, 0f);
    [SerializeField] private float shoulderLerp = 10f;

    [Header("Placement Settings")]
    [SerializeField] private LayerMask placeOnMask = ~0;

    [Header("Projectile Settings")]
    [Tooltip("Prefab กระสุนสำหรับ Portal A (ต้องมี NetworkObject + PortalProjectile + Rigidbody + Collider)")]
    [SerializeField] private GameObject projectilePrefabA;
    [Tooltip("Prefab กระสุนสำหรับ Portal B (ต้องมี NetworkObject + PortalProjectile + Rigidbody + Collider)")]
    [SerializeField] private GameObject projectilePrefabB;
    [SerializeField] private float projectileSpeed = 30f;
    [Tooltip("จุดออกกระสุน (ลาก GameObject ที่เป็นจุดยิงมาใส่)")]
    [SerializeField] private Transform firePoint;
    [Tooltip("Fire rate (วินาทีระหว่างกระสุนแต่ละนัด)")]
    [SerializeField] private float fireRate = 1f;

    [Header("Slide Zone Boost")]
    [Tooltip("ความเร็วกระสุนพิเศษเมื่ออยู่ใน SplineSlideZone (0 = ใช้ค่า projectileSpeed ปกติ)")]
    [SerializeField] private float projectileSpeedInSlide = 50f;

    [Header("Gun Item")]
    [Tooltip("GameObject/Transform ของไอเทมปืน — เล็งแล้วขยายสเกล 0→ค่าปกติ, ปล่อยเล็งแล้วหดกลับ")]
    [SerializeField] private Transform gunItem;
    [Tooltip("ความเร็ว Lerp สเกลไอเทม 0 ↔ ขนาดปกติ")]
    [SerializeField] private float gunItemScaleLerp = 12f;
    [Tooltip("จุดอ้างอิงสเกลใน local space ของไอเทม — ขยาย/หดรุ่งจากจุดนี้ (ตำแหน่งใน world ของจุดนี้จะไม่เลื่อน). (0,0,0) = สเกลรอบ pivot ของ Transform ตามปกติ")]
    [SerializeField] private Vector3 gunItemScalePivotLocal;

    [Header("Gun Animator")]
    [Tooltip("Animator ของปืน/แขน — จะเล่น Trigger PortalA/PortalB ตอนยิง (sync ผ่าน Client NetworkAnimator)")]
    [FormerlySerializedAs("playerAnimator")]
    [SerializeField] private Animator gunAnimator;

    [Header("Skill Animator (Layer + Trigger)")]
    [Tooltip("Animator ที่มี Skill Layer (เลเยอร์ทับบน — มักเป็น Animator เดียวกับตัวละครหรือแยกตามที่ตั้งในโปรเจกต์)")]
    [SerializeField] private Animator skillAnimator;
    [Tooltip("Index ของ Skill Layer ใน Animator (0 = Base Layer — ปกติใช้ 1 ขึ้นไป)")]
    [SerializeField] private int skillLayerIndex = 1;
    [Tooltip("น้ำหนักเลเยอร์ตอนเล็งเต็มที่ (0–100; Unity ใช้ค่า 0–1 ภายใน)")]
    [Range(0f, 100f)]
    [SerializeField] private float skillLayerAimWeightPercent = 100f;
    [Tooltip("ความเร็ว Lerp น้ำหนักเลเยอร์ 0 ↔ ค่าเล็ง")]
    [SerializeField] private float skillLayerWeightLerp = 10f;
    [SerializeField] private string skillActivateTriggerParam = "SkillActivate";

    [Header("Role")]
    [Tooltip("สกิลนี้จะทำงานได้เมื่อผู้เล่นมี Role ที่กำหนด")]
    [SerializeField] private PlayerRole requiredRole = PlayerRole.RoleB;

    [Header("Audio (SFX)")]
    [SerializeField] private AudioClip shootSound;
    [SerializeField] private AudioSource shootAudioSource;
    [SerializeField] private AudioMixerGroup sfxMixerGroup;
    [Range(0f, 1f)][SerializeField] private float shootVolume = 0.8f;

    private float _lastShootSfxTime = -999f;
    private const float _shootSfxDebounce = 0.05f;

    private bool isAiming;
    private CinemachineThirdPersonFollow follow;
    private Vector3 defaultShoulderOffset;

    private bool placingA = true;
    private float nextFireTime;

#if ENABLE_INPUT_SYSTEM
    private InputAction aimAction;
    private InputAction fireAction;
    private InputAction resetAction;
#endif

    private PlayerRoleFromLobby role;
    private ThirdPersonController_Rigidbody _tpc;

    private Vector3 _gunItemBaseScale = Vector3.one;
    private bool _gunItemBaseCached;

    private readonly NetworkVariable<bool> _netAimVisual = new(
        false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    private void SnapSkillLayerWeight(float weight01)
    {
        if (skillAnimator == null || !IsSkillLayerValid())
            return;
        skillAnimator.SetLayerWeight(skillLayerIndex, Mathf.Clamp01(weight01));
    }

    private bool IsSkillLayerValid()
    {
        if (skillAnimator == null) return false;
        return skillLayerIndex >= 0 && skillLayerIndex < skillAnimator.layerCount;
    }

    private void LerpSkillLayerWeightTowardTarget()
    {
        if (skillAnimator == null || !IsSkillLayerValid())
            return;

        float target01 = AimVisualActive ? skillLayerAimWeightPercent * 0.01f : 0f;
        float cur = skillAnimator.GetLayerWeight(skillLayerIndex);
        skillAnimator.SetLayerWeight(skillLayerIndex,
            Mathf.Lerp(cur, target01, Time.deltaTime * skillLayerWeightLerp));
    }

    void Awake()
    {
        role = GetComponent<PlayerRoleFromLobby>();
        _tpc = GetComponent<ThirdPersonController_Rigidbody>();
        if (crosshair) crosshair.enabled = false;

        CacheGunItemBaseScale();
        SnapGunItemScale(false);

#if ENABLE_INPUT_SYSTEM
        var pi = GetComponent<PlayerInput>();
        if (pi)
        {
            aimAction = pi.actions.FindAction("Aim", false);
            fireAction = pi.actions.FindAction("Fire", false);

            resetAction = pi.actions.FindAction("Reset Ability", false);
        }
#endif
    }

    public override void OnNetworkSpawn()
    {
        AssignLocalRefsSafely();
        ApplyPermissionGate();

        if (role != null)
            role.Role.OnValueChanged += (_, __) => ApplyPermissionGate();
    }

    private void AssignLocalRefsSafely()
    {
        playerCam = playerCam ? playerCam : (Camera.main ? Camera.main : FindObjectOfType<Camera>());
        if (playerCam && !playerCam.TryGetComponent<CinemachineBrain>(out _))
            playerCam.gameObject.AddComponent<CinemachineBrain>();

        if (!vcam)
            vcam = GetComponentInChildren<CinemachineCamera>(true);
        if (vcam)
        {
            var mine = GetComponent<NetworkObject>();
            var ownerOfVcam = vcam.GetComponentInParent<NetworkObject>();
            if (!ownerOfVcam || ownerOfVcam != mine) vcam = null;
        }

        follow = vcam ? vcam.GetComponent<CinemachineThirdPersonFollow>() : null;
        if (follow) defaultShoulderOffset = follow.ShoulderOffset;

        if (!crosshair)
        {
            foreach (var img in Resources.FindObjectsOfTypeAll<Image>())
            {
                if (img && img.gameObject.name == crosshairName) { crosshair = img; break; }
            }
        }
        if (crosshair) crosshair.enabled = false;

        if (!string.IsNullOrWhiteSpace(placeableLayerName))
        {
            int mask = LayerMask.GetMask(placeableLayerName);
            if (mask != 0) placeOnMask = mask;
        }

        // Animator fallback — หา Animator บน Player ถ้ายังไม่ได้ drag
        if (!gunAnimator)
            gunAnimator = GetComponentInChildren<Animator>();
        if (!skillAnimator)
            skillAnimator = gunAnimator;
    }

    private bool CanUseAbility() => IsOwner && role != null && role.Role.Value == requiredRole;

    private bool HasRoleForSkill() => role != null && role.Role.Value == requiredRole;

    private bool AimVisualActive => IsOwner ? isAiming : _netAimVisual.Value;

    private void ApplyPermissionGate()
    {
#if ENABLE_INPUT_SYSTEM
        if (aimAction != null)
        {
            aimAction.performed -= _OnAimPerformed;
            aimAction.canceled -= _OnAimCanceled;
        }
        if (fireAction != null) fireAction.performed -= _OnFirePerformed;
        if (resetAction != null) resetAction.performed -= _OnResetPerformed;
#endif

        if (!HasRoleForSkill())
        {
            isAiming = false;
            if (IsOwner) _netAimVisual.Value = false;
            SnapSkillLayerWeight(0f);
            if (crosshair) crosshair.enabled = false;
            SnapGunItemScale(false);
#if ENABLE_INPUT_SYSTEM
            aimAction?.Disable();
            fireAction?.Disable();
            resetAction?.Disable();
#endif
            enabled = false;
            return;
        }

        var pam = GetComponent<PlayerAbilityManager>();
        if (pam != null && !pam.IsAbilityBehaviourAllowed(this))
        {
            isAiming = false;
            if (IsOwner) _netAimVisual.Value = false;
            SnapSkillLayerWeight(0f);
            if (crosshair) crosshair.enabled = false;
            SnapGunItemScale(false);
#if ENABLE_INPUT_SYSTEM
            aimAction?.Disable();
            fireAction?.Disable();
            resetAction?.Disable();
#endif
            enabled = false;
            return;
        }

        enabled = true;

#if ENABLE_INPUT_SYSTEM
        if (IsOwner)
        {
            aimAction?.Enable();
            fireAction?.Enable();
            resetAction?.Enable();
            if (aimAction != null)
            {
                aimAction.performed += _OnAimPerformed;
                aimAction.canceled += _OnAimCanceled;
            }
            if (fireAction != null) fireAction.performed += _OnFirePerformed;
            if (resetAction != null) resetAction.performed += _OnResetPerformed;
        }
        else
        {
            aimAction?.Disable();
            fireAction?.Disable();
            resetAction?.Disable();
        }
#endif
    }

#if ENABLE_INPUT_SYSTEM
    private void _OnAimPerformed(InputAction.CallbackContext _ctx)
    {
        // ป้องกัน: ถ้า component ถูกปิดโดย PlayerAbilityManager ไม่ให้เริ่ม aim
        if (!enabled) return;
        StartAim();
    }
    private void _OnAimCanceled(InputAction.CallbackContext _ctx) => StopAim();
    private void _OnFirePerformed(InputAction.CallbackContext _ctx) => TryPlacePortal();
    private void _OnResetPerformed(InputAction.CallbackContext _ctx) => ResetPortals();
#endif

    void OnEnable() => ApplyPermissionGate();

    void OnDisable()
    {
        if (IsOwner && IsSpawned) _netAimVisual.Value = false;
        SnapSkillLayerWeight(0f);
#if ENABLE_INPUT_SYSTEM
        if (aimAction != null)
        {
            aimAction.performed -= _OnAimPerformed;
            aimAction.canceled -= _OnAimCanceled;
        }
        if (fireAction != null) fireAction.performed -= _OnFirePerformed;
        if (resetAction != null) resetAction.performed -= _OnResetPerformed;

        aimAction?.Disable();
        fireAction?.Disable();
        resetAction?.Disable();
#endif
        if (crosshair) crosshair.enabled = false;
        isAiming = false;
        SnapGunItemScale(false);
    }

    void Update()
    {
        if (!IsSpawned || !HasRoleForSkill()) return;

        if (CanUseAbility() && follow)
        {
            var target = isAiming ? shoulderOffsetWhenAiming : defaultShoulderOffset;
            follow.ShoulderOffset = Vector3.Lerp(follow.ShoulderOffset, target, Time.deltaTime * shoulderLerp);
        }

        LerpSkillLayerWeightTowardTarget();
        UpdateGunItemScale();
    }

    // ------- Aim Flow -------
    private void StartAim()
    {
        // เพิ่มเช็ค enabled: ถ้า PlayerAbilityManager ปิด component นี้ไว้ ไม่ให้เริ่ม aim
        if (!enabled || !CanUseAbility()) return;
        // ป้องกันเปิด crosshair ขณะ pause menu เปิดอยู่
        if (_tpc && _tpc.MovementLocked) return;
        isAiming = true;
        if (IsOwner) _netAimVisual.Value = true;
        if (_tpc) _tpc.IsAiming = true;
        if (crosshair) crosshair.enabled = true;
    }

    private void StopAim()
    {
        isAiming = false;
        if (IsOwner) _netAimVisual.Value = false;
        if (_tpc) _tpc.IsAiming = false;
        if (crosshair) crosshair.enabled = false;
    }

    // ------- Place / Reset -------
    private void TryPlacePortal()
    {
        if (!CanUseAbility()) return;
        if (!isAiming || !playerCam) return;
        if (Time.time < nextFireTime) return;
        if (firePoint == null)
        {
            Debug.LogError("[PortalSummoner] firePoint ยังไม่ได้ตั้ง!");
            return;
        }

        // คำนวณจุดเล็งจาก center of screen
        Ray ray = playerCam.ViewportPointToRay(new Vector3(0.5f, 0.5f));

        // หาจุดเล็งในระยะไกล เพื่อให้กระสุนพุ่งไปทางที่เล็ง
        Vector3 aimTarget;
        if (Physics.Raycast(ray, out var hit, aimMaxDistance, ~0, QueryTriggerInteraction.Ignore))
            aimTarget = hit.point;
        else
            aimTarget = ray.origin + ray.direction * aimMaxDistance;

        // ใช้ทิศทางจากกล้องตรงๆ แทนการคำนวณจาก firePoint → aimTarget
        // เพราะขณะ Spline Slide ตัวละคร (และ firePoint) จะเอียงตาม spline
        // ทำให้ spawnPos เพี้ยน → aimDir ผิดทิศ
        Vector3 spawnPos = firePoint.position;
        Vector3 aimDir = ray.direction;

        // เล่น Animator Trigger สลับตาม Portal ที่จะเปิด (sync ผ่าน Client NetworkAnimator)
        if (gunAnimator != null)
            gunAnimator.SetTrigger(placingA ? "PortalA" : "PortalB");

        if (skillAnimator != null)
            skillAnimator.SetTrigger(skillActivateTriggerParam);

        PlayShootSfx(firePoint.position);

        // ★ เลือกความเร็วกระสุน: ถ้าอยู่ใน Slide Zone ใช้ค่าพิเศษ
        float speed = (_tpc != null && _tpc.IsSliding.Value && projectileSpeedInSlide > 0f)
            ? projectileSpeedInSlide
            : projectileSpeed;

        // ส่ง prefabIndex เพื่อให้ Server เลือก prefab ที่ถูกต้อง
        int prefabIndex = placingA ? 0 : 1;
        FireProjectileServerRpc(spawnPos, aimDir * speed, placingA, placeOnMask.value, prefabIndex);
        nextFireTime = Time.time + fireRate;
    }

    private void PlayShootSfx(Vector3 position)
    {
        if (!IsOwner) return;
        if (shootSound == null) return;
        if (shootVolume <= 0f) return;
        if (Time.time - _lastShootSfxTime < _shootSfxDebounce) return;
        _lastShootSfxTime = Time.time;

        // local
        if (shootAudioSource != null)
        {
            if (sfxMixerGroup != null) shootAudioSource.outputAudioMixerGroup = sfxMixerGroup;
            shootAudioSource.transform.position = position;
            shootAudioSource.volume = Mathf.Clamp01(shootVolume);
            shootAudioSource.pitch = 1f;
            shootAudioSource.PlayOneShot(shootSound);
        }

        // others
        if (!IsSpawned) return;
        if (IsServer) PlayShootSfxClientRpc(position);
        else SendShootSfxServerRpc(position);
    }

    [ServerRpc]
    private void SendShootSfxServerRpc(Vector3 position, ServerRpcParams rpcParams = default)
    {
        // ส่งไปทุก client ยกเว้นคนยิง (กันเสียงซ้ำฝั่ง owner)
        var senderId = rpcParams.Receive.SenderClientId;
        var all = NetworkManager.ConnectedClientsIds;
        if (all == null || all.Count == 0) return;

        var targets = new System.Collections.Generic.List<ulong>(all.Count);
        for (int i = 0; i < all.Count; i++)
        {
            ulong id = all[i];
            if (id == senderId) continue;
            targets.Add(id);
        }
        if (targets.Count == 0) return;

        var clientParams = new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = targets.ToArray() }
        };
        PlayShootSfxClientRpc(position, clientParams);
    }

    [ClientRpc]
    private void PlayShootSfxClientRpc(Vector3 position, ClientRpcParams clientRpcParams = default)
    {
        if (shootSound == null) return;
        if (shootVolume <= 0f) return;
        if (shootAudioSource == null) return;

        if (sfxMixerGroup != null) shootAudioSource.outputAudioMixerGroup = sfxMixerGroup;
        shootAudioSource.transform.position = position;
        shootAudioSource.volume = Mathf.Clamp01(shootVolume);
        shootAudioSource.pitch = 1f;
        shootAudioSource.PlayOneShot(shootSound);
    }

    [ServerRpc]
    private void FireProjectileServerRpc(Vector3 spawnPos, Vector3 velocity, bool isPortalA, int placeOnMaskValue, int prefabIndex)
    {
        // เลือก prefab ตาม index: 0 = Portal A, 1 = Portal B
        GameObject chosenPrefab = prefabIndex == 0 ? projectilePrefabA : projectilePrefabB;

        if (chosenPrefab == null)
        {
            Debug.LogError($"[PortalSummoner] projectilePrefab{(prefabIndex == 0 ? "A" : "B")} ยังไม่ได้ตั้ง!");
            return;
        }

        var go = Instantiate(chosenPrefab, spawnPos, Quaternion.LookRotation(velocity.normalized));
        var no = go.GetComponent<NetworkObject>();
        no.Spawn(true);

        // ★ กัน projectile ชนตัวผู้เล่นที่ยิง
        // บน Server ตำแหน่งผู้เล่นอาจต่างจาก client-reported spawnPos (latency)
        // ทำให้กระสุนชน collider ของผู้เล่นตัวเอง → ถูกดันผิดทิศ
        var projCollider = go.GetComponent<Collider>();
        if (projCollider != null)
        {
            foreach (var playerCol in GetComponentsInChildren<Collider>())
            {
                Physics.IgnoreCollision(projCollider, playerCol);
            }
        }

        var proj = go.GetComponent<PortalProjectile>();
        if (proj != null)
        {
            proj.InitOnServer(velocity, isPortalA, placeOnMaskValue, this);
        }
        else
        {
            Debug.LogError($"[PortalSummoner] projectilePrefab{(prefabIndex == 0 ? "A" : "B")} ไม่มี PortalProjectile component!");
            no.Despawn(true);
        }
    }

    /// <summary>
    /// เรียกจาก PortalProjectile (ผ่าน Server) เมื่อพอร์ทัลถูกเปิดสำเร็จ
    /// เพื่อสลับ placingA ฝั่ง Owner เท่านั้น
    /// </summary>
    [ClientRpc]
    public void NotifyPortalPlacedClientRpc()
    {
        placingA = !placingA;
    }

    private void ResetPortals()
    {
        if (!CanUseAbility()) return;

        if (PortalPairManager_Net.Instance != null)
            PortalPairManager_Net.Instance.ResetPairServerRpc();
        else
            Debug.LogError("[PortalSummoner] PortalPairManager_Net.Instance == null (ลืมใส่ในซีน?)");

        placingA = true;
        if (crosshair) crosshair.enabled = false;
        isAiming = false;
        if (IsOwner) _netAimVisual.Value = false;
        SnapSkillLayerWeight(0f);
        if (_tpc) _tpc.IsAiming = false;
        SnapGunItemScale(false);
    }

    private void CacheGunItemBaseScale()
    {
        if (!gunItem || _gunItemBaseCached) return;
        _gunItemBaseScale = gunItem.localScale.sqrMagnitude > 1e-8f ? gunItem.localScale : Vector3.one;
        _gunItemBaseCached = true;
    }

    private bool GunItemUsesScalePivot()
    {
        return gunItemScalePivotLocal.sqrMagnitude > 1e-10f;
    }

    private void ApplyGunItemLocalScaleKeepingPivot(Vector3 newLocalScale)
    {
        if (!gunItem) return;
        if (!GunItemUsesScalePivot())
        {
            gunItem.localScale = newLocalScale;
            return;
        }

        Vector3 wsPivotBefore = gunItem.TransformPoint(gunItemScalePivotLocal);
        gunItem.localScale = newLocalScale;
        Vector3 wsPivotAfter = gunItem.TransformPoint(gunItemScalePivotLocal);
        gunItem.position += wsPivotBefore - wsPivotAfter;
    }

    private void SnapGunItemScale(bool fullSize)
    {
        if (!gunItem) return;
        CacheGunItemBaseScale();
        ApplyGunItemLocalScaleKeepingPivot(fullSize ? _gunItemBaseScale : Vector3.zero);
    }

    private void UpdateGunItemScale()
    {
        if (!gunItem) return;
        CacheGunItemBaseScale();
        var target = AimVisualActive ? _gunItemBaseScale : Vector3.zero;
        var lerped = Vector3.Lerp(gunItem.localScale, target, Time.deltaTime * gunItemScaleLerp);
        ApplyGunItemLocalScaleKeepingPivot(lerped);
    }

}