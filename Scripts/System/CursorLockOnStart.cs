using UnityEngine;

/// วางสคริปต์นี้ไว้ในซีนปลายทาง (เช่น Gameplay)
/// จะล็อคและซ่อนเคอร์เซอร์เมื่อซีนเริ่มทำงานบนเครื่องนั้นๆ
public class CursorLockOnStart : MonoBehaviour
{
    [Header("When to lock")]
    [Tooltip("ล็อคที่ Start (หลังทุกอย่าง Awake เสร็จ)")]
    [SerializeField] private bool lockOnStart = true;

    [Tooltip("หน่วงเวลาก่อนล็อค (เผื่อซีน/Canvas กำลังเซ็ตอัพ)")]
    [SerializeField, Min(0f)] private float delaySeconds = 0f;

    [Header("Behavior")]
    [Tooltip("ปลดล็อคเมื่อ Alt+Tab โฟกัสหลุดจากเกม")]
    [SerializeField] private bool unlockOnFocusLost = true;

    private void Start()
    {
        if (lockOnStart)
        {
            if (delaySeconds <= 0f) ApplyLock(true);
            else Invoke(nameof(DelayedLock), delaySeconds);
        }
    }

    private void DelayedLock() => ApplyLock(true);

    private void OnApplicationFocus(bool hasFocus)
    {
        if (!unlockOnFocusLost) return;

        if (!hasFocus)
            ApplyLock(false);
        else
            ApplyLock(true); // กลับมา focus → ล็อคกลับอัตโนมัติ
    }

    private void ApplyLock(bool locked)
    {
        if (locked)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
        else
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }
}
