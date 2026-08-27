using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Netcode;

public class MainMenuUI : MonoBehaviour
{
    [Header("Scene Names")]
    [Tooltip("ชื่อซีนของเกมหลัก เช่น 'Level1'")]
    public string gameSceneName = "GameScene";
    [SerializeField] private GameObject settingPanel;
    [SerializeField] private GameObject soundSettingPanel;
    [SerializeField] private GameObject confirmQuitPanel;
    [SerializeField] private GameObject confirmMainMenuPanel;

    public void OnStartGame()
    {
        // ทำลาย NetworkManager ที่หลงเหลือจาก session ก่อน (ถ้ามี)
        // เพราะ StartScene จะสร้างตัวใหม่เอง
        if (NetworkManager.Singleton != null)
        {
            Debug.Log("[MainMenuUI] Destroying leftover NetworkManager before loading StartScene.");
            DestroyImmediate(NetworkManager.Singleton.gameObject);
        }

        // ทำลาย LobbySelectionState ที่หลงเหลือ (ถ้ามี)
        if (LobbySelectionState.I != null)
        {
            DestroyImmediate(LobbySelectionState.I.gameObject);
        }

        // โหลดซีนหลักของเกม
        SceneManager.LoadScene(gameSceneName);
    }

    private static void SafeSetActive(GameObject go, bool active)
    {
        if (go != null)
            go.SetActive(active);
    }

    /// <summary>
    /// ปิด panel ทุกตัวฝั่ง Right เพื่อไม่ให้ซ้อนกัน
    /// </summary>
    private void CloseAllRightPanels()
    {
        SafeSetActive(soundSettingPanel, false);
        SafeSetActive(confirmQuitPanel, false);
        SafeSetActive(confirmMainMenuPanel, false);
    }

    public void OnSetting()
    {
        SafeSetActive(settingPanel, true);
    }

    public void OnSoundSetting()
    {
        CloseAllRightPanels();
        SafeSetActive(soundSettingPanel, true);
    }

    public void OnCloseSetting()
    {
        SafeSetActive(settingPanel, false);
        CloseAllRightPanels();
    }

    public void OnQuit()
    {
        // ปิด panel อื่นก่อน แล้วแสดง panel ยืนยัน
        CloseAllRightPanels();
        SafeSetActive(confirmQuitPanel, true);
    }

    public void OnConfirmQuit()
    {
        // ยืนยันออกจากเกม (ทำงานจริงตอน Build)
        Debug.Log("Quit Game");
        Application.Quit();
    }

    public void OnCancelQuit()
    {
        // ยกเลิก → ปิด confirm panel
        SafeSetActive(confirmQuitPanel, false);
    }

    public void OnMainMenu()
    {
        // ปิด panel อื่นก่อน แล้วแสดง panel ยืนยันกลับ Main Menu
        CloseAllRightPanels();
        SafeSetActive(confirmMainMenuPanel, true);
    }

    public void OnCancelMainMenu()
    {
        // ยกเลิก → ปิด confirm panel
        SafeSetActive(confirmMainMenuPanel, false);
    }

    public void OnRestartFromCheckpoint()
    {
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsClient)
        {
            Debug.LogWarning("Cannot restart from checkpoint: NetworkManager not initialized or not a client.");
            return;
        }

        var playerObject = NetworkManager.Singleton.SpawnManager.GetLocalPlayerObject();
        if (playerObject == null)
        {
            Debug.LogWarning("Local player object not found.");
            return;
        }

        // ปิดเมนู + คืนค่า cursor/movement ก่อน restart
        OnCloseSetting();
        var openMenu = FindObjectOfType<OpenMenu>();
        if (openMenu != null)
            openMenu.ForceCloseMenu();
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        
        var tpc = playerObject.GetComponent<StarterAssets.ThirdPersonController_Rigidbody>();
        if (tpc != null)
        {
            tpc.SetMovementLocked(false);
            tpc.LockCameraPosition = false;
        }

        // ถ้าอยู่ใน Slide Zone → ใช้ checkpoint ของ slide zone (ผ่าน flow KillForSlideZone + ForceRespawnAt)
        if (SplineSlideZone.ActiveZoneForLocalPlayer != null)
        {
            Debug.Log("Restarting from slide zone checkpoint...");
            SplineSlideZone.ActiveZoneForLocalPlayer.RequestRestartAtCheckpoint();
            return;
        }

        // นอก Slide Zone → ใช้ death flow ปกติ (respawn ที่ PlayerDeath.respawnPoint)
        var playerDeath = playerObject.GetComponent<PlayerDeath>();
        if (playerDeath != null)
        {
            Debug.Log("Restarting from checkpoint...");
            playerDeath.Kill();
        }
        else
        {
            Debug.LogError("PlayerDeath component not found on local player object.");
        }
    }
}