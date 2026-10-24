using DG.Tweening;
using Fusion;
using UnityEngine;
using UnityEngine.Serialization;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using UnityEngine.UI;
using Unity.Cinemachine;
using UnityEngine.Audio;
using StarterAssets;

/// <summary>
/// ฐานของสกิลแบบ "เล็งแล้วยิง" (Time Freeze / Portal) — ของที่สองสกิลเคยเขียนซ้ำกันคำต่อคำ:
/// ประตูสิทธิ์ (Role + PlayerAbilityManager), ผูกปุ่ม Aim/Fire/Reset, กล้องไหล่, crosshair,
/// ไอเทมในมือขยาย/หด, น้ำหนัก Skill Layer, เสียงยิง และ trigger อนิเมชัน
/// คลาสลูกเขียนแค่ OnFire / OnReset (+ hook เล็กๆ)
///
/// Fusion Shared Mode: เจ้าของตัวละคร (state authority) เป็นคนเล็ง/ยิง
/// - AimVisual [Networked] ให้เครื่องอื่นเห็นไอเทม + Skill Layer ขณะเล็ง
/// - เสียงยิง / trigger อนิเมชัน → RPC ไป Proxies (คนยิงเล่นเองไปแล้ว)
///   ไม่ใช้ NetworkMecanimAnimator ส่ง trigger (ดู ThirdPersonController_Rigidbody.SetAnimTrigger)
/// </summary>
[RequireComponent(typeof(PlayerRoleFromLobby))]
public abstract class AimedAbility_Net : NetworkBehaviour
{
    protected enum AnimSlot : byte { Skill = 0, Item = 1 }

    [Header("Refs (ปล่อยว่างได้ บน Owner จะหาให้อัตโนมัติ)")]
    [SerializeField] protected Camera playerCam;
    [SerializeField] protected CinemachineCamera vcam;
    [SerializeField] protected Image crosshair;

    [Header("UI (optional)")]
    [SerializeField] private string crosshairName = "Crosshair";

    [Header("Aim Settings")]
    [SerializeField] protected float aimMaxDistance = 25f;
    [SerializeField] private Vector3 shoulderOffsetWhenAiming = new(0.6f, 0.2f, 0f);
    [SerializeField] private float shoulderLerp = 10f;

    [Header("Reticle Snap (จอย — แบบ It Takes Two)")]
    [Tooltip("crosshair เลื่อนไปเกาะเป้าที่ห่างกลางจอไม่เกินค่านี้ (สัดส่วนของความสูงจอ) — กล้องไม่หมุนตาม")]
    [SerializeField, Range(0.02f, 0.4f)] private float snapRadius = 0.15f;
    [Tooltip("เกาะแล้วหลุดเมื่อห่างเกิน snapRadius × ค่านี้ (กัน crosshair กระพริบเข้าออกที่ขอบ)")]
    [SerializeField, Min(1f)] private float snapReleaseMultiplier = 1.3f;
    [Tooltip("เวลาที่ crosshair ใช้เลื่อนไปหาเป้า/กลับกลางจอ (วินาที) — ตอนสไลด์ใช้ครึ่งหนึ่ง")]
    [SerializeField, Min(0.01f)] private float snapSmoothTime = 0.08f;
    [Tooltip("crosshair เด้งตอนเกาะเป้าใหม่ (สัดส่วนที่ขยาย)")]
    [SerializeField, Min(0f)] private float snapPopStrength = 0.35f;
    [Tooltip("เกาะเป้าตอนใช้เมาส์ด้วย (ปิด = เมาส์ใช้ crosshair กลางจอนิ่งๆ แบบเดิม)")]
    [SerializeField] private bool snapWithMouse;
    [Tooltip("snapRadius ตอนใช้เมาส์ (เมาส์เล็งแม่นกว่าจอย — ลดได้ถ้ารู้สึกว่าดูดแรงเกิน)")]
    [SerializeField, Range(0.02f, 0.4f)] private float mouseSnapRadius = 0.15f;
    [Tooltip("สี crosshair ตอนเล็งติดเป้า")]
    [SerializeField] private Color crosshairLockedColor = new(1f, 0.85f, 0.3f, 1f);
    [Tooltip("รูป crosshair ตอนเกาะเป้าได้ (จอย / เมาส์ที่เปิด snapWithMouse) — ขีด 4 ทิศ + จุด ไม่มีวงนอก · ว่าง = ใช้รูปเดิมใน prefab · ควรเป็นสีขาว (ทาสีทองตอนเกาะเป้า)")]
    [SerializeField] private Sprite gamepadCrosshairSprite;
    [Tooltip("ขนาด crosshair ตอนเกาะเป้าได้ (หน่วยของ Canvas) — เมาส์ที่ไม่เกาะใช้ขนาดเดิมใน prefab")]
    [SerializeField, Min(4f)] private float gamepadCrosshairSize = 52f;
    [Tooltip("วงกลมขอบนอกของ crosshair — โผล่เฉพาะตอนเกาะเป้า (ซ้อนบน crosshair ขนาดเดียวกัน) · ว่าง = ไม่มีวง")]
    [SerializeField] private Sprite snapRingSprite;
    [Tooltip("เวลาที่วงกลมขอบนอกใช้โผล่/หาย (วินาที)")]
    [SerializeField, Min(0.01f)] private float snapRingFadeTime = 0.1f;
    [Tooltip("วงกลมขอบนอกเริ่มใหญ่เท่านี้แล้วหดเข้ามาตอนเกาะเป้า (หลุดเป้า = ขยายออกแล้วจางหาย)")]
    [SerializeField, Min(1f)] private float snapRingStartScale = 1.5f;
    [Tooltip("วงแหวนบนเป้าที่ยิงได้ตอนเล็งด้วยจอย — สีตัวละคร (Lyra ม่วง / Bobby น้ำเงิน)")]
    [SerializeField] private Color targetMarkerColor = new(0.35f, 0.75f, 1f, 0.9f);
    [Tooltip("ขนาดวงแหวนบนเป้า (หน่วยของ Canvas)")]
    [SerializeField, Min(4f)] private float targetMarkerSize = 34f;

    [Header("Held Item (นาฬิกา / ปืน)")]
    [Tooltip("GameObject/Transform ของไอเทมในมือ — เล็งแล้วขยายสเกล 0→ค่าปกติ, ปล่อยเล็งแล้วหดกลับ")]
    [FormerlySerializedAs("clockItem"), FormerlySerializedAs("gunItem")]
    [SerializeField] private Transform heldItem;
    [Tooltip("ความเร็ว Lerp สเกลไอเทม 0 ↔ ขนาดปกติ")]
    [FormerlySerializedAs("clockItemScaleLerp"), FormerlySerializedAs("gunItemScaleLerp")]
    [SerializeField] private float heldItemScaleLerp = 12f;
    [Tooltip("จุดอ้างอิงสเกลใน local space ของไอเทม — ขยาย/หดรุ่งจากจุดนี้ (ตำแหน่งใน world ของจุดนี้จะไม่เลื่อน). (0,0,0) = สเกลรอบ pivot ของ Transform ตามปกติ")]
    [FormerlySerializedAs("clockItemScalePivotLocal"), FormerlySerializedAs("gunItemScalePivotLocal")]
    [SerializeField] private Vector3 heldItemScalePivotLocal;

    [Header("Skill Animator (Layer + Trigger)")]
    [Tooltip("Animator ที่มี Skill Layer (ควรเป็น Animator ของตัวละคร ไม่ใช่ animator ของไอเทม)")]
    [SerializeField] protected Animator skillAnimator;
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

    private const float ShootSfxDebounce = 0.05f;
    private float _lastShootSfxTime = -999f;

    protected bool isAiming;
    private CinemachineThirdPersonFollow _follow;
    private Vector3 _defaultShoulderOffset;
    private Vector3 _itemBaseScale = Vector3.one;
    private bool _itemBaseCached;
    private Color _crosshairBaseColor = Color.white;
    private bool _crosshairLocked;      // คลาสลูกบอกว่าเล็งโดนเป้า
    private bool _crosshairColorLocked; // สีที่ทาอยู่จริง (ลูกบอก || เกาะเป้าอยู่)

    // reticle snap (เครื่องเจ้าของ)
    private AimAssistTarget _snapTarget;
    private Vector2 _reticleScreen;
    private Vector2 _reticleOffset;     // ห่างจากเป้าหมาย (เป้าที่เกาะ / กลางจอ) — ไหลเข้าหา 0 · เป้าขยับบนจอ crosshair ติดไปด้วย
    private Vector2 _reticleVelocity;
    private bool _reticleActive;
    private bool _lateHooked;
    private readonly System.Collections.Generic.List<AimAssistTarget> _visibleTargets = new();
    private Vector3 _crosshairBaseScale = Vector3.one;
    private Sprite _crosshairBaseSprite;
    private Vector2 _crosshairBaseSize;
    private bool _crosshairGamepadStyle;
    private Tween _crosshairPop;
    private Image _snapRing;
    private float _snapRingAmount;      // 0 = ซ่อน · 1 = โผล่เต็ม
    private AimTargetMarkers _markers;
    private int _sightMask;
    private System.Predicate<AimAssistTarget> _snapAccepts;
    private Vector3 _ownerAimPoint;

    protected PlayerRoleFromLobby role;
    protected ThirdPersonController_Rigidbody tpc;
    private PlayerAbilityManager _pam;

#if ENABLE_INPUT_SYSTEM
    private InputAction _aimAction;
    private InputAction _fireAction;
    private InputAction _resetAction;
#endif

    /// <summary>เจ้าของกำลังเล็งอยู่ไหม — เจ้าของเขียน ให้เครื่องอื่นขับสเกลไอเทม / Skill Layer</summary>
    [Networked] private NetworkBool AimVisual { get; set; }

    /// <summary>จุดที่เจ้าของเล็งอยู่ — เครื่องอื่นใช้ก้ม/เงยลำตัวตาม (PlayerAimPose) · เขียนเฉพาะตอนเล็ง</summary>
    [Networked] private Vector3 AimPoint { get; set; }

    // ---------- สิ่งที่คลาสลูกกำหนด ----------

    /// <summary>Role ตั้งต้นตอนเพิ่ม component ใหม่ (ค่าใน prefab ทับเสมอ)</summary>
    protected abstract PlayerRole DefaultRole { get; }

    /// <summary>Animator ของไอเทมในมือ (นาฬิกา / ปืน) — ใช้กับ AnimSlot.Item</summary>
    protected abstract Animator ItemAnimator { get; }

    /// <summary>กดยิง (เจ้าของ + มีสิทธิ์แล้ว)</summary>
    protected abstract void OnFire();

    /// <summary>กดรีเซ็ต (เจ้าของ + มีสิทธิ์แล้ว)</summary>
    protected abstract void OnReset();

    /// <summary>หยุดเล็ง / ถูกปิดสกิล — ล้างเป้าที่ไฮไลต์อยู่ ฯลฯ</summary>
    protected virtual void OnAimStopped() { }

    /// <summary>สกิลถูกปิด (PlayerAbilityManager / ตาย / despawn) — ล้างของที่ค้างอยู่</summary>
    protected virtual void OnAbilityDisabled() { }

    /// <summary>ทุกเฟรมบนเครื่องเจ้าของ (หลังขยับกล้องไหล่)</summary>
    protected virtual void OwnerUpdate() { }

    /// <summary>หา ref ที่ใช้เฉพาะเจ้าของ (กล้อง, layer mask ของเป้า) — เรียกตอน Spawned</summary>
    protected virtual void AssignOwnerRefs() { }

    /// <summary>หา Animator ที่ไม่ได้ลากใส่ — ทุกเครื่อง (proxy ก็ต้องใช้ขับ Skill Layer)</summary>
    protected virtual void ResolveAnimators() { }

    /// <summary>เป้าที่ crosshair เกาะได้ (จอย) — ไม่ override = ไม่เกาะอะไร</summary>
    protected virtual bool AcceptsSnapTarget(AimAssistTarget target) => false;

    /// <summary>ระยะไกลสุดที่ crosshair เกาะเป้า (= ระยะยิงของสกิล)</summary>
    protected virtual float SnapRange => aimMaxDistance;

    // ---------- สถานะ ----------

    protected bool IsSpawnedSafe => Object != null && Object.IsValid;
    protected bool IsOwnerSpawned => IsSpawnedSafe && HasStateAuthority;

    protected bool HasRoleForSkill() => role != null && role.Role == requiredRole;
    protected bool CanUseAbility() => IsOwnerSpawned && HasRoleForSkill();

    private bool AimVisualActive => IsOwnerSpawned ? isAiming : (IsSpawnedSafe && AimVisual);

    /// <summary>กำลังเล็งอยู่ไหม — ทุกเครื่อง (PlayerAimPose ใช้ก้ม/เงยลำตัว)</summary>
    public bool AimPoseActive => enabled && AimVisualActive;

    /// <summary>จุดที่เล็งอยู่ — เจ้าของ = เส้นเล็งเฟรมนี้ · เครื่องอื่น = ค่าที่เจ้าของส่งมา</summary>
    public Vector3 AimPosePoint => IsSpawnedSafe && !HasStateAuthority ? AimPoint : _ownerAimPoint;

    // ---------- Unity / Fusion ----------

    protected virtual void Reset() => requiredRole = DefaultRole;

    protected virtual void Awake()
    {
        role = GetComponent<PlayerRoleFromLobby>();
        tpc = GetComponent<ThirdPersonController_Rigidbody>();
        _pam = GetComponent<PlayerAbilityManager>();

        if (crosshair) crosshair.enabled = false;

        CacheItemBaseScale();
        SnapItemScale(false);
    }

    public override void Spawned()
    {
        ResolveAnimators();
        if (HasStateAuthority)
        {
            ResolveInputActions();
            AssignLocalRefs();
            AssignOwnerRefs();
        }
        ApplyPermissionGate();
    }

    // OnEnable/OnDisable เป็น private ไม่ให้คลาสลูก override — Fusion weave โค้ดของตัวเองเข้าเมธอดชื่อนี้
    private void OnEnable()
    {
        // ถูกเปิดโดย PlayerAbilityManager หรือระบบอื่น — ตรวจสิทธิ์ใหม่ (ไม่ผ่านจะปิดตัวเอง) แล้วผูกปุ่มให้เจ้าของ
        ApplyPermissionGate();
    }

    private void OnDisable()
    {
        UnbindInput();
        bool wasAiming = isAiming;
        if (IsOwnerSpawned)
        {
            AimVisual = false;
            // เดิมไม่คืนค่า → ถูกปิดสกิลกลางการเล็ง (เช่น เข้าโซน side-scroll) ตัวละครค้างท่าเดินเล็ง
            if (wasAiming && tpc) tpc.IsAiming = false;
        }
        if (crosshair) crosshair.enabled = false;
        isAiming = false;
        OnAimStopped();
        SetCrosshairLocked(false);
        ResetReticle();
        SnapSkillLayerWeight(0f);
        SnapItemScale(false);
        OnAbilityDisabled();
    }

    private void OnDestroy()
    {
        SetLateReticleHook(false);
        if (_snapRing) Destroy(_snapRing.gameObject);
        _crosshairPop?.Kill();
        _markers?.Destroy();
    }

    protected virtual void Update()
    {
        if (!IsSpawnedSafe || !HasRoleForSkill()) return;

        if (CanUseAbility())
        {
            if (_follow)
            {
                var target = isAiming ? shoulderOffsetWhenAiming : _defaultShoulderOffset;
                _follow.ShoulderOffset = Vector3.Lerp(_follow.ShoulderOffset, target, Time.deltaTime * shoulderLerp);
            }
            // ก่อน OwnerUpdate — คลาสลูกยิงเส้นผ่านตำแหน่ง crosshair ของเฟรมนี้
            if (isAiming && playerCam)
            {
                UpdateReticle();
                UpdateAimPoint();
            }
            OwnerUpdate();
        }

        LerpSkillLayerWeightTowardTarget();
        UpdateItemScale();
    }

    // ---------- Refs ----------

    private void ResolveInputActions()
    {
#if ENABLE_INPUT_SYSTEM
        // อ่านตอน Spawned (ไม่ใช่ Awake) — PlayerInput อาจ clone action asset ให้ผู้เล่นคนที่สองตอนเปิดใช้งาน
        var pi = GetComponent<PlayerInput>();
        if (pi && pi.actions != null)
        {
            _aimAction = pi.actions.FindAction("Aim", false);              // RMB Hold
            _fireAction = pi.actions.FindAction("Fire", false);            // LMB Press
            _resetAction = pi.actions.FindAction("Reset Ability", false);  // R
        }
#endif
    }

    private void AssignLocalRefs()
    {
        if (!playerCam) playerCam = Camera.main ? Camera.main : FindFirstObjectByType<Camera>();
        if (playerCam && !playerCam.TryGetComponent<CinemachineBrain>(out _))
            playerCam.gameObject.AddComponent<CinemachineBrain>();

        if (!vcam) vcam = GetComponentInChildren<CinemachineCamera>(true);
        // vcam ต้องเป็นของตัวละครตัวนี้เท่านั้น
        if (vcam && !vcam.transform.IsChildOf(transform)) vcam = null;

        _follow = vcam ? vcam.GetComponent<CinemachineThirdPersonFollow>() : null;
        if (_follow) _defaultShoulderOffset = _follow.ShoulderOffset;

        if (!crosshair) crosshair = FindCrosshair();
        if (crosshair)
        {
            crosshair.enabled = false;
            _crosshairBaseColor = crosshair.color;
            _crosshairBaseScale = crosshair.rectTransform.localScale;
            _crosshairBaseSprite = crosshair.sprite;
            _crosshairBaseSize = crosshair.rectTransform.sizeDelta;
            _markers ??= new AimTargetMarkers(crosshair.rectTransform.parent as RectTransform, targetMarkerColor, targetMarkerSize);
            CreateSnapRing();
        }

        // เส้นสายตา (เป้าถูกบังไหม) — นับผนังทุกอย่าง ยกเว้นตัวละคร · ไม่ใช้ ~0 ตรงๆ
        _sightMask = ~LayerMask.GetMask("Player", "PlayerA", "PlayerB", "Ghost", "Ignore Raycast");
        _snapAccepts = AcceptsSnapTarget;
    }

    /// <summary>
    /// หา crosshair ใน HUD ของตัวละครตัวเองก่อน — เดิมไล่ Resources.FindObjectsOfTypeAll ทั้งหน่วยความจำ
    /// (ช้า และเจอ "Crosshair" ของ prefab / ของตัวละครอีกคนได้ — T_PlayerB ไม่ได้ลาก crosshair ไว้)
    /// </summary>
    private Image FindCrosshair()
    {
        foreach (var img in GetComponentsInChildren<Image>(true))
            if (img && img.gameObject.name == crosshairName) return img;

        foreach (var img in FindObjectsByType<Image>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (img && img.gameObject.name == crosshairName && !img.GetComponentInParent<AimedAbility_Net>(true)) return img;

        return null;
    }

    // ---------- Permission gate ----------

    /// <summary>
    /// ทุกสกิลต้องผ่าน 2 ชั้น: Role ตรง และ PlayerAbilityManager อนุญาต — ไม่ผ่านปิดตัวเอง
    /// ผ่านแล้วผูกปุ่มเฉพาะบนเครื่องเจ้าของ
    /// </summary>
    protected void ApplyPermissionGate()
    {
        UnbindInput();

        bool allowed = HasRoleForSkill() && (_pam == null || _pam.IsAbilityBehaviourAllowed(this));
        if (!allowed)
        {
            ResetAimState();
#if ENABLE_INPUT_SYSTEM
            _aimAction?.Disable();
            _fireAction?.Disable();
            _resetAction?.Disable();
#endif
            enabled = false;
            return;
        }

        enabled = true;

#if ENABLE_INPUT_SYSTEM
        if (IsOwnerSpawned)
        {
            _aimAction?.Enable();
            _fireAction?.Enable();
            _resetAction?.Enable();
            if (_aimAction != null) { _aimAction.performed += OnAimPerformed; _aimAction.canceled += OnAimCanceled; }
            if (_fireAction != null) _fireAction.performed += OnFirePerformed;
            if (_resetAction != null) _resetAction.performed += OnResetPerformed;
        }
#endif
    }

    private void UnbindInput()
    {
#if ENABLE_INPUT_SYSTEM
        if (_aimAction != null) { _aimAction.performed -= OnAimPerformed; _aimAction.canceled -= OnAimCanceled; }
        if (_fireAction != null) _fireAction.performed -= OnFirePerformed;
        if (_resetAction != null) _resetAction.performed -= OnResetPerformed;
#endif
    }

    private void ResetAimState()
    {
        bool wasAiming = isAiming;
        isAiming = false;
        if (IsOwnerSpawned)
        {
            AimVisual = false;
            if (wasAiming && tpc) tpc.IsAiming = false;
        }
        SnapSkillLayerWeight(0f);
        if (crosshair) crosshair.enabled = false;
        HideSnapRing();
        OnAimStopped();
        SnapItemScale(false);
    }

#if ENABLE_INPUT_SYSTEM
    private void OnAimPerformed(InputAction.CallbackContext _)
    {
        // ป้องกัน: ถ้า component ถูกปิดโดย PlayerAbilityManager ไม่ให้เริ่ม aim
        if (!enabled) return;
        StartAim();
    }

    private void OnAimCanceled(InputAction.CallbackContext _) => StopAim();

    private void OnFirePerformed(InputAction.CallbackContext _)
    {
        if (CanUseAbility() && !(tpc && tpc.IsLedgeActive)) OnFire();
    }

    private void OnResetPerformed(InputAction.CallbackContext _)
    {
        if (CanUseAbility()) OnReset();
    }
#endif

    // ---------- Aim ----------

    private void StartAim()
    {
        if (!enabled || !CanUseAbility()) return;
        // ป้องกันเปิด crosshair ขณะ pause menu เปิดอยู่ · ห้อยขอบ = มือจับขอบอยู่
        if (tpc && (tpc.MovementLocked || tpc.IsLedgeActive)) return;
        isAiming = true;
        AimVisual = true;
        if (tpc) tpc.IsAiming = true;
        ApplyCrosshairStyle();
        if (crosshair) crosshair.enabled = true;
    }

    protected void StopAim()
    {
        isAiming = false;
        if (IsOwnerSpawned) AimVisual = false;
        if (tpc) tpc.IsAiming = false;
        if (crosshair) crosshair.enabled = false;
        OnAimStopped();
        SetCrosshairLocked(false);
        ResetReticle();
    }

    // ---------- Aim ray + reticle snap ----------

    private bool UseSnap => snapWithMouse || InputDeviceTracker.UsingGamepad;

    private Vector2 ScreenCenter => playerCam
        ? new Vector2(playerCam.pixelRect.center.x, playerCam.pixelRect.center.y)
        : new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);

    /// <summary>ตำแหน่ง crosshair บนจอตอนนี้ (pixel) — ไม่ได้เล็ง/ใช้เมาส์ = กลางจอ</summary>
    protected Vector2 ReticleScreenPoint => _reticleActive ? _reticleScreen : ScreenCenter;

    /// <summary>เป้าที่ crosshair เกาะอยู่ (null = ไม่ได้เกาะ)</summary>
    protected AimAssistTarget SnapTarget => _snapTarget;

    /// <summary>
    /// เส้นเล็งจากกล้องผ่านตำแหน่ง crosshair (กลางจอ หรือจุดที่เกาะเป้าอยู่) · startNear = เลื่อนจุดเริ่มไปข้างหน้าถึงระดับจุดนี้ (ปลายปืน)
    /// กันเส้นไปโดนของที่อยู่ระหว่างกล้องกับตัวละคร (เสา/กำแพงข้างหลัง)
    /// </summary>
    protected Ray AimRay(Vector3? startNear = null)
    {
        Ray ray = playerCam.ScreenPointToRay(ReticleScreenPoint);
        if (startNear.HasValue)
        {
            float along = Vector3.Dot(startNear.Value - ray.origin, ray.direction);
            if (along > 0f) ray.origin += ray.direction * along;
        }
        return ray;
    }

    /// <summary>ยิงเส้นเล็ง (ไม่นับ trigger)</summary>
    protected static bool RaycastAim(Ray ray, float maxDistance, int mask, out RaycastHit hit)
        => Physics.Raycast(ray, out hit, maxDistance, mask, QueryTriggerInteraction.Ignore);

    /// <summary>จุดที่เส้นเล็งโดน — ไม่โดนอะไร = จุดที่ระยะ maxDistance</summary>
    protected Vector3 FindAimPoint(Vector3 startNear, float maxDistance, int mask)
    {
        Ray ray = AimRay(startNear);
        return RaycastAim(ray, maxDistance, mask, out var hit) ? hit.point : ray.GetPoint(maxDistance);
    }

    /// <summary>
    /// แบบ It Takes Two: กล้องไม่หมุนเอง แต่ crosshair เลื่อนไปเกาะเป้าที่ยิงได้ซึ่งอยู่ใกล้กลางจอ
    /// เลือกตัวที่ใกล้กลางจอที่สุด (บนจอ) ที่มองเห็นจริง + ในระยะยิง · ตัวที่เกาะอยู่หลุดยากกว่า (snapReleaseMultiplier)
    /// ระหว่างนี้ทาวงแหวนบนเป้าทุกตัวที่ยิงได้และมองเห็น
    /// </summary>
    private void UpdateReticle()
    {
        Vector2 center = ScreenCenter;
        if (!_reticleActive)
        {
            _reticleActive = true;
            _reticleScreen = center;
            _reticleOffset = Vector2.zero;
            _reticleVelocity = Vector2.zero;
            SetLateReticleHook(true);
        }

        AimAssistTarget best = null;
        Vector2 bestScreen = center;
        _visibleTargets.Clear();

        if (UseSnap && _snapAccepts != null)
        {
            Ray centerRay = playerCam.ScreenPointToRay(center);
            Vector3 eye = playerCam.transform.position;
            float range = SnapRange;
            float pxPerUnit = Mathf.Max(1f, playerCam.pixelHeight);
            float bestScore = float.MaxValue;
            float radius = InputDeviceTracker.UsingGamepad ? snapRadius : mouseSnapRadius;

            // เส้นกลางจอโดนเป้าอยู่แล้ว = เป้านั้นเกาะได้เสมอ ไม่สนระยะดูด
            // (คริสตัลใหญ่เกาะกลางเป้า — จุดกลางอาจห่างกลางจอเกิน snapRadius ทั้งที่เล็งโดนตัวมันอยู่)
            AimAssistTarget direct = null;
            if (Physics.Raycast(centerRay, out var centerHit, range, _sightMask, QueryTriggerInteraction.Ignore))
                direct = centerHit.collider.GetComponentInParent<AimAssistTarget>();

            foreach (var t in AimAssistTarget.All)
            {
                if (!t || !_snapAccepts(t)) continue;
                if (!t.TryGetAimPoint(centerRay, out var p)) continue;

                Vector3 toP = p - eye;
                float dist = toP.magnitude;
                if (dist > range || dist < 0.3f) continue;

                Vector3 sp = playerCam.WorldToScreenPoint(p);
                if (sp.z <= 0f || !playerCam.pixelRect.Contains(sp)) continue;

                // มองเห็นจริง: เส้นจากกล้องต้องโดนเป้านี้ก่อนอย่างอื่น (เป้าหลังกำแพงไม่โชว์/ไม่เกาะ)
                if (!Physics.Raycast(eye, toP / dist, out var los, dist + 0.5f, _sightMask, QueryTriggerInteraction.Ignore)) continue;
                if (!t.Owns(los.collider)) continue;

                Vector2 screen = sp;
                _visibleTargets.Add(t);

                float d = Vector2.Distance(screen, center) / pxPerUnit;
                float limit = t == _snapTarget ? radius * snapReleaseMultiplier : radius;
                if (d > limit && t != direct) continue;

                // ตัวที่เส้นกลางจอโดนอยู่ชนะเสมอ · ตัวที่เกาะอยู่ได้เปรียบนิดหน่อย — สองเป้าใกล้กัน crosshair ไม่สลับไปมา
                float score = t == direct ? -1f : t == _snapTarget ? d * 0.7f : d;
                if (score >= bestScore) continue;
                bestScore = score;
                best = t;
                bestScreen = screen;
            }
        }

        Vector2 goal = best != null ? bestScreen : center;
        if (best != _snapTarget)
        {
            if (best != null) PopCrosshair();
            _snapTarget = best;
            // เปลี่ยนเป้า = ไหลต่อจากจุดที่ crosshair อยู่ตอนนี้
            _reticleOffset = _reticleScreen - goal;
        }

        if (!UseSnap)
        {
            // เมาส์ (ไม่เกาะ): crosshair กลางจอนิ่งๆ แบบเดิม
            _reticleOffset = Vector2.zero;
            _reticleVelocity = Vector2.zero;
        }
        else
        {
            // ไหลเข้าหาเป้าลื่นๆ แล้วติดเป้าไปเลย (เดิมไล่ตำแหน่งบนจอ = ตามหลังเป้าตอนหมุนกล้อง) · สไลด์ไหลเร็วขึ้นเท่าตัว
            float smooth = tpc != null && tpc.IsSliding ? snapSmoothTime * 0.5f : snapSmoothTime;
            _reticleOffset = Vector2.SmoothDamp(_reticleOffset, Vector2.zero, ref _reticleVelocity, smooth, Mathf.Infinity, Time.unscaledDeltaTime);
        }
        // ตำแหน่งนี้ใช้ยิงเส้นเล็งเฟรมนี้ (กล้องเดียวกับที่ฉายเป้า) — ภาพบนจอวางใหม่ใน PlaceReticleVisuals หลังกล้องขยับ
        _reticleScreen = goal + _reticleOffset;
        ApplyCrosshairStyle();
        ApplyCrosshairColor();
    }

    /// <summary>
    /// วาง crosshair + วงแหวนบนจอ หลัง CinemachineBrain ขยับกล้องเฟรมนี้แล้ว (Canvas.willRenderCanvases = หลัง LateUpdate ก่อนสร้าง UI)
    /// เดิมวางใน Update ด้วยกล้องเฟรมก่อน → ช้า 1 เฟรม เป็นเงาลากหางตอนสะบัดเมาส์ (จอยหมุนช้าเลยไม่ค่อยเห็น)
    /// </summary>
    private void PlaceReticleVisuals()
    {
        if (!isAiming || !_reticleActive || !playerCam) return;

        Vector2 center = ScreenCenter;
        Ray centerRay = playerCam.ScreenPointToRay(center);
        Vector2 goal = center;

        _markers?.Begin();
        foreach (var t in _visibleTargets)
        {
            if (!t || !t.TryGetAimPoint(centerRay, out var p)) continue;
            Vector3 sp = playerCam.WorldToScreenPoint(p);
            if (sp.z <= 0f) continue;
            _markers?.Show(t, sp);
            if (t == _snapTarget) goal = sp;
        }
        _markers?.End(_snapTarget);

        _reticleScreen = goal + _reticleOffset;
        PlaceCrosshair(_reticleScreen - center);
        UpdateSnapRing();
    }

    /// <summary>วงกลมขอบนอก = ลูกของ crosshair ยืดเต็มขนาด (ตามขนาด/เด้ง/ตำแหน่งของ crosshair เอง) — สร้างตอนรัน ไม่ต้องแก้ HUD</summary>
    private void CreateSnapRing()
    {
        if (_snapRing || !snapRingSprite || !crosshair) return;
        var go = new GameObject("Crosshair Snap Ring", typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(crosshair.rectTransform, false);
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        _snapRing = go.AddComponent<Image>();
        _snapRing.sprite = snapRingSprite;
        _snapRing.raycastTarget = false;
        _snapRing.enabled = false;
    }

    /// <summary>เกาะเป้า = วงหดเข้าจากวงใหญ่ + จางเข้า · หลุดเป้า = ขยายออก + จางหาย · สีตาม crosshair (ทองตอนเกาะ)</summary>
    private void UpdateSnapRing()
    {
        if (!_snapRing) return;
        bool show = _snapTarget != null && crosshair && crosshair.enabled;
        _snapRingAmount = Mathf.MoveTowards(_snapRingAmount, show ? 1f : 0f, Time.unscaledDeltaTime / snapRingFadeTime);
        if (_snapRingAmount <= 0f)
        {
            _snapRing.enabled = false;
            return;
        }
        float eased = 1f - (1f - _snapRingAmount) * (1f - _snapRingAmount);
        _snapRing.rectTransform.localScale = Vector3.one * Mathf.Lerp(snapRingStartScale, 1f, eased);
        var c = crosshair.color;
        c.a *= _snapRingAmount;
        _snapRing.color = c;
        _snapRing.enabled = true;
    }

    /// <summary>Image ลูกไม่ซ่อนตาม crosshair.enabled — ต้องปิดเองทุกที่ที่ซ่อน crosshair</summary>
    private void HideSnapRing()
    {
        _snapRingAmount = 0f;
        if (_snapRing) _snapRing.enabled = false;
    }

    private void SetLateReticleHook(bool on)
    {
        if (on == _lateHooked) return;
        _lateHooked = on;
        if (on) Canvas.willRenderCanvases += PlaceReticleVisuals;
        else Canvas.willRenderCanvases -= PlaceReticleVisuals;
    }

    /// <summary>
    /// จุดเล็งให้ท่าก้ม/เงย (PlayerAimPose) — เส้นเดียวกับที่ยิง เริ่มที่ระดับตัวละคร (ไม่โดนกำแพงหลังตัว)
    /// ทุกอย่างยกเว้นตัวละคร (_sightMask) — ลำตัวหันไปทางที่ crosshair ชี้จริง ไม่ใช่แค่ layer ของสกิล
    /// </summary>
    private void UpdateAimPoint()
    {
        _ownerAimPoint = FindAimPoint(transform.position, aimMaxDistance, _sightMask);
        AimPoint = _ownerAimPoint;
    }

    /// <summary>จอย = รูปวงกลม + ขีด (ใหญ่กว่า) · เมาส์ = รูปเดิมใน prefab — สลับกลางการเล็งได้</summary>
    private void ApplyCrosshairStyle()
    {
        if (!crosshair || !gamepadCrosshairSprite) return;
        bool pad = UseSnap;
        if (pad == _crosshairGamepadStyle) return;
        _crosshairGamepadStyle = pad;
        crosshair.sprite = pad ? gamepadCrosshairSprite : _crosshairBaseSprite;
        crosshair.rectTransform.sizeDelta = pad ? new Vector2(gamepadCrosshairSize, gamepadCrosshairSize) : _crosshairBaseSize;
    }

    /// <summary>
    /// วาง crosshair ห่างกลางจอเท่า offset (pixel) — ใช้ anchoredPosition (crosshair anchor อยู่กลาง Canvas)
    /// ไม่แปลงผ่าน world position: offset 0 = ตำแหน่งเดิมใน prefab เป๊ะ
    /// </summary>
    private void PlaceCrosshair(Vector2 offsetFromCenter)
    {
        if (!crosshair) return;
        var canvas = crosshair.canvas;
        float scale = canvas ? Mathf.Max(0.0001f, canvas.scaleFactor) : 1f;
        crosshair.rectTransform.anchoredPosition = offsetFromCenter / scale;
    }

    private void PopCrosshair()
    {
        if (!crosshair || snapPopStrength <= 0f) return;
        _crosshairPop?.Kill();
        var rt = crosshair.rectTransform;
        rt.localScale = _crosshairBaseScale;
        _crosshairPop = rt.DOPunchScale(_crosshairBaseScale * snapPopStrength, 0.2f, 6, 0.6f)
            .SetUpdate(true).SetId(gameObject).SetTarget(transform);
        UISfx.Play(UISound.Hover);
    }

    /// <summary>เลิกเล็ง/ปิดสกิล — crosshair กลับกลางจอทันที + ซ่อนวงแหวน</summary>
    private void ResetReticle()
    {
        SetLateReticleHook(false);
        HideSnapRing();
        _snapTarget = null;
        _reticleActive = false;
        _reticleOffset = Vector2.zero;
        _reticleVelocity = Vector2.zero;
        _visibleTargets.Clear();
        _crosshairPop?.Kill();
        _crosshairPop = null;
        if (crosshair)
        {
            crosshair.rectTransform.anchoredPosition = Vector2.zero;
            crosshair.rectTransform.localScale = _crosshairBaseScale;
        }
        _markers?.HideAll();
        ApplyCrosshairColor();
    }

    /// <summary>crosshair เปลี่ยนสีตอนเล็งติดเป้า (คลาสลูกบอกว่าเส้นเล็งโดนเป้า — เกาะเป้าอยู่ก็นับ)</summary>
    protected void SetCrosshairLocked(bool locked)
    {
        _crosshairLocked = locked;
        ApplyCrosshairColor();
    }

    private void ApplyCrosshairColor()
    {
        bool locked = _crosshairLocked || _snapTarget != null;
        if (locked == _crosshairColorLocked || !crosshair) return;
        _crosshairColorLocked = locked;
        crosshair.color = locked ? crosshairLockedColor : _crosshairBaseColor;
    }

    /// <summary>ตัดการเล็งทันที (ไม่ lerp) — ใช้ตอนรีเซ็ตสกิล</summary>
    protected void CancelAimImmediate()
    {
        StopAim();
        SnapSkillLayerWeight(0f);
        SnapItemScale(false);
    }

    // ---------- Skill Layer ----------

    private bool IsSkillLayerValid()
    {
        if (skillAnimator == null) return false;
        return skillLayerIndex >= 0 && skillLayerIndex < skillAnimator.layerCount;
    }

    protected void SnapSkillLayerWeight(float weight01)
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

    // ---------- Animator triggers ----------

    protected void PlaySkillActivateTrigger() => SetAnimTrigger(AnimSlot.Skill, skillActivateTriggerParam);

    /// <summary>เล่น trigger เอง แล้วส่งให้เครื่องอื่นเล่นตาม (เจ้าของเท่านั้น)</summary>
    protected void SetAnimTrigger(AnimSlot slot, string param)
    {
        if (string.IsNullOrEmpty(param)) return;
        int hash = Animator.StringToHash(param);
        var anim = AnimatorFor(slot);
        if (anim) anim.SetTrigger(hash);
        if (IsOwnerSpawned) RPC_AnimTrigger((byte)slot, hash);
    }

    [Rpc(RpcSources.StateAuthority, RpcTargets.Proxies, TickAligned = false)]
    private void RPC_AnimTrigger(byte slot, int hash)
    {
        var anim = AnimatorFor((AnimSlot)slot);
        if (anim) anim.SetTrigger(hash);
    }

    private Animator AnimatorFor(AnimSlot slot) => slot == AnimSlot.Skill ? skillAnimator : ItemAnimator;

    // ---------- Shoot SFX ----------

    /// <summary>เล่นเสียงยิงเอง แล้วส่งให้เครื่องอื่น (คนยิงไม่ได้ยินซ้ำ)</summary>
    protected void PlayShootSfx(Vector3 position)
    {
        if (!IsOwnerSpawned) return;
        if (shootSound == null || shootVolume <= 0f) return;
        if (Time.time - _lastShootSfxTime < ShootSfxDebounce) return;
        _lastShootSfxTime = Time.time;

        PlayShootSfxLocal(position);
        RPC_PlayShootSfx(position);
    }

    [Rpc(RpcSources.StateAuthority, RpcTargets.Proxies, TickAligned = false)]
    private void RPC_PlayShootSfx(Vector3 position) => PlayShootSfxLocal(position);

    private void PlayShootSfxLocal(Vector3 position)
    {
        if (shootSound == null || shootVolume <= 0f || shootAudioSource == null) return;

        if (sfxMixerGroup != null) shootAudioSource.outputAudioMixerGroup = sfxMixerGroup;
        shootAudioSource.transform.position = position;
        shootAudioSource.volume = Mathf.Clamp01(shootVolume);
        shootAudioSource.pitch = 1f;
        shootAudioSource.PlayOneShot(shootSound);
    }

    // ---------- Held item scale ----------

    private void CacheItemBaseScale()
    {
        if (!heldItem || _itemBaseCached) return;
        _itemBaseScale = heldItem.localScale.sqrMagnitude > 1e-8f ? heldItem.localScale : Vector3.one;
        _itemBaseCached = true;
    }

    private void ApplyItemLocalScaleKeepingPivot(Vector3 newLocalScale)
    {
        if (!heldItem) return;
        if (heldItemScalePivotLocal.sqrMagnitude <= 1e-10f)
        {
            heldItem.localScale = newLocalScale;
            return;
        }

        Vector3 wsPivotBefore = heldItem.TransformPoint(heldItemScalePivotLocal);
        heldItem.localScale = newLocalScale;
        Vector3 wsPivotAfter = heldItem.TransformPoint(heldItemScalePivotLocal);
        heldItem.position += wsPivotBefore - wsPivotAfter;
    }

    protected void SnapItemScale(bool fullSize)
    {
        if (!heldItem) return;
        CacheItemBaseScale();
        ApplyItemLocalScaleKeepingPivot(fullSize ? _itemBaseScale : Vector3.zero);
    }

    private void UpdateItemScale()
    {
        if (!heldItem) return;
        CacheItemBaseScale();
        var target = AimVisualActive ? _itemBaseScale : Vector3.zero;
        var lerped = Vector3.Lerp(heldItem.localScale, target, Time.deltaTime * heldItemScaleLerp);
        ApplyItemLocalScaleKeepingPivot(lerped);
    }
}
