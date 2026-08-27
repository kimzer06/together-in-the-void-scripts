using UnityEngine;

public interface ITrapCycle
{

   
    /// <summary>รวมเวลา 1 รอบ (ขึ้น + ค้างบน + ลง + ค้างล่าง)</summary>
    float GetCycleDuration();

    /// <summary>สั่งเล่นรอบเดียวกับทุกเครื่อง (ต้องถูกเรียกจากฝั่ง Server)</summary>
    void PlayOnceForAll(float startDelay = 0f);

    /// <summary>ตั้งค่าทับ (เช่น จาก SpawnPoint) ก่อนเล่นรอบ</summary>
    void ApplyOverrides(float raiseDistance, bool useLocalSpace);
}
