using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DG.Tweening;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.UI;
using UnityEngine.Rendering;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

[RequireComponent(typeof(NetworkObject))]
public class SwitchWind : NetworkBehaviour
{
    // ... (ส่วน TargetEntry, Enums, Hold 'E', Player Locking, Detect Area, Filter ... ไม่ต้องแก้) ...
    [Serializable]
    public class TargetEntry
    {
        public MonoBehaviour activatableComponent; // IWindModeActivatable / IActivatable
        public bool allow = true;
        public bool invert;
    }

    public enum PressMode { Toggle, ForceOn, ForceOff }
    private enum DetectionShape { Box, Sphere, Capsule, Mesh }

    [Header("Hold 'E' to Activate (กด E ค้าง)")]
    [SerializeField] private bool useHoldInteraction = true;

    [Header("Hold/Release Modes (กำหนดตรง ๆ)")]
    [SerializeField] private WindMode modeWhenReleased = WindMode.Pull; // เริ่ม/ปล่อย = Pull
    [SerializeField] private WindMode modeWhenHolding = WindMode.Push;  // ค้าง = Push

    [Header("Player Locking (เมื่อกด E ค้าง)")]
    [SerializeField] private string playerControllerTypeName = "ThirdPersonController_Rigidbody";

    [Header("Detect Area")]
    [SerializeField] private Transform pivot;
    [SerializeField] private Vector3 detectPosition = Vector3.zero;
    [SerializeField] private DetectionShape detectionShape = DetectionShape.Box;
    [SerializeField] private Vector3 boxSize = Vector3.one;
    [SerializeField] private float sphereRadius = 0.5f;
    [SerializeField] private float capsuleRadius = 0.5f;
    [SerializeField] private float capsuleHeight = 2f;
    [SerializeField] private Mesh meshPreview;

    [Header("Filter")]
    [SerializeField] private LayerMask detectLayers = ~0;
    [SerializeField] private string requiredTag = "Player";
    
    // ... (ส่วน Lever / Visual, Press-E Mode, Direct Targets, Manual On/Off ... ไม่ต้องแก้) ...
    [Header("Lever / Visual")]
    [SerializeField] private SwitchRotator modelRotator;
    [SerializeField, Min(0f)] private float activationDelay = 0.5f; // ใช้ตอน "ปล่อย" เท่านั้น

    [Header("Press-E Mode (Tap)")]
    [SerializeField] private PressMode pressMode = PressMode.Toggle;

    [Header("Direct Targets")]
    [SerializeField] private List<TargetEntry> directTargets = new();

    [Header("Manual On/Off (กด E เปิด/ปิด, ไม่ใช้ Manager)")]
    [SerializeField] private bool useManualOnOff;
    [SerializeField] private bool manualStartOn;
    [SerializeField] private WindMode manualOnMode = WindMode.Push;

    [Header("Input / UI")]
    [SerializeField] private GameObject idleIndicatorUI;
    [SerializeField] private GameObject promptUI;
    [Tooltip("ความเร็วในการทำอนิเมชั่น Fade UI (ยังใช้กับ Cancel UI)")]
    [SerializeField] private float uiFadeDuration = 0.2f;
    [Tooltip("ความเร็วในการทำอนิเมชั่น Scale UI (Idle / Prompt)")]
    [SerializeField] private float uiScaleDuration = 0.35f;
    [Tooltip("ค่า Overshoot ของ DOScale (ยิ่งสูงยิ่งดึ้ง)")]
    [SerializeField] private float uiScaleOvershoot = 1.7f;

    [Header("Cancel UI (สำหรับโหมด Hold ค้างแล้วปล่อยได้)")]
    [SerializeField] private GameObject cancelPromptUI;
    [SerializeField] private KeyCode cancelKey = KeyCode.Q;
#if ENABLE_INPUT_SYSTEM
    [SerializeField] private InputActionReference cancelAction;
#endif

#if ENABLE_INPUT_SYSTEM
    [SerializeField] private InputActionReference interactAction;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
    [SerializeField] private KeyCode fallbackKey = KeyCode.E;
#endif

    [Header("Anti-spam & One-shot")]
    [SerializeField] private float pressCooldown = 0.5f; // เพิ่มจาก 0.25f เป็น 0.5f
    [SerializeField] private bool disableAfterFire;

    [Header("Audio")]
    [Tooltip("Audio Source สำหรับตำแหน่งเกิดเสียง (กำหนดใน Inspector)")]
    [SerializeField] private AudioSource audioSource;
    [Tooltip("Audio Clip ที่จะเล่นเมื่อสวิตช์ทำงาน")]
    [SerializeField] private AudioClip activationSound;
    [Tooltip("Audio Mixer Group สำหรับส่ง Output (ถ้าต้องการ)")]
    [SerializeField] private AudioMixerGroup outputMixerGroup;


    // ================== [จุดแก้ไขที่ 1: เปลี่ยนแปลง Field] ==================
    [Header("Wind Manager (สำหรับกด E Tap)")]
    [Tooltip("ติ๊กถ้าต้องการให้สวิตช์นี้ (แบบ Tap) สั่งการ Manager ภายนอก")]
    [SerializeField] private bool useWindManager = true; 
    
    [Tooltip("ลาก 'GameObject' ที่มีสคริปต์ WindGroupManager หรือ WindModeSwitcher มาใส่ที่นี่")]
    [SerializeField] private GameObject windManagerGameObject; // <-- เปลี่ยนเป็น GameObject
    // ====================================================================

    [Header("Broadcast to IWindModeActivatable")]
    [SerializeField] private bool alsoBroadcastWindMode = true;

    [Header("Hold Progress (กด E ค้างให้เต็มก่อนทำงาน)")]
    [SerializeField] private bool useHoldProgress = true;
    
    [SerializeField, Min(0.05f)] private float holdTimeToActivate = 1.2f;
    [SerializeField] private Image holdProgressFill;
    [SerializeField] private Color holdProgressColor = Color.white;
    [SerializeField] private bool resetProgressIfExit = true;

    // -------- Runtime --------
    private Behaviour _localPlayerControllerComponent;
    private bool _isLocalPlayerHolding = false;
    private float _lastPressLocal = -999f;
    private float _lastPressServer = -999f;
    private bool _localInside;
    private bool _currentEnabledState;
    private float _holdTimer = 0f;
    private bool _armedThisHold = false;
    
    // เพิ่มตัวแปรป้องกัน spam และ race condition
    private bool _isProcessingActivation = false;
    private Coroutine _currentActivationCoroutine;
    // private bool _isFullyActivated = false; // ยกเลิกตัวแปร Local
    private readonly NetworkVariable<bool> _isFullyActivatedNV = 
        new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // ================== [จุดแก้ไขที่ 2: เพิ่มตัวแปร Interface] ==================
    private ISwitchableWindManager _switchableManager;
    // =======================================================================

    private readonly NetworkVariable<bool> _firedNV =
        new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private const ulong NO_HOLDER = ulong.MaxValue;
    private readonly NetworkVariable<ulong> _holderClientIdNV =
        new(NO_HOLDER, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<bool> _manualPowerOnNV =
        new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    
    // สำหรับให้ disable จากภายนอก (เช่น จาก SwitchWindSelector)
    private readonly NetworkVariable<bool> _externallyDisabledNV =
        new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private CanvasGroup _idleCG;
    private CanvasGroup _promptCG;
    private CanvasGroup _cancelCG;
    private Vector3 _idleOriginalScale;
    private Vector3 _promptOriginalScale;

    // ===== Unity Lifecycle =====
    private void Awake()
    {
        if (!pivot) pivot = transform;
        InitializeUI();
        InitializeAudio();

        // ================== [จุดแก้ไขที่ 3: เปลี่ยน Logic ใน Awake] ==================
        // ค้นหา Interface จาก GameObject ที่ลากมา
        if (windManagerGameObject != null)
        {
            _switchableManager = windManagerGameObject.GetComponent<ISwitchableWindManager>();
            
            if (_switchableManager == null)
            {
                Debug.LogError($"[SwitchWind] The GameObject '{windManagerGameObject.name}' on 'Wind Manager Game Object' field does not have a component that implements ISwitchableWindManager! (เช่น WindGroupManager หรือ WindModeSwitcher)", this);
            }
        }
        else if (useWindManager)
        {
             // ถ้าติ๊ก useWindManager แต่ลืมลากใส่ (จะ LogWarning ตอนกด)
        }
        // =========================================================================
    }

    // ... (OnNetworkSpawn, OnEnable, OnDisable, Update, LateUpdate ไม่ต้องแก้) ...
    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        InitializeManualPower();
        _manualPowerOnNV.OnValueChanged += OnManualPowerChanged;
        _externallyDisabledNV.OnValueChanged += OnExternallyDisabledChanged;
        _manualPowerOnNV.OnValueChanged += OnManualPowerChanged;
        _externallyDisabledNV.OnValueChanged += OnExternallyDisabledChanged;
        _isFullyActivatedNV.OnValueChanged += OnFullyActivatedChanged;
        _holderClientIdNV.OnValueChanged += OnHolderChanged;

        // ถ้าเคยถูก disable จากภายนอกแล้วให้ซ่อน UI
        if (_externallyDisabledNV.Value)
        {
            HideAllUI();
        }
        else
        {
            // เช็คสถานะเริ่มต้น
            if (_isFullyActivatedNV.Value)
            {
                UpdateUIVisuals(IsClientIdInsideArea(NetworkManager.Singleton.LocalClientId));
            }
        }

        if (IsServer && useHoldInteraction && alsoBroadcastWindMode)
        {
            BroadcastWindMode(modeWhenReleased);
        }
    }
    
    private void OnEnable()
    {
        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
#if ENABLE_INPUT_SYSTEM
#if ENABLE_INPUT_SYSTEM
        if (interactAction?.action != null) interactAction.action.Enable();
        if (cancelAction?.action != null) cancelAction.action.Enable();
#endif
#endif
    }

    private void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
#if ENABLE_INPUT_SYSTEM
#if ENABLE_INPUT_SYSTEM
        if (interactAction?.action != null) interactAction.action.Disable();
        if (cancelAction?.action != null) cancelAction.action.Disable();
#endif
#endif
        _manualPowerOnNV.OnValueChanged -= OnManualPowerChanged;
        _externallyDisabledNV.OnValueChanged -= OnExternallyDisabledChanged;
        _manualPowerOnNV.OnValueChanged -= OnManualPowerChanged;
        _externallyDisabledNV.OnValueChanged -= OnExternallyDisabledChanged;
        _isFullyActivatedNV.OnValueChanged -= OnFullyActivatedChanged;
        _holderClientIdNV.OnValueChanged -= OnHolderChanged;
    }
    
    private void Update()
    {
        // เช็คถ้าถูก disable จากภายนอก
        if (_externallyDisabledNV.Value)
        {
            if ((idleIndicatorUI != null && idleIndicatorUI.activeSelf) || (promptUI != null && promptUI.activeSelf))
                HideAllUI();
            return;
        }
        
        if (disableAfterFire && _firedNV.Value)
        {
            if ((idleIndicatorUI != null && idleIndicatorUI.activeSelf) || (promptUI != null && promptUI.activeSelf))
                HideAllUI();
            return;
        }

        if (!IsClient) return;

        bool isInsideNow = IsClientIdInsideArea(NetworkManager.Singleton.LocalClientId);
        UpdateLocalProximity(isInsideNow);

        if (isInsideNow)
        {
            if (useHoldInteraction) HandleKeyHoldInput();
            else HandleKeyTapInput();
        }
        else if (_isLocalPlayerHolding)
        {
            _isLocalPlayerHolding = false;
            LockLocalPlayerMovement(false);

            // ถ้าอยู่ในสถานะ Fully Activated ต้องส่ง cancel ผ่าน RPC ที่ถูกต้อง
            // ไม่งั้น RequestHoldServerRpc(false) จะถูก return เพราะ _isFullyActivatedNV == true
            if (_isFullyActivatedNV.Value)
            {
                RequestFullyActivateServerRpc(false);
            }
            else
            {
                RequestHoldServerRpc(false);
            }

            if (useHoldProgress && resetProgressIfExit) ResetHoldProgressUI();
        }
    }

    private Camera _mainCamera;

    private void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
    {
        if (!IsClient) return;
        if (_mainCamera == null) _mainCamera = Camera.main;
        if (camera == _mainCamera && _mainCamera != null)
        {
            BillboardUI();
        }
    }

    private void LateUpdate()
    {
        if (IsServer && useHoldInteraction && _holderClientIdNV.Value != NO_HOLDER)
        {
            // เช็คว่า Player ยังอยู่ใน Area หรือไม่
            if (!IsClientIdInsideArea(_holderClientIdNV.Value))
            {
                // ถ้ากำลัง Fully Activated อยู่ ต้อง reset ก่อน release
                // ไม่งั้นสวิชจะค้างเป็น Fully Activated ตลอดไป
                if (_isFullyActivatedNV.Value)
                {
                    _isFullyActivatedNV.Value = false;
                }
                ReleaseHold_Server();
            }
        }
    }

    private void BillboardUI()
    {
        if (_mainCamera == null) _mainCamera = Camera.main;
        if (_mainCamera == null) return;
        
        Quaternion camRot = _mainCamera.transform.rotation;
        if (idleIndicatorUI != null && idleIndicatorUI.activeSelf) idleIndicatorUI.transform.rotation = camRot;
        if (promptUI != null && promptUI.activeSelf) promptUI.transform.rotation = camRot;
        if (cancelPromptUI != null && cancelPromptUI.activeSelf) cancelPromptUI.transform.rotation = camRot;
    }

    // ... (UpdateLocalProximity, HandleKeyHoldInput, Was...Pressed/Released, InitializeUI, UpdateUIVisuals, HideAllUI ... ไม่ต้องแก้) ...
    private void UpdateLocalProximity(bool isInsideNow)
    {
        if (isInsideNow != _localInside)
        {
            _localInside = isInsideNow;
            UpdateUIVisuals(_localInside);

            if (isInsideNow)
            {
                FindAndCachePlayerController();
            }
            else
            {
                if (_isLocalPlayerHolding) LockLocalPlayerMovement(false);
                _localPlayerControllerComponent = null;
                _isLocalPlayerHolding = false;

                if (useHoldProgress && resetProgressIfExit) ResetHoldProgressUI();
            }
        }
    }
    
    private void HandleKeyHoldInput()
    {
        if (_localPlayerControllerComponent == null)
        {
            if (!FindAndCachePlayerController()) return;
        }

        // 1. ถ้ายังไม่ได้เริ่มกด (และยังไม่ Fully Activated)
        if (WasInteractPressedThisFrame() && !_isLocalPlayerHolding && !_isFullyActivatedNV.Value)
        {
            _isLocalPlayerHolding = true;
            LockLocalPlayerMovement(true);

            if (useHoldProgress && holdProgressFill != null)
            {
                _holdTimer = 0f;
                _armedThisHold = false;
                SetHoldProgress01(0f);
                ShowHoldProgress(true);
            }
            else
            {
                RequestHoldServerRpc(true);
                _armedThisHold = true;
            }
        }

        // 2. ถ้ากำลังกดค้างอยู่ (หรือ Fully Activated แล้ว)
        if (_isLocalPlayerHolding)
        {
            // 2.1 กรณี Fully Activated แล้ว -> รอรับ Input Cancel (Q)
            // เช็คจาก NV แทน Local
            if (_isFullyActivatedNV.Value)
            {
                // ปล่อยปุ่ม E ได้แล้ว ไม่ต้องเช็ค WasInteractReleasedThisFrame เพื่อยกเลิก
                // แต่ต้องเช็คปุ่ม Cancel
                if (WasCancelPressedThisFrame())
                {
                    // ยกเลิก
                    RequestFullyActivateServerRpc(false); // ส่ง RPC ยกเลิก
                    
                    _isLocalPlayerHolding = false;
                    LockLocalPlayerMovement(false);
                    ResetHoldProgressUI();
                }
                return; 
            }

            // 2.2 กรณีใช้ Hold Progress และยังไม่เต็ม
            if (useHoldProgress && holdProgressFill != null && !_armedThisHold)
            {
                _holdTimer += Time.deltaTime;
                float pct = _holdTimer / Mathf.Max(0.0001f, holdTimeToActivate);
                SetHoldProgress01(pct);

                if (pct >= 1f)
                {
                    SetHoldProgress01(1f);
                    _armedThisHold = true;
                    
                    // เข้าสู่สถานะ Fully Activated (ส่ง RPC)
                    RequestFullyActivateServerRpc(true);
                    
                    // อัปเดต UI: ซ่อน Progress (Cancel UI จะถูกจัดการผ่าน OnValueChanged ของ NV)
                    ShowHoldProgress(false); 
                }
            }

            // 2.3 ถ้าปล่อยปุ่ม E ก่อนที่จะเต็ม (เฉพาะตอนที่ยังไม่ Fully Activated)
            if (!_isFullyActivatedNV.Value && WasInteractReleasedThisFrame())
            {
                if (_armedThisHold)
                {
                    // ถ้า armed แล้วแต่ยังไม่ Fully Activated (กรณีไม่ใช้ Hold Progress หรือ Logic อื่น)
                    // แต่ในที่นี้ถ้าใช้ Hold Progress มันจะส่ง RequestFullyActivateServerRpc(true) ไปแล้ว
                    // ดังนั้นส่วนนี้อาจจะไม่ค่อยได้ใช้ถ้า logic 2.2 ทำงานถูกต้อง
                    // แต่เผื่อไว้สำหรับโหมดปกติ
                    RequestHoldServerRpc(false);
                }

                _isLocalPlayerHolding = false;
                LockLocalPlayerMovement(false);

                if (useHoldProgress && holdProgressFill != null)
                {
                    ResetHoldProgressUI(); 
                }
            }
        }
    }
    
    private bool WasInteractPressedThisFrame()
    {
#if ENABLE_INPUT_SYSTEM
        if (interactAction?.action != null) return interactAction.action.WasPressedThisFrame();
        return Keyboard.current?.eKey.wasPressedThisFrame ?? false;
#elif ENABLE_LEGACY_INPUT_MANAGER
        return Input.GetKeyDown(fallbackKey);
#else
        return false;
#endif
    }

    private bool WasInteractReleasedThisFrame()
    {
#if ENABLE_INPUT_SYSTEM
        if (interactAction?.action != null) return interactAction.action.WasReleasedThisFrame();
        return Keyboard.current?.eKey.wasReleasedThisFrame ?? false;
#elif ENABLE_LEGACY_INPUT_MANAGER
        return Input.GetKeyUp(fallbackKey);
#else
        return false;
#endif
    }

    private bool WasCancelPressedThisFrame()
    {
#if ENABLE_INPUT_SYSTEM
        if (cancelAction?.action != null) return cancelAction.action.WasPressedThisFrame();
        return Keyboard.current?.qKey.wasPressedThisFrame ?? false;
#else
        return Input.GetKeyDown(cancelKey);
#endif
    }
    
    private void InitializeUI()
    {
        // Idle Indicator: เริ่มต้นซ่อน (scale = 0), โผล่เฉพาะเมื่อเข้าโซน
        if (idleIndicatorUI != null)
        {
            _idleCG = idleIndicatorUI.GetComponent<CanvasGroup>();
            if (_idleCG == null) _idleCG = idleIndicatorUI.AddComponent<CanvasGroup>();
            _idleCG.alpha = 1f;
            _idleOriginalScale = idleIndicatorUI.transform.localScale;
            idleIndicatorUI.transform.localScale = Vector3.zero;
            idleIndicatorUI.SetActive(false);
        }

        // Prompt: เริ่มต้นซ่อน (scale = 0)
        if (promptUI != null)
        {
            _promptCG = promptUI.GetComponent<CanvasGroup>();
            if (_promptCG == null) _promptCG = promptUI.AddComponent<CanvasGroup>();
            _promptCG.alpha = 1f;
            _promptOriginalScale = promptUI.transform.localScale;
            promptUI.transform.localScale = Vector3.zero;
            promptUI.SetActive(false);
        }

        if (cancelPromptUI != null)
        {
            _cancelCG = cancelPromptUI.GetComponent<CanvasGroup>();
            if (_cancelCG == null) _cancelCG = cancelPromptUI.AddComponent<CanvasGroup>();
            _cancelCG.alpha = 0f;
            cancelPromptUI.SetActive(false);
        }

        if (holdProgressFill != null)
        {
            holdProgressFill.fillAmount = 0f;
            holdProgressFill.color = holdProgressColor;
            holdProgressFill.gameObject.SetActive(false);
        }
    }

    private void InitializeAudio()
    {
        if (audioSource != null)
        {
            audioSource.playOnAwake = false;
            if (outputMixerGroup != null)
            {
                audioSource.outputAudioMixerGroup = outputMixerGroup;
            }
        }
    }

    /// <summary>
    /// แสดง GameObject ด้วย DOScale bounce-in (Ease.OutBack)
    /// </summary>
    private void ScaleShow(GameObject go, Vector3 targetScale)
    {
        if (go == null) return;
        go.transform.DOKill();
        go.SetActive(true);
        go.transform.DOScale(targetScale, uiScaleDuration)
            .SetEase(Ease.OutBack, uiScaleOvershoot);
    }

    /// <summary>
    /// ซ่อน GameObject ด้วย DOScale bounce-out (Ease.InBack)
    /// </summary>
    private void ScaleHide(GameObject go)
    {
        if (go == null) return;
        go.transform.DOKill();
        go.transform.DOScale(Vector3.zero, uiScaleDuration)
            .SetEase(Ease.InBack, uiScaleOvershoot)
            .OnComplete(() => go.SetActive(false));
    }

    private void UpdateUIVisuals(bool isInside)
    {
        _cancelCG?.DOKill();

        if (isInside)
        {
            // ถ้า Fully Activated ให้โชว์ Cancel UI แทน Prompt UI
            if (_isFullyActivatedNV.Value)
            {
                // Hide Idle & Prompt
                ScaleHide(idleIndicatorUI);
                ScaleHide(promptUI);
                
                // Show Cancel ONLY if local player is the holder
                bool isOwner = _holderClientIdNV.Value == NetworkManager.Singleton.LocalClientId;
                if (_cancelCG != null)
                {
                    if (isOwner)
                    {
                        cancelPromptUI.SetActive(true);
                        _cancelCG.DOFade(1f, uiFadeDuration);
                    }
                    else
                    {
                        _cancelCG.DOFade(0f, uiFadeDuration).OnComplete(() => cancelPromptUI.SetActive(false));
                    }
                }
            }
            else
            {
                // Available: Show Prompt, Hide Idle & Cancel
                ScaleShow(promptUI, _promptOriginalScale);
                ScaleHide(idleIndicatorUI);
                if (_cancelCG != null) _cancelCG.DOFade(0f, uiFadeDuration).OnComplete(() => cancelPromptUI.SetActive(false));
            }
        }
        else
        {
            // ออกจากโซน: ซ่อนทุกอย่างด้วย Scale bounce-out
            ScaleHide(idleIndicatorUI);
            ScaleHide(promptUI);
            if (_cancelCG != null) _cancelCG.DOFade(0f, uiFadeDuration).OnComplete(() => cancelPromptUI.SetActive(false));
        }
    }

    private void HideAllUI()
    {
        if (idleIndicatorUI != null)
        {
            idleIndicatorUI.transform.DOKill();
            idleIndicatorUI.transform.localScale = Vector3.zero;
            idleIndicatorUI.SetActive(false);
        }
        if (promptUI != null)
        {
            promptUI.transform.DOKill();
            promptUI.transform.localScale = Vector3.zero;
            promptUI.SetActive(false);
        }
        if (cancelPromptUI) cancelPromptUI.SetActive(false);
        if (holdProgressFill != null) holdProgressFill.gameObject.SetActive(false);
    }
    
    // ... (Player Lock (Reflection) ... ไม่ต้องแก้) ...
    private bool FindAndCachePlayerController()
    {
        if (string.IsNullOrWhiteSpace(playerControllerTypeName)) return false;
        var playerObject = NetworkManager.Singleton?.LocalClient?.PlayerObject;
        if (playerObject == null) return false;

        var controllerType = FindTypeByName(playerControllerTypeName);
        if (controllerType != null)
        {
            _localPlayerControllerComponent = playerObject.GetComponent(controllerType) as Behaviour;
            return _localPlayerControllerComponent != null;
        }

        Debug.LogWarning($"[SwitchWind] Cannot find Component '{playerControllerTypeName}'.", this);
        return false;
    }

    private void LockLocalPlayerMovement(bool isLocked)
    {
        if (_localPlayerControllerComponent == null) return;
        if (!TrySetMovementLocked(_localPlayerControllerComponent, isLocked))
        {
            _localPlayerControllerComponent.enabled = !isLocked;
        }
    }

    private static bool TrySetMovementLocked(Behaviour behaviour, bool isLocked)
    {
        if (behaviour == null) return false;
        var type = behaviour.GetType();

        var method = type.GetMethod("SetMovementLocked", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (method != null && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == typeof(bool))
        {
            method.Invoke(behaviour, new object[] { isLocked });
            return true;
        }

        var property = type.GetProperty("MovementLocked", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property != null && property.CanWrite && property.PropertyType == typeof(bool))
        {
            property.SetValue(behaviour, isLocked);
            return true;
        }

        var field = type.GetField("MovementLocked", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null && field.FieldType == typeof(bool))
        {
            field.SetValue(behaviour, isLocked);
            return true;
        }

        return false;
    }

    private static Type FindTypeByName(string typeName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var type = assembly.GetTypes().FirstOrDefault(t => t.Name == typeName);
                if (type != null) return type;
            }
            catch { }
        }
        return null;
    }


    // ================== [จุดแก้ไขที่ 4: เปลี่ยน Logic การเช็ค] ==================
    private void HandleKeyTapInput()
    {
        // เพิ่มการเช็คว่ากำลัง process อยู่หรือไม่
        if (_isProcessingActivation) return;
        
        if (Time.time - _lastPressLocal > pressCooldown && WasInteractPressedThisFrame())
        {
            _lastPressLocal = Time.time;
            
            // ถ้าติ๊ก Manual On/Off "และ" ไม่ได้ใช้ Manager -> ใช้โหมด Manual
            if (useManualOnOff && !useWindManager)
            {
                ToggleManualPowerServerRpc();
            }
            // ถ้าติ๊กใช้ Manager
            else if (useWindManager)
            {
                // (เช็ค Interface ตอน Awake ไปแล้ว)
                if (_switchableManager != null)
                {
                    // ถ้าถูกต้อง -> ส่ง Request
                    RequestPressServerRpc();
                }
                else
                {
                    // ถ้าลืมลากใส่ หรือลากมาผิด (จะ Log Error ใน Awake แล้ว)
                    // เราจะ LogWarning ที่นี่อีกทีตอนพยายามกด
                    Debug.LogWarning($"[SwitchWind] 'Use Wind Manager' is ticked but 'Wind Manager Game Object' is missing or invalid.", this);
                }
            }
        }
    }
    // ======================================================================

    // ===== Network =====

    [ServerRpc(RequireOwnership = false)]
    private void RequestPressServerRpc(ServerRpcParams rpc = default)
    {
        if (useHoldInteraction) return;
        
        // เช็คถ้าถูก disable จากภายนอก
        if (_externallyDisabledNV.Value) return;
        
        // เพิ่มการเช็คว่ากำลัง process อยู่หรือไม่
        if (_isProcessingActivation) return;
        
        // ================== [จุดแก้ไขที่ 5: เปลี่ยน Logic การเช็ค] ==================
        // ถ้า "ไม่" ใช้ Manual Mode "และ" ติ๊กใช้ Manager
        if (!useManualOnOff && useWindManager)
        {
             if (_switchableManager == null) 
             {
                // ป้องกันอีกชั้น ถ้าสคริปต์บน Server หาสิ่งนี้ไม่เจอ
                Debug.LogError($"[SwitchWind SERVER] RequestPressServerRpc failed: _switchableManager is null.", this);
                return; 
             }
        }
        else
        {
            return; // ถ้าเป็น Manual Mode ให้ออก
        }
        // ======================================================================

        if (disableAfterFire && _firedNV.Value) return;
        if (Time.time - _lastPressServer < pressCooldown) return;

        if (IsClientIdInsideArea(rpc.Receive.SenderClientId))
        {
            _lastPressServer = Time.time;
            FireOnce_Server();
        }
    }

    [ServerRpc(RequireOwnership = false)]
    private void ToggleManualPowerServerRpc(ServerRpcParams rpc = default)
    {
        // เช็คถ้าถูก disable จากภายนอก
        if (_externallyDisabledNV.Value) return;
        
        // ================== [จุดแก้ไขที่ 6: เปลี่ยน Logic การเช็ค] ==================
        // ถ้า "ไม่" ใช้ Manual Mode "หรือ" "ดัน" ไปใช้ Manager ด้วย -> ให้ออก
        if (!useManualOnOff || useWindManager) return;
        // ======================================================================

        if (!IsClientIdInsideArea(rpc.Receive.SenderClientId)) return;

        _manualPowerOnNV.Value = !_manualPowerOnNV.Value;

        PlayLeverClientRpc();
        PlayLeverLocal();
        PlayActivationSoundClientRpc();

        WindMode mode = _manualPowerOnNV.Value ? manualOnMode : WindMode.Disabled;
        BroadcastWindMode(mode);
    }
    
    // ... (RequestHoldServerRpc, RPCs, OnManualPowerChanged ... ไม่ต้องแก้) ...
    [ServerRpc(RequireOwnership = false)]
    private void RequestFullyActivateServerRpc(bool active, ServerRpcParams rpc = default)
    {
        if (!useHoldInteraction) return;
        if (_externallyDisabledNV.Value) return;

        _isFullyActivatedNV.Value = active;

        if (active)
        {
            // เริ่มทำงาน (เหมือนกดค้าง)
            ulong senderId = rpc.Receive.SenderClientId;
            _holderClientIdNV.Value = senderId; // บันทึกว่าใครเป็นคนเปิด
            
            SetPressedClientRpc(true);
            ApplyHoldInvert_Server(true);
        }
        else
        {
            // ยกเลิก
            ReleaseHold_Server();
        }
    }

    [ServerRpc(RequireOwnership = false)]
    private void RequestHoldServerRpc(bool wantHold, ServerRpcParams rpc = default)
    {
        // ถ้าเป็นโหมด Hold Progress แบบใหม่ เราจะใช้ RequestFullyActivateServerRpc แทนเมื่อเต็ม
        // แต่ถ้ายังไม่เต็ม หรือเป็นโหมดเก่า ก็ใช้ logic นี้
        if (!useHoldInteraction) return;
        
        // เช็คถ้าถูก disable จากภายนอก
        if (_externallyDisabledNV.Value) return;
        
        ulong senderId = rpc.Receive.SenderClientId;

        if (wantHold)
        {
            if (_holderClientIdNV.Value != NO_HOLDER) return;
            if (!IsClientIdInsideArea(senderId)) return;

            _holderClientIdNV.Value = senderId;
            SetPressedClientRpc(true);
            ApplyHoldInvert_Server(true);
        }
        else
        {
            if (_holderClientIdNV.Value != senderId) return;
            
            // ถ้า Fully Activated อยู่ ห้ามปล่อยผ่าน RPC นี้ (ต้องใช้ RequestFullyActivateServerRpc(false) เท่านั้น)
            if (_isFullyActivatedNV.Value) return;

            ReleaseHold_Server();
        }
    }

    [ClientRpc] private void PlayLeverClientRpc() { if (!IsServer) PlayLeverLocal(); }
    [ClientRpc] private void ResetLeverClientRpc(float delay) { ResetLeverLocal(delay); }
    [ClientRpc] private void ResetLeverAfterCooldownClientRpc(float delay) { ResetLeverLocal(delay); }
    [ClientRpc] private void HidePromptAllClientRpc() => HideAllUI();
    [ClientRpc] private void SetPressedClientRpc(bool pressed) { modelRotator?.SetPressed(pressed); }
    [ClientRpc] private void ApplyWindModeClientRpc(WindMode mode) => ApplyWindModeToTargets(mode);
    [ClientRpc] private void PlayActivationSoundClientRpc() => PlayActivationSound();

    private void OnManualPowerChanged(bool prev, bool next)
    {
        if (!IsServer)
        {
            WindMode mode = next ? manualOnMode : WindMode.Disabled;
            ApplyWindModeToTargets(mode);
        }
    }

    private void OnExternallyDisabledChanged(bool prev, bool next)
    {
        if (next)
        {
            // ซ่อน UI เมื่อถูก disable จากภายนอก
            HideAllUI();
        }
    }

    private void OnFullyActivatedChanged(bool prev, bool next)
    {
        // อัปเดต UI เมื่อสถานะ Fully Activated เปลี่ยน
        UpdateUIVisuals(_localInside);
        
        if (!next)
        {
            // ถ้าถูกยกเลิก (เช่น โดยคนอื่น หรือ Server)
            if (_isLocalPlayerHolding)
            {
                _isLocalPlayerHolding = false;
                LockLocalPlayerMovement(false);
                ResetHoldProgressUI();
            }
        }
    }

    private void OnHolderChanged(ulong prev, ulong next)
    {
        // อัปเดต UI เมื่อ Holder เปลี่ยน (เพื่อเช็คความเป็นเจ้าของสำหรับ Cancel UI)
        UpdateUIVisuals(_localInside);
    }

    // ===== Core actions =====
    private void FireOnce_Server()
    {
        // ป้องกัน race condition
        if (_isProcessingActivation) return;
        _isProcessingActivation = true;
        
        bool nextState = ComputeNextState(); // (คำนวณ nextState สำหรับโหมด Direct/Manual)

        PlayLeverClientRpc();
        PlayLeverLocal();
        PlayActivationSoundClientRpc();

        // หมุนคันโยกกลับหลัง pressCooldown หมด
        ResetLeverAfterCooldownClientRpc(pressCooldown);

        // ================== [จุดแก้ไขที่ 7: เปลี่ยน Logic การเช็ค] ==================
        // ถ้าติ๊กใช้ Manager "และ" Manager นั้นถูกต้อง
        if (useWindManager && _switchableManager != null)
        {
            // เรียก Manager ผ่าน Interface
            _switchableManager.Server_OnSwitchPressed(activationDelay);
            
            // รีเซ็ต flag หลังจากเรียก Manager เสร็จ
            StartCoroutine(ResetProcessingFlagAfterDelay(activationDelay + 0.1f));
        }
        else
        {
            // ถ้าไม่ใช้ Manager ก็ให้ทำงานแบบเดิม (ควบคุม Direct Targets)
            // หยุด coroutine เดิมถ้ามี
            if (_currentActivationCoroutine != null)
            {
                StopCoroutine(_currentActivationCoroutine);
            }
            _currentActivationCoroutine = StartCoroutine(ActivateAfterDelayCo(nextState));
        }
        // ======================================================================
    }

    // ... (ReleaseHold_Server, ApplyHoldInvert_Server ... ไม่ต้องแก้) ...
    private void ReleaseHold_Server()
    {
        // Reset Fully Activated ด้วยเสมอ เพื่อป้องกันสวิชค้าง
        if (_isFullyActivatedNV.Value)
        {
            _isFullyActivatedNV.Value = false;
        }
        _holderClientIdNV.Value = NO_HOLDER;
        SetPressedClientRpc(false);
        ApplyHoldInvert_Server(false);
    }
    
    private void ApplyHoldInvert_Server(bool isHolding)
    {
        if (isHolding)
        {
            PlayActivationSoundClientRpc();
            BroadcastWindMode(modeWhenHolding);
            return;
        }

        if (activationDelay > 0f)
        {
            StartCoroutine(ApplyHoldAfterDelayCo(modeWhenReleased, activationDelay));
        }
        else
        {
            PlayActivationSoundClientRpc();
            BroadcastWindMode(modeWhenReleased);
        }
    }


    private IEnumerator ActivateAfterDelayCo(bool nextState)
    {
        yield return new WaitForSeconds(activationDelay);
        ApplyToDirectTargets(nextState);

        if (alsoBroadcastWindMode)
        {
            WindMode mode = nextState ? WindMode.Push : WindMode.Pull;
            BroadcastWindMode(mode);
        }

        // ================== [จุดแก้ไขที่ 8: ย้าย Logic] ==================
        if (pressMode != PressMode.Toggle)
        {
            _firedNV.Value = true; 
            HidePromptAllClientRpc();
            enabled = false;
        }
        // ==================================================================
        
        // รีเซ็ต processing flag
        _isProcessingActivation = false;
        _currentActivationCoroutine = null;
    }
    
    private IEnumerator ApplyHoldAfterDelayCo(WindMode target, float delay)
    {
        yield return new WaitForSeconds(delay);
        BroadcastWindMode(target);
    }
    
    private IEnumerator ResetProcessingFlagAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        _isProcessingActivation = false;
    }

    private void InitializeManualPower()
    {
        // ================== [จุดแก้ไขที่ 9: เพิ่มเช็ค] ==================
        if (IsServer && useManualOnOff && !useWindManager)
        // =============================================================
        {
            _manualPowerOnNV.Value = manualStartOn;
            WindMode mode = _manualPowerOnNV.Value ? manualOnMode : WindMode.Disabled;
            BroadcastWindMode(mode);
        }
    }
    
    // ... (ที่เหลือทั้งหมด: ComputeNextState, ApplyToDirectTargets, ApplyWindModeToTargets, ...
    // ... BroadcastWindMode, IsClientIdInsideArea, GetOverlaps, PlayLeverLocal, ...
    // ... Gizmos, Hold Progress Utilities ... ทั้งหมดนี้ "ไม่ต้องแก้ไข") ...
    
    private bool ComputeNextState()
    {
        switch (pressMode)
        {
            case PressMode.Toggle:   _currentEnabledState = !_currentEnabledState; return _currentEnabledState;
            case PressMode.ForceOn:  _currentEnabledState = true;   return true;
            case PressMode.ForceOff: _currentEnabledState = false;  return false;
            default: return true;
        }
    }

    private void ApplyToDirectTargets(bool on)
    {
        foreach (var t in directTargets)
        {
            if (!t.allow || t.activatableComponent == null) continue;
            if (t.activatableComponent is IActivatable activatable)
                activatable.Activate(t.invert ? !on : on);
        }
    }

    private void ApplyWindModeToTargets(WindMode mode)
    {
        foreach (var t in directTargets)
        {
            if (!t.allow || t.activatableComponent == null) continue;
            if (t.activatableComponent is IWindModeActivatable windActivatable)
                windActivatable.SetWindMode(mode);
        }
    }

    private void BroadcastWindMode(WindMode mode)
    {
        if (!alsoBroadcastWindMode) return;
        ApplyWindModeToTargets(mode);     // Server/Host
        ApplyWindModeClientRpc(mode);     // Clients
    }

    private bool IsClientIdInsideArea(ulong clientId)
    {
        var hits = GetOverlaps();
        if (hits == null || hits.Length == 0) return false;
        foreach (var c in hits)
        {
            if (!string.IsNullOrEmpty(requiredTag) && !c.CompareTag(requiredTag)) continue;
            var nob = c.GetComponentInParent<NetworkObject>();
            if (nob != null && nob.IsPlayerObject && nob.OwnerClientId == clientId)
                return true;
        }
        return false;
    }

    private Collider[] GetOverlaps()
    {
        Vector3 worldPos = pivot ? pivot.TransformPoint(detectPosition) : transform.TransformPoint(detectPosition);
        Quaternion worldRot = pivot ? pivot.rotation : transform.rotation;

        switch (detectionShape)
        {
            case DetectionShape.Box:
                return Physics.OverlapBox(worldPos, boxSize * 0.5f, worldRot, detectLayers);
            case DetectionShape.Sphere:
                return Physics.OverlapSphere(worldPos, sphereRadius, detectLayers);
            case DetectionShape.Capsule:
                float halfHeight = Mathf.Max(0, (capsuleHeight * 0.5f) - capsuleRadius);
                Vector3 up = pivot ? pivot.up : Vector3.up;
                Vector3 p1 = worldPos + up * halfHeight;
                Vector3 p2 = worldPos - up * halfHeight;
                return Physics.OverlapCapsule(p1, p2, capsuleRadius, detectLayers);
            default:
                return Array.Empty<Collider>();
        }
    }

    public void PlayLeverLocal() => modelRotator?.PlayOneShot();
    public void ResetLeverLocal(float delay = 0f) => modelRotator?.ResetToBaseAnimated(delay);

    private void PlayActivationSound()
    {
        if (audioSource != null && activationSound != null)
        {
            audioSource.clip = activationSound;
            audioSource.Play();
        }
    }

    // ===== Gizmos =====
    private void OnDrawGizmos()
    {
        if (!pivot) pivot = transform;
        Gizmos.color = new Color(0f, 1f, 0f, 0.25f);
        Gizmos.matrix = Matrix4x4.TRS(pivot.position, pivot.rotation, Vector3.one);
        switch (detectionShape)
        {
            case DetectionShape.Box:
                Gizmos.DrawCube(detectPosition, boxSize);
                Gizmos.DrawWireCube(detectPosition, boxSize);
                break;
            case DetectionShape.Sphere:
                Gizmos.DrawSphere(detectPosition, sphereRadius);
                Gizmos.DrawWireSphere(detectPosition, sphereRadius);
                break;
            case DetectionShape.Capsule:
                float hh = Mathf.Max(0, (capsuleHeight * 0.5f) - capsuleRadius);
                Vector3 up = Vector3.up * hh;
                Vector3 p1 = detectPosition + up;
                Vector3 p2 = detectPosition - up;
                Gizmos.DrawWireSphere(p1, capsuleRadius);
                Gizmos.DrawWireSphere(p2, capsuleRadius);
                Gizmos.DrawLine(p1 + Vector3.forward * capsuleRadius, p2 + Vector3.forward * capsuleRadius);
                Gizmos.DrawLine(p1 - Vector3.forward * capsuleRadius, p2 - Vector3.forward * capsuleRadius);
                Gizmos.DrawLine(p1 + Vector3.right * capsuleRadius, p2 + Vector3.right * capsuleRadius);
                Gizmos.DrawLine(p1 - Vector3.right * capsuleRadius, p2 - Vector3.right * capsuleRadius);
                break;
            case DetectionShape.Mesh:
                if (meshPreview)
                {
                    Gizmos.DrawMesh(meshPreview, detectPosition);
                    Gizmos.DrawWireMesh(meshPreview, detectPosition);
                }
                break;
        }
    }

    // ---------- NEW: Hold Progress Utilities ----------
    private void ShowHoldProgress(bool show)
    {
        if (holdProgressFill == null) return;
        holdProgressFill.gameObject.SetActive(show);
    }

    private void SetHoldProgress01(float v01)
    {
        if (holdProgressFill == null) return;
        holdProgressFill.fillAmount = Mathf.Clamp01(v01);
    }

    private void ResetHoldProgressUI()
    {
        _holdTimer = 0f;
        _armedThisHold = false;
        // _isFullyActivated = false; // ไม่ต้อง reset local แล้ว เพราะใช้ NV
        SetHoldProgress01(0f);
        ShowHoldProgress(false);
        UpdateUIVisuals(_localInside); 
    }

    // ===== Public API สำหรับ disable จากภายนอก =====
    /// <summary>
    /// (SERVER-ONLY) ตั้งค่าสถานะ disable จากภายนอก (เช่น จาก SwitchWindSelector)
    /// เมื่อ disable แล้วสวิตช์จะไม่รับ input และซ่อน UI
    /// </summary>
    public void Server_SetExternallyDisabled(bool disabled)
    {
        if (!IsServer) return;
        _externallyDisabledNV.Value = disabled;
        
        if (disabled)
        {
            // ถ้ากำลังกดค้างอยู่ ให้ปล่อย
            if (_holderClientIdNV.Value != NO_HOLDER)
            {
                // ถ้า Fully Activated อยู่ ก็ต้องปิดด้วย
                if (_isFullyActivatedNV.Value)
                {
                    _isFullyActivatedNV.Value = false;
                }
                ReleaseHold_Server();
            }
            // ซ่อน UI
            HidePromptAllClientRpc();
        }
    }
}
