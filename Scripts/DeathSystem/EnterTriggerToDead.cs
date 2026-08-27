using UnityEngine;

/// <summary>
/// ทริกเกอร์ฆ่าผู้เล่น
/// </summary>
public class EnterTriggerToDead : MonoBehaviour
{
    [SerializeField] private string playerTag = "Player";

    private void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag(playerTag)) return;

        var death = other.GetComponent<PlayerDeath>();
        if (death == null) return;
        if (death.IsRespawnImmune) return;

        death.Kill(); // ปลอดภัย: ถ้าไม่ใช่เซิร์ฟเวอร์จะยิง ServerRpc ให้
    }
}