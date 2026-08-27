using UnityEngine;
using Unity.Netcode;
using Unity.Cinemachine; // << 1. เพิ่ม using Cinemachine
using System.Collections;

/// <summary>
/// Quản lý việc chuyển đổi camera VCam (Client-side) bằng cách sử dụng Priority.
/// </summary>
public class PlayerCameraManager : NetworkBehaviour
{
    [Header("Camera References")]
    [Tooltip("Kéo VCam 3D chính (ví dụ: PlayerFollowCamera) vào đây")]
    public GameObject ThirdPersonVCam;

    [Header("Priority Settings")]
    [Tooltip("Priority ของกล้อง 3D เมื่อทำงาน (ค่า Default ควรสูง)")]
    public int MainCamActivePriority = 20;
    
    [Tooltip("Priority ของกล้อง 3D เมื่อ 'ซ่อน' (ควรต่ำกว่า 0)")]
    public int MainCamInactivePriority = -10;
    
    [Tooltip("Priority ที่จะตั้งให้กล้อง 2.5D เมื่อทำงาน (ต้องสูงกว่า MainCamActive)")]
    public int SideScrollCamPriority = 30;

    // --- Private ---
    // Cinemachine 3.x: เปลี่ยนจาก CinemachineVirtualCamera เป็น CinemachineCamera
    private CinemachineCamera _3dVCamComponent;
    private CinemachineCamera _activeSideScrollVCamComponent;
    private GameObject _activeSideScrollVCamGO; // เก็บ GameObject เพื่อ Debug

    // Freeze outgoing camera during blend to prevent wobble
    private Transform _freezeTarget;
    private Transform _saved3DTrackingTarget;
    private Coroutine _freezeCoroutine;
    private bool _is3DTrackingFrozen;

    public override void OnNetworkSpawn()
    {
        if (ThirdPersonVCam != null)
        {
            _3dVCamComponent = ThirdPersonVCam.GetComponent<CinemachineCamera>();
            if (_3dVCamComponent == null)
            {
                Debug.LogError("PlayerCameraManager: ThirdPersonVCam ไม่มี CinemachineCamera component!");
                return;
            }
        }
        else
        {
             Debug.LogError("PlayerCameraManager: ThirdPersonVCam is null!");
             return;
        }


        // Đảm bảo chỉ người chơi cục bộ mới kích hoạt camera 3D ban đầu
        if (IsOwner)
        {
            // << 2. แก้ไข: ใช้ Priority แทน SetActive
            _3dVCamComponent.Priority = MainCamActivePriority;
            EnsureFreezeTargetExists();
        }
        else
        {
            // << 3. แก้ไข: ใช้ Priority แทน SetActive
            _3dVCamComponent.Priority = MainCamInactivePriority;
        }
    }

    private void EnsureFreezeTargetExists()
    {
        if (_freezeTarget != null) return;
        var go = new GameObject($"{nameof(PlayerCameraManager)}_FreezeTarget");
        go.hideFlags = HideFlags.HideInHierarchy;
        _freezeTarget = go.transform;
    }

    private float GetDefaultBlendTimeSeconds()
    {
        var cam = Camera.main;
        if (cam != null && cam.TryGetComponent<CinemachineBrain>(out var brain))
        {
            // Cinemachine Brain default blend time (from prefab shows 2s)
            return Mathf.Max(0f, brain.DefaultBlend.Time);
        }
        return 0f;
    }

    private void Freeze3DTrackingTargetForBlend()
    {
        if (!IsOwner || _3dVCamComponent == null) return;
        EnsureFreezeTargetExists();

        // If we're already frozen (rapid enter/exit), don't overwrite the saved target.
        // Otherwise we risk saving the freeze target itself and never restoring correctly.
        if (!_is3DTrackingFrozen)
        {
            _saved3DTrackingTarget = _3dVCamComponent.Target.TrackingTarget;
        }

        Transform src = (_saved3DTrackingTarget != null && _saved3DTrackingTarget != _freezeTarget)
            ? _saved3DTrackingTarget
            : transform;
        _freezeTarget.position = src.position;
        _freezeTarget.rotation = src.rotation;

        // force 3D vcam to follow a stable target during blend
        _3dVCamComponent.Target.TrackingTarget = _freezeTarget;
        _is3DTrackingFrozen = true;

        float t = GetDefaultBlendTimeSeconds();
        if (_freezeCoroutine != null) StopCoroutine(_freezeCoroutine);
        _freezeCoroutine = StartCoroutine(Restore3DTrackingTargetAfterDelay(t));
    }

    private IEnumerator Restore3DTrackingTargetAfterDelay(float seconds)
    {
        if (seconds > 0f) yield return new WaitForSeconds(seconds);
        if (_3dVCamComponent != null)
        {
            _3dVCamComponent.Target.TrackingTarget = _saved3DTrackingTarget;
        }
        _is3DTrackingFrozen = false;
        _freezeCoroutine = null;
    }

    private void Restore3DTrackingTargetNow()
    {
        if (_freezeCoroutine != null)
        {
            StopCoroutine(_freezeCoroutine);
            _freezeCoroutine = null;
        }

        if (_3dVCamComponent != null)
        {
            // Only restore if we're currently forcing the freeze target.
            if (_3dVCamComponent.Target.TrackingTarget == _freezeTarget)
            {
                _3dVCamComponent.Target.TrackingTarget = _saved3DTrackingTarget;
            }
        }

        _is3DTrackingFrozen = false;
    }

    /// <summary>
    /// Kích hoạt camera 2.5D cụ thể này vàลด priority camera 3D.
    /// </summary>
    public void ActivateSideScrollCamera(GameObject vcamToActivate)
    {
        if (!IsOwner || vcamToActivate == null) return;

        // 1. Tắt camera 2.5D cũ (nếu có)
        DeactivateSideScrollCamera();

        // 2. Lấy component ของกล้อง 2.5D ตัวใหม่
        var incomingVCam = vcamToActivate.GetComponent<CinemachineCamera>();
        if (incomingVCam == null)
        {
            Debug.LogError($"SideScroll VCam '{vcamToActivate.name}' ไม่มี CinemachineCamera component!");
            return;
        }

        // << 4. แก้ไข: เปลี่ยน Logic ทั้งหมด
        
        // Freeze 3D vcam's tracking during blend to prevent wobble
        Freeze3DTrackingTargetForBlend();

        // ลดความสำคัญกล้อง 3D (แต่มันยัง Active และ Tracking อยู่!)
        if (_3dVCamComponent) _3dVCamComponent.Priority = MainCamInactivePriority;

        // เพิ่มความสำคัญกล้อง 2.5D (มันจะยึดหน้าจอทันที)
        incomingVCam.Priority = SideScrollCamPriority;

        // เก็บไว้
        _activeSideScrollVCamComponent = incomingVCam;
        _activeSideScrollVCamGO = vcamToActivate;
    }

    /// <summary>
    /// Tắt camera 2.5D đang hoạt động และ quay lại camera 3D.
    /// </summary>
    public void DeactivateSideScrollCamera()
    {
        if (!IsOwner) return;

        // << 5. แก้ไข: เปลี่ยน Logic ทั้งหมด
        Restore3DTrackingTargetNow();

        // ลดความสำคัญกล้อง 2.5D ตัวเก่า (ถ้ามี)
        if (_activeSideScrollVCamComponent != null)
        {
            _activeSideScrollVCamComponent.Priority = MainCamInactivePriority; // ตั้งกลับไปต่ำๆ
        }
        _activeSideScrollVCamComponent = null;
        _activeSideScrollVCamGO = null;

        // คืนความสำคัญให้กล้อง 3D
        if (_3dVCamComponent) _3dVCamComponent.Priority = MainCamActivePriority;
    }
}