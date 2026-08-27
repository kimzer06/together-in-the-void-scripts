using Unity.Netcode;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using UnityEngine.UI;
using Unity.Cinemachine;
using StarterAssets;
using UnityEngine.Audio;

[RequireComponent(typeof(PlayerRoleFromLobby))]
public class TimeFreezeAbility_Net : NetworkBehaviour
{
    public enum FreezeMode { Toggle, Timed }

    [Header("Refs (ปล่อยว่างได้ บน Owner จะหาให้อัตโนมัติ)")]
    [SerializeField] Camera playerCam;
    [SerializeField] CinemachineCamera vcam;
    [SerializeField] LayerMask targetMask = ~0;
    [SerializeField] Image crosshair;
    
    [Header("UI/Layer Names (optional)")]
    [SerializeField] string crosshairName = "Crosshair";
    [SerializeField] string freezableLayerName = "Freezable";

    [Header("Aim Settings")]
    [SerializeField] float aimMaxDistance = 25f;
    [SerializeField] Vector3 shoulderOffsetWhenAiming = new(0.6f, 0.2f, 0f);
    [SerializeField] float shoulderLerp = 10f;

    [Header("Freeze Mode (เลือกโหมดใน Inspector)")]
    [SerializeField] FreezeMode freezeMode = FreezeMode.Toggle;
    [Tooltip("ใช้เมื่อ FreezeMode = Timed")]
    [SerializeField] float timedDuration = 5f;

    [Header("Clock Item")]
    [Tooltip("GameObject/Transform ของไอเทมนาฬิกา — เล็งแล้วขยายสเกล 0→ค่าปกติ, ปล่อยเล็งแล้วหดกลับ")]
    [SerializeField] Transform clockItem;
    [Tooltip("ความเร็ว Lerp สเกลไอเทม 0 ↔ ขนาดปกติ")]
    [SerializeField] float clockItemScaleLerp = 12f;
    [Tooltip("จุดอ้างอิงสเกลใน local space ของไอเทม — ขยาย/หดรุ่งจากจุดนี้ (ตำแหน่งใน world ของจุดนี้จะไม่เลื่อน). (0,0,0) = สเกลรอบ pivot ของ Transform ตามปกติ")]
    [SerializeField] Vector3 clockItemScalePivotLocal;
    [Tooltip("Animator ของไอเทมนาฬิกา – ใช้ Bool parameter 'isActive' เพื่อเล่นอนิเมชั่นหมุน")]
    [SerializeField] Animator clockAnimator;

    [Header("Skill Animator (Layer + Trigger)")]
    [Tooltip("Animator ที่มี Skill Layer (ควรเป็น Animator ของตัวละคร ไม่ใช่ clock/item animator)")]
    [SerializeField] Animator skillAnimator;
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
    [SerializeField] private PlayerRole requiredRole = PlayerRole.RoleA;

    [Header("Audio (SFX)")]
    [SerializeField] private AudioClip shootSound;
    [SerializeField] private AudioSource shootAudioSource;
    [SerializeField] private AudioMixerGroup sfxMixerGroup;
    [Range(0f, 1f)][SerializeField] private float shootVolume = 0.8f;

    private float _lastShootSfxTime = -999f;
    private const float _shootSfxDebounce = 0.05f;

    // runtime
    bool isAiming;
    bool _clockIsOn;
    CinemachineThirdPersonFollow follow;
    Vector3 defaultShoulderOffset;
    TargetHighlight lastHighlight;
    FreezableNet lastTarget;

    Vector3 _clockItemBaseScale = Vector3.one;
    bool _clockItemBaseCached;

    /// <summary>ซิงก์ว่า owner กำลังเล็งสกิลหรือไม่ — ให้ client อื่นเห็นสเกลไอเทม / skill layer</summary>
    readonly NetworkVariable<bool> _netAimVisual = new(
        false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

#if ENABLE_INPUT_SYSTEM
    InputAction aimAction;
    InputAction fireAction;  // ใช้เป็น "trigger freeze" (toggle หรือ timed)
    InputAction resetAction; // ปุ่ม R
#endif

    PlayerRoleFromLobby role;
    ThirdPersonController_Rigidbody _tpc;

    void Awake()
    {
        role = GetComponent<PlayerRoleFromLobby>();
        _tpc = GetComponent<ThirdPersonController_Rigidbody>();

#if ENABLE_INPUT_SYSTEM
        var pi = GetComponent<PlayerInput>();
        if (pi)
        {
            aimAction   = pi.actions["Aim"];    // RMB Hold
            fireAction  = pi.actions["Fire"];   // LMB Press
            resetAction = pi.actions["Reset Ability"];  // R
        }
#endif
        if (crosshair) crosshair.enabled = false;

        CacheClockItemBaseScale();
        SnapClockItemScale(false);
    }

    public override void OnNetworkSpawn()
    {
        AssignLocalRefsSafely();
        ApplyPermissionGate();
        if (role != null)
            role.Role.OnValueChanged += (_, __) => ApplyPermissionGate();
    }

    void AssignLocalRefsSafely()
    {
        playerCam = Camera.main ? Camera.main : FindObjectOfType<Camera>();
        if (playerCam && !playerCam.TryGetComponent<CinemachineBrain>(out _))
            playerCam.gameObject.AddComponent<CinemachineBrain>();

        if (!vcam) vcam = GetComponentInChildren<CinemachineCamera>(true);

        if (vcam)
        {
            var mine = GetComponent<NetworkObject>();
            var ownerOfVcam = vcam.GetComponentInParent<NetworkObject>();
            if (!ownerOfVcam || ownerOfVcam != mine) vcam = null;
        }

        follow = vcam ? vcam.GetComponent<CinemachineThirdPersonFollow>() : null;
        if (follow != null) defaultShoulderOffset = follow.ShoulderOffset;

        if (!crosshair)
        {
            foreach (var img in Resources.FindObjectsOfTypeAll<Image>())
                if (img && img.gameObject.name == crosshairName) { crosshair = img; break; }
        }
        if (crosshair) crosshair.enabled = false;

        if (!string.IsNullOrEmpty(freezableLayerName))
        {
            int mask = LayerMask.GetMask(freezableLayerName);
            if (mask != 0) targetMask = mask;
        }

        // เลือก Animator ของ "ตัวละครผู้เล่น" โดยกัน clock/item animator ออก
        if (!skillAnimator)
        {
            var anims = GetComponentsInChildren<Animator>(true);
            if (anims != null && anims.Length > 0)
            {
                foreach (var a in anims)
                {
                    if (!a) continue;
                    if (clockAnimator != null && a == clockAnimator) continue;
                    skillAnimator = a;
                    break;
                }
            }
        }
    }

    bool CanUseAbility() => IsOwner && role != null && role.Role.Value == requiredRole;

    /// <summary>มี Role ที่ใช้สกิลนี้หรือไม่ (ทุก client — ใช้เปิด component ให้ proxy รันวิชวล)</summary>
    bool HasRoleForSkill() => role != null && role.Role.Value == requiredRole;

    /// <summary>ใช้ขับสเกลไอเทม / skill layer บนทุก client</summary>
    bool AimVisualActive => IsOwner ? isAiming : _netAimVisual.Value;

    private bool IsSkillLayerValid()
    {
        if (skillAnimator == null) return false;
        return skillLayerIndex >= 0 && skillLayerIndex < skillAnimator.layerCount;
    }

    private void SnapSkillLayerWeight(float weight01)
    {
        if (!IsSkillLayerValid()) return;
        skillAnimator.SetLayerWeight(skillLayerIndex, Mathf.Clamp01(weight01));
    }

    private void LerpSkillLayerWeightTowardTarget()
    {
        if (!IsSkillLayerValid()) return;
        float target01 = AimVisualActive ? skillLayerAimWeightPercent * 0.01f : 0f;
        float cur = skillAnimator.GetLayerWeight(skillLayerIndex);
        skillAnimator.SetLayerWeight(skillLayerIndex, Mathf.Lerp(cur, target01, Time.deltaTime * skillLayerWeightLerp));
    }

    void ApplyPermissionGate()
    {
#if ENABLE_INPUT_SYSTEM
        if (aimAction != null) { aimAction.performed -= _OnAimPerformed; aimAction.canceled -= _OnAimCanceled; }
        if (fireAction != null) fireAction.performed -= _OnFirePerformed;
        if (resetAction != null) resetAction.performed -= _OnResetPerformed;
#endif

        if (!HasRoleForSkill())
        {
            isAiming = false;
            if (IsOwner) _netAimVisual.Value = false;
            SnapSkillLayerWeight(0f);
            if (crosshair) crosshair.enabled = false;
            ClearTarget();
            SnapClockItemScale(false);
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
            ClearTarget();
            SnapClockItemScale(false);
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
            if (aimAction != null) { aimAction.performed += _OnAimPerformed; aimAction.canceled += _OnAimCanceled; }
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
    void _OnAimPerformed(InputAction.CallbackContext _ctx)
    {
        // ป้องกัน: ถ้า component ถูกปิดโดย PlayerAbilityManager ไม่ให้เริ่ม aim
        if (!enabled) return;
        StartAim();
    }
    void _OnAimCanceled(InputAction.CallbackContext _ctx) => StopAim();
    void _OnFirePerformed(InputAction.CallbackContext _ctx) => TriggerFreeze();   // ← เปลี่ยนชื่อให้กลาง ๆ
    void _OnResetPerformed(InputAction.CallbackContext _ctx) => ResetFreeze();
#endif

void OnEnable()
    {
        // เมื่อสคริปต์นี้ถูก "เปิด" ไม่ว่าจะโดย PlayerAbilityManager หรือโดยระบบ Role
        // เราต้องเรียก ApplyPermissionGate() เพื่อ:
        // 1. ตรวจสอบสิทธิ์ (CanUseAbility())
        // 2. ถ้าสิทธิ์ถูกต้อง, ก็จะทำการ "Subscribe" (สมัครรับฟัง) Input Actions ใหม่
        // 3. ถ้าสิทธิ์ไม่ถูกต้อง, มันจะสั่ง enabled = false ให้ตัวเอง (ปลอดภัย)
        ApplyPermissionGate();
    }
    void OnDisable()
    {
        if (IsOwner && IsSpawned) _netAimVisual.Value = false;
#if ENABLE_INPUT_SYSTEM
        if (aimAction != null) { aimAction.performed -= _OnAimPerformed; aimAction.canceled -= _OnAimCanceled; }
        if (fireAction != null) fireAction.performed -= _OnFirePerformed;
        if (resetAction != null) resetAction.performed -= _OnResetPerformed;
#endif
        if (crosshair) crosshair.enabled = false;
        ClearTarget();
        isAiming = false;
        SnapSkillLayerWeight(0f);
        SnapClockItemScale(false);
    }

    void Update()
    {
        if (!IsSpawned || !HasRoleForSkill()) return;

        if (CanUseAbility())
        {
            if (follow)
            {
                var target = isAiming ? shoulderOffsetWhenAiming : defaultShoulderOffset;
                follow.ShoulderOffset = Vector3.Lerp(follow.ShoulderOffset, target, Time.deltaTime * shoulderLerp);
            }

            if (isAiming) UpdateAimTarget();

            // เมื่อของที่ถูก freeze กลับมาเคลื่อนไหว (timed หมดเวลา / toggle off) → ปิดนาฬิกาอัตโนมัติ
            if (_clockIsOn && !FreezableNet.HasAnyFrozen)
                SetClockActive(false);
        }

        LerpSkillLayerWeightTowardTarget();
        UpdateClockItemScale();
    }

    void StartAim()
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

    void StopAim()
    {
        isAiming = false;
        if (IsOwner) _netAimVisual.Value = false;
        if (_tpc) _tpc.IsAiming = false;
        if (crosshair) crosshair.enabled = false;
        ClearTarget();
    }

    void UpdateAimTarget()
    {
        if (!playerCam) return;

        Ray ray = playerCam.ViewportPointToRay(new Vector3(0.5f, 0.5f));
        if (Physics.Raycast(ray, out var hit, aimMaxDistance, targetMask, QueryTriggerInteraction.Ignore))
        {
            var target = hit.collider.GetComponentInParent<FreezableNet>();
            var h = hit.collider.GetComponentInParent<TargetHighlight>();

            if (target != lastTarget) lastTarget = target;

            if (h != lastHighlight)
            {
                if (lastHighlight) lastHighlight.SetAimActive(false);
                lastHighlight = h;
                if (lastHighlight) lastHighlight.SetAimActive(true);
            }
        }
        else
        {
            ClearTarget();
        }
    }

    // ---------- Trigger (Toggle / Timed) & Reset ----------

    void TriggerFreeze()
    {
        if (!CanUseAbility()) return;
        if (!isAiming || lastTarget == null) return;

        PlayShootSfx(transform.position);

        if (freezeMode == FreezeMode.Toggle)
        {
            // ถ้า target ถูก freeze อยู่ → toggle off → ปิดนาฬิกา
            bool willUnfreeze = lastTarget.IsCurrentlyFrozen;
            lastTarget.RequestToggleFreezeServerRpc();
            if (skillAnimator) skillAnimator.SetTrigger(skillActivateTriggerParam);
            SetClockActive(!willUnfreeze);
        }
        else // FreezeMode.Timed
        {
            // ป้องกันสแปม: ถ้า target กำลังถูก freeze อยู่แล้ว ไม่ให้ยิงซ้ำ
            if (lastTarget.IsCurrentlyFrozen) return;

            float dur = Mathf.Max(0.05f, timedDuration);
            lastTarget.RequestTimedFreezeServerRpc(dur);
            if (skillAnimator) skillAnimator.SetTrigger(skillActivateTriggerParam);

            // ถ้านาฬิกาหมุนอยู่แล้ว (ของเก่าหลุด ของใหม่โดน) → ยิง ReActive เพื่อรีสตาร์ทอนิเมชั่น
            if (_clockIsOn)
                TriggerClockReActive();
            else
                SetClockActive(true);
        }
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

    void ResetFreeze()
    {
        if (!CanUseAbility()) return;
        if (lastTarget != null) lastTarget.RequestResetServerRpc();
        else
        {
            var any = FindObjectOfType<FreezableNet>();
            if (any) any.RequestResetServerRpc();
        }

        // หยุดอนิเมชั่นนาฬิกา
        SetClockActive(false);
        SnapSkillLayerWeight(0f);
    }

    void ClearTarget()
    {
        if (lastHighlight) lastHighlight.SetAimActive(false);
        lastHighlight = null;
        lastTarget = null;
    }

    // ---------- Clock Animator ----------
    void SetClockActive(bool active)
    {
        _clockIsOn = active;
        if (clockAnimator) clockAnimator.SetBool("isActive", active);
    }

    void TriggerClockReActive()
    {
        if (clockAnimator) clockAnimator.SetTrigger("ReActive");
    }

    void CacheClockItemBaseScale()
    {
        if (!clockItem || _clockItemBaseCached) return;
        _clockItemBaseScale = clockItem.localScale.sqrMagnitude > 1e-8f ? clockItem.localScale : Vector3.one;
        _clockItemBaseCached = true;
    }

    bool ClockItemUsesScalePivot()
    {
        return clockItemScalePivotLocal.sqrMagnitude > 1e-10f;
    }

    void ApplyClockItemLocalScaleKeepingPivot(Vector3 newLocalScale)
    {
        if (!clockItem) return;
        if (!ClockItemUsesScalePivot())
        {
            clockItem.localScale = newLocalScale;
            return;
        }

        Vector3 wsPivotBefore = clockItem.TransformPoint(clockItemScalePivotLocal);
        clockItem.localScale = newLocalScale;
        Vector3 wsPivotAfter = clockItem.TransformPoint(clockItemScalePivotLocal);
        clockItem.position += wsPivotBefore - wsPivotAfter;
    }

    void SnapClockItemScale(bool fullSize)
    {
        if (!clockItem) return;
        CacheClockItemBaseScale();
        ApplyClockItemLocalScaleKeepingPivot(fullSize ? _clockItemBaseScale : Vector3.zero);
    }

    void UpdateClockItemScale()
    {
        if (!clockItem) return;
        CacheClockItemBaseScale();
        var target = AimVisualActive ? _clockItemBaseScale : Vector3.zero;
        var lerped = Vector3.Lerp(clockItem.localScale, target, Time.deltaTime * clockItemScaleLerp);
        ApplyClockItemLocalScaleKeepingPivot(lerped);
    }
}