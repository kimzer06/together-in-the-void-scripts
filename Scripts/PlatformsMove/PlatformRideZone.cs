using UnityEngine;

[DisallowMultipleComponent]
public class PlatformRideZone : MonoBehaviour
{
    [Tooltip("ตัว Transform ของแพลตฟอร์ม (ตัวที่ขยับ/หมุนจริง)")]
    public Transform platformRoot;

    void Reset()
    {
        if (!platformRoot) platformRoot = transform.root;
        // อย่าลืมใส่ BoxCollider (IsTrigger = true) บน GameObject นี้
    }
}
