using UnityEngine;

/// <summary>
/// Interface สำหรับ Slide Zone ทุกชนิด (SplineSlideZone, ฯลฯ)
/// ให้ SlideZoneHitbox และ SlideZoneFinishLine ใช้ร่วมกันได้
/// </summary>
public interface ISlideZone
{
    /// <summary>
    /// เรียกเมื่อผู้เล่นชนสิ่งกีดขวาง
    /// </summary>
    void NotifyPlayerHitObstacle(ulong clientId);
    
    /// <summary>
    /// ตรวจสอบว่าผู้เล่นอยู่ใน active slide หรือไม่
    /// </summary>
    bool IsPlayerInActiveSlide(ulong clientId);
}
