using Unity.Netcode;
using UnityEngine;
using Unity.Cinemachine;

public class ClientCameraBinder : NetworkBehaviour
{
    [SerializeField] Transform cameraTarget; // ใส่ PlayerCameraRoot

    public override void OnNetworkSpawn()
    {
        if (!IsOwner) return; // ให้เฉพาะตัวที่เราเป็นเจ้าของ

        // หา vcam จาก prefab ของ player ตัวนี้เอง (ไม่ใช่ FindObjectOfType ที่อาจเจอของคนอื่น)
        var vcam = GetComponentInChildren<CinemachineCamera>(true);
        if (!vcam)
        {
            Debug.LogError("[ClientCameraBinder] No CinemachineCamera found on this player prefab.");
            return;
        }

        vcam.Target.TrackingTarget = cameraTarget;
        vcam.Priority = 100;        // ให้ชนะ vcam อื่น ๆ ถ้ามี
    }
}
