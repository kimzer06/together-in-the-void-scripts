using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// ปุ่มออกจากห้อง (Relay) แล้วกลับไปซีนที่ระบุ
/// ใช้ได้ทั้งใน CharacterSelect และ Pause Menu ในเกม
/// วิธีใช้:  ลากสคริปนี้ลง GameObject → ตั้งชื่อ Target Scene →
///          ลาก method LeaveAndLoadScene() ไปใส่ Button OnClick()
/// </summary>
public class LeaveRoomButton : MonoBehaviour
{
    [Header("ซีนปลายทางหลังออกจากห้อง")]
    [SerializeField] private string targetSceneName = "StartScene";

    [Header("UI (optional)")]
    [Tooltip("Panels/UI ที่ต้องการปิดก่อน Leave + LoadScene (เช่น Confirm Panel)")]
    [SerializeField] private GameObject[] panelsToClose;

    /// <summary>
    /// เรียกจาก Button OnClick() ได้เลย
    /// </summary>
    public void LeaveAndLoadScene()
    {
        ClosePanels();

        // ถ้าไม่มี network อยู่ (เช่นกด Back ตอน offline) → โหลดซีนเลย
        if (NetworkManager.Singleton == null ||
            (!NetworkManager.Singleton.IsConnectedClient && !NetworkManager.Singleton.IsListening))
        {
            SceneManager.LoadScene(targetSceneName);
            return;
        }

        // ใช้ LobbySelectionState ถ้ามี
        if (LobbySelectionState.I != null)
        {
            LobbySelectionState.I.LeaveRoom(targetSceneName);
        }
        else
        {
            // Fallback: ไม่มี LobbySelectionState → shutdown เอง
            FallbackCleanup();
        }
    }

    private void ClosePanels()
    {
        if (panelsToClose == null) return;
        for (int i = 0; i < panelsToClose.Length; i++)
        {
            if (panelsToClose[i] != null)
                panelsToClose[i].SetActive(false);
        }
    }

    private void FallbackCleanup()
    {
        Debug.Log($"[LeaveRoomButton] Fallback cleanup → {targetSceneName}");

        // ★ Cache reference ก่อน Shutdown
        GameObject nmGO = (NetworkManager.Singleton != null)
            ? NetworkManager.Singleton.gameObject
            : null;

        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.Shutdown();
        }

        if (nmGO)
        {
            Destroy(nmGO);
        }

        SceneManager.LoadScene(targetSceneName);
    }
}
