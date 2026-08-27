using DG.Tweening;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Rendering;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

[RequireComponent(typeof(Collider))]
public class ButtonSwitch : NetworkBehaviour
{
    [Header("Detect / Eligibility")]
    [SerializeField] private string playerTag = "Player";

    [Header("UI Prompt (local only)")]
    [SerializeField] private GameObject promptUI; // world/ screen-space ก็ได้
    [SerializeField] private float uiScaleDuration = 0.35f;
    [SerializeField] private float uiScaleOvershoot = 1.7f;

    [Header("Controller & Slot")]
    [SerializeField] private ColorPlatformsController controller;
    [SerializeField] private ColorPlatformsController.Slot slotOfThisButton = ColorPlatformsController.Slot.Red;

    [Header("Lever / Visual")]
    [SerializeField] private SwitchRotator modelRotator;

    [Header("Audio")]
    [Tooltip("Audio Source สำหรับตำแหน่งเกิดเสียง (กำหนดใน Inspector)")]
    [SerializeField] private AudioSource audioSource;
    [Tooltip("Audio Clip ที่จะเล่นเมื่อปุ่มนี้ถูกเลือกเป็น Active slot")]
    [SerializeField] private AudioClip activationSound;
    [Tooltip("Audio Clip ที่จะเล่นเมื่อปุ่มนี้ถูกยกเลิก (ไม่ Active)")]
    [SerializeField] private AudioClip deactivationSound;
    [Tooltip("Audio Mixer Group สำหรับส่ง Output (ถ้าต้องการ)")]
    [SerializeField] private AudioMixerGroup outputMixerGroup;

    [Header("Input")]
#if ENABLE_INPUT_SYSTEM
    [Tooltip("ใส่ InputActionReference (ถ้าไม่ใส่ จะ fallback เป็นคีย์บอร์ด E)")]
    [SerializeField] private InputActionReference interactAction;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
    [SerializeField] private KeyCode fallbackKey = KeyCode.E;
#endif

    [Header("Anti-spam")]
    [SerializeField] private float pressCooldown = 0.1f;
    float lastPress;

    ulong? eligibleClientId = null; // server-side
    bool isLocalEligible = false;   // client local

    Vector3 _promptOriginalScale;
    private bool _hasAppliedInitialSlot;
    private bool _isActiveLocal;

    void Awake()
    {
        if (promptUI)
        {
            _promptOriginalScale = promptUI.transform.localScale;
            promptUI.transform.localScale = Vector3.zero;
            promptUI.SetActive(false);
        }
        if (!controller) controller = FindAnyObjectByType<ColorPlatformsController>();
        InitializeAudio();
    }

    private void OnEnable()
    {
        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
    #if ENABLE_INPUT_SYSTEM
        if (interactAction?.action != null) interactAction.action.Enable();
    #endif
    }

    private void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
    #if ENABLE_INPUT_SYSTEM
        if (interactAction?.action != null) interactAction.action.Disable();
    #endif
    }

    public override void OnNetworkSpawn()
    {
        if (controller != null)
        {
            controller.OnActiveSlotLocal += OnActiveSlotChanged_Local;
            OnActiveSlotChanged_Local(controller.ActiveSlotValue);
        }
    }

    void OnDestroy()
    {
        if (controller != null) controller.OnActiveSlotLocal -= OnActiveSlotChanged_Local;
    }

    private Camera _mainCamera;

    void Update()
    {
        if (!IsClient || !isLocalEligible) return;

        if (WasInteractPressed() && Time.time - lastPress >= pressCooldown)
        {
            lastPress = Time.time;
            RequestPressServerRpc();
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
        
        if (promptUI != null && promptUI.activeSelf)
        {
            promptUI.transform.rotation = _mainCamera.transform.rotation;
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

    private void OnTriggerEnter(Collider other)
    {
        if (!IsServer) return;
        if (!other.CompareTag(playerTag)) return;
        var no = other.GetComponentInParent<NetworkObject>();
        if (no == null) return;

        if (!eligibleClientId.HasValue)
        {
            eligibleClientId = no.OwnerClientId;
            SetEligibleClientRpc(true, ToClient(eligibleClientId.Value));
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (!IsServer) return;
        if (!other.CompareTag(playerTag)) return;
        var no = other.GetComponentInParent<NetworkObject>();
        if (no == null) return;

        if (eligibleClientId.HasValue && no.OwnerClientId == eligibleClientId.Value)
        {
            SetEligibleClientRpc(false, ToClient(eligibleClientId.Value));
            eligibleClientId = null;
        }
    }

    [ClientRpc]
    void SetEligibleClientRpc(bool enable, ClientRpcParams p = default)
    {
        isLocalEligible = enable;
        if (promptUI != null)
        {
            if (enable)
            {
                promptUI.transform.DOKill();
                promptUI.SetActive(true);
                promptUI.transform.DOScale(_promptOriginalScale, uiScaleDuration)
                    .SetEase(Ease.OutBack, uiScaleOvershoot);
            }
            else
            {
                promptUI.transform.DOKill();
                promptUI.transform.DOScale(Vector3.zero, uiScaleDuration)
                    .SetEase(Ease.InBack, uiScaleOvershoot)
                    .OnComplete(() => promptUI.SetActive(false));
            }
        }
    }

    [ServerRpc(RequireOwnership = false)]
    void RequestPressServerRpc(ServerRpcParams sp = default)
    {
        if (!controller || !eligibleClientId.HasValue) return;
        ulong sender = sp.Receive.SenderClientId;
        if (sender != eligibleClientId.Value) return;

        controller.SetActiveSlotServerRpc(slotOfThisButton); // toggle อยู่ใน controller แล้ว
    }

    void OnActiveSlotChanged_Local(ColorPlatformsController.Slot active)
    {
        bool isActiveNow = active == slotOfThisButton;
        bool isStateChanged = _hasAppliedInitialSlot && (isActiveNow != _isActiveLocal);

        _isActiveLocal = isActiveNow;
        _hasAppliedInitialSlot = true;

        if (isActiveNow)
        {
            modelRotator?.PlayOneShot();
            if (isStateChanged) PlayActivationSound(activationSound);
        }
        else
        {
            modelRotator?.ResetToBaseAnimated();
            if (isStateChanged) PlayActivationSound(deactivationSound);
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

    private void PlayActivationSound(AudioClip clip)
    {
        if (audioSource == null || clip == null) return;
        audioSource.clip = clip;
        audioSource.Play();
    }

    ClientRpcParams ToClient(ulong id) => new ClientRpcParams
    {
        Send = new ClientRpcSendParams { TargetClientIds = new ulong[] { id } }
    };

#if UNITY_EDITOR
    void OnValidate()
    {
        if (!controller) controller = FindAnyObjectByType<ColorPlatformsController>();
    }
#endif
}
