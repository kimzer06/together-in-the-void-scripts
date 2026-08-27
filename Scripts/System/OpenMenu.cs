using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using StarterAssets;
using Unity.Netcode;
using UnityEngine.SceneManagement;
using System.Collections;
using UnityEngine.EventSystems;

public class OpenMenu : MonoBehaviour
{
    /// <summary>true เมื่อเมนูเปิดอยู่ — สคริปต์อื่นใช้เช็คได้</summary>
    public static bool IsMenuOpen { get; private set; }

    [Tooltip("GameObject ที่ต้องการเปิด/ปิด เมื่อกด Esc")]
    public GameObject menuObject;

    private ThirdPersonController_Rigidbody _localPlayerController;
    private StarterAssetsInputs _localInputs;

    [Header("Scene Persistence")]
    [Tooltip("ถ้าเปิด: ตัว OpenMenu จะอยู่ข้ามซีน (กันวางซ้ำหลายซีน)")]
    [SerializeField] private bool dontDestroyOnLoad = true;

    [Tooltip("ซีนที่ไม่ควรมี Pause/OpenMenu (เช่น MainMenu/StartScene/Loading/CharacterSelect). เมื่อเข้าแล้วจะปิดเมนู และจะไม่รับกด Esc")]
    [SerializeField] private string[] disallowScenes = new[] { "MainMenu", "StartScene", "Loading", "CharacterSelect" };

    [Tooltip("DEPRECATED: ไม่ใช้แล้ว (ไม่ Destroy ตอนเข้า disallowScenes)")]
    [SerializeField] private bool destroySelfInDisallowScenes = false;

    private static OpenMenu _instance;

    private void Awake()
    {
        if (!dontDestroyOnLoad) return;

        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void OnEnable()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void OnDisable()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        // กัน static ค้างจากซีนก่อนหน้า
        IsMenuOpen = false;
        _localPlayerController = null;
        _localInputs = null;

        if (menuObject != null)
        {
            menuObject.SetActive(false);
        }

        // ไม่ Destroy — แค่ปิดเมนูไว้และปล่อยให้ Update() บล็อกการกด Esc ในซีนเหล่านี้
    }

    private bool IsDisallowedScene(string sceneName)
    {
        if (disallowScenes == null) return false;
        for (int i = 0; i < disallowScenes.Length; i++)
        {
            if (string.Equals(disallowScenes[i], sceneName, System.StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    void Update()
    {
        if (IsDisallowedScene(SceneManager.GetActiveScene().name))
            return;

        bool pressedEscape = false;

#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            pressedEscape = true;
        }
#else
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            pressedEscape = true;
        }
#endif

        if (pressedEscape)
        {
            if (InteractableSignPanel.BlocksOpenMenuEscape)
                return;
            ToggleMenu();
        }
    }

    private void ToggleMenu()
    {
        if (menuObject != null)
        {
            bool isActive = !menuObject.activeSelf;
            menuObject.SetActive(isActive);
            IsMenuOpen = isActive;
            
            // Handle Cursor and Player Control
            SetCursorState(isActive);
            SetPlayerControl(!isActive);
            SetPlayerInputForMenu(isActive);

            // Some UI/input modules may keep focus until end-of-frame, requiring a click.
            // Force cursor lock again on the next frame when closing.
            if (!isActive)
            {
                if (EventSystem.current != null)
                    EventSystem.current.SetSelectedGameObject(null);

                StopAllCoroutines();
                StartCoroutine(ForceLockCursorNextFrames());
            }
        }
    }

    /// <summary>
    /// Force-close pause menu and restore local player look/movement input.
    /// Safe to call even if menu already closed.
    /// </summary>
    public void ForceCloseMenu()
    {
        if (menuObject != null)
            menuObject.SetActive(false);

        IsMenuOpen = false;

        // Restore cursor + controls even if something toggled menuObject directly.
        SetCursorState(false);
        SetPlayerControl(true);
        SetPlayerInputForMenu(false);

        if (EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(null);

        StopAllCoroutines();
        StartCoroutine(ForceLockCursorNextFrames());
    }

    private void SetCursorState(bool menuOpen)
    {
        if (menuOpen)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        else
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    private IEnumerator ForceLockCursorNextFrames()
    {
        // Apply immediately (already done), then again next frames to win against UI focus changes.
        yield return null;
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        yield return null;
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    private void SetPlayerControl(bool allowControl)
    {
        if (_localPlayerController == null)
        {
            FindLocalPlayer();
        }

        if (_localPlayerController != null)
        {
            _localPlayerController.SetMovementLocked(!allowControl);
            _localPlayerController.LockCameraPosition = !allowControl;
        }
    }

    private void SetPlayerInputForMenu(bool menuOpen)
    {
        if (_localInputs == null)
        {
            // Try to locate inputs from local player (most reliable), then fallback to global search.
            if (_localPlayerController != null)
                _localInputs = _localPlayerController.GetComponent<StarterAssetsInputs>();

            if (_localInputs == null)
                _localInputs = FindFirstObjectByType<StarterAssetsInputs>(FindObjectsInactive.Exclude);
        }

        if (_localInputs == null) return;

        // Starter Assets uses these flags to gate Look input + cursor lock behavior.
        _localInputs.cursorInputForLook = !menuOpen;
        _localInputs.cursorLocked = !menuOpen;
    }

    private void FindLocalPlayer()
    {
        var controllers = FindObjectsByType<ThirdPersonController_Rigidbody>(FindObjectsSortMode.None);
        foreach (var controller in controllers)
        {
            if (controller.IsOwner)
            {
                _localPlayerController = controller;
                _localInputs = controller.GetComponent<StarterAssetsInputs>();
                break;
            }
        }
    }
}
