using Unity.Netcode;
using UnityEngine;

// ✨ เพิ่ม enum ด้านนอก class เพื่อให้สคริปต์อื่นเรียกใช้ง่าย
public enum PlayerRole
{
    RoleA, // เดิมคือ Time Stopper
    RoleB  // เดิมคือ Wall Waker / Portal Summoner
}

public class PlayerRoleFromLobby : NetworkBehaviour
{
    [Header("กำหนดบทบาทของผู้เล่น")]
    [Tooltip("กำหนดบทบาทที่จะให้ผู้เล่นคนนี้ใน Inspector")]
    [SerializeField] private PlayerRole assignedRole = PlayerRole.RoleB;

    // เปลี่ยนจาก bool IsTimeStopper มาเป็น NetworkVariable ที่เก็บค่า enum ของ Role แทน
    // ทำให้ทุก Client รู้ว่าผู้เล่นคนนี้มีบทบาทอะไร
    public NetworkVariable<PlayerRole> Role =
        new(writePerm: NetworkVariableWritePermission.Server);

    // ลบ IsWallWakerLocal ออกไป เพราะไม่จำเป็นแล้ว

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            // Server อ่านค่าจาก Inspector แล้วกำหนดค่า Role ให้กับ NetworkVariable
            // จากนั้นค่านี้จะถูก Sync ไปให้ Client ทุกคนอัตโนมัติ
            Role.Value = assignedRole;
        }
    }

#if UNITY_EDITOR
    // OnValidate ไม่จำเป็นต้องมีแล้ว เพราะ enum จัดการค่าให้ถูกต้องเสมอ
#endif
}