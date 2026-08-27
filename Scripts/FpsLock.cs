using UnityEngine;

public class FrameRateLimiter : MonoBehaviour
{
    void Awake()
    {
        // 1. ปิด VSync เพื่อให้ตัวเลข FPS ทำงานตามที่เราสั่ง
        QualitySettings.vSyncCount = 0;

        // 2. ตั้งค่า FPS ที่ต้องการ (เช่น 60 หรือ 120)
        Application.targetFrameRate = 120; 

        // 3. ป้องกันไม่ให้ Object นี้ถูกทำลายเมื่อเปลี่ยน Scene
        DontDestroyOnLoad(gameObject);
    }
}