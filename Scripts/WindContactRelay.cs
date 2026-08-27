using System.Collections.Generic;
using UnityEngine;

public class WindContactRelay : MonoBehaviour
{
    private readonly List<FlyupZone> _activeZones = new();

    /// <summary>
    /// true ถ้า Player อยู่ในโซนลม (>=1)
    /// </summary>
    public bool InWind => _activeZones.Count > 0;

    /// <summary>
    /// Property ใหม่สำหรับเช็คว่าสามารถกระโดดได้หรือไม่
    /// จะเป็น true ถ้ามีโซนลมอย่างน้อย 1 โซนที่เปิดให้กระโดด
    /// </summary>
    public bool CanJumpInWind
    {
        get
        {
            foreach (var zone in _activeZones)
            {
                // ถ้าเจอโซนที่อนุญาตให้กระโดดแม้แต่อันเดียว ก็ให้กระโดดได้เลย
                if (zone != null && zone.AllowJumpingInWind)
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// เรียกจาก FlyupZone เมื่อ Rigidbody เข้ามาในโซนลม
    /// </summary>
    public void OnEnterZone(FlyupZone zone)
    {
        if (zone != null && !_activeZones.Contains(zone))
        {
            _activeZones.Add(zone);
        }
    }

    /// <summary>
    /// เรียกจาก FlyupZone เมื่อ Rigidbody ออกจากโซนลม
    /// </summary>
    public void OnExitZone(FlyupZone zone)
    {
        if (zone != null)
        {
            _activeZones.Remove(zone);
        }
    }
}