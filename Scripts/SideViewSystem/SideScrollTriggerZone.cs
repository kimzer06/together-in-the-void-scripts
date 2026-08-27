using UnityEngine;
using Unity.Netcode;
using StarterAssets;
using Unity.Cinemachine; // << 1. เพิ่ม using Cinemachine
using System.Collections;

/// <summary>
/// Trigger ที่กำหนดค่าการควบคุมแบบ 2.5D และตั้งค่า Target ให้ VCam
/// </summary>
[RequireComponent(typeof(Collider))]
public class SideScrollTriggerZone : MonoBehaviour
{
    [Header("Mouse Lock Settings")]
    [Tooltip("ระยะเวลาล็อคเมาส์หลังเข้าโซน (วินาที)")]
    public float mouseLockDuration = 1f;

    [Header("2.5D Axis Configuration")]
    [Tooltip("A/D (Horizontal Input) ควรแมปกับแกนไหนของโลก?")]
    public AxisBinding horizontalInputMapping = AxisBinding.WorldZ;
    
    [Tooltip("กลับด้าน A/D? (เช่น กด D ให้เดินไปทาง -Z)")]
    public bool invertHorizontal = false;

    [Space]
    [Tooltip("W/S (Vertical Input) ควรแมปกับแกนไหนของโลก?")]
    public AxisBinding verticalInputMapping = AxisBinding.WorldX;

    [Tooltip("กลับด้าน W/S? (เช่น กด W ให้เดินไปทาง -X)")]
    public bool invertVertical = false;
    
    [Header("References")]
    [Tooltip("ลาก VCam 2.5D (จากใน Scene) มาใส่ที่นี่")]
    public GameObject SideScrollVCam;

    [Header("Start Condition (Optional)")]
    [Tooltip("ถ้ากำหนด: จะเริ่ม SideView ก็ต่อเมื่อผู้เล่น 'ยืนบน' platform ใดๆ ในลิสต์นี้ (หรือ child ของมัน) ขณะอยู่ในโซน")]
    public Transform[] requiredPlatformRoots;

    [Tooltip("ระยะ raycast ลงพื้นเพื่อเช็คว่า 'ยืนบน platform' (เมตร)")]
    public float platformCheckDistance = 1.6f;

    [Tooltip("offset ของจุดเริ่ม raycast (เผื่อ pivot อยู่ต่ำ/สูง)")]
    public Vector3 platformCheckOriginOffset = new Vector3(0f, 0.25f, 0f);

    private CinemachineCamera _vcamComponent; // << 2. ตัวแปรสำหรับเก็บ VCam
    private bool _localOwnerInsideZone;
    private bool _localSideViewActivated;
    private Collider _localOwnerCollider;
    private PlayerCameraManager _localCamManager;
    private ThirdPersonController_Rigidbody _localController;

    private void Awake()
    {
        var col = GetComponent<Collider>();
        if (col != null) col.isTrigger = true;

        // << 3. ดึง Component ของ VCam มาเก็บไว้
        if (SideScrollVCam != null)
        {
            _vcamComponent = SideScrollVCam.GetComponent<CinemachineCamera>();
            if (_vcamComponent == null)
            {
                Debug.LogError($"SideScrollTriggerZone: '{SideScrollVCam.name}' ไม่มี CinemachineCamera component!");
            }
        }
        else
        {
            Debug.LogError("SideScrollTriggerZone: SideScrollVCam is not assigned!");
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        // ทำงานเฉพาะ Client ที่เป็นเจ้าของ Player เท่านั้น
        if (other.TryGetComponent<NetworkObject>(out var netObj) && netObj.IsOwner)
        {
            _localOwnerInsideZone = true;
            _localSideViewActivated = false;
            _localOwnerCollider = other;
            _localCamManager = other.GetComponent<PlayerCameraManager>();
            _localController = other.GetComponent<ThirdPersonController_Rigidbody>();

            // ถ้าไม่กำหนด platform เงื่อนไข → เริ่มทันที (พฤติกรรมเดิม)
            if (requiredPlatformRoots == null || requiredPlatformRoots.Length == 0)
            {
                TryActivateSideViewNow();
            }
        }
    }

    private void Update()
    {
        if (!_localOwnerInsideZone || _localSideViewActivated) return;
        if (requiredPlatformRoots == null || requiredPlatformRoots.Length == 0) return; // ไม่มีเงื่อนไขแล้วจะเริ่มจาก OnTriggerEnter ไปแล้ว

        if (IsStandingOnRequiredPlatform())
        {
            TryActivateSideViewNow();
        }
    }

    private bool IsStandingOnRequiredPlatform()
    {
        if (_localOwnerCollider == null) return false;
        if (_localController != null && !_localController.Grounded) return false;
        if (requiredPlatformRoots == null || requiredPlatformRoots.Length == 0) return true;

        Vector3 origin = _localOwnerCollider.transform.position + platformCheckOriginOffset;
        float dist = Mathf.Max(0.05f, platformCheckDistance);

        if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, dist, ~0, QueryTriggerInteraction.Ignore))
        {
            if (hit.collider == null || hit.collider.transform == null) return false;
            Transform hitTf = hit.collider.transform;

            for (int i = 0; i < requiredPlatformRoots.Length; i++)
            {
                var root = requiredPlatformRoots[i];
                if (root == null) continue;
                if (hitTf.IsChildOf(root)) return true;
            }
            return false;
        }
        return false;
    }

    private void TryActivateSideViewNow()
    {
        if (_localSideViewActivated) return;
        if (_localOwnerCollider == null) return;

        var other = _localOwnerCollider;
        var camManager = _localCamManager != null ? _localCamManager : other.GetComponent<PlayerCameraManager>();
        var controller = _localController != null ? _localController : other.GetComponent<ThirdPersonController_Rigidbody>();

        // ล็อคกล้อง 3D ฝั่ง local "ทันที" ก่อนเริ่ม blend
        if (controller != null) controller.SetLocalSideScrollState(true);

        // ตั้ง TrackingTarget ให้ SideView vcam
        if (_vcamComponent != null) _vcamComponent.Target.TrackingTarget = other.transform;

        // สลับกล้อง (Priority)
        if (camManager != null) camManager.ActivateSideScrollCamera(SideScrollVCam);

        // ล็อคเมาส์ชั่วคราวเพื่อให้การเปลี่ยนมุมมองราบรื่น
        StartCoroutine(LockMouseTemporarily());

        // ส่งค่า Config ไปให้ Server
        if (controller != null)
        {
            var config = new SideScrollConfig
            {
                horizontalAxis = (byte)horizontalInputMapping,
                verticalAxis = (byte)verticalInputMapping,
                horizontalInvert = invertHorizontal,
                verticalInvert = invertVertical
            };
            controller.SetSideScrollingStateServerRpc(true, config);
        }

        // ปิด Ability (ห้ามเล็ง) ขณะอยู่ใน SideScroll
        var freeze = other.GetComponent<TimeFreezeAbility_Net>();
        if (freeze) freeze.enabled = false;
        var portal = other.GetComponent<PortalSumoner_Net>();
        if (portal) portal.enabled = false;

        _localSideViewActivated = true;
    }

    private void OnTriggerExit(Collider other)
    {
        // ทำงานเฉพาะ Client ที่เป็นเจ้าของ Player เท่านั้น
        if (other.TryGetComponent<NetworkObject>(out var netObj) && netObj.IsOwner)
        {
            // ใช้ logic "ออกจาก SideView" เฉพาะกรณีที่เรา Activate SideView แล้วเท่านั้น
            bool wasActivated = _localSideViewActivated;
            var controller = other.GetComponent<ThirdPersonController_Rigidbody>();

            _localOwnerInsideZone = false;
            _localSideViewActivated = false;
            _localOwnerCollider = null;
            _localCamManager = null;
            _localController = null;

            if (!wasActivated)
            {
                // เข้าโซนแต่ยังไม่เริ่ม (ยังไม่เหยียบ platform) → ออกโซนแล้วไม่ต้องสลับกล้อง/ส่ง RPC/คืน ability
                return;
            }

            // สแน็ปมุมกล้อง 3D ให้ไปอยู่หลังตัวละครก่อนเริ่ม blend กลับ
            if (controller != null) controller.SnapCameraBehindCharacter(keepPitch: true);

            // 1. สลับกล้องกลับ
            var camManager = other.GetComponent<PlayerCameraManager>();
            if (camManager != null)
            {
                camManager.DeactivateSideScrollCamera();
            }

            // ล็อคเมาส์ชั่วคราวเพื่อให้การเปลี่ยนมุมมองราบรื่น
            StartCoroutine(LockMouseTemporarily());
            
            // << 5. ล้างค่า Follow และ LookAt
            // (สำคัญมาก! เพื่อไม่ให้ VCam อ้างอิง Player ที่ออกไปแล้ว)
            if (_vcamComponent != null)
            {
                _vcamComponent.Target.TrackingTarget = null;
            }
            // --- จบส่วนแก้ไข ---

            // 2. บอก Server ให้ออกจากโหมด 2.5D
            if (controller != null)
            {
                controller.SetLocalSideScrollState(false); // ปลดล็อคกล้อง
                controller.SetSideScrollingStateServerRpc(false, default);
            }

            // 3. เปิด Ability กลับ (เล็งได้อีกครั้ง)
            var freeze = other.GetComponent<TimeFreezeAbility_Net>();
            if (freeze) freeze.enabled = true;
            var portal = other.GetComponent<PortalSumoner_Net>();
            if (portal) portal.enabled = true;
        }
    }

    /// <summary>
    /// ล็อคเมาส์ชั่วคราวเพื่อให้การเปลี่ยนมุมมองกล้องราบรื่น
    /// </summary>
    private IEnumerator LockMouseTemporarily()
    {
        // บันทึกสถานะเดิมของ Cursor
        CursorLockMode previousLockState = Cursor.lockState;
        bool previousVisible = Cursor.visible;

        // ล็อคเมาส์และซ่อน
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        // รอตามเวลาที่กำหนด
        yield return new WaitForSeconds(mouseLockDuration);

        // คืนค่า Cursor กลับเป็นสถานะเดิม
        Cursor.lockState = previousLockState;
        Cursor.visible = previousVisible;
    }
}