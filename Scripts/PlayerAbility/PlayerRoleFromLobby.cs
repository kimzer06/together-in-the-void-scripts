using UnityEngine;

// ✨ เพิ่ม enum ด้านนอก class เพื่อให้สคริปต์อื่นเรียกใช้ง่าย
public enum PlayerRole
{
    RoleA, // เดิมคือ Time Stopper
    RoleB  // เดิมคือ Wall Waker / Portal Summoner
}

/// <summary>
/// บทบาทของผู้เล่น — ผูกกับ prefab (T_PlayerA = RoleA, T_PlayerB = RoleB)
/// ไม่ต้องซิงค์ข้ามเครื่อง: ทุกเครื่องสปอน prefab เดียวกันอยู่แล้ว จึงได้ค่าเดียวกันเสมอ
/// (สมัย NGO เป็น NetworkVariable ที่ server แค่เขียนค่าจาก Inspector ซ้ำลงไป)
/// </summary>
public class PlayerRoleFromLobby : MonoBehaviour
{
    [Header("กำหนดบทบาทของผู้เล่น")]
    [Tooltip("กำหนดบทบาทที่จะให้ผู้เล่นคนนี้ใน Inspector")]
    [SerializeField] private PlayerRole assignedRole = PlayerRole.RoleB;

    public PlayerRole Role => assignedRole;
}
