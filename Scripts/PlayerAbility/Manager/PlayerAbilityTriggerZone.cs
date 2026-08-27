using UnityEngine;
using Unity.Netcode;
using System.Collections.Generic;

[RequireComponent(typeof(Collider))]
public class PlayerAbilityTriggerZone : MonoBehaviour
{
    [Header("On Trigger Enter")]
    [Tooltip("ใส่ Ability ID ของสกิลที่ต้องการ 'เปิด' เมื่อผู้เล่นเดินเข้ามา")]
    [SerializeField] private List<string> onEnterEnableIDs;

    [Tooltip("ใส่ Ability ID ของสกิลที่ต้องการ 'ปิด' เมื่อผู้เล่นเดินเข้ามา")]
    [SerializeField] private List<string> onEnterDisableIDs;

    [Header("On Trigger Exit")]
    [Tooltip("ใส่ Ability ID ของสกิลที่ต้องการ 'เปิด' เมื่อผู้เล่นเดินออกไป")]
    [SerializeField] private List<string> onExitEnableIDs;

    [Tooltip("ใส่ Ability ID ของสกิลที่ต้องการ 'ปิด' เมื่อผู้เล่นเดินออกไป")]
    [SerializeField] private List<string> onExitDisableIDs;

    private Collider zoneCollider;

    private void Awake()
    {
        zoneCollider = GetComponent<Collider>();
        zoneCollider.isTrigger = true; // บังคับเป็น Trigger

        // [สำคัญ] Zone นี้ทำงานบน Server เท่านั้น
        if (!NetworkManager.Singleton.IsServer)
        {
            enabled = false; // ปิดสคริปต์นี้บน Client ไปเลย
        }
    }

    // [ทำงานเฉพาะบน Server]
    private void OnTriggerEnter(Collider other)
    {
        // พยายามหา Manager บนตัว Player ที่ชน
        if (other.TryGetComponent<PlayerAbilityManager>(out var playerManager))
        {
            // สั่งการ Player โดยใช้ลิสต์ OnEnter
            playerManager.Server_SetAbilityStates(onEnterEnableIDs, onEnterDisableIDs);
        }
    }

    // [ทำงานเฉพาะบน Server]
    private void OnTriggerExit(Collider other)
    {
        // พยายามหา Manager บนตัว Player ที่ชน
        if (other.TryGetComponent<PlayerAbilityManager>(out var playerManager))
        {
            // สั่งการ Player โดยใช้ลิสต์ OnExit
            playerManager.Server_SetAbilityStates(onExitEnableIDs, onExitDisableIDs);
        }
    }
}