using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using StarterAssets;
using Unity.Cinemachine;

public class ClientPlayerMove : NetworkBehaviour
{
    [Header("References")]
    [SerializeField] private PlayerInput m_PlayerInput;                 // อ่านอินพุตของ local player
    [SerializeField] private StarterAssetsInputs m_StarterInputs;       // ตัวเก็บค่า move/look/jump/sprint
    [SerializeField] private ThirdPersonController_Rigidbody m_Controller;        // คุมการเดิน/กระโดด
    [SerializeField] private Transform cameraTarget;                    // ใส่ PlayerCameraRoot ของ prefab นี้

    private CinemachineCamera _vcam;

    private void Awake()
    {
        // ปิดทุกอย่างไว้ก่อน แล้วค่อยเปิดเฉพาะ owner
        ToggleLocalControl(false);
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        ToggleLocalControl(IsOwner);

        if (IsOwner)
            BindCamera();
    }

    public override void OnNetworkDespawn()
    {
        base.OnNetworkDespawn();

        // ถ้าเราเป็น owner ให้ปลดกล้องออกเพื่อป้องกัน vcam ค้าง target
        if (_vcam && _vcam.Target.TrackingTarget == cameraTarget)
        {
            _vcam.Target.TrackingTarget = null;
        }
    }

    /// <summary>
    /// เปิด/ปิดคอนโทรลฝั่งโลคัล (ใช้เฉพาะ owner)
    /// </summary>
    private void ToggleLocalControl(bool enabled)
    {
        if (m_PlayerInput) m_PlayerInput.enabled = enabled;
        if (m_StarterInputs) m_StarterInputs.enabled = enabled;
        if (m_Controller) m_Controller.enabled = enabled;
    }

    /// <summary>
    /// ผูก Cinemachine vcam ให้ตาม player owner ตัวนี้
    /// </summary>
    private void BindCamera()
    {
        if (!cameraTarget)
        {
            Debug.LogError("[ClientPlayerMove] cameraTarget not assigned. Drag PlayerCameraRoot here.");
            return;
        }

        // หา vcam จาก prefab ของ player ตัวนี้เอง (ไม่ใช่ FindObjectOfType ที่อาจเจอของคนอื่น)
        _vcam = GetComponentInChildren<CinemachineCamera>(true);
        if (!_vcam)
        {
            Debug.LogError("[ClientPlayerMove] No CinemachineCamera found on this player prefab.");
            return;
        }

        _vcam.Target.TrackingTarget = cameraTarget;
        _vcam.Priority = 100; // ให้ชนะ vcam อื่นถ้ามี
    }
}
