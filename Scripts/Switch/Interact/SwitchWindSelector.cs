using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DG.Tweening;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.UI;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// สวิตช์ลมแบบเลือกโหมดเดียว แล้วทำงานแล้วปิดตัวเอง (One-Shot)
/// - เลือก Wind Mode (Push/Pull/Disabled) ได้
/// - กด E เพื่อ activate
/// - หลังจาก activate แล้วจะ disable ตัวเอง
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class SwitchWindSelector : NetworkBehaviour
{
    [Serializable]
    public class TargetEntry
    {
        public MonoBehaviour activatableComponent;
        public bool allow = true;
        public bool invert;
    }

    private enum DetectionShape { Box, Sphere, Capsule, Mesh }

    [Header("Wind Mode Selection")]
    [Tooltip("เลือก Wind Mode ที่จะส่งไปให้ targets เมื่อกดสวิตช์")]
    [SerializeField] private WindMode selectedWindMode = WindMode.Push;

    [Header("Targets (เป้าหมายที่จะส่ง Wind Mode ไป)")]
    [SerializeField] private List<TargetEntry> directTargets = new();

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

    [Header("Input / UI")]
    [SerializeField] private GameObject idleIndicatorUI;
    [SerializeField] private GameObject promptUI;
    [SerializeField] private float uiFadeDuration = 0.2f;

#if ENABLE_INPUT_SYSTEM
    [SerializeField] private InputActionReference interactAction;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
    [SerializeField] private KeyCode fallbackKey = KeyCode.E;
#endif

    [Header("Anti-spam")]
    [SerializeField] private float pressCooldown = 0.25f;

    [Header("Broadcast to IWindModeActivatable")]
    [SerializeField] private bool alsoBroadcastWindMode = true;

    [Header("Audio")]
    [Tooltip("Audio Source สำหรับตำแหน่งเกิดเสียง (กำหนดใน Inspector)")]
    [SerializeField] private AudioSource audioSource;
    [Tooltip("Audio Clip ที่จะเล่นเมื่อสวิตช์ทำงาน")]
    [SerializeField] private AudioClip activationSound;
    [Tooltip("Audio Mixer Group สำหรับส่ง Output (ถ้าต้องการ)")]
    [SerializeField] private AudioMixerGroup outputMixerGroup;

    [Header("Disable SwitchWind After Activation")]
    [Tooltip("ติ๊กเพื่อเปิดใช้งานการ disable SwitchWind ที่เลือกหลังจากสับสวิช")]
    [SerializeField] private bool disableSwitchWindsAfterActivation = false;
    
    [Tooltip("ลาก SwitchWind จาก Scene มาวางที่นี่ (จะถูก disable หลังจากสับสวิช)")]
    [SerializeField] private List<SwitchWind> switchWindsToDisable = new();

    // -------- Runtime --------
    private float _lastPressLocal = -999f;
    private float _lastPressServer = -999f;
    private bool _localInside;

    private readonly NetworkVariable<bool> _activatedNV =
        new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private CanvasGroup _idleCG;
    private CanvasGroup _promptCG;

    // ===== Unity Lifecycle =====
    private void Awake()
    {
        if (!pivot) pivot = transform;
        InitializeUI();
        InitializeAudio();
        _activatedNV.OnValueChanged += OnActivatedChanged;
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        
        // ถ้าเคย activate ไปแล้วให้ซ่อน UI (แต่ไม่ disable script เพื่อไม่ให้มีปัญหา network)
        if (_activatedNV.Value)
        {
            HideAllUI();
            // ไม่ disable script เพื่อไม่ให้มีปัญหา network
        }
    }

    private void OnEnable()
    {
#if ENABLE_INPUT_SYSTEM
        // Enable InputAction เมื่อ script ถูก enable
        // InputAction มี reference counting ดังนั้นจะไม่ถูก disable จนกว่าทุก script ที่ใช้มันจะถูก disable
        if (interactAction?.action != null) interactAction.action.Enable();
#endif
    }

    private void OnDisable()
    {
#if ENABLE_INPUT_SYSTEM
        // Disable InputAction เมื่อ script ถูก disable
        // InputAction มี reference counting ดังนั้นจะไม่ถูก disable จนกว่าทุก script ที่ใช้มันจะถูก disable
        // แต่เนื่องจากเราไม่ disable script อีกแล้ว (ใช้ _activatedNV แทน) จึงไม่น่าจะมาถึงที่นี่บ่อย
        if (interactAction?.action != null) interactAction.action.Disable();
#endif
        _activatedNV.OnValueChanged -= OnActivatedChanged;
    }

    private void Update()
    {
        // ถ้า activate ไปแล้วให้หยุดทำงาน (แต่ไม่ disable script)
        if (_activatedNV.Value)
        {
            // ซ่อน UI ถ้ายังแสดงอยู่
            if ((idleIndicatorUI != null && idleIndicatorUI.activeSelf) || (promptUI != null && promptUI.activeSelf))
                HideAllUI();
            // หยุด Update แต่ไม่ disable script เพื่อไม่ให้มีปัญหา network
            return;
        }

        if (!IsClient) return;

        bool isInsideNow = IsClientIdInsideArea(NetworkManager.Singleton.LocalClientId);
        UpdateLocalProximity(isInsideNow);

        if (isInsideNow)
        {
            HandleKeyTapInput();
        }
    }

    private void UpdateLocalProximity(bool isInsideNow)
    {
        if (isInsideNow != _localInside)
        {
            _localInside = isInsideNow;
            UpdateUIVisuals(_localInside);
        }
    }

    private void HandleKeyTapInput()
    {
        if (Time.time - _lastPressLocal > pressCooldown && WasInteractPressedThisFrame())
        {
            _lastPressLocal = Time.time;
            RequestPressServerRpc();
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

    private void InitializeUI()
    {
        if (idleIndicatorUI != null)
        {
            _idleCG = idleIndicatorUI.GetComponent<CanvasGroup>();
            if (_idleCG == null) _idleCG = idleIndicatorUI.AddComponent<CanvasGroup>();
            _idleCG.alpha = 1f;
            idleIndicatorUI.SetActive(true);
        }

        if (promptUI != null)
        {
            _promptCG = promptUI.GetComponent<CanvasGroup>();
            if (_promptCG == null) _promptCG = promptUI.AddComponent<CanvasGroup>();
            _promptCG.alpha = 0f;
            promptUI.SetActive(false);
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

    private void UpdateUIVisuals(bool isInside)
    {
        _idleCG?.DOKill();
        _promptCG?.DOKill();
        if (isInside)
        {
            if (_idleCG != null) _idleCG.DOFade(0f, uiFadeDuration).OnComplete(() => idleIndicatorUI.SetActive(false));
            if (_promptCG != null)
            {
                promptUI.SetActive(true);
                _promptCG.DOFade(1f, uiFadeDuration);
            }
        }
        else
        {
            if (_promptCG != null) _promptCG.DOFade(0f, uiFadeDuration).OnComplete(() => promptUI.SetActive(false));
            if (_idleCG != null)
            {
                idleIndicatorUI.SetActive(true);
                _idleCG.DOFade(1f, uiFadeDuration);
            }
        }
    }

    private void HideAllUI()
    {
        if (idleIndicatorUI) idleIndicatorUI.SetActive(false);
        if (promptUI) promptUI.SetActive(false);
    }

    // ===== Network =====

    [ServerRpc(RequireOwnership = false)]
    private void RequestPressServerRpc(ServerRpcParams rpc = default)
    {
        // ถ้า activate ไปแล้วให้หยุดทำงาน
        if (_activatedNV.Value) return;

        if (Time.time - _lastPressServer < pressCooldown) return;

        if (IsClientIdInsideArea(rpc.Receive.SenderClientId))
        {
            _lastPressServer = Time.time;
            FireOnce_Server();
        }
    }

    private void OnActivatedChanged(bool prev, bool next)
    {
        if (next)
        {
            // ซ่อน UI แต่ไม่ disable script เพื่อไม่ให้มีปัญหา network
            HideAllUI();
            // ไม่ disable script ที่นี่ เพื่อให้ NetworkBehaviour ยังทำงานได้
        }
    }

    // ===== Core actions =====
    private void FireOnce_Server()
    {
        // ตั้งค่าว่า activate แล้ว
        _activatedNV.Value = true;

        // ทำให้ Lever ขยับแบบสมูท (ใช้ PlayOneShot เท่านั้น เพื่อไม่ให้อนิเมชั่นชนกัน)
        PlayLeverClientRpc();
        PlayLeverLocal();
        PlayActivationSoundClientRpc();

        // ส่ง Wind Mode ไปให้ targets หลังจากดีเลย์
        StartCoroutine(ActivateAfterDelayCo());
    }

    private IEnumerator ActivateAfterDelayCo()
    {
        yield return new WaitForSeconds(activationDelay);
        
        // ส่ง Wind Mode ไปให้ targets
        ApplyWindModeToTargets(selectedWindMode);
        BroadcastWindMode(selectedWindMode);

        // Disable SwitchWind ที่เลือก (ถ้าเปิดใช้งาน)
        if (disableSwitchWindsAfterActivation)
        {
            DisableSwitchWinds();
        }

        // ซ่อน UI (ไม่ disable script เพื่อไม่ให้มีปัญหา network)
        HidePromptAllClientRpc();
        // ไม่ disable script เพื่อให้ NetworkBehaviour ยังทำงานได้และไม่กระทบสวิตช์อื่น
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

    [ClientRpc] 
    private void ApplyWindModeClientRpc(WindMode mode) 
    { 
        if (!IsServer) ApplyWindModeToTargets(mode); 
    }

    [ClientRpc] 
    private void PlayLeverClientRpc() 
    { 
        if (!IsServer) PlayLeverLocal(); 
    }

    [ClientRpc] 
    private void HidePromptAllClientRpc() => HideAllUI();

    [ClientRpc] 
    private void PlayActivationSoundClientRpc() => PlayActivationSound();

    /// <summary>
    /// Disable SwitchWind ที่เลือก (ต้องทำบน Server เท่านั้น)
    /// </summary>
    private void DisableSwitchWinds()
    {
        if (!IsServer) return; // ต้องทำบน Server เท่านั้น

        foreach (var switchWind in switchWindsToDisable)
        {
            if (switchWind == null) continue;

            // เรียก public method ใน SwitchWind เพื่อ disable
            switchWind.Server_SetExternallyDisabled(true);
            Debug.Log($"[SwitchWindSelector] Disabled SwitchWind: {switchWind.name}", this);
        }
    }

    public void PlayLeverLocal() => modelRotator?.PlayOneShot();

    private void PlayActivationSound()
    {
        if (audioSource != null && activationSound != null)
        {
            audioSource.clip = activationSound;
            audioSource.Play();
        }
    }

    // ===== Detection =====
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
}

