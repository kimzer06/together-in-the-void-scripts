using System.Collections;
using DG.Tweening;
using StarterAssets;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using Unity.Netcode;

/// <summary>
/// ป้าย/จุดอ่านข้อความ: ตรวจว่า local player อยู่ใน Sphere แล้วกด Interact เปิด Canvas
/// ปิดได้จากปุ่ม UI (เรียก ClosePanel) หรือเดินออกจาก Sphere
/// โชว์ปุ่ม/ไอคอน Interact แบบ World UI หมุนตามกล้อง (เหมือน InteractSwitch)
/// </summary>
public class InteractableSignPanel : MonoBehaviour
{
    private static int s_OpenMenuEscapeSuppressionCount;

    /// <summary>ใช้ใน OpenMenu — มีป้ายเปิดค้างและเลือกบล็อก Esc อยู่</summary>
    public static bool BlocksOpenMenuEscape => s_OpenMenuEscapeSuppressionCount > 0;

    [Header("Panel")]
    [SerializeField] private GameObject panelRoot;
    [Tooltip("เปิด/ปิด cursor ตอนแพนเนลเปิด (กดปุ่มใน Canvas)")]
    [SerializeField] private bool manageCursorWhileOpen = true;
    [Tooltip("เหมือน OpenMenu: หยุดเดิน + ล็อกมุมกล้อง/เมาส์ ขณะอ่านป้าย")]
    [SerializeField] private bool freezeLocalPlayerWhileOpen = true;
    [Tooltip("ขณะอ่านป้าย ไม่ให้ OpenMenu รับ Esc (กัน cursor ถูกเมนูปิดแล้วหาย)")]
    [SerializeField] private bool blockOpenMenuWhilePanelOpen = true;

    [Header("Interact prompt (World UI — หมุนตามกล้อง)")]
    [Tooltip("UI โลก เช่น ปุ่มกด E — แสดงเมื่อเข้าโซนและยังไม่เปิดแพนเนลใหญ่")]
    [SerializeField] private GameObject interactPromptUI;
    [SerializeField] private float promptScaleDuration = 0.35f;
    [SerializeField] private float promptScaleOvershoot = 1.7f;

    [Header("Sphere detect")]
    [SerializeField] private Transform pivot;
    [SerializeField] private Vector3 detectOffset = Vector3.zero;
    [SerializeField, Min(0.01f)] private float sphereRadius = 2f;
    [SerializeField] private LayerMask detectLayers = ~0;
    [SerializeField] private string requiredTag = "Player";

    [Header("Input")]
#if ENABLE_INPUT_SYSTEM
    [Tooltip("ลาก Interact จาก Input Actions asset เดียวกับสวิตช์อื่นในโปรเจกต์")]
    [SerializeField] private InputActionReference interactAction;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
    [SerializeField] private KeyCode fallbackKey = KeyCode.E;
#endif

    [Header("Anti-spam")]
    [SerializeField, Min(0f)] private float interactCooldown = 0.25f;

    private bool _panelOpen;
    private float _lastInteractTime = -999f;
    private CursorLockMode _savedLockMode;
    private bool _savedCursorVisible;
    private CanvasGroup _promptCG;
    private Vector3 _promptOriginalScale;
    private bool _promptVisibilityTarget;
    private Camera _mainCamera;
    private ThirdPersonController_Rigidbody _localPlayerController;
    private bool _playerFreezeApplied;
    private Coroutine _refreshHoverCo;
    private bool _registeredOpenMenuEscapeBlock;
    private bool _suppressAutoCloseUntilManualClose;

    private void Awake()
    {
        if (!pivot) pivot = transform;
        if (panelRoot) panelRoot.SetActive(false);
        InitializePromptUI();
    }

    private void InitializePromptUI()
    {
        if (interactPromptUI == null) return;
        _promptCG = interactPromptUI.GetComponent<CanvasGroup>();
        if (_promptCG == null) _promptCG = interactPromptUI.AddComponent<CanvasGroup>();
        _promptCG.alpha = 1f;
        _promptOriginalScale = interactPromptUI.transform.localScale;
        interactPromptUI.transform.localScale = Vector3.zero;
        interactPromptUI.SetActive(false);
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
        if (_panelOpen) ClosePanel();
        HidePromptImmediate();
    }

    private void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
    {
        if (_mainCamera == null) _mainCamera = Camera.main;
        if (camera == _mainCamera && _mainCamera != null) BillboardPromptUI();
    }

    private void BillboardPromptUI()
    {
        if (_mainCamera == null) _mainCamera = Camera.main;
        if (_mainCamera == null) return;
        if (interactPromptUI != null && interactPromptUI.activeSelf)
            interactPromptUI.transform.rotation = _mainCamera.transform.rotation;
    }

    private void Update()
    {
        bool inside = IsLocalPlayerInsideSphere();
        if (!inside)
        {
            if (_panelOpen && !_suppressAutoCloseUntilManualClose) ClosePanel();
            SetInteractPromptDesired(false);
            return;
        }

        SetInteractPromptDesired(!_panelOpen);

        if (_panelOpen) return;

        if (Time.time - _lastInteractTime < interactCooldown) return;
        if (!WasInteractPressed()) return;

        _lastInteractTime = Time.time;
        OpenPanel();
    }

    private void LateUpdate()
    {
        // StarterAssetsInputs.OnApplicationFocus ฯลฯ มักล็อก cursor คืนหลัง Alt+Tab — บังคับให้โผล่ขณะอ่านป้าย
        if (!_panelOpen || !manageCursorWhileOpen) return;
        if (Cursor.lockState != CursorLockMode.None)
            Cursor.lockState = CursorLockMode.None;
        if (!Cursor.visible)
            Cursor.visible = true;
    }

    private bool IsLocalPlayerInsideSphere()
    {
        Vector3 center = pivot.TransformPoint(detectOffset);
        Collider[] hits = Physics.OverlapSphere(center, sphereRadius, detectLayers, QueryTriggerInteraction.Collide);
        if (hits == null || hits.Length == 0) return false;

        foreach (var c in hits)
        {
            if (!string.IsNullOrEmpty(requiredTag) && !c.CompareTag(requiredTag)) continue;
            var no = c.GetComponentInParent<NetworkObject>();
            if (no != null && no.IsLocalPlayer) return true;
        }

        return false;
    }

    private bool WasInteractPressed()
    {
#if ENABLE_INPUT_SYSTEM
        if (interactAction?.action != null && interactAction.action.triggered) return true;
        if (Keyboard.current != null && Keyboard.current.eKey.wasPressedThisFrame) return true;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
        if (Input.GetKeyDown(fallbackKey)) return true;
#endif
        return false;
    }

    /// <summary>ผูกกับปุ่มปิดบน Canvas (Button OnClick)</summary>
    public void ClosePanel()
    {
        if (!panelRoot) return;
        _suppressAutoCloseUntilManualClose = false;
        UnregisterOpenMenuEscapeBlock();
        _panelOpen = false;
        if (panelRoot.activeSelf)
            ClearUIPointerStateBeforeHide(panelRoot);
        panelRoot.SetActive(false);
        RestorePlayerForPanel();
        RestoreCursor();
        if (isActiveAndEnabled && IsLocalPlayerInsideSphere()) SetInteractPromptDesired(true);
    }

    public void OpenPanel()
    {
        if (!panelRoot) return;
        _panelOpen = true;
        RegisterOpenMenuEscapeBlock();
        panelRoot.SetActive(true);
        ApplyCursorForPanel();
        ApplyPlayerForPanel();
        SetInteractPromptDesired(false);
        if (_refreshHoverCo != null) StopCoroutine(_refreshHoverCo);
        _refreshHoverCo = StartCoroutine(RefreshUIMouseHoverAfterShowCo());
    }

    /// <summary>
    /// เปิดจากสคริปต์ภายนอก (เช่น Tutorial หลัง Timeline) โดยไม่ auto-close ตอนอยู่นอก Sphere
    /// จะปิดเมื่อผู้เล่นกดปุ่มปิด (ClosePanel) เท่านั้น
    /// </summary>
    public void OpenPanelExternal()
    {
        _suppressAutoCloseUntilManualClose = true;
        OpenPanel();
    }

    private void RegisterOpenMenuEscapeBlock()
    {
        if (!blockOpenMenuWhilePanelOpen || _registeredOpenMenuEscapeBlock) return;
        s_OpenMenuEscapeSuppressionCount++;
        _registeredOpenMenuEscapeBlock = true;
    }

    private void UnregisterOpenMenuEscapeBlock()
    {
        if (!_registeredOpenMenuEscapeBlock) return;
        s_OpenMenuEscapeSuppressionCount = Mathf.Max(0, s_OpenMenuEscapeSuppressionCount - 1);
        _registeredOpenMenuEscapeBlock = false;
    }

    /// <summary>
    /// ก่อนปิดแพนเนล: เคลียร์ selection + ส่ง PointerUp/Exit ให้ UI ใต้แพนเนล
    /// (ถ้าไม่ทำ ตอนเมาส์ทับปุ่มปิดแล้ว SetActive(false) จะไม่ได้รับ OnPointerExit)
    /// </summary>
    private static void ClearUIPointerStateBeforeHide(GameObject root)
    {
        if (root == null) return;
        var es = EventSystem.current;
        if (es == null) return;

        var ped = new PointerEventData(es)
        {
            position = GetPointerScreenPosition(),
            pointerId = -1
        };

        foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (!mb.isActiveAndEnabled) continue;
            if (mb is IPointerUpHandler up) up.OnPointerUp(ped);
        }

        foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (!mb.isActiveAndEnabled) continue;
            if (mb is IPointerExitHandler exit) exit.OnPointerExit(ped);
        }

        es.SetSelectedGameObject(null);
    }

    private static Vector2 GetPointerScreenPosition()
    {
#if ENABLE_INPUT_SYSTEM
        if (Mouse.current != null) return Mouse.current.position.ReadValue();
#endif
        return Input.mousePosition;
    }

    /// <summary>
    /// หลังเปิดแพนเนล: บังคับให้โมดูลอินพุตประมวลผล hover ใหม่ (เมาส์นิ่งอยู่เหนือปุ่มเดิมจะไม่ได้ PointerEnter ซ้ำ)
    /// </summary>
    private IEnumerator RefreshUIMouseHoverAfterShowCo()
    {
        yield return null;
#if ENABLE_INPUT_SYSTEM
        if (Mouse.current != null)
        {
            Vector2 p = Mouse.current.position.ReadValue();
            Mouse.current.WarpCursorPosition(new Vector2(p.x + 1f, p.y));
            Mouse.current.WarpCursorPosition(p);
        }
#endif
        _refreshHoverCo = null;
    }

    private void ApplyPlayerForPanel()
    {
        if (!freezeLocalPlayerWhileOpen) return;
        if (_localPlayerController == null) FindLocalPlayerController();
        if (_localPlayerController == null) return;
        _localPlayerController.SetMovementLocked(true);
        _localPlayerController.LockCameraPosition = true;
        _playerFreezeApplied = true;
    }

    private void RestorePlayerForPanel()
    {
        if (!_playerFreezeApplied) return;
        if (_localPlayerController != null)
        {
            _localPlayerController.SetMovementLocked(false);
            _localPlayerController.LockCameraPosition = false;
        }
        _playerFreezeApplied = false;
    }

    private void FindLocalPlayerController()
    {
        var controllers = FindObjectsByType<ThirdPersonController_Rigidbody>(FindObjectsSortMode.None);
        foreach (var c in controllers)
        {
            if (c.IsOwner)
            {
                _localPlayerController = c;
                break;
            }
        }
    }

    private void SetInteractPromptDesired(bool visible)
    {
        if (interactPromptUI == null) return;
        if (_promptVisibilityTarget == visible) return;
        _promptVisibilityTarget = visible;
        if (visible) ScaleShowPrompt();
        else ScaleHidePrompt();
    }

    private void ScaleShowPrompt()
    {
        if (interactPromptUI == null) return;
        interactPromptUI.transform.DOKill();
        interactPromptUI.SetActive(true);
        interactPromptUI.transform.DOScale(_promptOriginalScale, promptScaleDuration)
            .SetEase(Ease.OutBack, promptScaleOvershoot);
    }

    private void ScaleHidePrompt()
    {
        if (interactPromptUI == null) return;
        interactPromptUI.transform.DOKill();
        interactPromptUI.transform.DOScale(Vector3.zero, promptScaleDuration)
            .SetEase(Ease.InBack, promptScaleOvershoot)
            .OnComplete(() => interactPromptUI.SetActive(false));
    }

    private void HidePromptImmediate()
    {
        if (interactPromptUI == null) return;
        interactPromptUI.transform.DOKill();
        interactPromptUI.transform.localScale = Vector3.zero;
        interactPromptUI.SetActive(false);
        _promptVisibilityTarget = false;
    }

    private void ApplyCursorForPanel()
    {
        if (!manageCursorWhileOpen) return;
        _savedLockMode = Cursor.lockState;
        _savedCursorVisible = Cursor.visible;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    private void RestoreCursor()
    {
        if (!manageCursorWhileOpen) return;
        Cursor.lockState = _savedLockMode;
        Cursor.visible = _savedCursorVisible;
    }

    private void OnDrawGizmosSelected()
    {
        Transform p = pivot ? pivot : transform;
        Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.25f);
        Vector3 c = p.TransformPoint(detectOffset);
        Gizmos.DrawSphere(c, sphereRadius);
        Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.9f);
        Gizmos.DrawWireSphere(c, sphereRadius);
    }
}
