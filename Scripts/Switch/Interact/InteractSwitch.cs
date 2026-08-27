using System;
using System.Collections;
using System.Collections.Generic;
using DG.Tweening;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Rendering;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// ตัวนับรวมแบบส่วนกลาง (Server-only) ต่อ IActivatable แต่ละตัว
/// สำหรับโหมด Trigger Hold: เปิดถ้า count>0 (มีแผ่นใดแผ่นหนึ่งเหยียบ)
/// </summary>
public static class PlateHoldAggregator
{
    private static readonly Dictionary<int, int> _pressCounts = new();

    /// <summary>
    /// pressed=true => +1, pressed=false => -1 (ไม่น้อยกว่า 0)
    /// คืนค่าสถานะรวมปัจจุบัน (count>0)
    /// </summary>
    public static bool AdjustAndGetState(MonoBehaviour activatableComponent, bool pressed)
    {
        if (activatableComponent == null) return false;

        int key = activatableComponent.GetInstanceID();
        _pressCounts.TryGetValue(key, out int count);
        count = pressed ? count + 1 : Mathf.Max(0, count - 1);

        if (count <= 0)
        {
            _pressCounts.Remove(key);
            return false;
        }
        _pressCounts[key] = count;
        return true;
    }

    /// <summary>
    /// ใช้ตอนสวิตช์ถูกปิด/ทำลายขณะยัง pressed เพื่อคืนตัวนับ 1 ครั้ง
    /// </summary>
    public static bool ForceReleaseOne(MonoBehaviour activatableComponent)
    {
        if (activatableComponent == null) return false;
        int key = activatableComponent.GetInstanceID();
        if (!_pressCounts.TryGetValue(key, out int count) || count <= 0)
        {
            _pressCounts.Remove(key);
            return false;
        }
        count = Mathf.Max(0, count - 1);
        if (count == 0)
        {
            _pressCounts.Remove(key);
            return false;
        }
        _pressCounts[key] = count;
        return true; // ยังมีคนกดอยู่
    }
}

/// <summary>
/// สวิตช์ที่ผู้เล่นสามารถโต้ตอบได้ผ่านการกดปุ่ม (Press-E) หรือการเหยียบค้าง (Trigger-Hold)
/// ควบคุมเป้าหมายที่ implement IActivatable และแสดงผล UI แบบสองสถานะ
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class InteractSwitch : NetworkBehaviour
{
    #region Inspector Fields & Enums
    [Serializable]
    public class TargetEntry
    {
        [Tooltip("คอมโพเนนต์ที่ implement IActivatable")]
        public MonoBehaviour activatableComponent;
        public bool allow = true;
        public bool invert;
    }

    public enum PressMode { Toggle, ForceOn, ForceOff }
    private enum DetectionShape { Box, Sphere, Capsule, Mesh }

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

    [Header("Lever / Visual")]
    [SerializeField] private SwitchRotator modelRotator;
    [SerializeField, Min(0f)] private float activationDelay = 0.5f;

    [Header("Mode (กด E)")]
    [SerializeField] private PressMode pressMode = PressMode.ForceOn;
    [Tooltip("สำหรับ ForceOn เท่านั้น: ติ๊กเพื่อให้สวิตช์ไม่ปิดหลังทำงาน สามารถใช้ซ้ำได้")]
    [SerializeField] private bool forceOnAllowReuse = false;

    [Header("Targets (ถ้าไม่ใช้ Manager)")]
    [SerializeField] private List<TargetEntry> targets = new();

    [Header("Input / UI")]
    [Tooltip("UI ที่แสดงเมื่อผู้เล่นยังไม่อยู่ในโซน (เช่น วงกลม)")]
    [SerializeField] private GameObject idleIndicatorUI;
    [Tooltip("UI ที่แสดงเมื่อผู้เล่นเข้ามาในโซนแล้ว (เช่น 'กด E')")]
    [SerializeField] private GameObject promptUI;
    [Tooltip("ความเร็วในการทำอนิเมชั่น Fade UI (ยังใช้กับ Cancel UI)")]
    [SerializeField] private float uiFadeDuration = 0.2f;
    [Tooltip("ความเร็วในการทำอนิเมชั่น Scale UI (Idle / Prompt)")]
    [SerializeField] private float uiScaleDuration = 0.35f;
    [Tooltip("ค่า Overshoot ของ DOScale (ยิ่งสูงยิ่งดึ้ง)")]
    [SerializeField] private float uiScaleOvershoot = 1.7f;

    [Header("Cancel UI (สำหรับ Central Manager)")]
    [SerializeField] private GameObject cancelPromptUI;
    [SerializeField] private KeyCode cancelKey = KeyCode.Q;
#if ENABLE_INPUT_SYSTEM
    [SerializeField] private InputActionReference cancelAction;
#endif

#if ENABLE_INPUT_SYSTEM
    [Tooltip("ใส่ InputActionReference (ถ้าไม่ใส่ จะ fallback เป็นคีย์บอร์ด E)")]
    [SerializeField] private InputActionReference interactAction;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
    [SerializeField] private KeyCode fallbackKey = KeyCode.E;
#endif

    [Header("Anti-spam & One-shot")]
    [SerializeField] private float pressCooldown = 0.25f;
    [SerializeField] private bool disableAfterFire = true;

    [Header("Audio")]
    [Tooltip("Audio Source สำหรับตำแหน่งเกิดเสียง (กำหนดใน Inspector)")]
    [SerializeField] private AudioSource audioSource;
    [Tooltip("Audio Clip ที่จะเล่นเมื่อสวิตช์ทำงาน")]
    [SerializeField] private AudioClip activationSound;
    [Tooltip("Audio Mixer Group สำหรับส่ง Output (ถ้าต้องการ)")]
    [SerializeField] private AudioMixerGroup outputMixerGroup;

    [Header("Central Manager (กดพร้อมกัน)")]
    [SerializeField] private bool useCentralManager;
    [SerializeField] private int switchGroupId = -1;
    [SerializeField] private CentralSwitchManager centralManager;
    [SerializeField] private bool playLeverOnlyOnGroupSuccess;

    [Header("Trigger Hold (เหยียบค้าง)")]
    [Tooltip("เปิดใช้โหมดเหยียบค้าง ปุ่มจะยุบ/โผล่และสั่งเปิด/ปิดเป้าหมายอัตโนมัติ")]
    [SerializeField] private bool useTriggerHold;
    [SerializeField, Min(0.01f)] private float triggerCheckInterval = 0.05f;
    [Tooltip("ดีบาวน์ตอนออกจากแผ่นเหยียบ (กันเด้ง ๆ)")]
    [SerializeField, Min(0f)] private float triggerExitGrace = 0.1f;
    [Tooltip("เล่นแอนิเมชันคันโยกทุกครั้งที่สถานะเหยียบเปลี่ยน")]
    [SerializeField] private bool triggerPlayLeverOnChange = true;
    #endregion

    #region Public Properties
    public bool Fired => _firedNV.Value;
    public ulong NetId => NetworkObject.NetworkObjectId;
    #endregion

    #region Runtime State
    private float _lastPressLocal = -999f;
    private float _lastPressServer = -999f;
    private bool _localInside;
    private bool _currentEnabledState;
    private Coroutine _activateCo;
    private readonly NetworkVariable<bool> _firedNV = new(writePerm: NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<float> _lastPressTimeNV = new(-999f, writePerm: NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<ulong> _holderClientIdNV = new(ulong.MaxValue, writePerm: NetworkVariableWritePermission.Server); // Track who is waiting

    // Trigger-hold state
    private bool _isServerPressed;
    private float _lastInsideSeenTime = -999f;
    private Coroutine _serverPollCo;

    // UI state
    private CanvasGroup _idleCG;
    private CanvasGroup _promptCG;
    private CanvasGroup _cancelCG;
    private Vector3 _idleOriginalScale;
    private Vector3 _promptOriginalScale;
    private float _localCooldownStart = -999f;  // เวลา local ที่บันทึกตอนได้รับ press event
    #endregion

    #region Unity Lifecycle
    private void Awake()
    {
        InitializeUI();
        InitializeAudio();
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        _holderClientIdNV.OnValueChanged += OnHolderChanged;
        _lastPressTimeNV.OnValueChanged += OnPressTimeChanged;

        if (useTriggerHold && IsServer && _serverPollCo == null)
        {
            _serverPollCo = StartCoroutine(ServerPollTriggerHoldCo());
        }
    }

    private void OnEnable()
    {
        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
    #if ENABLE_INPUT_SYSTEM
        if (interactAction?.action != null) interactAction.action.Enable();
        if (cancelAction?.action != null) cancelAction.action.Enable();
    #endif
    }

    private void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
    #if ENABLE_INPUT_SYSTEM
        if (interactAction?.action != null) interactAction.action.Disable();
        if (cancelAction?.action != null) cancelAction.action.Disable();
    #endif
        _holderClientIdNV.OnValueChanged -= OnHolderChanged;
        _lastPressTimeNV.OnValueChanged -= OnPressTimeChanged;
        if (_serverPollCo != null)
        {
            StopCoroutine(_serverPollCo);
            _serverPollCo = null;
        }

        // ถ้าเป็น TriggerHold และสวิตช์ถูกปิดขณะยัง pressed: คืนตัวนับรวม (Server เท่านั้น)
        if (useTriggerHold && IsServer && _isServerPressed)
        {
            Server_AggregatedApply(pressed:false, immediate:true);
            _isServerPressed = false;
        }
    }

    private Camera _mainCamera;

    private void Update()
    {
        if (IsClient)
        {
            HandleClientSideLogic();
        }
    }

    private void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
    {
        if (!IsClient) return;
        if (_mainCamera == null) _mainCamera = Camera.main;
        if (camera == _mainCamera && _mainCamera != null)
        {
            BillboardUI();
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

    private void OnValidate()
    {
        if (useCentralManager && centralManager == null)
        {
            centralManager = FindObjectOfType<CentralSwitchManager>();
        }
    }
    #endregion

    #region Client-Side Logic
    private void HandleClientSideLogic()
    {
        if (useTriggerHold)
        {
            // โหมดเหยียบค้างตัดสินบน Server เท่านั้น
            return;
        }

        // เช็ค disableAfterFire แต่ถ้าเป็น ForceOn และ forceOnAllowReuse = true ให้ข้าม
        bool shouldDisable = disableAfterFire && !(pressMode == PressMode.ForceOn && forceOnAllowReuse);
        if (shouldDisable && Fired)
        {
            if ((idleIndicatorUI != null && idleIndicatorUI.activeSelf) || (promptUI != null && promptUI.activeSelf))
            {
                HideAllUI();
            }
            return;
        }

        bool isInsideNow = IsClientIdInsideArea(NetworkManager.Singleton.LocalClientId);
        bool isInCooldown = Time.time - _localCooldownStart <= pressCooldown;
        UpdateLocalProximity(isInsideNow, isInCooldown);
        HandlePressEInput(isInsideNow);
    }

    private void OnPressTimeChanged(float prev, float next)
    {
        _localCooldownStart = Time.time;
    }

    private void OnHolderChanged(ulong prev, ulong next)
    {
        // Update UI when holder changes
        UpdateUIVisuals(_localInside);
    }

    private void UpdateLocalProximity(bool isInsideNow, bool isInCooldown = false)
    {
        if (isInsideNow != _localInside)
        {
            _localInside = isInsideNow;
            // ถ้าอยู่ใน cooldown ให้ปิด UI ทั้งหมด
            if (isInCooldown)
            {
                HideAllUI();
            }
            else
            {
                UpdateUIVisuals(_localInside);
            }
        }
        // ถ้าอยู่ข้างในแต่ติด cooldown ให้ปิด UI
        else if (_localInside && isInCooldown)
        {
            HideAllUI();
        }
        // ถ้าอยู่ข้างในและไม่ติด cooldown แล้ว ให้แสดง UI ตามปกติ
        else if (_localInside && !isInCooldown)
        {
            UpdateUIVisuals(_localInside);
        }
    }

    private void HandlePressEInput(bool isInsideNow)
    {
        ulong holderId = _holderClientIdNV.Value;
        ulong localId = NetworkManager.Singleton.LocalClientId;

        // Check if we are the holder (Waiting state)
        if (holderId == localId)
        {
            // We are waiting. Check for Cancel input.
            if (WasCancelPressed())
            {
                // Send same RequestPressServerRpc. 
                // CentralManager will detect that we are already in the list and treat it as Cancel.
                RequestPressServerRpc();
            }
            return; // Don't allow pressing E again while waiting
        }

        // Check if someone else is holding it
        if (holderId != ulong.MaxValue && holderId != localId)
        {
            // Switch is busy/held by someone else. Block input.
            return;
        }

        if (isInsideNow && Time.time - _lastPressLocal > pressCooldown)
        {
            if (WasInteractPressed())
            {
                _lastPressLocal = Time.time;
                RequestPressServerRpc();
            }
        }
    }

    private bool WasInteractPressed()
    {
    #if ENABLE_INPUT_SYSTEM
        if (interactAction?.action != null && interactAction.action.triggered) return true;
        if (Keyboard.current?.eKey.wasPressedThisFrame == true) return true;
    #endif
    #if ENABLE_LEGACY_INPUT_MANAGER
        if (Input.GetKeyDown(fallbackKey)) return true;
    #endif
        return false;
    }

    private bool WasCancelPressed()
    {
    #if ENABLE_INPUT_SYSTEM
        if (cancelAction?.action != null && cancelAction.action.triggered) return true;
        if (Keyboard.current?.qKey.wasPressedThisFrame == true) return true;
    #endif
        return Input.GetKeyDown(cancelKey);
    }
    #endregion

    #region Server-Side Logic (Trigger Hold OR-aggregate)
    private IEnumerator ServerPollTriggerHoldCo()
    {
        while (true)
        {
            yield return new WaitForSeconds(triggerCheckInterval);

            bool anyInside = IsAnyoneInsideArea();
            if (anyInside)
            {
                _lastInsideSeenTime = Time.time;
            }
            bool debouncedInside = anyInside || (Time.time - _lastInsideSeenTime <= triggerExitGrace);

            if (debouncedInside != _isServerPressed)
            {
                _isServerPressed = debouncedInside;

                // อัปเดตคันโยกให้ทุกคนเห็น (visual)
                SetPressedClientRpc(_isServerPressed);
                if (triggerPlayLeverOnChange)
                {
                    PlayLeverClientRpc();
                    PlayActivationSoundClientRpc();
                }

                // ใช้ตัวนับรวมแทนการ ApplyToTargets แบบเดิม
                if (_activateCo != null) StopCoroutine(_activateCo);
                _activateCo = StartCoroutine(Server_AggregatedApplyAfterDelayCo(_isServerPressed));
            }
        }
    }

    private IEnumerator Server_AggregatedApplyAfterDelayCo(bool pressed)
    {
        yield return new WaitForSeconds(activationDelay);
        Server_AggregatedApply(pressed, immediate:true);
    }

    /// <summary>
    /// ปรับตัวนับรวมต่อ target ทั้งหมดของสวิตช์นี้ (Server เท่านั้น)
    /// pressed=true => +1, false => -1 แล้วสั่ง IActivatable ตามผลรวม (count>0)
    /// </summary>
    private void Server_AggregatedApply(bool pressed, bool immediate)
    {
        foreach (var t in targets)
        {
            if (!t.allow || t.activatableComponent == null) continue;

            if (t.activatableComponent is IActivatable activatable)
            {
                bool aggregatedOn = PlateHoldAggregator.AdjustAndGetState(t.activatableComponent, pressed);
                bool finalState = t.invert ? !aggregatedOn : aggregatedOn;
                activatable.Activate(finalState);
            }
            else
            {
                Debug.LogWarning($"[{name}] Target '{t.activatableComponent.name}' does not implement IActivatable.", t.activatableComponent);
            }
        }
    }
    #endregion

    #region Server-Side Logic (Press-E = พฤติกรรมเดิม)
    private void FireOnce_Server(ulong senderClientId)
    {
        bool nextState = ComputeNextState();
        // ถ้าเป็น ForceOn และ forceOnAllowReuse = true ไม่ต้องตั้ง _firedNV
        if (pressMode != PressMode.Toggle && !(pressMode == PressMode.ForceOn && forceOnAllowReuse))
        {
            _firedNV.Value = true;
        }
        bool shouldPlayLeverNow = !(useCentralManager && playLeverOnlyOnGroupSuccess);
        if (shouldPlayLeverNow)
        {
            PlayLeverClientRpc();
            PlayLeverLocal();
            PlayActivationSoundClientRpc();

            // หมุนคันโยกกลับหลัง pressCooldown หมด
            ResetLeverAfterCooldownClientRpc(pressCooldown);
        }

        // เส้นทางเดิม (Single switch / ไม่ใช้ Central Manager)
        if (_activateCo != null) StopCoroutine(_activateCo);
        _activateCo = StartCoroutine(ActivateAfterDelayCo(nextState));
    }
    #endregion

    #region Shared Logic & RPCs
    [ServerRpc(RequireOwnership = false)]
    private void RequestPressServerRpc(ServerRpcParams rpc = default)
    {
        if (useTriggerHold) return;                     // โหมดเหยียบค้างไม่ใช้เส้นทางนี้
        // เช็ค disableAfterFire แต่ถ้าเป็น ForceOn และ forceOnAllowReuse = true ให้ข้าม
        bool shouldDisable = disableAfterFire && !(pressMode == PressMode.ForceOn && forceOnAllowReuse);
        if (shouldDisable && Fired) return;
        if (Time.time - _lastPressServer < pressCooldown) return;

        ulong sender = rpc.Receive.SenderClientId;
        if (!IsClientIdInsideArea(sender)) return;

        _lastPressServer = Time.time;
        _lastPressTimeNV.Value = Time.time; // ซิงค์เวลากดให้ทุก client

        if (useCentralManager && centralManager != null && switchGroupId >= 0)
        {
            // ถ้าใช้ Central Manager (โหมดอื่น) จะไปเส้นทางนั้น
            centralManager.Server_OnSwitchPressed(
                this,
                switchGroupId,
                sender,
                ComputeNextState(),
                activationDelay,
                pressMode,
                disableAfterFire
            );

            if (!playLeverOnlyOnGroupSuccess)
            {
                PlayLeverLocal();
                PlayLeverClientRpc();
                PlayActivationSoundClientRpc();
            }
            return;
        }

        // โหมดเดี่ยว / เส้นทางเดิม
        FireOnce_Server(sender);
    }

    private IEnumerator ActivateAfterDelayCo(bool nextState)
    {
        yield return new WaitForSeconds(activationDelay);
        ApplyToTargets(nextState);
        // เช็ค disableAfterFire แต่ถ้าเป็น ForceOn และ forceOnAllowReuse = true ไม่ต้องปิดสวิตช์
        bool shouldDisable = disableAfterFire && !(pressMode == PressMode.ForceOn && forceOnAllowReuse);
        if (!useTriggerHold && shouldDisable && pressMode != PressMode.Toggle)
        {
            HidePromptAllClientRpc();
            enabled = false;
        }
    }

    private bool ComputeNextState()
    {
        switch (pressMode)
        {
            case PressMode.Toggle: _currentEnabledState = !_currentEnabledState; return _currentEnabledState;
            case PressMode.ForceOn: _currentEnabledState = true; return true;
            case PressMode.ForceOff: _currentEnabledState = false; return false;
            default: return true;
        }
    }

    private void ApplyToTargets(bool groupOn)
    {
        foreach (TargetEntry t in targets)
        {
            if (!t.allow || t.activatableComponent == null) continue;
            if (t.activatableComponent is IActivatable activatable)
            {
                bool finalState = t.invert ? !groupOn : groupOn;
                activatable.Activate(finalState);
            }
            else
            {
                Debug.LogWarning($"[{name}] Target '{t.activatableComponent.name}' does not implement IActivatable.", t.activatableComponent);
            }
        }
    }
    #endregion

    #region Visuals & UI
    private void InitializeUI()
    {
        // Idle Indicator: เริ่มต้นซ่อน (scale = 0), จะโผล่เฉพาะเมื่อเข้าโซน
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
            ulong holderId = _holderClientIdNV.Value;
            ulong localId = NetworkManager.Singleton.LocalClientId;
            bool isHolder = holderId == localId;
            bool isHeldByOther = holderId != ulong.MaxValue && !isHolder;

            if (isHolder)
            {
                // Show Cancel, Hide Idle & Prompt
                ScaleHide(idleIndicatorUI);
                ScaleHide(promptUI);

                if (_cancelCG != null)
                {
                    cancelPromptUI.SetActive(true);
                    _cancelCG.DOFade(1f, uiFadeDuration);
                }
            }
            else if (isHeldByOther)
            {
                // Held by someone else: Hide Prompt & Cancel, show Idle
                ScaleHide(promptUI);
                if (_cancelCG != null) _cancelCG.DOFade(0f, uiFadeDuration).OnComplete(() => cancelPromptUI.SetActive(false));
                ScaleShow(idleIndicatorUI, _idleOriginalScale);
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
        if (cancelPromptUI != null) cancelPromptUI.SetActive(false);
    }

    public void PlayLeverLocal() => modelRotator?.PlayOneShot();
    public void ResetLeverLocal(float delay = 0f) => modelRotator?.ResetToBaseAnimated(delay);

    /// <summary>
    /// เรียกจาก CentralSwitchManager เพื่อสั่ง reset คันโยก (Server → ทุก Client)
    /// </summary>
    public void ResetLeverNetwork()
    {
        if (!IsServer) return;
        ResetLeverLocal();
        ResetLeverClientRpc(0f);
    }

    [ClientRpc] private void PlayLeverClientRpc() { if (!IsServer) PlayLeverLocal(); }
    [ClientRpc] private void ResetLeverClientRpc(float delay) { ResetLeverLocal(delay); }
    [ClientRpc] private void ResetLeverAfterCooldownClientRpc(float delay) { ResetLeverLocal(delay); }
    [ClientRpc] private void HidePromptAllClientRpc() => HideAllUI();
    [ClientRpc] private void SetPressedClientRpc(bool pressed) => modelRotator?.SetPressed(pressed);
    [ClientRpc] private void PlayActivationSoundClientRpc() => PlayActivationSound();

    public void PlayActivationSound()
    {
        if (audioSource != null && activationSound != null)
        {
            audioSource.clip = activationSound;
            audioSource.Play();
        }
    }
    #endregion

    #region Detection & Gizmos
    private bool IsClientIdInsideArea(ulong clientId)
    {
        Collider[] hits = GetOverlaps();
        if (hits == null || hits.Length == 0) return false;
        foreach (var c in hits)
        {
            if (!string.IsNullOrEmpty(requiredTag) && !c.CompareTag(requiredTag)) continue;
            var nob = c.GetComponentInParent<NetworkObject>();
            if (nob != null && nob.IsPlayerObject && nob.OwnerClientId == clientId) return true;
        }
        return false;
    }

    private bool IsAnyoneInsideArea()
    {
        Collider[] hits = GetOverlaps();
        if (hits == null || hits.Length == 0) return false;
        foreach (var c in hits)
        {
            if (!string.IsNullOrEmpty(requiredTag) && !c.CompareTag(requiredTag)) continue;
            var nob = c.GetComponentInParent<NetworkObject>();
            if (nob != null && nob.IsPlayerObject) return true;
        }
        return false;
    }

    private Collider[] GetOverlaps()
    {
        if (!pivot) pivot = transform;
        Vector3 worldPos = pivot.TransformPoint(detectPosition);
        Quaternion worldRot = pivot.rotation;
        switch (detectionShape)
        {
            case DetectionShape.Box: return Physics.OverlapBox(worldPos, boxSize * 0.5f, worldRot, detectLayers, QueryTriggerInteraction.Collide);
            case DetectionShape.Sphere: return Physics.OverlapSphere(worldPos, sphereRadius, detectLayers, QueryTriggerInteraction.Collide);
            case DetectionShape.Capsule:
                float halfHeight = Mathf.Max(0, (capsuleHeight * 0.5f) - capsuleRadius);
                Vector3 p1 = worldPos + pivot.up * halfHeight;
                Vector3 p2 = worldPos - pivot.up * halfHeight;
                return Physics.OverlapCapsule(p1, p2, capsuleRadius, detectLayers, QueryTriggerInteraction.Collide);
            default: return Array.Empty<Collider>();
        }
    }

    private void OnDrawGizmos()
    {
        if (!pivot) pivot = transform;
        Gizmos.color = new Color(0f, 1f, 0f, 0.25f);
        Gizmos.matrix = Matrix4x4.TRS(pivot.position, pivot.rotation, Vector3.one);
        switch (detectionShape)
        {
            case DetectionShape.Box: Gizmos.DrawCube(detectPosition, boxSize); Gizmos.DrawWireCube(detectPosition, boxSize); break;
            case DetectionShape.Sphere: Gizmos.DrawSphere(detectPosition, sphereRadius); Gizmos.DrawWireSphere(detectPosition, sphereRadius); break;
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
            case DetectionShape.Mesh: if (meshPreview) { Gizmos.DrawMesh(meshPreview, detectPosition); Gizmos.DrawWireMesh(meshPreview, detectPosition); } break;
        }
    }

    public float GetActivationDelay() => activationDelay;
    public PressMode GetPressMode() => pressMode;
    
    public void Server_SetHolder(ulong clientId)
    {
        if (IsServer) _holderClientIdNV.Value = clientId;
    }
    #endregion
}
